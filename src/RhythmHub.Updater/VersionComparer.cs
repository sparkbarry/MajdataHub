namespace RhythmHub.Updater;

public static class VersionComparer
{
    /// <summary>Parses "1.2.3", "1.2.3-beta.1" or "1.2.3+build" into the numeric core.</summary>
    public static bool TryParse(string value, out Version version)
    {
        var core = value.Split('-', '+')[0];
        if (Version.TryParse(core, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }
}
