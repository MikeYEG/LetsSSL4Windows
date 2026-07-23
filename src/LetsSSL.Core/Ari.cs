using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Acme;

/// <summary>
/// A CA's suggested renewal window for a certificate (ACME Renewal Information,
/// RFC 9773). Honoring it lets a client renew <em>early</em> when the CA advises
/// — for example ahead of a mass revocation — rather than only on a fixed
/// days-before-expiry timer.
/// </summary>
public record RenewalInfo
{
    /// <summary>Start of the CA's suggested renewal window (UTC).</summary>
    public required DateTimeOffset WindowStart { get; init; }
    /// <summary>End of the CA's suggested renewal window (UTC).</summary>
    public required DateTimeOffset WindowEnd { get; init; }
    /// <summary>Optional CA-provided URL explaining why renewal is advised.</summary>
    public string? ExplanationUrl { get; init; }
}

/// <summary>
/// Builds the ARI certificate identifier used to address a certificate on the
/// CA's <c>renewalInfo</c> endpoint: <c>base64url(AKI keyIdentifier) "."
/// base64url(serialNumber)</c> (RFC 9773 §4.1).
/// </summary>
public static class AriCertId
{
    private const string AuthorityKeyIdentifierOid = "2.5.29.35";

    /// <summary>Joins the two DER byte fields into the dotted, base64url CertID.</summary>
    public static string Compute(ReadOnlySpan<byte> authorityKeyIdentifier, ReadOnlySpan<byte> serialNumber)
        => Base64Url(authorityKeyIdentifier) + "." + Base64Url(serialNumber);

    /// <summary>
    /// Derives the CertID from an issued certificate. Throws if the certificate
    /// carries no Authority Key Identifier (ARI is not available for such certs).
    /// </summary>
    public static string FromCertificate(X509Certificate2 cert)
    {
        var akiExt = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == AuthorityKeyIdentifierOid)
            ?? throw new InvalidOperationException(
                "The certificate has no Authority Key Identifier extension, so ARI is not available for it.");

        var aki = new X509AuthorityKeyIdentifierExtension(akiExt.RawData, akiExt.Critical);
        var keyId = aki.KeyIdentifier
            ?? throw new InvalidOperationException(
                "The certificate's Authority Key Identifier has no keyIdentifier field, so ARI is not available for it.");

        // GetSerialNumber() returns the serial's content octets little-endian;
        // ARI needs the DER INTEGER content (big-endian), so reverse a copy.
        var serial = cert.GetSerialNumber();
        Array.Reverse(serial);

        return Compute(keyId.Span, serial);
    }

    private static string Base64Url(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Queries a CA's ACME Renewal Information (ARI) endpoint. Everything here is
/// best-effort and advisory: if the CA doesn't advertise ARI, the certificate
/// isn't eligible, or the network fails, the client falls back to the fixed
/// days-before-expiry schedule, so all failures return <c>null</c> rather than
/// throwing.
/// </summary>
public class RenewalInfoClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<RenewalInfoClient> _logger;

    // Cache of directory URI -> resolved renewalInfo endpoint (null = the CA has
    // no ARI). Only successful directory reads are cached, so a transient failure
    // is retried next cycle rather than disabling ARI for the client's lifetime.
    // The client outlives a single renewal run, so this avoids one directory GET
    // per certificate on every cycle.
    private readonly Dictionary<string, Uri?> _endpointCache = new();
    private readonly object _cacheLock = new();

    public RenewalInfoClient(HttpClient? httpClient = null, ILogger<RenewalInfoClient>? logger = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = httpClient is null;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RenewalInfoClient>.Instance;
    }

    /// <summary>
    /// Returns the CA's suggested renewal window for <paramref name="cert"/>, or
    /// <c>null</c> if ARI is unavailable for any reason.
    /// </summary>
    public async Task<RenewalInfo?> TryGetAsync(Uri directoryUri, X509Certificate2 cert, CancellationToken ct = default)
    {
        try
        {
            var endpoint = await GetRenewalInfoEndpointAsync(directoryUri, ct);
            if (endpoint is null)
            {
                _logger.LogDebug("CA at {Directory} does not advertise a renewalInfo endpoint.", directoryUri);
                return null;
            }

            var certId = AriCertId.FromCertificate(cert);
            var url = endpoint.AbsoluteUri.TrimEnd('/') + "/" + certId;

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("ARI request for {Subject} returned {Status}.", cert.Subject, (int)resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("suggestedWindow", out var window))
                return null;

            var start = window.GetProperty("start").GetDateTimeOffset();
            var end = window.GetProperty("end").GetDateTimeOffset();
            var explanation = doc.RootElement.TryGetProperty("explanationURL", out var e) ? e.GetString() : null;

            return new RenewalInfo { WindowStart = start, WindowEnd = end, ExplanationUrl = explanation };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ARI lookup failed for {Subject}; falling back to date-based renewal.", cert.Subject);
            return null;
        }
    }

    /// <summary>
    /// Reads the ACME directory and returns its <c>renewalInfo</c> URL, or null.
    /// The resolved endpoint (including a definitive "no ARI") is cached per
    /// directory URI; a transient directory-fetch failure is not cached.
    /// </summary>
    private async Task<Uri?> GetRenewalInfoEndpointAsync(Uri directoryUri, CancellationToken ct)
    {
        var key = directoryUri.AbsoluteUri;
        lock (_cacheLock)
        {
            if (_endpointCache.TryGetValue(key, out var cached)) return cached;
        }

        using var resp = await _http.GetAsync(directoryUri, ct);
        if (!resp.IsSuccessStatusCode) return null;   // transient: don't cache, retry next cycle

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Uri? endpoint = null;
        if (doc.RootElement.TryGetProperty("renewalInfo", out var ri)
            && ri.GetString() is { } s
            && Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            endpoint = uri;
        }

        lock (_cacheLock) { _endpointCache[key] = endpoint; }   // cache the definitive result
        return endpoint;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
