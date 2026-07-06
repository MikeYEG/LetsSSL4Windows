using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using LetsSSL.Core.Models;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Deployment;

/// <summary>Everything a deployment task needs about the certificate just issued.</summary>
[SupportedOSPlatform("windows")]
public sealed class DeploymentContext
{
    public required ManagedCertificate Certificate { get; init; }
    public required X509Certificate2 InstalledCertificate { get; init; }
    public required byte[] PfxBytes { get; init; }
    public required string PfxPassword { get; init; }
}

/// <summary>A single post-issuance action (export a file, run a script, …).</summary>
[SupportedOSPlatform("windows")]
public interface IDeploymentTask
{
    string Describe();
    Task ExecuteAsync(DeploymentContext context, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>Writes the issued certificate as a PFX, optionally re-encrypted.</summary>
[SupportedOSPlatform("windows")]
public sealed class ExportPfxTask : IDeploymentTask
{
    private readonly DeploymentTaskConfig _config;
    public ExportPfxTask(DeploymentTaskConfig config) => _config = config;
    public string Describe() => $"Export PFX to {_config.Get("Path")}";

    public async Task ExecuteAsync(DeploymentContext context, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var path = _config.Get("Path") ?? throw new InvalidOperationException("Export PFX task requires a 'Path' setting.");
        var password = _config.Get("Password");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var bytes = string.IsNullOrEmpty(password)
            ? context.PfxBytes
            : context.InstalledCertificate.Export(X509ContentType.Pfx, password);

        await File.WriteAllBytesAsync(path, bytes, ct);
        progress?.Report($"Exported PFX to {path}.");
    }
}

/// <summary>Exports fullchain.pem + privkey.pem into a directory (nginx/Apache/HAProxy).</summary>
[SupportedOSPlatform("windows")]
public sealed class ExportPemTask : IDeploymentTask
{
    private readonly DeploymentTaskConfig _config;
    public ExportPemTask(DeploymentTaskConfig config) => _config = config;
    public string Describe() => $"Export PEM to {_config.Get("Path")}";

    public async Task ExecuteAsync(DeploymentContext context, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = _config.Get("Path") ?? throw new InvalidOperationException("Export PEM task requires a 'Path' directory setting.");
        Directory.CreateDirectory(dir);

        var collection = new X509Certificate2Collection();
        collection.Import(context.PfxBytes, context.PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        var leaf = collection.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey) ?? collection[0];

        var fullChain = string.Join(Environment.NewLine, collection.Cast<X509Certificate2>().Select(c => c.ExportCertificatePem()));
        await File.WriteAllTextAsync(Path.Combine(dir, "fullchain.pem"), fullChain, ct);

        var keyPem = ExportPrivateKeyPem(leaf) ?? throw new InvalidOperationException("The certificate has no exportable private key.");
        await File.WriteAllTextAsync(Path.Combine(dir, "privkey.pem"), keyPem, ct);

        progress?.Report($"Exported fullchain.pem and privkey.pem to {dir}.");
    }

    private static string? ExportPrivateKeyPem(X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPrivateKey();
        if (rsa != null) return rsa.ExportPkcs8PrivateKeyPem();
        using var ecdsa = cert.GetECDsaPrivateKey();
        return ecdsa?.ExportPkcs8PrivateKeyPem();
    }
}

/// <summary>
/// Runs an external script/executable after issuance. Cert details are passed as
/// LETSSSL4WINDOWS_* environment variables. PowerShell (.ps1) is run via powershell.exe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunScriptTask : IDeploymentTask
{
    private readonly DeploymentTaskConfig _config;
    public RunScriptTask(DeploymentTaskConfig config) => _config = config;
    public string Describe() => $"Run script {_config.Get("Path")}";

    public async Task ExecuteAsync(DeploymentContext context, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var path = _config.Get("Path") ?? throw new InvalidOperationException("Run script task requires a 'Path' setting.");
        var arguments = _config.Get("Arguments") ?? string.Empty;
        var isPowerShell = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = isPowerShell ? "powershell.exe" : path,
            Arguments = isPowerShell ? $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {arguments}" : arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["LETSSSL4WINDOWS_THUMBPRINT"] = context.Certificate.Thumbprint ?? string.Empty;
        psi.Environment["LETSSSL4WINDOWS_DOMAIN"] = context.Certificate.PrimaryDomain;
        psi.Environment["LETSSSL4WINDOWS_PFX_PATH"] = context.Certificate.PfxPath ?? string.Empty;
        psi.Environment["LETSSSL4WINDOWS_PFX_PASSWORD"] = context.PfxPassword;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{path}'.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = (stdout + stderr).Trim();
        if (!string.IsNullOrEmpty(output)) progress?.Report(output);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Script exited with code {process.ExitCode}.");
        progress?.Report($"Script {Path.GetFileName(path)} completed.");
    }
}

/// <summary>Outcome of one deployment task.</summary>
public record DeploymentTaskOutcome(DeploymentTaskConfig Config, bool Succeeded, string? Error);

/// <summary>Builds task instances from their configs and runs them in order.</summary>
[SupportedOSPlatform("windows")]
public sealed class DeploymentTaskRunner
{
    private readonly ILogger<DeploymentTaskRunner> _logger;

    public DeploymentTaskRunner(ILogger<DeploymentTaskRunner>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DeploymentTaskRunner>.Instance;

    public async Task<IReadOnlyList<DeploymentTaskOutcome>> RunAllAsync(
        IEnumerable<DeploymentTaskConfig> configs, DeploymentContext context,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var outcomes = new List<DeploymentTaskOutcome>();
        foreach (var config in configs)
        {
            ct.ThrowIfCancellationRequested();
            var task = Create(config);
            progress?.Report($"Deployment: {task.Describe()}…");
            try
            {
                await task.ExecuteAsync(context, progress, ct);
                outcomes.Add(new DeploymentTaskOutcome(config, true, null));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment task {Type} failed.", config.Type);
                progress?.Report($"Deployment task failed: {ex.Message}");
                outcomes.Add(new DeploymentTaskOutcome(config, false, ex.Message));
            }
        }
        return outcomes;
    }

    public static IDeploymentTask Create(DeploymentTaskConfig config) => config.Type switch
    {
        DeploymentTaskType.ExportPfx => new ExportPfxTask(config),
        DeploymentTaskType.ExportPem => new ExportPemTask(config),
        DeploymentTaskType.RunScript => new RunScriptTask(config),
        _ => throw new NotSupportedException($"Unknown deployment task type: {config.Type}"),
    };
}
