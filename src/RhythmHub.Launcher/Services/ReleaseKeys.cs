namespace RhythmHub.Launcher.Services;

/// <summary>
/// Ed25519 public keys trusted to sign game manifests, by keyId. Generate a pair with
/// <c>rhythmhub-release keygen</c>, keep the private half in CI secrets only, and add the public half here.
/// Keep a retired key listed until no published manifest still uses it.
/// </summary>
internal static class ReleaseKeys
{
    /// <summary>Debug builds only: "keyId=base64PublicKey", for signing a local test feed.</summary>
    public const string DevKeyVariable = "RHYTHMHUB_DEV_PUBLIC_KEY";

    private static readonly Dictionary<string, string> Production = new()
    {
        // ["release-2026"] = "<base64 public key>",
    };

    public static IReadOnlyDictionary<string, string> Load()
    {
        var keys = new Dictionary<string, string>(Production);
#if DEBUG
        if (Environment.GetEnvironmentVariable(DevKeyVariable) is { } dev && dev.Split('=', 2) is [var id, var key])
            keys[id] = key;
#endif
        return keys;
    }
}
