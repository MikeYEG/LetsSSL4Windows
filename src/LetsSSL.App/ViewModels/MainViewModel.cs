using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LetsSSL.App.Service;
using LetsSSL.App.Services;
using LetsSSL.Core;
using LetsSSL.Core.Models;
using LetsSSL.Core.Updates;

namespace LetsSSL.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly LetsSslServices _services;
    private AppSettings _settings;

    public ObservableCollection<CertificateRowViewModel> Certificates { get; } = new();

    public MainViewModel(LetsSslServices services)
    {
        _services = services;
        _settings = services.Settings.Load();

        RenewSelectedCommand = new AsyncRelayCommand(_ => RenewSelectedAsync(), _ => SelectedCertificate != null);
        RenewAllCommand = new AsyncRelayCommand(_ => RenewAllAsync());
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedCertificate != null);
        RefreshCommand = new RelayCommand(_ => LoadCertificates());

        StartServiceCommand = new RelayCommand(_ => StartService(), _ => ServiceInstalled && !ServiceRunning);
        StopServiceCommand = new RelayCommand(_ => StopService(), _ => ServiceInstalled && ServiceRunning);
        InstallServiceCommand = new RelayCommand(_ => InstallService(), _ => !ServiceInstalled);
        OpenReleaseCommand = new RelayCommand(_ => OpenReleasePage());
        DismissUpdateCommand = new RelayCommand(_ => UpdateAvailable = false);
        DownloadInstallCommand = new AsyncRelayCommand(_ => DownloadInstallAsync());

        LoadCertificates();
    }

    public LetsSslServices Services => _services;
    public AppSettings Settings => _settings;

    public AsyncRelayCommand RenewSelectedCommand { get; }
    public AsyncRelayCommand RenewAllCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartServiceCommand { get; }
    public RelayCommand StopServiceCommand { get; }
    public RelayCommand InstallServiceCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public AsyncRelayCommand DownloadInstallCommand { get; }

    private CertificateRowViewModel? _selected;
    public CertificateRowViewModel? SelectedCertificate
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    private string _statusMessage = "Ready.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    private string _logText = string.Empty;
    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy)); }
    }
    public bool IsNotBusy => !_isBusy;

    public string EnvironmentDisplay =>
        _settings.Environment == AcmeEnvironment.Production
            ? "Let's Encrypt (Production)"
            : "Let's Encrypt (Staging — test certificates)";

    public void ReloadSettings()
    {
        _settings = _services.Settings.Load();
        OnPropertyChanged(nameof(EnvironmentDisplay));
    }

    // ---- Renewal service status & monitoring ----

    private const string ServiceName = "LetsSSL4WindowsRenewalService";

    public bool ServiceInstalled { get; private set; }
    public bool ServiceRunning { get; private set; }

    private string _serviceStateText = "Checking…";
    public string ServiceStateText { get => _serviceStateText; private set => SetField(ref _serviceStateText, value); }

    public Brush ServiceStateBrush =>
        ServiceRunning ? Brushes.SeaGreen : ServiceInstalled ? Brushes.DarkOrange : Brushes.Gray;

    private string _nextExpiryText = "Next expiry: —";
    public string NextExpiryText { get => _nextExpiryText; private set => SetField(ref _nextExpiryText, value); }

    private string _lastRunText = "Last renewal check: never";
    public string LastRunText { get => _lastRunText; private set => SetField(ref _lastRunText, value); }

    /// <summary>Refreshes the renewal-service state, next expiry, and last run summary.</summary>
    public void RefreshServiceStatus()
    {
        var status = TryGetServiceStatus();
        ServiceInstalled = status is not null;
        ServiceRunning = status == ServiceControllerStatus.Running;
        ServiceStateText = status switch
        {
            ServiceControllerStatus.Running => "Running",
            null => "Not installed",
            _ => "Stopped",
        };
        OnPropertyChanged(nameof(ServiceInstalled));
        OnPropertyChanged(nameof(ServiceRunning));
        OnPropertyChanged(nameof(ServiceStateBrush));
        CommandManager.InvalidateRequerySuggested();

        var soonest = _services.Certificates.GetAll()
            .Where(c => c.NotAfter is not null).OrderBy(c => c.NotAfter).FirstOrDefault();
        if (soonest?.NotAfter is { } na)
        {
            var days = (na - DateTimeOffset.UtcNow).Days;
            NextExpiryText = days < 0
                ? $"Next expiry: {soonest.PrimaryDomain} (expired)"
                : $"Next expiry: {soonest.PrimaryDomain} in {days} day(s)";
        }
        else
        {
            NextExpiryText = "Next expiry: —";
        }

        var run = _services.RenewalStatusStore.Load();
        LastRunText = run.LastRunUtc is { } t
            ? $"Last renewal check: {t.ToLocalTime():yyyy-MM-dd HH:mm} — {run.Succeeded} ok, {run.Failed} failed"
            : "Last renewal check: never";
    }

    private static ServiceControllerStatus? TryGetServiceStatus()
    {
        try { using var sc = new ServiceController(ServiceName); return sc.Status; }
        catch { return null; }
    }

    private void StartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
            StatusMessage = "Renewal service started.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Start service failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshServiceStatus();
    }

    private void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            StatusMessage = "Renewal service stopped.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Stop service failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshServiceStatus();
    }

    private void InstallService()
    {
        try
        {
            var code = ServiceInstaller.Install();
            StatusMessage = code == 0 ? "Renewal service installed and started." : "Service install failed.";
            if (code != 0)
                MessageBox.Show("Failed to install the renewal service.", "Install failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshServiceStatus();
    }

    public void LoadCertificates()
    {
        Certificates.Clear();
        foreach (var c in _services.Certificates.GetAll().OrderBy(c => c.PrimaryDomain))
            Certificates.Add(new CertificateRowViewModel(c));
        StatusMessage = $"{Certificates.Count} certificate(s).";
        RefreshServiceStatus();
    }

    /// <summary>Issues a new certificate and adds it to the list. Called by the New dialog.</summary>
    public Task IssueAsync(ManagedCertificate config) => RunIssuanceAsync(config);

    private async Task RenewSelectedAsync()
    {
        if (SelectedCertificate is null) return;
        await RunIssuanceAsync(SelectedCertificate.Model);
    }

    private async Task RenewAllAsync()
    {
        IsBusy = true;
        AppendLog("Renewing all due certificates…");
        try
        {
            var progress = CreateProgress();
            var outcomes = await _services.Renewal.RenewDueAsync(_settings.Environment, progress);
            var failed = outcomes.Count(o => !o.Succeeded);
            StatusMessage = $"Renewal complete: {outcomes.Count - failed} ok, {failed} failed.";
        }
        finally
        {
            IsBusy = false;
            LoadCertificates();
        }
    }

    private async Task RunIssuanceAsync(ManagedCertificate config)
    {
        IsBusy = true;
        AppendLog($"Requesting certificate for {config.PrimaryDomain}…");
        try
        {
            var progress = CreateProgress();
            await _services.Manager.RequestAndDeployAsync(config, _settings.Environment, progress);
            StatusMessage = $"Issued certificate for {config.PrimaryDomain}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Issuance failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            LoadCertificates();
        }
    }

    private void DeleteSelected()
    {
        if (SelectedCertificate is null) return;
        var name = SelectedCertificate.Name;
        var confirm = MessageBox.Show(
            $"Remove '{name}' from LetsSSL?\n\nThis stops managing/renewing it. " +
            "The installed certificate and IIS binding are left untouched.",
            "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _services.Certificates.Delete(SelectedCertificate.Model.Id);
        LoadCertificates();
        StatusMessage = $"Removed '{name}'.";
    }

    // ---- Update check (GitHub releases) ----

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetField(ref _updateAvailable, value);
    }

    private string _updateBannerText = string.Empty;
    public string UpdateBannerText { get => _updateBannerText; private set => SetField(ref _updateBannerText, value); }

    public string? UpdateUrl { get; private set; }

    private UpdateInfo? _updateInfo;

    /// <summary>Checks GitHub for a newer release. Silent on failure (offline/rate-limited).</summary>
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            var info = await _services.Updates.CheckAsync(current);
            if (info.UpdateAvailable)
            {
                _updateInfo = info;
                UpdateUrl = info.ReleaseUrl;
                UpdateBannerText =
                    $"A new version ({info.LatestVersion}) is available — you have v{info.CurrentVersion}.";
                UpdateAvailable = true;
            }
        }
        catch
        {
            // No network / rate-limited / no releases yet — just don't show a banner.
        }
    }

    private void OpenReleasePage()
    {
        if (string.IsNullOrEmpty(UpdateUrl)) return;
        try { Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private async Task DownloadInstallAsync()
    {
        if (_updateInfo is null) return;
        if (string.IsNullOrEmpty(_updateInfo.InstallerUrl))
        {
            OpenReleasePage(); // no installer asset — fall back to the release page
            return;
        }
        try
        {
            StatusMessage = "Downloading update…";
            IsBusy = true;
            await Updater.DownloadAndRunAsync(_services.Updates, _updateInfo);
            StatusMessage = "Launching the installer…";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update download failed.";
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IProgress<string> CreateProgress() =>
        new Progress<string>(AppendLog);

    /// <summary>Public log sink (e.g. for the manual-DNS cleanup notice).</summary>
    public void AppendToLog(string message) => AppendLog(message);

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
    }
}
