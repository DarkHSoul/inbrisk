using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Microsoft.Win32;
using Windows.Win32;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Apps;

public sealed partial class AppService : IAppService, IDisposable
{
    private static bool LooksLikePe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[2];
            return fs.Read(magic) == 2 && magic[0] == 'M' && magic[1] == 'Z';
        }
        catch { return false; }
    }

    private static bool IsSignedBinary(string path)
    {
        // X509Certificate.CreateFromSignedFile only sees signatures
        // EMBEDDED in the PE — the overwhelming majority of Windows system
        // binaries (notepad.exe, cmd.exe, calc.exe, …) are CATALOG-signed:
        // their signature lives in a security catalog under CatRoot, not in
        // the file, so CreateFromSignedFile throws and they'd be refused as
        // "unsigned". WinVerifyTrust with the generic-verify action resolves
        // both embedded and catalog signatures.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                _ = System.Security.Cryptography.X509Certificates
                    .X509Certificate.CreateFromSignedFile(path);
                return true;
            }
            catch { return false; }
        }

        return VerifyFileTrust(path) || IsCatalogSigned(path);
    }

    /// <summary>WinVerifyTrust (GENERIC_VERIFY_V2) on a single file —
    /// covers embedded signatures AND .cat catalog files themselves.</summary>
    private static bool VerifyFileTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToHGlobalUni(path),
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WtdUiNone,
            fdwRevocationChecks = WtdRevokeNone,
            dwUnionChoice = WtdChoiceFile,
            dwProvFlags = WtdRevocationCheckNone,
            dwStateAction = WtdStateactionVerify,
        };

        IntPtr pFile = IntPtr.Zero;
        IntPtr pData = IntPtr.Zero;
        try
        {
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo));
            Marshal.StructureToPtr(fileInfo, pFile, false);
            data.pFile = pFile;
            pData = Marshal.AllocHGlobal(Marshal.SizeOf(data));
            Marshal.StructureToPtr(data, pData, false);
            var action = WintrustActionGenericVerifyV2;
            int status = WinVerifyTrust(InvalidHwnd, ref action, pData);
            // trust status is a literal — end the WVT state data either way
            data.dwStateAction = WtdStateactionClose;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(InvalidHwnd, ref action, pData);
            return status == 0;
        }
        catch { return false; }
        finally
        {
            if (fileInfo.pcwszFilePath != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfo.pcwszFilePath);
            if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
            if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
        }
    }

    /// <summary>
    /// Catalog fallback: WinVerifyTrust on a bare file does NOT resolve
    /// Windows catalog signatures (notepad.exe/calc.exe/cmd.exe report
    /// TRUST_E_NOSIGNATURE even though Get-AuthenticodeSignature validates
    /// them). Walk the catalog chain like Authenticode does: hash the file,
    /// enumerate catalogs containing that hash, and accept when any owning
    /// .cat file itself verifies.
    /// </summary>
    private static bool IsCatalogSigned(string path)
    {
        const uint GenericRead = 0x80000000;
        const uint OpenExisting = 3;
        const int MaxCatalogs = 16;
        IntPtr hFile = IntPtr.Zero;
        IntPtr hCatAdmin = IntPtr.Zero;
        IntPtr hash = IntPtr.Zero;
        try
        {
            hFile = CreateFileW(path, GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (hFile == InvalidHandle) return false;
            if (!CryptCATAdminAcquireContext2(out hCatAdmin, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 0))
                return false;
            var cbHash = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile,
                    ref cbHash, IntPtr.Zero, 0) || cbHash <= 0)
                return false;
            hash = Marshal.AllocHGlobal(cbHash);
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile,
                    ref cbHash, hash, 0))
                return false;
            IntPtr prev = IntPtr.Zero;
            for (var i = 0; i < MaxCatalogs; i++)
            {
                var hCatInfo = CryptCATAdminEnumCatalogFromHash(
                    hCatAdmin, hash, cbHash, 0, ref prev);
                if (hCatInfo == IntPtr.Zero) break;
                var ci = new CATALOG_INFO
                { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0) &&
                    !string.IsNullOrEmpty(ci.wszCatalogFile) &&
                    VerifyFileTrust(ci.wszCatalogFile))
                {
                    CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
                    return true;
                }
                prev = hCatInfo;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (hash != IntPtr.Zero) Marshal.FreeHGlobal(hash);
            if (hCatAdmin != IntPtr.Zero) CryptCATAdminReleaseContext(hCatAdmin, 0);
            if (hFile != IntPtr.Zero && hFile != InvalidHandle) CloseHandle(hFile);
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint FileShareRead = 1;

    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin,
        IntPtr pgSubsystem, IntPtr pwszHashAlgorithm, IntPtr pStrongHashPolicy,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin,
        IntPtr hFile, ref int pcbHash, IntPtr pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin,
        IntPtr pbHash, int cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin,
        IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo,
        ref CATALOG_INFO psCatInfo, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

    private static readonly IntPtr InvalidHwnd = new(-1);
    private const uint WtdUiNone = 2;

    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;

    private const uint WtdRevocationCheckNone = 0x00000040;
    private const uint WtdStateactionVerify = 0;

    private const uint WtdStateactionClose = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);
}
