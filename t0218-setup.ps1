$ErrorActionPreference = 'Stop'
trap { $_ | Out-String | Set-Content C:\Temp\t0218-setup.txt; exit 1 }
New-Item -ItemType Directory -Force C:\Temp | Out-Null
$r = 'C:\Users\RiyadhSarker\AppData\Local\orchestra-node\repos\packman'
$qa = "$r\.orch\qa-output\Packman-QA-Orchestra_LinkTest\1.0.0"
$root = 'C:\Temp\t0218-src\T0218_T0218App\1.0.0'
Remove-Item 'C:\Temp\t0218-src' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $root | Out-Null
Copy-Item "$qa\Application" "$root\Application" -Recurse
$s = Get-Content "$r\Packman\PSADT\Application\Invoke-AppDeployToolkit.ps1" -Raw
$s = $s.Replace("AppVendor = ''", "AppVendor = 'T0218'").Replace("AppName = ''", "AppName = 'T0218App'").Replace("AppVersion = ''", "AppVersion = '1.0.0'")
$install = @'
    New-Item -ItemType Directory -Force "$env:ProgramFiles\T0218App" | Out-Null
    Copy-Item "$env:SystemRoot\System32\whoami.exe" "$env:ProgramFiles\T0218App\T0218App.exe" -Force
    Write-ADTLogEntry -Message "T0218 identity: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
    if (Test-Path C:\Temp\t0218-fail.flag) { Close-ADTSession -ExitCode 1603 }
'@
$uninstall = @'
    Remove-Item "$env:ProgramFiles\T0218App" -Recurse -Force -ErrorAction SilentlyContinue
'@
$s = $s.Replace('## <Perform Installation tasks here>', $install).Replace('## <Perform Uninstallation tasks here>', $uninstall)
Set-Content "$root\Application\Invoke-AppDeployToolkit.ps1" $s -Encoding UTF8
"ok" + (Get-ChildItem $root\Application | % Name) -join ',' | Set-Content C:\Temp\t0218-setup.txt
