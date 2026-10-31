#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Buffers.Binary;
using System.Text;
using Packman.Helpers;
using Packman.Services;

// Installer insight without Windows: static EXE detection against small in-memory PE32 images,
// the installer catalog's pinned markers and switches, ArgumentLine, PsadtScript's install and
// uninstall lines, and the portable parts of MsiInfoService. Uses the production sources; no
// installer runs and nothing touches the registry or Windows Installer.
//   dotnet run --file tests/InstallerInsightTests.cs -c Release
// Exits non-zero when a check fails.
internal static class InstallerInsightTests
{
    private const int PeOffset = 0x80;
    private const int OptionalHeaderOffset = 0x98;
    private const int OptionalHeaderSize = 224;
    private const int SectionTableOffset = 0x178;
    private const int HeaderSize = 0x400;
    private const int FileAlignment = 0x200;
    private const uint SectionCharacteristics = 0x40000040; // initialized data, readable

    // Larger than the 64 KB overlay and tail windows, so markers placed in one area never leak into the other.
    private const int PaddingBytes = 70_000;

    private static int _passed;
    private static readonly List<string> Failures = new();

    private static readonly (string Name, Action Check)[] Checks =
    [
        ("Each pinned marker is detected as its kind with its confidence", PinnedMarkersDetect),
        ("The catalog holds every pinned marker with its area and confidence", CatalogHoldsPinnedMarkers),
        ("A marker found only as UTF-16LE is detected", Utf16MarkerDetected),
        ("Marker matching is case-sensitive", CaseSensitiveMatch),
        ("A kind scores its strongest marker and evidence names that marker", StrongestMarkerWins),
        ("A higher confidence marker beats an earlier catalog kind", HigherConfidenceBeatsCatalogOrder),
        ("Equal confidence falls back to catalog order", CatalogOrderBreaksTie),
        ("InstallShield's Low InstallScript marker loses to its Medium MSI-based marker", InstallScriptLowLoses),
        ("Evidence reads as the area and the marker text", EvidenceNamesAreaAndText),
        ("The Authenticode table bounds the overlay and tail scans", CertificateBoundsScan),
        ("A section size past the end of the file is clamped, not thrown", PointerPastEndClamped),
        ("An image with no marker is Unknown with no confidence or evidence", NoMarkerIsUnknown),
        ("Input that is not a PE image throws InvalidDataException", NonPeThrows),
        ("Catalog kinds are in the pinned order with unique names", KindNamesPinned),
        ("Every kind has a marker, at most one MustBeLast switch, and every switch has text and an explanation", CatalogDataRules),
        ("Inno Setup switches and uninstaller location", InnoSwitches),
        ("NSIS switches, /D= as the last switch, and uninstall.exe", NsisSwitches),
        ("WiX Burn switches with a literal log path and no uninstaller location", BurnSwitches),
        ("InstallShield MSI-based and InstallScript switches and locations", InstallShieldSwitches),
        ("Advanced Installer, InstallAware, Wise and Setup Factory switches and locations", OtherSetupSwitches),
        ("Squirrel, ClickOnce, 7-Zip, WinRAR and MSIX entries", NotesAndSwitchlessKinds),
        ("Unknown lists only guesses marked as untested", UnknownGuessesUntested),
        ("ArgumentLine.Append examples", AppendExamples),
        ("ArgumentLine.MsiProperty examples", MsiPropertyExamples),
        ("PsadtScript EXE install and uninstall lines", PsadtExeLines),
        ("PsadtScript MSI lines add -AdditionalArgumentList only when arguments are given", PsadtMsiLines),
        ("MsiProperty.IsPublic and Argument examples", MsiPropertyPublicAndArgument),
        ("MsiInfo defaults and KeyProperties examples", KeyPropertyExamples),
        ("MsiInfo.Search examples", SearchExamples),
        ("MsiInfoService.FindTransforms examples", FindTransformsExamples),
    ];

    // The catalog's pinned markers, written as Latin-1 text so binary magics are \u00XX escapes.
    // The InstallScript "InstallShield" resource row is Low and shadowed by the MSI-based Medium row.
    private static readonly (string Kind, MarkerArea Area, string Text, Confidence Confidence)[] PinnedMarkers =
    [
        ("Inno Setup", MarkerArea.Resources, "rDlPtS", Confidence.High),
        ("Inno Setup", MarkerArea.Overlay, "Inno Setup Setup Data", Confidence.High),
        ("Inno Setup", MarkerArea.Resources, "Inno Setup", Confidence.Medium),
        ("NSIS (Nullsoft)", MarkerArea.Section, ".ndata", Confidence.High),
        ("NSIS (Nullsoft)", MarkerArea.Overlay, "\u00EF\u00BE\u00AD\u00DENullsoftInst", Confidence.High),
        ("NSIS (Nullsoft)", MarkerArea.Resources, "Nullsoft", Confidence.Medium),
        ("WiX Burn", MarkerArea.Section, ".wixburn", Confidence.High),
        ("InstallShield (MSI-based)", MarkerArea.Resources, "InstallShield", Confidence.Medium),
        ("InstallShield (InstallScript)", MarkerArea.Overlay, "setup.inx", Confidence.High),
        ("InstallShield (InstallScript)", MarkerArea.Resources, "InstallShield", Confidence.Low),
        ("Advanced Installer", MarkerArea.Tail, "ADVINSTSFX", Confidence.High),
        ("Advanced Installer", MarkerArea.Resources, "Advanced Installer", Confidence.Medium),
        ("InstallAware", MarkerArea.Resources, "InstallAware", Confidence.Medium),
        ("Wise", MarkerArea.Resources, "Wise Installation", Confidence.Medium),
        ("Setup Factory", MarkerArea.Resources, "Setup Factory", Confidence.Medium),
        ("Squirrel / Velopack", MarkerArea.Resources, ".nupkg", Confidence.Medium),
        ("Squirrel / Velopack", MarkerArea.Overlay, ".nupkg", Confidence.Medium),
        ("Squirrel / Velopack", MarkerArea.Tail, ".nupkg", Confidence.Medium),
        ("ClickOnce", MarkerArea.Resources, "SETUPCFG", Confidence.Medium),
        ("ClickOnce", MarkerArea.Resources, ".application", Confidence.Medium),
        ("7-Zip SFX", MarkerArea.Overlay, ";!@Install@!UTF-8!", Confidence.High),
        ("7-Zip SFX", MarkerArea.Overlay, "\u0037\u007A\u00BC\u00AF\u0027\u001C", Confidence.High),
        ("WinRAR SFX", MarkerArea.Overlay, "\u0052\u0061\u0072\u0021\u001A\u0007", Confidence.High),
        ("MSIX / AppX bundle", MarkerArea.Tail, "AppxManifest.xml", Confidence.High),
        ("MSIX / AppX bundle", MarkerArea.Tail, "AppxBundleManifest.xml", Confidence.High),
    ];

    private static readonly string[] KindNames =
    [
        "Inno Setup", "NSIS (Nullsoft)", "WiX Burn", "InstallShield (MSI-based)", "InstallShield (InstallScript)",
        "Advanced Installer", "InstallAware", "Wise", "Setup Factory", "Squirrel / Velopack", "ClickOnce",
        "7-Zip SFX", "WinRAR SFX", "MSIX / AppX bundle",
    ];

    public static int Main()
    {
        foreach (var (name, check) in Checks)
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
            : $"{Failures.Count} of {_passed + Failures.Count} checks failed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── Detection ──

    private static void PinnedMarkersDetect()
    {
        // The InstallScript Low row cannot win on its own; CatalogHoldsPinnedMarkers covers it.
        foreach (var pin in PinnedMarkers.Where(p => p.Confidence != Confidence.Low))
        {
            var detection = Detect(ImageWithMarker(pin.Area, pin.Text));
            True(detection.Kind.Name == pin.Kind && detection.Confidence == pin.Confidence,
                $"{pin.Kind} {pin.Area} \"{pin.Text}\" gave {detection.Kind.Name} {detection.Confidence}, expected {pin.Confidence}");
        }
    }

    private static void CatalogHoldsPinnedMarkers()
    {
        foreach (var pin in PinnedMarkers)
        {
            var kind = Kind(pin.Kind);
            True(kind.Markers.Any(m => m.Area == pin.Area && m.Text == pin.Text && m.Confidence == pin.Confidence),
                $"{pin.Kind} lacks {pin.Area} \"{pin.Text}\" {pin.Confidence}");
        }
    }

    private static void Utf16MarkerDetected()
    {
        var detection = Detect(Pe32([(".rsrc", Utf16("SETUPCFG"))]));
        Equal("ClickOnce", detection.Kind.Name);
        Equal<Confidence?>(Confidence.Medium, detection.Confidence);
    }

    private static void CaseSensitiveMatch()
    {
        var detection = Detect(Pe32([(".rsrc", Latin1("inno setup"))]));
        Equal("Unknown", detection.Kind.Name);
    }

    private static void StrongestMarkerWins()
    {
        // rDlPtS is High, "Inno Setup" is Medium; both sit in the resources.
        var detection = Detect(Pe32([(".rsrc", Join(Latin1("Inno Setup"), Zeros(4), Latin1("rDlPtS")))]));
        Equal("Inno Setup", detection.Kind.Name);
        Equal<Confidence?>(Confidence.High, detection.Confidence);
        Equal("resources rDlPtS", detection.Evidence);
    }

    private static void HigherConfidenceBeatsCatalogOrder()
    {
        // Inno Setup is first in the catalog but only Medium here; WiX Burn's .wixburn section is High.
        var detection = Detect(Pe32([(".rsrc", Latin1("Inno Setup")), (".wixburn", Zeros(0x200))]));
        Equal("WiX Burn", detection.Kind.Name);
        Equal<Confidence?>(Confidence.High, detection.Confidence);
    }

    private static void CatalogOrderBreaksTie()
    {
        // "Nullsoft" (NSIS) and "Inno Setup" (Inno) are both Medium; Inno comes first in the catalog.
        var detection = Detect(Pe32([(".rsrc", Join(Latin1("Nullsoft"), Zeros(4), Latin1("Inno Setup")))]));
        Equal("Inno Setup", detection.Kind.Name);
        Equal<Confidence?>(Confidence.Medium, detection.Confidence);
    }

    private static void InstallScriptLowLoses()
    {
        var detection = Detect(Pe32([(".rsrc", Latin1("InstallShield"))]));
        Equal("InstallShield (MSI-based)", detection.Kind.Name);
        Equal<Confidence?>(Confidence.Medium, detection.Confidence);
    }

    private static void EvidenceNamesAreaAndText()
    {
        Equal("section .wixburn", Detect(ImageWithMarker(MarkerArea.Section, ".wixburn")).Evidence);

        var nsisOverlay = Detect(ImageWithMarker(MarkerArea.Overlay, "\u00EF\u00BE\u00AD\u00DENullsoftInst")).Evidence;
        True(nsisOverlay.StartsWith("overlay ", StringComparison.Ordinal), $"overlay evidence was \"{nsisOverlay}\"");
        True(nsisOverlay.Contains("NullsoftInst", StringComparison.Ordinal), $"overlay evidence was \"{nsisOverlay}\"");
        True(!nsisOverlay.Any(c => char.IsControl(c)), "evidence holds control characters");
    }

    private static void CertificateBoundsScan()
    {
        var inCertificate = Pe32([], certificate: Join(Latin1("Inno Setup Setup Data"), Zeros(0x100)));
        Equal("Unknown", Detect(inCertificate).Kind.Name);

        // The same marker before the certificate table is found, so the miss above is the bound.
        var beforeCertificate = Pe32([], overlay: Join(Latin1("Inno Setup Setup Data"), Zeros(PaddingBytes)), certificate: Zeros(0x200));
        Equal("Inno Setup", Detect(beforeCertificate).Kind.Name);
    }

    private static void PointerPastEndClamped()
    {
        var image = Pe32([(".rsrc", Latin1("Nullsoft"))]);
        // Section 1 is .rsrc (section 0 is .text); its SizeOfRawData sits 16 bytes into the entry.
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionTableOffset + 40 + 16), 0x7FFF_FFFF);
        Equal("NSIS (Nullsoft)", Detect(image).Kind.Name);
    }

    private static void NoMarkerIsUnknown()
    {
        var detection = Detect(Pe32([(".rsrc", Latin1("nothing to see here"))]));
        Equal("Unknown", detection.Kind.Name);
        Equal<Confidence?>(null, detection.Confidence);
        Equal("", detection.Evidence);
    }

    private static void NonPeThrows()
    {
        Throws<InvalidDataException>(() => Detect(Zeros(10)));
        Throws<InvalidDataException>(() => Detect(Zeros(200)));

        var badPeOffset = Zeros(0x200);
        "MZ"u8.CopyTo(badPeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(badPeOffset.AsSpan(0x3C), 0x10000);
        Throws<InvalidDataException>(() => Detect(badPeOffset));

        var noSignature = Zeros(0x200);
        "MZ"u8.CopyTo(noSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(noSignature.AsSpan(0x3C), PeOffset);
        Throws<InvalidDataException>(() => Detect(noSignature));

        var folder = Directory.CreateTempSubdirectory("packman-insight-").FullName;
        try
        {
            var path = Path.Combine(folder, "setup.exe");
            File.WriteAllText(path, "This is a text file renamed to .exe");
            Throws<InvalidDataException>(() => InstallerDetector.Detect(path));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ── Catalog data ──

    private static void KindNamesPinned()
    {
        var names = InstallerCatalog.Kinds.Select(k => k.Name).ToArray();
        Equal(string.Join("|", KindNames), string.Join("|", names));
        Equal(KindNames.Length, names.Distinct().Count());
        Equal("Unknown", InstallerCatalog.Unknown.Name);
        Equal(0, InstallerCatalog.Unknown.Markers.Length);
    }

    private static void CatalogDataRules()
    {
        foreach (var kind in InstallerCatalog.Kinds)
        {
            True(kind.Markers.Length > 0, $"{kind.Name} has no marker");
            True(kind.Switches.Count(s => s.MustBeLast) <= 1, $"{kind.Name} has more than one MustBeLast switch");
            foreach (var s in kind.Switches)
            {
                True(!string.IsNullOrWhiteSpace(s.Text), $"{kind.Name} has a switch without text");
                True(!string.IsNullOrWhiteSpace(s.Explanation), $"{kind.Name} switch {s.Text} has no explanation");
            }
        }
    }

    private static void InnoSwitches()
    {
        var inno = Kind("Inno Setup");
        foreach (var flag in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG" })
        {
            True(HasSwitch(inno, false, flag), $"Inno install lacks {flag}");
            True(HasSwitch(inno, true, flag), $"Inno uninstall lacks {flag}");
        }
        True(inno.UninstallerLocation.Contains("unins000.exe", StringComparison.OrdinalIgnoreCase), "Inno location lacks unins000.exe");
    }

    private static void NsisSwitches()
    {
        var nsis = Kind("NSIS (Nullsoft)");
        True(HasSwitch(nsis, false, "/S"), "NSIS install lacks /S");
        True(HasSwitch(nsis, true, "/S"), "NSIS uninstall lacks /S");
        var last = nsis.Switches.Single(s => s.MustBeLast);
        True(!last.Uninstall && SwitchIs(last.Text, "/D="), $"NSIS MustBeLast is {last.Text}, expected /D=");
        True(nsis.UninstallerLocation.Contains("uninstall.exe", StringComparison.OrdinalIgnoreCase), "NSIS location lacks uninstall.exe");
    }

    private static void BurnSwitches()
    {
        var burn = Kind("WiX Burn");
        foreach (var flag in new[] { "/quiet", "/norestart" })
        {
            True(HasSwitch(burn, false, flag), $"Burn install lacks {flag}");
        }
        True(HasSwitch(burn, false, "/log"), "Burn install lacks /log");
        True(burn.Switches.Any(s => !s.Uninstall && SwitchIs(s.Text, "/log") && s.Text.Contains(@"C:\Windows\Logs\Software\", StringComparison.Ordinal)),
            "Burn /log lacks a literal path under C:\\Windows\\Logs\\Software\\");
        foreach (var flag in new[] { "/uninstall", "/quiet", "/norestart" })
        {
            True(HasSwitch(burn, true, flag), $"Burn uninstall lacks {flag}");
        }
        Equal("", burn.UninstallerLocation);
    }

    private static void InstallShieldSwitches()
    {
        var msi = Kind("InstallShield (MSI-based)");
        True(HasSwitch(msi, false, "/s"), "InstallShield MSI install lacks /s");
        True(HasSwitch(msi, false, @"/v""/qn REBOOT=ReallySuppress"""), "InstallShield MSI install lacks /v\"/qn REBOOT=ReallySuppress\"");
        foreach (var flag in new[] { "/s", "/x", @"/v""/qn""" })
        {
            True(HasSwitch(msi, true, flag), $"InstallShield MSI uninstall lacks {flag}");
        }

        var script = Kind("InstallShield (InstallScript)");
        True(HasSwitch(script, false, "/s"), "InstallShield InstallScript install lacks /s");
        foreach (var flag in new[] { "/s", "/x" })
        {
            True(HasSwitch(script, true, flag), $"InstallShield InstallScript uninstall lacks {flag}");
        }
        True(script.UninstallerLocation.Contains(@"InstallShield Installation Information", StringComparison.OrdinalIgnoreCase),
            "InstallScript location lacks InstallShield Installation Information");
    }

    private static void OtherSetupSwitches()
    {
        var advanced = Kind("Advanced Installer");
        foreach (var flag in new[] { "/exenoui", "/qn", "/norestart" })
        {
            True(HasSwitch(advanced, false, flag), $"Advanced Installer install lacks {flag}");
        }
        foreach (var flag in new[] { "/x", "/exenoui", "/qn" })
        {
            True(HasSwitch(advanced, true, flag), $"Advanced Installer uninstall lacks {flag}");
        }

        var aware = Kind("InstallAware");
        True(HasSwitch(aware, false, "/s"), "InstallAware install lacks /s");
        foreach (var flag in new[] { "/s", "MODIFY=FALSE", "REMOVE=TRUE", "UNINSTALL=YES" })
        {
            True(HasSwitch(aware, true, flag), $"InstallAware uninstall lacks {flag}");
        }

        var wise = Kind("Wise");
        True(HasSwitch(wise, false, "/s"), "Wise install lacks /s");
        True(HasSwitch(wise, true, "/s"), "Wise uninstall lacks /s");
        True(wise.UninstallerLocation.Contains("UNWISE.EXE", StringComparison.OrdinalIgnoreCase), "Wise location lacks UNWISE.EXE");

        var factory = Kind("Setup Factory");
        True(HasSwitch(factory, false, "/S"), "Setup Factory install lacks /S");
        True(HasSwitch(factory, true, "/S"), "Setup Factory uninstall lacks /S");
        True(factory.UninstallerLocation.Contains(@"uninstall\uninstall.exe", StringComparison.OrdinalIgnoreCase),
            @"Setup Factory location lacks uninstall\uninstall.exe");
    }

    private static void NotesAndSwitchlessKinds()
    {
        var squirrel = Kind("Squirrel / Velopack");
        True(HasSwitch(squirrel, false, "--silent"), "Squirrel install lacks --silent");
        True(HasSwitch(squirrel, true, "--uninstall"), "Squirrel uninstall lacks --uninstall");
        True(squirrel.UninstallerLocation.Contains("Update.exe", StringComparison.OrdinalIgnoreCase), "Squirrel location lacks Update.exe");
        True(squirrel.Note.Length > 0, "Squirrel has no note");

        Equal(0, Kind("ClickOnce").Switches.Length);
        True(Kind("ClickOnce").Note.Length > 0, "ClickOnce has no note");

        var sevenZip = Kind("7-Zip SFX");
        True(sevenZip.Switches.Any(s => SwitchIs(s.Text, "/S")), "7-Zip SFX lacks /S");
        True(sevenZip.Switches.Any(s => SwitchIs(s.Text, "-y")), "7-Zip SFX lacks -y");
        True(sevenZip.Note.Length > 0, "7-Zip SFX has no note");

        var winRar = Kind("WinRAR SFX");
        True(winRar.Switches.Any(s => SwitchIs(s.Text, "-s")), "WinRAR SFX lacks -s");
        True(winRar.Note.Length > 0, "WinRAR SFX has no note");

        Equal(0, Kind("MSIX / AppX bundle").Switches.Length);
        True(Kind("MSIX / AppX bundle").Note.Length > 0, "MSIX bundle has no note");
    }

    private static void UnknownGuessesUntested()
    {
        var unknown = InstallerCatalog.Unknown;
        foreach (var flag in new[] { "/S", "/silent", "/VERYSILENT", "/quiet", "/q", "-s", "--silent" })
        {
            True(HasSwitch(unknown, false, flag), $"Unknown install guess {flag} missing");
        }
        foreach (var flag in new[] { "/S", "/uninstall", "/quiet", "/x" })
        {
            True(HasSwitch(unknown, true, flag), $"Unknown uninstall guess {flag} missing");
        }
        foreach (var s in unknown.Switches)
        {
            True(s.Explanation.StartsWith("Untested", StringComparison.Ordinal), $"Unknown guess {s.Text} is not marked untested");
        }
    }

    // ── ArgumentLine, PsadtScript ──

    private static void AppendExamples()
    {
        (string Line, string Token, string? Last, string Expected)[] examples =
        [
            ("", "/S", null, "/S"),
            ("/VERYSILENT", "/NORESTART", null, "/VERYSILENT /NORESTART"),
            ("/S", "/s", null, "/S"),
            ("/SUPPRESSMSGBOXES", "/S", null, "/SUPPRESSMSGBOXES /S"),
            (@"/S /D=C:\Program Files\App", "/NCRC", "/D=", @"/S /NCRC /D=C:\Program Files\App"),
            (@"/S /D=C:\X", "/D=", "/D=", @"/S /D=C:\X"),
            ("ALLUSERS=1", "ALLUSERS=2", null, "ALLUSERS=1"),
        ];
        foreach (var (line, token, last, expected) in examples)
        {
            Equal(expected, ArgumentLine.Append(line, token, last));
        }
    }

    private static void MsiPropertyExamples()
    {
        Equal("ALLUSERS=1", ArgumentLine.MsiProperty("ALLUSERS", "1"));
        Equal(@"INSTALLDIR=""C:\Program Files\App""", ArgumentLine.MsiProperty("INSTALLDIR", @"C:\Program Files\App"));
        Equal(@"X=""""", ArgumentLine.MsiProperty("X", ""));
    }

    private static void PsadtExeLines()
    {
        const string Folder = @"Start-ADTProcess -FilePath ""$($adtSession.DirFiles)\setup.exe"" -ArgumentList ";
        Equal(Folder + "'/S'", PsadtScript.InstallCommand(false, "setup.exe", "/S"));
        Equal(Folder + "'/S /NORESTART'", PsadtScript.InstallCommand(false, "setup.exe", " /S /NORESTART "));
        Equal(Folder + "'<silent flags>'", PsadtScript.InstallCommand(false, "setup.exe"));
        Equal(Folder + "'/S'", PsadtScript.UninstallCommand(false, "setup.exe", null, "/S"));
        Equal(Folder + "'<uninstall flags>'", PsadtScript.UninstallCommand(false, "setup.exe", null));
    }

    private static void PsadtMsiLines()
    {
        const string Install = @"Start-ADTMsiProcess -Action 'Install' -FilePath ""$($adtSession.DirFiles)\app.msi""";
        Equal(Install, PsadtScript.InstallCommand(true, "app.msi"));
        Equal(Install + " -AdditionalArgumentList 'ALLUSERS=1'", PsadtScript.InstallCommand(true, "app.msi", "ALLUSERS=1"));
        True(PsadtScript.InstallCommand(true, "app.msi", "NAME=O'Brien").Contains("-AdditionalArgumentList 'NAME=O''Brien'"),
            "A single quote in MSI arguments is not escaped");

        const string Guid = "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}";
        Equal($"Start-ADTMsiProcess -Action 'Uninstall' -FilePath '{Guid}'", PsadtScript.UninstallCommand(true, "app.msi", Guid));
        Equal($"Start-ADTMsiProcess -Action 'Uninstall' -FilePath '{Guid}' -AdditionalArgumentList 'REMOVE=ALL'",
            PsadtScript.UninstallCommand(true, "app.msi", Guid, "REMOVE=ALL"));
    }

    // ── MsiInfoService ──

    private static void MsiPropertyPublicAndArgument()
    {
        True(new MsiInfoService.MsiProperty("ALLUSERS", "1").IsPublic, "ALLUSERS should be public");
        True(new MsiInfoService.MsiProperty("WIX_UPGRADE_DETECTED", "x").IsPublic, "WIX_UPGRADE_DETECTED should be public");
        True(!new MsiInfoService.MsiProperty("ProductCode", "{x}").IsPublic, "ProductCode should not be public");
        True(!new MsiInfoService.MsiProperty("SecureCustomProperties", "x").IsPublic, "SecureCustomProperties should not be public");
        Equal(@"INSTALLDIR=""C:\Program Files\App""", new MsiInfoService.MsiProperty("INSTALLDIR", @"C:\Program Files\App").Argument);
    }

    private static void KeyPropertyExamples()
    {
        var empty = new MsiInfoService.MsiInfo();
        Equal(0, empty.Properties.Count());
        Equal(0, empty.Transforms.Count());

        var info = new MsiInfoService.MsiInfo { Properties = SampleTable() };
        Equal("ProductName, Manufacturer, ProductCode, ALLUSERS, INSTALLFOLDER", string.Join(", ", info.KeyProperties.Select(p => p.Name)));
    }

    private static void SearchExamples()
    {
        var info = new MsiInfoService.MsiInfo { Properties = SampleTable() };
        Equal("ProductCode", string.Join(", ", info.Search("code").Select(p => p.Name)));
        Equal("Manufacturer", string.Join(", ", info.Search("CONTOSO").Select(p => p.Name)));
        Equal("INSTALLFOLDER, ProductCode, Manufacturer, ALLUSERS, ARPNOREPAIR, ProductName",
            string.Join(", ", info.Search("  ").Select(p => p.Name)));
        Equal("INSTALLFOLDER, ProductCode, Manufacturer, ALLUSERS, ARPNOREPAIR, ProductName",
            string.Join(", ", info.Search(null).Select(p => p.Name)));
        Equal(0, info.Search("zzz").Count());
    }

    private static void FindTransformsExamples()
    {
        var folder = Directory.CreateTempSubdirectory("packman-insight-").FullName;
        try
        {
            foreach (var name in new[] { "b.mst", "A.mst", "app.msi", "notes.txt" })
            {
                File.WriteAllText(Path.Combine(folder, name), "");
            }
            Equal("A.mst, b.mst", string.Join(", ", MsiInfoService.FindTransforms(Path.Combine(folder, "app.msi"))));
            Equal(0, MsiInfoService.FindTransforms(Path.Combine(folder, "missing", "app.msi")).Count());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ── PE image builder ──

    private static MsiInfoService.MsiProperty[] SampleTable() =>
    [
        new("INSTALLFOLDER", @"C:\Apps"),
        new("ProductCode", "{3FA85F64-5717-4562-B3FC-2C963F66AFA6}"),
        new("Manufacturer", "Contoso"),
        new("ALLUSERS", "1"),
        new("ARPNOREPAIR", "1"),
        new("ProductName", "Reader"),
    ];

    private static InstallerKind Kind(string name) => InstallerCatalog.Kinds.Single(k => k.Name == name);

    private static InstallerDetection Detect(byte[] image) => InstallerDetector.Detect(new MemoryStream(image));

    private static byte[] ImageWithMarker(MarkerArea area, string text)
    {
        var bytes = Latin1(text);
        return area switch
        {
            MarkerArea.Section => Pe32([(text, Zeros(0x200))]),
            MarkerArea.Resources => Pe32([(".rsrc", bytes)]),
            MarkerArea.Overlay => Pe32([], overlay: Join(bytes, Zeros(PaddingBytes))),
            MarkerArea.Tail => Pe32([], tail: bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
        };
    }

    /// <summary>
    /// A PE32 image: a .text section, the given sections, then overlay, tail and certificate bytes.
    /// The overlay defaults to padding, so the tail window never reaches the sections. Sections with
    /// data are placed on 512-byte raw boundaries, and the Authenticode table (data directory 4) points
    /// at the certificate bytes when one is given.
    /// </summary>
    private static byte[] Pe32(
        (string Name, byte[] Data)[] sections,
        byte[]? overlay = null,
        byte[]? tail = null,
        byte[]? certificate = null)
    {
        var all = new List<(string Name, byte[] Data)> { (".text", Zeros(0x200)) };
        all.AddRange(sections);

        var image = new MemoryStream();
        image.Write(Zeros(HeaderSize));
        var raw = new List<(int Pointer, int Size)>();
        foreach (var (_, data) in all)
        {
            var size = Align(data.Length, FileAlignment);
            raw.Add(((int)image.Length, size));
            image.Write(data);
            image.Write(Zeros(size - data.Length));
        }
        image.Write(overlay ?? Zeros(PaddingBytes));
        image.Write(tail ?? Array.Empty<byte>());
        var certificateOffset = (int)image.Length;
        if (certificate != null) image.Write(certificate);

        var bytes = image.ToArray();
        WriteHeaders(bytes, all, raw, certificate == null ? null : (certificateOffset, certificate.Length));
        return bytes;
    }

    private static void WriteHeaders(
        byte[] image,
        List<(string Name, byte[] Data)> sections,
        List<(int Pointer, int Size)> raw,
        (int Offset, int Length)? certificate)
    {
        "MZ"u8.CopyTo(image);
        U32(image, 0x3C, PeOffset);
        "PE\0\0"u8.CopyTo(image.AsSpan(PeOffset));

        var coff = PeOffset + 4;
        U16(image, coff, 0x14C);                         // Machine: i386
        U16(image, coff + 2, (ushort)sections.Count);
        U16(image, coff + 16, OptionalHeaderSize);
        U16(image, coff + 18, 0x0102);                   // executable, 32-bit

        U16(image, OptionalHeaderOffset, 0x10B);         // PE32
        U32(image, OptionalHeaderOffset + 32, 0x1000);   // SectionAlignment
        U32(image, OptionalHeaderOffset + 36, FileAlignment);
        U32(image, OptionalHeaderOffset + 60, HeaderSize);
        U32(image, OptionalHeaderOffset + 92, 16);       // NumberOfRvaAndSizes
        if (certificate is { } cert)
        {
            var securityDirectory = OptionalHeaderOffset + 96 + 4 * 8;
            U32(image, securityDirectory, (uint)cert.Offset);
            U32(image, securityDirectory + 4, (uint)cert.Length);
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var at = SectionTableOffset + 40 * i;
            Encoding.Latin1.GetBytes(sections[i].Name).CopyTo(image.AsSpan(at, 8));
            U32(image, at + 8, (uint)sections[i].Data.Length);
            U32(image, at + 12, (uint)(0x1000 * (i + 1)));
            U32(image, at + 16, (uint)raw[i].Size);
            U32(image, at + 20, (uint)raw[i].Pointer);
            U32(image, at + 36, SectionCharacteristics);
        }
    }

    private static void U16(byte[] image, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), value);
    private static void U32(byte[] image, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), value);

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);
    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);
    private static byte[] Zeros(int count) => new byte[count];

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static bool HasSwitch(InstallerKind kind, bool uninstall, string flag) =>
        kind.Switches.Any(s => s.Uninstall == uninstall && SwitchIs(s.Text, flag));

    /// <summary>True when the switch text is the flag, or starts with it as a whole token (/LOG, /LOG=..., /D=path).</summary>
    private static bool SwitchIs(string text, string flag) =>
        text == flag
        || text.StartsWith(flag, StringComparison.Ordinal) && text.Length > flag.Length
            && (flag.EndsWith('=') || text[flag.Length] is ' ' or '=');

    // ── Assertions ──

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected <{expected}>, got <{actual}>");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"expected {typeof(TException).Name}, nothing was thrown");
    }
}
