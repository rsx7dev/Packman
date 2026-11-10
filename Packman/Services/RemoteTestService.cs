using Packman.Helpers;
using Packman.Models;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Packman.Services;

/// <summary>
/// Deploys a PSADT v4 package to a test machine: over WinRM, through PsExec, or into a
/// Windows Sandbox on this computer.
///
/// Each of them is the transport only; the install runs from a one-shot scheduled task. A
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
    // The default staging folder. Literal, not %ProgramData%: the admin-share path must name the
    // same folder. Settings ▸ Paths can move it; see RemoteStaging.
    internal const string RemoteBasePath = RemoteStaging.DefaultParent + @"\" + RemoteStaging.FolderName;
    private const string TaskName = "Packman_RemoteTest";
    private const string ExitCodeSentinel = "PACKMAN_EXIT_CODE:";

    // SCHED_S_TASK_HAS_NOT_RUN: registered but never produced a result.
    private const int NeverRan = 267011;

    /// <summary>The deployment verbs PSADT accepts.</summary>
    public static readonly IReadOnlySet<string> DeploymentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "Install", "Uninstall", "Repair" };

    /// <summary>PSADT/MSI success codes; 3010 and 1641 mean reboot required.</summary>
    public static bool IsSuccess(int exitCode) => exitCode is 0 or 3010 or 1641;

    /// <summary>
    /// The exit code the remote monitor printed. The task scheduler reports it as an unsigned
    /// 32-bit number, so HRESULTs and crash codes arrive above int.MaxValue; they map back to
    /// the signed value. Anything unreadable is -1, never 0: no code must not read as success.
    /// </summary>
    internal static int ParseExitCode(ReadOnlySpan<char> text)
    {
        if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return -1;
        if (value is >= int.MinValue and <= int.MaxValue) return (int)value;
        return value <= uint.MaxValue ? unchecked((int)(uint)value) : -1;
    }

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
    /// <paramref name="stagingParent"/> is the Settings ▸ Paths value; empty stages in C:\ProgramData\Packman.
    /// <paramref name="method"/> picks the transport; a Sandbox run ignores <paramref name="computerName"/>
    /// and <paramref name="stagingParent"/>, and <paramref name="cleanupAfterDeploy"/> closes the sandbox.
    /// Throws PSRemotingTransportException when WinRM is unreachable, and
    /// OperationCanceledException when <paramref name="ct"/> stops the run.
    /// </summary>
    public async Task<int> DeployAsync(string computerName, string sourcePath, string deploymentType, string commandLine,
        bool cleanupAfterDeploy, bool runAsUser, Action<string> output,
        Action<int?>? copyProgress = null, CancellationToken ct = default, string? stagingParent = null,
        RemoteTestMethod method = RemoteTestMethod.WinRM, string? psExecPath = null, bool sandboxNetworking = true)
    {
        var sandbox = method == RemoteTestMethod.Sandbox;
        if (sandbox) computerName = "Windows Sandbox";
        else if (!IsValidComputerName(computerName))
            throw new ArgumentException($"'{computerName}' is not a valid computer name", nameof(computerName));

        // Checked before anything touches the target. A sandbox is new each time, so it uses the default.
        var basePath = RemoteStaging.BasePath(sandbox ? null : stagingParent);

        if (!DeploymentTypes.Contains(deploymentType))
            throw new ArgumentException($"'{deploymentType}' is not a valid deployment type", nameof(deploymentType));

        // Reaches a cmd.exe command line inside the task; one line, nothing else.
        commandLine = (commandLine ?? "").Trim();
        if (commandLine.Length == 0 || commandLine.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("The command line must be a single non-empty line", nameof(commandLine));

        // This computer needs no WinRM: one elevated helper does what the remote session would.
        var local = !sandbox && LocalTestRunner.IsLocalTarget(computerName);

        output("========================================");
        output(sandbox ? "Packman Test (Windows Sandbox)"
            : local ? "Packman Test (this computer, no WinRM)"
            : method == RemoteTestMethod.PsExec ? "Packman Remote Test (PsExec)" : "Packman Remote Test (WinRM)");
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

        // Layout is ...\Manufacturer_AppName\Version; combine both so packages don't mix
        // and a re-run only copies what differs.
        var sourceDir = new DirectoryInfo(sourcePath);
        string packageName = SanitiseFolderName(sourceDir.Parent?.Parent != null
            ? $"{sourceDir.Parent.Name}_{sourceDir.Name}"
            : sourceDir.Name);
        string remotePackagePath = $@"{basePath}\{packageName}";

        // Intune runs the command line from the content folder, so the task does too.
        string workingDirectory = relativeApplicationPath.Length == 0 ? remotePackagePath : $@"{remotePackagePath}\{relativeApplicationPath}";
        string configPath = $@"{workingDirectory}\Config\config.psd1";
        string consoleLog = $@"{remotePackagePath}\Packman_Console.log";

        if (sandbox)
            return await DeploySandboxAsync(sourcePath, commandLine, cleanupAfterDeploy, runAsUser, sandboxNetworking, output, copyProgress, ct,
                basePath, remotePackagePath, workingDirectory, consoleLog, configPath);

        if (local)
            return await DeployLocalAsync(sourcePath, commandLine, cleanupAfterDeploy, runAsUser, output, copyProgress, ct,
                basePath, remotePackagePath, workingDirectory, consoleLog, configPath);

        // ICMP is a hint only: plenty of fleets block it while WinRM or SMB is open.
        var transport = method == RemoteTestMethod.PsExec ? "PsExec" : "WinRM";
        output($"Checking connectivity to {computerName}...");
        using (var ping = new Ping())
        {
            try
            {
                var reply = ping.Send(computerName, 2000);
                output(reply.Status == IPStatus.Success
                    ? $"[OK] {computerName} responds to ping"
                    : $"[--] {computerName} did not respond to ping ({reply.Status}) - trying {transport} anyway");
            }
            catch (PingException ex)
            {
                output($"[--] Ping failed ({ex.InnerException?.Message ?? ex.Message}) - trying {transport} anyway");
            }
        }

        if (method == RemoteTestMethod.PsExec)
            return await DeployPsExecAsync(computerName, sourcePath, commandLine, cleanupAfterDeploy, runAsUser, output, copyProgress, ct,
                psExecPath, basePath, remotePackagePath, workingDirectory, consoleLog, configPath);

        // Connect before the copy so a target without WinRM fails fast.
        ct.ThrowIfCancellationRequested();
        output($"Connecting to {computerName} via WinRM...");
        var connectionInfo = new WSManConnectionInfo { ComputerName = computerName };
        using var runspace = RunspaceFactory.CreateRunspace(connectionInfo);
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

        string targetUnc = RemoteStaging.AdminShare(computerName, remotePackagePath);

        // The package runs as SYSTEM, so only SYSTEM and Administrators may write where it is staged.
        ct.ThrowIfCancellationRequested();
        output($"Securing {basePath} on target...");
        using (var secure = PowerShell.Create())
        {
            secure.Runspace = runspace;
            secure.AddScript(BuildSecureStagingScript(basePath));
            foreach (var line in secure.Invoke())
                output(line.ToString());
            if (secure.HadErrors)
                throw new InvalidOperationException($"Could not secure {basePath} on {computerName}: {secure.Streams.Error.FirstOrDefault()}");
        }

        await CopyToTargetAsync(sourcePath, targetUnc, output, copyProgress, ct);

        output("");
        output($"Registering scheduled task '{TaskName}' on target...");
        output($"Working directory: {workingDirectory}");
        output("");

        ct.ThrowIfCancellationRequested();
        int exitCode = -1;
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = runspace;
            ps.AddScript(BuildTaskScript(runAsUser, commandLine, remotePackagePath, workingDirectory, consoleLog, configPath));

            var stdout = new PSDataCollection<PSObject>();
            stdout.DataAdded += (s, e) =>
            {
                string? chunk = stdout[e.Index]?.ToString();
                if (string.IsNullOrEmpty(chunk)) return;
                foreach (string rawLine in chunk.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (line.StartsWith(ExitCodeSentinel, StringComparison.Ordinal))
                        exitCode = ParseExitCode(line.AsSpan(ExitCodeSentinel.Length));
                    else
                        output(line);
                }
            };
            ps.Streams.Error.DataAdded += (s, e) => output($"ERROR: {ps.Streams.Error[e.Index]}");

            using (ct.Register(() => ps.BeginStop(null, null)))
            {
                try
                {
                    ps.Invoke<object, PSObject>(null, stdout, null);
                }
                catch (PipelineStoppedException) when (ct.IsCancellationRequested) { }
            }
        }

        // Stopping the monitor leaves the task running on the target; stop and remove it too.
        if (ct.IsCancellationRequested)
        {
            using var stop = PowerShell.Create();
            stop.Runspace = runspace;
            stop.AddScript($"Stop-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue");
            stop.Invoke();
            output($"Stopped and removed the scheduled task '{TaskName}' on target");
            ct.ThrowIfCancellationRequested();
        }

        output("");
        output($"Exit code: {exitCode}");
        if (exitCode == -1)
            output("WARNING: The remote monitor returned no exit code; check the PSADT log on the target.");

        if (cleanupAfterDeploy) DeleteStagedPackage(targetUnc, output);

        return exitCode;
    }

    private static async Task CopyToTargetAsync(string sourcePath, string targetUnc, Action<string> output, Action<int?>? copyProgress, CancellationToken ct)
    {
        output($"Copying files to {targetUnc}...");
        var copyTimer = Stopwatch.StartNew();
        await CopyWithRobocopyAsync(sourcePath, targetUnc, copyProgress, ct);
        copyTimer.Stop();
        copyProgress?.Invoke(null);

        var copiedFiles = Directory.GetFiles(targetUnc, "*", SearchOption.AllDirectories);
        double sizeMb = copiedFiles.Sum(f => new FileInfo(f).Length) / 1048576.0;
        output($"Package size: {Math.Round(sizeMb, 2)} MB");
        output($"[OK] {copiedFiles.Length} files in sync after {Math.Round(copyTimer.Elapsed.TotalSeconds, 1)} seconds");
    }

    private static void DeleteStagedPackage(string targetUnc, Action<string> output)
    {
        output("");
        output("Cleaning up...");
        try
        {
            DirectoryCopy.ClearReadOnlyRecursive(targetUnc);
            Directory.Delete(targetUnc, true);
            output("[OK] Cleanup completed");
        }
        catch (Exception ex)
        {
            output($"WARNING: Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>Passes console lines on and picks the exit code out of the sentinel line.</summary>
    private sealed class ExitCodeReader(Action<string> output)
    {
        public int ExitCode { get; private set; } = -1;
        public bool Seen { get; private set; }

        public void Line(string line)
        {
            if (!line.StartsWith(ExitCodeSentinel, StringComparison.Ordinal))
            {
                output(line);
                return;
            }
            ExitCode = ParseExitCode(line.AsSpan(ExitCodeSentinel.Length));
            Seen = true;
        }

        public int Report(string where)
        {
            output("");
            output($"Exit code: {ExitCode}");
            if (!Seen || ExitCode == -1)
                output($"WARNING: The monitor returned no exit code; check the PSADT log {where}.");
            return ExitCode;
        }
    }

    internal const string PsExecMissing =
        "PsExec was not found. Download PsTools from Microsoft Sysinternals, then set PsExec Path under Settings → Paths " +
        "or put PsExec64.exe in a folder on PATH. Packman cannot ship it: its license does not allow redistribution.";

    /// <summary>
    /// The WinRM run with PsExec as the transport: the staging and task scripts are the same,
    /// each run on the target as SYSTEM by PsExec, and the copy goes over the admin share.
    /// Needs SMB and the Service Control Manager on the target instead of WinRM.
    /// </summary>
    private static async Task<int> DeployPsExecAsync(string computerName, string sourcePath, string commandLine, bool cleanupAfterDeploy,
        bool runAsUser, Action<string> output, Action<int?>? copyProgress, CancellationToken ct, string? psExecPath,
        string basePath, string remotePackagePath, string workingDirectory, string consoleLog, string configPath)
    {
        var psExec = PsExecTestRunner.Find(psExecPath) ?? throw new InvalidOperationException(PsExecMissing);
        output($"Using {psExec}");
        output("PsExec is started with -accepteula, which accepts the Sysinternals license terms for your account on this computer.");

        string targetUnc = RemoteStaging.AdminShare(computerName, remotePackagePath);

        // The package runs as SYSTEM, so only SYSTEM and Administrators may write where it is staged.
        ct.ThrowIfCancellationRequested();
        output($"Connecting to {computerName} with PsExec and securing {basePath}...");
        await PsExecTestRunner.RunScriptAsync(psExec, computerName, BuildSecureStagingScript(basePath), output, ct);
        output("[OK] PsExec ran on the target");

        await CopyToTargetAsync(sourcePath, targetUnc, output, copyProgress, ct);

        output("");
        output($"Registering scheduled task '{TaskName}' on target...");
        output($"Working directory: {workingDirectory}");
        output("");

        ct.ThrowIfCancellationRequested();
        var reader = new ExitCodeReader(output);
        try
        {
            await PsExecTestRunner.RunScriptAsync(psExec, computerName,
                BuildTaskScript(runAsUser, commandLine, remotePackagePath, workingDirectory, consoleLog, configPath), reader.Line, ct);
        }
        catch (OperationCanceledException)
        {
            // Killing PsExec here does not stop the task on the target; a second call removes it.
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await PsExecTestRunner.RunScriptAsync(psExec, computerName,
                    $"Stop-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue",
                    _ => { }, cleanup.Token);
                output($"Stopped and removed the scheduled task '{TaskName}' on target");
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
            {
                output($"WARNING: Could not remove the scheduled task '{TaskName}' on {computerName}. Check Task Scheduler there before running again.");
            }
            throw;
        }

        var exitCode = reader.Report("on the target");
        if (cleanupAfterDeploy) DeleteStagedPackage(targetUnc, output);
        return exitCode;
    }

    /// <summary>
    /// The same run inside Windows Sandbox on this computer: <see cref="SandboxTestRunner"/>
    /// starts a clean, disposable Windows whose logon command stages the package and runs the
    /// same task script, so the install is still done by a SYSTEM (or signed-in user) scheduled
    /// task. Nothing is installed on this computer, and closing the sandbox discards the test.
    /// </summary>
    private static async Task<int> DeploySandboxAsync(string sourcePath, string commandLine, bool closeWhenDone, bool runAsUser,
        bool networking, Action<string> output, Action<int?>? copyProgress, CancellationToken ct,
        string basePath, string stagedPackagePath, string workingDirectory, string consoleLog, string configPath)
    {
        ct.ThrowIfCancellationRequested();
        var reader = new ExitCodeReader(output);
        try
        {
            await SandboxTestRunner.RunAsync(sourcePath,
                () => SandboxTestRunner.BuildLogonScript(BuildSecureStagingScript(basePath),
                    // A line each second of the wait, so a closed sandbox is noticed.
                    BuildTaskScript(runAsUser, commandLine, stagedPackagePath, workingDirectory, consoleLog, configPath,
                        eachSecond: $"'{SandboxTestRunner.Heartbeat}'"),
                    stagedPackagePath, closeWhenDone),
                networking, reader.Line, copyProgress, ct);
        }
        catch (OperationCanceledException)
        {
            output("Closed Windows Sandbox; everything in it is discarded.");
            throw;
        }
        finally
        {
            copyProgress?.Invoke(null);
        }

        var exitCode = reader.Report("in the sandbox");
        output("");
        output(closeWhenDone
            ? "The sandbox closes itself now; the test left nothing on this computer."
            : "The sandbox stays open so you can inspect the result. Close its window when you are done; that discards everything in it.");
        return exitCode;
    }

    /// <summary>
    /// The same run on this computer: <see cref="LocalTestRunner"/> starts one elevated helper
    /// (a UAC prompt) that secures the staging folder, copies the package, and runs the same
    /// task script, so the install is still done by a SYSTEM (or logged-on user) scheduled task.
    /// </summary>
    private static async Task<int> DeployLocalAsync(string sourcePath, string commandLine, bool cleanupAfterDeploy, bool runAsUser,
        Action<string> output, Action<int?>? copyProgress, CancellationToken ct,
        string basePath, string stagedPackagePath, string workingDirectory, string consoleLog, string configPath)
    {
        ct.ThrowIfCancellationRequested();
        output("Asking for administrator rights: staging the package and running it as "
               + (runAsUser ? "the logged-on user" : "SYSTEM") + " needs them. Approve the Windows prompt to continue.");
        output("Whatever is in the package folder when it is copied is what runs" + (runAsUser ? "." : " as SYSTEM."));
        output($"Securing {basePath} on this computer...");

        // A heartbeat each second of the wait, so the helper notices a cancelled run at once.
        var taskScript = BuildTaskScript(runAsUser, commandLine, stagedPackagePath, workingDirectory, consoleLog, configPath,
            eachSecond: $"'{LocalTestRunner.Heartbeat}'");
        var helper = LocalTestRunner.BuildHelperScript(BuildSecureStagingScript(basePath), taskScript,
            LocalTestRunner.ToUncIfMapped(sourcePath).TrimEnd('\\', '/'), stagedPackagePath, TaskName, cleanupAfterDeploy);

        int exitCode = -1;
        var sawExitCode = false;
        try
        {
            await LocalTestRunner.RunAsync(helper, line =>
            {
                if (!line.StartsWith(ExitCodeSentinel, StringComparison.Ordinal))
                {
                    output(line);
                    return;
                }
                exitCode = ParseExitCode(line.AsSpan(ExitCodeSentinel.Length));
                sawExitCode = true;
                output("");
                output($"Exit code: {exitCode}");
            }, ct);
        }
        catch (OperationCanceledException ex)
        {
            output(ex.Data[LocalTestRunner.HelperExitedKey] is true
                ? $"Stopped and removed the scheduled task '{TaskName}' on this computer"
                : $"WARNING: The elevated helper has not finished stopping. Check Task Scheduler for '{TaskName}' and the staging folder before running again.");
            throw;
        }
        finally
        {
            copyProgress?.Invoke(null);
        }

        if (!sawExitCode)
        {
            output("");
            output($"Exit code: {exitCode}");
            output("WARNING: The monitor returned no exit code; check the PSADT log on this computer.");
        }
        return exitCode;
    }

    /// <summary>
    /// Registers a one-shot task that runs the command through cmd.exe from the package
    /// folder, starts it, and streams two things back through the pipeline until it ends:
    /// anything the command printed (rarely — the PSADT launcher swallows stdout) and the
    /// PSADT log entries written since the task started. The exit code arrives via the
    /// sentinel. Only the principal differs by context, and a user run also gets read
    /// access to its own package folder and write access to the console log alone.
    /// </summary>
    /// <param name="eachSecond">A statement run on every pass of the wait loop; a local run emits its heartbeat here.</param>
    internal static string BuildTaskScript(bool runAsUser, string commandLine, string packagePath, string workingDirectory,
        string consoleLog, string configPath, string eachSecond = "")
    {
        // S-1-5-18 rather than the account name: the SID is locale-independent.
        // Staging is SYSTEM and Administrators only, so a user run is granted read on this
        // package (traverse into the base needs no right) and write on a log created for it;
        // it can still change nothing a later SYSTEM run executes.
        string principal = runAsUser
            ? """
              $user = (Get-CimInstance Win32_ComputerSystem).UserName
              if (-not $user) { throw 'No user is logged on to the target computer' }
              $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive
              $contextLabel = $user
              try {
                  $userSid = (New-Object System.Security.Principal.NTAccount($user)).Translate([System.Security.Principal.SecurityIdentifier])
                  $package = Get-Item -LiteralPath '__PACKAGE_DIR__'
                  $acl = $package.GetAccessControl('Access')
                  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                      $userSid, 'ReadAndExecute', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
                  $package.SetAccessControl($acl)
                  [IO.File]::WriteAllText($consoleLog, '')
                  $log = Get-Item -LiteralPath $consoleLog
                  $acl = $log.GetAccessControl('Access')
                  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($userSid, 'Modify', 'Allow')))
                  $log.SetAccessControl($acl)
              }
              catch { throw "Could not give $user access to the staged package: $($_.Exception.Message)" }
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
                    # Names the config may use, in both the $env: and legacy $envX forms. They are
                    # replaced as text: the value comes from the package, and expanding it as
                    # PowerShell would run any $(...) in it with this session's admin rights.
                    $temp = [IO.Path]::GetTempPath().TrimEnd('\')
                    $names = [ordered]@{
                        'SystemRoot' = $env:SystemRoot; 'WinDir' = $env:SystemRoot; 'ProgramData' = $env:ProgramData
                        'ProgramFiles' = $env:ProgramFiles; 'LocalAppData' = $env:LOCALAPPDATA; 'AppData' = $env:APPDATA
                        'Temp' = $temp; 'SystemDrive' = $env:SystemDrive; 'Public' = $env:PUBLIC
                    }
                    foreach ($key in 'LogPath', 'LogPathNoAdminRights') {
                        $value = $toolkit[$key]
                        if (-not ($value -is [string]) -or -not $value) { continue }
                        $expanded = $value
                        foreach ($name in $names.Keys) {
                            $expanded = $expanded -ireplace ('\$env:?' + $name + '(?![A-Za-z0-9_])'), "$($names[$name])".Replace('$', '$$')
                        }
                        if ($expanded.Contains('$') -or $expanded.Contains('`')) {
                            "WARNING: $key in config.psd1 uses an expression Packman does not evaluate; watching the default PSADT log folders for it."
                            continue
                        }
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
            $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/s /c "__COMMAND__ > "__CONSOLE_LOG__" 2>&1"' -WorkingDirectory $workingDirectory
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
            $started = (Get-Date).AddSeconds(-5)
            Start-ScheduledTask -TaskName $taskName
            "Deployment task started as $contextLabel"
            $elapsed = 0
            while ($elapsed -lt 3600) {
                Start-Sleep -Seconds 1
                $elapsed += 1
                __EACH_SECOND__
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
            .Replace("__EACH_SECOND__", eachSecond)
            .Replace("__PRINCIPAL__", principal)
            .Replace("__PACKAGE_DIR__", EscapeSingleQuoted(packagePath))
            .Replace("__COMMAND__", EscapeSingleQuoted(commandLine))
            .Replace("__WORK_DIR__", EscapeSingleQuoted(workingDirectory))
            .Replace("__CONSOLE_LOG__", EscapeSingleQuoted(consoleLog))
            .Replace("__CONFIG_PATH__", EscapeSingleQuoted(configPath))
            .Replace("__TASK_NAME__", TaskName)
            .Replace("__NEVER_RAN__", NeverRan.ToString())
            .Replace("__SENTINEL__", ExitCodeSentinel);
    }

    /// <summary>
    /// Makes <paramref name="basePath"/> a folder only SYSTEM and Administrators can change.
    /// Any user can create folders under C:\ProgramData, so an existing one is trusted only
    /// when it is a real directory owned by one of them with a protected DACL naming no one
    /// else. Packman never leaves the folder unsecured, so an untrusted one holds no package
    /// of ours: it is renamed aside rather than reset, and a fresh one is created with the
    /// ACL already in place, so nothing can be planted in between. Nothing is recursive.
    /// </summary>
    internal static string BuildSecureStagingScript(string basePath) =>
        """
        $ErrorActionPreference = 'Stop'
        $base = '__BASE__'
        $trusted = @('S-1-5-18', 'S-1-5-32-544')
        $sidType = [System.Security.Principal.SecurityIdentifier]

        function Test-TrustedBase {
            $item = Get-Item -LiteralPath $base -Force
            # A junction could be repointed after the check, so only a plain directory counts.
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
            $acl = Get-Acl -LiteralPath $base
            if ($trusted -notcontains $acl.GetOwner($sidType).Value) { return $false }
            if (-not $acl.AreAccessRulesProtected) { return $false }
            foreach ($rule in $acl.GetAccessRules($true, $true, $sidType)) {
                if ($trusted -notcontains $rule.IdentityReference.Value) { return $false }
            }
            return $true
        }

        # The folders above $base matter as much as $base: whoever can rename or re-point one of
        # them can swap the whole staging tree between this check and the SYSTEM task starting.
        # Each must be a real directory owned by SYSTEM, Administrators or TrustedInstaller that
        # gives no one else delete, delete-child, change-permissions or take-ownership on itself.
        function Get-UntrustedAncestor {
            $owners = $trusted + 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
            $risky = [System.Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
            $dir = [System.IO.Directory]::GetParent($base)
            while ($dir) {
                $item = Get-Item -LiteralPath $dir.FullName -Force
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return "$($dir.FullName) is a link, not a folder" }
                $acl = Get-Acl -LiteralPath $dir.FullName
                if ($owners -notcontains $acl.GetOwner($sidType).Value) { return "$($dir.FullName) is not owned by Administrators or SYSTEM" }
                foreach ($rule in $acl.GetAccessRules($true, $true, $sidType)) {
                    if ($rule.AccessControlType -ne 'Allow') { continue }
                    # Inherit-only entries apply to children, not to this folder.
                    if ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
                    if ($owners -contains $rule.IdentityReference.Value) { continue }
                    if ($rule.FileSystemRights -band $risky) { return "$($dir.FullName) can be renamed or changed by $($rule.IdentityReference.Value)" }
                }
                $dir = $dir.Parent
            }
            return $null
        }

        function New-SecureBase {
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
            $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
            $acl.SetAccessRuleProtection($true, $false)
            foreach ($sid in $trusted) {
                $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                    (New-Object System.Security.Principal.SecurityIdentifier($sid)), 'FullControl',
                    'ContainerInherit, ObjectInherit', 'None', 'Allow')))
            }
            $null = [System.IO.Directory]::CreateDirectory($base, $acl)
        }

        if (-not (Test-Path -LiteralPath $base)) {
            New-SecureBase
        }
        elseif (-not (Test-TrustedBase)) {
            $moved = 'Packman.untrusted-' + (Get-Date -Format 'yyyyMMddHHmmss')
            Rename-Item -LiteralPath $base -NewName $moved
            "WARNING: $base was writable by other accounts; moved it to $moved"
            New-SecureBase
        }

        if (-not (Test-TrustedBase)) { throw "$base is not limited to SYSTEM and Administrators" }
        $unsafe = Get-UntrustedAncestor
        if ($unsafe) { throw "$base is not a safe place to stage an install that runs as SYSTEM: $unsafe. Use a folder only administrators can change, or leave Staging Folder on Target empty." }
        "[OK] $base is limited to SYSTEM and Administrators"
        """
        .Replace("__BASE__", PowerShellLiteral.SingleQuoted(basePath));

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

    internal static async Task CopyWithRobocopyAsync(string source, string destination, Action<int?>? progress, CancellationToken ct)
    {
        // /MIR mirrors, /MT:16 multithreaded, /J unbuffered, /NOOFFLOAD skips the ODX
        // attempt (unusable over SMB), /R:2 /W:5 replaces the 1M-retry defaults, and
        // /NP /NFL /NDL drops per-file output, which otherwise dominates the runtime.
        // No /SEC or /COPYALL: copied files must inherit the staging folder's ACL.
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "robocopy.exe"),   // never found through the current folder or PATH
            Arguments = $"\"{source.TrimEnd('\\')}\" \"{destination.TrimEnd('\\')}\" /MIR /MT:16 /J /NOOFFLOAD /R:2 /W:5 /NP /NFL /NDL /NJH",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start robocopy.exe");
        var outputTask = process.StandardOutput.ReadToEndAsync();

        string result;
        try
        {
            // Per-file output is off, so poll destination size against source size instead.
            long totalBytes = progress != null ? GetDirectorySize(source) : 0;
            var exited = process.WaitForExitAsync(ct);
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(5000, ct));
                ct.ThrowIfCancellationRequested();
                if (progress != null && totalBytes > 0 && !exited.IsCompleted)
                    progress((int)Math.Min(100, GetDirectorySize(destination) * 100 / totalBytes));
            }
            await exited;
            result = await outputTask;
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process object does not stop robocopy or its worker threads' copies.
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        progress?.Invoke(100);

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
