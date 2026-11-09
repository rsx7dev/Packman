using System.Diagnostics;
using System.IO;
using System.Text;

namespace Packman.Services;

/// <summary>
/// Per-package log for an upload or a content update, written to
/// {logs folder}\{Upload|Update}\{App}-{date}.log. The logs folder is Settings ▸ Paths ▸
/// Upload logs folder (a share, for a team audit trail), else %LocalAppData%\Packman\Logs.
/// Tokens and SAS URIs never go through here. Every line except metadata is also shown in the
/// live console (<see cref="ActivityLog"/>).
/// </summary>
public sealed class UploadLogger : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public string LogFilePath { get; }

    private readonly string _operation;

    /// <summary>%LocalAppData%\Packman\Logs: used when no folder is set or the set one can't be written.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packman", "Logs");

    /// <summary>The Settings value; the app keeps it current. Empty uses <see cref="DefaultFolder"/>.</summary>
    public static string ConfiguredFolder { get; set; } = "";

    /// <summary>The logs folder a set value resolves to.</summary>
    public static string FolderFor(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultFolder : Environment.ExpandEnvironmentVariables(configured.Trim());

    /// <param name="operation">"Upload" or "Update": the log folder and the session label.</param>
    public UploadLogger(string applicationName, string operation = "Upload")
    {
        _operation = operation;
        var safeAppName = string.Join("_", applicationName.Split(Path.GetInvalidFileNameChars()));
        var fileName = $"{safeAppName}-{DateTime.Now:yyyy-MM-dd}.log";

        // A share that is offline must not stop a publish: fall back to the local folder and say so.
        string? fallbackReason = null;
        var logDirectory = Path.Combine(FolderFor(ConfiguredFolder), operation);
        LogFilePath = Path.Combine(logDirectory, fileName);
        try
        {
            _writer = Open(logDirectory, LogFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            fallbackReason = $"Could not write to the logs folder {logDirectory} ({ex.Message}); logging locally instead.";
            logDirectory = Path.Combine(DefaultFolder, operation);
            LogFilePath = Path.Combine(logDirectory, fileName);
        }

        try
        {
            _writer ??= Open(logDirectory, LogFilePath);
            var separator = new string('=', 80);
            Write(separator);
            Write($"{_operation} Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            // A shared folder collects logs from several packagers.
            Write($"By: {Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName}");
            Write(separator);
            Write("");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UploadLogger: Failed to initialize log file: {ex.Message}");
        }

        if (fallbackReason != null) Warning(fallbackReason);
    }

    private static StreamWriter Open(string directory, string path)
    {
        Directory.CreateDirectory(directory);
        return new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
    }

    public void Info(string message)
    {
        Write($"[INFO ] {DateTime.Now:HH:mm:ss} - {message}");
        ActivityLog.Shared.Info(message);
    }

    public void Success(string message)
    {
        Write($"[ OK  ] {DateTime.Now:HH:mm:ss} - {message}");
        ActivityLog.Shared.Success(message);
    }

    public void Warning(string message)
    {
        Write($"[WARN ] {DateTime.Now:HH:mm:ss} - {message}");
        ActivityLog.Shared.Warning(message);
    }

    public void Error(string message)
    {
        Write($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");
        ActivityLog.Shared.Error(message);
    }

    public void Error(string message, Exception ex)
    {
        ActivityLog.Shared.Error($"{message}: {ex.Message}");
        Write($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");
        Write($"        Exception: {ex.GetType().Name}");
        Write($"        Message: {ex.Message}");
        if (ex.InnerException != null)
            Write($"        Inner Exception: {ex.InnerException.Message}");
        Write("        Stack Trace:");
        Write($"        {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "        ")}");
    }

    public void Progress(int percentage, string message)
    {
        Write($"[{percentage,3}%] {DateTime.Now:HH:mm:ss} - {message}");
        ActivityLog.Shared.Info(message);
    }

    public void LogMetadata(string key, string value) => Write($"  {key}: {value}");

    public void Section(string sectionName)
    {
        Write("");
        Write($"--- {sectionName} ---");
        ActivityLog.Shared.Section(sectionName);
    }

    private void Write(string message)
    {
        if (_disposed || _writer == null) return;

        lock (_lock)
        {
            try
            {
                _writer.WriteLine(message);
                Debug.WriteLine(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UploadLogger: Failed to write to log: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_writer != null)
            {
                Write("");
                Write($"{_operation} Session Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Write(new string('=', 80));
                Write("");
                _writer.Dispose();
                _writer = null;
            }
            _disposed = true;
        }
    }
}
