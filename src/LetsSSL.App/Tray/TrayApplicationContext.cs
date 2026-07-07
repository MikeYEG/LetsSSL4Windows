using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Windows.Forms;
using LetsSSL.Core;
using Microsoft.Win32;

namespace LetsSSL.App.Tray;

/// <summary>
/// The system-tray companion (LetsSSL4Windows.exe --tray): shows certificate
/// status, opens the dashboard, triggers a renewal, controls the renewal service,
/// and toggles start-at-login. Runs un-elevated; admin actions surface a message.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "LetsSSL4WindowsRenewalService";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LetsSSL4WindowsTray";

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly LetsSslServices _services;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _serviceItem;
    private readonly ToolStripMenuItem _runAtLoginItem;

    public TrayApplicationContext()
    {
        _services = new LetsSslServices();

        _statusItem = new ToolStripMenuItem("Loading…") { Enabled = false };
        var openItem = new ToolStripMenuItem("Open LetsSSL4Windows", null, (_, _) => OpenApp());
        var renewItem = new ToolStripMenuItem("Renew all due now", null, async (_, _) => await RenewNowAsync());
        _serviceItem = new ToolStripMenuItem("Service", null, (_, _) => ToggleService());
        _runAtLoginItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleRunAtLogin());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, new ToolStripSeparator(), openItem, renewItem,
            new ToolStripSeparator(), _serviceItem, _runAtLoginItem,
            new ToolStripSeparator(), exitItem,
        });
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon() ?? System.Drawing.SystemIcons.Shield,
            Text = "LetsSSL4Windows",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenApp();

        _timer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(30).TotalMilliseconds };
        _timer.Tick += (_, _) => RefreshMenu();
        _timer.Start();

        RefreshMenu();
    }

    // The tray runs as a WinForms app (no WPF Application), so load the icon from
    // the exe's own embedded Win32 icon rather than a pack resource.
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            return exe is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch { return null; }
    }

    private void RefreshMenu()
    {
        try
        {
            var certs = _services.Certificates.GetAll();
            if (certs.Count == 0)
            {
                _statusItem.Text = "No certificates managed";
                _icon.Text = "LetsSSL4Windows — no certificates";
            }
            else
            {
                var soonest = certs.Where(c => c.NotAfter is not null).OrderBy(c => c.NotAfter).FirstOrDefault();
                var days = soonest?.NotAfter is { } na ? (na - DateTimeOffset.UtcNow).Days : (int?)null;
                var summary = days is null
                    ? $"{certs.Count} certificate(s)"
                    : $"{certs.Count} certificate(s) · next expiry in {days} day(s)";
                _statusItem.Text = summary;
                _icon.Text = Truncate($"LetsSSL4Windows — {summary}", 63);
            }
        }
        catch { _statusItem.Text = "Status unavailable"; }

        var status = GetServiceStatus();
        _serviceItem.Text = status switch
        {
            ServiceControllerStatus.Running => "Stop renewal service (running)",
            null => "Renewal service not installed",
            _ => "Start renewal service (stopped)",
        };
        _serviceItem.Enabled = status is not null;
        _runAtLoginItem.Checked = IsRunAtLoginEnabled();
    }

    private void OpenApp()
    {
        try
        {
            // Launch the same executable with no arguments -> GUI mode (self-elevates).
            var exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "LetsSSL4Windows.exe");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not open LetsSSL4Windows", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task RenewNowAsync()
    {
        ShowBalloon("LetsSSL4Windows", "Checking certificates for renewal…", ToolTipIcon.Info);
        try
        {
            var settings = _services.Settings.Load();
            var outcomes = await Task.Run(() => _services.Renewal.RenewDueAsync(settings.Environment));
            var failed = outcomes.Count(o => !o.Succeeded);
            if (outcomes.Count == 0)
                ShowBalloon("LetsSSL4Windows", "No certificates were due for renewal.", ToolTipIcon.Info);
            else
                ShowBalloon("LetsSSL4Windows",
                    $"Renewal complete: {outcomes.Count - failed} succeeded, {failed} failed.",
                    failed == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowBalloon("Renewal failed", ex.Message, ToolTipIcon.Error);
        }
        RefreshMenu();
    }

    private void ToggleService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                ShowBalloon("LetsSSL4Windows", "Renewal service stopped.", ToolTipIcon.Info);
            }
            else
            {
                sc.Start();
                ShowBalloon("LetsSSL4Windows", "Renewal service started.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Service control failed",
                ex.Message + "\n(Starting/stopping a service may require administrator rights.)",
                ToolTipIcon.Error);
        }
        RefreshMenu();
    }

    private static ServiceControllerStatus? GetServiceStatus()
    {
        try { using var sc = new ServiceController(ServiceName); return sc.Status; }
        catch { return null; }
    }

    private void ToggleRunAtLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (IsRunAtLoginEnabled())
                key!.DeleteValue(RunValueName, throwOnMissingValue: false);
            else
                key!.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --tray");
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not update startup setting", ex.Message, ToolTipIcon.Error);
        }
        RefreshMenu();
    }

    private static bool IsRunAtLoginEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch { return false; }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(5000, title, text, icon);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void ExitApp()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
