using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LetsSSL.Core.Storage;

namespace LetsSSL.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Surface unhandled UI-thread exceptions instead of silently crashing.
        DispatcherUnhandledException += OnUnhandledException;
        // Swallow faults from fire-and-forget background tasks (e.g. an update or
        // DNS check) so an unobserved exception can never tear the process down.
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        // Apply the saved theme (dark by default) before any window shows.
        var settings = new SettingsRepository(new AppPaths()).Load();
        ThemeManager.Apply(settings.Theme);

        // Match each window's title bar to the current theme as it loads.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Theming.ApplyTitleBar((Window)sender)));
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "LetsSSL4Windows — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
