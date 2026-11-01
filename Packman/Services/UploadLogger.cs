using System.Diagnostics;
using System.IO;
using System.Text;

namespace Packman.Services;

/// <summary>
/// Per-package log for an upload or a content update, written to
/// %LocalAppData%\Packman\Logs\{Upload|Update}\{App}-{date}.log.
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

    /// <param name="operation">"Upload" or "Update": the log folder and the session label.</param>
    public UploadLogger(string applicationName, string operation = "Upload")
    {
        _operation = operation;
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packman", "Logs", operation);
        Directory.CreateDirectory(logDirectory);

        var safeAppName = string.Join("_", applicationName.Split(Path.GetInvalidFileNameChars()));
        LogFilePath = Path.Combine(logDirectory, $"{safeAppName}-{DateTime.Now:yyyy-MM-dd}.log");

        try
        {
            _writer = new StreamWriter(LogFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
            var separator = new string('=', 80);
            Write(separator);
            Write($"{_operation} Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Write(separator);
            Write("");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UploadLogger: Failed to initialize log file: {ex.Message}");
        }
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
