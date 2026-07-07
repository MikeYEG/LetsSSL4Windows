using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using LetsSSL.Core.Acme;
using LetsSSL.Core.Challenges;
using LetsSSL.Core.Deployment;
using LetsSSL.Core.Dns;
using LetsSSL.Core.Iis;
using LetsSSL.Core.Models;
using LetsSSL.Core.Notifications;
using LetsSSL.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core;

/// <summary>
/// High-level orchestration used by both the GUI and the renewal agent.
/// Requests a certificate (HTTP-01 or DNS-01), installs it into the Windows
/// store, saves the PFX, optionally binds it to IIS, runs deployment tasks, and
/// persists the updated managed-certificate state.
/// </summary>
[SupportedOSPlatform("windows")]
public class CertificateManager
{
    private readonly AppPaths _paths;
    private readonly CertificateRepository _repository;
    private readonly AcmeService _acme;
    private readonly WindowsCertificateStore _store;
    private readonly DeploymentTaskRunner _deployment;
    private readonly IManualDnsInteraction? _manualDns;
    private readonly NotificationService? _notifications;
    private readonly ILogger<CertificateManager> _logger;

    public CertificateManager(
        AppPaths paths,
        CertificateRepository repository,
        AcmeService acme,
        WindowsCertificateStore store,
        DeploymentTaskRunner deployment,
        IManualDnsInteraction? manualDns = null,
        NotificationService? notifications = null,
        ILogger<CertificateManager>? logger = null)
    {
        _paths = paths;
        _repository = repository;
        _acme = acme;
        _store = store;
        _deployment = deployment;
        _manualDns = manualDns;
        _notifications = notifications;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CertificateManager>.Instance;
    }

    /// <summary>
    /// Issues (or renews) the certificate described by <paramref name="config"/>
    /// and deploys it. Updates and saves the managed-certificate record either way.
    /// </summary>
    public async Task<ManagedCertificate> RequestAndDeployAsync(
        ManagedCertificate config,
        AcmeEnvironment environment,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        IAcmeChallengeHandler? handler = null;
        try
        {
            handler = BuildChallengeHandler(config);
            var result = await _acme.RequestCertificateAsync(config, environment, handler, progress, ct);

            progress?.Report("Installing certificate into the Windows store…");
            var installed = _store.ImportPfx(result.PfxBytes, result.PfxPassword);

            // Save the PFX alongside our data so it can be re-deployed if needed.
            var pfxPath = _paths.PfxFileFor(config.Id);
            await File.WriteAllBytesAsync(pfxPath, result.PfxBytes, ct);

            config.Thumbprint = installed.Thumbprint;
            config.NotBefore = new DateTimeOffset(installed.NotBefore.ToUniversalTime());
            config.NotAfter = new DateTimeOffset(installed.NotAfter.ToUniversalTime());
            config.LastRenewed = DateTimeOffset.UtcNow;
            config.PfxPath = pfxPath;
            config.LastError = null;

            if (config.BindToIis && EffectiveSites(config).Count > 0)
            {
                progress?.Report($"Binding certificate to IIS site(s): {string.Join(", ", EffectiveSites(config))}…");
                BindToIis(config, installed);
            }

            if (config.DeploymentTasks.Count > 0)
            {
                var context = new DeploymentContext
                {
                    Certificate = config,
                    InstalledCertificate = installed,
                    PfxBytes = result.PfxBytes,
                    PfxPassword = result.PfxPassword,
                };
                await _deployment.RunAllAsync(config.DeploymentTasks, context, progress, ct);
            }

            _repository.Upsert(config);
            progress?.Report("Done.");

            if (_notifications is not null)
                await _notifications.NotifyIssuanceResultAsync(config, success: true, error: null, ct);

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Issuance failed for {Domain}.", config.PrimaryDomain);
            config.LastError = ex.Message;
            _repository.Upsert(config);
            progress?.Report($"Error: {ex.Message}");

            if (_notifications is not null)
                await _notifications.NotifyIssuanceResultAsync(config, success: false, ex.Message, CancellationToken.None);

            throw;
        }
    }

    private IAcmeChallengeHandler BuildChallengeHandler(ManagedCertificate config)
    {
        if (config.ChallengeType == ChallengeType.Dns01)
            return new DnsChallengeHandler(BuildDnsProvider(config));

        if (config.AllDomains.Any(d => d.StartsWith("*.", StringComparison.Ordinal)))
            throw new InvalidOperationException("Wildcard domains require DNS-01 validation.");

        var webRoot = ResolveWebRoot(config)
            ?? throw new InvalidOperationException(
                "Could not determine a web root for HTTP-01 validation. " +
                "Set a web root path or choose an IIS site.");
        return new HttpChallengeHandler(new FileSystemHttpChallengeResponder(webRoot));
    }

    private IDnsProvider BuildDnsProvider(ManagedCertificate config) => config.DnsProvider switch
    {
        DnsProviderType.Cloudflare => new CloudflareDnsProvider(
            SecretProtector.Unprotect(config.DnsCredentialProtected)
                ?? throw new InvalidOperationException("A Cloudflare API token is required.")),

        DnsProviderType.Manual => _manualDns is not null
            ? new ManualDnsProvider(_manualDns)
            : throw new InvalidOperationException(
                "Manual DNS validation is interactive-only and cannot run in the renewal agent. " +
                "Use an automated DNS provider (e.g. Cloudflare) for unattended renewal."),

        _ => throw new NotSupportedException($"Unknown DNS provider: {config.DnsProvider}"),
    };

    /// <summary>The IIS sites a certificate should bind to (the list, or the single legacy name).</summary>
    private static IReadOnlyList<string> EffectiveSites(ManagedCertificate config)
    {
        if (config.IisSiteNames is { Count: > 0 })
            return config.IisSiteNames;
        return string.IsNullOrWhiteSpace(config.IisSiteName)
            ? Array.Empty<string>()
            : new[] { config.IisSiteName! };
    }

    private void BindToIis(ManagedCertificate config, X509Certificate2 cert)
    {
        var iis = new IisManager();
        var hash = HexToBytes(cert.Thumbprint);
        // Bind every host name on the certificate to every selected IIS site.
        foreach (var site in EffectiveSites(config))
            foreach (var domain in config.AllDomains)
            {
                if (domain.StartsWith("*.", StringComparison.Ordinal))
                    continue; // wildcard hosts are not valid SNI binding host names
                iis.BindCertificate(site, domain, hash, _store.StoreNameForIis);
            }
    }

    /// <summary>
    /// Binds an already-issued certificate to an IIS site on demand, and remembers
    /// the site so future renewals re-bind automatically.
    /// </summary>
    public void BindToIisSite(ManagedCertificate config, string siteName)
    {
        if (string.IsNullOrEmpty(config.Thumbprint))
            throw new InvalidOperationException("This certificate hasn't been issued yet, so there's nothing to bind.");

        config.IisSiteName = siteName;
        if (!config.IisSiteNames.Contains(siteName, StringComparer.OrdinalIgnoreCase))
            config.IisSiteNames.Add(siteName);
        config.BindToIis = true;

        var iis = new IisManager();
        var hash = HexToBytes(config.Thumbprint);
        foreach (var domain in config.AllDomains)
        {
            if (domain.StartsWith("*.", StringComparison.Ordinal)) continue;
            iis.BindCertificate(siteName, domain, hash, _store.StoreNameForIis);
        }

        _repository.Upsert(config);
    }

    private string? ResolveWebRoot(ManagedCertificate config)
    {
        if (!string.IsNullOrWhiteSpace(config.WebRootPath))
            return config.WebRootPath;
        if (!string.IsNullOrWhiteSpace(config.IisSiteName))
            return new IisManager().GetSitePhysicalPath(config.IisSiteName!);
        return null;
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
