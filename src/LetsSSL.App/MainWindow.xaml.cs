using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LetsSSL.App.Services;
using LetsSSL.App.ViewModels;
using LetsSSL.App.Views;
using LetsSSL.Core;
using LetsSSL.Core.Iis;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;

namespace LetsSSL.App;

public partial class MainWindow : Window
{
    private readonly LetsSslServices _services;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        // Supply the interactive manual-DNS handler so DNS-01 with the "Manual"
        // provider can prompt the user from the desktop app.
        var manualDns = new DialogManualDnsInteraction();
        _services = new LetsSslServices(AppLogging.Factory, manualDns: manualDns);
        _vm = new MainViewModel(_services);
        DataContext = _vm;

        // Route the manual-DNS cleanup notice to the activity log (UI-thread safe).
        manualDns.Log = new Progress<string>(_vm.AppendToLog);

        // Auto-scroll the activity log to the newest line.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogText))
                LogScroller.ScrollToEnd();
        };

        // Keep the renewal-service status / last-run line current.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _statusTimer.Tick += (_, _) => _vm.RefreshServiceStatus();
        _statusTimer.Start();

        // Check GitHub for a newer release (non-blocking; shows a banner if found).
        _ = _vm.CheckForUpdatesAsync();
    }

    private async void OnNewCertificate(object sender, RoutedEventArgs e)
    {
        var dialogVm = new NewCertificateViewModel(_vm.Settings.ContactEmail);
        var dialog = new NewCertificateWindow(dialogVm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var config = dialogVm.BuildConfig();
            await _vm.IssueAsync(config);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var settingsVm = new SettingsViewModel(_services.Settings, _services.Updates, _services.Paths);
        var dialog = new SettingsWindow(settingsVm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            settingsVm.Save();
            _vm.ReloadSettings();
            ThemeManager.Apply(settingsVm.IsDarkMode ? AppTheme.Dark : AppTheme.Light);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var cert = _vm.SelectedCertificate?.Model;
        if (cert is null)
        {
            MessageBox.Show("Select a certificate to export.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(cert.Thumbprint))
        {
            MessageBox.Show("This certificate hasn't been issued yet, so there's nothing to export.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var installed = _services.Store.FindByThumbprint(cert.Thumbprint);
        if (installed is null)
        {
            MessageBox.Show("The certificate was not found in the Windows store. Try renewing it first.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new ExportCertificateWindow(cert.PrimaryDomain) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.Format == ExportCertificateWindow.ExportFormat.Pfx)
            {
                CertificateExporter.ExportPfx(installed, dialog.OutputPath, dialog.Password);
            }
            else
            {
                var keyPath = Path.ChangeExtension(dialog.OutputPath, ".key");
                CertificateExporter.ExportPem(installed, dialog.OutputPath, keyPath);
            }
            MessageBox.Show($"Exported to {dialog.OutputPath}", "Export complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnBindToIis(object sender, RoutedEventArgs e)
    {
        var cert = _vm.SelectedCertificate?.Model;
        if (cert is null)
        {
            MessageBox.Show("Select a certificate to bind.", "Bind to IIS",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(cert.Thumbprint))
        {
            MessageBox.Show("This certificate hasn't been issued yet, so there's nothing to bind.",
                "Bind to IIS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        using (var found = _services.Store.FindByThumbprint(cert.Thumbprint))
        {
            if (found is null)
            {
                MessageBox.Show("The certificate was not found in the Windows store. Try renewing it first.",
                    "Bind to IIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (!IisManager.IsIisAvailable())
        {
            MessageBox.Show("IIS doesn't appear to be installed on this machine.",
                "Bind to IIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sites = new IisManager().GetSites();
        if (sites.Count == 0)
        {
            MessageBox.Show("No IIS sites were found to bind to.",
                "Bind to IIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new BindIisWindow(cert.Name, sites) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedSiteName is not { } siteName) return;

        try
        {
            _services.Manager.BindToIisSite(cert, siteName);
            _vm.AppendToLog($"Bound {cert.Name} to IIS site \"{siteName}\".");
            _vm.LoadCertificates();
            MessageBox.Show($"Bound \"{cert.Name}\" to IIS site \"{siteName}\".",
                "Bind to IIS", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Bind to IIS failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCopyThumbprint(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string thumbprint || string.IsNullOrEmpty(thumbprint))
            return;

        try { Clipboard.SetText(thumbprint); }
        catch { /* clipboard momentarily unavailable */ }

        var original = button.Content;
        button.Content = "Copied!";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { button.Content = original; timer.Stop(); };
        timer.Start();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(next);

        // Persist the choice so it sticks across restarts.
        var settings = _services.Settings.Load();
        settings.Theme = next;
        _services.Settings.Save(settings);
    }
}
