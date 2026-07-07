using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LetsSSL.Core.Storage;

namespace LetsSSL.App;

public partial class App : Application
{
    // The shared window/taskbar icon, loaded once from the embedded resource.
    private static readonly Lazy<BitmapFrame?> AppIcon = new(() =>
    {
        try
        {
            return BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        }
        catch { return null; }
    });

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

        // Give every window the app icon and a theme-matched title bar as it loads.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                var window = (Window)sender;
                if (window.Icon is null && AppIcon.Value is { } icon)
                    window.Icon = icon;
                Theming.ApplyTitleBar(window);
            }));
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
