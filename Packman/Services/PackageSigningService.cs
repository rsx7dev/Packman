using System.IO;
using Packman.Helpers;

namespace Packman.Services;

/// <summary>Shared signing stage for desktop publishing and future integration hosts.</summary>
public static class PackageSigningService
{
    /// <returns>The signed script path, or null when signing was explicitly disabled.</returns>
    public static async Task<string?> SignAsync(string packagePath, IFileSigner? signer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (signer is null) return null;

        var scriptPath = PsadtScript.Find(packagePath)
            ?? throw new FileNotFoundException("The deployment script could not be found for signing.");

        foreach (var file in FilesToSign(scriptPath))
        {
            var result = await signer.SignFileAsync(file, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                var reason = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "The signing provider reported a failure." : result.ErrorMessage;
                throw new InvalidOperationException($"Code signing failed. Publishing stopped: {reason}");
            }
        }

        return scriptPath;
    }

    private static readonly string[] PowerShellExtensions = [".ps1", ".psm1", ".psd1"];

    /// <summary>
    /// The deployment script, then every other PowerShell file beside it that carries no
    /// signature yet (the template's Extensions module, which the script imports on every run).
    /// Files PSADT already signed keep their signature, and the installer payload in Files\ is
    /// the vendor's and is left alone.
    /// </summary>
    public static IReadOnlyList<string> FilesToSign(string scriptPath)
    {
        var files = new List<string> { scriptPath };
        var folder = Path.GetDirectoryName(scriptPath);
        if (folder == null) return files;

        var payload = Path.Combine(folder, PsadtLayout.FilesFolderName) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!PowerShellExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (file.Equals(scriptPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.StartsWith(payload, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.ReadAllText(file).Contains(PsadtScript.SignatureBegin, StringComparison.Ordinal)) continue;
            files.Add(file);
        }
        return files;
    }
}
