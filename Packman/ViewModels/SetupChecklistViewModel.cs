using Packman.Helpers;
using Packman.Models;
using Packman.Services;

namespace Packman.ViewModels;

/// <summary>One prerequisite on the welcome card. Kind is "ok", "warn" or "bad", as in <see cref="SetupCheck"/>.</summary>
public sealed record SetupItem(string Label, string Kind, string Message, RelayCommand Go)
{
    public bool IsOk => Kind == "ok";
}

/// <summary>
/// The welcome card on the Create page: what Packman needs before a package can be built
/// and published, each with a route to the setting that fixes it. "Got it" hides the card
/// for good, but it comes back while something that blocks packaging is missing.
/// </summary>
public sealed class SetupChecklistViewModel : ObservableObject
{
    public const string ExeSwitchesText =
        "Packman can't guess an EXE's silent switches. Pick them in the Installer panel, or type them, before you generate. " +
        "Otherwise replace the <silent flags> and <uninstall flags> placeholders in Edit script; Review blocks Publish until they are gone.";

    private readonly SettingsService _settings;
    private readonly IntuneAuthService _auth;
    private int _sequence;

    public SetupChecklistViewModel(SettingsService settings, IntuneAuthService auth)
    {
        _settings = settings;
        _auth = auth;
        DismissCommand = new RelayCommand(Dismiss);

        _settings.Saved += () => ErrorReporter.FireAndForget(RefreshAsync);
        _auth.StateChanged += () => ErrorReporter.FireAndForget(RefreshAsync);
        ErrorReporter.FireAndForget(RefreshAsync);
    }

    // Replaced whole, never changed in place: StateChanged can arrive on a pool thread.
    private IReadOnlyList<SetupItem> _items = [];
    public IReadOnlyList<SetupItem> Items
    {
        get => _items;
        private set => Set(ref _items, value, [nameof(HasMissing), nameof(IsVisible)]);
    }

    /// <summary>Something blocks packaging or publishing. A missing sign-in only warns.</summary>
    public bool HasMissing => _items.Any(i => i.Kind == "bad");

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; private set => Set(ref _isChecking, value); }

    public bool IsVisible => !_settings.Settings.WelcomeCardDismissed || HasMissing;

    public RelayCommand DismissCommand { get; }

    public async Task RefreshAsync()
    {
        var sequence = Interlocked.Increment(ref _sequence);
        IsChecking = true;
        var paths = _settings.Settings.NetworkPaths;
        var (share, template, converter) = (paths.IntuneApplications, paths.PSADTTemplateInUse, paths.IntuneWinAppUtilInUse);

        // The share and template are often UNC paths, which can take seconds to time out.
        var checks = await Task.Run(() => new[]
        {
            SetupChecks.ApplicationsRoot(share),
            SetupChecks.Template(template),
            SetupChecks.IntuneWinAppUtil(converter),
        });
        if (sequence != Volatile.Read(ref _sequence)) return;

        Items =
        [
            Item("Output share", checks[0], AppNavigation.SettingsPaths),
            Item("PSADT template", checks[1], AppNavigation.SettingsPaths),
            Item("IntuneWinAppUtil", checks[2], AppNavigation.SettingsPaths),
            Item("Intune sign-in", SignInCheck(), AppNavigation.Settings),
        ];
        IsChecking = false;
    }

    private SetupCheck SignInCheck() =>
        _auth.IsSignedIn ? new("ok", $"Signed in as {_auth.SignedInUser}.")
        : _settings.Settings.AuthMode == AuthMode.AppRegistration ? new("ok", "The app registration connects when you publish.")
        : new("warn", "Sign in to publish. Packaging works without it.");

    private static SetupItem Item(string label, SetupCheck check, string page) =>
        new(label, check.Kind, check.Message, new RelayCommand(() => AppNavigation.Go(page)));

    private void Dismiss()
    {
        _settings.Settings.WelcomeCardDismissed = true;
        try
        {
            _settings.Save();
        }
        catch (Exception)
        {
            // Hidden for this run; it just won't be remembered.
        }
        OnPropertyChanged(nameof(IsVisible));
    }
}
