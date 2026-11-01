# Changelog

All notable changes to Packman are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). A release is cut by pushing a `v<version>` tag; see
[CONTRIBUTING.md](CONTRIBUTING.md#releases).

## [Unreleased]

### Security

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

- Loading another installer replaces the name, manufacturer and version the previous installer
  filled in, and keeps values typed by hand.
- **Generate package** is disabled until the installer, name, manufacturer, version, output share and
  PSADT template are set; its tooltip names the first missing item.
- Package preflight runs on entering Configure and Review. The rail shows its state, Review lists the
  issues, and **Publish** stays disabled until the package is clean.
- Remote test runs the same preflight and lists issues as warnings in the log.
- The remote copy runs asynchronously and can be cancelled; cancelling ends robocopy's process tree
  and removes the test task from the target.
- The Settings connection test skips the device and user read checks until those scopes are granted.
- CI runs `./check.sh`, so every test program runs, not two of them.
- The README is a short landing page. The feature guide moved to `docs/MANUAL.md`, and the contributor
  sections (preview utility, releases, offline builds, project layout) moved to CONTRIBUTING.md.

### Removed

- Internal design drafts and review notes, and the tooling folders that generated them, ahead of
  publishing the source.

### Added

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
- HOWTO instructions for getting `IntuneWinAppUtil.exe` from Microsoft and checking its signature
  and hash.

## [1.1.0]

Baseline for this changelog: the version in `Packman.csproj` before these entries. It was not tagged.
