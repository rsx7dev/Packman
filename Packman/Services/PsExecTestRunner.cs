using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>
/// Runs a PowerShell script on a remote computer through Sysinternals PsExec, for targets where
/// WinRM is not enabled. PsExec needs the admin shares (SMB) and the Service Control Manager,
/// and an account that is an administrator on the target.
///
/// PsExec is not shipped with Packman: its license does not allow redistribution, so the
/// packager supplies it (Settings ▸ Paths, or on PATH).
///
/// The script travels as a file in the target's Windows\Temp, which only administrators can
/// replace, and is run by a short bootstrap that checks its SHA-256 first.
/// </summary>
public static class PsExecTestRunner
{
    /// <summary>The last line of a script that ran to its end; without it PsExec itself failed.</summary>
    internal const string Done = "__PACKMAN_PSEXEC_DONE__";

    private const int HashMismatch = 87;
    private static readonly string[] FileNames = ["PsExec64.exe", "PsExec.exe"];

    // What PsExec prints about itself on stderr while it connects; not worth a console line.
    private static readonly string[] Chatter =
    [
        "Connecting to ", "Starting PSEXESVC", "Copying authentication key", "Connecting with PsExec service",
        "Starting powershell.exe on ", "powershell.exe exited on ", "PsExec v", "Copyright (C)", "Sysinternals - ",
    ];

    /// <summary>
    /// The PsExec to use: <paramref name="configuredPath"/> when it is set (null if that file is
    /// missing), otherwise the first PsExec64.exe or PsExec.exe in a rooted PATH folder.
    /// </summary>
    public static string? Find(string? configuredPath) =>
        Find(configuredPath, Environment.GetEnvironmentVariable("PATH"));

    internal static string? Find(string? configuredPath, string? pathVariable)
    {
        var configured = configuredPath?.Trim().Trim('"') ?? "";
        if (configured.Length > 0)
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        foreach (var folder in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A relative PATH entry would resolve against whatever folder Packman was started in.
            if (!Path.IsPathRooted(folder)) continue;
            foreach (var name in FileNames)
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>Why <paramref name="configuredPath"/> can't be used, or empty when it can (blank means "look on PATH").</summary>
    public static string PathProblem(string? configuredPath)
    {
        var value = configuredPath?.Trim().Trim('"') ?? "";
        if (value.Length == 0) return "";
        if (Directory.Exists(value)) return "This is a folder. Point to PsExec64.exe or PsExec.exe inside it.";
        if (!File.Exists(value)) return "This file does not exist or cannot be reached.";
        return FileNames.Contains(Path.GetFileName(value), StringComparer.OrdinalIgnoreCase)
            ? "" : "This file is not PsExec64.exe or PsExec.exe.";
    }

    /// <summary>
    /// <paramref name="script"/> wrapped so that everything it writes, errors included, reaches
    /// standard output as text, followed by <see cref="Done"/>. A non-terminating error is
    /// reported and the script carries on, as it does in a remote session.
    /// </summary>
    internal static string WrapScript(string script) =>
        "& {\n$ErrorActionPreference = 'Continue'\n" + script + "\n} 2>&1 | ForEach-Object {\n" +
        "    if ($_ -is [System.Management.Automation.ErrorRecord]) { \"ERROR: $_\" } else { \"$_\" }\n}\n" +
        $"'{Done}'\n";

    /// <summary>
    /// The command the remote Windows PowerShell is started with: read the script once, refuse
    /// it unless its SHA-256 is the one named here, run those bytes and delete the file.
    /// </summary>
    internal static string BuildBootstrap(string scriptFileName, string sha256Hex) =>
        "[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false);" +
        "$env:PSModulePath=\"$PSHOME\\Modules;$env:ProgramFiles\\WindowsPowerShell\\Modules\";" +
        $"$p=Join-Path $env:SystemRoot 'Temp\\{PowerShellLiteral.SingleQuoted(scriptFileName)}';" +
        "$b=[IO.File]::ReadAllBytes($p);" +
        "Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue;" +
        "$h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-','');" +
        $"if($h -ne '{sha256Hex}'){{exit {HashMismatch}}};" +
        "& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($b)))";

    /// <summary>
    /// PsExec's arguments: run Windows PowerShell on <paramref name="computer"/> as SYSTEM with
    /// <paramref name="bootstrap"/>. -accepteula records the user's acceptance of the
    /// Sysinternals license on this computer, which the Remote test page says before a run.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(string computer, string bootstrap) =>
    [
        @"\\" + computer, "-accepteula", "-nobanner", "-s",
        "powershell.exe", "-NoProfile", "-NonInteractive", "-InputFormat", "None", "-ExecutionPolicy", "Bypass",
        "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)),
    ];

    internal static bool IsChatter(string line) =>
        line.Length == 0 || Chatter.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs <paramref name="script"/> on <paramref name="computer"/> as SYSTEM and passes each
    /// line it prints to <paramref name="onLine"/>. Throws InvalidOperationException when PsExec
    /// could not run the script, and OperationCanceledException when <paramref name="ct"/> stops
    /// it (PsExec is killed; the caller cleans up what the script had started).
    /// </summary>
    public static async Task RunScriptAsync(string psExecPath, string computer, string script, Action<string> onLine, CancellationToken ct)
    {
        if (!RemoteTestService.IsValidComputerName(computer))
            throw new ArgumentException($"'{computer}' is not a valid computer name", nameof(computer));

        var fileName = $"Packman_{Guid.NewGuid():N}.ps1";
        var remoteFile = $@"\\{computer}\ADMIN$\Temp\{fileName}";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WrapScript(script));
        try
        {
            await File.WriteAllBytesAsync(remoteFile, bytes, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $@"Could not write to \\{computer}\ADMIN$: {ex.Message} PsExec needs the admin shares (File and Printer Sharing) " +
                "and an account that is an administrator on the target.", ex);
        }

        var start = new ProcessStartInfo
        {
            FileName = psExecPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in BuildArguments(computer, BuildBootstrap(fileName, Convert.ToHexString(SHA256.HashData(bytes)))))
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var completed = false;
        var notes = new List<string>();
        try
        {
            process.Start();
            // PowerShell under PsExec waits on standard input until it is closed.
            process.StandardInput.Close();

            var errors = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                {
                    line = line.Trim();
                    if (!IsChatter(line)) lock (notes) notes.Add(line);
                }
            }, ct);

            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line == Done) completed = true;
                else onLine(line);
            }
            await errors;
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        finally
        {
            // The bootstrap deletes it; this covers a run that never got that far.
            try { File.Delete(remoteFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (completed) return;
        string detail;
        lock (notes) detail = string.Join(" ", notes);
        throw new InvalidOperationException(process.ExitCode == HashMismatch
            ? $"The script on {computer} was changed before it ran, so it was not run."
            : $"PsExec could not run the script on {computer} (exit code {process.ExitCode}). {detail}".TrimEnd());
    }
}
