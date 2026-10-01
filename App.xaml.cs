using Microsoft.UI.Xaml;

namespace QuickEditor;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var initialFile = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(argument => File.Exists(argument));
        _window = new MainWindow(initialFile);
        _window.Activate();
    }
}