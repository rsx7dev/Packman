using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>
/// Runs a test in Windows Sandbox: a clean, disposable Windows that is thrown away when its
/// window closes. Packman copies the package to a session folder, writes a .wsb configuration
/// that maps the copy read-only and a small folder for results, and lets the sandbox's logon
/// command stage the package and run the same scheduled-task script a remote run uses.
///
/// The sandbox has no channel back to the host other than its mapped folders, so the script
/// appends its lines to a log file there and Packman follows that file.
/// </summary>
public static class SandboxTestRunner
{
    /// <summary>Written while the sandbox works, so a closed sandbox is told apart from a quiet one. Never shown.</summary>
    internal const string Heartbeat = "__PACKMAN_ALIVE__";

    /// <summary>Prefix of the one line the script writes when it could not carry on.</summary>
    internal const string Failed = "__PACKMAN_FAILED__";

    /// <summary>The script's last line.</summary>
    internal const string Done = "__PACKMAN_DONE__";

    internal const string PackageFolderInSandbox = @"C:\PackmanPackage";
    internal const string SessionFolderInSandbox = @"C:\PackmanSession";
    private const string ScriptName = "run.ps1";
    private const string OutputName = "output.log";
    private const string ProcessPrefix = "WindowsSandbox";

    internal static TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(5);
    internal static TimeSpan SilenceLimit { get; set; } = TimeSpan.FromSeconds(45);

    public static string ExecutablePath => Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");

    /// <summary>Windows Sandbox is an optional feature of Windows Pro, Enterprise and Education.</summary>
    public static bool IsInstalled => File.Exists(ExecutablePath);

    /// <summary>Windows runs one sandbox at a time; a second configuration fails with a dialog.</summary>
    public static bool IsRunning => SandboxProcesses().Count > 0;

    private static List<Process> SandboxProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Where(p => { try { return p.ProcessName.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase); } catch (InvalidOperationException) { return false; } })
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>Where session folders are kept; each run gets its own and old ones are removed.</summary>
    public static string SessionsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packman", "Sandbox");

    /// <summary>The .wsb file: the package copy read-only, the session folder writable, and the logon command.</summary>
    internal static string BuildConfiguration(string hostPackageFolder, string hostSessionFolder, bool networking) =>
        new XElement("Configuration",
            new XElement("Networking", networking ? "Enable" : "Disable"),
            new XElement("MappedFolders",
                MappedFolder(hostPackageFolder, PackageFolderInSandbox, readOnly: true),
                MappedFolder(hostSessionFolder, SessionFolderInSandbox, readOnly: false)),
            new XElement("LogonCommand",
                new XElement("Command",
                    $@"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File {SessionFolderInSandbox}\{ScriptName}")))
        .ToString();

    private static XElement MappedFolder(string host, string sandbox, bool readOnly) =>
        new("MappedFolder",
            new XElement("HostFolder", host),
            new XElement("SandboxFolder", sandbox),
            new XElement("ReadOnly", readOnly ? "true" : "false"));

    /// <summary>
    /// The script the sandbox runs at logon: secure the staging folder, copy the package into
    /// it (a mapped folder is not where Intune would run it from), run
    /// <paramref name="taskScript"/> and report every line to the session log.
    /// </summary>
    internal static string BuildLogonScript(string secureScript, string taskScript, string stagedPackagePath, bool closeWhenDone) =>
        System.Text.RegularExpressions.Regex.Replace("""
        $ErrorActionPreference = 'Stop'
        $out = '__SESSION__\__OUTPUT__'
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        function Send($value) {
            foreach ($line in ("$value" -split "`r?`n")) { [IO.File]::AppendAllText($out, $line + "`r`n", $utf8) }
        }

        $source = '__PACKAGE__'
        $staged = '__STAGED__'
        try {
            Send ("[OK] Windows Sandbox started, signed in as {0}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
            & {
        __SECURE__
            } | ForEach-Object { Send $_ }

            Send "Copying files to $staged..."
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $copy = Start-Process -FilePath ([Environment]::SystemDirectory + '\robocopy.exe') -PassThru -WindowStyle Hidden `
                -ArgumentList ('"{0}" "{1}" /MIR /R:2 /W:5 /NP /NFL /NDL /NJH /NJS' -f $source, $staged)
            $null = $copy.Handle
            while (-not $copy.HasExited) { Start-Sleep -Seconds 1; Send '__ALIVE__' }
            if ($copy.ExitCode -ge 8) { throw "Robocopy failed with exit code $($copy.ExitCode)." }
            $files = @(Get-ChildItem -LiteralPath $staged -Recurse -File -Force)
            $bytes = ($files | Measure-Object -Property Length -Sum).Sum
            Send ("Package size: {0} MB" -f [Math]::Round($bytes / 1MB, 2))
            Send ("[OK] {0} files in sync after {1} seconds" -f $files.Count, [Math]::Round($timer.Elapsed.TotalSeconds, 1))
            Send ''
            Send 'Registering the scheduled task in the sandbox...'
            Send ''

            & {
                $ErrorActionPreference = 'Continue'
        __TASK_SCRIPT__
            } 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) { Send "ERROR: $_" } else { Send $_ }
            }
        }
        catch {
            Send "__FAILED__$(("$($_.Exception.Message)" -replace "`r?`n", ' '))"
        }
        finally {
            Send '__DONE__'
            if (__CLOSE__) { Stop-Computer -Force }
        }
        """, "__[A-Z_]+?__", m => m.Value switch
        {
            // One pass: a value containing another placeholder's name is never expanded again.
            "__SESSION__" => SessionFolderInSandbox,
            "__OUTPUT__" => OutputName,
            "__PACKAGE__" => PackageFolderInSandbox,
            "__STAGED__" => PowerShellLiteral.SingleQuoted(stagedPackagePath),
            "__ALIVE__" => Heartbeat,
            "__FAILED__" => Failed,
            "__DONE__" => Done,
            "__CLOSE__" => closeWhenDone ? "$true" : "$false",
            "__SECURE__" => secureScript,
            "__TASK_SCRIPT__" => taskScript,
            _ => m.Value,
        });

    /// <summary>
    /// The complete lines appended to <paramref name="stream"/> since the last call. A line the
    /// sandbox is still writing stays in <paramref name="partial"/> until its line break arrives.
    /// </summary>
    internal static List<string> ReadNewLines(Stream stream, Decoder decoder, StringBuilder partial)
    {
        var lines = new List<string>();
        var buffer = new byte[8192];
        var chars = new char[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            partial.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));

        var text = partial.ToString();
        var start = 0;
        int end;
        while ((end = text.IndexOf('\n', start)) >= 0)
        {
            lines.Add(text[start..end].TrimEnd('\r'));
            start = end + 1;
        }
        partial.Clear().Append(text, start, text.Length - start);
        return lines;
    }

    /// <summary>
    /// Copies the package to a session folder, starts Windows Sandbox on it and passes each line
    /// the logon script reports to <paramref name="onLine"/> until it finishes. Throws
    /// InvalidOperationException when the sandbox is missing, already open, never reports or is
    /// closed mid-run, and OperationCanceledException when <paramref name="ct"/> stops the run
    /// (the sandbox is closed, which discards everything in it).
    /// </summary>
    /// <param name="buildScript">Given nothing; returns the logon script. Called after the copy so a failed copy starts nothing.</param>
    public static async Task RunAsync(string packagePath, Func<string> buildScript, bool networking,
        Action<string> onLine, Action<int?>? copyProgress, CancellationToken ct)
    {
        if (!IsInstalled)
            throw new InvalidOperationException(
                "Windows Sandbox is not installed on this computer. Turn on the \"Windows Sandbox\" feature " +
                "(Windows Pro, Enterprise or Education; virtualization enabled), restart, and run the test again.");
        if (IsRunning)
            throw new InvalidOperationException("Windows Sandbox is already open. Close it first: Windows runs one sandbox at a time.");

        var session = PrepareSessionFolder();
        var hostPackage = Path.Combine(session, "Package");
        var hostSession = Directory.CreateDirectory(Path.Combine(session, "Session")).FullName;

        // A local copy: a sandbox cannot map a network share, and the run should not change under it.
        onLine($"Copying the package to {hostPackage}...");
        await RemoteTestService.CopyWithRobocopyAsync(packagePath, hostPackage, copyProgress, ct);
        copyProgress?.Invoke(null);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        // Windows PowerShell reads a script without a byte order mark as ANSI.
        await File.WriteAllTextAsync(Path.Combine(hostSession, ScriptName), buildScript(), new UTF8Encoding(true), ct);
        var outputPath = Path.Combine(hostSession, OutputName);
        await File.WriteAllBytesAsync(outputPath, [], ct);
        var configuration = Path.Combine(session, "Packman.wsb");
        await File.WriteAllTextAsync(configuration, BuildConfiguration(hostPackage, hostSession, networking), utf8, ct);

        onLine($"Starting Windows Sandbox ({(networking ? "network on" : "network off")})...");
        onLine("A sandbox window opens. It can take a minute before the first line arrives.");
        using (Process.Start(new ProcessStartInfo { FileName = ExecutablePath, UseShellExecute = false, ArgumentList = { configuration } })) { }

        var finished = false;
        string? failure = null;
        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = utf8.GetDecoder();
            var partial = new StringBuilder();
            var started = false;
            var lastSign = Stopwatch.StartNew();

            while (!finished)
            {
                await Task.Delay(500, ct);
                foreach (var line in ReadNewLines(stream, decoder, partial))
                {
                    started = true;
                    lastSign.Restart();
                    if (line == Heartbeat) continue;
                    if (line == Done) { finished = true; break; }
                    if (line.StartsWith(Failed, StringComparison.Ordinal)) failure = line[Failed.Length..];
                    else onLine(line);
                }
                if (finished) break;

                if (!started && lastSign.Elapsed > StartTimeout)
                    throw new InvalidOperationException(
                        "Windows Sandbox did not report back. Check that it started and that virtualization is enabled, then try again.");
                // Both signs gone: no sandbox process and nothing written for a while.
                if (started && lastSign.Elapsed > SilenceLimit && !IsRunning)
                    throw new InvalidOperationException("Windows Sandbox was closed before the run finished.");
            }
        }
        catch (OperationCanceledException)
        {
            Close();
            throw;
        }

        if (failure != null) throw new InvalidOperationException(failure);
    }

    /// <summary>Closes the sandbox, which discards everything inside it.</summary>
    public static void Close()
    {
        foreach (var process in SandboxProcesses())
        {
            try { process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { process.Dispose(); }
        }
    }

    // Earlier sessions are removed here, not at the end of their run: a sandbox left open for
    // inspection still has its folders mapped.
    private static string PrepareSessionFolder()
    {
        var root = Directory.CreateDirectory(SessionsRoot).FullName;
        foreach (var old in Directory.GetDirectories(root))
        {
            try
            {
                DirectoryCopy.ClearReadOnlyRecursive(old);
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Directory.CreateDirectory(Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"))).FullName;
    }
}
