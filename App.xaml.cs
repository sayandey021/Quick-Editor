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
        string? initialFile = null;

        try
        {
            var appArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (appArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File &&
                appArgs.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs &&
                fileArgs.Files.Count > 0)
            {
                initialFile = fileArgs.Files[0].Path;
            }
        }
        catch
        {
            // Fallback for unpackaged or non-lifecycle launches
        }

        if (string.IsNullOrEmpty(initialFile))
        {
            initialFile = Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(argument => File.Exists(argument));
        }

        _window = new MainWindow(initialFile);
        _window.Activate();
    }
}