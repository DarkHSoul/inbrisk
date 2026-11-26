using Inbrisk.Setup;
using Xunit;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class AuthenticodeSigningTests : IDisposable
{
    private readonly string _tempDir;

    public AuthenticodeSigningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-auth-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private static string GetSignedSystemBinary()
    {
        var systemDir = Environment.SystemDirectory;
        var candidates = new[]
        {
            Path.Combine(systemDir, "cmd.exe"),
            Path.Combine(systemDir, "notepad.exe")
        };
        var found = candidates.FirstOrDefault(File.Exists);
        Assert.NotNull(found);
        return found!;
    }

    [Fact]
    public void UnsignedTempBinary_FailsWinVerifyTrust()
    {
        var unsignedPath = Path.Combine(_tempDir, "unsigned-dummy.exe");
        // Write a minimal dummy payload with MZ header so it's a non-empty temp file
        File.WriteAllBytes(unsignedPath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });

        var result = AuthenticodeVerifier.Verify(unsignedPath);

        Assert.False(result.IsValid);
        Assert.NotEqual(0, result.StatusCode);
        Assert.False(AuthenticodeVerifier.IsTrusted(unsignedPath));
    }

    [Fact]
    public void RealSignedSystemBinary_PassesWinVerifyTrust()
    {
        var sysBinary = GetSignedSystemBinary();

        var result = AuthenticodeVerifier.Verify(sysBinary);

        Assert.True(result.IsValid, $"Expected '{sysBinary}' to pass WinVerifyTrust, but got: {result.Message} (0x{unchecked((uint)result.StatusCode):X8})");
        Assert.Equal(0, result.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(result.SignerThumbprint));
        Assert.False(string.IsNullOrWhiteSpace(result.SignerSubject));
        Assert.Contains("Microsoft", result.SignerSubject, StringComparison.OrdinalIgnoreCase);

        // Matching thumbprint and subject should also pass
        var pinned = AuthenticodeVerifier.Verify(
            sysBinary,
            expectedThumbprint: result.SignerThumbprint,
            expectedSubjectContains: "Microsoft");
        Assert.True(pinned.IsValid, pinned.Message);
    }

    [Fact]
    public void TamperedCopyOfSignedBinary_FailsVerification()
    {
        var sysBinary = GetSignedSystemBinary();
        var tamperedPath = Path.Combine(_tempDir, "tampered.exe");

        var bytes = File.ReadAllBytes(sysBinary);
        // Flip bytes near the end of the binary to invalidate digest / catalog hash
        Assert.True(bytes.Length > 64);
        bytes[^1] ^= 0xFF;
        bytes[^2] ^= 0xAA;
        File.WriteAllBytes(tamperedPath, bytes);

        var result = AuthenticodeVerifier.Verify(tamperedPath);

        Assert.False(result.IsValid);
        Assert.NotEqual(0, result.StatusCode);
    }

    [Fact]
    public void ThumbprintMismatch_IsRejected()
    {
        var sysBinary = GetSignedSystemBinary();
        const string wrongThumbprint = "0000000000000000000000000000000000000000";

        var result = AuthenticodeVerifier.Verify(sysBinary, expectedThumbprint: wrongThumbprint);

        Assert.False(result.IsValid);
        Assert.Contains("mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(result.SignerThumbprint));
        Assert.NotEqual(wrongThumbprint, result.SignerThumbprint, StringComparer.OrdinalIgnoreCase);

        // Also verify UpdateSignatureVerifier wrapper rejects mismatched thumbprint
        var wrapperResult = UpdateSignatureVerifier.VerifyExtractedBinary(
            sysBinary, expectedThumbprint: wrongThumbprint);
        Assert.False(wrapperResult.IsValid);
    }
}
