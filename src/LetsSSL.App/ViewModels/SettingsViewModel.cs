using System.Reflection;
using System.Windows;
using LetsSSL.App.Services;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;
using LetsSSL.Core.Updates;

namespace LetsSSL.App.ViewModels;

/// <summary>Backs the Settings dialog (environment, renewal, notifications, updates).</summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsRepository _repository;
    private readonly UpdateChecker _updateChecker;
    private UpdateInfo? _updateInfo;

    public SettingsViewModel(SettingsRepository repository, UpdateChecker updateChecker)
    {
        _repository = repository;
        _updateChecker = updateChecker;
        CheckForUpdatesCommand = new AsyncRelayCommand(_ => CheckForUpdatesAsync());
        DownloadInstallCommand = new AsyncRelayCommand(_ => DownloadInstallAsync(), _ => UpdateFound);

        var s = repository.Load();
        _isProduction = s.Environment == AcmeEnvironment.Production;
        _contactEmail = s.ContactEmail;
        _enableAutoRenewal = s.EnableAutoRenewal;
        _isDarkMode = s.Theme == AppTheme.Dark;

        var n = s.Notifications;
        _notifyOnSuccess = n.NotifyOnSuccess;
        _notifyOnFailure = n.NotifyOnFailure;
        _webhookUrl = n.WebhookUrl ?? string.Empty;
        _emailEnabled = n.EmailEnabled;
        _smtpHost = n.SmtpHost ?? string.Empty;
        _smtpPort = n.SmtpPort;
        _smtpUseSsl = n.SmtpUseSsl;
        _smtpUsername = n.SmtpUsername ?? string.Empty;
        _smtpPassword = SecretProtector.Unprotect(n.SmtpPasswordProtected) ?? string.Empty;
        _fromAddress = n.FromAddress ?? string.Empty;
        _toAddress = n.ToAddress ?? string.Empty;
    }

    // ---- General ----

    private bool _isProduction;
    public bool IsProduction { get => _isProduction; set => SetField(ref _isProduction, value); }

    private string _contactEmail;
    public string ContactEmail { get => _contactEmail; set => SetField(ref _contactEmail, value); }

    private bool _enableAutoRenewal;
    public bool EnableAutoRenewal { get => _enableAutoRenewal; set => SetField(ref _enableAutoRenewal, value); }

    private bool _isDarkMode;
    public bool IsDarkMode { get => _isDarkMode; set => SetField(ref _isDarkMode, value); }

    // ---- Updates ----

    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand DownloadInstallCommand { get; }

    private bool _updateFound;
    public bool UpdateFound { get => _updateFound; private set => SetField(ref _updateFound, value); }

    private string _updateCheckStatus = string.Empty;
    public string UpdateCheckStatus { get => _updateCheckStatus; private set => SetField(ref _updateCheckStatus, value); }

    private async Task CheckForUpdatesAsync()
    {
        UpdateFound = false;
        UpdateCheckStatus = "Checking…";
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            _updateInfo = await _updateChecker.CheckAsync(current);
            if (_updateInfo.UpdateAvailable)
            {
                UpdateFound = true;
                UpdateCheckStatus = $"Update available: {_updateInfo.LatestVersion} (you have v{_updateInfo.CurrentVersion}).";
            }
            else
            {
                UpdateCheckStatus = $"You're on the latest version (v{_updateInfo.CurrentVersion}).";
            }
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = "Couldn't check for updates: " + ex.Message;
        }
    }

    private async Task DownloadInstallAsync()
    {
        if (_updateInfo is null) return;
        try
        {
            UpdateCheckStatus = "Downloading update…";
            await Updater.DownloadAndRunAsync(_updateChecker, _updateInfo);
            UpdateCheckStatus = "Launching the installer…";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- Notifications ----

    private bool _notifyOnSuccess;
    public bool NotifyOnSuccess { get => _notifyOnSuccess; set => SetField(ref _notifyOnSuccess, value); }

    private bool _notifyOnFailure;
    public bool NotifyOnFailure { get => _notifyOnFailure; set => SetField(ref _notifyOnFailure, value); }

    private string _webhookUrl;
    public string WebhookUrl { get => _webhookUrl; set => SetField(ref _webhookUrl, value); }

    private bool _emailEnabled;
    public bool EmailEnabled { get => _emailEnabled; set => SetField(ref _emailEnabled, value); }

    private string _smtpHost;
    public string SmtpHost { get => _smtpHost; set => SetField(ref _smtpHost, value); }

    private int _smtpPort;
    public int SmtpPort { get => _smtpPort; set => SetField(ref _smtpPort, value); }

    private bool _smtpUseSsl;
    public bool SmtpUseSsl { get => _smtpUseSsl; set => SetField(ref _smtpUseSsl, value); }

    private string _smtpUsername;
    public string SmtpUsername { get => _smtpUsername; set => SetField(ref _smtpUsername, value); }

    private string _smtpPassword;
    public string SmtpPassword { get => _smtpPassword; set => SetField(ref _smtpPassword, value); }

    private string _fromAddress;
    public string FromAddress { get => _fromAddress; set => SetField(ref _fromAddress, value); }

    private string _toAddress;
    public string ToAddress { get => _toAddress; set => SetField(ref _toAddress, value); }

    public void Save()
    {
        _repository.Save(new AppSettings
        {
            Environment = IsProduction ? AcmeEnvironment.Production : AcmeEnvironment.Staging,
            ContactEmail = ContactEmail?.Trim() ?? string.Empty,
            EnableAutoRenewal = EnableAutoRenewal,
            Theme = IsDarkMode ? AppTheme.Dark : AppTheme.Light,
            Notifications = new NotificationSettings
            {
                NotifyOnSuccess = NotifyOnSuccess,
                NotifyOnFailure = NotifyOnFailure,
                WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(),
                EmailEnabled = EmailEnabled,
                SmtpHost = string.IsNullOrWhiteSpace(SmtpHost) ? null : SmtpHost.Trim(),
                SmtpPort = SmtpPort,
                SmtpUseSsl = SmtpUseSsl,
                SmtpUsername = string.IsNullOrWhiteSpace(SmtpUsername) ? null : SmtpUsername.Trim(),
                SmtpPasswordProtected = string.IsNullOrEmpty(SmtpPassword) ? null : SecretProtector.Protect(SmtpPassword),
                FromAddress = string.IsNullOrWhiteSpace(FromAddress) ? null : FromAddress.Trim(),
                ToAddress = string.IsNullOrWhiteSpace(ToAddress) ? null : ToAddress.Trim(),
            },
        });
    }
}
