using Packman.Helpers;
using Packman.Models;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

namespace Packman.ViewModels;

/// <summary>
/// The Devices section of Application detail: every device's install status for the app,
/// searched by device or user, filtered by status, and exported as CSV.
/// </summary>
public sealed class DeviceStatusViewModel : ObservableObject
{
    public const string EmptyText = "No devices have reported for this app yet.";

    private readonly Func<IProgress<(int Read, int Total)>, CancellationToken, Task<List<DeviceInstallStatus>>> _load;
    private List<DeviceInstallStatus> _all = new();
    private CancellationTokenSource? _cts;

    public DeviceStatusViewModel(Func<IProgress<(int Read, int Total)>, CancellationToken, Task<List<DeviceInstallStatus>>> load)
    {
        _load = load;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsLoading);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>The rows that match the search and the status filter, in device-name order.</summary>
    public ObservableCollection<DeviceInstallStatus> Rows { get; } = new();

    // ── Search and status filter ──
    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    private string _filter = "all";
    /// <summary>"all", "installed", "failed", "pending", "notinstalled" or "notapplicable"; the chips bind to it.</summary>
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) ApplyFilter(); }
    }

    public string AllChip => $"All {_all.Count:N0}";
    public string InstalledChip => Chip(DeviceInstallState.Installed);
    public string FailedChip => Chip(DeviceInstallState.Failed);
    public string PendingChip => Chip(DeviceInstallState.Pending);
    public string NotInstalledChip => Chip(DeviceInstallState.NotInstalled);
    public string NotApplicableChip => Chip(DeviceInstallState.NotApplicable);

    private string Chip(DeviceInstallState state) =>
        $"{DeviceInstallStatus.Label(state)} {_all.Count(d => d.State == state):N0}";

    // ── Selection ──
    private DeviceInstallStatus? _selected;
    public DeviceInstallStatus? Selected
    {
        get => _selected;
        set => Set(ref _selected, value, [nameof(HasSelection)]);
    }
    public bool HasSelection => _selected != null;

    // ── Load state ──
    private bool _hasLoaded;
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value, [nameof(HasRows), nameof(HasNoMatches), nameof(CanExport), nameof(StateText)])) return;
            RefreshCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private string _errorText = "";
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }

    public bool HasRows => !IsLoading && Rows.Count > 0;
    public bool HasNoMatches => !IsLoading && _all.Count > 0 && Rows.Count == 0;
    public bool CanExport => HasRows;

    /// <summary>Loading, empty or not-yet-loaded text; blank when there is a list or an error to show.</summary>
    public string StateText =>
        IsLoading ? "Loading devices…"
        : _hasLoaded && _all.Count == 0 ? EmptyText
        : !_hasLoaded && ErrorText.Length == 0 ? "Device install status has not been loaded."
        : "";

    public string CountText => _all.Count == 0 ? "" : $"Showing {Rows.Count:N0} of {_all.Count:N0} devices";

    // ── ActivityDock: shown once a load runs past DockDelay, or as soon as the report needs more pages ──
    /// <summary>How long a load may run, first request included, before the dock offers Cancel.</summary>
    public TimeSpan DockDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    private int _loadGeneration;

    private bool _isDockActive;
    public bool IsDockActive { get => _isDockActive; private set => Set(ref _isDockActive, value); }

    private string _dockPhase = "";
    public string DockPhase { get => _dockPhase; private set => Set(ref _dockPhase, value); }

    private int _dockPercent;
    public int DockPercent { get => _dockPercent; private set => Set(ref _dockPercent, value); }

    /// <summary>Until a page reports a total, the dock shows activity rather than a percentage.</summary>
    private bool _isDockIndeterminate = true;
    public bool IsDockIndeterminate { get => _isDockIndeterminate; private set => Set(ref _isDockIndeterminate, value); }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        ErrorText = "";
        DockPhase = "Waiting for Intune…";
        DockPercent = 0;
        IsDockIndeterminate = true;
        IsLoading = true;
        _ = ShowDockIfSlowAsync(++_loadGeneration);
        try
        {
            var rows = await _load(new LoadProgress(this), _cts.Token);
            _all = rows.OrderBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase).ToList();
            _hasLoaded = true;
        }
        catch (OperationCanceledException)
        {
            _all = new();
            _hasLoaded = false;
            ErrorText = "Loading was cancelled. Refresh to load the device list.";
        }
        catch (Exception ex)
        {
            _all = new();
            _hasLoaded = false;
            ErrorText = $"Could not load device install status: {ex.Message}";
        }
        finally
        {
            IsDockActive = false;
            IsLoading = false;
            ApplyFilter();
        }
    }

    private async Task ShowDockIfSlowAsync(int generation)
    {
        await Task.Delay(DockDelay);
        if (IsLoading && generation == _loadGeneration) IsDockActive = true;
    }

    private void OnProgress(int read, int total)
    {
        if (!IsLoading) return;
        if (total > read)
        {
            IsDockActive = true;
            IsDockIndeterminate = false;
            DockPhase = $"Read {read:N0} of {total:N0} devices";
            DockPercent = read * 100 / total;
        }
        else
        {
            DockPhase = $"Read {read:N0} devices";
        }
    }

    /// <summary>Reports in line: the service resumes on the UI thread between pages.</summary>
    private sealed class LoadProgress : IProgress<(int Read, int Total)>
    {
        private readonly DeviceStatusViewModel _vm;
        public LoadProgress(DeviceStatusViewModel vm) => _vm = vm;

        public void Report((int Read, int Total) value) => _vm.OnProgress(value.Read, value.Total);
    }

    private void ApplyFilter()
    {
        var query = _search.Trim();
        var keep = Selected;
        Rows.Clear();
        foreach (var d in _all)
        {
            if (_filter != "all" && !string.Equals(_filter, d.State.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            if (query.Length > 0
                && !d.DeviceName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !d.UserPrincipalName.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            Rows.Add(d);
        }
        Selected = keep != null && Rows.Contains(keep) ? keep : null;
        RaiseAll(nameof(HasRows), nameof(HasNoMatches), nameof(CanExport), nameof(StateText), nameof(CountText),
            nameof(AllChip), nameof(InstalledChip), nameof(FailedChip), nameof(PendingChip), nameof(NotInstalledChip), nameof(NotApplicableChip));
    }

    // ── CSV export of the rows on show ──
    public string BuildCsv() => CsvWriter.Build(
        new[] { new[] { "Device", "User", "Status", "Status detail", "Error code (hex)", "Error code", "Error description", "Last modified (UTC)", "OS version" } }
            .Concat(Rows.Select(d => new[]
            {
                d.DeviceName, d.UserPrincipalName, d.StatusLabel, d.StateDetail,
                d.HexErrorCode, d.ErrorCode == 0 ? "" : d.ErrorCode.ToString(CultureInfo.InvariantCulture), d.ErrorDescription,
                d.LastModified?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "", d.OsVersion,
            })));

    public void ExportCsv(string path) => File.WriteAllText(path, BuildCsv(), CsvWriter.Encoding);
}
