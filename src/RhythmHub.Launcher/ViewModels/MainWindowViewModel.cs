using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public async Task InitializeAsync()
    {
        foreach (var game in Games)
            await game.InitializeAsync();
    }

    [RelayCommand]
    private void OpenLogs() => _context.Platform.RevealDirectory(_context.Loggers.LogDirectory);
}
