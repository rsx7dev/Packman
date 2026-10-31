# T-0213 node helper. Prints names, existence and hashes only; never ids or thumbprints.
$ErrorActionPreference = 'Stop'
$step = (Get-Content (Join-Path $PSScriptRoot 'step.txt') -Raw).Trim()
$work = 'C:\PackmanT0213'
$settings = Join-Path $env:LOCALAPPDATA 'Packman\appsettings.json'
$backup = "$settings.t0213-backup"
function Auth-Hash($path) {
    $a = (Get-Content $path -Raw | ConvertFrom-Json).Authentication | ConvertTo-Json -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($a)))).Replace('-', '').Substring(0, 12)
}
try {
switch ($step) {
 'prep' {
    New-Item -ItemType Directory -Force "$work\share", "$work\dl" | Out-Null
    if (-not (Test-Path "$work\PSADTTemplate\Invoke-AppDeployToolkit.ps1")) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $r = Invoke-RestMethod 'https://api.github.com/repos/PSAppDeployToolkit/PSAppDeployToolkit/releases/latest' -Headers @{ 'User-Agent' = 'packman-t0213' }
        $asset = $r.assets | Where-Object { $_.name -like '*Template*v4*.zip' } | Select-Object -First 1
        "release $($r.tag_name), asset $($asset.name)"
        $zip = "$work\dl\$($asset.name)"
        Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
        Expand-Archive $zip "$work\PSADTTemplate" -Force
    }
    "template script: $(Test-Path "$work\PSADTTemplate\Invoke-AppDeployToolkit.ps1"), toolkit folder: $(Test-Path "$work\PSADTTemplate\PSAppDeployToolkit")"
    if (Test-Path $settings) {
        "authentication hash before: $(Auth-Hash $settings)"
        $s = Get-Content $settings -Raw | ConvertFrom-Json
        "owner tool path exists: $(Test-Path $s.NetworkPaths.IntuneWinAppUtil)"
        Copy-Item $settings "$work\appsettings.copy-of-owner.json" -Force
        Move-Item $settings $backup -Force
        "settings moved aside"
    } else { "no settings file at the moment (backup present: $(Test-Path $backup))" }
 }
 'restore' {
    if (Test-Path $settings) { Remove-Item $settings -Force }
    Move-Item $backup $settings -Force
    "settings restored; authentication hash after: $(Auth-Hash $settings)"
 }
 'tree' {
    $root = "$work\share"
    Get-ChildItem $root -Directory | ForEach-Object { Get-ChildItem $_.FullName -Directory | ForEach-Object {
        $pkg = $_.FullName
        "package $($pkg.Substring($root.Length + 1))"
        Get-ChildItem $pkg -Force | ForEach-Object {
            if ($_.PSIsContainer) {
                $n = (Get-ChildItem $_.FullName -Recurse -Force -File | Measure-Object).Count
                "  $($_.Name)\  ($n files)"
                if ($_.Name -ne 'Application') { Get-ChildItem $_.FullName -Recurse -Force | ForEach-Object { "      " + $_.FullName.Substring($pkg.Length + 1) } }
            } else { "  $($_.Name)" }
        }
    } }
 }
 'intunewin' {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Get-ChildItem "$work\share" -Recurse -Filter *.intunewin | ForEach-Object {
        $z = [IO.Compression.ZipFile]::OpenRead($_.FullName)
        "$($_.FullName.Substring($work.Length + 7)) $($_.Length) bytes: " + (($z.Entries | ForEach-Object FullName | Where-Object { $_ -like '*Detection.xml' }) -join ',')
        $z.Dispose()
    }
 }
 default { "unknown step $step" }
}
} catch { "STEP FAILED: $($_.Exception.Message)" }
