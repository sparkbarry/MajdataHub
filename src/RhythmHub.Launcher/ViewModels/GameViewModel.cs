using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RhythmHub.Launcher.Resources;
using RhythmHub.Launcher.Services;
using RhythmHub.Updater;
using RhythmHub.Updater.Models;

namespace RhythmHub.Launcher.ViewModels;

public enum GameState
{
    Idle,
    Checking,
    NotInstalled,
    Ready,
    UpdateAvailable,
    Downloading,
    Verifying,
    Installing,
    Running,
    Failed,
}

public sealed partial class GameViewModel : ObservableObject
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(2);

    private readonly LauncherContext _context;
    private readonly GameEntry _entry;
    private readonly GameUpdateService _updater;
    private readonly ILogger _logger;
    private CancellationTokenSource? _operation;
    private bool _userCancelled;
    private GameRelease? _available;

    public GameViewModel(GameEntry entry, LauncherContext context)
    {
        _context = context;
        _entry = entry;
        Id = entry.Id;
        DisplayName = entry.DisplayName;
        Description = entry.LocalizedDescription;
        Accent = Brush.Parse(entry.Color ?? "#0071E3");
        Icon = LoadIcon(entry.Icon);
        _updater = new GameUpdateService(entry.Id, context.Options, context.Http, context.Platform, context.Loggers);
        _logger = context.Loggers.CreateLogger($"Game.{entry.Id}");
        selectedChannel = ChannelOption.Find(context.Settings.GetChannel(entry.Id, context.Options.Channel));
        _updater.Channel = selectedChannel.Id;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Initial => DisplayName[..1];
    public IBrush Accent { get; }

    /// <summary>The game's icon; null shows the initial on the accent color instead.</summary>
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;

    /// <summary>Where the game lives, with the home directory shortened to ~.</summary>
    public string GameFolderText
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = _updater.Layout.GameRoot;
            return OperatingSystem.IsWindows() || !root.StartsWith(home, StringComparison.Ordinal) ? root : "~" + root[home.Length..];
        }
    }

    public string RevealFolderText => OperatingSystem.IsMacOS() ? Strings.RevealInFinder : Strings.RevealInExplorer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(PrimaryText), nameof(ShowProgress), nameof(ShowStatus), nameof(CanChangeChannel),
        nameof(IsFailed), nameof(SidebarText), nameof(ShowCheckButton))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(CheckCommand), nameof(CancelCommand), nameof(RollbackCommand))]
    private GameState state;

    public IReadOnlyList<ChannelOption> Channels => ChannelOption.All;

    [ObservableProperty]
    private ChannelOption selectedChannel;

    /// <summary>Switching mid-download would orphan the transfer, and mid-game the install would be refused.</summary>
    public bool CanChangeChannel => !IsBusy && State != GameState.Running;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledVersionShort), nameof(PrimaryText), nameof(SidebarText),
        nameof(HasInstall), nameof(ShowCheckButton))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(OpenGameFolderCommand))]
    private InstalledGame? installed;

    [ObservableProperty]
    private string? remoteVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarText))]
    private double progressValue;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private bool canRollback;

    [ObservableProperty]
    private string? previousVersion;

    public bool IsBusy => State is GameState.Checking or GameState.Downloading or GameState.Verifying or GameState.Installing;
    public bool ShowProgress => State is GameState.Downloading or GameState.Verifying or GameState.Installing;
    public bool ShowStatus => !ShowProgress;

    /// <summary>The separate check button, hidden while the primary button already checks.</summary>
    public bool ShowCheckButton => !IsBusy && PrimaryText != CheckText;
    public bool IsFailed => State == GameState.Failed;
    public bool HasInstall => Installed is not null;
    public string InstalledVersionShort => Installed?.Version ?? Strings.NotInstalled;

    /// <summary>The one-line state under the game's name in the sidebar.</summary>
    public string SidebarText => State switch
    {
        GameState.Downloading or GameState.Verifying => string.Format(Strings.Sidebar_Downloading, ProgressValue.ToString("0")),
        GameState.Installing => Strings.Sidebar_Installing,
        GameState.Running => Strings.Sidebar_Running,
        _ => InstalledVersionShort,
    };

    public string PrimaryText => State switch
    {
        GameState.UpdateAvailable when Installed is null => Strings.Action_Install,
        GameState.UpdateAvailable => Strings.Action_Update,
        GameState.Checking => Strings.Action_Checking,
        GameState.Downloading or GameState.Verifying or GameState.Installing => Strings.Action_Updating,
        GameState.Running => Strings.Action_Running,
        GameState.Failed when _available is not null => Strings.Action_Retry,
        _ when Installed is not null => Strings.Action_Play,
        _ => CheckText,
    };

    private static string CheckText => Strings.Action_CheckForUpdates;

    partial void OnSelectedChannelChanged(ChannelOption value)
    {
        if (value is null || _updater.Channel == value.Id) return;
        _logger.LogInformation("Channel {Old} -> {New}", _updater.Channel, value.Id);
        _updater.Channel = value.Id;
        try
        {
            _context.Settings.SetChannel(Id, value.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the channel choice");
        }
        _available = null;
        _ = CheckAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _updater.RecoverAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery failed");
        }
        await RefreshInstalledAsync();
        await CheckAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        if (State is GameState.UpdateAvailable or GameState.Failed && _available is not null)
            await UpdateAsync(_available);
        else if (Installed is not null)
            await LaunchAsync();
        else
            await CheckAsync();
    }

    private bool CanPrimary() => !IsBusy && State != GameState.Running;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        await RunAsync(GameState.Checking, Strings.Status_Checking, CheckTimeout, async ct =>
        {
            _available = await _updater.CheckAsync(ct);
            RemoteVersion = _available?.Version ?? Installed?.Version;
            if (_available is null)
            {
                SetState(GameState.Ready, string.Format(Strings.Status_UpToDate, Installed?.Version));
                return;
            }
            var resumable = await _updater.GetResumableBytesAsync(_available, ct);
            var download = _available.DownloadBytes;
            var size = resumable > 0
                ? string.Format(Strings.Size_Resumable, FormatSize(resumable))
                : Installed is not null && download < _available.InstalledSizeBytes
                    ? string.Format(Strings.Size_Delta, FormatSize(download), FormatSize(_available.InstalledSizeBytes))
                    : FormatSize(download);
            SetState(GameState.UpdateAvailable, Installed is null
                ? string.Format(Strings.Status_CanInstall, _available.Version, size)
                : string.Format(Strings.Status_ChannelLatest, SelectedChannel.Label, _available.Version, size));
        });
    }

    private bool CanCheck() => !IsBusy;

    private async Task UpdateAsync(GameRelease release)
    {
        await RunAsync(GameState.Downloading, string.Format(Strings.Status_Downloading, release.Version), null, async ct =>
        {
            var meter = new SpeedMeter();
            var stage = DownloadStage.Downloading;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Stage != stage)
                {
                    stage = p.Stage;
                    meter = new SpeedMeter();
                    StatusText = stage == DownloadStage.ReusingLocalFiles
                        ? Strings.Status_Reusing
                        : string.Format(Strings.Status_Downloading, release.Version);
                }
                ProgressValue = p.TotalBytes == 0 ? 0 : 100.0 * p.CompletedBytes / p.TotalBytes;
                ProgressText = stage == DownloadStage.ReusingLocalFiles
                    ? $"{FormatSize(p.CompletedBytes)} / {FormatSize(p.TotalBytes)}"
                    : $"{FormatSize(p.CompletedBytes)} / {FormatSize(p.TotalBytes)}  ·  {FormatSize((long)meter.Sample(p.CompletedBytes))}/s";
            });
            await _updater.DownloadAsync(release, progress, ct);

            SetState(GameState.Installing, string.Format(Strings.Status_Installing, release.Version));
            IsProgressIndeterminate = true;
            ProgressText = "";
            await _updater.InstallAsync(release, ct);

            _available = null;
            await RefreshInstalledAsync();
            SetState(GameState.Ready, string.Format(Strings.Status_Installed, release.Version));
        });
    }

    private async Task LaunchAsync()
    {
        if (Installed is not { } game) return;
        try
        {
            if (await _context.Platform.IsGameRunningAsync(game.VersionDirectory, game.Entrypoint, CancellationToken.None))
            {
                StatusText = string.Format(Strings.Status_AlreadyRunning, DisplayName);
                return;
            }
            var ticket = _updater.Health.Prepare(game);
            _logger.LogInformation("Launching {Version} from {Dir}", game.Version, game.VersionDirectory);
            await _context.Platform.LaunchGameAsync(game.VersionDirectory, game.Entrypoint, ticket.Environment, CancellationToken.None);
            SetState(GameState.Running, string.Format(Strings.Status_Launching, game.Version));

            var outcome = await _updater.Health.WaitAsync(ticket, _entry.HealthCheck,
                ct => _context.Platform.IsGameRunningAsync(game.VersionDirectory, game.Entrypoint, ct),
                HealthTimeout, CancellationToken.None);
            await _updater.RecordLaunchAsync(game.Version, outcome, CancellationToken.None);
            await RefreshInstalledAsync();

            switch (outcome)
            {
                case LaunchOutcome.Healthy:
                    SetState(GameState.Ready, string.Format(Strings.Status_Launched, game.Version));
                    break;
                case LaunchOutcome.Exited:
                    SetState(GameState.Failed, CanRollback
                        ? string.Format(Strings.Status_LaunchFailedRollback, game.Version)
                        : string.Format(Strings.Status_LaunchFailed, game.Version));
                    break;
                case LaunchOutcome.TimedOut:
                    SetState(GameState.Ready, string.Format(Strings.Status_LaunchTimedOut, game.Version, HealthTimeout.TotalMinutes.ToString("0")) +
                        (CanRollback ? Strings.Status_LaunchTimedOutRollback : ""));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch failed");
            SetState(GameState.Failed, string.Format(Strings.Status_LaunchError, ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _userCancelled = true;
        _operation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRollbackNow))]
    private async Task RollbackAsync()
    {
        await RunAsync(GameState.Installing, Strings.Status_RollingBack, null, async ct =>
        {
            await _updater.RollbackAsync(ct);
            await RefreshInstalledAsync();
            SetState(GameState.Ready, string.Format(Strings.Status_RolledBack, Installed?.Version));
        });
    }

    private bool CanRollbackNow() => !IsBusy && CanRollback;

    partial void OnCanRollbackChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(HasInstall))]
    private void OpenGameFolder() => _context.Platform.RevealDirectory(_updater.Layout.GameRoot);

    private async Task RefreshInstalledAsync()
    {
        Installed = await _updater.GetInstalledAsync(CancellationToken.None);
        var stateFile = await new VersionRepository(_updater.Layout).LoadAsync(CancellationToken.None);
        CanRollback = stateFile.PreviousGoodVersion is { } prev && prev != stateFile.ActiveVersion &&
                      Directory.Exists(_updater.Layout.VersionDirectory(prev));
        PreviousVersion = CanRollback ? stateFile.PreviousGoodVersion : null;
    }

    /// <summary>Runs one state-machine step; failures keep whatever version is installed playable.</summary>
    private async Task RunAsync(GameState busyState, string message, TimeSpan? timeout, Func<CancellationToken, Task> body)
    {
        using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        _operation = cts;
        _userCancelled = false;
        ProgressValue = 0;
        ProgressText = "";
        IsProgressIndeterminate = false;
        SetState(busyState, message);
        try
        {
            await body(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var timedOut = !_userCancelled && timeout is not null;
            _logger.LogWarning("{Step} cancelled", busyState);
            SetState(_available is not null ? GameState.UpdateAvailable : Installed is null ? GameState.Failed : GameState.Ready,
                (timedOut ? Strings.Status_TimedOut : _available is not null ? Strings.Status_Paused : Strings.Status_Cancelled) + OfflineHint());
        }
        catch (UpdateException ex)
        {
            _logger.LogError(ex, "{Step} failed with {Code}", busyState, ex.Code);
            SetState(GameState.Failed, (ex.Code == UpdateErrorCode.ManifestNotFound
                ? string.Format(Strings.Status_ChannelEmpty, SelectedChannel.Label)
                : Describe(ex)) + OfflineHint());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Step} failed unexpectedly", busyState);
            SetState(GameState.Failed, string.Format(Strings.Status_Error, ex.Message) + OfflineHint());
        }
        finally
        {
            _operation = null;
            IsProgressIndeterminate = false;
        }
    }

    private string OfflineHint() => Installed is null ? "" : "\n" + string.Format(Strings.Status_OfflineHint, Installed.Version);

    private void SetState(GameState next, string message)
    {
        State = next;
        StatusText = message;
    }

    private static string Describe(UpdateException ex) => string.Format(Strings.Error_Detail, ex.Code switch
    {
        UpdateErrorCode.Network => Strings.Error_Network,
        UpdateErrorCode.Timeout => Strings.Error_Timeout,
        UpdateErrorCode.RateLimited => Strings.Error_RateLimited,
        UpdateErrorCode.PackageNotFound => Strings.Error_PackageNotFound,
        UpdateErrorCode.SignatureInvalid => Strings.Error_SignatureInvalid,
        UpdateErrorCode.ManifestNotFound => Strings.Error_ManifestNotFound,
        UpdateErrorCode.ManifestInvalid => Strings.Error_ManifestInvalid,
        UpdateErrorCode.LauncherTooOld => Strings.Error_LauncherTooOld,
        UpdateErrorCode.InsufficientDiskSpace => Strings.Error_InsufficientDiskSpace,
        UpdateErrorCode.DownloadIncomplete => Strings.Error_DownloadIncomplete,
        UpdateErrorCode.HashMismatch => Strings.Error_HashMismatch,
        UpdateErrorCode.UnsafeArchive or UpdateErrorCode.InvalidPackage => Strings.Error_InvalidPackage,
        UpdateErrorCode.GameRunning => Strings.Error_GameRunning,
        UpdateErrorCode.NoRollbackTarget => Strings.Error_NoRollbackTarget,
        _ => Strings.Error_UpdateFailed,
    }, ex.Message);

    private Bitmap? LoadIcon(string? path)
    {
        if (path is null) return null;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://RhythmHub.Launcher/Assets/{path}"));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            _logger.LogWarning(ex, "Icon {Path} not found", path);
            return null;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Bytes per second over a sliding ~2s window.</summary>
    private sealed class SpeedMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

        public double Sample(long bytes)
        {
            var now = _clock.Elapsed;
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().At > TimeSpan.FromSeconds(2)) _samples.Dequeue();
            var (at, first) = _samples.Peek();
            var dt = (now - at).TotalSeconds;
            return dt <= 0 ? 0 : (bytes - first) / dt;
        }
    }
}
