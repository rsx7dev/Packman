& {
    try {
$taskName = 'Packman_RemoteTest'
$consoleLog = 'C:\Temp\Packman\T0218_T0218App_1.0.0\Packman_Console.log'
$configPath = 'C:\Temp\Packman\T0218_T0218App_1.0.0\Application\Config\config.psd1'
$workingDirectory = 'C:\Temp\Packman\T0218_T0218App_1.0.0\Application'
$neverRan = 267011
$ErrorActionPreference = 'Continue'

# PSADT log folders: the package's own config first, the toolkit defaults after it.
# A non-admin run (user context) lands in LogPathNoAdminRights, so both are watched.
$logDirs = @("$env:SystemRoot\Logs\Software", "$env:ProgramData\Logs\Software")
if (Test-Path -LiteralPath $configPath) {
    try {
        $toolkit = (Import-PowerShellDataFile -LiteralPath $configPath).Toolkit
        # Names the config may use for expansion, in both the $env: and legacy $envX forms.
        $envWinDir = $env:SystemRoot; $envSystemRoot = $env:SystemRoot; $envProgramData = $env:ProgramData
        $envProgramFiles = $env:ProgramFiles; $envLocalAppData = $env:LOCALAPPDATA; $envAppData = $env:APPDATA
        $envTemp = [IO.Path]::GetTempPath().TrimEnd('\')
        foreach ($key in 'LogPath', 'LogPathNoAdminRights') {
            $value = $toolkit[$key]
            if (-not $value) { continue }
            $expanded = $ExecutionContext.InvokeCommand.ExpandString($value)
            if ($expanded -and ($logDirs -notcontains $expanded)) { $logDirs = @($expanded) + $logDirs }
        }
        if ($toolkit['CompressLogs']) { "WARNING: CompressLogs is on in config.psd1; PSADT writes to a temp folder and zips at the end, so no live log output is available." }
    }
    catch { "WARNING: Could not read $configPath ($($_.Exception.Message)); watching the default PSADT log folders." }
}
"Watching PSADT logs in: $($logDirs -join '; ')"

$logFilter = '*_PSAppDeployToolkit_*.log'
function Get-PsadtLogs {
    foreach ($dir in $logDirs) {
        if (Test-Path -LiteralPath $dir) {
            Get-ChildItem -LiteralPath $dir -Filter $logFilter -File -Recurse -ErrorAction SilentlyContinue
        }
    }
}

# Bytes already in each file before the run; only what follows is shown.
$offsets = @{}
$buffers = @{}
foreach ($f in Get-PsadtLogs) { $offsets[$f.FullName] = $f.Length }
Remove-Item -LiteralPath $consoleLog -Force -ErrorAction SilentlyContinue

# PSADT holds its log open, so read through a shared handle from the last offset.
function Read-NewText([string]$path) {
    $offset = 0
    if ($offsets.ContainsKey($path)) { $offset = $offsets[$path] }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -le $offset) { return $null }
        $null = $stream.Seek($offset, [IO.SeekOrigin]::Begin)
        $bytes = New-Object byte[] ([int]($stream.Length - $offset))
        $read = $stream.Read($bytes, 0, $bytes.Length)
        $offsets[$path] = $offset + $read
        return [Text.Encoding]::UTF8.GetString($bytes, 0, $read)
    }
    finally { $stream.Dispose() }
}

# CMTrace entries carry their severity; Legacy lines are one entry per line.
$entryPattern = [regex]'(?s)<!\[LOG\[(?<msg>.*?)\]LOG\]!><time="[^"]*" date="[^"]*" component="[^"]*" context="[^"]*" type="(?<type>\d)"[^>]*>\r?\n?'
function Emit-PsadtEntries([string]$path, [bool]$final) {
    $text = $buffers[$path]
    if (-not $text) { return }
    if ($text.Contains('<![LOG[')) {
        $last = 0
        foreach ($m in $entryPattern.Matches($text)) {
            $msg = $m.Groups['msg'].Value.TrimEnd()
            switch ($m.Groups['type'].Value) {
                '2' { "WARNING: $msg" }
                '3' { "ERROR: $msg" }
                default { $msg }
            }
            $last = $m.Index + $m.Length
        }
        $buffers[$path] = $text.Substring($last)
        return
    }
    $lines = $text -split "`r?`n"
    $complete = $lines.Length - 1
    if ($final) { $complete = $lines.Length }
    for ($i = 0; $i -lt $complete; $i++) {
        $line = $lines[$i]
        if (-not $line) { continue }
        if ($line -match '\] \[Error\] ::') { "ERROR: $line" }
        elseif ($line -match '\] \[Warning\] ::') { "WARNING: $line" }
        else { $line }
    }
    if ($final) { $buffers[$path] = '' } else { $buffers[$path] = $lines[-1] }
}

function Pump-Output([bool]$final) {
    if (Test-Path -LiteralPath $consoleLog) {
        $new = Read-NewText $consoleLog
        if ($new) { $new.TrimEnd() }
    }
    foreach ($f in Get-PsadtLogs) {
        # A log that already existed but was only found now (its folder appeared) is not ours.
        if (-not $offsets.ContainsKey($f.FullName) -and $f.LastWriteTime -lt $started) { $offsets[$f.FullName] = $f.Length; continue }
        $new = Read-NewText $f.FullName
        if ($new) { $buffers[$f.FullName] += $new }
        if ($new -or $final) { Emit-PsadtEntries $f.FullName $final }
    }
}

$user = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $user) { throw 'No user is logged on to the target computer' }
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive
$contextLabel = $user
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c Invoke-AppDeployToolkit.exe Install -DeployMode Silent > "C:\Temp\Packman\T0218_T0218App_1.0.0\Packman_Console.log" 2>&1' -WorkingDirectory $workingDirectory
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
$started = (Get-Date).AddSeconds(-5)
Start-ScheduledTask -TaskName $taskName
"Deployment task started as $contextLabel"
$elapsed = 0
while ($elapsed -lt 3600) {
    Start-Sleep -Seconds 1
    $elapsed += 1
    Pump-Output $false
    $state = (Get-ScheduledTask -TaskName $taskName).State
    $result = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
    if ($state -eq 'Ready' -and $result -ne $neverRan) { break }
    if ($state -ne 'Running' -and $result -eq $neverRan -and $elapsed -ge 120) { break }
}
$exitCode = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
# The log is flushed a moment after the process exits.
Start-Sleep -Seconds 1
Pump-Output $true
if ($elapsed -ge 3600) {
    "ERROR: Deployment timed out after 60 minutes"
    $exitCode = -3
}
elseif ($exitCode -eq $neverRan) {
    "ERROR: The deployment task never started on the target"
    $exitCode = -2
}
elseif ($exitCode -eq 9009) {
    "ERROR: cmd.exe could not find the command. Check that the command line names a file in $workingDirectory."
}
"PACKMAN_EXIT_CODE:$exitCode"
    } catch { "ERROR: $($_.Exception.Message)" }
} *>&1 | ForEach-Object {
    $line = if ($_ -is [Management.Automation.ErrorRecord]) { "ERROR: $_" } else { "$_" }
    [IO.File]::AppendAllText('C:\Temp\t0218-wrapped.log', $line + "`n")
}