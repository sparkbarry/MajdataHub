using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>
/// Describes a build directory as a <see cref="ReleaseManifest"/>. Runs in the game's CI, where the build still has
/// its symlinks and permission bits; those are lost once the files are uploaded to S3.
/// </summary>
public static class ReleaseManifestBuilder
{
    public static async Task<ReleaseManifest> FromDirectoryAsync(string root, string gameId, string platform, string version,
        string entrypoint, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        var files = new List<ManifestFile>();
        var links = new List<ManifestSymlink>();
        var emptyDirs = new List<string>();

        foreach (var path in Walk(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target)
            {
                links.Add(new ManifestSymlink(rel, target.Replace(Path.DirectorySeparatorChar, '/')));
            }
            else if (Directory.Exists(path))
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any()) emptyDirs.Add(rel);
            }
            else
            {
                int? mode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path) & 0x1FF;
                files.Add(new ManifestFile(rel, info.Length, await FileHasher.ComputeSha256Async(path, ct), mode));
            }
        }

        var manifest = new ReleaseManifest
        {
            SchemaVersion = ManifestClient.SchemaVersion,
            GameId = gameId,
            Platform = platform,
            Version = version,
            Entrypoint = entrypoint,
            Files = files,
            Symlinks = links,
            Directories = emptyDirs,
        };
        ManifestClient.ValidateRelease(manifest, gameId, platform);
        return manifest;
    }

    /// <summary>Recursive listing that records symlinked directories as entries instead of descending into them.</summary>
    private static IEnumerable<string> Walk(string dir)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            yield return path;
            if (new FileInfo(path).LinkTarget is null && Directory.Exists(path))
                foreach (var child in Walk(path)) yield return child;
        }
    }
}
