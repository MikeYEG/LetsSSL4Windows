using System.Text.Json.Serialization;
using Certes.Acme;

namespace LetsSSL.Core.Models;

/// <summary>The ACME directory to issue certificates against.</summary>
public enum AcmeEnvironment
{
    /// <summary>Let's Encrypt staging. Untrusted certs, very high rate limits. Use for testing.</summary>
    Staging = 0,
    /// <summary>Let's Encrypt production. Real, browser-trusted certificates.</summary>
    Production = 1,
}

public static class AcmeEnvironmentExtensions
{
    /// <summary>Maps the environment to the Let's Encrypt ACME directory URI.</summary>
    public static Uri DirectoryUri(this AcmeEnvironment env) => env switch
    {
        AcmeEnvironment.Production => WellKnownServers.LetsEncryptV2,
        _ => WellKnownServers.LetsEncryptStagingV2,
    };
}

/// <summary>The visual theme of the desktop app.</summary>
public enum AppTheme { Dark = 0, Light = 1 }

/// <summary>Lifecycle state of a managed certificate, derived from its dates and last result.</summary>
public enum CertificateStatus
{
    NotRequested = 0,
    Valid = 1,
    ExpiringSoon = 2,
    Expired = 3,
    Error = 4,
}

/// <summary>How domain control is proven.</summary>
public enum ChallengeType
{
    /// <summary>HTTP-01: serve a token file under /.well-known/acme-challenge/.</summary>
    Http01 = 0,
    /// <summary>DNS-01: publish a TXT record (required for wildcards).</summary>
    Dns01 = 1,
}

/// <summary>Which DNS provider is used to publish DNS-01 TXT records.</summary>
public enum DnsProviderType
{
    /// <summary>The user creates/removes the TXT record by hand (interactive only).</summary>
    Manual = 0,
    /// <summary>Cloudflare DNS via API token.</summary>
    Cloudflare = 1,
    /// <summary>Amazon Route 53 via access key + secret (AWS SigV4).</summary>
    Route53 = 2,
}

/// <summary>The kind of action run after a certificate is issued.</summary>
public enum DeploymentTaskType
{
    ExportPfx = 0,
    ExportPem = 1,
    RunScript = 2,
}

/// <summary>A configured post-issuance deployment task.</summary>
public class DeploymentTaskConfig
{
    public DeploymentTaskType Type { get; set; }

    /// <summary>Task parameters. Common keys: "Path", "Password", "Arguments".</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Settings.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// A remote Windows/IIS server the certificate is distributed to on every
/// issuance and renewal. The renewing instance connects over WinRM/PowerShell
/// Remoting (authenticated as its own domain service account — Kerberos, no
/// stored credentials), imports the PFX into the remote LocalMachine\My store,
/// and binds it to the listed IIS sites. This machine remains the single source
/// of truth; the target stores no certificate configuration of its own.
/// </summary>
public class RemoteIisTarget
{
    /// <summary>Remote host name (must be resolvable and WinRM-reachable).</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>WinRM port. 5986 for HTTPS (default), 5985 for HTTP.</summary>
    public int WinRmPort { get; set; } = 5986;
    /// <summary>Use the WinRM HTTPS listener (recommended).</summary>
    public bool UseSsl { get; set; } = true;
    /// <summary>IIS sites on the remote server to bind the certificate to.</summary>
    public List<string> SiteNames { get; set; } = new();

    // ---- Per-target deployment state (recorded on the renewing instance) ----
    public DateTimeOffset? LastDeployed { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Email/webhook notification configuration (stored inside AppSettings).</summary>
public class NotificationSettings
{
    public bool NotifyOnSuccess { get; set; } = false;
    public bool NotifyOnFailure { get; set; } = true;

    // Webhook channel
    public string? WebhookUrl { get; set; }

    // Email (SMTP) channel
    public bool EmailEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    /// <summary>DPAPI-protected SMTP password (see SecretProtector).</summary>
    public string? SmtpPasswordProtected { get; set; }
    public string? FromAddress { get; set; }
    public string? ToAddress { get; set; }
}

/// <summary>Summary of the most recent renewal run (by the service or the GUI).</summary>
public class RenewalStatus
{
    public DateTimeOffset? LastRunUtc { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
}

/// <summary>Global application settings, persisted to appsettings.json in ProgramData.</summary>
public class AppSettings
{
    public AcmeEnvironment Environment { get; set; } = AcmeEnvironment.Production;
    public string ContactEmail { get; set; } = string.Empty;
    /// <summary>Visual theme of the desktop app (dark by default).</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool EnableAutoRenewal { get; set; } = true;
    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>
/// A certificate the application manages: the request configuration plus the
/// current state of the most recently issued certificate. Persisted to JSON.
/// </summary>
public class ManagedCertificate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string PrimaryDomain { get; set; } = string.Empty;
    public List<string> SubjectAlternativeNames { get; set; } = new();
    public string ContactEmail { get; set; } = string.Empty;
    public ChallengeType ChallengeType { get; set; } = ChallengeType.Http01;

    // ---- DNS-01 settings (used when ChallengeType is Dns01, required for wildcards) ----
    public DnsProviderType DnsProvider { get; set; } = DnsProviderType.Manual;
    /// <summary>DPAPI-protected provider credential (e.g. a Cloudflare API token).</summary>
    public string? DnsCredentialProtected { get; set; }

    /// <summary>Post-issuance deployment tasks, run in order after install/bind.</summary>
    public List<DeploymentTaskConfig> DeploymentTasks { get; set; } = new();

    /// <summary>
    /// Primary IIS site (used to auto-detect the web root and kept for backward
    /// compatibility). When binding, every site in <see cref="IisSiteNames"/> is
    /// used; if that list is empty this single name is used.
    /// </summary>
    public string? IisSiteName { get; set; }
    /// <summary>All IIS sites the certificate should be bound to.</summary>
    public List<string> IisSiteNames { get; set; } = new();
    /// <summary>
    /// Remote Windows/IIS servers this certificate is distributed to on every
    /// issuance and renewal (over WinRM). Empty for a local-only certificate.
    /// </summary>
    public List<RemoteIisTarget> RemoteTargets { get; set; } = new();
    /// <summary>
    /// Optional friendly name applied to the certificate in the Windows store, so
    /// it appears with a recognisable label in IIS's Server Certificates list.
    /// When blank, no friendly name is set (IIS falls back to the subject/issuer).
    /// Applied on every issuance and renewal.
    /// </summary>
    public string? FriendlyName { get; set; }
    public string? WebRootPath { get; set; }
    public bool BindToIis { get; set; } = true;
    public bool AutoRenew { get; set; } = true;
    public int RenewalDaysBeforeExpiry { get; set; } = 30;

    // ---- State of the last issued certificate ----
    public string? Thumbprint { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
    public DateTimeOffset? LastRenewed { get; set; }
    public string? LastError { get; set; }
    public string? PfxPath { get; set; }

    // ---- ACME Renewal Information (ARI, RFC 9773) ----
    /// <summary>
    /// A renewal time chosen within the CA's ARI suggested window — a stable,
    /// randomly selected point in [start, end], so renewals spread out instead
    /// of all firing at the window's start. When set and reached, the
    /// certificate is due for renewal even before the fixed days-before-expiry
    /// threshold, which lets the CA pull renewal earlier (e.g. ahead of a
    /// revocation). Refreshed on each renewal cycle; null until first fetched.
    /// </summary>
    public DateTimeOffset? AriRenewalTime { get; set; }
    /// <summary>When the ARI window was last fetched from the CA.</summary>
    public DateTimeOffset? AriFetchedAt { get; set; }
    /// <summary>Optional CA-provided URL explaining why early renewal is advised.</summary>
    public string? AriExplanationUrl { get; set; }

    /// <summary>Primary domain plus SANs, de-duplicated, primary first.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllDomains
    {
        get
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(PrimaryDomain)) list.Add(PrimaryDomain.Trim());
            foreach (var san in SubjectAlternativeNames)
            {
                var s = san?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase))
                    list.Add(s);
            }
            return list;
        }
    }

    /// <summary>Computes the current status relative to <paramref name="now"/>.</summary>
    public CertificateStatus GetStatus(DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(LastError) && NotAfter is null) return CertificateStatus.Error;
        if (NotAfter is null) return CertificateStatus.NotRequested;
        if (now >= NotAfter.Value) return CertificateStatus.Expired;
        if (now >= NotAfter.Value.AddDays(-RenewalDaysBeforeExpiry)) return CertificateStatus.ExpiringSoon;
        return CertificateStatus.Valid;
    }

    /// <summary>True if the certificate is due for (re)issue at <paramref name="now"/>.</summary>
    public bool IsDueForRenewal(DateTimeOffset now)
    {
        if (!AutoRenew) return false;
        // ARI can advance renewal ahead of the fixed threshold: if the CA has
        // suggested a renewal time that has now arrived (and the certificate is
        // actually issued), it's due — even if the days-before-expiry window
        // hasn't opened yet. ARI only pulls renewal earlier, never delays it.
        if (AriRenewalTime is { } ari && NotAfter is not null && now >= ari) return true;
        var status = GetStatus(now);
        return status is CertificateStatus.NotRequested or CertificateStatus.ExpiringSoon
            or CertificateStatus.Expired or CertificateStatus.Error;
    }
}
