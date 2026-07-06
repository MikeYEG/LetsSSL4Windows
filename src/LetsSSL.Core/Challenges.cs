using System.Text;
using Certes;
using Certes.Acme;
using LetsSSL.Core.Dns;
using LetsSSL.Core.Models;

namespace LetsSSL.Core.Challenges;

/// <summary>Publishes and cleans up HTTP-01 challenge files.</summary>
public interface IHttpChallengeResponder
{
    Task PublishAsync(string token, string keyAuthorization, CancellationToken ct = default);
    Task CleanupAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// Writes HTTP-01 challenge files to a web root's /.well-known/acme-challenge/
/// directory, plus a scoped web.config so IIS serves the extensionless files.
/// </summary>
public class FileSystemHttpChallengeResponder : IHttpChallengeResponder
{
    private const string ChallengeDirRelative = ".well-known/acme-challenge";

    private const string WebConfig =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<configuration>\n" +
        "  <system.webServer>\n" +
        "    <staticContent>\n" +
        "      <mimeMap fileExtension=\".\" mimeType=\"text/plain\" />\n" +
        "    </staticContent>\n" +
        "    <handlers>\n" +
        "      <clear />\n" +
        "      <add name=\"StaticFile\" path=\"*\" verb=\"*\" type=\"\" modules=\"StaticFileModule\"\n" +
        "           resourceType=\"Either\" requireAccess=\"Read\" />\n" +
        "    </handlers>\n" +
        "  </system.webServer>\n" +
        "</configuration>\n";

    private readonly string _challengeDir;

    public FileSystemHttpChallengeResponder(string webRootPath)
    {
        if (string.IsNullOrWhiteSpace(webRootPath))
            throw new ArgumentException("A web root path is required for HTTP-01 validation.", nameof(webRootPath));
        _challengeDir = Path.Combine(webRootPath, ChallengeDirRelative.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task PublishAsync(string token, string keyAuthorization, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_challengeDir);
        var configPath = Path.Combine(_challengeDir, "web.config");
        if (!File.Exists(configPath))
            await File.WriteAllTextAsync(configPath, WebConfig, new UTF8Encoding(false), ct);
        await File.WriteAllTextAsync(Path.Combine(_challengeDir, token), keyAuthorization, new UTF8Encoding(false), ct);
    }

    public Task CleanupAsync(string token, CancellationToken ct = default)
    {
        var tokenPath = Path.Combine(_challengeDir, token);
        if (File.Exists(tokenPath)) File.Delete(tokenPath);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Strategy that satisfies a single authorization: selects the right ACME
/// challenge, publishes the proof, and returns the challenge to validate.
/// </summary>
public interface IAcmeChallengeHandler
{
    ChallengeType ChallengeType { get; }
    Task<IChallengeContext> PrepareAsync(AcmeContext acme, IAuthorizationContext authz, string domain, string displayDomain, CancellationToken ct = default);
    /// <summary>Called once after all domains are prepared and before validation begins.</summary>
    Task ReadyForValidationAsync(CancellationToken ct = default);
    Task CleanupAsync(CancellationToken ct = default);
}

/// <summary>Satisfies authorizations with HTTP-01, publishing token files to a web root.</summary>
public sealed class HttpChallengeHandler : IAcmeChallengeHandler
{
    private readonly IHttpChallengeResponder _responder;
    private readonly List<string> _tokens = new();

    public HttpChallengeHandler(IHttpChallengeResponder responder) => _responder = responder;

    public ChallengeType ChallengeType => ChallengeType.Http01;

    public async Task<IChallengeContext> PrepareAsync(AcmeContext acme, IAuthorizationContext authz, string domain, string displayDomain, CancellationToken ct = default)
    {
        var challenge = await authz.Http()
            ?? throw new InvalidOperationException($"No HTTP-01 challenge offered for {domain}.");
        await _responder.PublishAsync(challenge.Token, challenge.KeyAuthz, ct);
        _tokens.Add(challenge.Token);
        return challenge;
    }

    public Task ReadyForValidationAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        foreach (var token in _tokens) await _responder.CleanupAsync(token, ct);
        _tokens.Clear();
    }
}

/// <summary>
/// Satisfies authorizations with DNS-01 by publishing _acme-challenge TXT records
/// through an <see cref="IDnsProvider"/>. Required for wildcard certificates.
/// </summary>
public sealed class DnsChallengeHandler : IAcmeChallengeHandler
{
    private readonly IDnsProvider _provider;

    public DnsChallengeHandler(IDnsProvider provider) => _provider = provider;

    public ChallengeType ChallengeType => ChallengeType.Dns01;

    public async Task<IChallengeContext> PrepareAsync(AcmeContext acme, IAuthorizationContext authz, string domain, string displayDomain, CancellationToken ct = default)
    {
        var challenge = await authz.Dns()
            ?? throw new InvalidOperationException($"No DNS-01 challenge offered for {domain}.");
        var txtValue = acme.AccountKey.DnsTxt(challenge.Token);
        await _provider.PublishTxtRecordAsync(displayDomain, $"_acme-challenge.{domain}", txtValue, ct);
        return challenge;
    }

    public Task ReadyForValidationAsync(CancellationToken ct = default) => _provider.ConfirmPublishedAsync(ct);

    public Task CleanupAsync(CancellationToken ct = default) => _provider.RemoveTxtRecordsAsync(ct);
}
