using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Inbrisk.Setup;

/// <summary>
/// Authenticode signature verification using Win32 <c>WinVerifyTrust</c>
/// (<c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>) plus signer certificate
/// thumbprint and subject checks.
///
/// Supports both embedded PE Authenticode signatures (<c>WTD_CHOICE_FILE</c>)
/// and Windows Security Catalog signatures (<c>WTD_CHOICE_CATALOG</c>, used by
/// OS binaries such as <c>cmd.exe</c> and <c>notepad.exe</c>).
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>Optional environment override for the expected Authenticode signer SHA-1 thumbprint.</summary>
    public const string ExpectedThumbprintEnvVar = "INBRISK_AUTHENTICODE_THUMBPRINT";

    /// <summary>
    /// Embedded production signer SHA-1 thumbprints (uppercase hex, no spaces).
    /// Empty in unsigned dev/source builds; release builds may populate this
    /// or pass an expected thumbprint explicitly.
    /// </summary>
    private static readonly string[] EmbeddedAllowedThumbprints =
    {
        // Populate with production Authenticode certificate thumbprint(s) when shipping signed releases.
    };

    public sealed record VerificationResult(
        bool IsValid,
        int StatusCode,
        string Message,
        string? SignerThumbprint = null,
        string? SignerSubject = null,
        bool IsCatalogSigned = false);

    /// <summary>
    /// True when this build has at least one configured Authenticode thumbprint
    /// (embedded or via <see cref="ExpectedThumbprintEnvVar"/>).
    /// </summary>
    public static bool IsEnforced =>
        EmbeddedAllowedThumbprints.Any(t => !string.IsNullOrWhiteSpace(t)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExpectedThumbprintEnvVar));

    /// <summary>Returns the configured expected thumbprint(s), normalized to uppercase hex.</summary>
    public static IReadOnlyList<string> GetConfiguredThumbprints()
    {
        var env = Environment.GetEnvironmentVariable(ExpectedThumbprintEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeThumbprint)
                .Where(t => t.Length > 0)
                .ToArray();
        }

        return EmbeddedAllowedThumbprints
            .Select(NormalizeThumbprint)
            .Where(t => t.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Verifies the Authenticode signature of <paramref name="filePath"/> using
    /// <c>WinVerifyTrust</c> and optionally enforces signer thumbprint and/or subject substring.
    /// </summary>
    public static VerificationResult Verify(
        string filePath,
        string? expectedThumbprint = null,
        string? expectedSubjectContains = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new VerificationResult(false, unchecked((int)0x80070002),
                $"File not found: '{filePath}'");
        }

        var fullPath = Path.GetFullPath(filePath);
        int status = VerifyEmbeddedSignature(fullPath);
        bool isCatalog = false;
        string certSourcePath = fullPath;

        if (status == TrustENoSignature)
        {
            var (catStatus, catalogPath) = VerifyCatalogSignature(fullPath);
            if (catStatus == 0 && !string.IsNullOrWhiteSpace(catalogPath))
            {
                status = 0;
                isCatalog = true;
                certSourcePath = catalogPath;
            }
        }

        if (status != 0)
        {
            return new VerificationResult(
                IsValid: false,
                StatusCode: status,
                Message: FormatWinTrustError(status),
                IsCatalogSigned: isCatalog);
        }

        string? thumbprint;
        string? subject;
        try
        {
            using var baseCert = X509Certificate.CreateFromSignedFile(certSourcePath);
            using var cert = new X509Certificate2(baseCert);
            thumbprint = NormalizeThumbprint(cert.Thumbprint);
            subject = cert.Subject;
        }
        catch (Exception ex)
        {
            return new VerificationResult(
                IsValid: false,
                StatusCode: unchecked((int)0x800B0100),
                Message: $"WinVerifyTrust succeeded, but failed to extract signer certificate: {ex.Message}",
                IsCatalogSigned: isCatalog);
        }

        if (!string.IsNullOrWhiteSpace(expectedThumbprint))
        {
            var normalizedExpected = NormalizeThumbprint(expectedThumbprint);
            if (!string.Equals(thumbprint, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(
                    IsValid: false,
                    StatusCode: TrustECertificateMismatch,
                    Message: $"Signer thumbprint mismatch: expected '{normalizedExpected}', got '{thumbprint}'",
                    SignerThumbprint: thumbprint,
                    SignerSubject: subject,
                    IsCatalogSigned: isCatalog);
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSubjectContains))
        {
            if (string.IsNullOrEmpty(subject) ||
                !subject.Contains(expectedSubjectContains, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(
                    IsValid: false,
                    StatusCode: TrustECertificateMismatch,
                    Message: $"Signer subject '{subject}' does not contain '{expectedSubjectContains}'",
                    SignerThumbprint: thumbprint,
                    SignerSubject: subject,
                    IsCatalogSigned: isCatalog);
            }
        }

        return new VerificationResult(
            IsValid: true,
            StatusCode: 0,
            Message: isCatalog ? "Valid Authenticode signature (catalog)" : "Valid Authenticode signature",
            SignerThumbprint: thumbprint,
            SignerSubject: subject,
            IsCatalogSigned: isCatalog);
    }

    /// <summary>
    /// Verifies <paramref name="filePath"/> against any thumbprint in <paramref name="allowedThumbprints"/>
    /// (or just OS trust when <paramref name="allowedThumbprints"/> is empty).
    /// </summary>
    public static VerificationResult VerifyAgainstAllowedThumbprints(
        string filePath,
        IReadOnlyList<string>? allowedThumbprints,
        string? expectedSubjectContains = null)
    {
        var baseResult = Verify(filePath, expectedThumbprint: null, expectedSubjectContains: expectedSubjectContains);
        if (!baseResult.IsValid)
            return baseResult;

        if (allowedThumbprints == null || allowedThumbprints.Count == 0)
            return baseResult;

        var normalizedAllowed = allowedThumbprints
            .Select(NormalizeThumbprint)
            .Where(t => t.Length > 0)
            .ToArray();

        if (normalizedAllowed.Length == 0)
            return baseResult;

        if (baseResult.SignerThumbprint != null &&
            normalizedAllowed.Contains(baseResult.SignerThumbprint, StringComparer.OrdinalIgnoreCase))
        {
            return baseResult;
        }

        return new VerificationResult(
            IsValid: false,
            StatusCode: TrustECertificateMismatch,
            Message: $"Signer thumbprint '{baseResult.SignerThumbprint}' is not in the allowed thumbprint list",
            SignerThumbprint: baseResult.SignerThumbprint,
            SignerSubject: baseResult.SignerSubject,
            IsCatalogSigned: baseResult.IsCatalogSigned);
    }

    /// <summary>Convenience boolean wrapper around <see cref="Verify"/>.</summary>
    public static bool IsTrusted(
        string filePath,
        string? expectedThumbprint = null,
        string? expectedSubjectContains = null) =>
        Verify(filePath, expectedThumbprint, expectedSubjectContains).IsValid;

    private static string NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return string.Empty;
        return new string(thumbprint.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
    }

    private static string FormatWinTrustError(int status) => status switch
    {
        TrustENoSignature => "No Authenticode signature present (TRUST_E_NOSIGNATURE)",
        TrustEBadDigest => "Binary digest mismatch / file tampered (TRUST_E_BAD_DIGEST)",
        TrustEExplicitDistrust => "Signer certificate is explicitly distrusted (TRUST_E_EXPLICIT_DISTRUST)",
        TrustESubjectNotTrusted => "Signer certificate is not trusted (TRUST_E_SUBJECT_NOT_TRUSTED)",
        CertEUntrustedRoot => "Certificate chain terminates in an untrusted root (CERT_E_UNTRUSTEDROOT)",
        CertEExpired => "Signer certificate has expired (CERT_E_EXPIRED)",
        _ => $"WinVerifyTrust failed with HRESULT 0x{unchecked((uint)status):X8}"
    };

    // ------------------------------------------------------------------ Win32

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int TrustECertificateMismatch = unchecked((int)0x800B010F);

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    private static int VerifyEmbeddedSignature(string fullPath)
    {
        IntPtr filePathPtr = IntPtr.Zero;
        IntPtr fileInfoPtr = IntPtr.Zero;
        var winTrustData = new WINTRUST_DATA();
        Guid action = WinTrustActionGenericVerifyV2;

        try
        {
            filePathPtr = Marshal.StringToCoTaskMemUni(fullPath);
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            winTrustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pInfoStruct = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };

            return WinVerifyTrust(IntPtr.Zero, ref action, ref winTrustData);
        }
        finally
        {
            if (winTrustData.hWVTStateData != IntPtr.Zero)
            {
                winTrustData.dwStateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref winTrustData);
            }
            if (fileInfoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPtr);
            if (filePathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    private static (int Status, string? CatalogPath) VerifyCatalogSignature(string fullPath)
    {
        foreach (var hashAlgorithm in new[] { "SHA256", "SHA1" })
        {
            var result = TryVerifyCatalogWithAlgorithm(fullPath, hashAlgorithm);
            if (result.Status == 0)
                return result;
        }
        return (TrustENoSignature, null);
    }

    private static (int Status, string? CatalogPath) TryVerifyCatalogWithAlgorithm(
        string fullPath, string hashAlgorithm)
    {
        IntPtr hCatAdmin = IntPtr.Zero;
        IntPtr hCatInfo = IntPtr.Zero;

        try
        {
            if (!CryptCATAdminAcquireContext2(out hCatAdmin, IntPtr.Zero, hashAlgorithm, IntPtr.Zero, 0))
                return (TrustENoSignature, null);

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint hashLen = 0;
            _ = CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, fs.SafeFileHandle, ref hashLen, null, 0);
            if (hashLen == 0)
                return (TrustENoSignature, null);

            var hash = new byte[hashLen];
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, fs.SafeFileHandle, ref hashLen, hash, 0))
                return (TrustENoSignature, null);

            var memberTag = Convert.ToHexString(hash);
            IntPtr prevCatInfo = IntPtr.Zero;
            hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, hashLen, 0, ref prevCatInfo);
            if (hCatInfo == IntPtr.Zero)
                return (TrustENoSignature, null);

            var catInfo = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
            if (!CryptCATCatalogInfoFromContext(hCatInfo, ref catInfo, 0))
                return (TrustENoSignature, null);

            int status = VerifyCatalogMember(catInfo.wszCatalogFile, memberTag, fullPath, fs.SafeFileHandle, hash, hCatAdmin);
            return (status, status == 0 ? catInfo.wszCatalogFile : null);
        }
        catch
        {
            return (TrustENoSignature, null);
        }
        finally
        {
            if (hCatInfo != IntPtr.Zero && hCatAdmin != IntPtr.Zero)
                _ = CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
            if (hCatAdmin != IntPtr.Zero)
                _ = CryptCATAdminReleaseContext(hCatAdmin, 0);
        }
    }

    private static int VerifyCatalogMember(
        string catalogPath,
        string memberTag,
        string memberFilePath,
        SafeFileHandle memberFileHandle,
        byte[] hash,
        IntPtr hCatAdmin)
    {
        IntPtr catPathPtr = IntPtr.Zero;
        IntPtr memberTagPtr = IntPtr.Zero;
        IntPtr memberFilePathPtr = IntPtr.Zero;
        IntPtr hashPtr = IntPtr.Zero;
        IntPtr catalogInfoPtr = IntPtr.Zero;
        var winTrustData = new WINTRUST_DATA();
        Guid action = WinTrustActionGenericVerifyV2;

        try
        {
            catPathPtr = Marshal.StringToCoTaskMemUni(catalogPath);
            memberTagPtr = Marshal.StringToCoTaskMemUni(memberTag);
            memberFilePathPtr = Marshal.StringToCoTaskMemUni(memberFilePath);
            hashPtr = Marshal.AllocCoTaskMem(hash.Length);
            Marshal.Copy(hash, 0, hashPtr, hash.Length);

            var wtCatInfo = new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                dwCatalogVersion = 0,
                pcwszCatalogFilePath = catPathPtr,
                pcwszMemberTag = memberTagPtr,
                pcwszMemberFilePath = memberFilePathPtr,
                hMemberFile = memberFileHandle.DangerousGetHandle(),
                pbCalculatedFileHash = hashPtr,
                cbCalculatedFileHash = (uint)hash.Length,
                pcCatalogContext = IntPtr.Zero,
                hCatAdmin = hCatAdmin
            };
            catalogInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            Marshal.StructureToPtr(wtCatInfo, catalogInfoPtr, fDeleteOld: false);

            winTrustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceCatalog,
                pInfoStruct = catalogInfoPtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };

            return WinVerifyTrust(IntPtr.Zero, ref action, ref winTrustData);
        }
        finally
        {
            if (winTrustData.hWVTStateData != IntPtr.Zero)
            {
                winTrustData.dwStateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref winTrustData);
            }
            if (catalogInfoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(catalogInfoPtr);
            if (hashPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(hashPtr);
            if (memberFilePathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(memberFilePathPtr);
            if (memberTagPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(memberTagPtr);
            if (catPathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(catPathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfoStruct;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionID,
        ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr phCatAdmin,
        IntPtr pgSubsystem,
        string? pwszHashAlgorithm,
        IntPtr pStrongHashPolicy,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr hCatAdmin,
        SafeFileHandle hFile,
        ref uint pcbHash,
        [Out] byte[]? pbHash,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr hCatAdmin,
        [In] byte[] pbHash,
        uint cbHash,
        uint dwFlags,
        ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(
        IntPtr hCatInfo,
        ref CATALOG_INFO psCatInfo,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(
        IntPtr hCatAdmin,
        IntPtr hCatInfo,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(
        IntPtr hCatAdmin,
        uint dwFlags);
}
