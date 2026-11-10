# Changelog

All notable changes to Packman are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). A release is cut by pushing a `v<version>` tag; see
[CONTRIBUTING.md](CONTRIBUTING.md#releases).

## [Unreleased]

### Accessibility

- Status text marked as a live region is now announced by screen readers: generation and publish
  progress, results, errors and save confirmations. WPF records the setting but raises nothing by
  itself.
- Readiness rows, app tiles and page headers are exposed to screen readers with a name, for example
  "Detection: Ready, ok".
- Windows contrast themes are honoured: surfaces, text, lines and the accent take the system's own
  colours, and the app follows the theme being switched on or off while it runs.
- **System** theme follows a change of the Windows light/dark setting without a restart.
- In the light theme the selected-row bar, tab underline, progress fill and radio dot use a darker
  amber that holds 3:1 against every surface (the brand fill was 2.2:1 on white), and the chosen
  segment of a two-way toggle has a visible outline.

### Fixed

- Detection: a registry key pasted with its hive (`HKLM:\SOFTWARE\…`, `Computer\HKEY_LOCAL_MACHINE\…`)
  is no longer prefixed with the hive a second time; MSI product codes must be GUIDs and are sent as
  `{UPPER-CASE}`; versions must be one to four numbers; the standalone Upload page asks for the same
  fields as the wizard and offers "not equal"; a blank version pre-fills "File exists".
- Detection rules are marked "32-bit app on 64-bit" only for x86 packages. Every file rule used to
  be, which sent `%ProgramFiles%` paths of 64-bit apps to Program Files (x86).
- Upgrade repoints the script when the installer's file name has an apostrophe, and removes the
  previous installer when its name has a `$` or a backtick.
- Generation warns when no installer was found for the command it writes, and preflight blocks a
  script that names a file missing from `Files` or runs an installer with the command for the
  other kind (MSI/EXE).
- Settings ▸ Code signing lists only certificates that can sign code, so the sign-in
  certificate in the same store can no longer be picked by mistake.
- Code signing signs every unsigned PowerShell file in the package (the template's Extensions
  module was left unsigned), refuses a certificate that is expired, not yet valid, without a
  private key or not issued for code signing, names the timestamp server when it cannot be
  reached, and no longer falls back to DigiCert when the timestamp server is empty.
- Security: a package's `config.psd1` is never evaluated as PowerShell by the remote-test monitor;
  the folders above a custom staging folder must be administrator-only; line breaks in installer
  metadata cannot inject script; the upload log file is redacted like the console; copy and
  delete no longer follow junctions on the share.
- Generation adds `RequireAdmin` (and any other missing `$adtSession` entry) instead of skipping
  it; PSADT 4.2 templates dropped that entry.
- Deleting or replacing a package clears read-only attributes first; PSADT ships read-only files,
  which made the delete fail with "access denied".
- Console lines keep the order they were logged in across threads.
- An indeterminate progress bar sweeps instead of drawing as a full, static bar.
- The font stack loads as intended: Segoe UI Variable and Cascadia Mono where Windows has them, with
  the bundled Instrument Sans and JetBrains Mono as fallbacks. A pack URI in the family list had
  broken every name around it, so the UI always fell back to Segoe UI and Consolas.

- Remote test no longer reports a failed install as succeeded when the exit code is above
  0x7FFFFFFF (HRESULT and crash codes, `exit -1`) or cannot be read. A cancelled run reads as
  cancelled, and known exit codes are explained.
- **Replace package** builds the new package beside the existing one and swaps it in at the end; a
  cancel or failed copy leaves the existing package and its `packman.id` untouched. The prompt says
  what is discarded and whether the folder is linked to an Intune app.
- A source path that does not exist fails generation instead of producing a package without an
  installer; a path pasted with quotes is accepted, and an `.msi` whose product code cannot be read
  is still packaged as an MSI.
- Upgrade replaces only the package's own installer, not every file the script runs from `Files\`,
  and warns about the others and about an MSI/EXE switch. Preflight flags
  `{PRODUCT-CODE-PLACEHOLDER}`.
- The detection rule applied from Remote test survives the first visit to Configure.
- **Registry key exists** with a blank value name builds a key-exists rule instead of an error.
- The app detail page no longer saves detection rules it failed to load, which replaced the app's
  rules in Intune. A rule Intune rejects is put back and its editor reopened; incomplete rules are
  refused before saving. Republish can be cancelled from the detail page.
- Graph requests that can be replayed are retried on 408, 500 and 502 as well as 503 and 504.
  Report queries are retried as reads, and an unavailable status report no longer fails the app
  detail page.
- A sign-in that expires during a publish reports itself instead of failing inside a UI update.
- Publishing a package folder already linked to an app in the tenant asks before creating a second
  app. A failed publish says whether the half-created app was removed.
- All publish paths upload the package's icon; an upgraded package publishes with its own
  architecture. A new or rebuilt package starts Configure from the defaults again.
- Default groups from Settings are resolved by exact name; a name shared by several Entra groups is
  skipped with a warning instead of picking one.
- Closing Packman during a remote test or upgrade asks first, and the close no longer waits forever
  on a job that does not stop.
- The Application detail page's `detection.xml` check, which could never pass, is gone.

### Security

- Upgrade deletes the previous installer only when its name from the script is a plain file name
  inside the new package's `Files\`, and writes a new product code only when it is a braced GUID.
- When more than one package folder claims the same app, none is used for Republish content or
  Edit script; previously the newest date written in the (editable) `packman.id` won.
  `packman.id` ids with path or query characters are ignored.
- A per-package group that already exists is still reused, and the log now says so.

- Remote test stages packages in `C:\ProgramData\Packman` instead of `C:\Temp\Packman`. Before the
  copy the folder is made owner Administrators, inheritance off, SYSTEM and Administrators only; a
  folder there that other accounts could change is renamed aside, and the run stops if the folder
  cannot be secured. A logged-on-user run gets read access to its own package folder and write
  access to its console log only.
- Interactive sign-in asks only for read scopes (`User.Read`, `DeviceManagementApps.Read.All`).
  The write scope is requested when first needed (publishing and app changes); group and device
  scopes are requested only when the Advanced screen opens, and group features elsewhere point
  there until they are granted.
- Settings recommends a dedicated app registration while the shared Microsoft Graph Command Line
  Tools client is in use, and the manual documents how to create one.

### Changed

- `IntuneWinAppUtil.exe` is no longer shipped with Packman, because Microsoft's license terms do not
  allow redistributing it. With **IntuneWinAppUtil Path** empty, Packman downloads version 1.8.7 from
  Microsoft's repository on the first publish, checks its SHA-256, keeps it in
  `%LocalAppData%\Packman\Tools` and checks the hash again before every publish. A computer that
  cannot reach `raw.githubusercontent.com` needs the tool downloaded by hand and its path set in
  Settings.
- `Microsoft.Management.Infrastructure.Runtime.Win` is restored from nuget.org instead of the
  vendored `packages/` folder, for the same reason; the first restore on a machine needs nuget.org.
- CI no longer runs on pushes to `codex/**` branches.
- The Settings tab **Network paths** is now **Paths**.
- The Package layout tab no longer lists fixed **Application** and **Intune** rows; the example
  tree shows them.
- Package preview: the "MSI package" facts are gone, **Upgrade code** is shown, and a warning
  appears only where the MSI disagrees with the chosen architecture or install context.
- Script warnings from generation show on the **Package generated** card with an **Edit script**
  link, not only in the console.
- Preflight blocks publishing while a 4.2 template's Install or Uninstall phase is empty.
- On wide windows the form pages stay a centred 860px column and the side rail grows from 312px
  to 480px.
- The title bar pill names the tenant (`Connected · contoso`) instead of *Connected · your*; an
  app registration shows its tenant domain or ID.
- The Package step rail no longer shows the Installer / Output share / Intune connection readiness
  rows; it shows the package preview and what was read from the installer.
- An unrecognised EXE lists no switches instead of untested guesses. A recognised EXE also shows
  the setup program's architecture and whether it is signed.
- An MSI shows the options its dialogs set and its features, each with **Add**.
- Loading another installer replaces the name, manufacturer and version the previous installer
  filled in, and keeps values typed by hand.
- **Generate package** is disabled until the installer, name, manufacturer, version and output share
  are set; its tooltip names the first missing item.
- Package preflight runs on entering Configure and Review. The rail shows its state, Review lists the
  issues, and **Publish** stays disabled until the package is clean.
- Remote test runs the same preflight and lists issues as warnings in the log.
- The remote copy runs asynchronously and can be cancelled; cancelling ends robocopy's process tree
  and removes the test task from the target.
- The Settings connection test skips the device and user read checks until those scopes are granted.
- CI runs `./check.sh`, so every test program runs, not two of them.
- The README is a short landing page. The feature guide moved to `docs/MANUAL.md`, and the contributor
  sections (preview utility, releases, offline builds, project layout) moved to CONTRIBUTING.md.

- First-run setup asks one question, the packages folder, instead of five steps. Connecting to
  Intune and the package folders are set in Settings, which no longer has **Run setup again**.
- An empty **PSADT Template Path** or **IntuneWinAppUtil Path** uses the bundled copy, so only the
  packages folder is needed to start packaging.

### Removed

- Internal design drafts and review notes, and the tooling folders that generated them, ahead of
  publishing the source.

### Added

- `CODE_OF_CONDUCT.md`.
- **Package folder name** in Settings ▸ Package layout: a format of `%vendor%` and `%appName%` with
  at most one `\` (default `%vendor%_%appName%`, unchanged). The version is always its own
  subfolder; a format with `%appVersion%`, without `%appName%`, or with unknown tokens or invalid
  characters is refused and blocks **Save settings**. Upgrade puts the new version beside the
  existing one whatever the format is now, and linked packages are also found under
  `Vendor\App\Version`.
- One-click suggestions for extra folders (Documentation, Source, Detection) and an example package
  tree on the Package layout tab.
- Settings ▸ Paths: **Staging Folder on Target** (a local folder on Remote test targets; Packman
  stages in its own `Packman` subfolder there, empty uses `C:\ProgramData`), **Upload Logs Folder**
  with **Open logs folder** (falls back to the local folder when it cannot be written, and the
  console says so), and a read-only **PSADT Log Folder** read from the template's
  `Config\config.psd1`.
- PSADT 4.2 templates: the install and uninstall phases are found in
  `New-Variable -Name Install -Value { }` blocks as well as the 4.0/4.1
  `<Perform … tasks here>` markers, or under a `## MARK:` heading. The console logs the template's
  PSADT version and layout.
- Remote test on this computer: enter `localhost` (or this computer's name) and the run needs no
  WinRM. One administrator prompt per run starts a helper that stages the package and runs the
  same scheduled task, so the install still runs as SYSTEM or in your session.
- Console: resize from its top edge, collapse to the header (it reopens on a warning or error),
  warning and error counts, **Issues only**, and a pop-out into its own window. The state is kept
  as you move between pages.
- Bundled PSADT 4.1.8 template with its runtime (`Invoke-AppDeployToolkit.exe` and the
  `PSAppDeployToolkit` module), copied beside `Packman.exe`. Licenses are in THIRD-PARTY-NOTICES.md.
- `tests/BundledToolsTests.py`, run by `check.sh`: the template has its runtime, the script matches
  the runtime version, and `IntuneWinAppUtil.exe` is the pinned build.
- `tests/DocsLinkTests.py`, run by `check.sh`: checks relative links, `#` anchors, image alt text,
  heading levels, code fence languages, repo paths in code fences and leaked internal references in
  the tracked markdown. `--external` also fetches every web link before a release.
- **Before your first package** card on the Create page: output share, PSADT template,
  IntuneWinAppUtil and sign-in, each with an **Open settings** link, plus the EXE switches step. Its
  dismissal is saved in settings.
- **Cancel** for remote test runs.
- `tests/Packman.Tests` xunit project, run by `check.sh` on Windows.
- Tag-triggered release job: builds with the tag's version, signs `Packman.exe` and `Packman.dll`,
  and publishes a zip and its SHA256 as a GitHub Release.
- HOWTO instructions for checking the signature and hash of an `IntuneWinAppUtil.exe` of your own.

## [1.1.0]

Baseline for this changelog: the version in `Packman.csproj` before these entries. It was not tagged.
