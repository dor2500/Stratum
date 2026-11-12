using Microsoft.UI.Xaml;

namespace Stratum.Shell;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var request = StartupRequest.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _window = new MainWindow(request);
        _window.Activate();
    }
}
