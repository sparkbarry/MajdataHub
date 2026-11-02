namespace RhythmHub.Updater.Tests;

public class ArchiveExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/evil.txt")]
    public async Task Rejects_entries_outside_target(string name)
    {
        using var tmp = new TempDir();
        var zip = Zips.Write(tmp.Path, new Zips.Entry(name, "x"));
        var dest = Path.Combine(tmp.Path, "out");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => ArchiveExtractor.ExtractAsync(zip, dest, long.MaxValue, Ct));
        Assert.Equal(UpdateErrorCode.UnsafeArchive, ex.Code);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "evil.txt")));
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("/etc/passwd")]
    public async Task Rejects_symlinks_escaping_target(string target)
    {
        using var tmp = new TempDir();
        var zip = Zips.Write(tmp.Path, new Zips.Entry("app/link", target, UnixType: 0xA000, Mode: 0b111_111_111));

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            ArchiveExtractor.ExtractAsync(zip, Path.Combine(tmp.Path, "out"), long.MaxValue, Ct));
        Assert.Equal(UpdateErrorCode.UnsafeArchive, ex.Code);
    }

    [Fact]
    public async Task Rejects_special_files()
    {
        using var tmp = new TempDir();
        var zip = Zips.Write(tmp.Path, new Zips.Entry("fifo", "", UnixType: 0x1000));

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            ArchiveExtractor.ExtractAsync(zip, Path.Combine(tmp.Path, "out"), long.MaxValue, Ct));
        Assert.Equal(UpdateErrorCode.UnsafeArchive, ex.Code);
    }

    [Fact]
    public async Task Rejects_archives_over_size_limit()
    {
        using var tmp = new TempDir();
        var zip = Zips.Write(tmp.Path, new Zips.Entry("big", new string('x', 1000)));

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            ArchiveExtractor.ExtractAsync(zip, Path.Combine(tmp.Path, "out"), 999, Ct));
        Assert.Equal(UpdateErrorCode.UnsafeArchive, ex.Code);
    }

    [Fact]
    public async Task Restores_modes_and_internal_symlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions and symlinks only.");
            return;
        }
        using var tmp = new TempDir();
        var zip = Zips.Write(tmp.Path,
            new Zips.Entry("Game.app/Contents/MacOS/Game", "#!/bin/sh", Mode: 0b111_101_101),
            new Zips.Entry("Game.app/Contents/Frameworks/F.framework/Versions/A/F", "bin"),
            new Zips.Entry("Game.app/Contents/Frameworks/F.framework/Versions/Current", "A", UnixType: 0xA000, Mode: 0b111_101_101));
        var dest = Path.Combine(tmp.Path, "out");

        await ArchiveExtractor.ExtractAsync(zip, dest, long.MaxValue, Ct);

        var exe = Path.Combine(dest, "Game.app/Contents/MacOS/Game");
        Assert.True((File.GetUnixFileMode(exe) & UnixFileMode.UserExecute) != 0);
        var current = new DirectoryInfo(Path.Combine(dest, "Game.app/Contents/Frameworks/F.framework/Versions/Current"));
        Assert.Equal("A", current.LinkTarget);
        Assert.True(File.Exists(Path.Combine(current.FullName, "F")));
    }
}
