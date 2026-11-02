namespace RhythmHub.Updater;

public enum UpdateErrorCode
{
    Network,
    Timeout,
    RateLimited,
    PackageNotFound,
    SignatureInvalid,
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

public class UpdateException(UpdateErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateErrorCode Code { get; } = code;
}
