using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Runtime.Versioning;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Notifications;

/// <summary>A single notification to deliver across the configured channels.</summary>
public sealed class NotificationMessage
{
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public string Domain { get; init; } = string.Empty;
    public bool IsFailure { get; init; }
    public string Status => IsFailure ? "failure" : "success";
}

/// <summary>Delivers a notification over one channel (webhook, email, …).</summary>
public interface INotifier
{
    string Name { get; }
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}

/// <summary>Per-channel outcome of a notification connection test.</summary>
public sealed record NotificationTestResult(string Channel, bool Success, string? Error);

/// <summary>POSTs a JSON payload to a webhook URL (Slack/Teams/Discord/custom).</summary>
public sealed class WebhookNotifier : INotifier, IDisposable
{
    private readonly string _url;
    private readonly HttpClient _http;

    public WebhookNotifier(string url, HttpClient? httpClient = null)
    {
        _url = url;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Name => "webhook";

    public async Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        var payload = new
        {
            text = $"[LetsSSL4Windows] {message.Subject}",
            status = message.Status,
            domain = message.Domain,
            subject = message.Subject,
            body = message.Body,
            timestamp = DateTimeOffset.UtcNow,
        };
        using var resp = await _http.PostAsJsonAsync(_url, payload, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Sends notifications by email over SMTP.</summary>
public sealed class EmailNotifier : INotifier
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _useSsl;
    private readonly string? _username;
    private readonly string? _password;
    private readonly string _from;
    private readonly string _to;

    public EmailNotifier(string host, int port, bool useSsl, string? username, string? password, string from, string to)
    {
        _host = host; _port = port; _useSsl = useSsl; _username = username; _password = password; _from = from; _to = to;
    }

    public string Name => "email";

    public async Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        using var mail = new MailMessage(_from, _to)
        {
            Subject = $"[LetsSSL4Windows] {message.Subject}",
            Body = message.Body,
            IsBodyHtml = false,
        };
        using var client = new SmtpClient(_host, _port) { EnableSsl = _useSsl };
        if (!string.IsNullOrEmpty(_username)) client.Credentials = new NetworkCredential(_username, _password);
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>
/// Builds the configured notification channels from settings and delivers
/// issuance results. Best-effort: delivery failures are logged, never thrown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotificationService
{
    private readonly SettingsRepository _settings;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(SettingsRepository settings, ILogger<NotificationService>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance;
    }

    /// <param name="warnings">
    /// Non-fatal problems that occurred even though issuance itself succeeded —
    /// e.g. a remote IIS deployment that failed. When present on a success, the
    /// result is treated as a partial failure and alerted under NotifyOnFailure.
    /// </param>
    public async Task NotifyIssuanceResultAsync(
        ManagedCertificate cert, bool success, string? error,
        IReadOnlyList<string>? warnings = null, CancellationToken ct = default)
    {
        var hasWarnings = warnings is { Count: > 0 };
        var settings = _settings.Load().Notifications;
        // A success carrying warnings is a partial failure: gate it on NotifyOnFailure.
        if (success && !hasWarnings && !settings.NotifyOnSuccess) return;
        if (success && hasWarnings && !settings.NotifyOnFailure) return;
        if (!success && !settings.NotifyOnFailure) return;

        var message = BuildMessage(cert, success, error, warnings);
        foreach (var (channel, create) in NotifierFactories(settings))
        {
            INotifier notifier;
            try
            {
                notifier = create();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not initialise the {Channel} notifier.", channel);
                continue;
            }

            try
            {
                await notifier.SendAsync(message, ct);
                _logger.LogInformation("Sent {Channel} notification for {Domain}.", channel, cert.PrimaryDomain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send {Channel} notification.", channel);
            }
            finally
            {
                (notifier as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Sends a test notification over every channel configured in
    /// <paramref name="settings"/> (regardless of the notify-on-success/failure
    /// toggles), returning a per-channel result so the UI can report which
    /// channels delivered. Never throws — delivery errors are captured per channel.
    /// </summary>
    public static async Task<IReadOnlyList<NotificationTestResult>> SendTestAsync(
        NotificationSettings settings, CancellationToken ct = default)
    {
        var message = new NotificationMessage
        {
            Domain = "test.example.com",
            IsFailure = false,
            Subject = "LetsSSL4Windows test notification",
            Body = "This is a test notification from LetsSSL4Windows. " +
                   "If you received it, your notification settings are working.",
        };

        var results = new List<NotificationTestResult>();
        foreach (var (channel, create) in NotifierFactories(settings))
        {
            INotifier notifier;
            try
            {
                notifier = create();
            }
            catch (Exception ex)
            {
                // Building the channel failed (e.g. decrypting the SMTP password);
                // record it and keep testing the other channels.
                results.Add(new NotificationTestResult(channel, false, ex.Message));
                continue;
            }

            try
            {
                await notifier.SendAsync(message, ct);
                results.Add(new NotificationTestResult(channel, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new NotificationTestResult(channel, false, ex.Message));
            }
            finally
            {
                (notifier as IDisposable)?.Dispose();
            }
        }
        return results;
    }

    private static NotificationMessage BuildMessage(ManagedCertificate cert, bool success, string? error, IReadOnlyList<string>? warnings = null)
    {
        var domains = string.Join(", ", cert.AllDomains);
        if (success && warnings is { Count: > 0 })
        {
            var detail = string.Join("\n", warnings.Select(w => $"  - {w}"));
            return new NotificationMessage
            {
                Domain = cert.PrimaryDomain,
                IsFailure = true,
                Subject = $"Certificate issued for {cert.PrimaryDomain}, but {warnings.Count} remote deployment(s) FAILED",
                Body = $"A certificate for {domains} was issued/renewed and installed locally, " +
                       $"but the following remote deployment(s) failed:\n{detail}\n\n" +
                       $"Valid until: {cert.NotAfter?.ToString("u") ?? "unknown"}\nThumbprint: {cert.Thumbprint}",
            };
        }
        if (success)
        {
            return new NotificationMessage
            {
                Domain = cert.PrimaryDomain,
                IsFailure = false,
                Subject = $"Certificate issued for {cert.PrimaryDomain}",
                Body = $"A certificate for {domains} was issued/renewed successfully.\n" +
                       $"Valid until: {cert.NotAfter?.ToString("u") ?? "unknown"}\nThumbprint: {cert.Thumbprint}",
            };
        }
        return new NotificationMessage
        {
            Domain = cert.PrimaryDomain,
            IsFailure = true,
            Subject = $"Certificate renewal FAILED for {cert.PrimaryDomain}",
            Body = $"Issuance/renewal for {domains} failed.\nError: {error ?? "unknown error"}",
        };
    }

    /// <summary>
    /// The channels a settings object enables, as deferred factories. Enumerating
    /// this never throws — construction (which may decrypt the SMTP password and
    /// can therefore fail) is deferred into the factory so each caller can guard
    /// it per channel.
    /// </summary>
    private static IEnumerable<(string Channel, Func<INotifier> Create)> NotifierFactories(NotificationSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.WebhookUrl))
            yield return ("webhook", () => new WebhookNotifier(s.WebhookUrl!));

        if (s.EmailEnabled
            && !string.IsNullOrWhiteSpace(s.SmtpHost)
            && !string.IsNullOrWhiteSpace(s.FromAddress)
            && !string.IsNullOrWhiteSpace(s.ToAddress))
        {
            yield return ("email", () => new EmailNotifier(
                s.SmtpHost!, s.SmtpPort, s.SmtpUseSsl,
                s.SmtpUsername, SecretProtector.Unprotect(s.SmtpPasswordProtected),
                s.FromAddress!, s.ToAddress!));
        }
    }
}
