# Third-party components

Packman's own source is distributed under [Apache 2.0](LICENSE). That license does not replace the terms of bundled fonts, editor assets, PSADT source or NuGet dependencies.

## Bundled assets

| Component | License and retained notices | Source |
| --- | --- | --- |
| Monaco Editor 0.52.2, including its bundled dependencies and icons | [MIT license](Packman/MonacoEditor/LICENSE), [third-party notices](Packman/MonacoEditor/ThirdPartyNotices.txt) | [Monaco Editor](https://github.com/microsoft/monaco-editor/tree/v0.52.2) |
| Instrument Sans, static instances | [SIL OFL 1.1](Packman/Fonts/InstrumentSans-OFL.txt) | [Instrument Sans](https://github.com/Instrument/instrument-sans) |
| JetBrains Mono, static instances | [SIL OFL 1.1](Packman/Fonts/JetBrainsMono-OFL.txt) | [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) |
| Martian Mono, static instances | [SIL OFL 1.1](Packman/Fonts/MartianMono-OFL.txt) | [Martian Mono](https://github.com/evilmartians/mono) |
| PSAppDeployToolkit 4.1.8 template with its runtime (`Invoke-AppDeployToolkit.exe`, the `PSAppDeployToolkit` module) | [LGPL 3.0](Packman/PSADT/COPYING.Lesser), [incorporated GPL 3.0](Packman/PSADT/COPYING.GPL), [module copy](Packman/PSADT/Application/PSAppDeployToolkit/COPYING.Lesser) | [PSAppDeployToolkit_Template_v4.zip, release 4.1.8](https://github.com/PSAppDeployToolkit/PSAppDeployToolkit/releases/tag/4.1.8) |
| PSAppDeployToolkit function help (names, synopses, parameter descriptions, docs links) in the editor's function catalog | [LGPL 3.0](Packman/PSADT/COPYING.Lesser), [notice](Packman/MonacoEditor/psadt-functions-NOTICE.txt) | [PSAppDeployToolkit](https://github.com/PSAppDeployToolkit/PSAppDeployToolkit) |

The font changes made for WPF are described in [Fonts/README.md](Packman/Fonts/README.md). The PSADT template and runtime are included as upstream released them, apart from the version numbers in `Invoke-AppDeployToolkit.ps1` and the Zero-Config MSI blocks Packman removes; preserve the upstream notices. The installed WebView2 runtime is obtained separately and retains its own distribution terms.

## Not distributed

Two Microsoft components are used but not redistributed by this repository, because their license terms grant use and not redistribution (section 4(e) of the Content Prep Tool terms says you will not "share, publish, rent, or lend the software"):

- **Microsoft Win32 Content Prep Tool** (`IntuneWinAppUtil.exe` 1.8.7, SHA-256 `c1ba45b5cb939e84af064bb7ff4b38fb3dfe33c8dc1078fd9b157672eae671f6`). Packman downloads it from [Microsoft-Win32-Content-Prep-Tool v1.8.7](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/tree/v1.8.7) on the first publish and runs it only while the hash matches. Microsoft's license terms in that repository apply to whoever downloads it.
- **Microsoft.Management.Infrastructure.Runtime.Win 3.0.0**, a dependency of the PowerShell SDK. It is restored from nuget.org instead of the vendored feed; [third-party/nuget-external.json](third-party/nuget-external.json) records it and its [license terms](third-party/nuget-notices/Microsoft.Management.Infrastructure.Runtime.Win-3.0.0/LICENSE.txt) are retained. A built Packman still contains its files, as any application that hosts PowerShell does; read those terms before redistributing a build.

## NuGet dependencies

The [machine-readable inventory](third-party/nuget-packages.json) records every package the solution restores, its version, SHA-256 digest, declared license, project URL and embedded notice paths. It includes build/test packages and runtime packs as well as application dependencies. Embedded texts are retained verbatim in [third-party/nuget-notices](third-party/nuget-notices).

Some NuGet packages declare a license expression or URL without embedding a notice file. The inventory preserves that distinction; it does not classify every dependency as Apache 2.0 or claim every package is used by the desktop runtime.

Regenerate and verify the inventory after changing the vendored feed or `third-party/nuget-external.json`:

```text
python scripts/update-third-party-notices.py
python scripts/update-third-party-notices.py --check
```

CI verifies the inventory. Desktop output includes the root license, this notice, the inventory and retained component notices. In the desktop archive, the asset folders (`Fonts`, `MonacoEditor` and `PSADT`) sit beside `Packman.exe`; omit the `Packman/` source-directory prefix from the links above to find those files.
