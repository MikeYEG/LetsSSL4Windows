using System.Diagnostics;
using System.Runtime.Versioning;

namespace LetsSSL.App.Service;

/// <summary>
/// Registers/removes the Windows Service using sc.exe (run elevated). The service
/// runs this same executable with the --service argument.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceInstaller
{
    public const string ServiceName = "LetsSSL4WindowsRenewalService";
    public const string DisplayName = "LetsSSL4Windows Renewal Service";

    // A legacy scheduled task used by older versions for renewal; removed on install
    // so two renewers never run concurrently after an upgrade.
    private const string OverlappingTaskName = "LetsSSL4Windows Certificate Renewal";

    public static int Install()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine executable path.");

        // Register the Event Log source now, while we're elevated, so the service
        // (and the GUI) can write to Event Viewer without needing admin at run time.
        if (!AppLogging.EnsureEventSource())
            Console.WriteLine("Warning: could not register the 'LetsSSL4Windows' Event Log source; " +
                              "Event Viewer logging may be unavailable until it can be created.");

        Console.WriteLine($"Installing service \"{DisplayName}\"…");
        var create = Run("sc.exe",
            $"create {ServiceName} binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"{DisplayName}\"");
        if (create.ExitCode != 0)
        {
            Console.WriteLine("Failed to create service (run as Administrator).");
            return create.ExitCode;
        }

        Run("sc.exe", $"description {ServiceName} \"Automatically renews SSL/TLS certificates managed by LetsSSL4Windows.\"");
        RemoveOverlappingScheduledTask();
        Run("sc.exe", $"start {ServiceName}");
        Console.WriteLine("Service installed and started. This is now the active renewer.");
        return 0;
    }

    public static int Uninstall()
    {
        Console.WriteLine($"Removing service \"{DisplayName}\"…");
        Run("sc.exe", $"stop {ServiceName}");
        var delete = Run("sc.exe", $"delete {ServiceName}");
        Console.WriteLine(delete.ExitCode == 0 ? "Service removed." : "Failed to remove service.");
        return delete.ExitCode;
    }

    private static void RemoveOverlappingScheduledTask()
    {
        var query = Run("schtasks.exe", $"/Query /TN \"{OverlappingTaskName}\"", echo: false);
        if (query.ExitCode != 0) return;
        Run("schtasks.exe", $"/Delete /TN \"{OverlappingTaskName}\" /F", echo: false);
        Console.WriteLine("Removed a legacy renewal scheduled task to avoid duplicate renewals.");
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments, bool echo = true)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        if (echo && !string.IsNullOrEmpty(output)) Console.WriteLine(output);
        return (p.ExitCode, output);
    }
}
