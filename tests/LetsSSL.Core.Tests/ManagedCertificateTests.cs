using LetsSSL.Core.Models;
using Xunit;

namespace LetsSSL.Core.Tests;

public class ManagedCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Status_is_NotRequested_when_no_certificate_issued()
    {
        var cert = new ManagedCertificate { PrimaryDomain = "example.com" };
        Assert.Equal(CertificateStatus.NotRequested, cert.GetStatus(Now));
    }

    [Fact]
    public void Status_is_Valid_when_far_from_expiry()
    {
        var cert = new ManagedCertificate { NotAfter = Now.AddDays(60), RenewalDaysBeforeExpiry = 30 };
        Assert.Equal(CertificateStatus.Valid, cert.GetStatus(Now));
    }

    [Fact]
    public void Status_is_ExpiringSoon_inside_renewal_window()
    {
        var cert = new ManagedCertificate { NotAfter = Now.AddDays(10), RenewalDaysBeforeExpiry = 30 };
        Assert.Equal(CertificateStatus.ExpiringSoon, cert.GetStatus(Now));
    }

    [Fact]
    public void Status_is_Expired_after_NotAfter()
    {
        var cert = new ManagedCertificate { NotAfter = Now.AddDays(-1) };
        Assert.Equal(CertificateStatus.Expired, cert.GetStatus(Now));
    }

    [Fact]
    public void Status_is_Error_when_failed_and_never_issued()
    {
        var cert = new ManagedCertificate { LastError = "boom" };
        Assert.Equal(CertificateStatus.Error, cert.GetStatus(Now));
    }

    [Theory]
    [InlineData(60, true, false)]   // valid, auto-renew on -> not due
    [InlineData(10, true, true)]    // expiring soon -> due
    [InlineData(10, false, false)]  // expiring soon, auto-renew off -> not due
    public void IsDueForRenewal_respects_window_and_flag(int daysToExpiry, bool autoRenew, bool expectedDue)
    {
        var cert = new ManagedCertificate
        {
            NotAfter = Now.AddDays(daysToExpiry),
            RenewalDaysBeforeExpiry = 30,
            AutoRenew = autoRenew,
        };
        Assert.Equal(expectedDue, cert.IsDueForRenewal(Now));
    }

    [Fact]
    public void AllDomains_puts_primary_first_and_dedupes_case_insensitively()
    {
        var cert = new ManagedCertificate
        {
            PrimaryDomain = "example.com",
            SubjectAlternativeNames = new() { "www.example.com", "EXAMPLE.COM", "api.example.com" },
        };
        Assert.Equal(new[] { "example.com", "www.example.com", "api.example.com" }, cert.AllDomains);
    }

    [Fact]
    public void DeploymentTaskConfig_Get_is_case_insensitive_and_null_safe()
    {
        var config = new DeploymentTaskConfig { Type = DeploymentTaskType.ExportPfx };
        config.Settings["Path"] = @"C:\certs\out.pfx";
        Assert.Equal(@"C:\certs\out.pfx", config.Get("path"));
        Assert.Null(config.Get("missing"));
    }
}
