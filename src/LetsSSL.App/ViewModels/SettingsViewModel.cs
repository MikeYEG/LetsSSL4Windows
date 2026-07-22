using System.Reflection;
using System.Windows;
using LetsSSL.App.Services;
using LetsSSL.Core;
using LetsSSL.Core.Models;
using LetsSSL.Core.Notifications;
using LetsSSL.Core.Storage;
using LetsSSL.Core.Updates;

namespace LetsSSL.App.ViewModels;

/// <summary>Backs the Settings dialog (environment, renewal, notifications, updates).</summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsRepository _repository;
    private readonly UpdateChecker _updateChecker;
    private readonly AppPaths _paths;
    private UpdateInfo? _updateInfo;

    public SettingsViewModel(SettingsRepository repository, UpdateChecker updateChecker, AppPaths paths)
    {
        _repository = repository;
        _updateChecker = updateChecker;
        _paths = paths;
        CheckForUpdatesCommand = new AsyncRelayCommand(_ => CheckForUpdatesAsync());
        DownloadInstallCommand = new AsyncRelayCommand(_ => DownloadInstallAsync(), _ => UpdateFound);
        TestNotificationsCommand = new AsyncRelayCommand(_ => TestNotificationsAsync());

        LoadFromSettings();
    }

    /// <summary>Loads every field from the persisted settings (also used after a restore).</summary>
    private void LoadFromSettings()
    {
        var s = _repository.Load();
        IsProduction = s.Environment == AcmeEnvironment.Production;
        ContactEmail = s.ContactEmail;
        EnableAutoRenewal = s.EnableAutoRenewal;
        IsDarkMode = s.Theme == AppTheme.Dark;

        var n = s.Notifications;
        NotifyOnSuccess = n.NotifyOnSuccess;
        NotifyOnFailure = n.NotifyOnFailure;
        WebhookUrl = n.WebhookUrl ?? string.Empty;
        EmailEnabled = n.EmailEnabled;
        SmtpHost = n.SmtpHost ?? string.Empty;
        SmtpPort = n.SmtpPort;
        SmtpUseSsl = n.SmtpUseSsl;
        SmtpUsername = n.SmtpUsername ?? string.Empty;
        SmtpPassword = SecretProtector.Unprotect(n.SmtpPasswordProtected) ?? string.Empty;
        FromAddress = n.FromAddress ?? string.Empty;
        ToAddress = n.ToAddress ?? string.Empty;
    }

    // ---- General ----

    private bool _isProduction;
    public bool IsProduction { get => _isProduction; set => SetField(ref _isProduction, value); }

    private string _contactEmail = string.Empty;
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

    private string _webhookUrl = string.Empty;
    public string WebhookUrl { get => _webhookUrl; set => SetField(ref _webhookUrl, value); }

    private bool _emailEnabled;
    public bool EmailEnabled { get => _emailEnabled; set => SetField(ref _emailEnabled, value); }

    private string _smtpHost = string.Empty;
    public string SmtpHost { get => _smtpHost; set => SetField(ref _smtpHost, value); }

    private int _smtpPort;
    public int SmtpPort { get => _smtpPort; set => SetField(ref _smtpPort, value); }

    private bool _smtpUseSsl;
    public bool SmtpUseSsl { get => _smtpUseSsl; set => SetField(ref _smtpUseSsl, value); }

    private string _smtpUsername = string.Empty;
    public string SmtpUsername { get => _smtpUsername; set => SetField(ref _smtpUsername, value); }

    private string _smtpPassword = string.Empty;
    public string SmtpPassword { get => _smtpPassword; set => SetField(ref _smtpPassword, value); }

    private string _fromAddress = string.Empty;
    public string FromAddress { get => _fromAddress; set => SetField(ref _fromAddress, value); }

    private string _toAddress = string.Empty;
    public string ToAddress { get => _toAddress; set => SetField(ref _toAddress, value); }

    // ---- Test connection ----

    public AsyncRelayCommand TestNotificationsCommand { get; }

    private string _notificationTestStatus = string.Empty;
    public string NotificationTestStatus { get => _notificationTestStatus; private set => SetField(ref _notificationTestStatus, value); }

    /// <summary>True if at least one channel has enough configuration to deliver.</summary>
    private bool HasConfiguredChannel(NotificationSettings s) =>
        !string.IsNullOrWhiteSpace(s.WebhookUrl)
        || (s.EmailEnabled
            && !string.IsNullOrWhiteSpace(s.SmtpHost)
            && !string.IsNullOrWhiteSpace(s.FromAddress)
            && !string.IsNullOrWhiteSpace(s.ToAddress));

    private async Task TestNotificationsAsync()
    {
        var settings = BuildNotificationSettings();
        if (!HasConfiguredChannel(settings))
        {
            NotificationTestStatus = "Configure a webhook URL or email (SMTP host, from and to addresses) first.";
            return;
        }

        NotificationTestStatus = "Sending a test notification…";
        try
        {
            var results = await NotificationService.SendTestAsync(settings);
            NotificationTestStatus = results.Count == 0
                ? "No channels were configured to test."
                : string.Join("   ", results.Select(r =>
                    r.Success ? $"✓ {r.Channel}: sent" : $"✗ {r.Channel}: {r.Error}"));
        }
        catch (Exception ex)
        {
            NotificationTestStatus = "Test failed: " + ex.Message;
        }
    }

    /// <summary>Builds the notification settings from the current editor state.</summary>
    private NotificationSettings BuildNotificationSettings() => new()
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
    };

    // ---- Backup / restore ----

    private string _backupStatus = string.Empty;
    public string BackupStatus { get => _backupStatus; private set => SetField(ref _backupStatus, value); }

    /// <summary>Writes a backup archive of the data store to <paramref name="path"/>.</summary>
    public void Backup(string path)
    {
        try
        {
            new ConfigBackup(_paths).Create(path);
            BackupStatus = $"Backed up configuration to {path}.";
        }
        catch (Exception ex)
        {
            BackupStatus = "Backup failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Restores the data store from a backup archive, then reloads this dialog's
    /// fields from the restored settings so the visible values match (and a later
    /// Save writes the restored values back rather than the pre-restore ones).
    /// </summary>
    public void Restore(string path)
    {
        try
        {
            var count = new ConfigBackup(_paths).Restore(path);
            LoadFromSettings();
            BackupStatus = $"Restored {count} file(s). Reopen the certificate list to see the restored certificates.";
        }
        catch (Exception ex)
        {
            BackupStatus = "Restore failed: " + ex.Message;
        }
    }

    public void Save()
    {
        _repository.Save(new AppSettings
        {
            Environment = IsProduction ? AcmeEnvironment.Production : AcmeEnvironment.Staging,
            ContactEmail = ContactEmail?.Trim() ?? string.Empty,
            EnableAutoRenewal = EnableAutoRenewal,
            Theme = IsDarkMode ? AppTheme.Dark : AppTheme.Light,
            Notifications = BuildNotificationSettings(),
        });
    }
}
