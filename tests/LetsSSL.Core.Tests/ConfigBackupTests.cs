using LetsSSL.Core;
using LetsSSL.Core.Storage;
using Xunit;

namespace LetsSSL.Core.Tests;

public class ConfigBackupTests
{
    [Fact]
    public void Backup_then_restore_round_trips_store_and_excludes_logs()
    {
        var root = Path.Combine(Path.GetTempPath(), "lsw-test-" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(Path.GetTempPath(), "lsw-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        var paths = new AppPaths(root);
        var backup = new ConfigBackup(paths);

        try
        {
            paths.EnsureCreated();
            File.WriteAllText(paths.CertificatesFile, "[]");
            File.WriteAllText(Path.Combine(paths.AccountsDir, "account-prod.pem"), "KEY");
            File.WriteAllText(Path.Combine(paths.LogsDir, "activity.log"), "noise"); // must be excluded

            backup.Create(zip);

            // Wipe the store, then restore from the archive.
            Directory.Delete(root, recursive: true);
            var restored = backup.Restore(zip);

            Assert.Equal(2, restored); // certificates.json + the account key (logs excluded)
            Assert.Equal("[]", File.ReadAllText(paths.CertificatesFile));
            Assert.Equal("KEY", File.ReadAllText(Path.Combine(paths.AccountsDir, "account-prod.pem")));
            Assert.False(File.Exists(Path.Combine(paths.LogsDir, "activity.log")));
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
