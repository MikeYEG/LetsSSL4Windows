using System.Diagnostics;
using System.IO;
using LetsSSL.Core.Updates;

namespace LetsSSL.App.Services;

/// <summary>Downloads a release's installer asset and launches it.</summary>
internal static class Updater
{
    public static async Task DownloadAndRunAsync(UpdateChecker checker, UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl))
            throw new InvalidOperationException("This release doesn't include an installer download.");

        var fileName = Path.GetFileName(new Uri(info.InstallerUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "LetsSSL4Windows-Setup.exe";
        var dest = Path.Combine(Path.GetTempPath(), fileName);

        await checker.DownloadAsync(info.InstallerUrl, dest);

        // Launch the installer; its own CloseApplications step closes this app to update it.
        Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
    }
}
