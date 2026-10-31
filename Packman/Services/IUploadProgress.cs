namespace Packman.Services;

/// <summary>How a console line is coloured.</summary>
public enum UploadLogLevel { Info, Success, Warning, Error, Section }

public interface IUploadProgress
{
    void UpdateProgress(int percentage, string message);

    /// <summary>One line for the publish console, already free of secrets. Callers that only show a percentage ignore it.</summary>
    void Log(UploadLogLevel level, string message) { }
}
