using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;

namespace Packman.ViewModels;

/// <summary>An existing tenant app the package will supersede, and how.</summary>
public sealed class SupersededApp : ObservableObject
{
    private string _type = SupersedenceType.Update;

    public SupersededApp(string id, string displayName, string version, string publisher)
    {
        Id = id;
        DisplayName = displayName;
        Version = version;
        Publisher = publisher;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string Publisher { get; }

    public string Type
    {
        get => _type;
        set => Set(ref _type, value, [nameof(TypeLabel), nameof(Summary)]);
    }

    public string TypeLabel => _type == SupersedenceType.Replace ? "Replace" : "Update";

    /// <summary>"v1.2 · Contoso", whichever parts the app has.</summary>
    public string Detail => string.Join(" · ", new[] { string.IsNullOrEmpty(Version) ? "" : $"v{Version}", Publisher }.Where(p => p.Length > 0));

    public string Summary => $"{DisplayName} ({TypeLabel})";
}

/// <summary>
/// The Configure step's Supersedes field: search the connected tenant's Win32 apps, pick the
/// ones this package supersedes and set Update or Replace for each.
/// </summary>
public sealed class SupersedencePickerViewModel : ObservableObject
{
    public const string UpdateHelp = "Update: the new app replaces the previous one in place; the previous app isn't uninstalled first.";
    public const string ReplaceHelp = "Replace: the previous app is uninstalled first, then the new app is installed.";

    private const int MaxResults = 20;

    private readonly IntuneAuthService _auth = AppServices.Auth;
    private readonly IntuneService _apps = AppServices.Apps;
    private readonly Func<string> _ownAppId;

    private string? _tenantId;
    private Task<List<IntuneApplication>>? _appList;
    private int _searchSeq;
    private string _query = "";
    private string _hint = "";
    private IntuneApplication? _suggestion;

    /// <param name="ownAppId">The Intune app the package is already linked to, or "". It can't supersede itself.</param>
    public SupersedencePickerViewModel(Func<string> ownAppId)
    {
        _ownAppId = ownAppId;
        _tenantId = _auth.TenantId;
        _auth.StateChanged += OnAuthChanged;

        AddCommand = new RelayCommand<IntuneApplication>(Add);
        RemoveCommand = new RelayCommand<SupersededApp>(app => { if (app != null) Selected.Remove(app); });
        AddSuggestionCommand = new RelayCommand(() => Add(Suggestion));
        Selected.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (SupersededApp app in e.NewItems) app.PropertyChanged += (_, _) => Changed?.Invoke();
            RaiseAll(nameof(HasSelection), nameof(HasSuggestion));
            Changed?.Invoke();
        };
    }

    /// <summary>The selection or a type in it changed.</summary>
    public event Action? Changed;

    public ObservableCollection<IntuneApplication> Results { get; } = new();
    public ObservableCollection<SupersededApp> Selected { get; } = new();

    public RelayCommand<IntuneApplication> AddCommand { get; }
    public RelayCommand<SupersededApp> RemoveCommand { get; }
    public RelayCommand AddSuggestionCommand { get; }

    public bool IsEnabled => _auth.IsSignedIn;
    public bool IsDisabled => !_auth.IsSignedIn;
    public bool HasSelection => Selected.Count > 0;

    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value)) ErrorReporter.FireAndForget(SearchAsync); }
    }

    /// <summary>Why the last search or add did nothing, or "".</summary>
    public string Hint { get => _hint; private set => Set(ref _hint, value); }

    /// <summary>Upgrade only: the linked previous version, offered but never added on its own.</summary>
    public IntuneApplication? Suggestion
    {
        get => _suggestion;
        private set => Set(ref _suggestion, value, [nameof(HasSuggestion), nameof(SuggestionText)]);
    }

    public bool HasSuggestion => IsEnabled && Suggestion != null && Selected.All(s => s.Id != Suggestion.Id);
    public string SuggestionText => Suggestion == null ? "" : $"Supersede the previous version ({Suggestion.DisplayName}, {Suggestion.Version})";

    /// <summary>Empties the field for a new package.</summary>
    public void Reset()
    {
        Selected.Clear();
        Results.Clear();
        Query = "";
        Hint = "";
        Suggestion = null;
        _appList = null; // apps published since the last package should be searchable
    }

    /// <summary>Offers the app with this id as a one-click suggestion. An app that is gone offers nothing.</summary>
    public async Task SuggestAsync(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return;
        try
        {
            var app = await _apps.GetApplicationDetailAsync(appId);
            Suggestion = new IntuneApplication { Id = app.Id, DisplayName = app.DisplayName, Version = app.Version, Publisher = app.Publisher };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Suggestion = null;
        }
    }

    private async Task SearchAsync()
    {
        var seq = ++_searchSeq;
        var query = Query.Trim();
        Hint = "";
        if (query.Length < 2)
        {
            Results.Clear();
            return;
        }

        List<IntuneApplication> apps;
        try
        {
            apps = await (_appList ??= _apps.GetApplicationsAsync(forceRefresh: true));
        }
        catch (Exception ex)
        {
            _appList = null;
            if (seq != _searchSeq) return;
            Results.Clear();
            Hint = $"Search failed: {ex.Message}";
            return;
        }

        if (seq != _searchSeq) return; // a newer query owns the list
        Results.Clear();
        foreach (var app in apps.Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(MaxResults))
            Results.Add(app);
        Hint = Results.Count == 0 ? "No Win32 apps match that name." : "";
    }

    private void Add(IntuneApplication? app)
    {
        if (app == null) return;
        if (app.Id == _ownAppId())
        {
            Hint = "A package can't supersede itself.";
            return;
        }
        if (Selected.Any(s => s.Id == app.Id))
        {
            Hint = $"{app.DisplayName} is already in the list.";
            return;
        }

        Selected.Add(new SupersededApp(app.Id, app.DisplayName, app.Version, app.Publisher));
        Query = "";
    }

    private void OnAuthChanged()
    {
        RaiseAll(nameof(IsEnabled), nameof(IsDisabled), nameof(HasSuggestion));
        if (_auth.TenantId == _tenantId) return;

        // The chosen apps belong to the tenant that was connected.
        _tenantId = _auth.TenantId;
        Reset();
    }
}
