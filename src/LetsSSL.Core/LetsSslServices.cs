using System.Runtime.Versioning;
using LetsSSL.Core.Acme;
using LetsSSL.Core.Deployment;
using LetsSSL.Core.Dns;
using LetsSSL.Core.Notifications;
using LetsSSL.Core.Renewal;
using LetsSSL.Core.Storage;
using LetsSSL.Core.Updates;
using Microsoft.Extensions.Logging;

namespace LetsSSL.Core;

/// <summary>
/// Composition root that wires the Core services together. Both the GUI and the
/// renewal agent create one of these so they share the same data store.
/// </summary>
[SupportedOSPlatform("windows")]
public class LetsSslServices
{
    public AppPaths Paths { get; }
    public SettingsRepository Settings { get; }
    public CertificateRepository Certificates { get; }
    public AcmeService Acme { get; }
    public WindowsCertificateStore Store { get; }
    public DeploymentTaskRunner Deployment { get; }
    public NotificationService Notifications { get; }
    public RenewalStatusStore RenewalStatusStore { get; }
    public UpdateChecker Updates { get; }
    public RenewalInfoClient RenewalInfo { get; }
    public CertificateManager Manager { get; }
    public RenewalService Renewal { get; }

    /// <param name="manualDns">
    /// Interactive handler for manual DNS validation. The GUI supplies one; the
    /// unattended renewal agent passes null (manual DNS then fails fast).
    /// </param>
    public LetsSslServices(
        ILoggerFactory? loggerFactory = null,
        string? rootDir = null,
        IManualDnsInteraction? manualDns = null)
    {
        var lf = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        Paths = new AppPaths(rootDir);
        Paths.EnsureCreated();
        Settings = new SettingsRepository(Paths);
        Certificates = new CertificateRepository(Paths);
        Store = new WindowsCertificateStore();
        Acme = new AcmeService(Paths, lf.CreateLogger<AcmeService>());
        Deployment = new DeploymentTaskRunner(lf.CreateLogger<DeploymentTaskRunner>());
        Notifications = new NotificationService(Settings, lf.CreateLogger<NotificationService>());
        RenewalStatusStore = new RenewalStatusStore(Paths);
        Updates = new UpdateChecker();
        RenewalInfo = new RenewalInfoClient(logger: lf.CreateLogger<RenewalInfoClient>());
        Manager = new CertificateManager(Paths, Certificates, Acme, Store, Deployment, manualDns,
            Notifications, lf.CreateLogger<CertificateManager>());
        Renewal = new RenewalService(Certificates, Manager, RenewalStatusStore, Store, RenewalInfo,
            lf.CreateLogger<RenewalService>());
    }
}
