# Changelog

All notable changes to Packman are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). A release is cut by pushing a `v<version>` tag; see
[CONTRIBUTING.md](CONTRIBUTING.md#releases).

## [Unreleased]

### Fixed

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
  Tools client is in use, and the README documents how to create one.

### Changed

- The title bar pill names the tenant (`Connected · contoso`) instead of *Connected · your*; an
  app registration shows its tenant domain or ID.
- The Package step rail no longer shows the Installer / Output share / Intune connection readiness
  rows; it shows the package preview and what was read from the installer.
- An unrecognised EXE lists no switches instead of untested guesses. A recognised EXE also shows
  the setup program's architecture and whether it is signed.
- An MSI shows its package details (authoring tool, platform, languages, install scope, package
  code, minimum Windows Installer, created date), the options its dialogs set and its features,
  each with **Add**.
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

- Bundled PSADT 4.1.8 template with its runtime (`Invoke-AppDeployToolkit.exe` and the
  `PSAppDeployToolkit` module) and Microsoft's `IntuneWinAppUtil.exe` 1.8.7, copied beside
  `Packman.exe`. Licenses are in THIRD-PARTY-NOTICES.md.
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
