using System.Net;
using LetsSSL.Core.Dns;
using LetsSSL.Core.Storage;
using Xunit;

namespace LetsSSL.Core.Tests;

public class DnsProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    /// <summary>Replies per request based on method + path, so a publish/delete flow can be scripted.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Code, string Body)> _reply;
        public readonly List<string> Requests = new();
        public RouteHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var (code, body) = _reply(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    private const string ZonesJson =
        "{\"success\":true,\"result\":[{\"id\":\"zone1\",\"name\":\"example.com\"}]}";
    private const string CreatedJson =
        "{\"success\":true,\"result\":{\"id\":\"rec1\"}}";

    [Fact]
    public async Task Cloudflare_verify_reports_active_token_as_valid()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            "{\"success\":true,\"result\":{\"status\":\"active\"}}"));
        using var provider = new CloudflareDnsProvider("token", http);

        var result = await provider.VerifyCredentialsAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Cloudflare_verify_reports_invalid_token_as_failure()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized,
            "{\"success\":false,\"errors\":[{\"message\":\"Invalid API Token\"}]}"));
        using var provider = new CloudflareDnsProvider("bad", http);

        var result = await provider.VerifyCredentialsAsync();

        Assert.False(result.Success);
        Assert.Contains("Invalid API Token", result.Message);
    }

    [Fact]
    public async Task Cloudflare_verify_reports_inactive_token_as_failure()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            "{\"success\":true,\"result\":{\"status\":\"disabled\"}}"));
        using var provider = new CloudflareDnsProvider("token", http);

        var result = await provider.VerifyCredentialsAsync();

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Message);
    }

    // ---- Journaling (Tier 2): a created record is always traceable ----

    private static (DnsRecordJournal Journal, AppPaths Paths, string Root) NewJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), "letsssl-dnsprov-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        return (new DnsRecordJournal(paths), paths, root);
    }

    [Fact]
    public async Task Publishing_journals_the_record_and_cleanup_clears_it()
    {
        var (journal, _, root) = NewJournal();
        try
        {
            var handler = new RouteHandler(req =>
                req.Method == HttpMethod.Get ? (HttpStatusCode.OK, ZonesJson) : (HttpStatusCode.OK, CreatedJson));
            var http = new HttpClient(handler);
            using var provider = new CloudflareDnsProvider("token", http,
                new DnsJournalContext(journal, "cert1"));

            await provider.PublishTxtRecordAsync("example.com", "_acme-challenge.example.com", "val");

            var entry = Assert.Single(journal.GetAll());
            Assert.Equal("_acme-challenge.example.com", entry.RecordName);
            Assert.Equal("zone1/rec1", entry.ProviderRef);   // handle recorded for later deletion
            Assert.Equal("cert1", entry.CertificateId);

            await provider.RemoveTxtRecordsAsync();

            Assert.Empty(journal.GetAll());                   // confirmed delete clears the journal
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_cleanup_leaves_the_record_listed_with_the_reason()
    {
        var (journal, _, root) = NewJournal();
        try
        {
            var handler = new RouteHandler(req => req.Method switch
            {
                _ when req.Method == HttpMethod.Get => (HttpStatusCode.OK, ZonesJson),
                _ when req.Method == HttpMethod.Post => (HttpStatusCode.OK, CreatedJson),
                // The delete fails — previously swallowed, now it must stay visible.
                _ => (HttpStatusCode.Forbidden, "{\"success\":false,\"errors\":[{\"message\":\"no permission\"}]}"),
            });
            var http = new HttpClient(handler);
            using var provider = new CloudflareDnsProvider("token", http,
                new DnsJournalContext(journal, "cert1"));

            await provider.PublishTxtRecordAsync("example.com", "_acme-challenge.example.com", "val");
            await provider.RemoveTxtRecordsAsync();   // must not throw

            var orphan = Assert.Single(journal.GetAll());
            Assert.Contains("no permission", orphan.LastCleanupError);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_record_already_gone_counts_as_cleaned_up()
    {
        var (journal, _, root) = NewJournal();
        try
        {
            var handler = new RouteHandler(req => req.Method switch
            {
                _ when req.Method == HttpMethod.Get => (HttpStatusCode.OK, ZonesJson),
                _ when req.Method == HttpMethod.Post => (HttpStatusCode.OK, CreatedJson),
                _ => (HttpStatusCode.NotFound, "{\"success\":false,\"errors\":[{\"message\":\"not found\"}]}"),
            });
            var http = new HttpClient(handler);
            using var provider = new CloudflareDnsProvider("token", http,
                new DnsJournalContext(journal, "cert1"));

            await provider.PublishTxtRecordAsync("example.com", "_acme-challenge.example.com", "val");
            await provider.RemoveTxtRecordsAsync();

            Assert.Empty(journal.GetAll());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Publishing_without_a_journal_still_works()
    {
        var handler = new RouteHandler(req =>
            req.Method == HttpMethod.Get ? (HttpStatusCode.OK, ZonesJson) : (HttpStatusCode.OK, CreatedJson));
        var http = new HttpClient(handler);
        using var provider = new CloudflareDnsProvider("token", http);   // no journal

        await provider.PublishTxtRecordAsync("example.com", "_acme-challenge.example.com", "val");
        await provider.RemoveTxtRecordsAsync();
    }
}
