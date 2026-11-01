using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;

namespace Packman.Services;

/// <summary>How a console line is coloured.</summary>
public enum ActivityLevel { Info, Success, Warning, Error, Section }

/// <summary>One timestamped line of the live console.</summary>
public sealed record ActivityLine(DateTime Time, ActivityLevel Level, string Text)
{
    /// <summary>The line as the Copy button puts it on the clipboard.</summary>
    public string Display => $"{Time:HH:mm:ss}  {Text}";
}

/// <summary>
/// What the live console in the right column shows: package creation, upload and update report
/// here as they run, from any thread. Secrets are removed on the way in.
/// </summary>
public sealed partial class ActivityLog
{
    private const int MaxLines = 500;

    public static ActivityLog Shared { get; } = new();

    public ObservableCollection<ActivityLine> Lines { get; } = [];

    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Display));

    public void Info(string message) => Add(ActivityLevel.Info, message);
    public void Success(string message) => Add(ActivityLevel.Success, message);
    public void Warning(string message) => Add(ActivityLevel.Warning, message);
    public void Error(string message) => Add(ActivityLevel.Error, message);
    public void Section(string message) => Add(ActivityLevel.Section, message);

    public void Add(ActivityLevel level, string message)
    {
        var line = new ActivityLine(DateTime.Now, level, Redact(message));
        OnUiThread(() =>
        {
            Lines.Add(line);
            if (Lines.Count > MaxLines) Lines.RemoveAt(0);
        });
    }

    public void Clear() => OnUiThread(Lines.Clear);

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.InvokeAsync(action);
    }

    // A SAS URL keeps its path but loses the query (sig, se, sp...); the rest are tokens
    // and key=value secrets that an error message could echo.
    [GeneratedRegex(@"(?<url>https?://[^\s?#'""<>]+)[?#][^\s'""<>]*")]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+|\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*|\b[0-9A-Fa-f]{40}\b", RegexOptions.IgnoreCase)]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"\b(?<key>sig|token|access_token|refresh_token|client_secret|client_assertion|password|secret)=[^&\s'""]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPair();

    /// <summary>Removes what must never reach the console: URL query strings, bearer and JWT tokens, secret pairs and certificate thumbprints.</summary>
    internal static string Redact(string text)
    {
        text = UrlQuery().Replace(text, "${url}");
        text = SecretPair().Replace(text, "${key}=***");
        return TokenLike().Replace(text, "***");
    }
}
