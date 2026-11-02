namespace RhythmHub.Updater;

public enum UpdateErrorCode
{
    Network,
    ManifestNotFound,
    ManifestInvalid,
    LauncherTooOld,
    InsufficientDiskSpace,
    DownloadIncomplete,
    HashMismatch,
    UnsafeArchive,
    InvalidPackage,
    GameRunning,
    NotInstalled,
    NoRollbackTarget,
}

public sealed class UpdateException(UpdateErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateErrorCode Code { get; } = code;
}
