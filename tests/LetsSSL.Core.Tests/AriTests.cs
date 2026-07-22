using LetsSSL.Core.Acme;
using LetsSSL.Core.Models;
using Xunit;

namespace LetsSSL.Core.Tests;

public class AriTests
{
    [Fact]
    public void CertId_matches_RFC9773_example()
    {
        // RFC 9773 §4.1 worked example: an Authority Key Identifier keyIdentifier
        // and serial number that produce a known CertID string.
        var aki = new byte[]
        {
            0x69, 0x88, 0x5b, 0x6b, 0x87, 0x46, 0x40, 0x41, 0xe1, 0xb3,
            0x7b, 0x84, 0x7b, 0xa0, 0xae, 0x2c, 0xde, 0x01, 0xc8, 0xd4,
        };
        var serial = new byte[] { 0x00, 0x87, 0x65, 0x43, 0x21 };

        var certId = AriCertId.Compute(aki, serial);

        Assert.Equal("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", certId);
    }

    [Fact]
    public void CertId_uses_url_safe_base64_without_padding()
    {
        // Bytes chosen so standard base64 would contain '+', '/', and '=' padding.
        var value = new byte[] { 0xfb, 0xff, 0xbf };   // base64 "+/+/" -> url "-_-_"
        var certId = AriCertId.Compute(value, value);

        Assert.DoesNotContain('+', certId);
        Assert.DoesNotContain('/', certId);
        Assert.DoesNotContain('=', certId);
        Assert.Equal("-_-_.-_-_", certId);
    }

    [Fact]
    public void IsDueForRenewal_is_true_once_ARI_time_passes_even_before_the_date_window()
    {
        var now = DateTimeOffset.UtcNow;
        var cert = new ManagedCertificate
        {
            AutoRenew = true,
            NotBefore = now.AddDays(-1),
            NotAfter = now.AddDays(80),               // far from the 30-day window
            RenewalDaysBeforeExpiry = 30,
            AriRenewalTime = now.AddMinutes(-5),      // CA says: renew now
        };

        Assert.Equal(CertificateStatus.Valid, cert.GetStatus(now));   // date-wise, still valid
        Assert.True(cert.IsDueForRenewal(now));                       // but ARI pulls it forward
    }

    [Fact]
    public void IsDueForRenewal_ignores_a_future_ARI_time()
    {
        var now = DateTimeOffset.UtcNow;
        var cert = new ManagedCertificate
        {
            AutoRenew = true,
            NotBefore = now.AddDays(-1),
            NotAfter = now.AddDays(80),
            RenewalDaysBeforeExpiry = 30,
            AriRenewalTime = now.AddDays(40),         // CA window not reached yet
        };

        Assert.False(cert.IsDueForRenewal(now));
    }

    [Fact]
    public void IsDueForRenewal_ignores_ARI_time_when_certificate_not_issued()
    {
        var now = DateTimeOffset.UtcNow;
        var cert = new ManagedCertificate
        {
            AutoRenew = true,
            NotAfter = null,                          // never issued
            AriRenewalTime = now.AddMinutes(-5),
        };

        // Still due, but because it's NotRequested — not because of a stale ARI time.
        Assert.Equal(CertificateStatus.NotRequested, cert.GetStatus(now));
        Assert.True(cert.IsDueForRenewal(now));
    }

    [Fact]
    public void IsDueForRenewal_respects_AutoRenew_off_regardless_of_ARI()
    {
        var now = DateTimeOffset.UtcNow;
        var cert = new ManagedCertificate
        {
            AutoRenew = false,
            NotBefore = now.AddDays(-1),
            NotAfter = now.AddDays(80),
            AriRenewalTime = now.AddMinutes(-5),
        };

        Assert.False(cert.IsDueForRenewal(now));
    }
}
