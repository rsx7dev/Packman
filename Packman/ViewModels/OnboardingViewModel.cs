using Packman.Helpers;
using Packman.Services;

namespace Packman.ViewModels;

/// <summary>
/// First-run setup: the one thing Packman cannot guess, the folder packages are written to.
/// The PSADT template and IntuneWinAppUtil ship with Packman, and connecting to Intune is
/// done in Settings when publishing, so neither is asked here.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly SettingsService _svc;
    private CancellationTokenSource? _cts;

    /// <summary>Raised after the packages folder has been saved.</summary>
    public event Action? Completed;

    private string _applicationsRoot;
    public string ApplicationsRoot
    {
        get => _applicationsRoot;
        set { if (Set(ref _applicationsRoot, value)) ApplicationsRootCheck = null; }
    }

    private SetupCheck? _applicationsRootCheck;
    /// <summary>The result of the last check of the folder; null once the folder is edited.</summary>
    public SetupCheck? ApplicationsRootCheck { get => _applicationsRootCheck; private set => Set(ref _applicationsRootCheck, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) NextCommand.RaiseCanExecuteChanged(); }
    }

    public AsyncRelayCommand NextCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>What Get started runs: checks the folder, saves it and completes. A bad folder stays on the page.</summary>
    public async Task NextAsync()
    {
        if (IsBusy) return;

        var root = ApplicationsRoot.Trim();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            // WaitAsync lets Cancel free the page while a dead share is still timing out.
            var check = await Task.Run(() => SetupChecks.ApplicationsRoot(root)).WaitAsync(_cts.Token);
            // A folder edited while the check ran makes it stale; the user presses Get started again.
            if (root != ApplicationsRoot.Trim()) return;
            ApplicationsRootCheck = check;
            if (check.Kind == "bad") return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _svc.Settings.NetworkPaths.IntuneApplications = root;
        try
        {
            _svc.Save();
        }
        catch (Exception ex)
        {
            ApplicationsRootCheck = new("bad", $"Settings could not be saved: {ex.Message}");
            return;
        }
        Completed?.Invoke();
    }

    public OnboardingViewModel(SettingsService svc)
    {
        _svc = svc;
        _applicationsRoot = svc.Settings.NetworkPaths.IntuneApplications;
        NextCommand = new AsyncRelayCommand(NextAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }
}
