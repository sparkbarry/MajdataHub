using Avalonia;

namespace RhythmHub.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // TODO(phase C): VelopackApp.Build().Run(); must run before anything else.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
