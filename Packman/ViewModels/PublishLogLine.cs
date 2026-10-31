using Packman.Services;

namespace Packman.ViewModels;

/// <summary>One timestamped line of the publish console.</summary>
public sealed record PublishLogLine(DateTime Time, UploadLogLevel Level, string Text)
{
    /// <summary>The line as the Copy button puts it on the clipboard.</summary>
    public string Display => $"{Time:HH:mm:ss}  {Text}";
}
