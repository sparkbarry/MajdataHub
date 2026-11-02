using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
        Description = entry.Description ?? "";
        _updater = new GameUpdateService(entry.Id, context.Options, context.Http, context.Platform, context.Loggers);
        _logger = context.Loggers.CreateLogger($"Game.{entry.Id}");
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Initial => DisplayName[..1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(PrimaryText), nameof(ShowProgress))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(CheckCommand), nameof(CancelCommand), nameof(RollbackCommand))]
    private GameState state;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledVersionText), nameof(PrimaryText))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(OpenGameFolderCommand))]
    private InstalledGame? installed;

    [ObservableProperty]
    private string? remoteVersion;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private bool canRollback;

    public bool IsBusy => State is GameState.Checking or GameState.Downloading or GameState.Verifying or GameState.Installing;
    public bool ShowProgress => State is GameState.Downloading or GameState.Verifying or GameState.Installing;
    public string InstalledVersionText => Installed is null ? "未安装" : $"已安装 {Installed.Version}";

    public string PrimaryText => State switch
    {
        GameState.UpdateAvailable when Installed is null => "安装",
        GameState.UpdateAvailable => "更新",
        GameState.Checking => "检查中…",
        GameState.Downloading or GameState.Verifying or GameState.Installing => "更新中…",
        GameState.Running => "游戏运行中",
        GameState.Failed when _available is not null => "重试",
        _ when Installed is not null => "开始游戏",
        _ => "检查更新",
    };

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
        await RunAsync(GameState.Checking, "正在检查更新…", CheckTimeout, async ct =>
        {
            _available = await _updater.CheckAsync(ct);
            RemoteVersion = _available?.Version ?? Installed?.Version;
            if (_available is null)
            {
                SetState(GameState.Ready, $"已是最新版本 {Installed?.Version}");
                return;
            }
            var resumable = await _updater.GetResumableBytesAsync(_available, ct);
            var download = _available.DownloadBytes;
            var size = resumable > 0
                ? $"已下载 {FormatSize(resumable)}，可继续"
                : Installed is not null && download < _available.InstalledSizeBytes
                    ? $"需下载 {FormatSize(download)}，完整大小 {FormatSize(_available.InstalledSizeBytes)}"
                    : FormatSize(download);
            SetState(GameState.UpdateAvailable, Installed is null
                ? $"可安装 {_available.Version}（{size}）"
                : $"发现新版本 {_available.Version}（{size}）");
        });
    }

    private bool CanCheck() => !IsBusy;

    private async Task UpdateAsync(GameRelease release)
    {
        await RunAsync(GameState.Downloading, $"正在下载 {release.Version}…", null, async ct =>
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
                        ? "正在复用本地未变化的文件…"
                        : $"正在下载 {release.Version}…";
                }
                ProgressValue = p.TotalBytes == 0 ? 0 : 100.0 * p.CompletedBytes / p.TotalBytes;
                ProgressText = stage == DownloadStage.ReusingLocalFiles
                    ? $"{FormatSize(p.CompletedBytes)} / {FormatSize(p.TotalBytes)}"
                    : $"{FormatSize(p.CompletedBytes)} / {FormatSize(p.TotalBytes)}  ·  {FormatSize((long)meter.Sample(p.CompletedBytes))}/s";
            });
            await _updater.DownloadAsync(release, progress, ct);

            SetState(GameState.Installing, $"正在安装 {release.Version}…");
            IsProgressIndeterminate = true;
            ProgressText = "";
            await _updater.InstallAsync(release, ct);

            _available = null;
            await RefreshInstalledAsync();
            SetState(GameState.Ready, $"{release.Version} 安装完成");
        });
    }

    private async Task LaunchAsync()
    {
        if (Installed is not { } game) return;
        try
        {
            if (await _context.Platform.IsGameRunningAsync(game.VersionDirectory, game.Entrypoint, CancellationToken.None))
            {
                StatusText = $"{DisplayName} 已在运行";
                return;
            }
            var ticket = _updater.Health.Prepare(game);
            _logger.LogInformation("Launching {Version} from {Dir}", game.Version, game.VersionDirectory);
            await _context.Platform.LaunchGameAsync(game.VersionDirectory, game.Entrypoint, ticket.Environment, CancellationToken.None);
            SetState(GameState.Running, $"正在启动 {game.Version}…");

            var outcome = await _updater.Health.WaitAsync(ticket, _entry.HealthCheck,
                ct => _context.Platform.IsGameRunningAsync(game.VersionDirectory, game.Entrypoint, ct),
                HealthTimeout, CancellationToken.None);
            await _updater.RecordLaunchAsync(game.Version, outcome, CancellationToken.None);
            await RefreshInstalledAsync();

            switch (outcome)
            {
                case LaunchOutcome.Healthy:
                    SetState(GameState.Ready, $"已启动 {game.Version}");
                    break;
                case LaunchOutcome.Exited:
                    SetState(GameState.Failed, CanRollback
                        ? $"{game.Version} 未能正常启动。可以点击“恢复上一版本”回到可用的旧版。"
                        : $"{game.Version} 未能正常启动，请查看日志或重新安装。");
                    break;
                case LaunchOutcome.TimedOut:
                    SetState(GameState.Ready, $"{game.Version} 已启动，但未在 {HealthTimeout.TotalMinutes:0} 分钟内报告就绪。" +
                        (CanRollback ? "如果游戏异常，可恢复上一版本。" : ""));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch failed");
            SetState(GameState.Failed, $"启动失败：{ex.Message}");
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
        await RunAsync(GameState.Installing, "正在恢复上一版本…", null, async ct =>
        {
            await _updater.RollbackAsync(ct);
            await RefreshInstalledAsync();
            SetState(GameState.Ready, $"已恢复到 {Installed?.Version}");
        });
    }

    private bool CanRollbackNow() => !IsBusy && CanRollback;

    partial void OnCanRollbackChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(HasInstall))]
    private void OpenGameFolder() => _context.Platform.RevealDirectory(_updater.Layout.GameRoot);

    private bool HasInstall() => Installed is not null;

    private async Task RefreshInstalledAsync()
    {
        Installed = await _updater.GetInstalledAsync(CancellationToken.None);
        var stateFile = await new VersionRepository(_updater.Layout).LoadAsync(CancellationToken.None);
        CanRollback = stateFile.PreviousGoodVersion is { } prev && prev != stateFile.ActiveVersion &&
                      Directory.Exists(_updater.Layout.VersionDirectory(prev));
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
                (timedOut ? "连接超时，可稍后重试" : _available is not null ? "已暂停，再次点击可继续下载" : "已取消") + OfflineHint());
        }
        catch (UpdateException ex)
        {
            _logger.LogError(ex, "{Step} failed with {Code}", busyState, ex.Code);
            SetState(GameState.Failed, Describe(ex) + OfflineHint());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Step} failed unexpectedly", busyState);
            SetState(GameState.Failed, $"出错了：{ex.Message}" + OfflineHint());
        }
        finally
        {
            _operation = null;
            IsProgressIndeterminate = false;
        }
    }

    private string OfflineHint() => Installed is null ? "" : $"\n仍可启动已安装的 {Installed.Version}。";

    private void SetState(GameState next, string message)
    {
        State = next;
        StatusText = message;
    }

    private static string Describe(UpdateException ex) => ex.Code switch
    {
        UpdateErrorCode.Network => "无法连接更新服务器",
        UpdateErrorCode.Timeout => "网络超时，已下载的部分会保留，可继续下载",
        UpdateErrorCode.RateLimited => "更新服务器繁忙，请稍后重试",
        UpdateErrorCode.PackageNotFound => "服务器上缺少该版本的文件",
        UpdateErrorCode.SignatureInvalid => "更新清单签名无效，已拒绝",
        UpdateErrorCode.ManifestNotFound => "服务器上暂无该游戏的发布版本",
        UpdateErrorCode.ManifestInvalid => "更新清单无效",
        UpdateErrorCode.LauncherTooOld => "需要先更新 RhythmHub",
        UpdateErrorCode.InsufficientDiskSpace => "磁盘空间不足",
        UpdateErrorCode.DownloadIncomplete => "下载不完整，请重试",
        UpdateErrorCode.HashMismatch => "文件校验失败，请重试",
        UpdateErrorCode.UnsafeArchive or UpdateErrorCode.InvalidPackage => "安装包无效",
        UpdateErrorCode.GameRunning => "请先关闭游戏再更新",
        UpdateErrorCode.NoRollbackTarget => "没有可恢复的上一版本",
        _ => "更新失败",
    } + $"（{ex.Message}）";

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
