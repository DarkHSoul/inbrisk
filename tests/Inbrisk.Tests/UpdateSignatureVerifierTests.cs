using System.Security.Cryptography;
using System.Text;
using Inbrisk.Setup;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class UpdateSignatureVerifierTests
{
    [Fact]
    public void CanonicalManifestBytes_StripsSignatureAndSortsKeysOrdinally()
    {
        var jsonA = """
        {
          "signature": "AAAA",
          "version": "1.2.3",
          "nested": { "z": 1, "a": 2 },
          "files": ["b", "a"]
        }
        """;
        var jsonB = """
        {"files":["b","a"],"nested":{"a":2,"z":1},"version":"1.2.3","SIGNATURE":"BBBB"}
        """;

        var canonA = UpdateSignatureVerifier.CanonicalManifestBytes(Encoding.UTF8.GetBytes(jsonA));
        var canonB = UpdateSignatureVerifier.CanonicalManifestBytes(Encoding.UTF8.GetBytes(jsonB));

        Assert.Equal(canonA, canonB);
        var text = Encoding.UTF8.GetString(canonA);
        Assert.DoesNotContain("signature", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("""{"files":["b","a"],"nested":{"a":2,"z":1},"version":"1.2.3"}""", text);
    }

    [Fact]
    public void VerifyManifest_ValidP1363AndDerSignatures_Pass_AndTamperingFails()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Encoding.UTF8.GetBytes("""{"version":"2.0.0","sha256":"abc123","url":"https://example.com/app.zip"}""");
        var canonical = UpdateSignatureVerifier.CanonicalManifestBytes(manifest);

        var sigP1363 = ecdsa.SignData(canonical, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var sigDer = ecdsa.SignData(canonical, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(UpdateSignatureVerifier.VerifyManifest(canonical, sigP1363, ecdsa));
        Assert.True(UpdateSignatureVerifier.VerifyManifest(canonical, sigDer, ecdsa));

        // Tamper with payload (sha256 swapped) -> MUST fail
        var tampered = UpdateSignatureVerifier.CanonicalManifestBytes(
            Encoding.UTF8.GetBytes("""{"version":"2.0.0","sha256":"deadbeef","url":"https://example.com/app.zip"}"""));
        Assert.False(UpdateSignatureVerifier.VerifyManifest(tampered, sigP1363, ecdsa));
        Assert.False(UpdateSignatureVerifier.VerifyManifest(tampered, sigDer, ecdsa));

        // Tamper with signature byte -> MUST fail
        var badSig = (byte[])sigP1363.Clone();
        badSig[0] ^= 0xFF;
        Assert.False(UpdateSignatureVerifier.VerifyManifest(canonical, badSig, ecdsa));

        // Wrong key -> MUST fail
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignatureVerifier.VerifyManifest(canonical, sigP1363, otherKey));
    }

    [Fact]
    public void DecodeSignature_HandlesBase64AndBase64Url_RejectsMalformed()
    {
        var raw = new byte[] { 0xFB, 0xFF, 0xFE, 0x01, 0x02, 0x03 };
        var std = Convert.ToBase64String(raw);
        var url = std.Replace('+', '-').Replace('/', '_');

        Assert.Equal(raw, UpdateSignatureVerifier.DecodeSignature(std));
        Assert.Equal(raw, UpdateSignatureVerifier.DecodeSignature(url));
        Assert.Null(UpdateSignatureVerifier.DecodeSignature(null));
        Assert.Null(UpdateSignatureVerifier.DecodeSignature("   "));
        Assert.Null(UpdateSignatureVerifier.DecodeSignature("!!!not-valid-base64!!!"));
    }

    [Fact]
    public void LoadPublicKey_SupportsInlinePemAndPemFile_FailsClosedOnCorruptKey()
    {
        var prev = Environment.GetEnvironmentVariable(UpdateSignatureVerifier.KeyEnvVar);
        var tempPem = Path.Combine(Path.GetTempPath(), $"inbrisk-pub-{Guid.NewGuid():N}.pem");
        try
        {
            using var keypair = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pubPem = keypair.ExportSubjectPublicKeyInfoPem();

            // 1. Inline PEM override
            Environment.SetEnvironmentVariable(UpdateSignatureVerifier.KeyEnvVar, pubPem);
            Assert.True(UpdateSignatureVerifier.SignaturesEnforced);
            using (var loaded = UpdateSignatureVerifier.LoadPublicKey(null))
            {
                Assert.NotNull(loaded);
                var data = Encoding.UTF8.GetBytes("{\"version\":\"1.0\"}");
                var sig = keypair.SignData(data, HashAlgorithmName.SHA256);
                Assert.True(UpdateSignatureVerifier.VerifyManifest(data, sig, loaded!));
            }

            // 2. PEM file path override
            File.WriteAllText(tempPem, pubPem);
            Environment.SetEnvironmentVariable(UpdateSignatureVerifier.KeyEnvVar, tempPem);
            using (var loadedFromFile = UpdateSignatureVerifier.LoadPublicKey("any-key"))
            {
                Assert.NotNull(loadedFromFile);
            }

            // 3. Corrupt PEM fails closed (returns null, does not throw)
            Environment.SetEnvironmentVariable(UpdateSignatureVerifier.KeyEnvVar, "NOT A VALID PEM");
            Assert.Null(UpdateSignatureVerifier.LoadPublicKey(null));

            // 4. Unknown keyId without env override fails closed
            Environment.SetEnvironmentVariable(UpdateSignatureVerifier.KeyEnvVar, null);
            Assert.Null(UpdateSignatureVerifier.LoadPublicKey("unknown-key-id"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateSignatureVerifier.KeyEnvVar, prev);
            try { if (File.Exists(tempPem)) File.Delete(tempPem); } catch { }
        }
    }
}
