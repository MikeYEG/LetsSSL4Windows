using System.Text;
using System.Text.Json;
using LetsSSL.Core.Iis;
using LetsSSL.Core.Models;
using Xunit;

namespace LetsSSL.Core.Tests;

public class RemoteIisTargetTests
{
    [Fact]
    public void ManagedCertificate_has_no_remote_targets_by_default()
    {
        var cert = new ManagedCertificate();
        Assert.NotNull(cert.RemoteTargets);
        Assert.Empty(cert.RemoteTargets);
    }

    [Fact]
    public void RemoteTarget_defaults_to_winrm_https()
    {
        var target = new RemoteIisTarget();
        Assert.Equal(5986, target.WinRmPort);
        Assert.True(target.UseSsl);
        Assert.Empty(target.SiteNames);
    }

    [Fact]
    public void RemoteTargets_round_trip_through_json()
    {
        var cert = new ManagedCertificate
        {
            PrimaryDomain = "example.com",
            RemoteTargets =
            {
                new RemoteIisTarget { Host = "web2.corp.local", WinRmPort = 5985, UseSsl = false, SiteNames = { "Default Web Site", "api" } },
            },
        };

        var json = JsonSerializer.Serialize(cert);
        var restored = JsonSerializer.Deserialize<ManagedCertificate>(json)!;

        var target = Assert.Single(restored.RemoteTargets);
        Assert.Equal("web2.corp.local", target.Host);
        Assert.Equal(5985, target.WinRmPort);
        Assert.False(target.UseSsl);
        Assert.Equal(new[] { "Default Web Site", "api" }, target.SiteNames);
    }

    [Fact]
    public void Legacy_json_without_remote_targets_deserializes_to_empty_list()
    {
        // A certificate record saved before the feature existed has no field.
        const string legacy = """{ "PrimaryDomain": "example.com" }""";
        var cert = JsonSerializer.Deserialize<ManagedCertificate>(legacy)!;
        Assert.NotNull(cert.RemoteTargets);
        Assert.Empty(cert.RemoteTargets);
    }

    [Fact]
    public void BuildEnvironment_maps_target_and_inputs()
    {
        var target = new RemoteIisTarget { Host = "web2", WinRmPort = 5986, UseSsl = true, SiteNames = { "Default Web Site", "api" } };
        var pfx = Encoding.UTF8.GetBytes("pfx-bytes");

        var env = RemoteIisDeployer.BuildEnvironment(
            target, pfx, "secret", "example.com (LE)", new[] { "example.com", "www.example.com" });

        Assert.Equal("web2", env["LSW_HOST"]);
        Assert.Equal("5986", env["LSW_PORT"]);
        Assert.Equal("1", env["LSW_SSL"]);
        Assert.Equal(Convert.ToBase64String(pfx), env["LSW_PFX_B64"]);
        Assert.Equal("secret", env["LSW_PFX_PASS"]);
        Assert.Equal("example.com (LE)", env["LSW_FRIENDLY"]);
        Assert.Equal("example.com\nwww.example.com", env["LSW_DOMAINS"]);
        Assert.Equal("Default Web Site\napi", env["LSW_SITES"]);
    }

    [Fact]
    public void BuildEnvironment_uses_zero_flag_when_ssl_disabled_and_blank_friendly()
    {
        var target = new RemoteIisTarget { Host = "web3", UseSsl = false };
        var env = RemoteIisDeployer.BuildEnvironment(target, Array.Empty<byte>(), "pw", null, Array.Empty<string>());

        Assert.Equal("0", env["LSW_SSL"]);
        Assert.Equal(string.Empty, env["LSW_FRIENDLY"]);
        Assert.Equal(string.Empty, env["LSW_SITES"]);
        Assert.Equal(string.Empty, env["LSW_DOMAINS"]);
    }
}
