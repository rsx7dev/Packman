# PSADT v4 Template

This is the template folder Packman copies when creating a package. It is PSADT 4.1.8 from the
official [PSAppDeployToolkit_Template_v4.zip](https://github.com/PSAppDeployToolkit/PSAppDeployToolkit/releases/tag/4.1.8),
complete with its runtime, and Packman uses it while **Settings → Network Paths → PSADT Template Path**
is empty.

## Structure

```text
PSADT/
  Application/                     <- the template, copied into each new package
    Invoke-AppDeployToolkit.ps1    <- v4 deploy script (Packman edits the metadata)
    Invoke-AppDeployToolkit.exe    <- launcher Intune runs
    PSAppDeployToolkit/            <- the module
    Assets/ Config/ Strings/       <- toolkit assets, configuration and UI strings
    Files/                         <- source installers are copied here
    SupportFiles/
```

Packman creates each package as `{Vendor}_{AppName}/{version}/` holding three
folders: `Application/` (this template), `Intune/` (the .intunewin is built here
at upload time) and `Icon/` (the archived app icon).

## Changes from upstream

`Invoke-AppDeployToolkit.ps1` has no Zero-Config MSI blocks (Packman writes the MSI install and
uninstall commands itself, so they would run twice). Everything else is as released.

## Updating

Download the new `PSAppDeployToolkit_Template_v4.zip`, replace `Application/` with its contents,
reapply the change above, and update the version in `THIRD-PARTY-NOTICES.md`.
`tests/BundledToolsTests.py` fails if the script and the runtime disagree on the version.
