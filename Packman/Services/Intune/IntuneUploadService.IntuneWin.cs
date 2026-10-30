using Packman.Helpers;
using Packman.Models;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace Packman.Services;

public partial class IntuneUploadService
{
    private async Task SignApplicationFilesAsync(string packagePath, IUploadProgress? progress, UploadLogger log, CancellationToken ct)
    {
        log.Section("FILE SIGNING");
        if (_signer is null)
        {
            ct.ThrowIfCancellationRequested();
            progress?.UpdateProgress(15, "Code signing disabled — skipping file signing");
            log.Info("Code signing explicitly disabled in Settings");
            return;
        }

        progress?.UpdateProgress(10, "Signing the deployment script...");
        try
        {
            var scriptPath = await PackageSigningService.SignAsync(packagePath, _signer, ct);
            var scriptName = Path.GetFileName(scriptPath);
            log.Success($"{scriptName} signed successfully");
            progress?.UpdateProgress(15, $"{scriptName} signed successfully");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Error("Required code signing failed; publishing stopped", ex);
            throw;
        }
    }

    /// <summary>Runs IntuneWinAppUtil and returns the .intunewin it produced.</summary>
    private async Task<string> CreateIntuneWinFileAsync(string packagePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_converterPath) || !File.Exists(_converterPath))
            throw new FileNotFoundException($"IntuneWinAppUtil.exe not found at: '{_converterPath}'. Set the IntuneWinAppUtil path on the Settings page.");

        var applicationFolder = Path.Combine(packagePath, "Application");
        var setupFile = Path.Combine(applicationFolder, PsadtLayout.SetupFileName);
        var outputFolder = Path.Combine(packagePath, "Intune");

        if (!Directory.Exists(applicationFolder))
            throw new DirectoryNotFoundException($"Application folder not found: {applicationFolder}");

        if (!File.Exists(setupFile))
            throw new FileNotFoundException(
                $"{PsadtLayout.SetupFileName} not found in: {applicationFolder}. Packman builds PSADT v4 packages; check the PSADT Template Path on the Settings page.");

        Directory.CreateDirectory(outputFolder);

        // A stale .intunewin from an earlier run would be picked up as this build's output.
        foreach (var stale in Directory.GetFiles(outputFolder, "*.intunewin"))
            File.Delete(stale);

        var startInfo = new ProcessStartInfo
        {
            FileName = _converterPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c"); startInfo.ArgumentList.Add(applicationFolder);
        startInfo.ArgumentList.Add("-s"); startInfo.ArgumentList.Add(setupFile);
        startInfo.ArgumentList.Add("-o"); startInfo.ArgumentList.Add(outputFolder);
        startInfo.ArgumentList.Add("-q");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        string stdout, stderr;
        try
        {
            // Drain both pipes concurrently: in sequence this deadlocks as soon as the child
            // fills the buffer of the stream we are not reading yet.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(ct);
            stdout = outputTask.Result;
            stderr = errorTask.Result;
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process object does not stop the child; it would keep writing
            // into Intune\ and the next run would pick its output up.
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new Exception($"IntuneWinAppUtil failed with exit code {process.ExitCode}: {message}");
        }

        var produced = Directory.GetFiles(outputFolder, "*.intunewin");
        if (produced.Length == 0)
            throw new FileNotFoundException("IntuneWinAppUtil finished but produced no .intunewin file.");
        return produced[0];
    }

    /// <summary>
    /// Reads detection.xml out of the archive. The encryption keys stay in memory; nothing
    /// is written to %TEMP%, and the payload is streamed from the archive at upload time.
    /// </summary>
    private static IntuneWinInfo ExtractIntuneWinInfo(string intuneWinPath)
    {
        using var archive = ZipFile.OpenRead(intuneWinPath);

        var detectionEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("detection.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"detection.xml not found. Available files: {string.Join(", ", archive.Entries.Select(e => e.Name))}");

        XElement appInfo;
        using (var stream = detectionEntry.Open())
            appInfo = XDocument.Load(stream).Root
                ?? throw new Exception("detection.xml is empty.");

        if (!appInfo.Name.LocalName.Equals("ApplicationInfo", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Root element is not ApplicationInfo. Found: {appInfo.Name}");

        var encryption = appInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "EncryptionInfo")
            ?? throw new Exception($"EncryptionInfo not found. Available elements: {string.Join(", ", appInfo.Elements().Select(e => e.Name.LocalName))}");

        var contentEntry = FindContentEntry(archive)
            ?? throw new Exception("Could not find the encrypted content file in the archive.\n\nAvailable files:\n  "
                                   + string.Join("\n  ", archive.Entries.Select(e => $"{e.Name} ({e.Length:N0} bytes)")));

        static string Value(XElement parent, string localName)
            => parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

        var fileName = Value(appInfo, "FileName");
        if (string.IsNullOrEmpty(fileName)) fileName = Path.GetFileName(intuneWinPath);

        if (!long.TryParse(Value(appInfo, "UnencryptedContentSize"), out var unencryptedSize))
            unencryptedSize = contentEntry.Length;

        var encryptionKey = Value(encryption, "EncryptionKey");
        if (string.IsNullOrEmpty(encryptionKey))
            throw new Exception("EncryptionKey is missing from detection.xml");

        return new IntuneWinInfo
        {
            IntuneWinPath = intuneWinPath,
            FileName = fileName,
            UnencryptedContentSize = unencryptedSize,
            ContentEntryName = contentEntry.FullName,
            EncryptedContentSize = contentEntry.Length,
            EncryptionInfo = new EncryptionInfo
            {
                EncryptionKey = encryptionKey,
                MacKey = Value(encryption, "MacKey"),
                InitializationVector = Value(encryption, "InitializationVector"),
                Mac = Value(encryption, "Mac"),
                ProfileIdentifier = Value(encryption, "ProfileIdentifier") is { Length: > 0 } p ? p : "ProfileVersion1",
                FileDigest = Value(encryption, "FileDigest"),
                FileDigestAlgorithm = Value(encryption, "FileDigestAlgorithm") is { Length: > 0 } a ? a : "SHA256",
            },
        };
    }

    // IntuneWinAppUtil names the payload IntunePackage.intunewin inside Contents\; older
    // builds used Contents.dat. Fall back to the largest non-XML entry.
    private static ZipArchiveEntry? FindContentEntry(ZipArchive archive)
    {
        var known = new[] { "IntunePackage.intunewin", "Contents.dat", "IntunePackage.dat" };
        foreach (var name in known)
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry != null) return entry;
        }

        return archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase) && e.Length > 1000)
            ?? archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.Where(e => !e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Length).FirstOrDefault();
    }

    private async Task<string> CreateWin32LobAppAsync(
        ApplicationInfo appInfo,
        string installCommand,
        string uninstallCommand,
        string description,
        List<DetectionRule> detectionRules,
        string installContext,
        IntuneWinInfo intuneWin,
        string? iconPath,
        RequirementInfo? requirements,
        List<ReturnCodeInfo>? returnCodes,
        string? privacyUrl,
        string? informationUrl,
        string? restartBehavior,
        int? maxRunTimeMinutes,
        CancellationToken ct)
    {
        // A guessed rule uploads fine and then never detects the app, so refuse instead.
        if (detectionRules.Count == 0)
            throw new InvalidOperationException(
                "No usable detection rule was supplied. Set a detection rule on the Upload step before publishing.");

        var payload = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["displayName"] = appInfo.DisplayName,
            ["description"] = description,
            ["publisher"] = appInfo.Manufacturer,
            ["displayVersion"] = appInfo.Version,
            ["installCommandLine"] = installCommand,
            ["uninstallCommandLine"] = uninstallCommand,
            ["applicableArchitectures"] = "none",
            ["allowedArchitectures"] = AllowedArchitectures(appInfo.Architecture),
            ["minimumSupportedWindowsRelease"] = (requirements ?? new RequirementInfo()).MinimumSupportedWindowsRelease,
            ["fileName"] = intuneWin.FileName,
            ["setupFilePath"] = PsadtLayout.SetupFileName,
            ["installExperience"] = new Dictionary<string, object>
            {
                // Graph enum values are lower case; "System"/"User" only work by tolerance.
                ["runAsAccount"] = installContext.Equals("User", StringComparison.OrdinalIgnoreCase) ? "user" : "system",
                ["deviceRestartBehavior"] = RestartBehavior(restartBehavior),
                // Intune accepts 1–1440 minutes.
                ["maxRunTimeInMinutes"] = Math.Clamp(maxRunTimeMinutes ?? AppSettings.IntuneDefaultsConfig.DefaultMaxRunTimeMinutes, 1, 1440),
            },
            ["detectionRules"] = detectionRules.Select(DetectionRuleGraph.Serialize).ToList(),
            ["returnCodes"] = (returnCodes is { Count: > 0 } ? returnCodes : ReturnCodeInfo.Defaults())
                .Select(rc => new Dictionary<string, object> { ["returnCode"] = rc.Code, ["type"] = rc.GraphType })
                .ToList(),
        };

        if (!string.IsNullOrWhiteSpace(privacyUrl))
            payload["privacyInformationUrl"] = privacyUrl.Trim();
        if (!string.IsNullOrWhiteSpace(informationUrl))
            payload["informationUrl"] = informationUrl.Trim();

        if (requirements?.MinimumFreeDiskSpaceMB is int disk)
            payload["minimumFreeDiskSpaceInMB"] = disk;
        if (requirements?.MinimumMemoryMB is int mem)
            payload["minimumMemoryInMB"] = mem;
        if (requirements?.MinimumNumberOfProcessors is int cpus)
            payload["minimumNumberOfProcessors"] = cpus;
        if (requirements?.MinimumCpuSpeedMHz is int mhz)
            payload["minimumCpuSpeedInMHz"] = mhz;

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) && ReadIcon(iconPath) is { } icon)
            payload["largeIcon"] = icon;

        var created = (await _graph.PostAsync(GraphClient.MobileApps, payload, "Create Win32 app", ct)).Json;
        return created.GetSafeString("id") is { Length: > 0 } id ? id : throw new Exception("App ID not returned from creation");
    }

    /// <summary>
    /// The package's architecture says what the installer is built for, so it only rules out
    /// devices that cannot run it: an x64 package skips 32-bit Windows, a 32-bit package runs
    /// everywhere, and ARM64 runs both through emulation. Unknown keeps every architecture.
    /// </summary>
    internal static string AllowedArchitectures(string? architecture) =>
        architecture?.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" => "x64,arm64",
            "arm64" => "arm64",
            _ => "x86,x64,arm64",
        };

    // A hand-edited settings file must not produce a Graph 400 at the end of an upload.
    private static string RestartBehavior(string? value) =>
        AppSettings.IntuneDefaultsConfig.RestartBehaviors.Any(o => o.Value == value)
            ? value!
            : AppSettings.IntuneDefaultsConfig.DefaultRestartBehavior;

    private static Dictionary<string, object>? ReadIcon(string iconPath)
    {
        try
        {
            var mimeType = Path.GetExtension(iconPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".ico" => "image/x-icon",
                _ => "image/png",
            };
            return new Dictionary<string, object>
            {
                ["type"] = mimeType,
                ["value"] = Convert.ToBase64String(File.ReadAllBytes(iconPath)),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading icon: {ex.Message}");
            return null;
        }
    }
}
