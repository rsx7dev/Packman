using Microsoft.Web.WebView2.Core;
using Packman.Services;
using Packman.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Packman.Views;

/// <summary>
/// Hosts Monaco in WebView2 and draws the package tree. All editor state lives in
/// <see cref="EditorSessionViewModel"/>; this class only bridges it to the browser.
/// </summary>
public partial class StepEdit : UserControl, IMonacoHost
{
    /// <summary>Loaded once per process; the CSV doesn't change at runtime.</summary>
    private static List<PSADTFunction>? _catalog;

    private bool _editorReady;
    private bool _editorFailed;
    private Task? _editorInit;          // one initialisation, shared by Loaded and IsVisibleChanged
    private bool _suppressTreeSelection;
    private EditorSessionViewModel? _session;

    public StepEdit()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachSession();
        // The editor lives for the whole window, so the subscription needs no unhook.
        ThemeService.Changed += ApplyEditorTheme;
    }

    private void AttachSession()
    {
        if (_session != null)
        {
            _session.TreeRefreshRequested -= OnTreeRefreshRequested;
            _session.ActiveFileChanged -= SelectInTree;
        }

        _session = (DataContext as MainViewModel)?.Editor;
        if (_session == null) return;

        _session.AttachHost(this);
        _session.TreeRefreshRequested += OnTreeRefreshRequested;
        _session.ActiveFileChanged += SelectInTree;
    }

    // ═══════════ Lifetime ═══════════

    private void StepEdit_Loaded(object sender, RoutedEventArgs e)
    {
        // Warm WebView2 up front, or the step stalls the first time it is shown.
        ErrorReporter.FireAndForget(InitializeEditorAsync);
    }

    private void StepEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        ErrorReporter.FireAndForget(async () =>
        {
            await InitializeEditorAsync();
            _session?.LoadPackage();
        });
    }

    // ═══════════ WebView2 / Monaco host ═══════════

    bool IMonacoHost.IsReady => _editorReady;

    public void Post(object message) =>
        EditorWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    /// <summary>Reads a buffer out of Monaco; null when it no longer holds one.</summary>
    public async Task<EditorBufferSnapshot?> GetBufferAsync(string path)
    {
        if (EditorWebView.CoreWebView2 == null) return null;
        var json = await EditorWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.packmanContent({JsonSerializer.Serialize(path)})");
        return json is null or "null" ? null : JsonSerializer.Deserialize<EditorBufferSnapshot>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public async Task<bool> TryReloadAsync(object message)
    {
        if (EditorWebView.CoreWebView2 == null) return false;
        var json = await EditorWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.packmanReload({JsonSerializer.Serialize(message)})");
        return json == "true";
    }

    // Loaded and IsVisibleChanged both call this; a second EnsureCoreWebView2Async with a
    // fresh environment while the first is still running throws, so the task is shared.
    private Task InitializeEditorAsync() => _editorInit ??= InitializeEditorCoreAsync();

    private async Task InitializeEditorCoreAsync()
    {
        if (_editorReady || _editorFailed || EditorWebView.CoreWebView2 != null) return;

        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packman", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await EditorWebView.EnsureCoreWebView2Async(env);

            var core = EditorWebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 initialised without a CoreWebView2.");

            var assetsFolder = Path.Combine(AppContext.BaseDirectory, "MonacoEditor");
            core.SetVirtualHostNameToFolderMapping(
                "packman-editor", assetsFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += Editor_WebMessageReceived;
            core.ProcessFailed += Editor_ProcessFailed;
            core.NavigationCompleted += Editor_NavigationCompleted;
            core.NavigationStarting += (_, e) => { if (!IsEditorOrigin(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    ErrorReporter.FireAndForget(() =>
                    {
                        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        return Task.CompletedTask;
                    });
            };
            ApplyEditorTheme();
            core.Navigate("https://packman-editor/index.html");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView2 init failed: {ex.Message}");
            ShowEditorFallback();
        }
    }

    private void ShowEditorFallback()
    {
        _editorFailed = true;
        _editorReady = false;
        EditorWebView.Visibility = Visibility.Collapsed;
        EditorFallbackText.Visibility = Visibility.Visible;
    }

    /// <summary>A failed load of index.html never sends "ready"; fall back instead of waiting forever.</summary>
    private void Editor_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;
        Debug.WriteLine($"Monaco failed to load: {e.WebErrorStatus}");
        ShowEditorFallback();
    }

    /// <summary>The renderer died: reload it and let the session reopen its files from disk.</summary>
    private void Editor_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Debug.WriteLine($"WebView2 process failed: {e.ProcessFailedKind}");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited) { ShowEditorFallback(); return; }

        _editorReady = false;
        _session?.OnEditorCrashed();
        EditorWebView.CoreWebView2?.Reload();
    }

    private void Editor_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsEditorOrigin(e.Source)) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProperty)) return;

        switch (typeProperty.GetString())
        {
            case "ready":
                _editorReady = true;
                Post(new { type = "init", catalog = BuildCatalogPayload(), background = CodeBackgroundHex(), dark = ThemeService.IsDark });
                _session?.OnEditorReady();
                break;

            case "dirty":
                _session?.OnDirtyChanged(root.GetProperty("path").GetString(), root.GetProperty("dirty").GetBoolean());
                break;

            case "save":
                _session?.OnSaveRequested();
                break;

            case "validate":
                if (_session != null)
                {
                    var path = root.GetProperty("path").GetString();
                    var content = root.GetProperty("content").GetString();
                    ErrorReporter.FireAndForget(() => _session.ValidateAsync(path, content));
                }
                break;

            case "cursor":
                _session?.OnCursor(
                    root.GetProperty("line").GetInt32(),
                    root.GetProperty("column").GetInt32(),
                    root.GetProperty("selected").GetInt32());
                break;
        }
    }

    private Color CodeBackground()
        => (TryFindResource("CodeBgBrush") as SolidColorBrush)?.Color
           ?? (ThemeService.IsDark ? Color.FromRgb(0x07, 0x08, 0x0B) : Color.FromRgb(0xF9, 0xF9, 0xF9));

    private string CodeBackgroundHex()
    {
        var color = CodeBackground();
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>Keeps Monaco's canvas and syntax colours on the current palette.</summary>
    private void ApplyEditorTheme()
    {
        var color = CodeBackground();
        EditorWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        if (_editorReady) Post(new { type = "theme", background = CodeBackgroundHex(), dark = ThemeService.IsDark });
    }

    private static bool IsEditorOrigin(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.Host == "packman-editor" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    private static object BuildCatalogPayload()
    {
        _catalog ??= PSADTFunctionCatalog.LoadFromCsv(PSADTFunctionCatalog.GetCsvPath());
        return _catalog.Select(f => new
        {
            name = f.Name,
            synopsis = f.Synopsis,
            category = f.Category,
            snippet = f.GenerateCallWithPlaceholders(),
            @params = f.Parameters
                .Where(p => p.Name != "(none)")
                .Select(p => new
                {
                    name = p.Name,
                    type = p.Type,
                    mandatory = p.Mandatory,
                    isSwitch = p.IsSwitch,
                    description = p.Description,
                }),
        }).ToList();
    }

    // ═══════════ Package tree ═══════════

    private void OnTreeRefreshRequested() => ErrorReporter.FireAndForget(RefreshTreeAsync);

    private bool _treeRefreshRunning;
    private bool _treeRefreshPending;

    private async Task RefreshTreeAsync()
    {
        if (_treeRefreshRunning) { _treeRefreshPending = true; return; }
        _treeRefreshRunning = true;
        try
        {
            do
            {
                _treeRefreshPending = false;
                await RefreshTreeCoreAsync();
            } while (_treeRefreshPending);
        }
        finally { _treeRefreshRunning = false; }
    }

    private async Task RefreshTreeCoreAsync()
    {
        var session = _session;
        var appFolder = session?.ApplicationFolder;
        if (appFolder == null) return;

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CaptureExpanded(FileTree.Items, expanded);

        var roots = await Task.Run(() => ScanFolder(appFolder));
        if (!ReferenceEquals(session, _session) || !string.Equals(appFolder, _session?.ApplicationFolder, StringComparison.OrdinalIgnoreCase)) return;

        _suppressTreeSelection = true;
        FileTree.Items.Clear();
        foreach (var node in roots)
            FileTree.Items.Add(BuildTreeItem(node, expanded));
        _suppressTreeSelection = false;

        var active = _session?.OpenFiles.FirstOrDefault(f => f.IsActive);
        if (active != null) SelectInTree(active.Path);
    }

    private sealed record ScanNode(string Path, string Name, bool IsDirectory, List<ScanNode> Children);

    private static List<ScanNode> ScanFolder(string folder)
    {
        var nodes = new List<ScanNode>();
        try
        {
            foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d))
                nodes.Add(new ScanNode(dir, Path.GetFileName(dir), true, ScanFolder(dir)));
            foreach (var f in Directory.GetFiles(folder).OrderBy(f => f))
                if (!f.EndsWith(Helpers.TextFileIO.TempSuffix, StringComparison.OrdinalIgnoreCase))
                    nodes.Add(new ScanNode(f, Path.GetFileName(f), false, new List<ScanNode>()));
        }
        catch (UnauthorizedAccessException) { }
        return nodes;
    }

    private TreeViewItem BuildTreeItem(ScanNode node, HashSet<string> expanded)
    {
        var item = new TreeViewItem
        {
            Header = BuildNodeHeader(node),
            Tag = node.Path,
        };
        if (node.Children.Count == 0) return item;

        // Collapsed folders keep one placeholder, not a control for every descendant.
        var childrenLoaded = false;
        void LoadChildren()
        {
            if (childrenLoaded) return;
            childrenLoaded = true;
            item.Items.Clear();
            foreach (var child in node.Children) item.Items.Add(BuildTreeItem(child, expanded));
        }
        item.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) LoadChildren(); };
        if (expanded.Contains(node.Path))
        {
            LoadChildren();
            item.IsExpanded = true;
        }
        else item.Items.Add(new TreeViewItem { Header = "Loading…", IsEnabled = false });
        return item;
    }

    private StackPanel BuildNodeHeader(ScanNode node)
    {
        var iconKey = node.IsDirectory
            ? "IconFolder"
            : PackageFileSearch.PowerShellExtensions.Contains(Path.GetExtension(node.Path)) ? "IconCode" : "IconFile";

        var icon = new System.Windows.Shapes.Path { Data = (Geometry)FindResource(iconKey), StrokeThickness = 1.8 };
        icon.SetResourceReference(StyleProperty, "Icon");
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            node.IsDirectory ? "MutedBrush" : "InkBrush");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Viewbox { Width = 13, Height = 13, Child = icon });
        panel.Children.Add(new TextBlock { Text = node.Name, Margin = new Thickness(6, 0, 0, 0) });
        return panel;
    }

    private static void CaptureExpanded(ItemCollection items, HashSet<string> expanded)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.IsExpanded && item.Tag is string path) expanded.Add(path);
            CaptureExpanded(item.Items, expanded);
        }
    }

    private void ToggleTree_Click(object sender, RoutedEventArgs e)
    {
        var show = TreePanel.Visibility != Visibility.Visible;
        TreePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TreeRail.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshTree_Click(object sender, RoutedEventArgs e) => ErrorReporter.FireAndForget(RefreshTreeAsync);

    private void OpenExternally_Click(object sender, RoutedEventArgs e) => _session?.OpenInExternalEditor();

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection) return;
        if (e.NewValue is TreeViewItem { Tag: string path } && File.Exists(path))
            _session?.Open(path);
    }

    private void SelectInTree(string path)
    {
        _suppressTreeSelection = true;
        var item = FindTreeItem(FileTree.Items, path);
        if (item != null) item.IsSelected = true;
        _suppressTreeSelection = false;
    }

    private static TreeViewItem? FindTreeItem(ItemCollection items, string path)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag as string == path) return item;
            if (item.Tag is string parent && path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                item.IsExpanded = true;
            var hit = FindTreeItem(item.Items, path);
            if (hit != null) return hit;
        }
        return null;
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is SearchHit hit) _session?.OpenSearchHit(hit);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
