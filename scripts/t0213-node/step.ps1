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
 'seed' {
    $pkg = (Get-ChildItem "$work\share" -Directory | Select-Object -First 1 | ForEach-Object { Get-ChildItem $_.FullName -Directory } | Select-Object -First 1).FullName
    Set-Content "$pkg\Docs\note.txt" 'carry me forward'
    Set-Content "$pkg\Icon\keep.png" 'not really a png'
    $tool = (Get-Content $settings -Raw | ConvertFrom-Json).NetworkPaths.IntuneWinAppUtil
    & $tool -c "$pkg\Application" -s Invoke-AppDeployToolkit.exe -o "$pkg\Intune" -q | Out-Null
    "seeded; build exit code $LASTEXITCODE"
 }
 'final' {
    $root = "$work\share"
    $tool = (Get-Content $settings -Raw | ConvertFrom-Json).NetworkPaths.IntuneWinAppUtil
    "layout now saved: " + ((Get-Content $settings -Raw | ConvertFrom-Json).PackageLayout | ConvertTo-Json -Compress)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Get-ChildItem $root -Directory | ForEach-Object { Get-ChildItem $_.FullName -Directory | ForEach-Object {
        $pkg = $_.FullName
        if ($pkg -like '*test_two*') {
            & $tool -c "$pkg\Application" -s Invoke-AppDeployToolkit.exe -o "$pkg\Intune" -q | Out-Null
            "built test_two, exit code $LASTEXITCODE"
        }
        $rel = $pkg.Substring($root.Length + 1)
        "package " + $rel.Substring(0, [Math]::Min(60, $rel.Length))
        Get-ChildItem $pkg -Force | ForEach-Object {
            if ($_.PSIsContainer) {
                $n = (Get-ChildItem $_.FullName -Recurse -Force -File | Measure-Object).Count
                "  $($_.Name)\  ($n files)"
                if ($_.Name -ne 'Application') { Get-ChildItem $_.FullName -Recurse -Force -File | ForEach-Object { "      " + $_.Name } }
            } else { "  $($_.Name)" }
        }
        Get-ChildItem "$pkg\Intune" -Filter *.intunewin | ForEach-Object {
            $z = [IO.Compression.ZipFile]::OpenRead($_.FullName)
            "  intunewin $($_.Length) bytes, detection xml: " + (($z.Entries | Where-Object { $_.FullName -like '*Detection.xml' } | Measure-Object).Count -eq 1)
            $z.Dispose()
        }
    } }
    if (Test-Path $backup) {
        Remove-Item $settings -Force
        Move-Item $backup $settings -Force
        "settings restored; authentication hash after: $(Auth-Hash $settings)"
    } else { "NO BACKUP FOUND" }
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
