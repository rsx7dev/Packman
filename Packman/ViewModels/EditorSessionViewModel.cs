using Packman.Helpers;
using Packman.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace Packman.ViewModels;

/// <summary>The text and Monaco version captured together for one save.</summary>
public sealed record EditorBufferSnapshot(string Content, int VersionId, bool IsDirty = false);

/// <summary>What the editor session needs from the view: a channel to Monaco.</summary>
public interface IMonacoHost
{
    bool IsReady { get; }
    void Post(object message);
    Task<EditorBufferSnapshot?> GetBufferAsync(string path);
    Task<bool> TryReloadAsync(object message);
}

/// <summary>
/// The script editor's state: open files, dirty tracking, save/revert/reload with the
/// on-disk conflict check, the package-folder watcher, search and the status bar. The
/// view owns WebView2 and the file tree; everything else lives here so it can be tested
/// without a browser.
/// </summary>
public sealed class EditorSessionViewModel : ObservableObject
{
    private const long MaxEditorFileBytes = 8 * 1024 * 1024;
    private static readonly IReadOnlySet<string> TextExtensions = PackageFileSearch.TextExtensions;
    private static readonly IReadOnlySet<string> PowerShellExtensions = PackageFileSearch.PowerShellExtensions;

    private readonly CreatePackageViewModel _create;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private IMonacoHost? _host;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _searchCts;
    private OpenFile? _active;
    private string? _pendingOpenPath;    // file waiting for the editor to become ready
    private string? _loadedPackagePath;  // package the session currently shows
    private List<string>? _reopenAfterCrash;
    private string _searchQuery = "";

    public ObservableCollection<OpenFile> OpenFiles { get; } = new();
    public ObservableCollection<SearchHit> SearchHits { get; } = new();

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand<OpenFile> ActivateCommand { get; }
    public RelayCommand<OpenFile> CloseCommand { get; }

    /// <summary>The package on disk changed or a new one was loaded; the view rescans its tree.</summary>
    public event Action? TreeRefreshRequested;

    /// <summary>The active tab changed; the view highlights it in the tree.</summary>
    public event Action<string>? ActiveFileChanged;

    public EditorSessionViewModel(CreatePackageViewModel create, IDialogService dialogs)
    {
        _create = create;
        _dialogs = dialogs;

        SaveCommand = new RelayCommand(() => { if (_active != null) ErrorReporter.FireAndForget(() => SaveAsync(_active)); }, () => CanSave);
        RevertCommand = new RelayCommand(() => { if (_active != null) ErrorReporter.FireAndForget(() => ReadIntoEditorAsync(_active)); }, () => CanRevert);
        ReloadCommand = new RelayCommand(() => ErrorReporter.FireAndForget(ReloadActiveAsync), () => CanReload);
        ActivateCommand = new RelayCommand<OpenFile>(f => { if (f != null) Activate(f); });
        CloseCommand = new RelayCommand<OpenFile>(f => { if (f != null) ErrorReporter.FireAndForget(() => CloseAsync(f)); });

        _watchTimer.Tick += (_, _) => { _watchTimer.Stop(); ErrorReporter.FireAndForget(OnPackageChangedOnDiskAsync); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ErrorReporter.FireAndForget(() => RunSearchAsync(_searchQuery)); };

        _create.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CreatePackageViewModel.CurrentPackagePath))
                RaiseAll(nameof(HasPackage), nameof(PackageName), nameof(ApplicationFolder));
        };
    }

    // ── Package ────────────────────────────────────────────────────────

    public bool HasPackage => !string.IsNullOrEmpty(_create.CurrentPackagePath);
    public string PackageName => HasPackage ? new DirectoryInfo(_create.CurrentPackagePath).Name : "";
    public string ApplicationFolder => PsadtLayout.ApplicationFolder(_create.CurrentPackagePath);

    /// <summary>True while any open file has unsaved edits.</summary>
    public bool HasUnsavedChanges => OpenFiles.Any(f => f.IsDirty);

    public void AttachHost(IMonacoHost host) => _host = host;

    /// <summary>Shows the current package: on a new one, closes the old files and opens the script.</summary>
    public void LoadPackage()
    {
        var packagePath = _create.CurrentPackagePath;
        if (string.IsNullOrEmpty(packagePath) || !Directory.Exists(ApplicationFolder))
        {
            StopWatching();
            return;
        }

        var isNewPackage = packagePath != _loadedPackagePath;
        if (isNewPackage)
        {
            CloseAll();
            _loadedPackagePath = packagePath;
            StartWatching(ApplicationFolder);
        }

        TreeRefreshRequested?.Invoke();
        PostPsadtVersion();

        if (isNewPackage)
        {
            var script = Path.Combine(ApplicationFolder, PsadtLayout.ScriptName);
            if (File.Exists(script)) Open(script);
        }
    }

    /// <summary>Tells the page which PSADT the package bundles, so completion hides functions it lacks.</summary>
    private void PostPsadtVersion() =>
        _host?.Post(new { type = "psadtVersion", version = PsadtManifest.ReadModuleVersion(ApplicationFolder) });

    // ── Editor callbacks (from the host) ───────────────────────────────

    /// <summary>Monaco is up: replay what was waiting on it.</summary>
    public void OnEditorReady()
    {
        if (_loadedPackagePath != null) PostPsadtVersion();
        if (_reopenAfterCrash != null)
        {
            var files = _reopenAfterCrash;
            _reopenAfterCrash = null;
            foreach (var path in files.Where(File.Exists)) Open(path);
        }
        if (_pendingOpenPath != null)
        {
            var path = _pendingOpenPath;
            _pendingOpenPath = null;
            Open(path);
        }
    }

    /// <summary>The renderer died: its buffers are gone. Remember what to reopen after the reload.</summary>
    public void OnEditorCrashed()
    {
        var reopen = OpenFiles.Select(f => f.Path).ToList();
        var active = _active?.Path;
        OpenFiles.Clear();
        _active = null;
        _pendingOpenPath = active ?? reopen.FirstOrDefault();
        _reopenAfterCrash = reopen;
        RefreshStatus();
    }

    public void OnDirtyChanged(string? path, bool dirty)
    {
        var file = OpenFiles.FirstOrDefault(f => f.Path == path);
        if (file == null) return;
        file.IsDirty = dirty;
        if (file == _active) RefreshStatus();
    }

    public void OnCursor(int line, int column, int selected)
    {
        CursorText = $"Ln {line}, Col {column}";
        SelectionText = selected > 0 ? $"{selected} selected" : "";
    }

    public void OnSaveRequested()
    {
        if (_active != null) ErrorReporter.FireAndForget(() => SaveAsync(_active));
    }

    /// <summary>
    /// Parses a buffer and sends syntax errors back as Monaco markers. Off the UI thread:
    /// it fires on every keystroke pause and a large script is enough to feel.
    /// </summary>
    public async Task ValidateAsync(string? path, string? content)
    {
        if (path is null || content is null) return;

        var errors = await Task.Run(() => PowerShellSyntaxValidator.Validate(content));
        _host?.Post(new
        {
            type = "markers",
            path,
            markers = errors.Select(e => new
            {
                line = e.Line,
                column = e.Column,
                endLine = e.EndLine,
                endColumn = e.EndColumn,
                message = e.Message,
            }),
        });

        var file = OpenFiles.FirstOrDefault(f => f.Path == path);
        if (file == null) return;
        file.ErrorCount = errors.Count;
        if (file == _active) RefreshStatus();
    }

    // ── Status bar and action state ────────────────────────────────────

    private string _cursorText = "Ln 1, Col 1";
    public string CursorText { get => _cursorText; private set => Set(ref _cursorText, value); }

    private string _selectionText = "";
    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }

    public string StatusEol => _active is null ? "" : _active.Crlf ? "CRLF" : "LF";
    public string StatusEncoding => _active?.EncodingLabel ?? "";
    public string StatusLanguage => _active is null ? "" : _active.IsPowerShell ? "PowerShell" : "Plain Text";

    public int ProblemCount => _active?.IsPowerShell == true ? _active.ErrorCount : 0;
    public bool HasProblems => ProblemCount > 0;
    public string StatusProblems => _active?.IsPowerShell != true ? ""
        : ProblemCount == 0 ? "No problems"
        : ProblemCount == 1 ? "1 problem"
        : $"{ProblemCount} problems";

    public bool CanSave => _active is { IsDirty: true, IsReadOnly: false };
    public bool CanRevert => _active?.IsDirty == true;
    public bool CanReload => _active != null;
    public bool ShowChangedOnDisk => _active?.ChangedOnDisk == true;

    private void RefreshStatus()
    {
        RaiseAll(nameof(StatusEol), nameof(StatusEncoding), nameof(StatusLanguage), nameof(ProblemCount),
                 nameof(HasProblems), nameof(StatusProblems), nameof(CanSave), nameof(CanRevert),
                 nameof(CanReload), nameof(ShowChangedOnDisk), nameof(HasUnsavedChanges));
        SaveCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
    }

    // ── Watching the package folder ────────────────────────────────────

    private void StartWatching(string folder)
    {
        StopWatching();
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnPackageFolderEvent;
            _watcher.Created += OnPackageFolderEvent;
            _watcher.Deleted += OnPackageFolderEvent;
            _watcher.Renamed += OnPackageFolderEvent;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not watch package folder: {ex.Message}");
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchTimer.Stop();
    }

    // Watcher callbacks arrive on a pool thread; the debounce timer lives on the UI thread.
    private void OnPackageFolderEvent(object sender, FileSystemEventArgs e) =>
        _watchTimer.Dispatcher.InvokeAsync(() => { _watchTimer.Stop(); _watchTimer.Start(); });

    /// <summary>
    /// The package folder changed: refresh the tree and pull external edits into open
    /// files. Files with unsaved edits are flagged and left alone.
    /// </summary>
    private async Task OnPackageChangedOnDiskAsync()
    {
        TreeRefreshRequested?.Invoke();

        foreach (var file in OpenFiles.ToList())
        {
            if (!File.Exists(file.Path)) continue;
            if (File.GetLastWriteTimeUtc(file.Path) == file.LastWriteUtc) continue;

            if (file.IsDirty)
            {
                file.ChangedOnDisk = true;
                if (file == _active) RefreshStatus();
            }
            else
            {
                await ReadIntoEditorAsync(file, preserveEdits: true);
            }
        }
    }

    // ── Open / save / close ────────────────────────────────────────────

    public void Open(string path)
    {
        if (_host is not { IsReady: true })
        {
            _pendingOpenPath = path;
            return;
        }

        var existing = OpenFiles.FirstOrDefault(f => f.Path == path);
        if (existing != null)
        {
            Activate(existing);
            return;
        }

        var file = new OpenFile(path);
        OpenFiles.Add(file);
        Activate(file);
        ErrorReporter.FireAndForget(() => ReadIntoEditorAsync(file));
    }

    /// <summary>Reads the file off the share (not on the UI thread) and hands the text to Monaco.</summary>
    private async Task ReadIntoEditorAsync(OpenFile file, bool preserveEdits = false)
    {
        var beforeRead = preserveEdits && _host != null ? await _host.GetBufferAsync(file.Path) : null;
        if (preserveEdits && (beforeRead == null || beforeRead.IsDirty || file.IsDirty))
        {
            FlagChangedOnDisk(file);
            return;
        }

        var ext = Path.GetExtension(file.Path);
        string content;
        var encoding = file.Encoding;
        var crlf = file.Crlf;
        string encodingLabel;
        bool isReadOnly;

        if (!TextExtensions.Contains(ext))
        {
            content = $"[Binary file — open in an external editor to view]\n\n{file.Path}";
            isReadOnly = true;
            encodingLabel = "binary";
        }
        else
        {
            try
            {
                var text = await Task.Run(() =>
                {
                    if (new FileInfo(file.Path).Length > MaxEditorFileBytes)
                        throw new IOException("This file exceeds the 8 MB editor limit. Open it in the external editor to view it.");
                    return TextFileIO.Read(file.Path);
                });
                content = text.Content;
                encoding = text.Encoding;
                crlf = text.Crlf;
                encodingLabel = EncodingLabel(text.Encoding);
                isReadOnly = false;
            }
            catch (Exception ex)
            {
                content = $"Could not read file: {ex.Message}";
                isReadOnly = true;
                encodingLabel = "—";
            }
        }

        // The tab may have been closed while the read was in flight.
        if (!OpenFiles.Contains(file)) return;

        var lastWriteUtc = File.Exists(file.Path) ? File.GetLastWriteTimeUtc(file.Path) : DateTime.MinValue;
        var isPowerShell = PowerShellExtensions.Contains(ext);
        var message = new
        {
            type = "open",
            path = file.Path,
            content,
            language = isPowerShell ? "powershell" : "plaintext",
            readOnly = isReadOnly,
            eol = crlf ? "crlf" : "lf",
            activate = file == _active,
            expectedVersionId = beforeRead?.VersionId,
        };

        if (preserveEdits)
        {
            // Check and replace atomically inside Monaco. A C# check followed by Post
            // would still allow a keystroke between those two browser operations.
            if (_host == null || !await _host.TryReloadAsync(message))
            {
                FlagChangedOnDisk(file);
                return;
            }
        }
        else
        {
            _host?.Post(message);
        }

        file.Encoding = encoding;
        file.Crlf = crlf;
        file.EncodingLabel = encodingLabel;
        file.IsReadOnly = isReadOnly;
        file.LastWriteUtc = lastWriteUtc;
        file.ChangedOnDisk = false;
        file.IsPowerShell = isPowerShell;

        if (file == _active) RefreshStatus();
    }

    private void FlagChangedOnDisk(OpenFile file)
    {
        file.ChangedOnDisk = true;
        if (file == _active) RefreshStatus();
    }

    public void Activate(OpenFile file)
    {
        foreach (var f in OpenFiles) f.IsActive = ReferenceEquals(f, file);
        _active = file;
        _host?.Post(new { type = "activate", path = file.Path });
        RefreshStatus();
        ActiveFileChanged?.Invoke(file.Path);
    }

    private static string EncodingLabel(Encoding encoding) => encoding switch
    {
        UTF8Encoding utf8 => utf8.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8",
        UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE",
        UnicodeEncoding => "UTF-16 LE",
        _ => encoding.WebName.ToUpperInvariant(),
    };

    /// <summary>Writes the buffer back in the file's original encoding. False when it did not happen.</summary>
    public async Task<bool> SaveAsync(OpenFile file)
    {
        // Ctrl+S and a close/save prompt can overlap. Serialize writes to the same
        // temporary file and capture each buffer only after the preceding save ends.
        await _saveGate.WaitAsync();
        try
        {
            return await SaveCoreAsync(file);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> SaveCoreAsync(OpenFile file)
    {
        if (file.IsReadOnly || _host == null) return true;

        var buffer = await _host.GetBufferAsync(file.Path);
        if (buffer == null)
        {
            _dialogs.Warn($"Could not read the editor buffer for {file.Name}.", "Save failed");
            return false;
        }

        if (File.Exists(file.Path) && File.GetLastWriteTimeUtc(file.Path) != file.LastWriteUtc)
        {
            var overwrite = _dialogs.Confirm(
                $"{file.Name} has changed on disk since it was opened.\n\nOverwrite it with your version?",
                "File changed on disk", "Overwrite");
            if (!overwrite) return false;
        }

        try
        {
            await Task.Run(() => TextFileIO.Write(file.Path, buffer.Content, file.Encoding));
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"Could not save file: {ex.Message}", "Save failed");
            return false;
        }

        file.LastWriteUtc = File.GetLastWriteTimeUtc(file.Path);
        file.ChangedOnDisk = false;
        _host.Post(new { type = "markSaved", path = file.Path, versionId = buffer.VersionId });
        if (file == _active) RefreshStatus();

        // A save-and-close must stop if the user kept typing during the share write.
        // Monaco retains those edits as dirty; a later save can persist them.
        var current = await _host.GetBufferAsync(file.Path);
        return current?.VersionId == buffer.VersionId;
    }

    private async Task ReloadActiveAsync()
    {
        var file = _active;
        if (file == null) return;
        if (file.IsDirty &&
            !_dialogs.Confirm($"Discard your unsaved changes to {file.Name} and reload it from disk?", "Reload file", "Discard and reload", "Keep my changes"))
            return;

        await ReadIntoEditorAsync(file);
    }

    /// <summary>Offers to save every dirty file. False means the user cancelled.</summary>
    public async Task<bool> PromptSaveAllAsync()
    {
        if (_host is not { IsReady: true }) return true;

        var dirty = OpenFiles.Where(f => f.IsDirty).ToList();
        if (dirty.Count == 0) return true;

        var names = string.Join("\n", dirty.Select(f => f.Name));
        var answer = _dialogs.ConfirmOrCancel(
            dirty.Count == 1 ? $"Save changes to {names}?" : $"Save changes to these {dirty.Count} files?\n\n{names}",
            "Unsaved changes", "Save", "Don't save");
        if (answer == null) return false;

        foreach (var file in dirty)
        {
            if (answer == true)
            {
                if (!await SaveAsync(file)) return false;
            }
            else
            {
                await ReadIntoEditorAsync(file);   // discard: put the file back the way it is on disk
            }
        }
        return true;
    }

    public async Task CloseAsync(OpenFile file)
    {
        if (file.IsDirty)
        {
            var answer = _dialogs.ConfirmOrCancel($"Save changes to {file.Name}?", "Unsaved changes", "Save", "Don't save");
            if (answer == null) return;
            if (answer == true && !await SaveAsync(file)) return;
        }

        var index = OpenFiles.IndexOf(file);
        OpenFiles.Remove(file);
        _host?.Post(new { type = "close", path = file.Path });

        if (file == _active)
        {
            _active = null;
            if (OpenFiles.Count > 0) Activate(OpenFiles[Math.Min(index, OpenFiles.Count - 1)]);
            else RefreshStatus();
        }
    }

    private void CloseAll()
    {
        foreach (var file in OpenFiles)
            _host?.Post(new { type = "close", path = file.Path });
        OpenFiles.Clear();
        _active = null;
        RefreshStatus();
    }

    // ── Search ─────────────────────────────────────────────────────────

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!Set(ref _searchQuery, value)) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    private bool _showSearchResults;
    public bool ShowSearchResults { get => _showSearchResults; private set => Set(ref _showSearchResults, value, [nameof(ShowTree)]); }
    public bool ShowTree => !_showSearchResults;

    private async Task RunSearchAsync(string query)
    {
        _searchCts?.Cancel();

        if (query.Length < 2)
        {
            ShowSearchResults = false;
            return;
        }

        var appFolder = ApplicationFolder;
        if (!Directory.Exists(appFolder)) return;

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        List<SearchHit> hits;
        try
        {
            hits = await Task.Run(() => PackageFileSearch.Search(appFolder, query, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested) return;   // a newer query is already running

        SearchHits.Clear();
        foreach (var hit in hits) SearchHits.Add(hit);
        ShowSearchResults = true;
    }

    public void OpenSearchHit(SearchHit hit)
    {
        Open(hit.Path);
        if (hit.Line > 0) _host?.Post(new { type = "reveal", line = hit.Line });
    }

    // ── External editor ────────────────────────────────────────────────

    /// <summary>Opens the active file, or the deploy script, in VS Code or ISE.</summary>
    public void OpenInExternalEditor()
    {
        var target = _active?.Path;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
            target = Path.Combine(ApplicationFolder, PsadtLayout.ScriptName);

        if (!File.Exists(target))
        {
            _dialogs.Info("Script not found. Generate the package first.", "Not Found");
            return;
        }

        try
        {
            EditorLocator.Open(target);
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"Could not open script: {ex.Message}", "Error");
        }
    }
}
