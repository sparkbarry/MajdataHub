using System.IO.Compression;

namespace RhythmHub.Updater;

/// <summary>
/// Extracts a release zip entry by entry, rejecting anything that would land outside the target directory.
/// Restores Unix permission bits and in-tree symlinks so macOS .app bundles survive.
/// </summary>
public static class ArchiveExtractor
{
    private const int UnixFileTypeMask = 0xF000; // S_IFMT
    private const int UnixSymlink = 0xA000;       // S_IFLNK
    private const int UnixRegular = 0x8000;       // S_IFREG
    private const int UnixDirectory = 0x4000;     // S_IFDIR
    private const int MaxSymlinkTargetLength = 1024;

    /// <returns>Total bytes of regular files written.</returns>
    public static async Task<long> ExtractAsync(string zipPath, string destination, long maxTotalBytes, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        await using var archive = await ZipFile.OpenReadAsync(zipPath, ct);
        long total = 0;
        var symlinks = new List<(string Path, string Target)>();

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || Path.IsPathRooted(name) || name.Contains(':'))
                throw Unsafe($"absolute path '{entry.FullName}'");

            var fullPath = Path.GetFullPath(Path.Combine(root, name));
            if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal) && fullPath != root)
                throw Unsafe($"path traversal '{entry.FullName}'");

            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var fileType = unixMode & UnixFileTypeMask;

            if (name.EndsWith('/') || fileType == UnixDirectory)
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }
            if (fileType != 0 && fileType != UnixRegular && fileType != UnixSymlink)
                throw Unsafe($"special file '{entry.FullName}'");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            if (fileType == UnixSymlink)
            {
                if (entry.Length > MaxSymlinkTargetLength)
                    throw Unsafe($"oversized symlink '{entry.FullName}'");
                using var reader = new StreamReader(await entry.OpenAsync(ct));
                var target = await reader.ReadToEndAsync(ct);
                if (Path.IsPathRooted(target))
                    throw Unsafe($"absolute symlink '{entry.FullName}' -> '{target}'");
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, target));
                if (!resolved.StartsWith(rootWithSep, StringComparison.Ordinal))
                    throw Unsafe($"symlink escapes archive '{entry.FullName}' -> '{target}'");
                symlinks.Add((fullPath, target));
                continue;
            }

            total += entry.Length;
            if (total > maxTotalBytes)
                throw Unsafe($"unpacked size exceeds {maxTotalBytes} bytes");

            await entry.ExtractToFileAsync(fullPath, overwrite: false, ct);
            if (!OperatingSystem.IsWindows() && unixMode != 0)
                File.SetUnixFileMode(fullPath, (UnixFileMode)(unixMode & 0x1FF));
        }

        // Create links last so no regular entry can be written through one.
        foreach (var (path, target) in symlinks)
            File.CreateSymbolicLink(path, target);

        return total;
    }

    private static UpdateException Unsafe(string why) =>
        new(UpdateErrorCode.UnsafeArchive, $"Archive rejected: {why}.");
}
