using System.Security.Cryptography;

namespace Inbrisk.Setup;

/// <summary>
/// Update-manifest signature verification (F31). A hash inside the manifest
/// proves nothing — whoever can replace the payload can replace the manifest.
/// The manifest bytes must be RSA-signed; the PUBLIC half is embedded in this
/// binary so the trust anchor travels with the code doing the verification.
///
/// Release builds inject the real PEM into <see cref="EmbeddedPublicKeyPem"/>
/// (it is empty in dev/source builds). For local verification of the flow,
/// INBRISK_UPDATE_PUBLIC_KEY may hold a PEM string or a path to a .pem file —
/// this is a development override, never set it in production installs.
/// </summary>
public static class UpdateSigning
{
    /// <summary>
    /// PEM-encoded RSA public key baked into release artifacts by the signing
    /// pipeline. Empty means "no release key" — unsigned dev feeds stay usable
    /// but any manifest that DOES carry a signature is refused (we cannot
    /// distinguish a real signature from a forged one without the key).
    /// </summary>
    internal const string EmbeddedPublicKeyPem = "";

    /// <summary>Dev/test override for the verification key (PEM text or .pem path).</summary>
    public const string KeyEnvVar = "INBRISK_UPDATE_PUBLIC_KEY";

    /// <summary>True when a release key is embedded in this build.</summary>
    public static bool HasEmbeddedKey => !string.IsNullOrWhiteSpace(EmbeddedPublicKeyPem);

    /// <summary>
    /// The public key to verify manifests with, or null when this build has no
    /// key (unsigned dev mode). Env override wins over the embedded constant.
    /// </summary>
    public static RSA? LoadPublicKey()
    {
        try
        {
            var source = Environment.GetEnvironmentVariable(KeyEnvVar);
            var pem = !string.IsNullOrWhiteSpace(source)
                ? (File.Exists(source) ? File.ReadAllText(source) : source)
                : EmbeddedPublicKeyPem;
            if (string.IsNullOrWhiteSpace(pem)) return null;
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch { return null; }
    }

    /// <summary>RSA/SHA-256 (PKCS#1 v1.5) verify of the raw manifest bytes.</summary>
    public static bool VerifyManifest(byte[] manifestBytes, byte[] signature, RSA key) =>
        key.VerifyData(manifestBytes, signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}
