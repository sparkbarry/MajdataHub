using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RhythmHub.Launcher.Views;

public partial class AlreadyRunningWindow : Window
{
    public AlreadyRunningWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
