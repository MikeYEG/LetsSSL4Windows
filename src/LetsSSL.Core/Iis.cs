using System.Runtime.Versioning;
using Microsoft.Web.Administration;

namespace LetsSSL.Core.Iis;

/// <summary>A lightweight view of an IIS site for display in the UI.</summary>
public class IisSiteInfo
{
    public string Name { get; set; } = string.Empty;
    public string? PhysicalPath { get; set; }
    public List<string> Bindings { get; set; } = new();
    public bool HasHttpsBinding { get; set; }
}

/// <summary>
/// Reads IIS site information and binds certificates to HTTPS bindings using
/// the server-side IIS configuration. SNI lets multiple certificates share :443.
/// </summary>
[SupportedOSPlatform("windows")]
public class IisManager
{
    /// <summary>True if IIS configuration can be opened on this machine.</summary>
    public static bool IsIisAvailable()
    {
        try
        {
            using var sm = new ServerManager();
            _ = sm.Sites.Count;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<IisSiteInfo> GetSites()
    {
        using var sm = new ServerManager();
        var result = new List<IisSiteInfo>();
        foreach (var site in sm.Sites)
        {
            var info = new IisSiteInfo { Name = site.Name };
            var root = site.Applications["/"]?.VirtualDirectories["/"];
            info.PhysicalPath = root?.PhysicalPath;
            foreach (var b in site.Bindings)
            {
                info.Bindings.Add($"{b.Protocol}://{b.BindingInformation}");
                if (string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase))
                    info.HasHttpsBinding = true;
            }
            result.Add(info);
        }
        return result;
    }

    /// <summary>Resolves the on-disk physical path of a site's root application.</summary>
    public string? GetSitePhysicalPath(string siteName)
    {
        using var sm = new ServerManager();
        var site = sm.Sites[siteName];
        var vdir = site?.Applications["/"]?.VirtualDirectories["/"];
        return ExpandPath(vdir?.PhysicalPath);
    }

    /// <summary>
    /// Ensures the site has an HTTPS binding for <paramref name="hostName"/> on the
    /// given port, pointing at the certificate identified by <paramref name="certHash"/>.
    /// Existing matching bindings are replaced.
    /// </summary>
    public void BindCertificate(string siteName, string hostName, byte[] certHash, string storeName = "MY", int port = 443)
    {
        using var sm = new ServerManager();
        var site = sm.Sites[siteName]
            ?? throw new InvalidOperationException($"IIS site '{siteName}' was not found.");

        var bindingInfo = $"*:{port}:{hostName}";
        var existing = site.Bindings.FirstOrDefault(b =>
            string.Equals(b.Protocol, "https", StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.BindingInformation, bindingInfo, StringComparison.OrdinalIgnoreCase));
        if (existing != null) site.Bindings.Remove(existing);

        var binding = site.Bindings.Add(bindingInfo, certHash, storeName);
        if (!string.IsNullOrEmpty(hostName))
            binding.SetAttributeValue("sslFlags", 1 /* SslFlags.Sni */);

        sm.CommitChanges();
    }

    private static string? ExpandPath(string? path) =>
        string.IsNullOrEmpty(path) ? path : Environment.ExpandEnvironmentVariables(path);
}
