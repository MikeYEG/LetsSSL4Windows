using System.Runtime.Versioning;
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
    private readonly ILogger<RenewalService> _logger;

    public RenewalService(
        CertificateRepository repository,
        CertificateManager manager,
        RenewalStatusStore? statusStore = null,
        ILogger<RenewalService>? logger = null)
    {
        _repository = repository;
        _manager = manager;
        _statusStore = statusStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RenewalService>.Instance;
    }

    public IReadOnlyList<ManagedCertificate> GetDue(DateTimeOffset now) =>
        _repository.GetAll().Where(c => c.IsDueForRenewal(now)).ToList();

    /// <summary>Renews every certificate currently due. Never throws; collects per-cert outcomes.</summary>
    public async Task<IReadOnlyList<RenewalOutcome>> RenewDueAsync(
        AcmeEnvironment environment, IProgress<string>? progress = null, CancellationToken ct = default)
    {
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
