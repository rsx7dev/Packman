
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Packman;
using Packman.Helpers;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Packman.Views;

// First-run setup on Windows, with the real generator, upgrade service, SettingsService and
// OnboardingViewModel / OnboardingWindow. Every settings file is a temp file. Graph answers from a
// fake handler and the sign-in is UseTestSession with an all-zero tenant, so nothing signs in or
// calls a real service. Covers the package folders for each layout, an upgrade carrying Icon and
// extra folders forward, the Connection gate, Back keeping values, an unedited rerun writing the
// same bytes, Finish touching only its own settings blocks, and a non-empty AutomationProperties.Name
// on every visible input.
//   dotnet run --file tests/SetupTests.cs -c Release
// Exits non-zero when a check fails.
internal static class SetupTests
{
    private const string AllZeroTenant = "00000000-0000-0000-0000-000000000000";
    private const string FakeClientId = "00000000-0000-0000-0000-000000000001";
    private const string FakeThumbprint = "0000000000000000000000000000000000000000";
    private const string Permission = "DeviceManagementApps.ReadWrite.All";
    private const string Version1 = "1.0";
    private const string Version2 = "2.0";

    // Settings blocks Finish does not own. Everything else must come back unchanged.
    private static readonly string[] UntouchedBlocks =
        ["Theme", "NavHidden", "CodeSigning", "GroupAssignment", "IntuneDefaults", "RemoteTest"];

    private static int _passed;
    private static readonly List<string> Failures = new();

    [STAThread]
    public static int Main()
    {
        // Graph and sign-in are faked for the whole run; no check reaches a real tenant.
        GraphClient.Http = new HttpClient(new FakeGraph());
        AppServices.Auth.UseTestSession(AllZeroTenant, FakeToken());

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();

        var checks = new (string Name, Action Check)[]
        {
            ("Generator with Icon on creates Application, Intune and Icon", GeneratorIconOn),
            ("Generator with Icon off creates Application and Intune only", GeneratorIconOff),
            ("Generator with two extra folders creates them empty next to Application and Intune", GeneratorExtraFolders),
            ("Upgrade copies Icon and extra-folder contents from the previous version", UpgradeCopiesOptionalFolders),
            ("Connection Next is blocked until Test connection passes or Connect later is chosen", ConnectionGate),
            ("A passing Test connection unlocks Connection Next, and editing a field clears it", ConnectionPassAndEdit),
            ("Test connection signs in with the chosen method and tenant, not an earlier session", ConnectionTestsChosenMethod),
            ("A connection test finishing after an edit does not unlock Next", ConnectionEditDuringTest),
            ("A path edited while the checks run is not advanced on the old result", PathEditDuringCheck),
            ("Going Back and then Next keeps every value entered on earlier steps", BackKeepsValues),
            ("An unedited rerun Finish leaves the settings file byte-identical", UneditedRerunIdentical),
            ("Finish with edits changes only AuthMode, Authentication, NetworkPaths and PackageLayout", FinishTouchesOwnedBlocks),
            ("A layout saved in Settings applies to the next package and leaves the first untouched", SettingsLayoutAppliesToNewPackages),
            ("Every visible onboarding input has a non-empty AutomationProperties.Name", InputsAreNamed),
        };

        foreach (var (name, check) in checks)
        {
            try
            {
                check();
                _passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        Console.WriteLine(Failures.Count == 0
            ? $"All {_passed} checks passed."
            : $"{Failures.Count} of {checks.Length} checks failed.");
        return Failures.Count == 0 ? 0 : 1;
    }

    // Package generation and upgrade

    private static void GeneratorIconOn()
    {
        var root = TempDir();
        var package = Generate(root, new AppSettings.PackageLayoutConfig { IconFolder = true });
        IsTrue(Directory.Exists(Path.Combine(package, "Application")), "Application exists");
        IsTrue(Directory.Exists(Path.Combine(package, "Intune")), "Intune exists");
        IsTrue(Directory.Exists(Path.Combine(package, "Icon")), "Icon exists");
        IsTrue(File.Exists(Path.Combine(package, "Application", "Invoke-AppDeployToolkit.ps1")), "script in Application");
    }

    private static void GeneratorIconOff()
    {
        var root = TempDir();
        var package = Generate(root, new AppSettings.PackageLayoutConfig { IconFolder = false });
        IsTrue(Directory.Exists(Path.Combine(package, "Application")), "Application exists");
        IsTrue(Directory.Exists(Path.Combine(package, "Intune")), "Intune exists");
        IsFalse(Directory.Exists(Path.Combine(package, "Icon")), "Icon absent");
    }

    private static void GeneratorExtraFolders()
    {
        var root = TempDir();
        var package = Generate(root, new AppSettings.PackageLayoutConfig
        {
            IconFolder = false,
            ExtraFolders = ["Docs", "Tools"],
        });
        foreach (var folder in new[] { "Docs", "Tools" })
        {
            var path = Path.Combine(package, folder);
            IsTrue(Directory.Exists(path), $"{folder} exists");
            Empty(Directory.GetFileSystemEntries(path));
        }
    }

    private static void UpgradeCopiesOptionalFolders()
    {
        var root = TempDir();
        var layout = new AppSettings.PackageLayoutConfig { IconFolder = true, ExtraFolders = ["Docs"] };
        var v1 = Generate(root, layout);
        File.WriteAllText(Path.Combine(v1, "Icon", "icon.png"), "ICON-V1");
        File.WriteAllText(Path.Combine(v1, "Docs", "notes.txt"), "NOTES-V1");

        var source = Path.Combine(TempDir(), "setup.exe");
        File.WriteAllText(source, "installer-v2");
        var upgrade = new PackageUpgradeService(root, layout);
        var result = upgrade.UpgradePackageAsync(v1, Version2, source);
        Wait(result);

        var v2 = VersionFolder(root, Version2);
        Equal("ICON-V1", File.ReadAllText(Path.Combine(v2, "Icon", "icon.png")));
        Equal("NOTES-V1", File.ReadAllText(Path.Combine(v2, "Docs", "notes.txt")));
        IsTrue(Directory.Exists(Path.Combine(v2, "Intune")), "Intune created for the new version");
    }

    // Onboarding flow

    private static void ConnectionGate()
    {
        var vm = NewViewModel(TempSettings());
        vm.CurrentStepIndex = 1;
        IsFalse(vm.NextCommand.CanExecute(null), "Next blocked before a test or Connect later");
        vm.ConnectLaterCommand.Execute(null);
        IsTrue(vm.NextCommand.CanExecute(null), "Next allowed after Connect later");
    }

    private static void ConnectionPassAndEdit()
    {
        var vm = NewViewModel(TempSettings());
        vm.IsInteractive = false;
        vm.TenantId = AllZeroTenant;
        vm.ClientId = FakeClientId;
        vm.Thumbprint = FakeThumbprint;
        vm.CurrentStepIndex = 1;
        Wait(vm.TestConnectionAsync());
        IsTrue(vm.ConnectionOk, "test connection passes against the fake Graph");
        IsTrue(vm.NextCommand.CanExecute(null), "Next allowed after a passing test");

        vm.TenantId = "00000000-0000-0000-0000-000000000002";
        IsFalse(vm.ConnectionOk, "editing the tenant clears the pass");
        IsFalse(vm.NextCommand.CanExecute(null), "Next blocked again after the edit");
    }

    private static void ConnectionTestsChosenMethod()
    {
        var vm = NewViewModel(TempSettings());
        var calls = new List<(AuthMode Mode, string Tenant)>();
        vm.SignIn = (mode, cfg, _) =>
        {
            calls.Add((mode, cfg.TenantId));
            AppServices.Auth.UseTestSession(AllZeroTenant, FakeToken());
            return Task.CompletedTask;
        };
        vm.CurrentStepIndex = 1;

        vm.IsInteractive = false;
        vm.TenantId = AllZeroTenant;
        vm.ClientId = FakeClientId;
        vm.Thumbprint = FakeThumbprint;
        Wait(vm.TestConnectionAsync());
        IsTrue(vm.ConnectionOk, "app registration test passes");

        // A session from the app registration exists now; Interactive must still sign in itself.
        vm.IsInteractive = true;
        IsFalse(vm.ConnectionOk, "switching the method clears the pass");
        Wait(vm.TestConnectionAsync());
        Equal(2, calls.Count);
        Equal(AuthMode.Interactive, calls[1].Mode);

        // A new interactive tenant is signed in again with that tenant.
        vm.TenantId = "00000000-0000-0000-0000-000000000002";
        Wait(vm.TestConnectionAsync());
        Equal(3, calls.Count);
        Equal("00000000-0000-0000-0000-000000000002", calls[2].Tenant);
    }

    private static void ConnectionEditDuringTest()
    {
        var vm = NewViewModel(TempSettings());
        vm.SignIn = (_, _, _) =>
        {
            AppServices.Auth.UseTestSession(AllZeroTenant, FakeToken());
            vm.TenantId = "00000000-0000-0000-0000-000000000002";
            return Task.CompletedTask;
        };
        vm.CurrentStepIndex = 1;
        vm.IsInteractive = false;
        vm.TenantId = AllZeroTenant;
        vm.ClientId = FakeClientId;
        vm.Thumbprint = FakeThumbprint;

        Wait(vm.TestConnectionAsync());
        IsFalse(vm.ConnectionOk, "the pass belongs to the old tenant");
        IsFalse(vm.NextCommand.CanExecute(null), "Next stays blocked");
        Equal(0, vm.ConnectionChecks.Count);
    }

    private static void PathEditDuringCheck()
    {
        var vm = NewViewModel(TempSettings());
        vm.CurrentStepIndex = 2;
        vm.ApplicationsRoot = TempDir();
        vm.TemplatePath = FakeTemplate(TempDir());
        vm.IntuneWinAppUtilPath = FakeTool(TempDir());

        // Run inside a dispatcher operation so the check's continuation can only run after the edit.
        Wait(Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            var next = vm.NextAsync();
            vm.TemplatePath = Path.Combine(TempDir(), "missing");
            await next;
        }).Task.Unwrap());

        Equal(2, vm.CurrentStepIndex);
        IsTrue(vm.TemplateCheck == null, "no stale result is shown for the edited path");
    }

    private static void BackKeepsValues()
    {
        var root = TempDir();
        var template = FakeTemplate(TempDir());
        var tool = FakeTool(TempDir());
        var vm = NewViewModel(TempSettings());

        vm.CurrentStepIndex = 1;
        vm.TenantId = AllZeroTenant;
        vm.ClientId = FakeClientId;
        vm.ConnectLaterCommand.Execute(null);
        Wait(vm.NextAsync());

        vm.ApplicationsRoot = root;
        vm.TemplatePath = template;
        vm.IntuneWinAppUtilPath = tool;
        Wait(vm.NextAsync());
        Equal(3, vm.CurrentStepIndex);

        vm.Layout.IconFolder = false;
        vm.Layout.NewFolderName = "Docs";
        vm.Layout.AddFolderCommand.Execute(null);

        vm.BackCommand.Execute(null);
        Equal(2, vm.CurrentStepIndex);
        Equal(root, vm.ApplicationsRoot);
        Equal(template, vm.TemplatePath);
        Equal(tool, vm.IntuneWinAppUtilPath);

        vm.BackCommand.Execute(null);
        Equal(1, vm.CurrentStepIndex);
        Equal(AllZeroTenant, vm.TenantId);
        Equal(FakeClientId, vm.ClientId);
        IsTrue(vm.NextCommand.CanExecute(null), "Connect later still set on Back");

        Wait(vm.NextAsync());
        Wait(vm.NextAsync());
        Equal(3, vm.CurrentStepIndex);
        IsFalse(vm.Layout.IconFolder, "Icon choice kept");
        SequenceEqual(new[] { "Docs" }, vm.Layout.ExtraFolders);
    }

    private static void UneditedRerunIdentical()
    {
        var settings = TempSettings();
        var root = TempDir();
        Seed(settings.Service, root, FakeTemplate(TempDir()), FakeTool(TempDir()));
        var before = File.ReadAllBytes(settings.Path);

        var vm = NewViewModel(new SettingsService(settings.Path));
        Walk(vm);

        var after = File.ReadAllBytes(settings.Path);
        IsTrue(before.AsSpan().SequenceEqual(after), "settings file byte-identical after an unedited rerun");
    }

    private static void FinishTouchesOwnedBlocks()
    {
        var settings = TempSettings();
        var root = TempDir();
        Seed(settings.Service, root, FakeTemplate(TempDir()), FakeTool(TempDir()));
        var s = settings.Service.Settings;
        s.Theme = AppTheme.Light;
        s.NavHidden = true;
        s.CodeSigning.Enabled = true;
        s.GroupAssignment.CreateGroupPerPackage = true;
        settings.Service.Save();
        var before = ReadNode(settings.Path);

        var vm = NewViewModel(new SettingsService(settings.Path));
        var newRoot = TempDir();
        vm.CurrentStepIndex = 1;
        vm.ConnectLaterCommand.Execute(null);
        Wait(vm.NextAsync());
        vm.ApplicationsRoot = newRoot;
        Wait(vm.NextAsync());
        vm.Layout.IconFolder = false;
        Wait(vm.NextAsync());
        Wait(vm.NextAsync());

        var after = ReadNode(settings.Path);
        foreach (var block in UntouchedBlocks)
            IsTrue(JsonNode.DeepEquals(before[block], after[block]), $"{block} unchanged");
        Equal(newRoot, after["NetworkPaths"]!["IntuneApplications"]!.GetValue<string>());
        IsFalse(after["PackageLayout"]!["IconFolder"]!.GetValue<bool>(), "PackageLayout saved");
    }

    private static void SettingsLayoutAppliesToNewPackages()
    {
        var root = TempDir();
        var settings = TempSettings();
        Seed(settings.Service, root, FakeTemplate(TempDir()), FakeTool(TempDir()));
        var template = settings.Service.Settings.NetworkPaths.PSADTTemplate;

        var first = Generate(root, settings.Service.Settings.PackageLayout, "First", template);
        IsTrue(Directory.Exists(Path.Combine(first, "Docs")), "first package has Docs");

        var vm = new SettingsViewModel(settings.Service, AppServices.Auth);
        vm.Layout.ExtraFolders.Remove("Docs");
        vm.Layout.NewFolderName = "Tools";
        vm.Layout.AddFolderCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        var saved = new SettingsService(settings.Path).Settings.PackageLayout;
        var second = Generate(root, saved, "Second", template);
        IsTrue(Directory.Exists(Path.Combine(second, "Tools")), "second package has Tools");
        IsFalse(Directory.Exists(Path.Combine(second, "Docs")), "second package has no Docs");
        IsTrue(Directory.Exists(Path.Combine(first, "Docs")), "first package keeps Docs");
        IsFalse(Directory.Exists(Path.Combine(first, "Tools")), "first package gets no Tools");
    }

    private static void InputsAreNamed()
    {
        var settings = TempSettings();
        var vm = NewViewModel(settings);
        var window = new OnboardingWindow(vm);
        window.Show();
        try
        {
            foreach (var interactive in new[] { true, false })
            {
                vm.IsInteractive = interactive;
                for (var step = 0; step < vm.Steps.Count; step++)
                {
                    vm.CurrentStepIndex = step;
                    window.UpdateLayout();
                    foreach (var input in VisibleInputs(window))
                    {
                        var name = AutomationProperties.GetName(input);
                        IsFalse(string.IsNullOrWhiteSpace(name),
                            $"{input.GetType().Name} on step {step} ({(interactive ? "Interactive" : "App registration")}) has no AutomationProperties.Name");
                    }
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // Helpers: view models, settings, package fixtures

    // The sign-in is the test session, so a test never needs a certificate or a prompt.
    private static OnboardingViewModel NewViewModel(TempSettingsFile settings)
        => new(settings.Service, AppServices.Auth)
        {
            SignIn = (_, _, _) =>
            {
                AppServices.Auth.UseTestSession(AllZeroTenant, FakeToken());
                return Task.CompletedTask;
            },
        };

    private static TempSettingsFile TempSettings()
    {
        var path = Path.Combine(TempDir(), "appsettings.json");
        return new TempSettingsFile(path, new SettingsService(path));
    }

    private sealed record TempSettingsFile(string Path, SettingsService Service);

    private static void Seed(SettingsService svc, string root, string template, string tool)
    {
        var s = svc.Settings;
        s.AuthMode = AuthMode.AppRegistration;
        s.Authentication.TenantId = AllZeroTenant;
        s.Authentication.ClientId = FakeClientId;
        s.Authentication.CertificateThumbprint = FakeThumbprint;
        s.NetworkPaths.IntuneApplications = root;
        s.NetworkPaths.PSADTTemplate = template;
        s.NetworkPaths.IntuneWinAppUtil = tool;
        s.PackageLayout = new AppSettings.PackageLayoutConfig { IconFolder = true, ExtraFolders = ["Docs"] };
        svc.Save();
    }

    // Walks a fresh onboarding run: Connect later, the paths, the layout, then Finish.
    private static void Walk(OnboardingViewModel vm)
    {
        vm.CurrentStepIndex = 1;
        vm.ConnectLaterCommand.Execute(null);
        Wait(vm.NextAsync());
        Wait(vm.NextAsync());
        Wait(vm.NextAsync());
        Wait(vm.NextAsync());
    }

    private static string Generate(string root, AppSettings.PackageLayoutConfig layout, string name = "Reader", string? template = null)
    {
        var info = NewApplicationInfo(name);
        template ??= FakeTemplate(TempDir());
        var generator = new PSADTGenerator(root, template, layout);
        Wait(generator.CreatePackageAsync(info));
        return VersionFolder(root, info.Version, name);
    }

    private static ApplicationInfo NewApplicationInfo(string name)
    {
        var source = Path.Combine(TempDir(), "setup.exe");
        File.WriteAllText(source, "installer-v1");
        return new ApplicationInfo
        {
            Manufacturer = "Contoso",
            Name = name,
            Version = Version1,
            SourcesPath = source,
        };
    }

    private static string VersionFolder(string root, string version, string name = "Reader")
        => PackagePaths.VersionFolder(root, PackagePaths.AppFolderName("Contoso", name), version);

    private static string FakeTemplate(string dir)
    {
        // Just the $adtSession block the generator and the upgrade fill in.
        File.WriteAllText(Path.Combine(dir, "Invoke-AppDeployToolkit.ps1"),
            "$adtSession = @{\n    AppVendor = ''\n    AppName = ''\n    AppVersion = ''\n    AppArch = ''\n" +
            "    AppScriptDate = ''\n    AppScriptAuthor = ''\n    RequireAdmin = $true\n}\n");
        Directory.CreateDirectory(Path.Combine(dir, "PSAppDeployToolkit"));
        return dir;
    }

    private static string FakeTool(string dir)
    {
        var path = Path.Combine(dir, "IntuneWinAppUtil.exe");
        File.WriteAllText(path, "fake tool");
        return path;
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("packman-setup-").FullName;

    private static JsonNode ReadNode(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static IEnumerable<FrameworkElement> VisibleInputs(DependencyObject root)
    {
        foreach (var element in Descendants(root))
        {
            if (element is TextBox or ComboBox or CheckBox or RadioButton or ListBox && element is UIElement { IsVisible: true })
                yield return (FrameworkElement)element;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    // The onboarding awaits background checks that post back to the dispatcher. Pumping it
    // while waiting avoids the deadlock a plain GetResult would cause on the UI thread.
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        _ = task.Result;
    }

    // A fake JWT: the test session only reads its claims. The signature is never checked.
    private static string FakeToken()
    {
        static string Segment(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Segment(new { alg = "none", typ = "JWT" })}.{Segment(new { tid = AllZeroTenant, scp = Permission })}.";
    }

    private sealed class FakeGraph : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = new StringContent("{\"value\":[]}", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected '{expected}', got '{actual}'");
    }

    private static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new Exception($"expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    private static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void IsFalse(bool value, string message)
    {
        if (value) throw new Exception(message);
    }

    private static void Empty(IReadOnlyCollection<string> items)
    {
        if (items.Count != 0) throw new Exception($"expected empty folder, found {items.Count} entries");
    }
}
