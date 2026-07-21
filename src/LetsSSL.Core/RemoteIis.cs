using System.Diagnostics;
using System.Runtime.Versioning;
using LetsSSL.Core.Models;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core.Iis;

/// <summary>Outcome of distributing a certificate to one remote IIS server.</summary>
public sealed record RemoteDeploymentOutcome(RemoteIisTarget Target, bool Succeeded, string? Error);

/// <summary>Result of a WinRM connectivity pre-flight against a remote server.</summary>
public sealed record RemoteConnectionTest(bool Succeeded, string Message, IReadOnlyList<string> RemoteSites);

/// <summary>
/// Distributes an issued certificate to remote Windows/IIS servers over WinRM /
/// PowerShell Remoting. The renewing instance authenticates as its own domain
/// service account (Kerberos — no stored credentials); on each target it imports
/// the PFX into LocalMachine\My (with the friendly name) and binds it to the
/// configured IIS sites with SNI, mirroring the local install/bind behaviour.
///
/// The orchestration runs through the always-present Windows PowerShell
/// (<c>powershell.exe</c>), the same mechanism the RunScript deployment task
/// uses. All per-target data flows through environment variables — never
/// interpolated into the script text — so host names, site names and domains
/// can't inject PowerShell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteIisDeployer
{
    private readonly ILogger<RemoteIisDeployer> _logger;

    public RemoteIisDeployer(ILogger<RemoteIisDeployer>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteIisDeployer>.Instance;

    /// <summary>
    /// Builds the environment passed to the orchestration script for one target.
    /// Multi-valued fields are newline-separated (a separator no host, site or
    /// domain can contain). Kept separate from <see cref="DeployAsync"/> so it can
    /// be unit-tested without invoking PowerShell.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        RemoteIisTarget target, byte[] pfxBytes, string pfxPassword,
        string? friendlyName, IEnumerable<string> domains)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LSW_HOST"] = target.Host,
            ["LSW_PORT"] = target.WinRmPort.ToString(),
            ["LSW_SSL"] = target.UseSsl ? "1" : "0",
            ["LSW_PFX_B64"] = Convert.ToBase64String(pfxBytes),
            ["LSW_PFX_PASS"] = pfxPassword,
            ["LSW_FRIENDLY"] = friendlyName ?? string.Empty,
            ["LSW_DOMAINS"] = string.Join("\n", domains),
            ["LSW_SITES"] = string.Join("\n", target.SiteNames),
        };
    }

    /// <summary>
    /// Pre-flight: opens a WinRM session to the target (as the current identity)
    /// and lists its IIS sites, so the user can confirm reachability, auth, and
    /// the exact remote site names before saving. Returns a failure result rather
    /// than throwing, except that a cancelled <paramref name="ct"/> propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<RemoteConnectionTest> TestConnectionAsync(RemoteIisTarget target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target.Host))
            return new RemoteConnectionTest(false, "No host name.", Array.Empty<string>());

        var scriptPath = Path.Combine(Path.GetTempPath(), $"lsw-test-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, TestScript, ct);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["LSW_HOST"] = target.Host;
            psi.Environment["LSW_PORT"] = target.WinRmPort.ToString();
            psi.Environment["LSW_SSL"] = target.UseSsl ? "1" : "0";

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start powershell.exe.");
            // Read both streams concurrently so a full stderr/stdout buffer can't deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                // The script prints an "OK" marker line, then one site per line. Drop
                // only the leading marker so a site legitimately named "OK" survives.
                var lines = stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (lines.Count > 0 && lines[0] == "OK") lines.RemoveAt(0);
                var sites = lines;
                var msg = sites.Count > 0
                    ? $"Connected to {target.Host}. Remote IIS sites: {string.Join(", ", sites)}"
                    : $"Connected to {target.Host}, but no IIS sites were found (is IIS installed?).";
                return new RemoteConnectionTest(true, msg, sites);
            }

            var error = (stderr + stdout).Trim();
            if (string.IsNullOrEmpty(error)) error = $"powershell.exe exited with code {process.ExitCode}.";
            return new RemoteConnectionTest(false, $"Could not connect to {target.Host}: {error}", Array.Empty<string>());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new RemoteConnectionTest(false, $"Could not connect to {target.Host}: {ex.Message}", Array.Empty<string>());
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>Imports and binds the certificate on a single remote server.</summary>
    public async Task<RemoteDeploymentOutcome> DeployAsync(
        RemoteIisTarget target, byte[] pfxBytes, string pfxPassword,
        string? friendlyName, IReadOnlyList<string> domains,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target.Host))
            return new RemoteDeploymentOutcome(target, false, "The remote target has no host name.");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"lsw-remote-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, OrchestrationScript, ct);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var (key, value) in BuildEnvironment(target, pfxBytes, pfxPassword, friendlyName, domains))
                psi.Environment[key] = value;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start powershell.exe for remote deployment.");
            // Read both streams concurrently so a full stderr/stdout buffer can't deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                progress?.Report($"Deployed to {target.Host}.");
                _logger.LogInformation("Remote deployment to {Host} succeeded.", target.Host);
                return new RemoteDeploymentOutcome(target, true, null);
            }

            var error = (stderr + stdout).Trim();
            if (string.IsNullOrEmpty(error)) error = $"powershell.exe exited with code {process.ExitCode}.";
            _logger.LogError("Remote deployment to {Host} failed: {Error}", target.Host, error);
            return new RemoteDeploymentOutcome(target, false, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote deployment to {Host} threw.", target.Host);
            return new RemoteDeploymentOutcome(target, false, ex.Message);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// The local orchestration script. It reads every parameter from environment
    /// variables (so nothing is interpolated), then opens a WinRM session to the
    /// target — as the current identity, i.e. Kerberos — and runs the import/bind
    /// remotely. Emits a non-zero exit code and the error on stderr on failure.
    /// </summary>
    public const string OrchestrationScript = """
        $ErrorActionPreference = 'Stop'

        $targetHost = $env:LSW_HOST
        $port       = [int]$env:LSW_PORT
        $useSsl     = $env:LSW_SSL -eq '1'
        $pfxB64     = $env:LSW_PFX_B64
        $pfxPass    = $env:LSW_PFX_PASS
        $friendly   = $env:LSW_FRIENDLY
        $domains    = @($env:LSW_DOMAINS -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $sites      = @($env:LSW_SITES   -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })

        $remote = {
            param($PfxB64, $PfxPass, $FriendlyName, $Domains, $Sites)
            $ErrorActionPreference = 'Stop'
            $bytes = [Convert]::FromBase64String($PfxB64)
            $tmp = Join-Path $env:TEMP ([guid]::NewGuid().ToString('N') + '.pfx')
            [IO.File]::WriteAllBytes($tmp, $bytes)
            try {
                $secure = ConvertTo-SecureString -String $PfxPass -AsPlainText -Force
                $imported = @(Import-PfxCertificate -FilePath $tmp -CertStoreLocation 'Cert:\LocalMachine\My' -Password $secure -Exportable)
                $installed = $imported | Where-Object { $_.HasPrivateKey } | Select-Object -First 1
                if (-not $installed) { $installed = $imported | Select-Object -First 1 }

                if (-not [string]::IsNullOrWhiteSpace($FriendlyName)) {
                    $item = Get-Item -LiteralPath ("Cert:\LocalMachine\My\{0}" -f $installed.Thumbprint)
                    $item.FriendlyName = $FriendlyName
                }

                Get-ChildItem 'Cert:\LocalMachine\My' | Where-Object {
                    $_.Subject -eq $installed.Subject -and
                    $_.Thumbprint -ne $installed.Thumbprint -and
                    $_.NotAfter -le $installed.NotAfter
                } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Force -ErrorAction SilentlyContinue }

                if ($Sites.Count -gt 0) {
                    Import-Module WebAdministration -ErrorAction Stop
                    foreach ($site in $Sites) {
                        foreach ($d in $Domains) {
                            if ($d.StartsWith('*.')) { continue }
                            $existing = Get-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d -ErrorAction SilentlyContinue
                            if ($existing) { Remove-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d -ErrorAction SilentlyContinue }
                            New-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d -SslFlags 1 -ErrorAction Stop | Out-Null
                            $b = Get-WebBinding -Name $site -Protocol 'https' -Port 443 -HostHeader $d
                            $b.AddSslCertificate($installed.Thumbprint, 'My')
                        }
                    }
                }

                Write-Output ("DEPLOYED {0}" -f $installed.Thumbprint)
            } finally {
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            }
        }

        $icmArgs = @{
            ComputerName = $targetHost
            Port         = $port
            ScriptBlock  = $remote
            ArgumentList = @($pfxB64, $pfxPass, $friendly, $domains, $sites)
            ErrorAction  = 'Stop'
        }
        if ($useSsl) { $icmArgs['UseSSL'] = $true }

        try {
            Invoke-Command @icmArgs
            exit 0
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    /// <summary>
    /// WinRM connectivity pre-flight: connects and returns the remote IIS site
    /// names (one per line after an "OK" marker), or exits non-zero with the error.
    /// </summary>
    public const string TestScript = """
        $ErrorActionPreference = 'Stop'
        $targetHost = $env:LSW_HOST
        $port       = [int]$env:LSW_PORT
        $useSsl     = $env:LSW_SSL -eq '1'

        $remote = {
            try { Import-Module WebAdministration -ErrorAction Stop; @(Get-Website | ForEach-Object { $_.Name }) }
            catch { @() }
        }

        $icmArgs = @{ ComputerName = $targetHost; Port = $port; ScriptBlock = $remote; ErrorAction = 'Stop' }
        if ($useSsl) { $icmArgs['UseSSL'] = $true }

        try {
            $sites = @(Invoke-Command @icmArgs)
            Write-Output 'OK'
            $sites | ForEach-Object { Write-Output $_ }
            exit 0
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;
}
