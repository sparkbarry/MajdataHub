using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RhythmHub.Launcher.Resources;
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
        _startupLanguage = LanguageOption.Find(context.Settings.Language);
        selectedLanguage = _startupLanguage;
    }

    private readonly LanguageOption _startupLanguage;

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;

    [ObservableProperty]
    private LanguageOption selectedLanguage;

    /// <summary>Strings are loaded once at startup, so a new language shows after a restart.</summary>
    [ObservableProperty]
    private bool languageChanged;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        // The combo box can briefly clear and restore its selection while binding; only a real change counts.
        if (value is null || value.Id == (_context.Settings.Language ?? "")) return;
        try
        {
            _context.Settings.SetLanguage(value.Id);
            LanguageChanged = value != _startupLanguage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Loggers.CreateLogger("Settings").LogWarning(ex, "Could not save the language choice");
        }
    }

    public ObservableCollection<GameViewModel> Games { get; }

    [ObservableProperty]
    private GameViewModel? selectedGame;

    public string LauncherVersionText => $"RhythmHub {_context.Options.LauncherVersion}";

    /// <summary>Version of a downloaded launcher update waiting for a restart; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherUpdate))]
    private string? launcherUpdateVersion;

    public bool HasLauncherUpdate => LauncherUpdateVersion is not null;

    [ObservableProperty]
    private string launcherUpdateText = "";

    partial void OnLauncherUpdateVersionChanged(string? value) => LauncherUpdateText = Strings.Launcher_UpdateReady;

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
            LauncherUpdateText = Strings.Launcher_WaitForGames;
            return;
        }
        _context.SelfUpdate.ApplyAndRestart();
    }

    [RelayCommand]
    private void OpenLogs() => _context.Platform.RevealDirectory(_context.Loggers.LogDirectory);
}
