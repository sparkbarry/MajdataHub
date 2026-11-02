using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RhythmHub.Launcher.Services;
using RhythmHub.Launcher.ViewModels;
using RhythmHub.Launcher.Views;

namespace RhythmHub.Launcher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var context = LauncherContext.Create();
            if (context.InstanceLock is null)
            {
                context.Dispose();
                desktop.MainWindow = new AlreadyRunningWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var vm = new MainWindowViewModel(context);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => context.Dispose();
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
