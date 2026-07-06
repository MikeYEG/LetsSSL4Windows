using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using LetsSSL.Core.Challenges;
using LetsSSL.Core.Models;
using LetsSSL.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Acme;

/// <summary>The output of a successful ACME order: a password-protected PFX.</summary>
public class CertificateOrderResult
{
    public required byte[] PfxBytes { get; init; }
    public required string PfxPassword { get; init; }
}

/// <summary>
/// Thin wrapper over the Certes ACME client implementing the issuance flow:
/// account → order → authorization (HTTP-01/DNS-01) → finalize → download → PFX.
/// </summary>
public class AcmeService
{
    private readonly AppPaths _paths;
    private readonly ILogger<AcmeService> _logger;

    public AcmeService(AppPaths paths, ILogger<AcmeService>? logger = null)
    {
        _paths = paths;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AcmeService>.Instance;
    }

    /// <summary>Loads the saved ACME account for the environment, or registers a new one.</summary>
    public async Task<AcmeContext> GetOrCreateAccountAsync(AcmeEnvironment environment, string email, CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        var keyFile = _paths.AccountKeyFile(environment.ToString());
        var directory = environment.DirectoryUri();

        if (File.Exists(keyFile))
        {
            var accountKey = KeyFactory.FromPem(await File.ReadAllTextAsync(keyFile, ct));
            _logger.LogInformation("Reusing existing ACME account for {Environment}.", environment);
            return new AcmeContext(directory, accountKey);
        }

        var acme = new AcmeContext(directory);
        await acme.NewAccount(email, termsOfServiceAgreed: true);
        await File.WriteAllTextAsync(keyFile, acme.AccountKey.ToPem(), ct);
        _logger.LogInformation("Registered new ACME account for {Environment} ({Email}).", environment, email);
        return acme;
    }

    /// <summary>Runs a full issuance for the configured domains and returns a PFX.</summary>
    public async Task<CertificateOrderResult> RequestCertificateAsync(
        ManagedCertificate config, AcmeEnvironment environment, IAcmeChallengeHandler challengeHandler,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        void Report(string msg) { _logger.LogInformation("{Message}", msg); progress?.Report(msg); }

        var domains = config.AllDomains.ToArray();
        if (domains.Length == 0) throw new InvalidOperationException("At least one domain is required.");

        var email = string.IsNullOrWhiteSpace(config.ContactEmail) ? "admin@" + domains[0] : config.ContactEmail;
        Report($"Connecting to Let's Encrypt ({environment})…");
        var acme = await GetOrCreateAccountAsync(environment, email, ct);

        Report($"Creating order for: {string.Join(", ", domains)}");
        var order = await acme.NewOrder(domains);
        var authorizations = (await order.Authorizations()).ToList();

        try
        {
            // Phase 1: publish the proof (file or DNS record) for every domain.
            var pending = new List<(IAuthorizationContext Authz, IChallengeContext Challenge, string Domain)>();
            foreach (var authz in authorizations)
            {
                ct.ThrowIfCancellationRequested();
                var res = await authz.Resource();
                var domain = res.Identifier.Value;
                var displayDomain = res.Wildcard == true ? "*." + domain : domain;
                Report($"Preparing {challengeHandler.ChallengeType} challenge for {displayDomain}…");
                var challenge = await challengeHandler.PrepareAsync(acme, authz, domain, displayDomain, ct);
                pending.Add((authz, challenge, domain));
            }

            // All records are now known — let the handler confirm readiness (manual
            // DNS shows a single prompt listing every record) before validation.
            await challengeHandler.ReadyForValidationAsync(ct);

            // Validate each authorization.
            foreach (var (authz, challenge, domain) in pending)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Asking the CA to validate {domain}…");
                await challenge.Validate();
                await WaitForAuthorizationAsync(authz, domain, Report, ct);
            }

            Report("Generating key and finalizing the certificate…");
            var privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
            var cert = await order.Generate(new CsrInfo { CommonName = domains[0] }, privateKey);

            var password = Guid.NewGuid().ToString("N");
            var friendlyName = $"LetsSSL4Windows {domains[0]} {DateTime.UtcNow:yyyy-MM-dd}";
            var pfxBuilder = cert.ToPfx(privateKey);
            // Don't require a complete chain up to a trusted root. Let's Encrypt's
            // staging chain (and some cross-signed production chains) reference an
            // issuer that isn't in the downloaded bundle; with FullChain=true,
            // Build() throws "Can not find issuer …". Setting it false still bundles
            // the leaf + available intermediates, which is what IIS needs.
            pfxBuilder.FullChain = false;
            var pfxBytes = pfxBuilder.Build(friendlyName, password);

            Report("Certificate issued successfully.");
            return new CertificateOrderResult { PfxBytes = pfxBytes, PfxPassword = password };
        }
        finally
        {
            try { await challengeHandler.CleanupAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Challenge cleanup failed."); }
        }
    }

    private static async Task WaitForAuthorizationAsync(IAuthorizationContext authz, string domain, Action<string> report, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var res = await authz.Resource();
            switch (res.Status)
            {
                case AuthorizationStatus.Valid:
                    report($"Validation succeeded for {domain}.");
                    return;
                case AuthorizationStatus.Invalid:
                    var detail = res.Challenges?.FirstOrDefault(c => c.Error != null)?.Error?.Detail;
                    throw new InvalidOperationException($"Validation failed for {domain}: {detail ?? "the CA rejected the challenge."}");
                default:
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    break;
            }
        }
        throw new TimeoutException($"Timed out waiting for {domain} to validate.");
    }
}
