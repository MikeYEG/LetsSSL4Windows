using System.Windows;
using System.Windows.Media;
using LetsSSL.Core.Models;

namespace LetsSSL.App;

/// <summary>
/// Switches the app between dark and light at runtime by mutating the shared
/// theme brush instances in <see cref="Application.Resources"/>. Because every
/// control resolves these brushes by reference (StaticResource), changing each
/// brush's Color repaints the whole UI live — no restart required.
/// </summary>
internal static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;
    public static bool IsDark => Current == AppTheme.Dark;

    // brush key -> (dark hex, light hex)
    private static readonly (string Key, string Dark, string Light)[] Palette =
    {
        ("Accent",       "#3B82F6", "#2563EB"),
        ("AccentHover",  "#60A5FA", "#1D4ED8"),
        ("Bg",           "#0F172A", "#F8FAFC"),
        ("Surface",      "#1E293B", "#FFFFFF"),
        ("SurfaceAlt",   "#273449", "#F1F5F9"),
        ("SurfaceHover", "#334155", "#E2E8F0"),
        ("Border",       "#334155", "#E2E8F0"),
        ("TextPrimary",  "#F1F5F9", "#0F172A"),
        ("TextMuted",    "#94A3B8", "#64748B"),
        ("Ok",           "#22C55E", "#16A34A"),
        ("Warn",         "#F59E0B", "#D97706"),
        ("Danger",       "#EF4444", "#DC2626"),
        ("ConsoleBg",    "#0B1120", "#0F172A"),
        ("ConsoleFg",    "#CBD5E1", "#E2E8F0"),
    };

    /// <summary>Applies a theme: updates every theme brush and all open windows' title bars.</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var dark = theme == AppTheme.Dark;
        var resources = Application.Current.Resources;

        foreach (var (key, darkHex, lightHex) in Palette)
        {
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
                brush.Color = (Color)ColorConverter.ConvertFromString(dark ? darkHex : lightHex);
        }

        foreach (Window window in Application.Current.Windows)
            Theming.ApplyTitleBar(window);
    }
}
