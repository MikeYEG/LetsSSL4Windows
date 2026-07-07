using System.Text.Json;

namespace LetsSSL.Core.Updates;

/// <summary>Result of an update check against GitHub releases.</summary>
public sealed class UpdateInfo
{
    public bool UpdateAvailable { get; init; }
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }

    /// <summary>Direct download URL of the release's installer (.exe) asset, if any.</summary>
    public string? InstallerUrl { get; init; }
}

/// <summary>
/// Checks the GitHub Releases API for a newer published version. Network errors
/// are surfaced as exceptions; callers treat failures as "no update".
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/MikeYEG/LetsSSL4Windows/releases/latest";

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub requires a User-Agent on API requests.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LetsSSL4Windows-UpdateCheck");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateInfo> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(LatestReleaseApi, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

        var latest = ParseVersion(tag);
        var available = latest is not null && latest > current;

        return new UpdateInfo
        {
            CurrentVersion = current.ToString(),
            LatestVersion = tag,
            ReleaseUrl = url,
            InstallerUrl = FindInstallerAsset(root),
            UpdateAvailable = available,
        };
    }

    /// <summary>Streams a release asset (the installer) to a local file.</summary>
    public async Task DownloadAsync(string url, string destinationPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destinationPath);
        await source.CopyToAsync(file, ct);
    }

    /// <summary>Picks the .exe asset from the release (preferring the Setup installer).</summary>
    private static string? FindInstallerAsset(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var dl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
            if (name is null || dl is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                return dl; // prefer the installer
            fallback ??= dl;
        }
        return fallback;
    }

    /// <summary>Parses a release tag like "v1.2.3" into a <see cref="Version"/>.</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var cleaned = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}
