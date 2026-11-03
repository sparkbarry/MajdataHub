using Avalonia.Controls;

namespace RhythmHub.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            // Let the sidebar run under the traffic lights, as native macOS apps do.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 44;
            TitleBarSpacer.Height = 44;
        }
    }
}
