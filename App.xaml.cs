using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace BatteryChecker;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        // Launched at login (Store: the StartupTask; .exe: the Run key passes --startup): sit in the tray, don't pop the window.
        bool startedAtLogin = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask
                              || Environment.GetCommandLineArgs().Contains("--startup");
        if (!startedAtLogin)
            _window.ShowFlyout();

        // A second launch redirected here just means "show me".
        AppInstance.GetCurrent().Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(() => _window.ShowFlyout());
    }
}
