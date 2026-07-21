using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetsSSL.App.Service;

/// <summary>Builds and runs the Windows Service host (LetsSSL4Windows.exe --service).</summary>
[SupportedOSPlatform("windows")]
internal static class ServiceHost
{
    public static void Run(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // Use the SCM internal service name (the one sc.exe registered), not the
        // display name, so the hosted ServiceBase.ServiceName matches.
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);

        // AddWindowsService adds its own Event Log provider (default source = the
        // app name, level Warning). Replace it with the shared LetsSSL4Windows
        // source so service activity shows up next to the GUI's in Event Viewer.
        builder.Logging.ClearProviders();
        AppLogging.Configure(builder.Logging);

        builder.Services.AddHostedService<RenewalWorker>();
        builder.Build().Run();
    }
}
