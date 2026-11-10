using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Packman.Models;
using Packman.Services;
using Packman.ViewModels;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// Detection methods end to end: the rule each picker builds, the exact win32LobApp
/// detectionRules JSON Graph receives, every validation message, and a rule read back
/// from Graph into the detail-page editor and out again unchanged.
///
/// The expected payloads follow the Graph beta win32LobApp schema:
/// win32LobAppProductCodeDetection (productCode, productVersion, productVersionOperator),
/// win32LobAppFileSystemDetection (path, fileOrFolderName, check32BitOn64System,
/// detectionType, operator, detectionValue), win32LobAppRegistryDetection (keyPath,
/// valueName, check32BitOn64System, detectionType, operator, detectionValue) and
/// win32LobAppPowerShellScriptDetection (scriptContent, enforceSignatureCheck, runAs32Bit).
/// </summary>
public sealed class DetectionRuleTests : GraphStaticsFixture
{
    private const string AppId = "app-1";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ProductCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";

    // win32LobAppDetectionOperator, per the Graph beta schema.
    private static readonly string[] GraphOperators =
        ["notConfigured", "equal", "notEqual", "greaterThanOrEqual", "greaterThan", "lessThanOrEqual", "lessThan"];
    // win32LobAppFileSystemDetectionType / win32LobAppRegistryDetectionType.
    private static readonly string[] GraphFileDetectionTypes =
        ["notConfigured", "exists", "modifiedDate", "createdDate", "version", "sizeInMB", "doesNotExist"];
    private static readonly string[] GraphRegistryDetectionTypes =
        ["notConfigured", "exists", "doesNotExist", "string", "integer", "version"];

    private readonly string _root = Directory.CreateTempSubdirectory("packman-tests-detection-").FullName;

    public override void Dispose()
    {
        base.Dispose();
        Directory.Delete(_root, recursive: true);
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packman", "Logs", "Upload");
        if (Directory.Exists(logs))
            foreach (var log in Directory.GetFiles(logs, "PackmanDetectionTests-*.log"))
                File.Delete(log);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Serialize(DetectionRule rule) => DetectionRuleGraph.Serialize(rule);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>Asserts the rule serialises to exactly these properties, in this order.</summary>
    private static void AssertPayload(DetectionRule rule, params (string Key, object? Value)[] expected)
    {
        var actual = Serialize(rule);
        Assert.Equal(expected.Select(e => e.Key), actual.Keys);
        foreach (var (key, value) in expected)
            Assert.Equal(value, actual[key]);
    }

    /// <summary>A win32LobApp document carrying these detectionRules, as Graph would return it.</summary>
    private static JsonElement GraphApp(params Dictionary<string, object?>[] rules) =>
        JsonSerializer.Deserialize<JsonElement>(Json(new Dictionary<string, object?> { ["detectionRules"] = rules }));

    private static List<DetectionRule> ParseBack(params DetectionRule[] rules) =>
        DetectionRuleGraph.Parse(GraphApp(rules.Select(Serialize).ToArray()));

    /// <summary>Graph ▸ Packman ▸ Graph for one rule; the JSON must come back identical.</summary>
    private static void AssertRoundTrips(DetectionRule rule)
    {
        var sent = Serialize(rule);
        var readBack = Assert.Single(DetectionRuleGraph.Parse(GraphApp(sent)));
        Assert.Equal(Json(sent), Json(Serialize(readBack)));
    }

    /// <summary>Graph ▸ Packman ▸ detail-page editor (opened and saved untouched) ▸ Graph.</summary>
    private static void AssertRoundTripsThroughTheEditor(DetectionRule rule)
    {
        var sent = Serialize(rule);
        var readBack = Assert.Single(DetectionRuleGraph.Parse(GraphApp(sent)));
        var display = DetectionRuleDisplay.From(readBack);
        display.BeginEdit();
        Assert.Null(display.EditProblem);
        display.ApplyEdit();
        Assert.Equal(Json(sent), Json(Serialize(display.Rule)));
    }

    /// <summary>Records every request body, then lets the inner handler answer.</summary>
    private sealed class BodySpy : DelegatingHandler
    {
        public List<(string Path, string Body)> Bodies { get; } = [];

        public BodySpy(HttpMessageHandler inner) => InnerHandler = inner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                lock (Bodies) Bodies.Add((request.RequestUri!.AbsolutePath, body));
            }
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class NoContent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Fakes.Json(HttpStatusCode.NoContent, ""));
    }

    /// <summary>The real PATCH body the detail page sends for these rules.</summary>
    private async Task<string> PatchBody(params DetectionRule[] rules)
    {
        var spy = new BodySpy(new NoContent());
        UseHandler(spy);
        await new IntuneService(() => Task.FromResult("test-token")).UpdateDetectionRulesAsync(AppId, rules);
        return Assert.Single(spy.Bodies).Body;
    }

    private static JsonElement Rules(string patchBody) =>
        JsonSerializer.Deserialize<JsonElement>(patchBody).GetProperty("detectionRules");

    // ── MSI product code ────────────────────────────────────────────────

    [Fact]
    public void MsiProductCodeSerialisesToProductCodeDetection()
    {
        AssertPayload(DetectionRuleFactory.Msi(ProductCode),
            ("@odata.type", "#microsoft.graph.win32LobAppProductCodeDetection"),
            ("productCode", ProductCode),
            ("productVersion", null),
            ("productVersionOperator", "notConfigured"));
    }

    [Fact]
    public void MsiProductCodeIsTrimmedAndNormalisedThroughTheWholePayload()
    {
        // Intune's agent matches the code Windows Installer registered, so Packman sends it
        // in that one form: braced, upper case, and without the whitespace a paste carries.
        AssertPayload(DetectionRuleFactory.Msi($"  {ProductCode.ToLowerInvariant()}\t"),
            ("@odata.type", "#microsoft.graph.win32LobAppProductCodeDetection"),
            ("productCode", ProductCode),
            ("productVersion", null),
            ("productVersionOperator", "notConfigured"));
    }

    [Theory]
    // However the code was typed or pasted, one form reaches Graph.
    [InlineData("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]
    [InlineData("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")]
    [InlineData("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("  {aaaaaaaa-BBBB-cccc-DDDD-eeeeeeeeeeee}  ")]
    [InlineData("\t{AAAAAAAA-bbbb-CCCC-dddd-EEEEEEEEEEEE}\r\n")]
    public void MsiProductCodeIsNormalisedToAnUpperCaseBracedGuid(string code)
    {
        Assert.Equal(ProductCode, DetectionInput.NormalizeProductCode(code));
        Assert.Equal(ProductCode, Serialize(DetectionRuleFactory.Msi(code))["productCode"]);
    }

    [Fact]
    public void AProductCodeThatIsNotAGuidIsLeftTrimmedByTheFactoryForTheValidatorsToRefuse()
    {
        // The factory cannot report a problem, so it keeps what was typed; the three gates in
        // front of it (DetectionPickerTests) are what stop the rule ever reaching Intune.
        Assert.Null(DetectionInput.NormalizeProductCode("not-a-guid-at-all"));
        Assert.Equal("not-a-guid-at-all", Serialize(DetectionRuleFactory.Msi("  not-a-guid-at-all  "))["productCode"]);
    }

    [Theory]
    // Anything Intune's version comparison cannot read is refused rather than sent.
    [InlineData("1", true)]
    [InlineData("1.2", true)]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData(" 1.2 ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("v1.2", false)]
    [InlineData("1.0.0-beta", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1..2", false)]
    [InlineData("1.", false)]
    public void OnlyOneToFourNumericPartsCountAsAVersion(string value, bool isVersion)
    {
        Assert.Equal(isVersion, DetectionInput.IsVersion(value));
    }

    [Fact]
    public void TheInputCheckMessagesNameWhatTheFieldNeeds()
    {
        Assert.Equal("The MSI product code must be a GUID like {12345678-ABCD-1234-ABCD-1234567890AB}.", DetectionInput.ProductCodeProblem);
        Assert.Equal("The version must be one to four numbers separated by dots, like 1.2.3.4.", DetectionInput.VersionProblem);
        Assert.False(DetectionInput.IsVersion(null));
        Assert.Null(DetectionInput.NormalizeProductCode(null));
    }

    [Fact]
    public void BlankMsiProductCodeIsRefusedByTheFactory()
    {
        Assert.Throws<ArgumentException>(() => DetectionRuleFactory.Msi(""));
        Assert.Throws<ArgumentException>(() => DetectionRuleFactory.Msi("   "));
        Assert.Throws<ArgumentNullException>(() => DetectionRuleFactory.Msi(null!));
    }

    [Fact]
    public void MsiVersionComparisonSendsTheVersionAndOperator()
    {
        var rule = DetectionRuleFactory.Msi(ProductCode);
        rule.CheckVersion = true;
        rule.FileOrFolderName = " 4.2.1 ";
        rule.Operator = "greaterThanOrEqual";

        AssertPayload(rule,
            ("@odata.type", "#microsoft.graph.win32LobAppProductCodeDetection"),
            ("productCode", ProductCode),
            ("productVersion", "4.2.1"),
            ("productVersionOperator", "greaterThanOrEqual"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("notConfigured")]
    public void MsiVersionComparisonWithoutAnOperatorFallsBackToEqual(string op)
    {
        var rule = DetectionRuleFactory.Msi(ProductCode);
        rule.CheckVersion = true;
        rule.FileOrFolderName = "4.2.1";
        rule.Operator = op;

        Assert.Equal("equal", Serialize(rule)["productVersionOperator"]);
    }

    [Fact]
    public void MsiRuleNeverSends32BitOr64BitFields()
    {
        // win32LobAppProductCodeDetection has no check32BitOn64System in the Graph schema.
        var rule = DetectionRuleFactory.Msi(ProductCode);
        rule.Check32BitOn64System = true;

        Assert.DoesNotContain("check32BitOn64System", Serialize(rule).Keys);
    }

    // ── File system ─────────────────────────────────────────────────────

    [Fact]
    public void FileExistsSerialisesToFileSystemDetection()
    {
        AssertPayload(DetectionRuleFactory.FileExists(@"C:\Program Files\Contoso\App", "app.exe"),
            ("@odata.type", "#microsoft.graph.win32LobAppFileSystemDetection"),
            ("path", @"C:\Program Files\Contoso\App"),
            ("fileOrFolderName", "app.exe"),
            // The 32-bit view is off unless the package says the app is 32-bit.
            ("check32BitOn64System", false),
            ("detectionType", "exists"),
            ("operator", "notConfigured"),
            ("detectionValue", null));
    }

    [Fact]
    public void FileVersionSerialisesWithTheDefaultOperator()
    {
        AssertPayload(DetectionRuleFactory.FileVersion(@"C:\Program Files\Contoso", "app.exe", "4.2.1.0"),
            ("@odata.type", "#microsoft.graph.win32LobAppFileSystemDetection"),
            ("path", @"C:\Program Files\Contoso"),
            ("fileOrFolderName", "app.exe"),
            ("check32BitOn64System", false),
            ("detectionType", "version"),
            ("operator", "greaterThanOrEqual"),
            ("detectionValue", "4.2.1.0"));
        Assert.Equal("greaterThanOrEqual", DetectionRuleFactory.DefaultVersionOperator);
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("notEqual")]
    [InlineData("greaterThan")]
    [InlineData("greaterThanOrEqual")]
    [InlineData("lessThan")]
    [InlineData("lessThanOrEqual")]
    public void EveryGraphOperatorSurvivesSerialisation(string op)
    {
        Assert.Contains(op, GraphOperators);
        var rule = DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "1.0", op);

        Assert.Equal(op, Serialize(rule)["operator"]);
    }

    [Theory]
    // Intune compares a.b.c.d; Packman passes whatever was typed. Two- and three-part
    // versions are padded by the agent, and a non-numeric version can never match.
    [InlineData("1.2")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.2")]
    [InlineData("1.0.0-beta")]
    [InlineData("12.0 build 3")]
    public void TheVersionStringIsSentVerbatim(string version)
    {
        var rule = DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", version);

        Assert.Equal(version, Serialize(rule)["detectionValue"]);
        Assert.Equal("version", Serialize(rule)["detectionType"]);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("doesNotExist")]
    public void FileExistenceTypesSendNoOperatorAndNoValue(string detectionType)
    {
        Assert.Contains(detectionType, GraphFileDetectionTypes);
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\App",
            FileOrFolderName = "app.exe",
            DetectionType = detectionType,
            // Left over from an earlier choice in the editor: it must not be sent.
            Operator = "greaterThanOrEqual",
            DetectionValue = "4.2",
        };

        var payload = Serialize(rule);
        Assert.Equal(detectionType, payload["detectionType"]);
        Assert.Equal("notConfigured", payload["operator"]);
        Assert.Null(payload["detectionValue"]);
    }

    [Theory]
    [InlineData("sizeInMB", "250")]
    [InlineData("modifiedDate", "2026-01-31T00:00:00Z")]
    [InlineData("createdDate", "2026-01-31T00:00:00Z")]
    public void FileComparisonTypesSendTheOperatorAndValue(string detectionType, string value)
    {
        Assert.Contains(detectionType, GraphFileDetectionTypes);
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\App",
            FileOrFolderName = "app.exe",
            DetectionType = detectionType,
            Operator = "greaterThanOrEqual",
            DetectionValue = value,
        };

        var payload = Serialize(rule);
        Assert.Equal(detectionType, payload["detectionType"]);
        Assert.Equal("greaterThanOrEqual", payload["operator"]);
        Assert.Equal(value, payload["detectionValue"]);
    }

    [Fact]
    public void CheckVersionWithoutAnExplicitTypeStillSendsAVersionRule()
    {
        // The pre-v4 shape: rules built with CheckVersion and the default "exists" type.
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\App",
            FileOrFolderName = "app.exe",
            CheckVersion = true,
            DetectionValue = "4.2",
            Operator = "equal",
        };

        Assert.Equal("version", Serialize(rule)["detectionType"]);
        Assert.Equal("4.2", Serialize(rule)["detectionValue"]);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Contoso\")]
    [InlineData(@"%ProgramFiles%\Contoso")]
    [InlineData(@"%LOCALAPPDATA%\Programs\Contoso")]
    [InlineData(@"C:\Program Files (x86)\O'Brien & Co")]
    public void ThePathIsSentVerbatimIncludingTrailingSlashesAndVariables(string path)
    {
        // Packman does not normalise the path; Intune expands the environment variables and
        // tolerates the trailing separator.
        Assert.Equal(path, Serialize(DetectionRuleFactory.FileExists(path, "app.exe"))["path"]);
    }

    [Fact]
    public void FilePathAndNameAreTrimmed()
    {
        var payload = Serialize(DetectionRuleFactory.FileExists("  C:\\App  ", "  app.exe  "));

        Assert.Equal(@"C:\App", payload["path"]);
        Assert.Equal("app.exe", payload["fileOrFolderName"]);
    }

    [Fact]
    public void AVeryLongPathAndNameAreNotTruncated()
    {
        var path = @"C:\" + new string('p', 2000);
        var name = new string('n', 500) + ".exe";

        var payload = Serialize(DetectionRuleFactory.FileExists(path, name));

        Assert.Equal(path, payload["path"]);
        Assert.Equal(name, payload["fileOrFolderName"]);
    }

    [Fact]
    public void AnEmptyFilePathAndNameAreStillSerialised()
    {
        // Nothing in the model refuses a blank rule; the view models are the only gate.
        var payload = Serialize(DetectionRuleFactory.FileExists("", ""));

        Assert.Equal("", payload["path"]);
        Assert.Equal("", payload["fileOrFolderName"]);
    }

    [Fact]
    public void NewRulesLeaveThe32BitViewOffUnlessTheAppIs32Bit()
    {
        // The flag is not on any screen: it follows the package's architecture, and the
        // Intune portal's own default (off) is what an unknown architecture gets.
        Assert.Equal(false, Serialize(DetectionRuleFactory.FileExists(@"C:\App", "app.exe"))["check32BitOn64System"]);
        Assert.Equal(false, Serialize(DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "1.0"))["check32BitOn64System"]);
        Assert.Equal(false, Serialize(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", ""))["check32BitOn64System"]);

        // A 32-bit app resolves %ProgramFiles%, System32 and HKLM\SOFTWARE in the 32-bit view,
        // for registry rules as much as for file rules.
        Assert.Equal(true, Serialize(DetectionRuleFactory.FileExists(@"C:\App", "app.exe", is32BitApp: true))["check32BitOn64System"]);
        Assert.Equal(true, Serialize(DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "1.0", is32BitApp: true))["check32BitOn64System"]);
        Assert.Equal(true, Serialize(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "", is32BitApp: true))["check32BitOn64System"]);
    }

    [Theory]
    [InlineData("x86", true)]
    [InlineData("X86", true)]
    [InlineData("x64", false)]
    [InlineData("", false)]
    public void FromMethodPassesThe32BitFlagToEveryRuleTypeThatCarriesIt(string architecture, bool expected)
    {
        var is32Bit = string.Equals(architecture, "x86", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, is32Bit);

        foreach (var method in new[] { DetectionMethod.FileExists, DetectionMethod.FileVersion, DetectionMethod.RegistryKey })
        {
            var rule = DetectionRuleFactory.FromMethod(method, @"C:\App", "app.exe", "1.0", null,
                RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "", ProductCode, is32BitApp: is32Bit);

            Assert.Equal(expected, Serialize(rule!)["check32BitOn64System"]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The32BitFlagIsSentAsABoolean(bool check32Bit)
    {
        var rule = DetectionRuleFactory.FileExists(@"C:\App", "app.exe");
        rule.Check32BitOn64System = check32Bit;

        Assert.Equal(check32Bit, Serialize(rule)["check32BitOn64System"]);
    }

    // ── Registry ────────────────────────────────────────────────────────

    [Fact]
    public void RegistryKeyExistsSerialisesToRegistryDetection()
    {
        AssertPayload(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso\App", ""),
            ("@odata.type", "#microsoft.graph.win32LobAppRegistryDetection"),
            ("keyPath", @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso\App"),
            // No value name means "the key itself exists"; Graph takes that as null.
            ("valueName", null),
            ("check32BitOn64System", false),
            ("detectionType", "exists"),
            ("operator", "notConfigured"),
            ("detectionValue", null));
    }

    [Fact]
    public void RegistryValueExistsSendsTheValueName()
    {
        var payload = Serialize(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "  DisplayVersion  "));

        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso", payload["keyPath"]);
        Assert.Equal("DisplayVersion", payload["valueName"]);
        Assert.Equal("exists", payload["detectionType"]);
    }

    [Fact]
    public void AWhitespaceValueNameIsTheKeyItself()
    {
        Assert.Null(Serialize(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "   "))["valueName"]);
    }

    [Theory]
    [InlineData("HKEY_LOCAL_MACHINE")]
    [InlineData("HKEY_CURRENT_USER")]
    [InlineData("HKEY_CLASSES_ROOT")]
    [InlineData("HKEY_USERS")]
    [InlineData("HKEY_CURRENT_CONFIG")]
    public void EveryOfferedHiveIsSentAsAFullRootName(string hive)
    {
        Assert.Contains(hive, RegistryHiveNames.All);

        Assert.Equal($@"{hive}\SOFTWARE\Contoso",
            Serialize(DetectionRuleFactory.RegistryKeyExists(hive, @"SOFTWARE\Contoso", ""))["keyPath"]);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Contoso", @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso")]
    [InlineData(@"\SOFTWARE\Contoso", @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso")]
    [InlineData(@"SOFTWARE\Contoso\", @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso")]
    [InlineData(@"  \SOFTWARE\Contoso\  ", @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso")]
    [InlineData("", "HKEY_LOCAL_MACHINE")]
    [InlineData(@"\", "HKEY_LOCAL_MACHINE")]
    [InlineData(@"SOFTWARE\Wow6432Node\Contoso", @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Contoso")]
    public void TheHiveAndSubKeyAreJoinedWithOneSeparator(string subKey, string expected)
    {
        Assert.Equal(expected, RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, subKey));
    }

    [Theory]
    // A key path pasted whole from regedit or PowerShell already names its hive; that hive is
    // used once and never prefixed a second time.
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso")]
    [InlineData(@"hkey_local_machine\SOFTWARE\Contoso")]
    [InlineData(@"HKLM\SOFTWARE\Contoso")]
    [InlineData(@"hklm\SOFTWARE\Contoso")]
    [InlineData(@"HKLM:\SOFTWARE\Contoso")]
    [InlineData(@"Computer\HKEY_LOCAL_MACHINE\SOFTWARE\Contoso")]
    [InlineData(@"Computer\HKLM:\SOFTWARE\Contoso")]
    [InlineData(@"  Computer\HKLM:\SOFTWARE\Contoso\  ")]
    public void AKeyPathThatAlreadyNamesTheHiveIsNotDoubledUp(string subKey)
    {
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, subKey));
    }

    [Theory]
    // The hive on its own, with nothing under it.
    [InlineData("HKLM")]
    [InlineData("HKLM:")]
    [InlineData(@"HKLM:\")]
    [InlineData("HKEY_LOCAL_MACHINE")]
    [InlineData(@"Computer\HKEY_LOCAL_MACHINE")]
    public void ASubKeyThatIsNothingButTheHiveIsJustTheHive(string subKey)
    {
        Assert.Equal("HKEY_LOCAL_MACHINE", RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, subKey));
    }

    [Fact]
    public void TheHiveNamedInTheSubKeyWinsOverTheDropdown()
    {
        // Pasting an HKCU path while the dropdown still says HKLM must not produce an HKLM rule.
        Assert.Equal(@"HKEY_CURRENT_USER\SOFTWARE\Contoso",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"HKEY_CURRENT_USER\SOFTWARE\Contoso"));
        Assert.Equal(@"HKEY_CURRENT_USER\SOFTWARE\Contoso",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"HKCU:\SOFTWARE\Contoso"));
        Assert.Equal(@"HKEY_CLASSES_ROOT\CLSID",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"HKCR\CLSID"));
        Assert.Equal(@"HKEY_USERS\.DEFAULT",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"HKU\.DEFAULT"));
        Assert.Equal(@"HKEY_CURRENT_CONFIG\System",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"HKCC\System"));

        // And the whole rule carries that hive, not the one the picker was left on.
        Assert.Equal(@"HKEY_CURRENT_USER\SOFTWARE\Contoso",
            Serialize(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"HKCU:\SOFTWARE\Contoso", ""))["keyPath"]);
    }

    [Fact]
    public void ASubKeyThatOnlyLooksLikeAHiveNameIsStillASubKey()
    {
        // "HKLMTools" is not a hive, and a hive name further down the path is left alone.
        Assert.Equal(@"HKEY_LOCAL_MACHINE\HKLMTools\Contoso",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"HKLMTools\Contoso"));
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\HKLM\Contoso",
            RegistryHiveNames.Combine(RegistryHiveNames.LocalMachine, @"SOFTWARE\HKLM\Contoso"));
    }

    [Fact]
    public void ABlankHiveOrKeyPathIsRefusedByTheFactory()
    {
        Assert.Throws<ArgumentException>(() => DetectionRuleFactory.RegistryKeyExists("", @"SOFTWARE\Contoso", ""));
        Assert.Throws<ArgumentException>(() => DetectionRuleFactory.RegistryKeyExists("   ", @"SOFTWARE\Contoso", ""));
        Assert.Throws<ArgumentException>(() => DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, "", ""));
        Assert.Throws<ArgumentNullException>(() => DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", null!));
    }

    [Theory]
    [InlineData("string", "4.2.1")]
    [InlineData("integer", "1")]
    [InlineData("version", "4.2.1.0")]
    public void RegistryComparisonTypesSendTheOperatorAndValue(string detectionType, string value)
    {
        Assert.Contains(detectionType, GraphRegistryDetectionTypes);
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.Registry,
            Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            FileOrFolderName = "DisplayVersion",
            DetectionType = detectionType,
            Operator = "equal",
            DetectionValue = value,
        };

        var payload = Serialize(rule);
        Assert.Equal(detectionType, payload["detectionType"]);
        Assert.Equal("equal", payload["operator"]);
        Assert.Equal(value, payload["detectionValue"]);
    }

    [Fact]
    public void RegistryDoesNotExistSendsNoOperatorAndNoValue()
    {
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.Registry,
            Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            DetectionType = "doesNotExist",
            Operator = "equal",
            DetectionValue = "leftover",
        };

        var payload = Serialize(rule);
        Assert.Equal("doesNotExist", payload["detectionType"]);
        Assert.Equal("notConfigured", payload["operator"]);
        Assert.Null(payload["detectionValue"]);
    }

    [Fact]
    public async Task QuotesAndBackslashesInARegistryValueAreEscapedNotMangled()
    {
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.Registry,
            Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            FileOrFolderName = "InstallPath",
            DetectionType = "string",
            Operator = "equal",
            DetectionValue = @"He said ""C:\Program Files\App""",
        };

        var rules = Rules(await PatchBody(rule));

        Assert.Equal(@"He said ""C:\Program Files\App""", rules[0].GetProperty("detectionValue").GetString());
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso", rules[0].GetProperty("keyPath").GetString());
    }

    // ── PowerShell script ───────────────────────────────────────────────

    [Fact]
    public void ScriptDetectionSerialisesToPowerShellScriptDetection()
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("exit 0\n"));
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.Script,
            ScriptContent = base64,
            EnforceSignatureCheck = true,
            RunAs32Bit = true,
        };

        AssertPayload(rule,
            ("@odata.type", "#microsoft.graph.win32LobAppPowerShellScriptDetection"),
            ("scriptContent", base64),
            ("enforceSignatureCheck", true),
            ("runAs32Bit", true));
    }

    [Fact]
    public void ScriptContentIsSentExactlyAsStoredWithNoEncodingStep()
    {
        // Graph expects base64. Packman stores and forwards whatever Graph gave it, so a
        // rule read from Intune round-trips, but plain PowerShell assigned to the model
        // would be sent unencoded. No screen creates a script rule today.
        var rule = new DetectionRule { Type = DetectionRuleType.Script, ScriptContent = "Write-Output 'hi'" };

        Assert.Equal("Write-Output 'hi'", Serialize(rule)["scriptContent"]);
    }

    [Fact]
    public void ScriptFlagsDefaultToFalse()
    {
        AssertPayload(new DetectionRule { Type = DetectionRuleType.Script, ScriptContent = "ZXhpdCAw" },
            ("@odata.type", "#microsoft.graph.win32LobAppPowerShellScriptDetection"),
            ("scriptContent", "ZXhpdCAw"),
            ("enforceSignatureCheck", false),
            ("runAs32Bit", false));
    }

    [Fact]
    public void AVeryLongScriptIsNotTruncated()
    {
        var long64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string('#', 60_000)));
        var rule = new DetectionRule { Type = DetectionRuleType.Script, ScriptContent = long64 };

        Assert.Equal(long64, Serialize(rule)["scriptContent"]);
    }

    [Fact]
    public void AnUnknownRuleTypeIsRefusedRatherThanSentEmpty()
    {
        var rule = new DetectionRule { Type = (DetectionRuleType)99 };

        var ex = Assert.Throws<NotSupportedException>(() => Serialize(rule));
        Assert.Contains("Unknown detection rule type", ex.Message);
    }

    // ── The JSON Graph actually receives ────────────────────────────────

    [Fact]
    public async Task ThePatchBodyIsAWin32LobAppWithTheWholeRulesArray()
    {
        var body = await PatchBody(
            DetectionRuleFactory.Msi(ProductCode),
            DetectionRuleFactory.FileVersion(@"C:\Program Files\Contoso", "app.exe", "4.2.1.0"));

        var payload = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("#microsoft.graph.win32LobApp", payload.GetProperty("@odata.type").GetString());
        var rules = payload.GetProperty("detectionRules");
        Assert.Equal(2, rules.GetArrayLength());
        Assert.Equal("#microsoft.graph.win32LobAppProductCodeDetection", rules[0].GetProperty("@odata.type").GetString());
        Assert.Equal("#microsoft.graph.win32LobAppFileSystemDetection", rules[1].GetProperty("@odata.type").GetString());
    }

    [Fact]
    public async Task GraphPropertyNamesSurviveTheCamelCaseSerialiser()
    {
        // GraphClient serialises bodies with a camel-case naming policy; dictionary keys
        // must pass through untouched or every rule would be rejected.
        var body = await PatchBody(DetectionRuleFactory.FileExists(@"C:\App", "app.exe"));

        Assert.Contains("\"@odata.type\"", body);
        Assert.Contains("\"check32BitOn64System\"", body);
        Assert.Contains("\"fileOrFolderName\"", body);
        Assert.Contains("\"detectionType\"", body);
        Assert.DoesNotContain("\"Check32BitOn64System\"", body);
        Assert.DoesNotContain("\"odataType\"", body);
    }

    [Fact]
    public async Task UnusedComparisonFieldsAreSentAsJsonNull()
    {
        var rules = Rules(await PatchBody(DetectionRuleFactory.FileExists(@"C:\App", "app.exe")));

        Assert.Equal(JsonValueKind.Null, rules[0].GetProperty("detectionValue").ValueKind);
        Assert.Equal("notConfigured", rules[0].GetProperty("operator").GetString());
    }

    [Fact]
    public async Task AnEmptyRuleListIsPatchedAsAnEmptyArray()
    {
        // BUG/RISK: the edit path has no "at least one rule" guard, unlike the upload path.
        // PATCHing [] leaves the app in Intune with no way to detect it.
        var rules = Rules(await PatchBody());

        Assert.Equal(JsonValueKind.Array, rules.ValueKind);
        Assert.Equal(0, rules.GetArrayLength());
    }

    [Fact]
    public async Task TheUploadRefusesToPublishWithoutADetectionRule()
    {
        var intune = new FakeIntune();
        var spy = new BodySpy(intune);
        UseHandler(spy);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(new List<DetectionRule>()));

        Assert.Contains("No usable detection rule", ex.Message);
        Assert.DoesNotContain(intune.Requests, r => r.Method == "POST" && r.Path.EndsWith("/mobileApps"));
    }

    [Fact]
    public async Task ANewUploadSendsTheSameRuleShapeAsAnEdit()
    {
        var spy = new BodySpy(new FakeIntune());
        UseHandler(spy);
        var rule = DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso\App", "DisplayVersion");

        await Upload([rule]);

        var created = JsonSerializer.Deserialize<JsonElement>(spy.Bodies.First(b => b.Path.EndsWith("/mobileApps")).Body);
        Assert.Equal("#microsoft.graph.win32LobApp", created.GetProperty("@odata.type").GetString());
        var fromUpload = created.GetProperty("detectionRules")[0];
        Assert.Equal("#microsoft.graph.win32LobAppRegistryDetection", fromUpload.GetProperty("@odata.type").GetString());
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso\App", fromUpload.GetProperty("keyPath").GetString());
        Assert.Equal("DisplayVersion", fromUpload.GetProperty("valueName").GetString());
        Assert.False(fromUpload.GetProperty("check32BitOn64System").GetBoolean());

        // Byte for byte what the detail page would PATCH for the same rule.
        Assert.Equal(Rules(await PatchBody(rule))[0].GetRawText(), fromUpload.GetRawText());
    }

    private Task<string> Upload(List<DetectionRule> rules)
    {
        var package = Fakes.Package(Path.Combine(_root, "Contoso_Detection", "1.0"));
        var appInfo = new ApplicationInfo
        {
            Manufacturer = "Contoso",
            Name = "PackmanDetectionTests",
            Version = "1.0",
            DisplayName = "PackmanDetectionTests",
        };
        var service = new IntuneUploadService(() => Task.FromResult("test-token"), Tenant, null,
            Fakes.IntuneWinAppUtil(Path.Combine(_root, ".tools")));
        return Task.Run(() => service.UploadWin32ApplicationAsync(appInfo, package, rules,
            "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.exe -DeploymentType Uninstall", "test", "system"));
    }

    // ── Reading rules back off a win32LobApp ────────────────────────────

    [Fact]
    public void EveryRuleTypeIsParsedBackOffTheApp()
    {
        var parsed = ParseBack(
            DetectionRuleFactory.Msi(ProductCode),
            // Built for a 32-bit app, so the flag is set and has to survive the round trip.
            DetectionRuleFactory.FileVersion(@"C:\Program Files\Contoso", "app.exe", "4.2.1.0", is32BitApp: true),
            DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "DisplayVersion"),
            new DetectionRule { Type = DetectionRuleType.Script, ScriptContent = "ZXhpdCAw", RunAs32Bit = true });

        Assert.Equal(
            [DetectionRuleType.MSI, DetectionRuleType.File, DetectionRuleType.Registry, DetectionRuleType.Script],
            parsed.Select(r => r.Type));
        Assert.Equal(ProductCode, parsed[0].Path);
        Assert.False(parsed[0].CheckVersion);
        Assert.Equal(@"C:\Program Files\Contoso", parsed[1].Path);
        Assert.True(parsed[1].CheckVersion);
        Assert.True(parsed[1].Check32BitOn64System);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso", parsed[2].Path);
        Assert.Equal("DisplayVersion", parsed[2].FileOrFolderName);
        Assert.False(parsed[2].Check32BitOn64System);
        Assert.Equal("ZXhpdCAw", parsed[3].ScriptContent);
        Assert.True(parsed[3].RunAs32Bit);
    }

    [Fact]
    public void AnMsiVersionRuleIsParsedAsAVersionCheck()
    {
        var rule = DetectionRuleFactory.Msi(ProductCode);
        rule.CheckVersion = true;
        rule.FileOrFolderName = "4.2.1";
        rule.Operator = "greaterThan";

        var parsed = Assert.Single(ParseBack(rule));

        Assert.True(parsed.CheckVersion);
        Assert.Equal("4.2.1", parsed.FileOrFolderName);
        Assert.Equal("greaterThan", parsed.Operator);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"detectionRules\":null}")]
    [InlineData("{\"detectionRules\":{}}")]
    [InlineData("{\"detectionRules\":[]}")]
    [InlineData("{\"detectionRules\":[{\"@odata.type\":\"#microsoft.graph.win32LobAppSomethingNew\"}]}")]
    [InlineData("{\"detectionRules\":[{}]}")]
    [InlineData("[]")]
    public void UnreadableDetectionRulesReadAsNoRules(string app)
    {
        Assert.Empty(DetectionRuleGraph.Parse(JsonSerializer.Deserialize<JsonElement>(app)));
    }

    [Fact]
    public void MissingPropertiesReadAsEmptyRatherThanThrowing()
    {
        var app = JsonSerializer.Deserialize<JsonElement>(
            "{\"detectionRules\":[{\"@odata.type\":\"#microsoft.graph.win32LobAppFileSystemDetection\"}]}");

        var parsed = Assert.Single(DetectionRuleGraph.Parse(app));

        Assert.Equal("", parsed.Path);
        Assert.Equal("", parsed.FileOrFolderName);
        Assert.Equal("", parsed.DetectionType);
        Assert.False(parsed.Check32BitOn64System);
        // A rule with no detectionType is sent back as an existence check.
        Assert.Equal("exists", Serialize(parsed)["detectionType"]);
    }

    [Fact]
    public void AnOperatorOnAnExistenceRuleIsDroppedOnTheWayBack()
    {
        // Some apps carry "operator": "equal" on an exists rule. Graph only accepts
        // notConfigured there, so the round trip has to clean it up.
        var app = JsonSerializer.Deserialize<JsonElement>("""
            {"detectionRules":[{"@odata.type":"#microsoft.graph.win32LobAppRegistryDetection",
            "keyPath":"HKEY_LOCAL_MACHINE\\SOFTWARE\\Contoso","valueName":null,
            "check32BitOn64System":false,"detectionType":"exists","operator":"equal","detectionValue":"1.0"}]}
            """);

        var parsed = Assert.Single(DetectionRuleGraph.Parse(app));
        var payload = Serialize(parsed);

        Assert.Equal("notConfigured", payload["operator"]);
        Assert.Null(payload["detectionValue"]);
    }

    [Fact]
    public void NotConfiguredFileDetectionSurvivesTheRoundTrip()
    {
        var app = JsonSerializer.Deserialize<JsonElement>("""
            {"detectionRules":[{"@odata.type":"#microsoft.graph.win32LobAppFileSystemDetection",
            "path":"C:\\App","fileOrFolderName":"app.exe","check32BitOn64System":true,
            "detectionType":"notConfigured","operator":"notConfigured","detectionValue":null}]}
            """);

        var parsed = Assert.Single(DetectionRuleGraph.Parse(app));

        Assert.Equal("notConfigured", Serialize(parsed)["detectionType"]);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("doesNotExist")]
    [InlineData("version")]
    [InlineData("sizeInMB")]
    [InlineData("modifiedDate")]
    [InlineData("createdDate")]
    public void EveryFileRuleRoundTripsUnchanged(string detectionType)
    {
        AssertRoundTrips(new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\Program Files\Contoso",
            FileOrFolderName = "app.exe",
            DetectionType = detectionType,
            CheckVersion = detectionType == "version",
            Operator = "greaterThanOrEqual",
            DetectionValue = "4.2.1.0",
            Check32BitOn64System = true,
        });
    }

    [Theory]
    [InlineData("exists", "")]
    [InlineData("exists", "DisplayVersion")]
    [InlineData("doesNotExist", "DisplayVersion")]
    [InlineData("string", "DisplayVersion")]
    [InlineData("integer", "Installed")]
    [InlineData("version", "DisplayVersion")]
    public void EveryRegistryRuleRoundTripsUnchanged(string detectionType, string valueName)
    {
        AssertRoundTrips(new DetectionRule
        {
            Type = DetectionRuleType.Registry,
            Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            FileOrFolderName = valueName,
            DetectionType = detectionType,
            Operator = "equal",
            DetectionValue = "4.2.1.0",
            Check32BitOn64System = true,
        });
    }

    [Fact]
    public void MsiAndScriptRulesRoundTripUnchanged()
    {
        AssertRoundTrips(DetectionRuleFactory.Msi(ProductCode));

        var withVersion = DetectionRuleFactory.Msi(ProductCode);
        withVersion.CheckVersion = true;
        withVersion.FileOrFolderName = "4.2.1";
        withVersion.Operator = "lessThanOrEqual";
        AssertRoundTrips(withVersion);

        AssertRoundTrips(new DetectionRule
        {
            Type = DetectionRuleType.Script,
            ScriptContent = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("exit 0")),
            EnforceSignatureCheck = true,
            RunAs32Bit = true,
        });
    }

    [Fact]
    public void TheRulesArrayKeepsItsOrder()
    {
        var parsed = ParseBack(
            DetectionRuleFactory.FileExists(@"C:\One", "one.exe"),
            DetectionRuleFactory.FileExists(@"C:\Two", "two.exe"),
            DetectionRuleFactory.FileExists(@"C:\Three", "three.exe"));

        Assert.Equal([@"C:\One", @"C:\Two", @"C:\Three"], parsed.Select(r => r.Path));
    }

    // ── The detail page's inline editor ─────────────────────────────────

    [Theory]
    [InlineData("exists")]
    [InlineData("doesNotExist")]
    [InlineData("version")]
    [InlineData("sizeInMB")]
    public void OpeningAndSavingAFileRuleUntouchedChangesNothing(string detectionType)
    {
        AssertRoundTripsThroughTheEditor(new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\Program Files\Contoso",
            FileOrFolderName = "app.exe",
            DetectionType = detectionType,
            CheckVersion = detectionType == "version",
            Operator = "greaterThanOrEqual",
            DetectionValue = "4.2.1.0",
            Check32BitOn64System = true,
        });
    }

    [Theory]
    [InlineData("exists", "")]
    [InlineData("exists", "DisplayVersion")]
    [InlineData("string", "DisplayVersion")]
    [InlineData("integer", "Installed")]
    [InlineData("version", "DisplayVersion")]
    public void OpeningAndSavingARegistryRuleUntouchedChangesNothing(string detectionType, string valueName)
    {
        AssertRoundTripsThroughTheEditor(new DetectionRule
        {
            Type = DetectionRuleType.Registry,
            Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            FileOrFolderName = valueName,
            DetectionType = detectionType,
            Operator = "equal",
            DetectionValue = "4.2.1.0",
            Check32BitOn64System = true,
        });
    }

    [Fact]
    public void OpeningAndSavingAnMsiRuleUntouchedChangesNothing()
    {
        AssertRoundTripsThroughTheEditor(DetectionRuleFactory.Msi(ProductCode));

        var withVersion = DetectionRuleFactory.Msi(ProductCode);
        withVersion.CheckVersion = true;
        withVersion.FileOrFolderName = "4.2.1";
        withVersion.Operator = "notEqual";
        AssertRoundTripsThroughTheEditor(withVersion);
    }

    [Fact]
    public void A32BitRegistryRuleStays32BitThroughAnEdit()
    {
        // The flag has no editor field; an edit must not silently clear it.
        var rule = new DetectionRule
        {
            Type = DetectionRuleType.Registry,
            Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
            DetectionType = "exists",
            Check32BitOn64System = true,
        };
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();
        display.EditName = "DisplayVersion";
        display.ApplyEdit();

        Assert.Equal(true, Serialize(rule)["check32BitOn64System"]);
        Assert.Equal("DisplayVersion", Serialize(rule)["valueName"]);
    }

    [Fact]
    public void AScriptRuleIsNotEditableAndSurvivesAStrayApplyEdit()
    {
        var rule = new DetectionRule { Type = DetectionRuleType.Script, ScriptContent = "ZXhpdCAw", RunAs32Bit = true };
        var display = DetectionRuleDisplay.From(rule);
        Assert.False(display.CanEdit);
        Assert.Equal("PS1", display.TypeTag);

        display.BeginEdit();
        display.ApplyEdit();

        Assert.Equal("ZXhpdCAw", Serialize(rule)["scriptContent"]);
        Assert.Equal(true, Serialize(rule)["runAs32Bit"]);
    }

    [Fact]
    public void ChangingAFileRuleToAVersionCheckRewritesTheWholePayload()
    {
        var rule = DetectionRuleFactory.FileExists(@"C:\Program Files\Contoso", "app.exe");
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();
        display.EditDetectionType = "version";
        Assert.True(display.EditNeedsValue);
        display.EditOperator = "greaterThanOrEqual";
        display.EditValue = " 4.2.1.0 ";
        display.ApplyEdit();

        AssertPayload(rule,
            ("@odata.type", "#microsoft.graph.win32LobAppFileSystemDetection"),
            ("path", @"C:\Program Files\Contoso"),
            ("fileOrFolderName", "app.exe"),
            // The rule was created for a 64-bit app and an edit must not change that.
            ("check32BitOn64System", false),
            ("detectionType", "version"),
            ("operator", "greaterThanOrEqual"),
            ("detectionValue", "4.2.1.0"));
        Assert.True(rule.CheckVersion);
    }

    [Fact]
    public void ChangingAVersionCheckBackToExistsDropsTheComparison()
    {
        var rule = DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "4.2.1.0");
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();
        display.EditDetectionType = "exists";
        Assert.False(display.EditNeedsValue);
        display.ApplyEdit();

        var payload = Serialize(rule);
        Assert.Equal("exists", payload["detectionType"]);
        Assert.Equal("notConfigured", payload["operator"]);
        Assert.Null(payload["detectionValue"]);
        Assert.False(rule.CheckVersion);
    }

    [Fact]
    public void TurningOnAnMsiVersionCheckSendsTheVersionField()
    {
        var rule = DetectionRuleFactory.Msi(ProductCode);
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();
        Assert.False(display.EditNeedsValue);
        display.EditCheckVersion = true;
        Assert.True(display.EditNeedsValue);
        display.EditOperator = "greaterThanOrEqual";
        display.EditValue = "4.2.1";
        display.ApplyEdit();

        AssertPayload(rule,
            ("@odata.type", "#microsoft.graph.win32LobAppProductCodeDetection"),
            ("productCode", ProductCode),
            ("productVersion", "4.2.1"),
            ("productVersionOperator", "greaterThanOrEqual"));
    }

    [Fact]
    public void EditingAnMsiRuleKeepsTheVersionOutOfTheNameField()
    {
        var rule = DetectionRuleFactory.Msi(ProductCode);
        rule.CheckVersion = true;
        rule.FileOrFolderName = "4.2.1";
        rule.Operator = "equal";
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();

        Assert.Equal(ProductCode, display.EditPath);
        Assert.Equal("", display.EditName);
        Assert.Equal("4.2.1", display.EditValue);
        Assert.True(display.EditCheckVersion);
    }

    [Fact]
    public void TheEditorNamesTheMissingFieldForEachRuleType()
    {
        var file = DetectionRuleDisplay.From(new DetectionRule { Type = DetectionRuleType.File, DetectionType = "exists" });
        file.BeginEdit();
        Assert.Equal("The rule needs a folder path.", file.EditProblem);
        file.EditPath = @"C:\App";
        Assert.Equal("The rule needs a file or folder name.", file.EditProblem);
        file.EditName = "app.exe";
        Assert.Null(file.EditProblem);
        file.EditDetectionType = "version";
        Assert.Equal("The rule needs a value to compare against.", file.EditProblem);
        file.EditValue = "4.2";
        Assert.Null(file.EditProblem);

        var registry = DetectionRuleDisplay.From(new DetectionRule { Type = DetectionRuleType.Registry, DetectionType = "exists" });
        registry.BeginEdit();
        Assert.Equal("The rule needs a registry key path.", registry.EditProblem);
        registry.EditPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso";
        // A registry rule needs no value name: that is the "key exists" check.
        Assert.Null(registry.EditProblem);
        registry.EditDetectionType = "string";
        Assert.Equal("The rule needs a value to compare against.", registry.EditProblem);

        var msi = DetectionRuleDisplay.From(new DetectionRule { Type = DetectionRuleType.MSI });
        msi.BeginEdit();
        Assert.Equal("The rule needs an MSI product code.", msi.EditProblem);
        msi.EditPath = ProductCode;
        Assert.Null(msi.EditProblem);
        msi.EditCheckVersion = true;
        Assert.Equal("The rule needs a value to compare against.", msi.EditProblem);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void WhitespaceOnlyEditorFieldsCountAsEmpty(string blank)
    {
        var display = DetectionRuleDisplay.From(DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "4.2"));
        display.BeginEdit();

        display.EditValue = blank;
        Assert.Equal("The rule needs a value to compare against.", display.EditProblem);

        display.EditValue = "4.2";
        display.EditName = blank;
        Assert.Equal("The rule needs a file or folder name.", display.EditProblem);

        display.EditName = "app.exe";
        display.EditPath = blank;
        Assert.Equal("The rule needs a folder path.", display.EditProblem);
    }

    [Fact]
    public void ARefusedSaveRestoresTheRuleAndKeepsTheEditorOpen()
    {
        var rule = DetectionRuleFactory.FileExists(@"C:\Program Files\Contoso", "app.exe");
        var before = Json(Serialize(rule));
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();
        display.EditPath = @"C:\Program Files\Other";
        display.EditDetectionType = "version";
        display.EditValue = "9.9";
        var snapshot = display.Snapshot();
        display.ApplyEdit();
        Assert.NotEqual(before, Json(Serialize(rule)));

        display.ReopenEdit(snapshot, wasNew: false);

        Assert.Equal(before, Json(Serialize(rule)));
        Assert.True(display.IsEditing);
        Assert.Equal(@"C:\Program Files\Other", display.EditPath);
        Assert.Equal("9.9", display.EditValue);
    }

    [Fact]
    public void TheEditorOnlyOffersDetectionTypesAndOperatorsGraphAccepts()
    {
        var file = DetectionRuleDisplay.From(new DetectionRule { Type = DetectionRuleType.File });
        var registry = DetectionRuleDisplay.From(new DetectionRule { Type = DetectionRuleType.Registry });

        Assert.All(file.DetectionTypeChoices, o => Assert.Contains(o.Value, GraphFileDetectionTypes));
        Assert.All(registry.DetectionTypeChoices, o => Assert.Contains(o.Value, GraphRegistryDetectionTypes));
        Assert.All(file.OperatorChoices, o => Assert.Contains(o.Value, GraphOperators));
        // The registry list must not offer the file-only comparisons.
        Assert.DoesNotContain("sizeInMB", registry.DetectionTypeChoices.Select(o => o.Value));
        Assert.DoesNotContain("string", file.DetectionTypeChoices.Select(o => o.Value));
    }

    [Fact]
    public void TheRowSummaryDescribesEachRuleType()
    {
        Assert.Equal($"{ProductCode} · product code present",
            DetectionRuleDisplay.From(DetectionRuleFactory.Msi(ProductCode)).Summary);
        Assert.Equal(@"C:\App\app.exe · version >= 4.2",
            DetectionRuleDisplay.From(DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "4.2")).Summary);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso · — · exists",
            DetectionRuleDisplay.From(DetectionRuleFactory.RegistryKeyExists(RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "")).Summary);
        Assert.Equal("PowerShell detection script",
            DetectionRuleDisplay.From(new DetectionRule { Type = DetectionRuleType.Script }).Summary);
    }

    // ── Discovery from a test machine ───────────────────────────────────

    [Fact]
    public void ADiscoveredPathUnderTheTestUsersProfileBecomesLocalAppData()
    {
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Contoso",
            DetectionDiscoveryService.ToLocalPath(@"\\qa-01\C$\Users\tester\AppData\Local\Programs\Contoso", "qa-01"));
        Assert.Equal("%LOCALAPPDATA%",
            DetectionDiscoveryService.ToLocalPath(@"\\qa-01\C$\Users\tester\AppData\Local", "qa-01"));
    }

    [Fact]
    public void ADiscoveredMachineWidePathBecomesALocalCDrivePath()
    {
        Assert.Equal(@"C:\Program Files\Contoso",
            DetectionDiscoveryService.ToLocalPath(@"\\qa-01\C$\Program Files\Contoso", "qa-01"));
        // The share is matched case-insensitively, as the UNC itself is.
        Assert.Equal(@"C:\Program Files\Contoso",
            DetectionDiscoveryService.ToLocalPath(@"\\QA-01\c$\Program Files\Contoso", "qa-01"));
        Assert.Equal(@"C:\Users\Public\Contoso",
            DetectionDiscoveryService.ToLocalPath(@"\\qa-01\C$\Users\Public\Contoso", "qa-01"));
    }

    [Fact]
    public void APathForAnotherComputerIsLeftAlone()
    {
        Assert.Equal(@"\\qa-02\C$\Program Files\Contoso",
            DetectionDiscoveryService.ToLocalPath(@"\\qa-02\C$\Program Files\Contoso", "qa-01"));
    }

    [Fact]
    public void AFileWithoutVersionInfoYieldsNoVersionRule()
    {
        var text = Path.Combine(_root, "not-a-binary.txt");
        File.WriteAllText(text, "hello");

        Assert.Null(DetectionDiscoveryService.NormalizedFileVersion(FileVersionInfo.GetVersionInfo(text)));
    }

    [Fact]
    public void ADiscoveredVersionIsNormalisedToFourParts()
    {
        var assembly = FileVersionInfo.GetVersionInfo(typeof(DetectionRuleTests).Assembly.Location);

        var version = DetectionDiscoveryService.NormalizedFileVersion(assembly);

        Assert.NotNull(version);
        Assert.Equal(4, version!.Split('.').Length);
        Assert.All(version.Split('.'), part => Assert.True(int.TryParse(part, out _), $"'{part}' is not numeric"));
        Assert.Equal($"{assembly.FileMajorPart}.{assembly.FileMinorPart}.{assembly.FileBuildPart}.{assembly.FilePrivatePart}", version);
    }

    [Fact]
    public async Task DiscoveryWithNoUsableSearchTermsSaysSo()
    {
        var result = await new DetectionDiscoveryService().DiscoverAsync("qa-01", "ab", "1.0");

        Assert.False(result.Success);
        Assert.Equal("Could not derive search terms from app name 'ab'", result.ErrorMessage);
        Assert.Empty(result.SuggestedRules);
    }

    [Fact]
    public async Task DiscoveryThatFindsNothingReportsTheComputerAndSuggestsNoRule()
    {
        // An unusable computer name keeps every candidate root non-existent, so no share is
        // ever touched.
        var result = await new DetectionDiscoveryService().DiscoverAsync("no|such|host", "Contoso App", "1.0");

        Assert.False(result.Success);
        Assert.Equal("No matching application files found on no|such|host", result.ErrorMessage);
        Assert.Empty(result.SuggestedRules);
        Assert.Contains(result.Messages, m => m.StartsWith("Search terms:"));
    }

    [Fact]
    public void ADiscoveredRuleIsAFileRuleIntuneCanEvaluate()
    {
        // The shape DetectionDiscoveryService produces, as the Remote Test tool hands it on.
        var exists = new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\Program Files\Contoso",
            FileOrFolderName = "app.exe",
            DetectionType = "exists",
            Check32BitOn64System = true,
        };
        var version = new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\Program Files\Contoso",
            FileOrFolderName = "app.exe",
            DetectionType = "version",
            DetectionValue = "4.2.1.0",
            Operator = "greaterThanOrEqual",
            CheckVersion = true,
            Check32BitOn64System = true,
        };

        Assert.Equal("exists", Serialize(exists)["detectionType"]);
        Assert.Equal("greaterThanOrEqual", Serialize(version)["operator"]);
        Assert.Equal(@"File: C:\Program Files\Contoso\app.exe (Version >= 4.2.1.0)", version.Title);
        AssertRoundTrips(exists);
        AssertRoundTrips(version);
    }
}

/// <summary>
/// The wizard's detection picker: its validation messages, what it pre-fills from the
/// package, and the rule it hands to the upload.
/// </summary>
public sealed class DetectionPickerTests : IDisposable
{
    private const string ProductCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";

    private readonly string _folder = Directory.CreateTempSubdirectory("packman-tests-detection-picker-").FullName;
    private readonly SettingsService _settings;
    private readonly IntuneAuthService _auth = new();
    private readonly CreatePackageViewModel _create = new();

    public DetectionPickerTests() => _settings = new SettingsService(Path.Combine(_folder, "appsettings.json"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>The Configure step over a generated package, so the summary line is live.</summary>
    private UploadStepViewModel Picker(string? version = "1.0", string? productCode = null)
    {
        _create.AppName = "Contoso App";
        _create.Manufacturer = "Contoso";
        _create.Version = version ?? "";
        _create.CurrentPackagePath = Fakes.Package(Path.Combine(_folder, "package"));
        if (productCode != null)
            _create.CurrentMsiInfo = new MsiInfoService.MsiInfo { ProductCode = productCode };
        return new UploadStepViewModel(_create, _settings, _auth);
    }

    // ── Validation messages ─────────────────────────────────────────────

    [Fact]
    public void FileExistsNeedsBothAPathAndAName()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.FileExists;

        Assert.Equal("Detection needs a path and a file or folder name.", vm.DescribeDetectionProblem());
        vm.DetectionPath = @"C:\Program Files\Contoso";
        Assert.Equal("Detection needs a path and a file or folder name.", vm.DescribeDetectionProblem());
        vm.DetectionName = "app.exe";
        Assert.Null(vm.DescribeDetectionProblem());

        vm.DetectionPath = "   ";
        Assert.Equal("Detection needs a path and a file or folder name.", vm.DescribeDetectionProblem());
    }

    [Fact]
    public void FileVersionNeedsAPathANameAndAVersion()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.FileVersion;
        vm.DetectionValue = "";

        Assert.Equal("Detection needs a path and a file name.", vm.DescribeDetectionProblem());
        vm.DetectionPath = @"C:\Program Files\Contoso";
        vm.DetectionName = "app.exe";
        Assert.Equal("Detection needs the version to compare against.", vm.DescribeDetectionProblem());
        vm.DetectionValue = "  ";
        Assert.Equal("Detection needs the version to compare against.", vm.DescribeDetectionProblem());
        vm.DetectionValue = "4.2.1.0";
        Assert.Null(vm.DescribeDetectionProblem());
    }

    [Fact]
    public void RegistryDetectionNeedsOnlyAKeyPath()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.RegistryKey;

        Assert.Equal("Detection needs a registry key path.", vm.DescribeDetectionProblem());
        vm.RegistryKeyPath = "   ";
        Assert.Equal("Detection needs a registry key path.", vm.DescribeDetectionProblem());
        vm.RegistryKeyPath = @"SOFTWARE\Contoso\App";
        Assert.Null(vm.DescribeDetectionProblem());
        Assert.Equal(@"Registry: HKEY_LOCAL_MACHINE\SOFTWARE\Contoso\App (Key exists)", vm.DetectionSummary);
    }

    [Fact]
    public void MsiDetectionNeedsAProductCode()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.MsiProductCode;

        Assert.Equal("Detection needs an MSI product code.", vm.DescribeDetectionProblem());
        Assert.True(vm.HasNoMsiProductCode);
        vm.DetectionProductCode = ProductCode;
        Assert.Null(vm.DescribeDetectionProblem());
        Assert.False(vm.HasNoMsiProductCode);
    }

    [Fact]
    public void TheRailReportsReadinessAndTheChosenMethod()
    {
        var vm = Picker();
        // The summary line is filled when the step is entered or a field changes, not on
        // construction.
        Assert.Equal("", vm.DetectionSummary);
        vm.SelectedDetectionMethod = DetectionMethod.RegistryKey;
        vm.SelectedDetectionMethod = DetectionMethod.FileExists;

        Assert.False(vm.DetectionReady);
        Assert.Equal("Incomplete", vm.DetectionState);
        Assert.Equal("Detection needs a path and a file or folder name.", vm.DetectionIssue);
        Assert.Equal(vm.DetectionIssue, vm.DetectionSummary);
        Assert.Equal(DetectionMethod.FileExists, vm.DetectionMethodLabel);

        vm.DetectionPath = @"C:\Program Files\Contoso";
        vm.DetectionName = "app.exe";

        Assert.True(vm.DetectionReady);
        Assert.Equal("Ready", vm.DetectionState);
        Assert.Equal("", vm.DetectionIssue);
        Assert.Equal(@"File: C:\Program Files\Contoso\app.exe (Exists)", vm.DetectionSummary);
    }

    [Fact]
    public void EveryMethodTheDropdownOffersCanBuildARule()
    {
        Assert.Equal(
            [DetectionMethod.FileExists, DetectionMethod.FileVersion, DetectionMethod.RegistryKey, DetectionMethod.MsiProductCode],
            new UploadStepViewModel(_create, _settings, _auth).DetectionMethods);

        foreach (var method in DetectionMethod.All)
            Assert.NotNull(DetectionRuleFactory.FromMethod(method, @"C:\App", "app.exe", "1.0", null,
                RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "", ProductCode));
    }

    [Fact]
    public void AnUnknownMethodBuildsNoRule()
    {
        Assert.Null(DetectionRuleFactory.FromMethod("Script", @"C:\App", "app.exe", "1.0", null,
            RegistryHiveNames.LocalMachine, @"SOFTWARE\Contoso", "", ProductCode));
    }

    [Fact]
    public void ABlankRegistryHiveIsReportedAsAProblemRatherThanThrown()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.RegistryKey;
        vm.RegistryKeyPath = @"SOFTWARE\Contoso";
        Assert.Null(vm.DescribeDetectionProblem());

        // The summary is rebuilt from the setter, so a rule it cannot build must be reported.
        vm.SelectedRegistryHive = "";

        Assert.Equal("Detection needs a registry hive.", vm.DescribeDetectionProblem());
        Assert.False(vm.DetectionReady);
        Assert.Equal("Detection needs a registry hive.", vm.DetectionIssue);
        Assert.Equal("Detection needs a registry hive.", vm.DetectionSummary);

        vm.SelectedRegistryHive = RegistryHiveNames.LocalMachine;
        Assert.True(vm.DetectionReady);
    }

    [Fact]
    public void AnInvalidVersionAndProductCodeAreRefused()
    {
        // A typo in either would publish an app Intune can never detect.
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.FileVersion;
        vm.DetectionPath = @"C:\App";
        vm.DetectionName = "app.exe";
        vm.DetectionValue = "version 4 beta";
        Assert.Same(DetectionInput.VersionProblem, vm.DescribeDetectionProblem());
        vm.DetectionValue = "4.2.1.0";
        Assert.Null(vm.DescribeDetectionProblem());

        vm.SelectedDetectionMethod = DetectionMethod.MsiProductCode;
        vm.DetectionProductCode = "oops";
        Assert.Same(DetectionInput.ProductCodeProblem, vm.DescribeDetectionProblem());
        vm.DetectionProductCode = ProductCode.ToLowerInvariant();
        Assert.Null(vm.DescribeDetectionProblem());
    }

    [Theory]
    // Everything Intune's version comparison cannot read, refused by all three gates in front
    // of the factory, so none of them can drift away from the others.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v1.2")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.")]
    public void AVersionIntuneCannotReadIsRefusedByEveryGate(string version)
    {
        Assert.False(DetectionInput.IsVersion(version));

        // 1. The wizard's Configure step.
        var wizard = Picker();
        wizard.SelectedDetectionMethod = DetectionMethod.FileVersion;
        wizard.DetectionPath = @"C:\App";
        wizard.DetectionName = "app.exe";
        wizard.DetectionValue = version;
        Assert.NotNull(wizard.DescribeDetectionProblem());
        Assert.False(wizard.DetectionReady);

        // 2. The standalone Upload page's Add rule button.
        var page = new UploadToIntuneViewModel
        {
            NewRuleMethod = DetectionMethod.FileVersion,
            NewRulePath = @"C:\App",
            NewRuleName = "app.exe",
            NewRuleValue = version,
        };
        Assert.False(page.CanAddRule);

        // 3. The detail page's inline editor, for a file rule and for an MSI version check.
        var file = DetectionRuleDisplay.From(DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "1.0"));
        file.BeginEdit();
        file.EditValue = version;
        Assert.NotNull(file.EditProblem);

        var msi = DetectionRuleDisplay.From(DetectionRuleFactory.Msi(ProductCode));
        msi.BeginEdit();
        msi.EditCheckVersion = true;
        msi.EditValue = version;
        Assert.NotNull(msi.EditProblem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("oops")]
    [InlineData("not-a-guid-at-all")]
    [InlineData("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEE}")]      // one digit short
    [InlineData("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEEE}")]    // one digit too many
    [InlineData("{GGGGGGGG-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]     // not hex
    public void AProductCodeThatIsNotAGuidIsRefusedByEveryGate(string code)
    {
        Assert.Null(DetectionInput.NormalizeProductCode(code));

        var wizard = Picker();
        wizard.SelectedDetectionMethod = DetectionMethod.MsiProductCode;
        wizard.DetectionProductCode = code;
        Assert.NotNull(wizard.DescribeDetectionProblem());
        Assert.False(wizard.DetectionReady);

        var page = new UploadToIntuneViewModel
        {
            NewRuleMethod = DetectionMethod.MsiProductCode,
            NewRuleProductCode = code,
        };
        Assert.False(page.CanAddRule);

        var msi = DetectionRuleDisplay.From(DetectionRuleFactory.Msi(ProductCode));
        msi.BeginEdit();
        msi.EditPath = code;
        Assert.NotNull(msi.EditProblem);
    }

    [Fact]
    public void AValidVersionAndProductCodePassEveryGate()
    {
        var wizard = Picker();
        wizard.SelectedDetectionMethod = DetectionMethod.FileVersion;
        wizard.DetectionPath = @"C:\App";
        wizard.DetectionName = "app.exe";
        wizard.DetectionValue = " 4.2 ";
        Assert.Null(wizard.DescribeDetectionProblem());

        var page = new UploadToIntuneViewModel
        {
            NewRuleMethod = DetectionMethod.FileVersion,
            NewRulePath = @"C:\App",
            NewRuleName = "app.exe",
            NewRuleValue = " 4.2 ",
        };
        Assert.True(page.CanAddRule);

        var file = DetectionRuleDisplay.From(DetectionRuleFactory.FileVersion(@"C:\App", "app.exe", "1.0"));
        file.BeginEdit();
        file.EditValue = " 4.2 ";
        Assert.Null(file.EditProblem);
    }

    [Fact]
    public void TheInlineEditorNormalisesAnMsiProductCodeOnSave()
    {
        // A code typed on the detail page has to reach Intune in the same form the upload
        // pickers send, or the same app would be detected one way and not the other.
        var rule = DetectionRuleFactory.Msi(ProductCode);
        var display = DetectionRuleDisplay.From(rule);

        display.BeginEdit();
        display.EditPath = "  aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee  ";
        Assert.Null(display.EditProblem);
        display.ApplyEdit();

        Assert.Equal(ProductCode, rule.Path);
    }

    // ── Pre-filling from the package ────────────────────────────────────

    [Fact]
    public void AnMsiPackagePreFillsTheProductCodeAndPicksMsiDetection()
    {
        var vm = Picker(productCode: ProductCode);

        vm.RefreshFromPackage();

        Assert.Equal(DetectionMethod.MsiProductCode, vm.SelectedDetectionMethod);
        Assert.Equal(ProductCode, vm.DetectionProductCode);
        Assert.False(vm.HasNoMsiProductCode);
        Assert.True(vm.DetectionReady);
        Assert.Equal($"MSI: {ProductCode}", vm.DetectionSummary);
    }

    [Fact]
    public void ANonMsiPackagePreFillsTheVersionAndStillNeedsAPath()
    {
        var vm = Picker(version: "4.2.1.0");

        vm.RefreshFromPackage();

        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
        Assert.Equal("4.2.1.0", vm.DetectionValue);
        Assert.Equal("", vm.DetectionPath);
        Assert.Equal("", vm.DetectionName);
        Assert.Equal("Detection needs a path and a file name.", vm.DetectionSummary);
        Assert.True(vm.HasNoMsiProductCode == false);
    }

    [Theory]
    // The pre-fill uses the version as typed on the Create step, not the 1.0.0 placeholder
    // the package metadata substitutes: a comparison against a number nobody chose would be
    // wrong, so anything Intune cannot compare pre-fills "File exists" instead.
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("2024 R2", "2024 R2")]
    [InlineData("v4.2", "v4.2")]
    public void APackageWithNoUsableVersionPreFillsFileExists(string? version, string expectedValue)
    {
        var vm = Picker(version: version);

        vm.RefreshFromPackage();

        Assert.Equal(DetectionMethod.FileExists, vm.SelectedDetectionMethod);
        Assert.Equal(expectedValue, vm.DetectionValue);
        Assert.Equal("Detection needs a path and a file or folder name.", vm.DetectionSummary);
    }

    [Theory]
    [InlineData("4.2.1.0")]
    [InlineData("4.2")]
    [InlineData("4")]
    public void APackageWithAComparableVersionPreFillsFileVersion(string version)
    {
        var vm = Picker(version: version);

        vm.RefreshFromPackage();

        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
        Assert.Equal(version, vm.DetectionValue);
    }

    [Fact]
    public void SwitchingToMsiDetectionReadsTheProductCodeOffTheStagedMsi()
    {
        var vm = Picker(productCode: ProductCode);
        vm.SelectedDetectionMethod = DetectionMethod.FileExists;
        Assert.Equal("", vm.DetectionProductCode);

        vm.SelectedDetectionMethod = DetectionMethod.MsiProductCode;

        Assert.Equal(ProductCode, vm.DetectionProductCode);
        Assert.False(vm.HasNoMsiProductCode);
    }

    [Fact]
    public void SwitchingBetweenMethodsKeepsEachMethodsFields()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.FileExists;
        vm.DetectionPath = @"C:\App";
        vm.DetectionName = "app.exe";

        vm.SelectedDetectionMethod = DetectionMethod.RegistryKey;
        vm.RegistryKeyPath = @"SOFTWARE\Contoso";
        vm.SelectedDetectionMethod = DetectionMethod.FileExists;

        Assert.Equal(@"C:\App", vm.DetectionPath);
        Assert.Equal("app.exe", vm.DetectionName);
        Assert.True(vm.DetectionReady);
    }

    [Fact]
    public void ReSeedingANewPackageDropsThePreviousProductCode()
    {
        var vm = Picker(productCode: ProductCode);
        vm.RefreshFromPackage();
        Assert.Equal(ProductCode, vm.DetectionProductCode);

        // An EXE package next: its detection must not inherit the MSI's product code.
        _create.CurrentMsiInfo = null;
        _create.Version = "9.9";
        _create.CurrentPackagePath = Fakes.Package(Path.Combine(_folder, "package-2"));
        vm.RefreshFromPackage();

        Assert.Equal("", vm.DetectionProductCode);
        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
        Assert.Equal("9.9", vm.DetectionValue);
    }

    [Fact]
    public void SteppingBackToConfigureKeepsTheEditedFields()
    {
        var vm = Picker();
        vm.RefreshFromPackage();
        vm.DetectionPath = @"C:\Program Files\Contoso";
        vm.DetectionName = "app.exe";

        vm.RefreshFromPackage();

        Assert.Equal(@"C:\Program Files\Contoso", vm.DetectionPath);
        Assert.Equal("app.exe", vm.DetectionName);
    }

    // ── Applying a rule discovered on a test machine ────────────────────

    [Fact]
    public void ADiscoveredVersionRuleFillsTheFileVersionFields()
    {
        var vm = Picker();

        vm.ApplyDiscoveredRule(new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"C:\Program Files\Contoso",
            FileOrFolderName = "app.exe",
            DetectionType = "version",
            DetectionValue = "4.2.1.0",
            Operator = "greaterThanOrEqual",
            CheckVersion = true,
            Check32BitOn64System = true,
        });

        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
        Assert.Equal(@"C:\Program Files\Contoso", vm.DetectionPath);
        Assert.Equal("app.exe", vm.DetectionName);
        Assert.Equal("4.2.1.0", vm.DetectionValue);
        Assert.True(vm.DetectionReady);
        Assert.Equal(@"File: C:\Program Files\Contoso\app.exe (Version >= 4.2.1.0)", vm.DetectionSummary);
    }

    [Fact]
    public void ADiscoveredExistsRuleSelectsFileExists()
    {
        var vm = Picker();

        vm.ApplyDiscoveredRule(new DetectionRule
        {
            Type = DetectionRuleType.File,
            Path = @"%LOCALAPPDATA%\Programs\Contoso",
            FileOrFolderName = "app.exe",
            DetectionType = "exists",
            Check32BitOn64System = true,
        });

        Assert.Equal(DetectionMethod.FileExists, vm.SelectedDetectionMethod);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Contoso", vm.DetectionPath);
        Assert.True(vm.DetectionReady);
    }

    [Fact]
    public void ADiscoveredRuleThatIsNotAFileRuleIsIgnored()
    {
        var vm = Picker();
        vm.SelectedDetectionMethod = DetectionMethod.FileExists;
        vm.DetectionPath = @"C:\App";
        vm.DetectionName = "app.exe";

        vm.ApplyDiscoveredRule(DetectionRuleFactory.Msi(ProductCode));

        Assert.Equal(DetectionMethod.FileExists, vm.SelectedDetectionMethod);
        Assert.Equal(@"C:\App", vm.DetectionPath);
        Assert.Equal("", vm.DetectionProductCode);
    }

    [Fact]
    public void ADiscoveredRuleSurvivesTheConfigureStepSeedingTheSamePackage()
    {
        var vm = Picker(productCode: ProductCode);

        vm.ApplyDiscoveredRule(DetectionRuleFactory.FileVersion(@"C:\Program Files\Contoso", "app.exe", "4.2.1.0"));
        // Configure seeds detection the first time it sees the package, after Remote test
        // already applied a rule to it.
        vm.RefreshFromPackage();

        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
        Assert.Equal(@"C:\Program Files\Contoso", vm.DetectionPath);
        Assert.Equal("4.2.1.0", vm.DetectionValue);
        // The MSI's product code stays available behind the MSI option.
        Assert.Equal(ProductCode, vm.DetectionProductCode);
    }

    [Fact]
    public void ForgettingThePackageDropsTheDiscoveredRule()
    {
        var vm = Picker(version: "4.2.1.0");
        vm.ApplyDiscoveredRule(DetectionRuleFactory.FileExists(@"C:\Program Files\Contoso", "app.exe"));

        vm.ForgetPackage();
        vm.RefreshFromPackage();

        Assert.Equal("", vm.DetectionPath);
        Assert.Equal("", vm.DetectionName);
        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
    }

    [Fact]
    public void ADiscoveredRuleForAnotherPackageIsNotApplied()
    {
        var vm = Picker(version: "4.2.1.0");
        vm.ApplyDiscoveredRule(DetectionRuleFactory.FileExists(@"C:\Program Files\Contoso", "app.exe"));

        _create.CurrentPackagePath = Fakes.Package(Path.Combine(_folder, "package-3"));
        vm.RefreshFromPackage();

        Assert.Equal("", vm.DetectionPath);
        Assert.Equal(DetectionMethod.FileVersion, vm.SelectedDetectionMethod);
    }

    // ── The standalone Upload page's rule list ──────────────────────────

    [Fact]
    public void TheStandalonePageAddsOneRulePerMethodAndClearsTheRightFields()
    {
        var vm = new UploadToIntuneViewModel();
        Assert.False(vm.HasDetectionRule);
        Assert.Equal("Incomplete", vm.DetectionState);
        Assert.Equal("Add a detection rule", vm.DetectionDetail);

        vm.NewRuleMethod = DetectionMethod.FileVersion;
        Assert.False(vm.CanAddRule);
        vm.NewRulePath = @"C:\Program Files\Contoso";
        vm.NewRuleName = "app.exe";
        vm.NewRuleValue = "4.2.1.0";
        Assert.True(vm.CanAddRule);
        vm.AddRuleCommand.Execute(null);

        Assert.Equal("1 rule", vm.DetectionDetail);
        Assert.Equal("Ready", vm.DetectionState);
        // The file fields are cleared for the next rule.
        Assert.Equal("", vm.NewRulePath);
        Assert.Equal("", vm.NewRuleName);
        Assert.Equal("", vm.NewRuleValue);

        vm.NewRuleMethod = DetectionMethod.RegistryKey;
        vm.NewRuleKeyPath = @"SOFTWARE\Contoso";
        vm.NewRuleValueName = "DisplayVersion";
        vm.AddRuleCommand.Execute(null);

        Assert.Equal("2 rules", vm.DetectionDetail);
        Assert.Equal("", vm.NewRuleKeyPath);
        Assert.Equal("", vm.NewRuleValueName);

        var payloads = vm.DetectionRules.Select(DetectionRuleGraph.Serialize).ToList();
        Assert.Equal("version", payloads[0]["detectionType"]);
        Assert.Equal("4.2.1.0", payloads[0]["detectionValue"]);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso", payloads[1]["keyPath"]);
        Assert.Equal("DisplayVersion", payloads[1]["valueName"]);

        vm.RemoveRuleCommand.Execute(vm.DetectionRules[0]);
        Assert.Equal("1 rule", vm.DetectionDetail);
    }

    [Fact]
    public void TheStandalonePageOnlyOffersOperatorsGraphAccepts()
    {
        var vm = new UploadToIntuneViewModel();

        Assert.All(vm.Operators, op => Assert.Contains(op,
            new[] { "notConfigured", "equal", "notEqual", "greaterThanOrEqual", "greaterThan", "lessThanOrEqual", "lessThan" }));
        Assert.Equal(DetectionRuleFactory.DefaultVersionOperator, vm.NewRuleOperator);
        Assert.Equal(RegistryHiveNames.All, vm.RegistryHives);
    }

    [Fact]
    public void AddRuleOnlyLightsUpWhenTheMethodsFieldsAreComplete()
    {
        var vm = new UploadToIntuneViewModel();

        // File exists: path and name, both of them.
        vm.NewRuleMethod = DetectionMethod.FileExists;
        Assert.False(vm.CanAddRule);
        vm.NewRulePath = @"C:\Program Files\Contoso";
        Assert.False(vm.CanAddRule);
        vm.NewRuleName = "app.exe";
        Assert.True(vm.CanAddRule);
        vm.NewRuleName = "   ";
        Assert.False(vm.CanAddRule);
        vm.NewRuleName = "app.exe";

        // File version: and a version Intune can compare.
        vm.NewRuleMethod = DetectionMethod.FileVersion;
        Assert.False(vm.CanAddRule);
        vm.NewRuleValue = "v1.2";
        Assert.False(vm.CanAddRule);
        vm.NewRuleValue = "4.2.1.0";
        Assert.True(vm.CanAddRule);

        // Registry: a key path and a hive.
        vm.NewRuleMethod = DetectionMethod.RegistryKey;
        Assert.False(vm.CanAddRule);
        vm.NewRuleKeyPath = @"SOFTWARE\Contoso";
        Assert.True(vm.CanAddRule);
        vm.NewRuleHive = "";
        Assert.False(vm.CanAddRule);
        vm.NewRuleHive = RegistryHiveNames.LocalMachine;
        Assert.True(vm.CanAddRule);

        // MSI: a product code that is a GUID, in any of the forms one gets pasted in.
        vm.NewRuleMethod = DetectionMethod.MsiProductCode;
        vm.NewRuleProductCode = "";
        Assert.False(vm.CanAddRule);
        vm.NewRuleProductCode = "oops";
        Assert.False(vm.CanAddRule);
        vm.NewRuleProductCode = ProductCode.ToLowerInvariant().Trim('{', '}');
        Assert.True(vm.CanAddRule);

        // And what it adds is the normalised code, not what was typed.
        vm.AddRuleCommand.Execute(null);
        Assert.Equal(ProductCode, DetectionRuleGraph.Serialize(Assert.Single(vm.DetectionRules))["productCode"]);
    }

    [Fact]
    public void EveryFieldThatAddRuleDependsOnRefreshesTheButton()
    {
        // The button is bound to the command, so a field whose setter does not raise
        // CanExecuteChanged leaves it greyed out until something else is touched.
        var vm = new UploadToIntuneViewModel { NewRuleMethod = DetectionMethod.FileVersion };
        var raised = 0;
        vm.AddRuleCommand.CanExecuteChanged += (_, _) => raised++;

        vm.NewRulePath = @"C:\App";
        Assert.Equal(1, raised);
        vm.NewRuleName = "app.exe";
        Assert.Equal(2, raised);
        vm.NewRuleValue = "4.2";
        Assert.Equal(3, raised);
        Assert.True(vm.AddRuleCommand.CanExecute(null));

        vm.NewRuleHive = "HKEY_CURRENT_USER";
        Assert.Equal(4, raised);
        vm.NewRuleKeyPath = @"SOFTWARE\Contoso";
        Assert.Equal(5, raised);
        vm.NewRuleProductCode = ProductCode;
        Assert.Equal(6, raised);
    }

    [Fact]
    public void TheStandalonePageOffersTheNotEqualOperatorGraphAccepts()
    {
        Assert.Contains("notEqual", new UploadToIntuneViewModel().Operators);
    }

    [Theory]
    // check32BitOn64System follows the package's architecture: on for a 32-bit app, off
    // otherwise, for file and registry rules alike.
    [InlineData("x86", true)]
    [InlineData("x64", false)]
    public void RulesAddedForAPackageFollowItsArchitecture(string architecture, bool expected)
    {
        var package = Fakes.Package(Path.Combine(_folder, "arch-" + architecture),
            $"$adtSession = @{{\n    AppVendor = 'Contoso'\n    AppName = 'Arch'\n    AppVersion = '1.0'\n    AppArch = '{architecture}'\n}}\n");
        var vm = new UploadToIntuneViewModel();
        vm.ProcessSelectedFolder(package);
        Assert.Equal("", vm.ValidationError);
        Assert.Equal(architecture, vm.PackageMeta.Split(" · ")[1]);

        vm.NewRuleMethod = DetectionMethod.FileExists;
        vm.NewRulePath = @"C:\Program Files\Contoso";
        vm.NewRuleName = "app.exe";
        vm.AddRuleCommand.Execute(null);

        vm.NewRuleMethod = DetectionMethod.RegistryKey;
        vm.NewRuleKeyPath = @"SOFTWARE\Contoso";
        vm.AddRuleCommand.Execute(null);

        Assert.Equal(2, vm.DetectionRules.Count);
        Assert.All(vm.DetectionRules, r => Assert.Equal(expected, DetectionRuleGraph.Serialize(r)["check32BitOn64System"]));
    }
}
