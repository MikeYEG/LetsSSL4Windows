using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LetsSSL.Core.Dns;

/// <summary>
/// Publishes and removes DNS-01 challenge TXT records. Implementations track the
/// records they create so <see cref="RemoveTxtRecordsAsync"/> can clean them up.
/// </summary>
/// <summary>A DNS-01 challenge TXT record to create, with the domain it proves.</summary>
public readonly record struct DnsTxtRecord(string Domain, string Name, string Value)
{
    /// <summary>"wildcard" for *.domain, otherwise "base domain".</summary>
    public string Kind => Domain.StartsWith("*.", StringComparison.Ordinal) ? "wildcard" : "base domain";

    /// <summary>Heading shown in the manual DNS dialog, e.g. "For *.example.com (wildcard)".</summary>
    public string Heading => $"For {Domain} ({Kind})";
}

/// <summary>
/// Checks whether a TXT record is already published, using public DNS over HTTPS
/// (Google) — closer to what the CA's resolvers see than the local cache.
/// </summary>
public sealed class DnsTxtVerifier : IDisposable
{
    private readonly HttpClient _http;

    public DnsTxtVerifier(HttpClient? httpClient = null) =>
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>True if a TXT record at <paramref name="recordName"/> currently has the expected value.</summary>
    public async Task<bool> ExistsAsync(string recordName, string expectedValue, CancellationToken ct = default)
    {
        var url = $"https://dns.google/resolve?name={Uri.EscapeDataString(recordName)}&type=TXT";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return false;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var answer in answers.EnumerateArray())
        {
            var data = answer.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (data is null) continue;
            // TXT data is quoted, and long values may be split into "abc" "def" chunks.
            var value = data.Replace("\" \"", string.Empty).Trim('"');
            if (string.Equals(value, expectedValue, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}

public interface IDnsProvider
{
    /// <summary>
    /// Records the TXT record that needs to exist (may not create it immediately).
    /// <paramref name="domain"/> is the human-readable domain it proves (e.g. *.example.com).
    /// </summary>
    Task PublishTxtRecordAsync(string domain, string recordName, string value, CancellationToken ct = default);

    /// <summary>
    /// Called once after every record has been published and before validation —
    /// the point at which manual providers prompt the user (batched).
    /// </summary>
    Task ConfirmPublishedAsync(CancellationToken ct = default);

    Task RemoveTxtRecordsAsync(CancellationToken ct = default);
}

/// <summary>
/// Lets the manual DNS provider ask the user to create/remove TXT records. The
/// GUI implements this with a single dialog listing all records; the unattended
/// service has no implementation.
/// </summary>
public interface IManualDnsInteraction
{
    Task PromptCreateAsync(IReadOnlyList<DnsTxtRecord> records, CancellationToken ct = default);
    Task PromptRemoveAsync(IReadOnlyList<DnsTxtRecord> records, CancellationToken ct = default);
}

/// <summary>
/// DNS provider that defers to a human via <see cref="IManualDnsInteraction"/>.
/// Records are gathered first, then shown together in a single prompt so a
/// wildcard + apex certificate doesn't pop up multiple dialogs.
/// </summary>
public class ManualDnsProvider : IDnsProvider
{
    private readonly IManualDnsInteraction _interaction;
    private readonly List<DnsTxtRecord> _records = new();

    public ManualDnsProvider(IManualDnsInteraction interaction) => _interaction = interaction;

    public Task PublishTxtRecordAsync(string domain, string recordName, string value, CancellationToken ct = default)
    {
        _records.Add(new DnsTxtRecord(domain, recordName, value));
        return Task.CompletedTask;
    }

    public async Task ConfirmPublishedAsync(CancellationToken ct = default)
    {
        if (_records.Count > 0)
            await _interaction.PromptCreateAsync(_records, ct);
    }

    public async Task RemoveTxtRecordsAsync(CancellationToken ct = default)
    {
        if (_records.Count > 0)
            await _interaction.PromptRemoveAsync(_records, ct);
        _records.Clear();
    }
}

/// <summary>
/// Publishes DNS-01 TXT records through the Cloudflare API using an API token
/// with Zone:DNS:Edit permission.
/// </summary>
public sealed class CloudflareDnsProvider : IDnsProvider, IDisposable
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly HttpClient _http;
    private readonly List<(string ZoneId, string RecordId)> _created = new();

    public CloudflareDnsProvider(string apiToken, HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
    }

    // Cloudflare creates records immediately in PublishTxtRecordAsync.
    public Task ConfirmPublishedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task PublishTxtRecordAsync(string domain, string recordName, string value, CancellationToken ct = default)
    {
        var zoneId = await FindZoneIdAsync(recordName, ct);
        var payload = new { type = "TXT", name = recordName, content = value, ttl = 120 };
        using var resp = await _http.PostAsJsonAsync($"{ApiBase}/zones/{zoneId}/dns_records", payload, ct);
        var doc = await ReadResultAsync(resp, ct);
        var recordId = doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
        _created.Add((zoneId, recordId));
    }

    public async Task RemoveTxtRecordsAsync(CancellationToken ct = default)
    {
        foreach (var (zoneId, recordId) in _created)
        {
            try { using var _ = await _http.DeleteAsync($"{ApiBase}/zones/{zoneId}/dns_records/{recordId}", ct); }
            catch { /* best-effort cleanup */ }
        }
        _created.Clear();
    }

    /// <summary>Finds the Cloudflare zone whose name is the longest suffix of the record name.</summary>
    private async Task<string> FindZoneIdAsync(string recordName, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{ApiBase}/zones?per_page=50", ct);
        var doc = await ReadResultAsync(resp, ct);

        string? bestId = null;
        var bestLen = -1;
        foreach (var zone in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var name = zone.GetProperty("name").GetString() ?? "";
            if ((recordName.Equals(name, StringComparison.OrdinalIgnoreCase)
                 || recordName.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                && name.Length > bestLen)
            {
                bestLen = name.Length;
                bestId = zone.GetProperty("id").GetString();
            }
        }

        return bestId ?? throw new InvalidOperationException(
            $"No Cloudflare zone found that contains '{recordName}'. Check the API token's zone access.");
    }

    private static async Task<JsonDocument> ReadResultAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        if (!resp.IsSuccessStatusCode
            || (doc.RootElement.TryGetProperty("success", out var ok) && !ok.GetBoolean()))
        {
            throw new InvalidOperationException($"Cloudflare API error: {ExtractError(doc) ?? $"HTTP {(int)resp.StatusCode}"}");
        }
        return doc;
    }

    private static string? ExtractError(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            return string.Join("; ", errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s)));
        return null;
    }

    public void Dispose() => _http.Dispose();
}
