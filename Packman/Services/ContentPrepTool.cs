using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>
/// Microsoft's Win32 Content Prep Tool (IntuneWinAppUtil.exe). Its license terms do not allow
/// Packman to redistribute it, so the pinned build is downloaded from Microsoft's repository on
/// the first publish and kept under %LocalAppData%. It is run only while its SHA-256 matches.
/// </summary>
public static class ContentPrepTool
{
    public const string Version = "1.8.7";
    public const string Sha256 = "c1ba45b5cb939e84af064bb7ff4b38fb3dfe33c8dc1078fd9b157672eae671f6";
    public const string LicenseUrl = "https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/blob/v1.8.7/Microsoft%20License%20Terms%20For%20Win32%20Content%20Prep%20Tool.pdf";

    public static readonly Uri DownloadUri =
        new("https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/v1.8.7/IntuneWinAppUtil.exe");

    /// <summary>The real tool is well under 1 MB; anything far larger is not it.</summary>
    private const int MaxBytes = 16 * 1024 * 1024;

    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>True when <paramref name="path"/> is the copy Packman downloads, not one the user chose.</summary>
    public static bool IsManaged(string? path) =>
        string.Equals(path, BundledTools.IntuneWinAppUtil, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the file exists and is the pinned build.</summary>
    public static bool IsVerified(string path) => IsVerified(path, Sha256);

    internal static bool IsVerified(string path, string sha256)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Matches(SHA256.HashData(stream), sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes sure the pinned build is at <paramref name="path"/>, downloading it when it is
    /// missing or has been changed. Returns true when it had to download.
    /// </summary>
    public static Task<bool> EnsureAsync(string path, CancellationToken ct = default) => EnsureAsync(path, Sha256, ct);

    internal static async Task<bool> EnsureAsync(string path, string sha256, CancellationToken ct)
    {
        if (IsVerified(path, sha256)) return false;

        byte[] bytes;
        try
        {
            using var response = await Http.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new InvalidOperationException(Refused("the file is larger than expected"));
            bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(ct), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new InvalidOperationException(
                $"IntuneWinAppUtil.exe {Version} could not be downloaded from {DownloadUri.Host}: {ex.Message} " +
                "Check the connection, or download the tool from Microsoft yourself and set IntuneWinAppUtil Path under Settings → Paths.", ex);
        }

        var hash = SHA256.HashData(bytes);
        if (!Matches(hash, sha256))
            throw new InvalidOperationException(Refused($"its SHA-256 is {Convert.ToHexStringLower(hash)}, not {sha256}"));

        // Written beside the target and moved into place, so a cancelled or failed write
        // never leaves a partial executable where the next publish would look.
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { }
        }
        return true;
    }

    private static string Refused(string reason) =>
        $"The IntuneWinAppUtil.exe downloaded from {DownloadUri.Host} was not saved: {reason}. " +
        $"Packman only runs the pinned {Version} build. Download the tool from Microsoft yourself and set IntuneWinAppUtil Path under Settings → Paths.";

    private static bool Matches(byte[] hash, string sha256) =>
        Convert.ToHexStringLower(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        await using var _ = stream;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new InvalidOperationException(Refused("the file is larger than expected"));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
