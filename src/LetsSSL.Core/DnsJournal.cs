using System.Text.Json;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Dns;

/// <summary>
/// A DNS-01 challenge TXT record this application created, recorded durably
/// <em>before</em> the record is published so it survives a crash, a service
/// restart, or a failed cleanup. An entry that outlives its issuance is an
/// orphaned record still sitting in the zone.
/// </summary>
public class DnsJournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Which provider created it (Cloudflare, Route53, Manual).</summary>
    public DnsProviderType Provider { get; set; }

    /// <summary>The managed certificate this record was published for (for credential lookup on retry).</summary>
    public string? CertificateId { get; set; }

    /// <summary>The domain being proven, e.g. "*.example.com".</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The TXT record name, e.g. "_acme-challenge.example.com".</summary>
    public string RecordName { get; set; } = string.Empty;

    /// <summary>
    /// The TXT value. Safe to persist and log: DNS TXT records are world-readable
    /// by design — that is the whole point of the DNS-01 mechanism.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Provider-specific handle needed to delete the record again. Cloudflare
    /// stores "zoneId/recordId"; Route 53 stores the hosted zone id. Empty for
    /// Manual, where removal is a human action.
    /// </summary>
    public string? ProviderRef { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when a cleanup attempt failed, so the reason is visible.</summary>
    public string? LastCleanupError { get; set; }
}

/// <summary>
/// Durable record of the DNS-01 TXT records this application has created but not
/// yet confirmed removed. Written before publishing and cleared on a confirmed
/// delete, so anything left behind is an orphan that can be listed and retried.
/// </summary>
public class DnsRecordJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    public DnsRecordJournal(AppPaths paths, ILogger? logger = null)
    {
        _paths = paths;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public IReadOnlyList<DnsJournalEntry> GetAll()
    {
        lock (_gate) return ReadUnlocked();
    }

    /// <summary>Records a TXT record before it is published.</summary>
    public void Add(DnsJournalEntry entry)
    {
        lock (_gate)
        {
            var all = ReadUnlocked();
            all.Add(entry);
            WriteUnlocked(all);
        }
    }

    /// <summary>Updates the provider handle once the record actually exists.</summary>
    public void SetProviderRef(string id, string providerRef)
    {
        lock (_gate)
        {
            var all = ReadUnlocked();
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            entry.ProviderRef = providerRef;
            WriteUnlocked(all);
        }
    }

    /// <summary>Marks a cleanup attempt as failed, keeping the entry for retry.</summary>
    public void SetCleanupError(string id, string? error)
    {
        lock (_gate)
        {
            var all = ReadUnlocked();
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            entry.LastCleanupError = error;
            WriteUnlocked(all);
        }
    }

    /// <summary>Removes an entry after its record is confirmed deleted.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var all = ReadUnlocked();
            if (all.RemoveAll(e => e.Id == id) > 0) WriteUnlocked(all);
        }
    }

    /// <summary>
    /// Reads the journal. A malformed file is <em>preserved</em> alongside as
    /// ".corrupt-{timestamp}" and reported, rather than being silently treated as
    /// "no outstanding records" — quietly discarding it would hide exactly the
    /// orphaned records this journal exists to make visible.
    /// </summary>
    private List<DnsJournalEntry> ReadUnlocked()
    {
        var path = _paths.DnsJournalFile;
        if (!File.Exists(path)) return new List<DnsJournalEntry>();

        try
        {
            return JsonSerializer.Deserialize<List<DnsJournalEntry>>(File.ReadAllText(path), JsonOptions)
                   ?? new List<DnsJournalEntry>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var quarantine = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(path, quarantine);
                _logger.LogError(ex,
                    "The DNS record journal at {Path} could not be read and was preserved as {Quarantine}. " +
                    "Outstanding DNS-01 records recorded in it are no longer listed — inspect that file if " +
                    "you need to find records still published in your DNS zone.",
                    path, quarantine);
            }
            catch (Exception moveEx)
            {
                _logger.LogError(moveEx,
                    "The DNS record journal at {Path} could not be read or preserved. It is being treated as empty.",
                    path);
            }
            return new List<DnsJournalEntry>();
        }
    }

    private void WriteUnlocked(List<DnsJournalEntry> entries) =>
        JsonFile.Write(_paths.DnsJournalFile, entries);
}

/// <summary>
/// What a DNS provider needs to journal the records it creates: the journal
/// itself plus the certificate the records belong to. Passing null to a provider
/// disables journaling (used by credential tests and unit tests).
/// </summary>
public sealed record DnsJournalContext(DnsRecordJournal Journal, string? CertificateId = null)
{
    /// <summary>Builds an unsaved entry for a record about to be published.</summary>
    public DnsJournalEntry NewEntry(DnsProviderType provider, string domain, string recordName, string value) => new()
    {
        Provider = provider,
        CertificateId = CertificateId,
        Domain = domain,
        RecordName = recordName,
        Value = value,
    };
}

/// <summary>Outcome of retrying cleanup for one journaled record.</summary>
public sealed record DnsCleanupOutcome(DnsJournalEntry Entry, bool Succeeded, string? Error);

/// <summary>
/// Retries deletion of orphaned DNS-01 TXT records left in the journal — records
/// whose cleanup failed, or that were stranded when the process stopped between
/// publishing and cleanup. Credentials come from the managed certificate the
/// record was created for.
/// </summary>
public class DnsCleanupService
{
    private readonly DnsRecordJournal _journal;
    private readonly Func<string, ManagedCertificateCredentials?> _credentialLookup;
    private readonly ILogger<DnsCleanupService> _logger;

    /// <param name="credentialLookup">
    /// Maps a certificate id to the provider credentials needed to delete its
    /// records. Returns null when the certificate (or its credential) is gone.
    /// </param>
    public DnsCleanupService(
        DnsRecordJournal journal,
        Func<string, ManagedCertificateCredentials?> credentialLookup,
        ILogger<DnsCleanupService>? logger = null)
    {
        _journal = journal;
        _credentialLookup = credentialLookup;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DnsCleanupService>.Instance;
    }

    /// <summary>Records still outstanding, newest first.</summary>
    public IReadOnlyList<DnsJournalEntry> GetOrphans() =>
        _journal.GetAll().OrderByDescending(e => e.CreatedUtc).ToList();

    /// <summary>
    /// Attempts to delete every outstanding record. Manual-provider entries can't
    /// be deleted automatically and are reported so a human can remove them.
    /// Never throws; each record's outcome is returned.
    /// </summary>
    public async Task<IReadOnlyList<DnsCleanupOutcome>> RetryAllAsync(CancellationToken ct = default)
    {
        var outcomes = new List<DnsCleanupOutcome>();

        foreach (var entry in _journal.GetAll())
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Provider == DnsProviderType.Manual)
            {
                const string manual = "Created manually — remove this TXT record in your DNS provider, then dismiss it.";
                _journal.SetCleanupError(entry.Id, manual);
                outcomes.Add(new DnsCleanupOutcome(entry, false, manual));
                continue;
            }

            try
            {
                var creds = _credentialLookup(entry.CertificateId ?? string.Empty)
                    ?? throw new InvalidOperationException(
                        "The certificate this record belongs to no longer has stored credentials, so it can't be removed automatically.");

                await DeleteAsync(entry, creds, ct);
                _journal.Remove(entry.Id);
                _logger.LogInformation("Removed orphaned DNS TXT record {Record} ({Provider}).",
                    entry.RecordName, entry.Provider);
                outcomes.Add(new DnsCleanupOutcome(entry, true, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _journal.SetCleanupError(entry.Id, ex.Message);
                _logger.LogWarning("Could not remove orphaned DNS TXT record {Record} ({Provider}): {Error}",
                    entry.RecordName, entry.Provider, ex.Message);
                outcomes.Add(new DnsCleanupOutcome(entry, false, ex.Message));
            }
        }

        return outcomes;
    }

    /// <summary>Forgets an entry without deleting anything (for records removed by hand).</summary>
    public void Dismiss(string entryId) => _journal.Remove(entryId);

    private static async Task DeleteAsync(DnsJournalEntry entry, ManagedCertificateCredentials creds, CancellationToken ct)
    {
        switch (entry.Provider)
        {
            case DnsProviderType.Cloudflare:
            {
                var parts = (entry.ProviderRef ?? string.Empty).Split('/', 2);
                if (parts.Length != 2)
                    throw new InvalidOperationException("The record was never confirmed created, so there is no Cloudflare record id to delete.");
                using var cf = new CloudflareDnsProvider(creds.Secret);
                await cf.DeleteRecordAsync(parts[0], parts[1], ct);
                break;
            }
            case DnsProviderType.Route53:
            {
                var r53creds = creds.Route53
                    ?? throw new InvalidOperationException("Route 53 credentials could not be read.");
                using var r53 = new Route53DnsProvider(r53creds.AccessKeyId, r53creds.SecretAccessKey, r53creds.HostedZoneId);
                await r53.DeleteRecordAsync(entry.ProviderRef, entry.RecordName, entry.Value, ct);
                break;
            }
            default:
                throw new NotSupportedException($"Cleanup is not supported for {entry.Provider}.");
        }
    }
}

/// <summary>
/// The DNS credentials associated with a managed certificate, resolved and
/// unprotected by the caller so <see cref="DnsCleanupService"/> stays free of
/// storage and DPAPI concerns.
/// </summary>
public sealed record ManagedCertificateCredentials(string Secret, Route53Credentials? Route53 = null);
