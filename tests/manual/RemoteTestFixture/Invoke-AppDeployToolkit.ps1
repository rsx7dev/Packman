[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')] [string]$DeploymentType = 'Install',
    [ValidateSet('Auto', 'Interactive', 'NonInteractive', 'Silent')] [string]$DeployMode = 'Auto'
)
$ErrorActionPreference = 'Stop'
$adtSession = @{
    AppVendor = 'Orchestra'
    AppName = 'PackmanLocalProbe'
    AppVersion = '1.0.0'
    AppArch = 'x64'
    AppLang = 'EN'
    AppRevision = '01'
    AppScriptVersion = '1.0.0'
    AppScriptDate = '2026-10-31'
    AppScriptAuthor = 'Orchestra'
    AppSuccessExitCodes = @(0)
    AppRebootExitCodes = @(1641, 3010)
    RequireAdmin = $true
}
Import-Module "$PSScriptRoot\PSAppDeployToolkit\PSAppDeployToolkit.psd1" -Force
$adtSession = Open-ADTSession @adtSession -DeploymentType $DeploymentType -DeployMode $DeployMode -PassThru
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    Write-ADTLogEntry -Message "PROBE identity=$($identity.Name); SID=$($identity.User.Value); mode=$DeployMode; operation=$DeploymentType"
    if ($identity.User.Value -ne 'S-1-5-18') { throw 'The test must run as SYSTEM.' }
    $destination = "$env:ProgramFiles\PackmanLocalProbe"
    if ($DeploymentType -eq 'Install') {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item "$env:SystemRoot\System32\whoami.exe" "$destination\PackmanLocalProbe.exe"
        Write-ADTLogEntry -Message "PROBE installed $destination\PackmanLocalProbe.exe; returning 3010 without restarting"
        Close-ADTSession -ExitCode 3010
    } else {
        Remove-Item -LiteralPath $destination -Recurse -Force
        Write-ADTLogEntry -Message "PROBE removed $destination; returning 0"
        Close-ADTSession -ExitCode 0
    }
} catch {
    Write-ADTLogEntry -Message "PROBE ERROR: $_" -Severity 3
    Close-ADTSession -ExitCode 60001
}
