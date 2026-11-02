namespace RhythmHub.Updater;

/// <summary>
/// Exclusive lock on <c>{installRoot}/launcher.lock</c> held for the launcher's lifetime, so two launchers never
/// download, install or recover the same game at once. The OS releases it if the process dies.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock? TryAcquire(string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        var path = Path.Combine(installRoot, "launcher.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            stream.SetLength(0);
            using (var w = new StreamWriter(stream, leaveOpen: true)) w.Write(Environment.ProcessId);
            stream.Flush(flushToDisk: true);
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
