using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace LetsSSL.App;

/// <summary>
/// Central logging configuration for every host mode (GUI, tray, service). All
/// three write to the Windows Event Log under a single <see cref="EventSource"/>
/// so activity is visible in Event Viewer (Windows Logs → Application, filtered
/// by the "LetsSSL4Windows" source).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AppLogging
{
    /// <summary>Event source name shown in Event Viewer's "Source" column.</summary>
    public const string EventSource = "LetsSSL4Windows";

    /// <summary>The event log the source writes to.</summary>
    public const string EventLogName = "Application";

    // Lazy with the default (thread-safe) mode guarantees the factory — and thus
    // the Event Log provider — is created exactly once even under concurrent access.
    private static readonly Lazy<ILoggerFactory> LazyFactory = new(CreateFactory);

    /// <summary>
    /// Shared, app-lifetime logger factory for the GUI and tray. Created once and
    /// reused so a single Event Log provider is registered per process.
    /// </summary>
    public static ILoggerFactory Factory => LazyFactory.Value;

    /// <summary>
    /// Ensures the event source exists so log writes succeed, returning whether it
    /// is now available. Creating a source requires administrator rights (a
    /// one-time operation); the GUI runs elevated, and the service installer and
    /// service both run elevated, so this normally succeeds there. When it can't
    /// (e.g. the un-elevated tray before the source has ever been created), the
    /// caller skips the Event Log provider so a write never throws.
    /// </summary>
    public static bool EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EventSource))
                EventLog.CreateEventSource(new EventSourceCreationData(EventSource, EventLogName));
            return true;
        }
        catch
        {
            // Requires admin, or the security log isn't readable — leave it to a
            // later elevated run. Logging falls back to no-op until the source exists.
            return false;
        }
    }

    /// <summary>Applies the shared filters + Event Log provider to a logging builder.</summary>
    public static void Configure(ILoggingBuilder builder)
    {
        // Keep Event Viewer readable: our own categories at Information, everything
        // else (framework/hosting noise) only at Warning and above.
        builder.SetMinimumLevel(LogLevel.Warning);
        builder.AddFilter("LetsSSL", LogLevel.Information);

        // Only attach the provider if the source is registered; otherwise a write
        // from a non-elevated process would throw trying to auto-create it.
        if (EnsureEventSource())
        {
            builder.AddEventLog(settings =>
            {
                settings.SourceName = EventSource;
                settings.LogName = EventLogName;
            });
        }
    }

    private static ILoggerFactory CreateFactory() => LoggerFactory.Create(Configure);
}
