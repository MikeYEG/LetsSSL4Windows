using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using LetsSSL.App.Service;
using LetsSSL.App.Tray;
using WinFormsApp = System.Windows.Forms.Application;

namespace LetsSSL.App;

/// <summary>
/// Single entry point for the whole product. The same LetsSSL4Windows.exe runs as
/// the GUI (default), the system tray (--tray), or the Windows Service (--service),
/// and can install/remove the service (--install-service / --uninstall-service).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (HasArg(args, "--install-service")) { AttachConsole(-1); return ServiceInstaller.Install(); }
        if (HasArg(args, "--uninstall-service")) { AttachConsole(-1); return ServiceInstaller.Uninstall(); }
        if (HasArg(args, "--service")) { ServiceHost.Run(args); return 0; }
        if (HasArg(args, "--tray")) { RunTray(); return 0; }
        return RunGui();
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int RunGui()
    {
        // The dashboard manages the machine certificate store and IIS, which need
        // administrator rights — relaunch elevated if we aren't already.
        if (!IsAdministrator())
        {
            RelaunchElevated(arguments: null);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run(new MainWindow());
    }

    private static void RunTray()
    {
        WinFormsApp.EnableVisualStyles();
        WinFormsApp.SetCompatibleTextRenderingDefault(false);
        WinFormsApp.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        WinFormsApp.Run(new TrayApplicationContext());
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated(string? arguments)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = arguments ?? string.Empty,
        };
        try { Process.Start(psi); }
        catch (Win32Exception) { /* user dismissed the UAC prompt */ }
    }
}
