using Packman.Models;
using Packman.Services;
using System.Collections.ObjectModel;

namespace Packman.ViewModels;

/// <summary>
/// The Applications library screen: loads the Win32 app list, then filters and pages it
/// client-side at 50 a page so a large tenant isn't one endless scroll.
/// </summary>
public sealed class ApplicationsViewModel : ObservableObject
{
    private const string AllCategories = "Category: All";
    private const string AllManufacturers = "Publisher: All";
    private const int PageSize = 50;

    private static readonly (string Label, int Days)[] UpdatedWindowChoices =
    {
        ("Updated: Any time", 0),
        ("Updated: Last 7 days", 7),
        ("Updated: Last 30 days", 30),
        ("Updated: Last 90 days", 90),
    };

    private readonly IntuneService _apps = AppServices.Apps;
    private readonly IntuneAuthService _auth = AppServices.Auth;

    private readonly List<IntuneApplication> _all = new();

    /// <summary>The current page of filtered apps; bound by the list.</summary>
    public ObservableCollection<IntuneApplication> Page { get; } = new();
    public ObservableCollection<string> Categories { get; } = new() { AllCategories };
    public ObservableCollection<string> Manufacturers { get; } = new() { AllManufacturers };
    public ObservableCollection<string> UpdatedWindows { get; } =
        new(UpdatedWindowChoices.Select(w => w.Label));

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand<IntuneApplication> OpenCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand SortByNameCommand { get; }
    public RelayCommand SortByUpdatedCommand { get; }
    public RelayCommand ViewInIntuneCommand { get; }

    /// <summary>Raised on row activation; the host swaps in the detail screen.</summary>
    public event Action<IntuneApplication>? OpenRequested;

    /// <summary>Raised on "connect"; the host switches to Settings.</summary>
    public event Action? ConnectRequested;

    private bool _loadedOnce;
    private int _currentPage = 1;
    private int _totalCount;

    public ApplicationsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true), () => !IsLoading);
        NextPageCommand = new RelayCommand(() => GoToPage(_currentPage + 1), () => CanNext);
        PrevPageCommand = new RelayCommand(() => GoToPage(_currentPage - 1), () => CanPrev);
        OpenCommand = new RelayCommand<IntuneApplication>(app => { if (app != null) OpenRequested?.Invoke(app); });
        ConnectCommand = new RelayCommand(() => ConnectRequested?.Invoke());
        SortByNameCommand = new RelayCommand(() => ToggleSort("name"));
        SortByUpdatedCommand = new RelayCommand(() => ToggleSort("updated"));
        ViewInIntuneCommand = new RelayCommand(() =>
        {
            if (SelectedApp == null) return;
            try { ApplicationDetailViewModel.OpenInIntune(SelectedApp.Id); }
            catch (Exception ex) { ErrorReporter.Report(ex); }
        });

        // Sign-out or a different tenant: the cached list belongs to the old session.
        _auth.StateChanged += () =>
        {
            _loadedOnce = false;
            _all.Clear();
            _currentPage = 1;
            ApplyFilters();
            StatusText = _auth.IsSignedIn ? "" : "Sign in on the Settings page to load applications from Intune.";
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowConnectPrompt));
            OnPropertyChanged(nameof(ShowPager));
        };
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) { _currentPage = 1; ApplyFilters(); } }
    }

    private string _selectedCategory = AllCategories;
    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value)) { _currentPage = 1; ApplyFilters(); } }
    }

    private string _selectedManufacturer = AllManufacturers;
    public string SelectedManufacturer
    {
        get => _selectedManufacturer;
        set { if (Set(ref _selectedManufacturer, value)) { _currentPage = 1; ApplyFilters(); } }
    }

    private string _selectedUpdatedWindow = UpdatedWindowChoices[0].Label;
    public string SelectedUpdatedWindow
    {
        get => _selectedUpdatedWindow;
        set { if (Set(ref _selectedUpdatedWindow, value)) { _currentPage = 1; ApplyFilters(); } }
    }

    // ── Sorting (column headers): default is Updated, newest first ──
    private string _sortColumn = "updated";
    private bool _sortDesc = true;

    public string NameHeader => _sortColumn == "name" ? (_sortDesc ? "Application ↓" : "Application ↑") : "Application";
    public string UpdatedHeader => _sortColumn == "updated" ? (_sortDesc ? "Updated ↓" : "Updated ↑") : "Updated";

    // ── Selection: one click selects (rail shows it), double click or Enter opens ──
    private IntuneApplication? _selectedApp;
    public IntuneApplication? SelectedApp
    {
        get => _selectedApp;
        set => Set(ref _selectedApp, value, [nameof(SelectedMeta), nameof(SelectedStateText), nameof(SelectedCategoryText)]);
    }

    public string SelectedMeta => SelectedApp == null ? "" : $"{SelectedApp.Version} · Win32";

    public string SelectedStateText => SelectedApp switch
    {
        null => "",
        { PublishingState: "" } => "Unknown",
        { ShowStateWarning: true } a => a.StateWarningText,
        _ => "Published",
    };

    public string SelectedCategoryText => string.IsNullOrWhiteSpace(SelectedApp?.Category) ? "None" : SelectedApp.Category;

    private DateTime? _lastRefreshed;
    /// <summary>When the list last finished loading from Intune; shown in the footer.</summary>
    public string LastRefreshedText => _lastRefreshed is { } t ? $"Last refreshed at {t:t}" : "";

    private void ToggleSort(string column)
    {
        if (_sortColumn == column)
        {
            _sortDesc = !_sortDesc;
        }
        else
        {
            _sortColumn = column;
            _sortDesc = column == "updated";   // dates newest-first, names A→Z
        }
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(UpdatedHeader));
        _currentPage = 1;
        ApplyFilters();
    }

    public int CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

    private int _maxPage = 1;
    public int MaxPage { get => _maxPage; private set => Set(ref _maxPage, value); }

    private bool _canPrev, _canNext;
    public bool CanPrev { get => _canPrev; private set { if (Set(ref _canPrev, value)) PrevPageCommand.RaiseCanExecuteChanged(); } }
    public bool CanNext { get => _canNext; private set { if (Set(ref _canNext, value)) NextPageCommand.RaiseCanExecuteChanged(); } }

    private string _pageDisplay = "Page 1 of 1";
    public string PageDisplay { get => _pageDisplay; private set => Set(ref _pageDisplay, value); }

    private string _rangeText = "";
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }

    public bool ShowPager => !IsLoading && _all.Count > 0;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowConnectPrompt));
            OnPropertyChanged(nameof(ShowPager));
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrEmpty(_statusText);

    private string _loadStatus = "";
    public string LoadStatus { get => _loadStatus; private set => Set(ref _loadStatus, value); }

    /// <summary>Shown when nobody is signed in.</summary>
    public bool ShowConnectPrompt => !IsLoading && !_auth.IsSignedIn;

    public bool ShowEmpty => !IsLoading && _auth.IsSignedIn && _all.Count == 0;

    public async Task LoadAsync(bool force = false)
    {
        if (IsLoading) return;

        if (!_auth.IsSignedIn)
        {
            _all.Clear();
            ApplyFilters();
            StatusText = "Sign in on the Settings page to load applications from Intune.";
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowConnectPrompt));
            return;
        }

        if (_loadedOnce && !force) return;

        IsLoading = true;
        StatusText = "";
        LoadStatus = "Connecting to Microsoft Intune…";
        try
        {
            var progress = new Progress<int>(n => LoadStatus = $"Loading applications… {n} fetched");
            var apps = await _apps.GetApplicationsAsync(force, progress);
            _all.Clear();
            _all.AddRange(apps);
            _loadedOnce = true;
            _lastRefreshed = DateTime.Now;
            OnPropertyChanged(nameof(LastRefreshedText));
            _currentPage = 1;
            RebuildCategories();
            RebuildManufacturers();
            ApplyFilters();
            if (_all.Count == 0)
                StatusText = "No Win32 applications found in this tenant.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load applications: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowPager));
        }
    }

    private void GoToPage(int page)
    {
        _currentPage = Math.Clamp(page, 1, MaxPage);
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var matched = _all.Where(Matches);
        var filtered = (_sortColumn switch
        {
            "name" => _sortDesc ? matched.OrderByDescending(a => a.DisplayName) : matched.OrderBy(a => a.DisplayName),
            _ => _sortDesc ? matched.OrderByDescending(a => a.LastModified) : matched.OrderBy(a => a.LastModified),
        }).ToList();
        _totalCount = filtered.Count;

        MaxPage = _totalCount > 0 ? (int)Math.Ceiling(_totalCount / (double)PageSize) : 1;
        if (_currentPage > MaxPage) _currentPage = MaxPage;
        if (_currentPage < 1) _currentPage = 1;
        CurrentPage = _currentPage;

        var skip = (_currentPage - 1) * PageSize;
        var paged = filtered.Skip(skip).Take(PageSize).ToList();

        // Clearing the page makes the list drop its selection; keep it when the app is still shown.
        var selectedId = _selectedApp?.Id;
        Page.Clear();
        foreach (var a in paged) Page.Add(a);
        SelectedApp = paged.FirstOrDefault(a => a.Id == selectedId);

        PageDisplay = $"Page {_currentPage} of {MaxPage}";
        RangeText = _totalCount > 0 ? $"Showing {skip + 1}–{skip + paged.Count} of {_totalCount}" : "No applications";
        CanPrev = _currentPage > 1;
        CanNext = _currentPage < MaxPage;
    }

    private void RebuildCategories()
    {
        var previous = _selectedCategory;
        Categories.Clear();
        Categories.Add(AllCategories);
        foreach (var c in _all
                     .SelectMany(a => a.Category.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                     .Distinct()
                     .OrderBy(c => c))
            Categories.Add(c);

        // Field, not property: the setter would run ApplyFilters a second time.
        _selectedCategory = !string.IsNullOrEmpty(previous) && Categories.Contains(previous) ? previous : AllCategories;
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private void RebuildManufacturers()
    {
        var previous = _selectedManufacturer;
        Manufacturers.Clear();
        Manufacturers.Add(AllManufacturers);
        foreach (var m in _all
                     .Select(a => a.Publisher.Trim())
                     .Where(m => !string.IsNullOrEmpty(m))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(m => m))
            Manufacturers.Add(m);

        _selectedManufacturer = !string.IsNullOrEmpty(previous) && Manufacturers.Contains(previous) ? previous : AllManufacturers;
        OnPropertyChanged(nameof(SelectedManufacturer));
    }

    private bool Matches(IntuneApplication a)
    {
        if (!string.IsNullOrEmpty(_selectedCategory) && _selectedCategory != AllCategories &&
            !a.Category.Split(',', StringSplitOptions.TrimEntries).Contains(_selectedCategory))
            return false;

        if (!string.IsNullOrEmpty(_selectedManufacturer) && _selectedManufacturer != AllManufacturers &&
            !string.Equals(a.Publisher.Trim(), _selectedManufacturer, StringComparison.OrdinalIgnoreCase))
            return false;

        var days = UpdatedWindowChoices.FirstOrDefault(w => w.Label == _selectedUpdatedWindow).Days;
        if (days > 0 && a.LastModified < DateTime.Now.AddDays(-days))
            return false;

        if (string.IsNullOrWhiteSpace(_search)) return true;
        var q = _search.Trim();
        return a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
