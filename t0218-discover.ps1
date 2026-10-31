$out = 'C:\Temp\t0218-discover.txt'
New-Item -ItemType Directory -Force C:\Temp | Out-Null
$r = 'C:\Users\RiyadhSarker\AppData\Local\orchestra-node\repos\packman'
$lines = @()
$lines += "elevated: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')
$lines += "user: $env:USERNAME  machine: $env:COMPUTERNAME"
$lines += "winrm: " + (Get-Service WinRM).Status
$lines += "uac: " + (Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System).EnableLUA + " consent=" + (Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System).ConsentPromptBehaviorAdmin
$lines += "Invoke-AppDeployToolkit.exe found:"
$lines += (Get-ChildItem $r -Recurse -Filter Invoke-AppDeployToolkit.exe -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object FullName)
$lines += "C:\Temp\Packman exists: " + (Test-Path C:\Temp\Packman)
$lines += "task exists: " + [bool](Get-ScheduledTask -TaskName Packman_RemoteTest -ErrorAction SilentlyContinue)
$lines += "settings:"
$lines += (Get-ChildItem "$env:APPDATA\Packman*" -ErrorAction SilentlyContinue | ForEach-Object FullName)
$lines | Set-Content $out
