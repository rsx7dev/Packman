# Contributing to Packman

Packman is a Windows desktop application for preparing PSADT packages and managing Win32 applications in Intune. The community edition includes the workflows documented in the [manual](docs/MANUAL.md). See [ROADMAP.md](ROADMAP.md) for the boundary between community work and planned commercial integrations.

## Build and validate

Use Windows with the .NET 10 SDK. Visual Studio is optional.

```powershell
dotnet restore Packman.sln
dotnet build Packman.sln -c Release --no-restore
dotnet run --file scripts/RenderUiPreviews.cs -c Release -- artifacts/ui-previews
```

The solution holds the desktop application and its xunit project, `tests/Packman.Tests`.

Run every test with `./check.sh` from Git Bash. On Linux and macOS it restores, builds and runs the
tenant-free file-based checks (report parser, CSV, package links, setup, installer insight,
settings template, script editor), the Python catalog test and the documentation check
(`tests/DocsLinkTests.py`: links, images, heading levels, command paths and leaked internal
references in the Markdown files). On Windows it also builds Packman in Release, runs the ViewModel,
WPF and first-run checks, and runs `dotnet test tests/Packman.Tests/Packman.Tests.csproj`. Windows
CI runs `./check.sh` through bash, so CI and a local run cover the same tests, and a test added to
it runs in CI too. Portable checks alone do not verify the Windows application. All commands
propagate failures.

The repository's NuGet feed is the vendored `packages/` directory. Keep package references and the vendored feed in sync when changing dependencies. Preserve their licenses and notices.

The standalone Windows preview utility renders the main views, both themes, Settings sections and Application Detail tabs. CI attaches the screenshots as `ui-previews` and launches/closes the packaged application. Previews use sample data and do not connect to a tenant or deploy an application. Add unit tests for new behavior to `tests/Packman.Tests`, and report other validation evidence in the pull request.

## Refreshing the documentation screenshots

The screenshots in the README and the [manual](docs/MANUAL.md) are renders of the real WPF views
with sample data (Contoso Reader 4.2.0, TEST-PC-01). Only `scripts/RenderUiPreviews.cs` produces
them; it runs on Windows, and never authenticates, uploads or runs a deployment. Do not screenshot
a running Packman: it shows the tenant you are signed in to.

The utility builds the main view model from the settings of the Windows user it runs as, so the
share paths, tenant ID and client ID configured on a working machine would end up in the images.
Take the PNGs from the `ui-previews` artifact of a Windows CI run, or render on a clean Windows
account or VM that has never run Packman.

1. Render the previews. The generated-package views print a path under the temp folder, so point
   `TMP` and `TEMP` at a neutral folder first and your user name stays out of the images:

   ```powershell
   $env:TMP = $env:TEMP = 'C:\Sample'
   New-Item -ItemType Directory -Force $env:TMP | Out-Null
   dotnet run --file scripts/RenderUiPreviews.cs -c Release -- artifacts/ui-previews
   ```

2. Open every PNG you are about to publish and confirm it shows only sample data: no real names,
   share paths, tenant IDs, GUIDs, user names or machine names.
3. Copy the renders that the documentation uses into `docs/images`, renaming them:

   ```powershell
   $renders = 'artifacts/ui-previews'
   $docs = 'docs/images'
   $names = [ordered]@{
       'Light-01-package'                  = 'create-package-light'
       'Light-02-package-generated'        = 'package-generated-light'
       'Light-01-upgrade'                  = 'upgrade-package-light'
       'Light-03-configure'                = 'configure-light'
       'Light-04-review'                   = 'review-light'
       'Light-05-upload'                   = 'upload-to-intune-light'
       'Light-06-remote-test'              = 'remote-test-light'
       'Light-07-applications'             = 'applications-light'
       'Light-08-detail-overview'          = 'application-overview-light'
       'Dark-08-detail-deployment'         = 'application-deployment-dark'
       'Light-09-advanced'                 = 'advanced-light'
       'Light-10-settings-TabAuth'         = 'settings-authentication-light'
       'Light-10-settings-TabNetworkPaths' = 'settings-network-paths-light'
   }
   foreach ($render in $names.Keys) {
       Copy-Item (Join-Path $renders "$render.png") (Join-Path $docs "$($names[$render]).png") -Force
   }
   ```

4. Embed each image under the section named below, with alt text that says the view, its state and
   the theme, for example `![Remote test page in the light theme, with sample computer TEST-PC-01](images/remote-test-light.png)`.
   Only embed files that exist in `docs/images`: `python3 tests/DocsLinkTests.py` fails on a
   missing image or empty alt text.

| File in `docs/images` | Where it is used |
|---|---|
| `create-package-light.png` | README hero; manual, Creating a package |
| `package-generated-light.png` | Manual, Creating a package, after generation |
| `upgrade-package-light.png` | Manual, Upgrading a package to a new version |
| `configure-light.png` | Manual, Step 2 — Configure |
| `review-light.png` | Manual, Step 3 — Review & publish |
| `upload-to-intune-light.png` | Manual, Upload to Intune (standalone) |
| `remote-test-light.png` | Manual, Remote testing |
| `applications-light.png` | Manual, Navigating the list |
| `application-overview-light.png` | Manual, Application detail |
| `application-deployment-dark.png` | README dark example; manual, Application detail |
| `advanced-light.png` | Manual, Advanced — directory tools |
| `settings-authentication-light.png` | Manual, Authentication |
| `settings-network-paths-light.png` | Manual, Network paths |

The script editor (WebView2) is a native window that WPF cannot render, so the manual describes
it in text.

## Offline builds

`NuGet.config` at the repo root points restore at `packages/`, a folder of `.nupkg`
files checked into the repo — the project's full dependency closure, transitive
packages included. The config opens with `<clear />`, so restore ignores nuget.org and
any feed configured on the machine. A fresh clone builds and runs with no network
access.

The .NET 10 SDK and the WebView2 Runtime from [Requirements](README.md#requirements) still have
to be installed on the machine; neither ships as a NuGet package.

### Publishing offline

`packages/` also carries the `win-x64` runtime packs, so both publish modes work with
no network:

```bash
dotnet publish Packman/Packman.csproj -r win-x64 --self-contained false -c Release
```

```bash
dotnet publish Packman/Packman.csproj -r win-x64 --self-contained true -c Release
```

Self-contained produces a ~170 MB folder that needs no .NET install on the target. It
pulls in the ASP.NET Core runtime pack as well as the desktop one, because
`System.Management.Automation` carries a framework reference to `Microsoft.AspNetCore.App`.

Only `win-x64` is vendored. Another RID (`win-arm64`, `win-x86`) means re-vendoring
with that RID in the restore command below.

### Adding or updating a package

Do it somewhere with internet, then re-vendor:

```powershell
$feed = "https://api.nuget.org/v3/index.json"
dotnet restore Packman.sln --source $feed --packages .\obj\pkgstage
dotnet restore Packman\Packman.csproj -r win-x64 --source $feed --packages .\obj\pkgstage
Get-ChildItem .\obj\pkgstage -Recurse -Filter *.nupkg | Copy-Item -Destination .\packages
```

`--source` bypasses the offline config for those commands. The second restore is what
picks up the runtime packs — skip it and publishing breaks offline while building still
works, which is an easy thing not to notice.

Commit the new `.nupkg` files together with the `.csproj` change, then confirm the
result still works with no network:

```bash
dotnet restore Packman.sln --force
```

## Releases

1. Run `python3 tests/DocsLinkTests.py --external` and fix any link it reports, so the published
   documentation points at pages that still exist.
2. Move the `[Unreleased]` entries in [CHANGELOG.md](CHANGELOG.md) under a new version heading.
3. Tag the commit `v<major>.<minor>.<patch>` (a `-suffix` such as `-beta.1` makes a pre-release)
   and push the tag. The version comes from the tag; `Packman.csproj` is not edited.
4. The `release` job in `.github/workflows/build.yml` builds with that version, signs
   `Packman.exe` and `Packman.dll`, and creates a GitHub Release with
   `Packman-<version>-win-x64.zip`, its `.sha256` and generated notes.

Signing needs two repository secrets: `SIGNING_CERT_PFX_BASE64` (the code-signing PFX, base64) and
`SIGNING_CERT_PASSWORD`. Without them the job fails rather than publish unsigned files. The workflow
comment shows the Azure Trusted Signing alternative.

## Project layout

.NET 10 / WPF (`net10.0-windows`), MVVM, no DI container. Authentication via MSAL with the
Windows broker. Intune access over the Microsoft Graph beta endpoint
(`deviceAppManagement/mobileApps`). Remote testing over PowerShell remoting
(`System.Management.Automation`). Script editing hosted in WebView2 with Monaco.

```text
Packman/
  Models/         settings, application info, detection rules, groups, return codes
  Services/       PSADT generation, upgrade, code signing, remote test, detection
                  discovery, syntax validation, settings, theme, logging
    Intune/       Graph client and upload pipeline (blob upload, detection rules,
                  assignments, supersedence, advanced directory lookups)
  ViewModels/     one per screen and wizard step, plus the shared publish overlay
                  and editor session
  Views/          XAML screens and wizard steps
    Controls/     shared headings, assignment picker, return-code editor, publishing
                  status, and six focused Settings sections
  Helpers/        the PSADT script editor (PsadtScript), package paths, MSI/EXE
                  metadata, icon extraction, converters
  Themes/         light/dark palettes, styles, icons
  Fonts/          embedded UI fonts
  MonacoEditor/   the editor host page and Monaco assets
  PSADT/          bundled PSADT v4 template (script only)
  Tools/          extract-psadt-functions.py, regenerates MonacoEditor/psadt-functions.json
tests/            file-based test programs, Python checks and the xunit project Packman.Tests
docs/             the manual, the UI design notes and the documentation screenshots
scripts/          native Windows preview utility and third-party inventory tooling
packages/         vendored .nupkg files for offline restore
.github/workflows/build.yml   restore, build, native previews and app startup on windows-latest
```

## Making a change

For a bug, explain what you did, what happened and what you expected. For a larger feature, describe the packaging problem in an issue before building a new workflow.

Keep view state in view models, reusable behavior in services and shared appearance in `Themes/Styles.xaml`. Reuse the existing controls and dependency patterns. Avoid putting Graph calls or packaging logic in XAML event handlers.

Test behavior that can lose edits, alter generated scripts, change Graph payloads or affect cancellation and cleanup. For UI changes, inspect both themes with realistic long names and the minimum window size. Include validation results in the pull request and update the [manual](docs/MANUAL.md) when the workflow or a label changes.

Use a test tenant and disposable Windows device for deployment validation. Remove access tokens, certificate material, tenant identifiers and personal information from example files, screenshots and logs submitted publicly. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License and commercial work

Contributions to this repository are provided under its existing [Apache 2.0 license](LICENSE). Bundled dependencies retain their own terms. Paid integration implementations are maintained separately; the public core and its documented extension contracts remain usable without a commercial subscription.

The planned Pro products are not required to build or run the community app. Do not add unavailable paid actions to community navigation or publish proprietary integration code here.
