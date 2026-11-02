using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RhythmHub.Launcher.Services;

namespace RhythmHub.Launcher.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherContext _context;

    public MainWindowViewModel(LauncherContext context)
    {
        _context = context;
        Games = new ObservableCollection<GameViewModel>(
            context.Config.Games.Select(g => new GameViewModel(g, context)));
        selectedGame = Games.FirstOrDefault();
    }

    public ObservableCollection<GameViewModel> Games { get; }

    [ObservableProperty]
    private GameViewModel? selectedGame;

    public string LauncherVersionText => $"RhythmHub {_context.Options.LauncherVersion} · {_context.Options.PlatformId}";

    /// <summary>Version of a downloaded launcher update waiting for a restart; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherUpdate))]
    private string? launcherUpdateVersion;

    public bool HasLauncherUpdate => LauncherUpdateVersion is not null;

    [ObservableProperty]
    private string launcherUpdateText = "";

    partial void OnLauncherUpdateVersionChanged(string? value) => LauncherUpdateText = $"启动器新版本 {value} 已下载";

    public async Task InitializeAsync()
    {
        _ = PrepareLauncherUpdateAsync();
        foreach (var game in Games)
            await game.InitializeAsync();
    }

    private async Task PrepareLauncherUpdateAsync()
    {
        try
        {
            LauncherUpdateVersion = await _context.SelfUpdate.PrepareAsync();
        }
        catch (Exception ex)
        {
            // A broken launcher feed must never block the games.
            _context.Loggers.CreateLogger("SelfUpdate").LogWarning(ex, "Launcher update check failed");
        }
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        // Restarting would abort a download or install in progress; the game itself keeps running.
        if (Games.Any(g => g.IsBusy))
        {
            LauncherUpdateText = "请等游戏下载或安装完成后再重启更新";
            return;
        }
        _context.SelfUpdate.ApplyAndRestart();
    }

    [RelayCommand]
    private void OpenLogs() => _context.Platform.RevealDirectory(_context.Loggers.LogDirectory);
}
