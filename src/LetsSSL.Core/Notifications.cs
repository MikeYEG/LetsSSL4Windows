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
        CancellationToken ct = default, IReadOnlyList<string>? warnings = null)
    {
        var hasWarnings = warnings is { Count: > 0 };
        var settings = _settings.Load().Notifications;
        // A success carrying warnings is a partial failure: gate it on NotifyOnFailure.
        if (success && !hasWarnings && !settings.NotifyOnSuccess) return;
        if (success && hasWarnings && !settings.NotifyOnFailure) return;
        if (!success && !settings.NotifyOnFailure) return;

        var message = BuildMessage(cert, success, error, warnings);
        foreach (var notifier in BuildNotifiers(settings))
        {
            try
            {
                await notifier.SendAsync(message, ct);
                _logger.LogInformation("Sent {Channel} notification for {Domain}.", notifier.Name, cert.PrimaryDomain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send {Channel} notification.", notifier.Name);
            }
            finally
            {
                (notifier as IDisposable)?.Dispose();
            }
        }
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

    private static IEnumerable<INotifier> BuildNotifiers(NotificationSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.WebhookUrl))
            yield return new WebhookNotifier(s.WebhookUrl!);

        if (s.EmailEnabled
            && !string.IsNullOrWhiteSpace(s.SmtpHost)
            && !string.IsNullOrWhiteSpace(s.FromAddress)
            && !string.IsNullOrWhiteSpace(s.ToAddress))
        {
            yield return new EmailNotifier(
                s.SmtpHost!, s.SmtpPort, s.SmtpUseSsl,
                s.SmtpUsername, SecretProtector.Unprotect(s.SmtpPasswordProtected),
                s.FromAddress!, s.ToAddress!);
        }
    }
}
