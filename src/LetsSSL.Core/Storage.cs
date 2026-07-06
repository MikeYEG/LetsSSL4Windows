using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LetsSSL.Core.Models;

namespace LetsSSL.Core.Storage;

/// <summary>
/// Resolves and creates the directories the application uses under
/// %ProgramData%\LetsSSL4Windows so the GUI and renewal service share one store.
/// </summary>
public class AppPaths
{
    public string RootDir { get; }
    public string PfxDir { get; }
    public string AccountsDir { get; }
    public string LogsDir { get; }

    public string SettingsFile => Path.Combine(RootDir, "appsettings.json");
    public string CertificatesFile => Path.Combine(RootDir, "certificates.json");
    public string RenewalStatusFile => Path.Combine(RootDir, "lastrun.json");

    public AppPaths(string? rootDir = null)
    {
        RootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LetsSSL4Windows");
        PfxDir = Path.Combine(RootDir, "pfx");
        AccountsDir = Path.Combine(RootDir, "accounts");
        LogsDir = Path.Combine(RootDir, "logs");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(PfxDir);
        Directory.CreateDirectory(AccountsDir);
        Directory.CreateDirectory(LogsDir);
    }

    public string AccountKeyFile(string environmentName) =>
        Path.Combine(AccountsDir, $"account-{environmentName.ToLowerInvariant()}.pem");

    public string PfxFileFor(string certificateId) =>
        Path.Combine(PfxDir, $"{certificateId}.pfx");
}

/// <summary>Minimal, atomic JSON read/write helper used by the data stores.</summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static T Read<T>(string path, Func<T> createDefault) where T : class
    {
        if (!File.Exists(path)) return createDefault();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? createDefault();
        }
        catch (JsonException)
        {
            return createDefault();
        }
    }

    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, Options);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}

/// <summary>Persists the list of managed certificates to certificates.json.</summary>
public class CertificateRepository
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public CertificateRepository(AppPaths paths) => _paths = paths;

    public List<ManagedCertificate> GetAll()
    {
        lock (_gate)
            return JsonFile.Read(_paths.CertificatesFile, () => new List<ManagedCertificate>());
    }

    public ManagedCertificate? GetById(string id) => GetAll().FirstOrDefault(c => c.Id == id);

    public void Upsert(ManagedCertificate cert)
    {
        lock (_gate)
        {
            var all = JsonFile.Read(_paths.CertificatesFile, () => new List<ManagedCertificate>());
            var idx = all.FindIndex(c => c.Id == cert.Id);
            if (idx >= 0) all[idx] = cert; else all.Add(cert);
            JsonFile.Write(_paths.CertificatesFile, all);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var all = JsonFile.Read(_paths.CertificatesFile, () => new List<ManagedCertificate>());
            all.RemoveAll(c => c.Id == id);
            JsonFile.Write(_paths.CertificatesFile, all);
        }
    }
}

/// <summary>Loads and saves global <see cref="AppSettings"/>.</summary>
public class SettingsRepository
{
    private readonly AppPaths _paths;
    public SettingsRepository(AppPaths paths) => _paths = paths;
    public AppSettings Load() => JsonFile.Read(_paths.SettingsFile, () => new AppSettings());
    public void Save(AppSettings settings) => JsonFile.Write(_paths.SettingsFile, settings);
}

/// <summary>Reads/writes the last renewal run summary (shared by the service and GUI).</summary>
public class RenewalStatusStore
{
    private readonly AppPaths _paths;
    public RenewalStatusStore(AppPaths paths) => _paths = paths;
    public RenewalStatus Load() => JsonFile.Read(_paths.RenewalStatusFile, () => new RenewalStatus());
    public void Save(RenewalStatus status) => JsonFile.Write(_paths.RenewalStatusFile, status);
}

/// <summary>
/// Encrypts small secrets (DNS API tokens, SMTP passwords) at rest using Windows
/// DPAPI with the LocalMachine scope, so both the elevated GUI and the SYSTEM
/// renewal service on the same machine can decrypt them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    private const string Prefix = "DPAPI:";

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var encrypted = Convert.FromBase64String(stored[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.LocalMachine));
    }
}

/// <summary>
/// Imports and removes certificates in the Windows LocalMachine\My store, where
/// IIS expects to find certificates for HTTPS bindings.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsCertificateStore
{
    private readonly StoreName _storeName;
    private readonly StoreLocation _storeLocation;

    public WindowsCertificateStore(StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.LocalMachine)
    {
        _storeName = storeName;
        _storeLocation = storeLocation;
    }

    /// <summary>The store name as IIS expects it (e.g. "MY").</summary>
    public string StoreNameForIis => _storeName.ToString().ToUpperInvariant();

    public X509Certificate2 ImportPfx(byte[] pfxBytes, string password, bool removeOlderWithSameSubject = true)
    {
        var cert = new X509Certificate2(pfxBytes, password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        using var store = new X509Store(_storeName, _storeLocation);
        store.Open(OpenFlags.ReadWrite);

        if (removeOlderWithSameSubject)
        {
            foreach (var existing in store.Certificates)
            {
                if (string.Equals(existing.Subject, cert.Subject, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(existing.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase)
                    && existing.NotAfter <= cert.NotAfter)
                {
                    store.Remove(existing);
                }
            }
        }

        store.Add(cert);
        return cert;
    }

    public void RemoveByThumbprint(string thumbprint)
    {
        using var store = new X509Store(_storeName, _storeLocation);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false))
            store.Remove(c);
    }

    /// <summary>Returns the installed certificate with the given thumbprint, or null.</summary>
    public X509Certificate2? FindByThumbprint(string thumbprint)
    {
        using var store = new X509Store(_storeName, _storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return matches.Count > 0 ? matches[0] : null;
    }
}

/// <summary>
/// Exports an installed certificate (which was imported as exportable) on demand
/// to a PFX or to PEM cert/key files.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CertificateExporter
{
    /// <summary>Writes a password-protected PFX (certificate + private key).</summary>
    public static void ExportPfx(X509Certificate2 cert, string path, string? password)
    {
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password ?? string.Empty));
    }

    /// <summary>Writes the certificate (PEM) and its private key (PKCS#8 PEM) to two files.</summary>
    public static void ExportPem(X509Certificate2 cert, string certPemPath, string keyPemPath)
    {
        File.WriteAllText(certPemPath, cert.ExportCertificatePem());

        string? keyPem;
        using (var rsa = cert.GetRSAPrivateKey())
            keyPem = rsa?.ExportPkcs8PrivateKeyPem();
        if (keyPem is null)
        {
            using var ecdsa = cert.GetECDsaPrivateKey();
            keyPem = ecdsa?.ExportPkcs8PrivateKeyPem();
        }
        if (keyPem is null)
            throw new InvalidOperationException("The certificate has no exportable private key.");

        File.WriteAllText(keyPemPath, keyPem);
    }
}
