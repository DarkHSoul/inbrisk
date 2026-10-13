using System.Security.Cryptography;
using System.Text.Json;

namespace Inbrisk.Setup;

/// <summary>
/// Update-manifest signature verification (F31). A sha256 inside the manifest
/// proves nothing — whoever controls the feed can swap the payload AND the
/// hash. The manifest's "signature" field signs the CANONICAL serialization
/// of every other field (top-level "signature" stripped, object keys sorted
/// ordinally, compact output) with ECDSA P-256 / SHA-256. The public key
/// ships inside this binary so the trust anchor travels with the verifying
/// code; the private key never enters the repo — the release pipeline signs
/// the canonical manifest bytes offline with the same transform.
///
/// Key rotation: a manifest may name its key via "signatureKeyId";
/// <see cref="EmbeddedKeys"/> maps keyId → SPKI PEM. Rotate by adding a new
/// entry, keep the old one until all supported builds carry the new key,
/// then drop it. A signature naming a keyId this build doesn't know fails
/// closed.
///
/// Development escape hatch: source/dev builds ship an empty key table —
/// unsigned manifests stay usable but a signed one is refused (we cannot
/// tell a real signature from a forged one without the key). A manifest
/// missing a signature is rejected whenever any verification key exists.
/// INBRISK_UPDATE_PUBLIC_KEY (PEM text or .pem path) overrides the embedded
/// keys for local flow testing — never set it on production installs.
/// </summary>
public static class UpdateSignatureVerifier
{
    /// <summary>keyId → SPKI PEM (ECDSA P-256). Empty in source builds; the
    /// release pipeline injects the production anchor(s) here.</summary>
    private static readonly Dictionary<string, string> EmbeddedKeys = new()
    {
        // { "inbrisk-update-2026-1", "-----BEGIN PUBLIC KEY-----\n..." },
    };

    /// <summary>Dev/test override for the verification key (PEM text or .pem path).</summary>
    public const string KeyEnvVar = "INBRISK_UPDATE_PUBLIC_KEY";

    /// <summary>True while any verification key exists in this build —
    /// unsigned manifests must be refused once enforcement is possible.</summary>
    public static bool SignaturesEnforced =>
        EmbeddedKeys.Values.Any(p => !string.IsNullOrWhiteSpace(p)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyEnvVar));

    /// <summary>
    /// The public key for <paramref name="keyId"/>, or null when this build
    /// has none. The env override wins. A null keyId resolves only when a
    /// single embedded anchor exists; an unknown keyId fails closed.
    /// </summary>
    public static ECDsa? LoadPublicKey(string? keyId)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(KeyEnvVar);
            var pem = !string.IsNullOrWhiteSpace(env)
                ? (File.Exists(env) ? File.ReadAllText(env) : env)
                : ResolveEmbeddedPem(keyId);
            if (string.IsNullOrWhiteSpace(pem)) return null;
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key;
        }
        catch { return null; }
    }

    private static string? ResolveEmbeddedPem(string? keyId)
    {
        var anchors = EmbeddedKeys
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToList();
        if (keyId != null)
            return anchors.FirstOrDefault(kv => kv.Key == keyId).Value;
        return anchors.Count == 1 ? anchors[0].Value : null;
    }

    /// <summary>
    /// Canonical manifest bytes: every field except top-level "signature",
    /// object keys sorted ordinally, compact output. Verification and the
    /// offline signer MUST use this exact transform — the signature never
    /// covers itself.
    /// </summary>
    public static byte[] CanonicalManifestBytes(byte[] manifestBytes)
    {
        using var doc = JsonDocument.Parse(manifestBytes);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            WriteCanonical(doc.RootElement, w, topLevel: true);
        return ms.ToArray();
    }

    private static void WriteCanonical(JsonElement e, Utf8JsonWriter w,
        bool topLevel = false)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (topLevel && string.Equals(p.Name, "signature",
                            StringComparison.OrdinalIgnoreCase))
                        continue;   // the signature never covers itself
                    w.WritePropertyName(p.Name);
                    WriteCanonical(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) WriteCanonical(item, w);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    /// <summary>base64 (or base64url) signature → bytes; null when absent/malformed.</summary>
    public static byte[]? DecodeSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return null;
        try
        {
            var b64 = signature.Trim().Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b64);
        }
        catch { return null; }
    }

    /// <summary>ECDSA P-256 / SHA-256 over the canonical manifest bytes.
    /// Accepts IEEE P1363 (r‖s) or DER-encoded signatures.</summary>
    public static bool VerifyManifest(byte[] canonicalManifest, byte[] signature,
        ECDsa key) =>
        key.VerifyData(canonicalManifest, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation) ||
        key.VerifyData(canonicalManifest, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
}
