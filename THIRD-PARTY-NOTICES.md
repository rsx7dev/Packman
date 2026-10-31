# Third-party components

Packman's own source is distributed under [Apache 2.0](LICENSE). That license does not replace the terms of bundled fonts, editor assets, PSADT source or NuGet dependencies.

## Bundled assets

| Component | License and retained notices | Source |
| --- | --- | --- |
| Monaco Editor 0.52.2, including its bundled dependencies and icons | [MIT license](Packman/MonacoEditor/LICENSE), [third-party notices](Packman/MonacoEditor/ThirdPartyNotices.txt) | [Monaco Editor](https://github.com/microsoft/monaco-editor/tree/v0.52.2) |
| Instrument Sans, static instances | [SIL OFL 1.1](Packman/Fonts/InstrumentSans-OFL.txt) | [Instrument Sans](https://github.com/Instrument/instrument-sans) |
| JetBrains Mono, static instances | [SIL OFL 1.1](Packman/Fonts/JetBrainsMono-OFL.txt) | [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) |
| Martian Mono, static instances | [SIL OFL 1.1](Packman/Fonts/MartianMono-OFL.txt) | [Martian Mono](https://github.com/evilmartians/mono) |
| PSAppDeployToolkit deploy-script template | [LGPL 3.0](Packman/PSADT/COPYING.Lesser), [incorporated GPL 3.0](Packman/PSADT/COPYING.GPL) | [PSAppDeployToolkit](https://github.com/PSAppDeployToolkit/PSAppDeployToolkit) |
| PSAppDeployToolkit function help (names, synopses, parameter descriptions, docs links) in the editor's function catalog | [LGPL 3.0](Packman/PSADT/COPYING.Lesser), [notice](Packman/MonacoEditor/psadt-functions-NOTICE.txt) | [PSAppDeployToolkit](https://github.com/PSAppDeployToolkit/PSAppDeployToolkit) |

The font changes made for WPF are described in [Fonts/README.md](Packman/Fonts/README.md). The PSADT template is included as editable source; users must supply the complete PSADT v4 runtime and preserve its upstream notices. IntuneWinAppUtil and the installed WebView2 runtime are obtained separately and retain their own distribution terms.

## NuGet dependencies

The [machine-readable inventory](third-party/nuget-packages.json) records every package in the offline feed, its version, SHA-256 digest, declared license, project URL and embedded notice paths. It includes build/test packages and runtime packs as well as application dependencies. Embedded texts are retained verbatim in [third-party/nuget-notices](third-party/nuget-notices).

Some NuGet packages declare a license expression or URL without embedding a notice file. The inventory preserves that distinction; it does not classify every dependency as Apache 2.0 or claim every package is used by the desktop runtime.

Regenerate and verify the inventory after changing the vendored feed:

```text
python scripts/update-third-party-notices.py
python scripts/update-third-party-notices.py --check
```

CI verifies the inventory. Desktop output includes the root license, this notice, the inventory and retained component notices. In the desktop archive, the asset folders (`Fonts`, `MonacoEditor` and `PSADT`) sit beside `Packman.exe`; omit the `Packman/` source-directory prefix from the links above to find those files.
