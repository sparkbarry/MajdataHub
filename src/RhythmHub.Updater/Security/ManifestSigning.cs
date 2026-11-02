using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace RhythmHub.Updater.Security;

/// <summary>
/// Ed25519 over the JCS form of the manifest with the top-level "signature" member removed.
/// Keys are raw 32-byte values, base64 encoded.
/// </summary>
public static class ManifestSigning
{
    public const string Algorithm = "Ed25519";
    public const string SignatureProperty = "signature";

    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var priv = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (Convert.ToBase64String(priv.GetEncoded()), Convert.ToBase64String(priv.GeneratePublicKey().GetEncoded()));
    }

    public static string PublicKeyFor(string privateKey) =>
        Convert.ToBase64String(new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey)).GeneratePublicKey().GetEncoded());

    /// <summary>Adds (or replaces) the signature member on <paramref name="manifest"/>.</summary>
    public static void Sign(JsonObject manifest, string privateKey, string keyId)
    {
        manifest.Remove(SignatureProperty);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey)));
        var payload = JsonCanonicalizer.Canonicalize(manifest);
        signer.BlockUpdate(payload, 0, payload.Length);
        manifest[SignatureProperty] = new JsonObject
        {
            ["algorithm"] = Algorithm,
            ["keyId"] = keyId,
            ["value"] = Convert.ToBase64String(signer.GenerateSignature()),
        };
    }

    /// <summary>Throws <see cref="UpdateException"/> unless the manifest carries a valid signature from a trusted key.</summary>
    public static void Verify(JsonObject manifest, IReadOnlyDictionary<string, string> trustedKeys)
    {
        static UpdateException Fail(string why) => new(UpdateErrorCode.SignatureInvalid, $"Manifest signature rejected: {why}.");

        if (manifest[SignatureProperty] is not JsonObject sig)
            throw Fail("missing signature");
        var algorithm = (string?)sig["algorithm"];
        var keyId = (string?)sig["keyId"];
        var value = (string?)sig["value"];
        if (algorithm != Algorithm) throw Fail($"unsupported algorithm '{algorithm}'");
        // The key comes only from the launcher's built-in list, never from the manifest itself.
        if (keyId is null || !trustedKeys.TryGetValue(keyId, out var publicKey)) throw Fail($"unknown key '{keyId}'");

        byte[] signature, key;
        try
        {
            signature = Convert.FromBase64String(value ?? "");
            key = Convert.FromBase64String(publicKey);
        }
        catch (FormatException)
        {
            throw Fail("malformed base64");
        }
        if (signature.Length != 64 || key.Length != 32) throw Fail("wrong key or signature length");

        var unsigned = (JsonObject)manifest.DeepClone();
        unsigned.Remove(SignatureProperty);
        byte[] payload;
        try
        {
            payload = JsonCanonicalizer.Canonicalize(unsigned);
        }
        catch (FormatException ex)
        {
            throw Fail(ex.Message);
        }

        bool valid;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key));
            verifier.BlockUpdate(payload, 0, payload.Length);
            valid = verifier.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            throw Fail($"trusted key '{keyId}' is not a valid Ed25519 public key");
        }
        if (!valid) throw Fail("signature does not match");
    }
}
