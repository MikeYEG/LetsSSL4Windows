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

    /// <summary>
    /// Writes a backup archive of the store to <paramref name="destinationZipPath"/>.
    /// Captures everything under the store root (settings, certificate list, ACME
    /// account keys for both editions — <c>accounts/</c> and <c>posh-acme/</c> —
    /// and optionally <c>pfx/</c>), excluding the <c>logs/</c> folder.
    /// </summary>
    public void Create(string destinationZipPath, bool includePfx = true)
    {
        var root = Path.GetFullPath(_paths.RootDir);
        var tmp = destinationZipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        // The destination (and its temp) may sit inside the store root — never
        // archive them, or a previous backup would bloat/corrupt the new one.
        var destFull = Path.GetFullPath(destinationZipPath);
        var tmpFull = Path.GetFullPath(tmp);

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                if (string.Equals(full, destFull, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, tmpFull, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var top = relative.Split('/')[0];
                if (string.Equals(top, "logs", StringComparison.OrdinalIgnoreCase)) continue;
                if (!includePfx && string.Equals(top, "pfx", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(file, relative);
            }
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
}
