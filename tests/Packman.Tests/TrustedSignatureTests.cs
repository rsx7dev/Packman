using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Packman.Helpers;
using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>
/// The signature as a device that enforces signing sees it. The other signing tests leave the
/// throwaway certificate untrusted, so Windows can only ever answer "not trusted" there. Here
/// the certificate is trusted for the length of the test, which is what an organisation does
/// with its own signing certificate, and the questions become the real ones: is the signature
/// Valid, does the script run under the AllSigned execution policy, and is a script changed
/// after signing refused.
///
/// Trusting a certificate means writing to the LocalMachine Root and TrustedPublisher stores,
/// so this runs on a CI runner only (a throwaway machine with administrator rights), never on
/// a contributor's computer. What it found is written to a file the workflow prints.
/// </summary>
public sealed class TrustedSignatureTests : IDisposable
{
    private static readonly bool OnCiRunner = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    private readonly TempSigningCertificates? _certificates = OnCiRunner ? new TempSigningCertificates() : null;
    private readonly string _root = Directory.CreateTempSubdirectory("packman-trusted-").FullName;
    private readonly List<string> _report = [];

    public void Dispose()
    {
        if (_certificates != null)
        {
            Untrust(_certificates.Valid);
            _certificates.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (!OnCiRunner) return;
        var folder = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (!string.IsNullOrEmpty(folder))
            File.WriteAllLines(Path.Combine(folder, "packman-trusted-signature.txt"), _report);
    }

    private static readonly (StoreName Name, string Label)[] TrustStores =
        [(StoreName.Root, "Trusted Root Certification Authorities"), (StoreName.TrustedPublisher, "Trusted Publishers")];

    /// <summary>The public half only, in the two stores AllSigned looks at.</summary>
    private static void Trust(X509Certificate2 certificate)
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        foreach (var (name, _) in TrustStores)
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(publicOnly);
        }
    }

    private static void Untrust(X509Certificate2 certificate)
    {
        foreach (var (name, _) in TrustStores)
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                foreach (var found in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false))
                    store.Remove(found);
            }
            catch (CryptographicException) { }
        }
    }

    private static bool IsTrusted(X509Certificate2 certificate) => TrustStores.Any(s =>
    {
        using var store = new X509Store(s.Name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
    });

    private static (int ExitCode, string Output, string Error) PowerShell(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "Windows PowerShell did not finish");
        return (process.ExitCode, output.Trim(), error.Result.Trim());
    }

    /// <summary>Get-AuthenticodeSignature's verdict on <paramref name="path"/>.</summary>
    private static (string Status, string Message) Status(string path)
    {
        var (_, output, error) = PowerShell("-Command",
            $"$s = Get-AuthenticodeSignature -LiteralPath '{PowerShellLiteral.SingleQuoted(path)}'; \"$($s.Status)`t$($s.StatusMessage)\"");
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        var fields = output.Split('\t', 2);
        return (fields[0], fields.Length > 1 ? fields[1] : "");
    }

    /// <summary>Runs the script the way a device that only accepts signed scripts would.</summary>
    private static (int ExitCode, string Output, string Error) RunAllSigned(string script) =>
        PowerShell("-ExecutionPolicy", "AllSigned", "-File", script);

    private string Package(string name)
    {
        var package = Fakes.Package(Path.Combine(_root, name));
        File.WriteAllText(PsadtLayout.ScriptPath(package),
            "$adtSession = @{\r\n    AppName = 'Signing test'\r\n}\r\nWrite-Output $adtSession.AppName\r\n");
        return package;
    }

    [Fact]
    public async Task ATrustedSignatureIsValidRunsUnderAllSignedAndIsRefusedOnceTheScriptChanges()
    {
        if (!OnCiRunner) return;   // Would write to this computer's LocalMachine trust stores.
        if (!RealTimestampServer.Reachable)
        {
            _report.Add("NOT RUN: the timestamp server could not be reached, so nothing was signed.");
            return;
        }
        var certificate = _certificates!.Valid;
        var signer = new NativeCodeSigner(certificate.Thumbprint, RealTimestampServer.Url);
        _report.Add($"Certificate: {certificate.Subject}, self-signed, code signing, thumbprint {certificate.Thumbprint}");

        // A small script that is safe to actually run, and the real template for the files PSADT imports.
        var package = Package("Trusted");
        var script = PsadtLayout.ScriptPath(package);
        await PackageSigningService.SignAsync(package, signer);
        var template = Path.Combine(_root, "RealTemplate");
        DirectoryCopy.Copy(BundledTools.PsadtTemplate, PsadtLayout.ApplicationFolder(template));
        var unsignedBefore = PackageSigningService.FilesToSign(PsadtLayout.ScriptPath(template));
        await PackageSigningService.SignAsync(template, signer);

        // 1. Signed but not trusted: the signature is there, and a device that enforces signing says no.
        Assert.False(IsTrusted(certificate));
        var (untrustedStatus, untrustedMessage) = Status(script);
        Assert.True(untrustedStatus is "UnknownError" or "NotTrusted", $"unexpected status '{untrustedStatus}': {untrustedMessage}");
        var refused = RunAllSigned(script);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.DoesNotContain("Signing test", refused.Output);
        _report.Add($"Before the certificate is trusted: status {untrustedStatus}; AllSigned refuses the script (exit {refused.ExitCode}).");

        // 2. Trusted, as an organisation's own signing certificate is on its devices.
        Trust(certificate);
        var (status, message) = Status(script);
        Assert.True(status == "Valid", $"the signature is not Valid once its certificate is trusted: {status}: {message}");
        var ran = RunAllSigned(script);
        Assert.True(ran.ExitCode == 0, $"AllSigned did not run the signed script (exit {ran.ExitCode}): {ran.Error}");
        Assert.Equal("Signing test", ran.Output);
        _report.Add($"After it is trusted: status {status}; AllSigned runs the script and it prints \"{ran.Output}\".");

        // 3. Every file Packman signed in the real template is Valid too, not only the deployment script.
        Assert.True(unsignedBefore.Count > 1, "the bundled template has nothing besides the deployment script for Packman to sign");
        foreach (var file in unsignedBefore)
        {
            var (fileStatus, fileMessage) = Status(file);
            Assert.True(fileStatus == "Valid", $"{Path.GetFileName(file)} is not Valid: {fileStatus}: {fileMessage}");
        }
        _report.Add($"Real PSADT template: all {unsignedBefore.Count} files Packman signed are Valid ({string.Join(", ", unsignedBefore.Select(Path.GetFileName))}).");

        // 4. One changed character after signing: the signature no longer covers the file.
        File.WriteAllText(script, File.ReadAllText(script).Replace("'Signing test'", "'Tampered'"));
        var (tamperedStatus, _) = Status(script);
        Assert.Equal("HashMismatch", tamperedStatus);
        var tampered = RunAllSigned(script);
        Assert.NotEqual(0, tampered.ExitCode);
        Assert.DoesNotContain("Tampered", tampered.Output);
        _report.Add($"After changing the signed script: status {tamperedStatus}; AllSigned refuses it (exit {tampered.ExitCode}).");

        // 5. Signing it again makes the changed script acceptable again.
        await PackageSigningService.SignAsync(package, signer);
        var (resignedStatus, resignedMessage) = Status(script);
        Assert.True(resignedStatus == "Valid", $"re-signing did not make the script Valid: {resignedStatus}: {resignedMessage}");
        Assert.Equal("Tampered", RunAllSigned(script).Output);
        _report.Add($"After Packman signs it again: status {resignedStatus}; AllSigned runs it.");

        Untrust(certificate);
        Assert.False(IsTrusted(certificate));
        _report.Add("The certificate was removed from the trust stores again.");
    }
}
