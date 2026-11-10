using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Packman.Services;

/// <summary>
/// Authenticode signer over SignerSignEx2: no PowerShell, no temp PFX on disk.
/// The certificate comes from CurrentUser\My or LocalMachine\My by thumbprint.
/// </summary>
public class NativeCodeSigner : IFileSigner
{
    private readonly string _thumbprint;
    private readonly string _timestampServer;

    public NativeCodeSigner(string thumbprint, string? timestampServer = null)
    {
        _thumbprint = (thumbprint ?? "").Replace(" ", "").Trim();
        // Never a host the packager did not name: an empty value is refused when signing.
        _timestampServer = timestampServer?.Trim() ?? "";
    }

    private X509Certificate2? GetCertificate()
    {
        if (string.IsNullOrEmpty(_thumbprint))
            return null;

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, _thumbprint, validOnly: false);
                if (found.Count == 0) continue;

                // Find hands back fresh contexts; keep only the one we return.
                for (int i = 1; i < found.Count; i++) found[i].Dispose();
                return found[0];
            }
            catch { /* store not accessible */ }
        }

        return null;
    }

    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private const string AnyEku = "2.5.29.37.0";   // anyExtendedKeyUsage: Windows accepts it for code signing

    public bool IsCertificateAvailable() => CertificateProblem() == null;

    /// <summary>
    /// Why the configured certificate can't sign code, or null when it can: not found, no
    /// private key, outside its validity dates, or issued for something other than code
    /// signing. Windows signs with such a certificate anyway and devices then reject the result.
    /// </summary>
    public string? CertificateProblem()
    {
        using var cert = GetCertificate();
        return CertificateProblem(cert);
    }

    private string? CertificateProblem(X509Certificate2? cert)
    {
        if (cert is null)
            return string.IsNullOrEmpty(_thumbprint)
                ? "No signing certificate is selected. Choose a code-signing certificate in Settings ▸ Code signing."
                : $"The signing certificate {_thumbprint} is not in the CurrentUser or LocalMachine Personal store.";
        return UsageProblem(cert);
    }

    /// <summary>Why <paramref name="cert"/> can't sign code, or null when it can. Also filters the Settings picker.</summary>
    public static string? UsageProblem(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey)
            return $"The certificate \"{Name(cert)}\" has no private key on this computer, so it cannot sign.";

        var now = DateTime.Now;
        if (now > cert.NotAfter)
            return $"The certificate \"{Name(cert)}\" expired on {cert.NotAfter:yyyy-MM-dd}.";
        if (now < cert.NotBefore)
            return $"The certificate \"{Name(cert)}\" is not valid until {cert.NotBefore:yyyy-MM-dd}.";

        // No EKU extension means every usage is allowed; one that lists usages must list code signing.
        var usages = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (usages != null && !usages.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is CodeSigningEku or AnyEku))
            return $"The certificate \"{Name(cert)}\" is not issued for code signing (its Enhanced Key Usage lacks Code Signing).";
        return null;
    }

    private static string Name(X509Certificate2 cert)
    {
        var name = cert.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? cert.Thumbprint : name;
    }

    /// <summary>SHA-256 Authenticode with an RFC 3161 timestamp.</summary>
    public Task<SigningResult> SignFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var cert = GetCertificate();
            if (CertificateProblem(cert) is { } problem)
                return new SigningResult { FilePath = filePath, Success = false, ErrorMessage = problem };

            return SignOneFile(filePath, cert);
        }, cancellationToken);
    }

    private const uint SHA256_ALG_ID = CALG_SHA_256;
    private const string SHA256_OID = "2.16.840.1.101.3.4.2.1";

    private SigningResult SignOneFile(string filePath, X509Certificate2 cert)
    {
        var result = new SigningResult { FilePath = filePath };

        if (!File.Exists(filePath))
        {
            result.Success = false;
            result.ErrorMessage = $"File does not exist: {filePath}";
            return result;
        }

        IntPtr hMemStore = IntPtr.Zero;
        IntPtr pSubjectInfo = IntPtr.Zero;
        IntPtr pFileInfo = IntPtr.Zero;
        IntPtr pSignerCert = IntPtr.Zero;
        IntPtr pCertStoreInfo = IntPtr.Zero;
        IntPtr pSignatureInfo = IntPtr.Zero;
        IntPtr pIndex = IntPtr.Zero;
        IntPtr pSignerContext = IntPtr.Zero;

        try
        {
            hMemStore = CertOpenStore(
                lpszStoreProvider: (IntPtr)CERT_STORE_PROV_MEMORY,
                dwMsgAndCertEncodingType: X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                hCryptProv: IntPtr.Zero,
                dwFlags: 0,
                pvPara: IntPtr.Zero);
            if (hMemStore == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CertOpenStore failed");

            if (!CertAddCertificateContextToStore(hMemStore, cert.Handle, CERT_STORE_ADD_ALWAYS, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CertAddCertificateContextToStore failed");

            var fileInfo = new SIGNER_FILE_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_FILE_INFO>(),
                pwszFileName = filePath,
                hFile = IntPtr.Zero
            };
            pFileInfo = Marshal.AllocHGlobal((int)fileInfo.cbSize);
            Marshal.StructureToPtr(fileInfo, pFileInfo, fDeleteOld: false);

            pIndex = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(pIndex, 0);

            var subjectInfo = new SIGNER_SUBJECT_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_SUBJECT_INFO>(),
                pdwIndex = pIndex,
                dwSubjectChoice = SIGNER_SUBJECT_FILE,
                union1 = pFileInfo
            };
            pSubjectInfo = Marshal.AllocHGlobal((int)subjectInfo.cbSize);
            Marshal.StructureToPtr(subjectInfo, pSubjectInfo, fDeleteOld: false);

            var certStoreInfo = new SIGNER_CERT_STORE_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_CERT_STORE_INFO>(),
                pSigningCert = cert.Handle,
                dwCertPolicy = SIGNER_CERT_POLICY_CHAIN,
                hCertStore = hMemStore
            };
            pCertStoreInfo = Marshal.AllocHGlobal((int)certStoreInfo.cbSize);
            Marshal.StructureToPtr(certStoreInfo, pCertStoreInfo, fDeleteOld: false);

            var signerCert = new SIGNER_CERT
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_CERT>(),
                dwCertChoice = SIGNER_CERT_STORE,
                union1 = pCertStoreInfo,
                hwnd = IntPtr.Zero
            };
            pSignerCert = Marshal.AllocHGlobal((int)signerCert.cbSize);
            Marshal.StructureToPtr(signerCert, pSignerCert, fDeleteOld: false);

            var sigInfo = new SIGNER_SIGNATURE_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_SIGNATURE_INFO>(),
                algidHash = SHA256_ALG_ID,
                dwAttrChoice = SIGNER_NO_ATTR,
                union1 = IntPtr.Zero,
                psAuthenticated = IntPtr.Zero,
                psUnauthenticated = IntPtr.Zero
            };
            pSignatureInfo = Marshal.AllocHGlobal((int)sigInfo.cbSize);
            Marshal.StructureToPtr(sigInfo, pSignatureInfo, fDeleteOld: false);

            if (string.IsNullOrWhiteSpace(_timestampServer))
            {
                result.Success = false;
                result.ErrorMessage = "No timestamp server is set. Enter one in Settings ▸ Code signing (for example http://timestamp.digicert.com): "
                                      + "Packman always timestamps, so a signature stays valid after the certificate expires.";
                return result;
            }

            int hr = SignerSignEx2(
                dwFlags: 0,
                pSubjectInfo: pSubjectInfo,
                pSignerCert: pSignerCert,
                pSignatureInfo: pSignatureInfo,
                pProviderInfo: IntPtr.Zero,
                dwTimestampFlags: SIGNER_TIMESTAMP_RFC3161,
                pszTimestampAlgorithmOid: SHA256_OID,
                pwszHttpTimeStamp: _timestampServer,
                psRequest: IntPtr.Zero,
                pSipData: IntPtr.Zero,
                ppSignerContext: out pSignerContext,
                pCryptoPolicy: IntPtr.Zero,
                pReserved: IntPtr.Zero);

            if (hr == 0)
            {
                result.Success = true;
            }
            else
            {
                result.Success = false;
                result.HResult = hr;
                result.ErrorMessage = IsNetworkFailure(hr)
                    ? $"Could not reach the timestamp server {_timestampServer} (0x{hr:X8}). Check the address in Settings ▸ Code signing, the network and any proxy."
                    : HResultToMessage(hr);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Debug.WriteLine($"Exception signing {filePath}: {ex}");
        }
        finally
        {
            // Native code holds cert.Handle past our last managed read of cert.
            GC.KeepAlive(cert);

            if (pSignerContext != IntPtr.Zero) SignerFreeSignerContext(pSignerContext);
            if (pSignatureInfo != IntPtr.Zero) Marshal.FreeHGlobal(pSignatureInfo);
            if (pSignerCert != IntPtr.Zero) Marshal.FreeHGlobal(pSignerCert);
            if (pCertStoreInfo != IntPtr.Zero) Marshal.FreeHGlobal(pCertStoreInfo);
            if (pSubjectInfo != IntPtr.Zero) Marshal.FreeHGlobal(pSubjectInfo);
            if (pFileInfo != IntPtr.Zero)
            {
                Marshal.DestroyStructure<SIGNER_FILE_INFO>(pFileInfo);
                Marshal.FreeHGlobal(pFileInfo);
            }
            if (pIndex != IntPtr.Zero) Marshal.FreeHGlobal(pIndex);
            if (hMemStore != IntPtr.Zero) CertCloseStore(hMemStore, 0);
        }

        return result;
    }

    // FACILITY_WIN32 carrying a WinINet/WinHTTP error (12000-12199): the only network call
    // signing makes is the timestamp request.
    private static bool IsNetworkFailure(int hr) =>
        (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) && (hr & 0xFFFF) is >= 12000 and <= 12199;

    private static string HResultToMessage(int hr)
    {
        try
        {
            var ex = Marshal.GetExceptionForHR(hr);
            if (ex is not null && !string.IsNullOrWhiteSpace(ex.Message))
                return $"0x{hr:X8}: {ex.Message}";
        }
        catch { /* fall through */ }
        return $"SignerSignEx2 failed with HRESULT 0x{hr:X8}";
    }

    private const uint SIGNER_SUBJECT_FILE = 0x01;
    private const uint SIGNER_CERT_STORE = 0x02;
    private const uint SIGNER_CERT_POLICY_CHAIN = 0x02;
    private const uint SIGNER_NO_ATTR = 0x00;
    private const uint SIGNER_TIMESTAMP_RFC3161 = 0x02;
    private const uint CALG_SHA_256 = 0x0000800c;
    private const int CERT_STORE_PROV_MEMORY = 2;
    private const uint CERT_STORE_ADD_ALWAYS = 4;
    private const uint X509_ASN_ENCODING = 0x00000001;
    private const uint PKCS_7_ASN_ENCODING = 0x00010000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SIGNER_FILE_INFO
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pwszFileName;
        public IntPtr hFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_SUBJECT_INFO
    {
        public uint cbSize;
        public IntPtr pdwIndex;
        public uint dwSubjectChoice;
        public IntPtr union1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CERT_STORE_INFO
    {
        public uint cbSize;
        public IntPtr pSigningCert;
        public uint dwCertPolicy;
        public IntPtr hCertStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CERT
    {
        public uint cbSize;
        public uint dwCertChoice;
        public IntPtr union1;
        public IntPtr hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_SIGNATURE_INFO
    {
        public uint cbSize;
        public uint algidHash;
        public uint dwAttrChoice;
        public IntPtr union1;
        public IntPtr psAuthenticated;
        public IntPtr psUnauthenticated;
    }

    [DllImport("Mssign32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern int SignerSignEx2(
        uint dwFlags,
        IntPtr pSubjectInfo,
        IntPtr pSignerCert,
        IntPtr pSignatureInfo,
        IntPtr pProviderInfo,
        uint dwTimestampFlags,
        [MarshalAs(UnmanagedType.LPStr)] string? pszTimestampAlgorithmOid,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwszHttpTimeStamp,
        IntPtr psRequest,
        IntPtr pSipData,
        out IntPtr ppSignerContext,
        IntPtr pCryptoPolicy,
        IntPtr pReserved);

    [DllImport("Mssign32.dll", ExactSpelling = true)]
    private static extern int SignerFreeSignerContext(IntPtr pSignerContext);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CertOpenStore(
        IntPtr lpszStoreProvider,
        uint dwMsgAndCertEncodingType,
        IntPtr hCryptProv,
        uint dwFlags,
        IntPtr pvPara);

    [DllImport("Crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertAddCertificateContextToStore(
        IntPtr hCertStore,
        IntPtr pCertContext,
        uint dwAddDisposition,
        IntPtr ppStoreContext);

    [DllImport("Crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr hCertStore, uint dwFlags);
}

public class SigningResult
{
    public string FilePath { get; set; } = "";
    public bool Success { get; set; }
    public int HResult { get; set; }
    public string ErrorMessage { get; set; } = "";
}
