using Avalonia;
using Velopack;

namespace RhythmHub.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles Velopack install / update hooks and exits early for them; must run before anything else.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
