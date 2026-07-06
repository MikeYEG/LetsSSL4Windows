using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LetsSSL.App;

/// <summary>Applies a dark or light OS title bar to WPF windows via DWM (Windows 10 2004+).</summary>
internal static class Theming
{
    private const int DwmwaUseImmersiveDarkMode = 20;        // Win10 20H1+ / Win11
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;  // earlier Win10 builds

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Sets the window's title bar to match the current theme. Safe no-op on failure.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var enabled = ThemeManager.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
    }
}
