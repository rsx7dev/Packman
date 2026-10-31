namespace Packman.Services;

public enum Confidence { Low, Medium, High }

/// <summary>Where in the PE file a marker is looked for.</summary>
public enum MarkerArea { Section, Overlay, Resources, Tail }

/// <summary>
/// Text to find in <paramref name="Area"/>. Matched as Latin-1 bytes or UTF-16LE, ordinal and
/// case-sensitive, so binary magics are written as \u00XX escapes.
/// </summary>
public record Marker(MarkerArea Area, string Text, Confidence Confidence);

public record InstallerSwitch(string Text, string Explanation, bool Uninstall = false, bool MustBeLast = false);

/// <param name="UninstallerLocation">Where the vendor's own uninstaller usually lives; empty when the setup file uninstalls itself.</param>
public record InstallerKind(string Name, Marker[] Markers, InstallerSwitch[] Switches, string UninstallerLocation, string Note = "");

/// <summary>
/// Every installer technology Packman knows, in the order they win ties. Adding a type is
/// adding an entry here and nowhere else.
/// </summary>
public static class InstallerCatalog
{
    private const string LogFolder = @"C:\Windows\Logs\Software";
    private const string SfxNote = "Usually wraps an MSI or setup.exe. Packaging the inner MSI is more reliable.";

    public static readonly InstallerKind Unknown = new("Unknown", [],
    [
        new("/S", "Untested: NSIS, Setup Factory, 7-Zip SFX and others use this."),
        new("/silent", "Untested: used by Inno Setup and several custom installers."),
        new("/VERYSILENT", "Untested: Inno Setup's fully silent mode."),
        new("/quiet", "Untested: WiX Burn bundles and many MSI wrappers use this."),
        new("/q", "Untested: a short quiet switch used by some wrappers."),
        new("-s", "Untested: WinRAR SFX and some older installers use this."),
        new("--silent", "Untested: used by Squirrel and Electron-style installers."),
        new("/S", "Untested: the same flag is most often the uninstall switch too.", Uninstall: true),
        new("/uninstall", "Untested: Burn bundles and some custom installers accept this.", Uninstall: true),
        new("/quiet", "Untested: suppresses the uninstall UI for Burn bundles.", Uninstall: true),
        new("/x", "Untested: InstallShield and Advanced Installer EXEs remove the product with this.", Uninstall: true),
    ], "");

    public static readonly InstallerKind[] Kinds =
    [
        new("Inno Setup",
        [
            new(MarkerArea.Resources, "rDlPtS", Confidence.High),
            new(MarkerArea.Overlay, "Inno Setup Setup Data", Confidence.High),
            new(MarkerArea.Resources, "Inno Setup", Confidence.Medium),
            new(MarkerArea.Resources, "JR.Inno.Setup", Confidence.Medium),
        ],
        [
            new("/VERYSILENT", "No wizard and no progress window."),
            new("/SUPPRESSMSGBOXES", "Answers message boxes with their default instead of waiting."),
            new("/NORESTART", "Never restarts the computer when the install finishes."),
            new("/LOG", "Writes a setup log to %TEMP% (add =path for a fixed file)."),
            new("/VERYSILENT", "Removes without a wizard when run from the uninstaller.", Uninstall: true),
            new("/SUPPRESSMSGBOXES", "Answers message boxes with their default.", Uninstall: true),
            new("/NORESTART", "Never restarts the computer after the removal.", Uninstall: true),
            new("/LOG", "Writes an uninstall log to %TEMP%.", Uninstall: true),
        ], @"unins000.exe in the install folder (unins001.exe and up for repeat installs)"),

        new("NSIS (Nullsoft)",
        [
            new(MarkerArea.Section, ".ndata", Confidence.High),
            new(MarkerArea.Overlay, "\u00EF\u00BE\u00AD\u00DENullsoftInst", Confidence.High),
            new(MarkerArea.Resources, "Nullsoft", Confidence.Medium),
        ],
        [
            new("/S", "Silent install. The switch is case-sensitive."),
            new("/D=", "Install folder. Must be the last argument and is never quoted, even with spaces.", MustBeLast: true),
            new("/S", "Silent removal when run from the uninstaller.", Uninstall: true),
        ], "uninstall.exe in the install folder (the name is chosen by the vendor)"),

        new("WiX Burn",
        [
            new(MarkerArea.Section, ".wixburn", Confidence.High),
        ],
        [
            new("/quiet", "No UI at all."),
            new("/norestart", "Never restarts the computer."),
            new($@"/log {LogFolder}\Install.log", "Writes the bundle log to a fixed file."),
            new("/uninstall", "Removes the bundle instead of installing it.", Uninstall: true),
            new("/quiet", "No UI at all.", Uninstall: true),
            new("/norestart", "Never restarts the computer.", Uninstall: true),
        ], ""),

        new("InstallShield (MSI-based)",
        [
            new(MarkerArea.Resources, "InstallShield", Confidence.Medium),
        ],
        [
            new("/s", "Silent mode for the setup launcher."),
            new("/v\"/qn REBOOT=ReallySuppress\"", "Passes /qn and a reboot suppression on to the MSI inside."),
            new("/s", "Silent mode for the setup launcher.", Uninstall: true),
            new("/x", "Removes the product instead of installing it.", Uninstall: true),
            new("/v\"/qn\"", "Passes /qn on to the MSI inside.", Uninstall: true),
        ], ""),

        new("InstallShield (InstallScript)",
        [
            new(MarkerArea.Overlay, "setup.inx", Confidence.High),
            new(MarkerArea.Resources, "InstallShield", Confidence.Low),
        ],
        [
            new("/s", "Silent install; replays the setup.iss beside setup.exe, recorded once with /r."),
            new("/s", "Silent mode for the setup launcher.", Uninstall: true),
            new("/x", "Removes the product instead of installing it.", Uninstall: true),
        ], @"InstallShield Installation Information\{product GUID}\setup.exe",
        "InstallScript installs need a recorded setup.iss. Run setup.exe /r once on a test machine and add the file to Files."),

        new("Advanced Installer",
        [
            new(MarkerArea.Tail, "ADVINSTSFX", Confidence.High),
            new(MarkerArea.Resources, "Advanced Installer", Confidence.Medium),
            new(MarkerArea.Resources, "Caphyon", Confidence.Medium),
        ],
        [
            new("/exenoui", "Hides the EXE launcher's own UI."),
            new("/qn", "Passes quiet mode on to the MSI inside."),
            new("/norestart", "Never restarts the computer."),
            new("/x", "Removes the product instead of installing it.", Uninstall: true),
            new("/exenoui", "Hides the EXE launcher's own UI.", Uninstall: true),
            new("/qn", "Passes quiet mode on to the MSI inside.", Uninstall: true),
        ], ""),

        new("InstallAware",
        [
            new(MarkerArea.Resources, "InstallAware", Confidence.Medium),
        ],
        [
            new("/s", "Silent install."),
            new("/s", "Silent removal.", Uninstall: true),
            new("MODIFY=FALSE", "Turns off the modify/repair path.", Uninstall: true),
            new("REMOVE=TRUE", "Selects the removal path.", Uninstall: true),
            new("UNINSTALL=YES", "Confirms the removal.", Uninstall: true),
        ], ""),

        new("Wise",
        [
            new(MarkerArea.Resources, "Wise Installation", Confidence.Medium),
        ],
        [
            new("/s", "Silent install."),
            new("/s", "Silent removal when run from UNWISE.EXE.", Uninstall: true),
        ], "UNWISE.EXE in the install folder (it reads INSTALL.LOG)"),

        new("Setup Factory",
        [
            new(MarkerArea.Resources, "Setup Factory", Confidence.Medium),
            new(MarkerArea.Resources, "Indigo Rose", Confidence.Medium),
        ],
        [
            new("/S", "Silent install."),
            new("/S", "Silent removal when run from the uninstaller.", Uninstall: true),
        ], @"uninstall\uninstall.exe in the install folder"),

        new("Squirrel / Velopack",
        [
            new(MarkerArea.Resources, ".nupkg", Confidence.Medium),
            new(MarkerArea.Overlay, ".nupkg", Confidence.Medium),
            new(MarkerArea.Tail, ".nupkg", Confidence.Medium),
        ],
        [
            new("--silent", "Installs without showing the splash window."),
            new("--uninstall", "Removes the app.", Uninstall: true),
        ], @"%LocalAppData%\<app>\Update.exe",
        "Installs per user. Deploy it in User context."),

        new("ClickOnce",
        [
            new(MarkerArea.Resources, "SETUPCFG", Confidence.Medium),
            new(MarkerArea.Resources, ".application", Confidence.Medium),
        ],
        [],
        "",
        "Installs per user from its .application manifest and has no silent switch, so Intune can't deploy it silently. Ask the vendor for an MSI or MSIX."),

        new("7-Zip SFX",
        [
            new(MarkerArea.Overlay, ";!@Install@!UTF-8!", Confidence.High),
            new(MarkerArea.Overlay, "7z\u00BC\u00AF'\u001C", Confidence.High),
        ],
        [
            new("/S", "Silent extraction and run, if the archive's config allows it."),
            new("-y", "Answers yes to the extraction prompts."),
        ], "", SfxNote),

        new("WinRAR SFX",
        [
            new(MarkerArea.Overlay, "Rar!\u001A\u0007", Confidence.High),
        ],
        [
            new("-s", "Silent extraction and run."),
        ], "", SfxNote),

        new("MSIX / AppX bundle",
        [
            new(MarkerArea.Tail, "AppxManifest.xml", Confidence.High),
            new(MarkerArea.Tail, "AppxBundleManifest.xml", Confidence.High),
        ],
        [],
        "",
        "Extract the package and add it to Intune as MSIX."),
    ];
}
