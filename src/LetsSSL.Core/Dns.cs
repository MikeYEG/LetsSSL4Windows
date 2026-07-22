using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

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

/// <summary>Result of validating an automated DNS provider's credentials.</summary>
public sealed record DnsCredentialTestResult(bool Success, string Message);

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

    /// <summary>Validates the API token via Cloudflare's token-verify endpoint.</summary>
    public async Task<DnsCredentialTestResult> VerifyCredentialsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/user/tokens/verify", ct);
            var doc = await ReadResultAsync(resp, ct);
            var status = doc.RootElement.TryGetProperty("result", out var r)
                         && r.TryGetProperty("status", out var s) ? s.GetString() : null;
            return string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                ? new DnsCredentialTestResult(true, "Cloudflare API token is valid and active.")
                : new DnsCredentialTestResult(false, $"Cloudflare token is not active (status: {status ?? "unknown"}).");
        }
        catch (Exception ex)
        {
            return new DnsCredentialTestResult(false, ex.Message);
        }
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

/// <summary>Amazon Route 53 credentials, persisted (DPAPI-protected) as JSON.</summary>
public sealed record Route53Credentials(string AccessKeyId, string SecretAccessKey, string? HostedZoneId);

/// <summary>
/// Publishes DNS-01 TXT records in Amazon Route 53 via the REST API, signed with
/// AWS Signature V4 (no AWS SDK dependency). Records that share a name (a wildcard
/// plus its apex) are written into a single TXT record set, as Route 53 requires.
/// The change is created in <see cref="ConfirmPublishedAsync"/> and awaited until
/// Route 53 reports it INSYNC, so validation isn't attempted too early.
/// </summary>
public sealed class Route53DnsProvider : IDnsProvider, IDisposable
{
    private const string Host = "route53.amazonaws.com";
    private const string Region = "us-east-1";   // Route 53 is global; requests are signed in us-east-1
    private const string Service = "route53";
    private const string V = "2013-04-01";
    private static readonly XNamespace Ns = "https://route53.amazonaws.com/doc/2013-04-01/";

    private readonly HttpClient _http;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string? _hostedZoneId;

    // record name -> the TXT values that must exist there (wildcard + apex share one name)
    private readonly Dictionary<string, HashSet<string>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string ZoneId, string Name, string[] Values)> _applied = new();

    public Route53DnsProvider(string accessKeyId, string secretAccessKey, string? hostedZoneId = null, HttpClient? httpClient = null)
    {
        _accessKey = accessKeyId;
        _secretKey = secretAccessKey;
        _hostedZoneId = string.IsNullOrWhiteSpace(hostedZoneId) ? null : hostedZoneId.Trim();
        _http = httpClient ?? new HttpClient();
    }

    /// <summary>Validates the AWS credentials by listing hosted zones (read-only).</summary>
    public async Task<DnsCredentialTestResult> VerifyCredentialsAsync(CancellationToken ct = default)
    {
        try
        {
            var doc = await SendAsync(HttpMethod.Get, $"/{V}/hostedzone", null, ct);
            var count = doc.Descendants(Ns + "HostedZone").Count();
            return new DnsCredentialTestResult(true,
                $"AWS credentials are valid — {count} hosted zone(s) accessible.");
        }
        catch (Exception ex)
        {
            return new DnsCredentialTestResult(false, ex.Message);
        }
    }

    public Task PublishTxtRecordAsync(string domain, string recordName, string value, CancellationToken ct = default)
    {
        var name = recordName.TrimEnd('.');
        if (!_pending.TryGetValue(name, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _pending[name] = set;
        }
        set.Add(value);
        return Task.CompletedTask;
    }

    public async Task ConfirmPublishedAsync(CancellationToken ct = default)
    {
        var changeIds = new List<string>();
        foreach (var (name, values) in _pending)
        {
            var zoneId = _hostedZoneId ?? await FindZoneIdAsync(name, ct);
            var vals = values.ToArray();
            var changeId = await ChangeAsync(zoneId, name, vals, "UPSERT", ct);
            _applied.Add((zoneId, name, vals));
            if (!string.IsNullOrEmpty(changeId)) changeIds.Add(changeId!);
        }
        foreach (var id in changeIds) await WaitInSyncAsync(id, ct);
    }

    public async Task RemoveTxtRecordsAsync(CancellationToken ct = default)
    {
        foreach (var (zoneId, name, values) in _applied)
        {
            try { await ChangeAsync(zoneId, name, values, "DELETE", ct); }
            catch { /* best-effort cleanup */ }
        }
        _applied.Clear();
        _pending.Clear();
    }

    /// <summary>Finds the hosted zone whose name is the longest suffix of the record name.</summary>
    private async Task<string> FindZoneIdAsync(string recordName, CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Get, $"/{V}/hostedzone", null, ct);
        string? bestId = null;
        var bestLen = -1;
        foreach (var hz in doc.Descendants(Ns + "HostedZone"))
        {
            var zoneName = ((string?)hz.Element(Ns + "Name") ?? string.Empty).TrimEnd('.');
            if (zoneName.Length == 0) continue;
            var isMatch = recordName.Equals(zoneName, StringComparison.OrdinalIgnoreCase)
                          || recordName.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase);
            if (isMatch && zoneName.Length > bestLen)
            {
                bestLen = zoneName.Length;
                bestId = ((string?)hz.Element(Ns + "Id") ?? string.Empty).Replace("/hostedzone/", string.Empty);
            }
        }
        return bestId ?? throw new InvalidOperationException(
            $"No Route 53 hosted zone found for '{recordName}'. Enter the Hosted Zone ID or check the AWS credentials.");
    }

    private async Task<string?> ChangeAsync(string zoneId, string name, string[] values, string action, CancellationToken ct)
    {
        var records = string.Concat(values.Select(v =>
            $"<ResourceRecord><Value>{XmlEscape("\"" + v + "\"")}</Value></ResourceRecord>"));
        var body =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<ChangeResourceRecordSetsRequest xmlns=\"{Ns}\"><ChangeBatch><Changes><Change>" +
            $"<Action>{action}</Action><ResourceRecordSet><Name>{XmlEscape(name)}</Name>" +
            "<Type>TXT</Type><TTL>60</TTL>" +
            $"<ResourceRecords>{records}</ResourceRecords></ResourceRecordSet></Change></Changes></ChangeBatch></ChangeResourceRecordSetsRequest>";
        var doc = await SendAsync(HttpMethod.Post, $"/{V}/hostedzone/{zoneId}/rrset", body, ct);
        return ((string?)doc.Descendants(Ns + "Id").FirstOrDefault())?.Replace("/change/", string.Empty);
    }

    private async Task WaitInSyncAsync(string changeId, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var doc = await SendAsync(HttpMethod.Get, $"/{V}/change/{changeId}", null, ct);
            if (string.Equals((string?)doc.Descendants(Ns + "Status").FirstOrDefault(), "INSYNC", StringComparison.OrdinalIgnoreCase))
                return;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task<XDocument> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var uri = new Uri($"https://{Host}{path}");
        var payload = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

        using var req = new HttpRequestMessage(method, uri);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "text/xml");

        // Host is added by HttpClient; the rest come from the signer.
        foreach (var (n, val) in AwsV4Signer.SignedHeaders(
                     method.Method, uri, string.Empty, payload, _accessKey, _secretKey, Region, Service, DateTime.UtcNow))
        {
            req.Headers.TryAddWithoutValidation(n, val);
        }

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Route 53 API error (HTTP {(int)resp.StatusCode}): {ExtractError(text)}");
        return string.IsNullOrWhiteSpace(text) ? new XDocument(new XElement("Empty")) : XDocument.Parse(text);
    }

    private static string ExtractError(string xml)
    {
        try
        {
            var msg = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            return string.IsNullOrWhiteSpace(msg) ? xml.Trim() : msg!;
        }
        catch { return xml.Trim(); }
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Dispose() => _http.Dispose();
}

/// <summary>Minimal AWS Signature Version 4 signer (verified against AWS's published vector).</summary>
internal static class AwsV4Signer
{
    /// <summary>
    /// Returns the headers to attach to the request: x-amz-content-sha256,
    /// x-amz-date and Authorization. The Host header is added by HttpClient and is
    /// included in the signature. <paramref name="canonicalQuery"/> must already be
    /// canonical (sorted + encoded); pass "" when there is none.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> SignedHeaders(
        string method, Uri uri, string canonicalQuery, byte[] payload,
        string accessKey, string secretKey, string region, string service, DateTime utcNow)
    {
        var amzDate = utcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = ToHex(Sha256(payload));

        var signed = new (string Name, string Value)[]
        {
            ("host", uri.Host),
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", amzDate),
        };
        var canonicalHeaders = string.Concat(signed.Select(h => $"{h.Name}:{h.Value.Trim()}\n"));
        var signedNames = string.Join(";", signed.Select(h => h.Name));

        var canonicalRequest =
            method + "\n" + uri.AbsolutePath + "\n" + canonicalQuery + "\n" +
            canonicalHeaders + "\n" + signedNames + "\n" + payloadHash;

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign =
            "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" +
            ToHex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signature = ToHex(HmacSha256(SigningKey(secretKey, dateStamp, region, service), stringToSign));
        var authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedNames}, Signature={signature}";

        return new[]
        {
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", amzDate),
            ("Authorization", authorization),
        };
    }

    private static byte[] SigningKey(string secret, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
