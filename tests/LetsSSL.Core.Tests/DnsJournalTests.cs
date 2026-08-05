using LetsSSL.Core.Dns;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;
using Xunit;

namespace LetsSSL.Core.Tests;

public class DnsJournalTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly DnsRecordJournal _journal;

    public DnsJournalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "letsssl-dnsjournal-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _journal = new DnsRecordJournal(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static DnsJournalEntry NewEntry(string record = "_acme-challenge.example.com") => new()
    {
        Provider = DnsProviderType.Cloudflare,
        CertificateId = "cert1",
        Domain = "example.com",
        RecordName = record,
        Value = "token-value",
    };

    [Fact]
    public void Journal_starts_empty()
    {
        Assert.Empty(_journal.GetAll());
    }

    [Fact]
    public void Added_entries_persist_across_instances()
    {
        _journal.Add(NewEntry());

        // A fresh instance reads from disk — this is the crash-survival property.
        var reloaded = new DnsRecordJournal(_paths).GetAll();

        var entry = Assert.Single(reloaded);
        Assert.Equal("_acme-challenge.example.com", entry.RecordName);
        Assert.Equal("token-value", entry.Value);
        Assert.Equal(DnsProviderType.Cloudflare, entry.Provider);
        Assert.Equal("cert1", entry.CertificateId);
    }

    [Fact]
    public void Remove_deletes_only_the_matching_entry()
    {
        var a = NewEntry("_acme-challenge.a.example.com");
        var b = NewEntry("_acme-challenge.b.example.com");
        _journal.Add(a);
        _journal.Add(b);

        _journal.Remove(a.Id);

        var remaining = Assert.Single(_journal.GetAll());
        Assert.Equal(b.Id, remaining.Id);
    }

    [Fact]
    public void Remove_is_a_no_op_for_an_unknown_id()
    {
        _journal.Add(NewEntry());
        _journal.Remove("does-not-exist");
        Assert.Single(_journal.GetAll());
    }

    [Fact]
    public void SetProviderRef_records_the_deletion_handle()
    {
        var entry = NewEntry();
        _journal.Add(entry);

        _journal.SetProviderRef(entry.Id, "zone123/record456");

        Assert.Equal("zone123/record456", Assert.Single(_journal.GetAll()).ProviderRef);
    }

    [Fact]
    public void SetCleanupError_keeps_the_entry_and_records_why()
    {
        var entry = NewEntry();
        _journal.Add(entry);

        _journal.SetCleanupError(entry.Id, "403 Forbidden");

        var stored = Assert.Single(_journal.GetAll());
        Assert.Equal("403 Forbidden", stored.LastCleanupError);
    }

    [Fact]
    public void Entries_round_trip_through_json_with_all_fields()
    {
        var entry = new DnsJournalEntry
        {
            Provider = DnsProviderType.Route53,
            CertificateId = "abc",
            Domain = "*.example.com",
            RecordName = "_acme-challenge.example.com",
            Value = "v",
            ProviderRef = "Z123",
            LastCleanupError = "boom",
        };
        _journal.Add(entry);

        var stored = Assert.Single(new DnsRecordJournal(_paths).GetAll());
        Assert.Equal(DnsProviderType.Route53, stored.Provider);
        Assert.Equal("*.example.com", stored.Domain);
        Assert.Equal("Z123", stored.ProviderRef);
        Assert.Equal("boom", stored.LastCleanupError);
        Assert.Equal(entry.Id, stored.Id);
    }

    [Fact]
    public async Task Manual_entries_are_reported_rather_than_deleted()
    {
        var entry = NewEntry();
        entry.Provider = DnsProviderType.Manual;
        _journal.Add(entry);

        var cleanup = new DnsCleanupService(_journal, _ => null);
        var outcomes = await cleanup.RetryAllAsync();

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Contains("manually", outcome.Error, StringComparison.OrdinalIgnoreCase);
        // Still listed — a human has to remove it, so it must not silently vanish.
        Assert.Single(_journal.GetAll());
    }

    [Fact]
    public async Task Missing_credentials_are_reported_and_the_entry_is_kept()
    {
        _journal.Add(NewEntry());

        var cleanup = new DnsCleanupService(_journal, _ => null);   // credentials gone
        var outcomes = await cleanup.RetryAllAsync();

        Assert.False(Assert.Single(outcomes).Succeeded);
        var kept = Assert.Single(_journal.GetAll());
        Assert.False(string.IsNullOrEmpty(kept.LastCleanupError));
    }

    [Fact]
    public void Dismiss_forgets_a_record_removed_by_hand()
    {
        var entry = NewEntry();
        _journal.Add(entry);

        new DnsCleanupService(_journal, _ => null).Dismiss(entry.Id);

        Assert.Empty(_journal.GetAll());
    }

    [Fact]
    public void GetOrphans_returns_newest_first()
    {
        var old = NewEntry("_acme-challenge.old.example.com");
        old.CreatedUtc = DateTimeOffset.UtcNow.AddDays(-3);
        var recent = NewEntry("_acme-challenge.new.example.com");
        _journal.Add(old);
        _journal.Add(recent);

        var orphans = new DnsCleanupService(_journal, _ => null).GetOrphans();

        Assert.Equal("_acme-challenge.new.example.com", orphans[0].RecordName);
        Assert.Equal("_acme-challenge.old.example.com", orphans[1].RecordName);
    }
}
