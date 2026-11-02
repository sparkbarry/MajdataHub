using System.Text.RegularExpressions;

namespace RhythmHub.Updater;

/// <summary>Rules for names and paths that arrive from the network and end up on the local file system.</summary>
internal static partial class ManifestPaths
{
    public const string ReservedDirectory = ".rhythmhub";
    private const int MaxPathLength = 1024;

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*([-+][0-9A-Za-z.+-]+)?$")]
    private static partial Regex VersionPattern();

    /// <summary>Versions start with a digit so they can never collide with a channel file name like stable.json.</summary>
    public static string Version(string value) =>
        value.Length <= 64 && VersionPattern().IsMatch(value) ? value : throw Invalid($"Illegal version '{value}'.");

    /// <summary>Relative, forward-slash path that is safe on both Windows and macOS.</summary>
    public static string RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path.StartsWith('/') || path.EndsWith('/') ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
            throw Invalid($"Illegal path '{path}'.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                WindowsReserved.Contains(segment.Split('.')[0]))
                throw Invalid($"Illegal path '{path}'.");
        }
        if (path.Split('/')[0].Equals(ReservedDirectory, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"Path '{path}' uses the reserved {ReservedDirectory} directory.");
        return path;
    }

    /// <summary>Resolves <paramref name="path"/> under <paramref name="root"/>, refusing anything that escapes it.</summary>
    public static string Under(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw Invalid($"Path '{path}' escapes the install directory.");
        return full;
    }

    public static UpdateException Invalid(string message) => new(UpdateErrorCode.ManifestInvalid, message);
}
