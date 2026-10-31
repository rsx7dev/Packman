# Contributing to Packman

Packman is a Windows desktop application for preparing PSADT packages and managing Win32 applications in Intune. The community edition includes the workflows documented in [README.md](README.md). See [ROADMAP.md](ROADMAP.md) for the boundary between community work and planned commercial integrations.

## Build and validate

Use Windows with the .NET 10 SDK. Visual Studio is optional.

```powershell
dotnet restore Packman.sln
dotnet build Packman.sln -c Release --no-restore
dotnet run --file scripts/RenderUiPreviews.cs -c Release -- artifacts/ui-previews
```

Run every test with `./check.sh` from Git Bash. It runs the file-based test programs under `tests/`,
the Python catalog test, and on Windows the WPF and first-run checks and the xunit project
`tests/Packman.Tests`. CI runs the same script, so a test added to it runs in CI too.

The repository's NuGet feed is the vendored `packages/` directory. Keep package references and the vendored feed in sync when changing dependencies. Preserve their licenses and notices.

The standalone Windows preview utility renders the main views, both themes, Settings sections and Application Detail tabs. CI attaches the screenshots as `ui-previews` and launches/closes the packaged application. Previews use sample data and do not connect to a tenant or deploy an application. Add unit tests for new behavior to `tests/Packman.Tests`, and report other validation evidence in the pull request.

## Releases

1. Move the `[Unreleased]` entries in [CHANGELOG.md](CHANGELOG.md) under a new version heading.
2. Tag the commit `v<major>.<minor>.<patch>` (a `-suffix` such as `-beta.1` makes a pre-release)
   and push the tag. The version comes from the tag; `Packman.csproj` is not edited.
3. The `release` job in `.github/workflows/build.yml` builds with that version, signs
   `Packman.exe` and `Packman.dll`, and creates a GitHub Release with
   `Packman-<version>-win-x64.zip`, its `.sha256` and generated notes.

Signing needs two repository secrets: `SIGNING_CERT_PFX_BASE64` (the code-signing PFX, base64) and
`SIGNING_CERT_PASSWORD`. Without them the job fails rather than publish unsigned files. The workflow
comment shows the Azure Trusted Signing alternative.

## Making a change

For a bug, explain what you did, what happened and what you expected. For a larger feature, describe the packaging problem in an issue before building a new workflow.

Keep view state in view models, reusable behavior in services and shared appearance in `Themes/Styles.xaml`. Reuse the existing controls and dependency patterns. Avoid putting Graph calls or packaging logic in XAML event handlers.

Test behavior that can lose edits, alter generated scripts, change Graph payloads or affect cancellation and cleanup. For UI changes, inspect both themes with realistic long names and the minimum window size. Include validation results in the pull request and update the user instructions when the workflow changes.

Use a test tenant and disposable Windows device for deployment validation. Remove access tokens, certificate material, tenant identifiers and personal information from example files, screenshots and logs submitted publicly. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License and commercial work

Contributions to this repository are provided under its existing [Apache 2.0 license](LICENSE). Bundled dependencies retain their own terms. Paid integration implementations are maintained separately; the public core and its documented extension contracts remain usable without a commercial subscription.

The planned Pro products are not required to build or run the community app. Do not add unavailable paid actions to community navigation or publish proprietary integration code here.
