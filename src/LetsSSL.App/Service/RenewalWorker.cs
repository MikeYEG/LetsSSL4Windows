using System.Runtime.Versioning;
using LetsSSL.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetsSSL.App.Service;

/// <summary>
/// Long-running background worker that renews due certificates on an interval,
/// hosted by the Windows Service (LetsSSL4Windows.exe --service).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RenewalWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly ILogger<RenewalWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public RenewalWorker(ILogger<RenewalWorker> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LetsSSL4Windows renewal service started (checking every {Hours}h).", CheckInterval.TotalHours);
        var services = new LetsSslServices(_loggerFactory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = services.Settings.Load();
                if (settings.EnableAutoRenewal)
                {
                    _logger.LogInformation("Checking certificates for renewal…");
                    var outcomes = await services.Renewal.RenewDueAsync(settings.Environment, progress: null, stoppingToken);
                    var failed = outcomes.Count(o => !o.Succeeded);
                    _logger.LogInformation("Renewal cycle complete: {Ok} ok, {Failed} failed.",
                        outcomes.Count - failed, failed);
                }
                else
                {
                    _logger.LogInformation("Automatic renewal is disabled in settings; skipping cycle.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renewal cycle failed.");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("LetsSSL4Windows renewal service stopping.");
    }
}
