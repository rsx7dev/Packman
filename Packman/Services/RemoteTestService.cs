using Packman.Helpers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Packman.Services;

/// <summary>
/// Deploys a PSADT v4 package to a test machine over WinRM, or to this PC without it.
///
/// WinRM is the transport only (this PC needs none: the same task script runs in an
/// elevated local PowerShell instead); the install runs from a one-shot scheduled task. A
/// remote session runs as the connecting admin, not as SYSTEM, and the two differ in
/// %TEMP%, HKCU and network identity, so it would not match what Intune does.
///
/// The task runs the same command line Intune will run — Invoke-AppDeployToolkit.exe
/// plus the chosen -DeployMode — from the staged Application folder. The launcher
/// starts Windows PowerShell hidden without forwarding its output, so live output
/// comes from tailing the PSADT log the toolkit writes, not from stdout.
/// </summary>
public class RemoteTestService
{
    private const string RemoteBasePath = @"C:\Temp\Packman";
    private const string TaskName = "Packman_RemoteTest";
    private const string ExitCodeSentinel = "PACKMAN_EXIT_CODE:";

    // /MIR mirrors, /MT:16 multithreaded, /J unbuffered, /NOOFFLOAD skips the ODX
    // attempt (unusable over SMB), /R:2 /W:5 replaces the 1M-retry defaults, and
    // /NP /NFL /NDL drops per-file output, which otherwise dominates the runtime.
    private const string RobocopyOptions = "/MIR /MT:16 /J /NOOFFLOAD /R:2 /W:5 /NP /NFL /NDL /NJH";

    // SCHED_S_TASK_HAS_NOT_RUN: registered but never produced a result.
    private const int NeverRan = 267011;

    /// <summary>The deployment verbs PSADT accepts.</summary>
    public static readonly IReadOnlySet<string> DeploymentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "Install", "Uninstall", "Repair" };

    /// <summary>PSADT/MSI success codes; 3010 and 1641 mean reboot required.</summary>
    public static bool IsSuccess(int exitCode) => exitCode is 0 or 3010 or 1641;

    /// <summary>Computer names only: the value reaches a UNC path and a remote script.</summary>
    public static bool IsValidComputerName(string computerName) =>
        !string.IsNullOrWhiteSpace(computerName) &&
        Regex.IsMatch(computerName, @"^[A-Za-z0-9][A-Za-z0-9\.\-]{0,62}$");

    /// <summary>
    /// Runs <paramref name="commandLine"/> — the install or uninstall command exactly as
    /// Intune would, e.g. <c>Invoke-AppDeployToolkit.exe Install -DeployMode Silent</c> —
    /// from the staged package and returns the exit code. <paramref name="deploymentType"/>
    /// only labels the run.
    /// <paramref name="runAsUser"/>: false runs as SYSTEM (what Intune does), true runs in
    /// the logged-on user's session. <paramref name="copyProgress"/> reports 0-100, then null.
    /// <paramref name="local"/> runs on this PC instead of over WinRM and asks for elevation
    /// (UAC) when Packman is not elevated; <paramref name="computerName"/> is then only a label.
    /// Throws PSRemotingTransportException when WinRM is unreachable.
    /// </summary>
    public int Deploy(string computerName, string sourcePath, string deploymentType, string commandLine,
        bool cleanupAfterDeploy, bool runAsUser, Action<string> output,
        Action<int?>? copyProgress = null, bool local = false)
    {
        if (!IsValidComputerName(computerName))
            throw new ArgumentException($"'{computerName}' is not a valid computer name", nameof(computerName));

        if (!DeploymentTypes.Contains(deploymentType))
            throw new ArgumentException($"'{deploymentType}' is not a valid deployment type", nameof(deploymentType));

        // Reaches a cmd.exe command line inside the task; one line, nothing else.
        commandLine = (commandLine ?? "").Trim();
        if (commandLine.Length == 0 || commandLine.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("The command line must be a single non-empty line", nameof(commandLine));

        output("========================================");
        output(local ? "Packman Remote Test (this PC)" : "Packman Remote Test (WinRM)");
        output("========================================");
        output($"Target: {computerName}");
        output($"Source: {sourcePath}");
        output($"Type: {deploymentType}");
        output($"Command: {commandLine}");
        output($"Run as: {(runAsUser ? "Logged-on user" : "NT AUTHORITY\\SYSTEM")}");
        output("");

        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Source path not found: {sourcePath}");

        string relativeApplicationPath;
        if (File.Exists(PsadtLayout.ScriptPath(sourcePath)))
        {
            relativeApplicationPath = PsadtLayout.ApplicationFolderName;
            output($"[OK] Found {PsadtLayout.ScriptName} in Application subfolder");
        }
        else if (File.Exists(Path.Combine(sourcePath, PsadtLayout.ScriptName)))
        {
            relativeApplicationPath = "";
            output($"[OK] Found {PsadtLayout.ScriptName} in root folder");
        }
        else
        {
            throw new FileNotFoundException($"{PsadtLayout.ScriptName} not found in package");
        }

        // A script-only template has no launcher; cmd would report 9009 after the copy.
        var runsLauncher = commandLine.StartsWith(PsadtLayout.SetupFileName, StringComparison.OrdinalIgnoreCase)
                        || commandLine.StartsWith($".\\{PsadtLayout.SetupFileName}", StringComparison.OrdinalIgnoreCase);
        if (runsLauncher && !File.Exists(Path.Combine(sourcePath, relativeApplicationPath, PsadtLayout.SetupFileName)))
            throw new FileNotFoundException(
                $"{PsadtLayout.SetupFileName} is missing from the package. The PSADT template needs the full v4 runtime, not just the script.");

        using var runspace = local ? null : OpenRemoteSession(computerName, runAsUser, output);

        // Layout is ...\Manufacturer_AppName\Version; combine both so packages don't mix
        // and a re-run only copies what differs.
        var sourceDir = new DirectoryInfo(sourcePath);
        string packageName = SanitiseFolderName(sourceDir.Parent?.Parent != null
            ? $"{sourceDir.Parent.Name}_{sourceDir.Name}"
            : sourceDir.Name);
        string remotePackagePath = $@"{RemoteBasePath}\{packageName}";
        string stagedPath = local ? remotePackagePath : $@"\\{computerName}\C$\Temp\Packman\{packageName}";

        output($"Copying files to {stagedPath}...");
        // Locally the copy runs inside the elevated script, so a staging folder only an admin can write works too.
        if (!local)
        {
            var copyTimer = Stopwatch.StartNew();
            CopyWithRobocopy(sourcePath, stagedPath, copyProgress);
            copyTimer.Stop();
            copyProgress?.Invoke(null);

            var copiedFiles = Directory.GetFiles(stagedPath, "*", SearchOption.AllDirectories);
            double sizeMb = copiedFiles.Sum(f => new FileInfo(f).Length) / 1048576.0;
            output($"Package size: {Math.Round(sizeMb, 2)} MB");
            output($"[OK] {copiedFiles.Length} files in sync after {Math.Round(copyTimer.Elapsed.TotalSeconds, 1)} seconds");
        }

        // Intune runs the command line from the content folder, so the task does too.
        string workingDirectory = relativeApplicationPath.Length == 0 ? remotePackagePath : $@"{remotePackagePath}\{relativeApplicationPath}";
        string configPath = $@"{workingDirectory}\Config\config.psd1";
        string consoleLog = $@"{remotePackagePath}\Packman_Console.log";

        output("");
        output($"Registering scheduled task '{TaskName}' on {(local ? "this PC" : "target")}...");
        output($"Working directory: {workingDirectory}");
        output("");

        int exitCode = -1;
        void Emit(string chunk)
        {
            foreach (string rawLine in chunk.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith(ExitCodeSentinel))
                    int.TryParse(line.AsSpan(ExitCodeSentinel.Length), out exitCode);
                else
                    output(line);
            }
        }

        string taskScript = BuildTaskScript(runAsUser, commandLine, workingDirectory, consoleLog, configPath);
        if (local)
        {
            // Staging, task and cleanup share one elevation; the cleanup lines therefore precede "Exit code".
            string cleanupScript = cleanupAfterDeploy ? BuildLocalCleanupScript(stagedPath) : "";
            RunElevated(BuildLocalStageScript(sourcePath, stagedPath) + taskScript + cleanupScript, output, Emit);
        }
        else
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(taskScript);

            var stdout = new PSDataCollection<PSObject>();
            stdout.DataAdded += (s, e) =>
            {
                string? chunk = stdout[e.Index]?.ToString();
                if (!string.IsNullOrEmpty(chunk)) Emit(chunk);
            };
            ps.Streams.Error.DataAdded += (s, e) => output($"ERROR: {ps.Streams.Error[e.Index]}");

            ps.Invoke<object, PSObject>(null, stdout, null);
        }

        output("");
        output($"Exit code: {exitCode}");
        if (InstallErrorText.Describe(exitCode) is { } meaning)
            output($"Meaning: {meaning}");
        if (exitCode == -1)
            output("WARNING: The remote monitor returned no exit code; check the PSADT log on the target.");

        if (cleanupAfterDeploy && !local)
        {
            output("");
            output("Cleaning up...");
            try
            {
                Directory.Delete(stagedPath, true);
                output("[OK] Cleanup completed");
            }
            catch (Exception ex)
            {
                output($"WARNING: Cleanup failed: {ex.Message}");
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Runs the task script in an elevated Windows PowerShell on this PC, prompting for UAC
    /// when Packman is not elevated. An elevated process cannot be redirected, so the script
    /// appends its output to a file, which is tailed here until the process ends.
    /// </summary>
    internal virtual bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal virtual void RunElevated(string taskScript, Action<string> output, Action<string> emit)
    {
        string outputFile = Path.Combine(Path.GetTempPath(), $"Packman_Local_{Guid.NewGuid():N}.log");
        File.WriteAllText(outputFile, "");

        // A terminating error ends the pipeline before it reaches the file, so catch it inside.
        string wrapped = """
            & {
                try {
            __SCRIPT__
                } catch { "ERROR: $($_.Exception.Message)" }
            } *>&1 | ForEach-Object {
                $line = if ($_ -is [Management.Automation.ErrorRecord]) { "ERROR: $_" } else { "$_" }
                [IO.File]::AppendAllText('__OUTPUT__', $line + "`n")
            }
            """
            .Replace("__OUTPUT__", EscapeSingleQuoted(outputFile))
            .Replace("__SCRIPT__", taskScript);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!IsElevated)
        {
            psi.Verb = "runas";
            output("Registering a SYSTEM task needs administrator rights; asking for elevation (UAC)...");
        }

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Windows PowerShell");

            long offset = 0;
            while (!process.WaitForExit(500))
                emit(ReadNewLines(outputFile, ref offset, final: false));
            emit(ReadNewLines(outputFile, ref offset, final: true));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Elevation was declined; running on this PC needs administrator rights.");
        }
        finally
        {
            File.Delete(outputFile);
        }
    }

    /// <summary>Elevated twin of <see cref="CopyWithRobocopy"/>: mirrors the package into the staging folder.</summary>
    private static string BuildLocalStageScript(string source, string destination) => """
        robocopy.exe '__SOURCE__' '__DESTINATION__' __OPTIONS__ | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Robocopy failed with exit code $LASTEXITCODE" }
        "[OK] Package staged in __DESTINATION__"

        """
        .Replace("__SOURCE__", EscapeSingleQuoted(source.TrimEnd('\\')))
        .Replace("__DESTINATION__", EscapeSingleQuoted(destination.TrimEnd('\\')))
        .Replace("__OPTIONS__", RobocopyOptions);

    private static string BuildLocalCleanupScript(string stagedPath) => """

        "Cleaning up..."
        try { Remove-Item -LiteralPath '__PATH__' -Recurse -Force -ErrorAction Stop; "[OK] Cleanup completed" }
        catch { "WARNING: Cleanup failed: $($_.Exception.Message)" }
        """
        .Replace("__PATH__", EscapeSingleQuoted(stagedPath));

    /// <summary>Text appended since <paramref name="offset"/>; until <paramref name="final"/>, only whole lines.</summary>
    internal static string ReadNewLines(string path, ref long offset, bool final)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[stream.Length - offset];
        int count = stream.Read(bytes, 0, bytes.Length);
        if (!final)
            count = bytes.AsSpan(0, count).LastIndexOf((byte)'\n') + 1;
        offset += count;
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    /// <summary>Pings the target, opens the WinRM runspace and, for a user-context run, checks someone is logged on.</summary>
    private static Runspace OpenRemoteSession(string computerName, bool runAsUser, Action<string> output)
    {
        // ICMP is a hint only: plenty of fleets block it while WinRM is open.
        output($"Checking connectivity to {computerName}...");
        using (var ping = new Ping())
        {
            try
            {
                var reply = ping.Send(computerName, 2000);
                output(reply.Status == IPStatus.Success
                    ? $"[OK] {computerName} responds to ping"
                    : $"[--] {computerName} did not respond to ping ({reply.Status}) - trying WinRM anyway");
            }
            catch (PingException ex)
            {
                output($"[--] Ping failed ({ex.InnerException?.Message ?? ex.Message}) - trying WinRM anyway");
            }
        }

        // Connect before the copy so a target without WinRM fails fast.
        output($"Connecting to {computerName} via WinRM...");
        var connectionInfo = new WSManConnectionInfo { ComputerName = computerName };
        var runspace = RunspaceFactory.CreateRunspace(connectionInfo);
        try
        {
            runspace.Open();
            output("[OK] WinRM session established");

            // For user-context installs, fail before the copy if nobody is logged on.
            if (runAsUser)
            {
                using var check = PowerShell.Create();
                check.Runspace = runspace;
                check.AddScript("(Get-CimInstance Win32_ComputerSystem).UserName");
                string? loggedOnUser = check.Invoke().FirstOrDefault()?.ToString();
                if (string.IsNullOrEmpty(loggedOnUser))
                    throw new InvalidOperationException($"No user is logged on to {computerName} — a user-context install requires a logged-on user");
                output($"[OK] Logged-on user: {loggedOnUser}");
            }
        }
        catch
        {
            runspace.Dispose();
            throw;
        }

        return runspace;
    }

    /// <summary>
    /// Registers a one-shot task that runs the command through cmd.exe from the package
    /// folder, starts it, and streams two things back through the pipeline until it ends:
    /// anything the command printed (rarely — the PSADT launcher swallows stdout) and the
    /// PSADT log entries written since the task started. The exit code arrives via the
    /// sentinel. Only the principal differs by context.
    /// </summary>
    private static string BuildTaskScript(bool runAsUser, string commandLine, string workingDirectory, string consoleLog, string configPath)
    {
        // S-1-5-18 rather than the account name: the SID is locale-independent.
        string principal = runAsUser
            ? """
              $user = (Get-CimInstance Win32_ComputerSystem).UserName
              if (-not $user) { throw 'No user is logged on to the target computer' }
              $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive
              $contextLabel = $user
              """
            : """
              $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
              $contextLabel = 'NT AUTHORITY\SYSTEM'
              """;

        return """
            $taskName = '__TASK_NAME__'
            $consoleLog = '__CONSOLE_LOG__'
            $configPath = '__CONFIG_PATH__'
            $workingDirectory = '__WORK_DIR__'
            $neverRan = __NEVER_RAN__
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

            __PRINCIPAL__
            $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c __COMMAND__ > "__CONSOLE_LOG__" 2>&1' -WorkingDirectory $workingDirectory
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
            "__SENTINEL__$exitCode"
            """
            .Replace("__PRINCIPAL__", principal)
            .Replace("__COMMAND__", EscapeSingleQuoted(commandLine))
            .Replace("__WORK_DIR__", EscapeSingleQuoted(workingDirectory))
            .Replace("__CONSOLE_LOG__", EscapeSingleQuoted(consoleLog))
            .Replace("__CONFIG_PATH__", EscapeSingleQuoted(configPath))
            .Replace("__TASK_NAME__", TaskName)
            .Replace("__NEVER_RAN__", NeverRan.ToString())
            .Replace("__SENTINEL__", ExitCodeSentinel);
    }

    /// <summary>All substituted values land inside single-quoted PowerShell strings.</summary>
    private static string EscapeSingleQuoted(string value) => PowerShellLiteral.SingleQuoted(value);

    /// <summary>
    /// The folder name reaches a UNC path, a task command line and a /MIR destination.
    /// /MIR deletes, so anything that could redirect is replaced rather than escaped.
    /// </summary>
    private static string SanitiseFolderName(string name)
    {
        var cleaned = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' or '\'' or '%' or '$' or '`' ? '_' : c)
            .ToArray())
            .Trim().Trim('.');

        return string.IsNullOrEmpty(cleaned) ? "Package" : cleaned;
    }

    private static void CopyWithRobocopy(string source, string destination, Action<int?>? progress)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = $"\"{source.TrimEnd('\\')}\" \"{destination.TrimEnd('\\')}\" {RobocopyOptions}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start robocopy.exe");
        var outputTask = process.StandardOutput.ReadToEndAsync();

        // Per-file output is off, so poll destination size against source size instead.
        if (progress != null)
        {
            long totalBytes = GetDirectorySize(source);
            while (!process.WaitForExit(5000))
            {
                if (totalBytes > 0)
                {
                    long copied = GetDirectorySize(destination);
                    progress((int)Math.Min(100, copied * 100 / totalBytes));
                }
            }
            progress(100);
        }

        process.WaitForExit();
        string result = outputTask.GetAwaiter().GetResult();

        // Robocopy exit codes are a bitfield: 0-7 succeeded, 8+ failed.
        if (process.ExitCode >= 8)
            throw new IOException($"Robocopy failed with exit code {process.ExitCode}:\n{result}");
    }

    // Tolerates files appearing and disappearing mid-copy.
    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch
        {
            return 0;
        }
    }
}
