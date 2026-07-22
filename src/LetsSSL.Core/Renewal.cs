using System.Runtime.Versioning;
using LetsSSL.Core.Acme;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Renewal;

/// <summary>Result of a single certificate's renewal attempt.</summary>
public record RenewalOutcome(ManagedCertificate Certificate, bool Succeeded, string? Error);

/// <summary>
/// Finds certificates due for renewal and renews them via <see cref="CertificateManager"/>.
/// Used by the GUI ("Renew All") and the unattended renewal service. Records a
/// summary of each run so the dashboard can show when renewal last ran.
/// </summary>
[SupportedOSPlatform("windows")]
public class RenewalService
{
    private readonly CertificateRepository _repository;
    private readonly CertificateManager _manager;
    private readonly RenewalStatusStore? _statusStore;
    private readonly WindowsCertificateStore? _store;
    private readonly RenewalInfoClient? _ariClient;
    private readonly ILogger<RenewalService> _logger;

    public RenewalService(
        CertificateRepository repository,
        CertificateManager manager,
        RenewalStatusStore? statusStore = null,
        WindowsCertificateStore? store = null,
        RenewalInfoClient? ariClient = null,
        ILogger<RenewalService>? logger = null)
    {
        _repository = repository;
        _manager = manager;
        _statusStore = statusStore;
        _store = store;
        _ariClient = ariClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RenewalService>.Instance;
    }

    public IReadOnlyList<ManagedCertificate> GetDue(DateTimeOffset now) =>
        _repository.GetAll().Where(c => c.IsDueForRenewal(now)).ToList();

    /// <summary>
    /// Refreshes each issued certificate's ACME Renewal Information (ARI) from
    /// the CA, so a CA-suggested early-renewal window is honored on the next
    /// due-check. Best-effort: a certificate that isn't installed, isn't
    /// ARI-eligible, or whose CA doesn't support ARI is simply left on its
    /// date-based schedule. Never throws (except on cancellation).
    /// </summary>
    public async Task RefreshRenewalInfoAsync(AcmeEnvironment environment, CancellationToken ct = default)
    {
        if (_store is null || _ariClient is null) return;

        var directory = environment.DirectoryUri();
        foreach (var cert in _repository.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(cert.Thumbprint) || cert.NotAfter is null) continue;

            using var x509 = _store.FindByThumbprint(cert.Thumbprint);
            if (x509 is null) continue;

            var info = await _ariClient.TryGetAsync(directory, x509, ct);
            if (info is null) continue;

            // Keep the previously chosen time if it still falls inside the
            // returned window, so the renewal moment stays stable across polls;
            // otherwise pick a fresh random point within the new window.
            if (cert.AriRenewalTime is not { } prev || prev < info.WindowStart || prev > info.WindowEnd)
                cert.AriRenewalTime = PickWithinWindow(info.WindowStart, info.WindowEnd);

            cert.AriFetchedAt = DateTimeOffset.UtcNow;
            cert.AriExplanationUrl = info.ExplanationUrl;
            _repository.Upsert(cert);

            _logger.LogInformation(
                "ARI for {Domain}: renew around {Time:u} (CA window {Start:u} – {End:u}).",
                cert.PrimaryDomain, cert.AriRenewalTime, info.WindowStart, info.WindowEnd);
        }
    }

    /// <summary>Picks a stable random instant within [start, end] to spread renewals.</summary>
    private static DateTimeOffset PickWithinWindow(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return start;
        return start + (end - start) * Random.Shared.NextDouble();
    }

    /// <summary>Renews every certificate currently due. Never throws; collects per-cert outcomes.</summary>
    public async Task<IReadOnlyList<RenewalOutcome>> RenewDueAsync(
        AcmeEnvironment environment, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Pull the CA's latest renewal advice first, so an ARI-advanced window is
        // reflected in what's considered due. Advisory only — never blocks a run.
        try
        {
            await RefreshRenewalInfoAsync(environment, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh renewal information; using the date-based schedule.");
        }

        var due = GetDue(DateTimeOffset.UtcNow);
        var outcomes = new List<RenewalOutcome>();

        if (due.Count == 0)
        {
            progress?.Report("No certificates are due for renewal.");
        }
        else
        {
            foreach (var cert in due)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Renewing {cert.PrimaryDomain}…");
                try
                {
                    await _manager.RequestAndDeployAsync(cert, environment, progress, ct);
                    outcomes.Add(new RenewalOutcome(cert, true, null));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Renewal failed for {Domain}.", cert.PrimaryDomain);
                    outcomes.Add(new RenewalOutcome(cert, false, ex.Message));
                }
            }
        }

        TryRecordRun(outcomes);
        return outcomes;
    }

    private void TryRecordRun(IReadOnlyList<RenewalOutcome> outcomes)
    {
        try
        {
            _statusStore?.Save(new RenewalStatus
            {
                LastRunUtc = DateTimeOffset.UtcNow,
                Succeeded = outcomes.Count(o => o.Succeeded),
                Failed = outcomes.Count(o => !o.Succeeded),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the renewal run summary.");
        }
    }
}
