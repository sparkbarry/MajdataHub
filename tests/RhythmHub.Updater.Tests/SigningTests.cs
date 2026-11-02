using System.Text;
using System.Text.Json.Nodes;
using RhythmHub.Updater.Security;

namespace RhythmHub.Updater.Tests;

public class SigningTests
{
    private static JsonObject Manifest() => JsonNode.Parse("""
        {"schemaVersion":1,"gameId":"demo","version":"1.0.0","package":{"sizeBytes":42,"sha256":"ab"},"entrypoint":"Game.app"}
        """)!.AsObject();

    private static UpdateErrorCode Reject(JsonObject m, IReadOnlyDictionary<string, string>? keys = null) =>
        Assert.Throws<UpdateException>(() => ManifestSigning.Verify(m, keys ?? TestKeys.Trusted)).Code;

    [Fact]
    public void Canonical_form_sorts_keys_and_uses_minimal_escaping()
    {
        var node = JsonNode.Parse("""{ "b": 1, "a": "€\n\u0001\"", "c": [true, null, {"z":0,"y":-5}] }""");
        Assert.Equal("""{"a":"€\n\u0001\"","b":1,"c":[true,null,{"y":-5,"z":0}]}""",
            Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(node)));
    }

    [Fact]
    public void Canonical_form_sorts_by_utf16_code_units()
    {
        // RFC 8785 §3.2.3: U+20AC sorts before an astral character whose high surrogate is 0xD83D.
        var node = JsonNode.Parse("""{"😀":1,"€":2,"a":3}""");
        Assert.Equal("{\"a\":3,\"€\":2,\"😀\":1}", Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(node)));
    }

    [Theory]
    [InlineData("""{"n":1.5}""")]
    [InlineData("""{"n":1e3}""")]
    [InlineData("""{"n":9007199254740993}""")]
    public void Canonical_form_rejects_non_integer_or_unsafe_numbers(string json) =>
        Assert.Throws<FormatException>(() => JsonCanonicalizer.Canonicalize(JsonNode.Parse(json)));

    [Fact]
    public void Signed_manifest_verifies_regardless_of_formatting()
    {
        var m = Manifest();
        ManifestSigning.Sign(m, TestKeys.Pair.Private, TestKeys.KeyId);
        var reformatted = JsonNode.Parse(m.ToJsonString(new() { WriteIndented = true }))!.AsObject();
        ManifestSigning.Verify(reformatted, TestKeys.Trusted);
    }

    [Fact]
    public void Tampered_field_is_rejected()
    {
        var m = Manifest();
        ManifestSigning.Sign(m, TestKeys.Pair.Private, TestKeys.KeyId);
        m["package"]!["sha256"] = "cd";
        Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(m));
    }

    [Fact]
    public void Added_field_is_rejected()
    {
        var m = Manifest();
        ManifestSigning.Sign(m, TestKeys.Pair.Private, TestKeys.KeyId);
        m["extra"] = "x";
        Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(m));
    }

    [Fact]
    public void Missing_signature_is_rejected() => Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(Manifest()));

    [Fact]
    public void Unknown_key_is_rejected()
    {
        var m = Manifest();
        ManifestSigning.Sign(m, TestKeys.Pair.Private, "someone-else");
        Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(m));
    }

    [Fact]
    public void Signature_from_untrusted_key_under_trusted_id_is_rejected()
    {
        var attacker = ManifestSigning.GenerateKeyPair();
        var m = Manifest();
        ManifestSigning.Sign(m, attacker.PrivateKey, TestKeys.KeyId);
        Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(m));
    }

    [Fact]
    public void Malformed_trusted_key_is_rejected_not_thrown()
    {
        var m = Manifest();
        ManifestSigning.Sign(m, TestKeys.Pair.Private, TestKeys.KeyId);
        var bogus = new Dictionary<string, string> { [TestKeys.KeyId] = Convert.ToBase64String(new byte[32]) };
        Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(m, bogus));
    }

    [Fact]
    public void Wrong_algorithm_is_rejected()
    {
        var m = Manifest();
        ManifestSigning.Sign(m, TestKeys.Pair.Private, TestKeys.KeyId);
        m["signature"]!["algorithm"] = "none";
        Assert.Equal(UpdateErrorCode.SignatureInvalid, Reject(m));
    }
}
