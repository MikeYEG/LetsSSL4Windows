using System.IO.Compression;
using LetsSSL.Core.Storage;

namespace LetsSSL.Core;

/// <summary>
/// Backs up and restores the LetsSSL4Windows data store (settings, the managed
/// certificate list, ACME account keys, and optionally the saved PFX files) as a
/// single .zip, for disaster recovery or moving to a new machine.
///
/// Caveat: DPAPI-protected secrets (DNS API tokens, SMTP password) are encrypted
/// with the LocalMachine key, so they can only be decrypted on the machine that
/// created them. Restoring onto a different machine keeps everything else but
/// those secrets must be re-entered.
/// </summary>
public sealed class ConfigBackup
{
    private readonly AppPaths _paths;

    public ConfigBackup(AppPaths paths) => _paths = paths;

    /// <summary>Writes a backup archive of the store to <paramref name="destinationZipPath"/>.</summary>
    public void Create(string destinationZipPath, bool includePfx = true)
    {
        var tmp = destinationZipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            AddFile(zip, _paths.SettingsFile, "appsettings.json");
            AddFile(zip, _paths.CertificatesFile, "certificates.json");
            AddFile(zip, _paths.RenewalStatusFile, "lastrun.json");
            AddDirectory(zip, _paths.AccountsDir, "accounts");
            if (includePfx) AddDirectory(zip, _paths.PfxDir, "pfx");
        }

        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);
        File.Move(tmp, destinationZipPath);
    }

    /// <summary>
    /// Restores a backup archive into the store, overwriting existing files.
    /// Returns the number of files restored.
    /// </summary>
    public int Restore(string sourceZipPath)
    {
        _paths.EnsureCreated();
        var root = Path.GetFullPath(_paths.RootDir);
        var restored = 0;

        using var zip = ZipFile.OpenRead(sourceZipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory marker

            var destPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
            // Guard against path traversal ("zip slip") — stay within the store root.
            if (!destPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destPath, root, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
            restored++;
        }
        return restored;
    }

    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        if (File.Exists(path)) zip.CreateEntryFromFile(path, entryName);
    }

    private static void AddDirectory(ZipArchive zip, string dir, string entryPrefix)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir))
            zip.CreateEntryFromFile(file, $"{entryPrefix}/{Path.GetFileName(file)}");
    }
}
