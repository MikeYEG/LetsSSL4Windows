using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LetsSSL.App.Service;

/// <summary>Builds and runs the Windows Service host (LetsSSL4Windows.exe --service).</summary>
[SupportedOSPlatform("windows")]
internal static class ServiceHost
{
    public static void Run(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.DisplayName);
        builder.Services.AddHostedService<RenewalWorker>();
        builder.Build().Run();
    }
}
