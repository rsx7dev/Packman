using Packman.Helpers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Packman.Services;

/// <summary>
/// Runs a Remote test on this computer without WinRM. The same staging, scheduled task and log
/// monitor scripts a remote run sends over WinRM are run here by one elevated Windows
/// PowerShell process (one UAC prompt per run), so the install still happens as SYSTEM, or in
/// the logged-on user's session, exactly as on a remote target. Packman itself stays unelevated.
///
/// The helper reports back over a named pipe Packman owns, so the elevated process never writes
/// to a path the user controls. Closing the pipe is how a run is cancelled: the helper's next
/// write fails and it stops and removes the task.
/// </summary>
public static class LocalTestRunner
{
    /// <summary>Sent once a second while the task runs, so a closed pipe is noticed promptly. Never shown.</summary>
    internal const string Heartbeat = "__PACKMAN_ALIVE__";

    /// <summary>Prefix of the one line the helper sends when it could not carry on.</summary>
    internal const string Failed = "__PACKMAN_FAILED__";

    private const int UacDeclined = 1223;   // ERROR_CANCELLED

    /// <summary>On the OperationCanceledException of a cancelled run: true once the helper has exited.</summary>
    internal const string HelperExitedKey = "Packman.HelperExited";

    // Only this user, Administrators and SYSTEM may open the pipe. The default ACL lets Everyone
    // open it for reading, which is enough for another account to take the single instance and
    // stall the run. Not CurrentUserOnly: the elevated helper may be a different administrator.
    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        foreach (var sid in new[]
                 {
                     WindowsIdentity.GetCurrent().User!,
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                 })
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    /// <summary>True when <paramref name="computerName"/> names this computer.</summary>
    public static bool IsLocalTarget(string computerName)
    {
        var name = computerName?.Trim() ?? "";
        if (name.Length == 0) return false;
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1") return true;
        if (name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            return name.Equals(ip.HostName, StringComparison.OrdinalIgnoreCase)
                || (ip.DomainName.Length > 0 && name.Equals($"{ip.HostName}.{ip.DomainName}", StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The script the elevated helper runs: secure the staging folder, mirror the package into
    /// it, run <paramref name="taskScript"/> (which must emit <see cref="Heartbeat"/> while it
    /// waits) and optionally delete the staged copy. Every line it sends is console output,
    /// except the heartbeat and a <see cref="Failed"/> line.
    /// </summary>
    internal static string BuildHelperScript(string secureScript, string taskScript, string sourcePath,
        string stagedPackagePath, string taskName, bool cleanupAfterRun) =>
        """
        $ErrorActionPreference = 'Stop'
        # Identification only: whoever holds the other end can learn who we are, never act as us.
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PackmanPipe, [System.IO.Pipes.PipeDirection]::Out,
            [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification)
        $pipe.Connect(20000)
        $writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
        $writer.AutoFlush = $true
        # Connected: Packman is still listening. Completed: the task script ran to its end.
        $state = @{ Connected = $true; Completed = $false }

        function Send($value) {
            try {
                foreach ($line in ("$value" -split "`r?`n")) { $writer.WriteLine($line) }
            }
            catch {
                $state.Connected = $false
                throw 'Packman closed the connection'
            }
        }

        $taskName = '__TASK_NAME__'
        $source = '__SOURCE__'
        $staged = '__STAGED__'
        try {
            & {
        __SECURE__
            } | ForEach-Object { Send $_ }

            Send "Copying files to $staged..."
            $timer = [Diagnostics.Stopwatch]::StartNew()
            # Same switches as the remote copy; no /SEC, so files inherit the staging folder's ACL.
            $copyOutput = & ([Environment]::SystemDirectory + '\robocopy.exe') $source $staged /MIR /MT:16 /J /R:2 /W:5 /NP /NFL /NDL
            if ($LASTEXITCODE -ge 8) { throw "Robocopy failed with exit code $LASTEXITCODE. $($copyOutput -join ' ')" }
            $files = @(Get-ChildItem -LiteralPath $staged -Recurse -File -Force)
            $bytes = ($files | Measure-Object -Property Length -Sum).Sum
            Send ("Package size: {0} MB" -f [Math]::Round($bytes / 1MB, 2))
            Send ("[OK] {0} files in sync after {1} seconds" -f $files.Count, [Math]::Round($timer.Elapsed.TotalSeconds, 1))
            Send ''
            Send "Registering scheduled task '$taskName' on this computer..."
            Send ''

            & {
                # As in a remote session: a non-terminating error is reported, not fatal.
                $ErrorActionPreference = 'Continue'
        __TASK_SCRIPT__
            } 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) { Send "ERROR: $_" } else { Send $_ }
            }
            $state.Completed = $true

            if (__CLEANUP__) {
                Send ''
                Send 'Cleaning up...'
                try {
                    Remove-Item -LiteralPath $staged -Recurse -Force
                    Send '[OK] Cleanup completed'
                }
                catch { Send "WARNING: Cleanup failed: $($_.Exception.Message)" }
            }
        }
        catch {
            if ($state.Connected) {
                try { Send "__FAILED__$(("$($_.Exception.Message)" -replace "`r?`n", ' '))" } catch { }
            }
        }
        finally {
            # Cancelled or failed part-way: never leave the task running or registered.
            if (-not $state.Completed) {
                Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            }
            try { $writer.Dispose() } catch { }
            try { $pipe.Dispose() } catch { }
        }
        """
        .ReplaceOnce(new Dictionary<string, string>
        {
            ["__TASK_NAME__"] = PowerShellLiteral.SingleQuoted(taskName),
            ["__SOURCE__"] = PowerShellLiteral.SingleQuoted(sourcePath),
            ["__STAGED__"] = PowerShellLiteral.SingleQuoted(stagedPackagePath),
            ["__CLEANUP__"] = cleanupAfterRun ? "$true" : "$false",
            ["__FAILED__"] = Failed,
            ["__SECURE__"] = secureScript,
            ["__TASK_SCRIPT__"] = taskScript,
        });

    // One pass over the template: a value that happens to contain another placeholder's name
    // (a folder called __TASK_SCRIPT__, say) is never expanded a second time.
    private static string ReplaceOnce(this string template, Dictionary<string, string> values) =>
        System.Text.RegularExpressions.Regex.Replace(template, "__[A-Z_]+?__", m => values.TryGetValue(m.Value, out var v) ? v : m.Value);

    /// <summary>
    /// What the elevated PowerShell is started with. It reads the helper script once, runs it
    /// only if its SHA-256 is the one named here, and runs those same bytes from memory: the
    /// file sits in the user's profile, and nothing that swaps it after the UAC prompt is
    /// answered can get its own code run elevated.
    /// </summary>
    internal static string BuildBootstrap(string scriptPath, string sha256Hex, string pipeName) =>
        // Windows' own modules only: a module in the user's Documents must not be auto-loaded elevated.
        "$env:PSModulePath=\"$PSHOME\\Modules;$env:ProgramFiles\\WindowsPowerShell\\Modules\";" +
        $"$PackmanPipe='{PowerShellLiteral.SingleQuoted(pipeName)}';" +
        $"$b=[IO.File]::ReadAllBytes('{PowerShellLiteral.SingleQuoted(scriptPath)}');" +
        "$h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');" +
        $"if($h -ne '{sha256Hex}'){{exit 87}};" +
        "& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($b)))";

    /// <summary>
    /// Runs <paramref name="helperScript"/> in an elevated Windows PowerShell and passes each
    /// line it reports to <paramref name="onLine"/> (heartbeats excluded). Throws
    /// InvalidOperationException when the UAC prompt is declined or the helper reports a
    /// failure, and OperationCanceledException when <paramref name="ct"/> stops the run, after
    /// giving the helper a moment to remove its task.
    /// </summary>
    /// <param name="elevate">False runs the helper as the current user; tests only.</param>
    public static async Task RunAsync(string helperScript, Action<string> onLine, CancellationToken ct, bool elevate = true)
    {
        var pipeName = "packman-localtest-" + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packman", "LocalTest");
        Directory.CreateDirectory(folder);
        var scriptPath = Path.Combine(folder, pipeName + ".ps1");

        var scriptBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(helperScript);
        await File.WriteAllBytesAsync(scriptPath, scriptBytes, ct);
        var bootstrap = BuildBootstrap(scriptPath, Convert.ToHexString(SHA256.HashData(scriptBytes)), pipeName);

        var pipe = CreatePipe(pipeName);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                // Full path: an elevated process must not be found through PATH.
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "
                            + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)),
                UseShellExecute = true,   // required for the runas verb
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) start.Verb = "runas";

            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == UacDeclined)
            {
                throw new InvalidOperationException("The administrator prompt was declined, so nothing was run on this computer.");
            }

            var connected = pipe.WaitForConnectionAsync(ct);
            var exited = process.WaitForExitAsync(ct);
            var first = await Task.WhenAny(connected, exited, Task.Delay(TimeSpan.FromSeconds(45), ct));
            ct.ThrowIfCancellationRequested();
            if (first != connected)
                throw new InvalidOperationException(first == exited
                    ? $"The elevated helper stopped before it could report back (exit code {process.ExitCode})."
                    : "The elevated helper did not report back in time.");
            await connected;

            string? failure = null;
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line == Heartbeat) continue;
                if (line.StartsWith(Failed, StringComparison.Ordinal)) failure = line[Failed.Length..];
                else onLine(line);
            }
            if (failure != null) throw new InvalidOperationException(failure);
        }
        catch (OperationCanceledException ex)
        {
            // Closing our end makes the helper's next heartbeat fail; it then stops and removes the task.
            pipe.Dispose();
            var exited = process == null;
            if (process != null)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(grace.Token); exited = true; }
                catch (OperationCanceledException) { }
            }
            ex.Data[HelperExitedKey] = exited;
            throw;
        }
        finally
        {
            pipe.Dispose();
            process?.Dispose();
            try { File.Delete(scriptPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>
    /// <paramref name="path"/> with a mapped drive letter replaced by its UNC share. Drive
    /// mappings belong to the unelevated logon, so the elevated helper would not find Z:\.
    /// </summary>
    internal static string ToUncIfMapped(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.Length < 3 || full[1] != ':' || new DriveInfo(full[..1]).DriveType != DriveType.Network) return full;

            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return WNetGetConnection(full[..2], buffer, ref length) == 0 ? buffer + full[2..] : full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }
}
