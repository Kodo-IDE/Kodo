// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shared = Kodo.HotfixShared.HotfixShared;

namespace Kodo;

internal static class ThemeResolver
{
    private const string SettingsFileName = "kodosettings.json";
    private static readonly DialogThemePalette DarkPalette = new(Color.Parse("#1E1E1E"), Color.Parse("#1A1A1A"), Color.Parse("#3A3A3A"), Color.Parse("#2B2B2B"), Color.Parse("#F4F4F4"), Color.Parse("#A0A0A0"), Color.Parse("#606060"));
    private static readonly DialogThemePalette LightPalette = new(Color.Parse("#F3F3F3"), Color.Parse("#FFFFFF"), Color.Parse("#D7DCE5"), Color.Parse("#E3E8F1"), Color.Parse("#202124"), Color.Parse("#5F6B7A"), Color.Parse("#8A8A8A"));
    public static DialogThemePalette GetCurrentPalette()
    {
        var settings = LoadThemeSettings();
        return settings.ThemeName switch
        {
            "Light" => LightPalette, "Dark" => DarkPalette,
#pragma warning disable CA1416
            "System" => IsWindowsLightTheme() ? LightPalette : DarkPalette,
#pragma warning restore CA1416
            _ => ResolveExtensionPalette(settings),
        };
    }
    private static DialogThemePalette ResolveExtensionPalette(ThemeSettings settings)
    {
        var bgHex = settings.CachedThemeWindowBackgroundHex;
        if (string.IsNullOrWhiteSpace(bgHex)) return DarkPalette;
        Color bg; try { bg = Color.Parse(bgHex); } catch { return DarkPalette; }
        var isLight = IsLightColor(bg);
        return isLight ? new DialogThemePalette(bg, Lighten(bg, 0.04), Blend(Color.Parse("#D7DCE5"), bg, 0.5), Blend(Color.Parse("#E3E8F1"), bg, 0.4), Color.Parse("#202124"), Color.Parse("#5F6B7A"), Color.Parse("#8A8A8A"))
                       : new DialogThemePalette(bg, Darken(bg, 0.04), Blend(Color.Parse("#3A3A3A"), bg, 0.5), Blend(Color.Parse("#2B2B2B"), bg, 0.4), Color.Parse("#F4F4F4"), Color.Parse("#A0A0A0"), Color.Parse("#606060"));
    }
    private static bool IsLightColor(Color c) { static double Lin(byte ch) { var s = ch / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } var luminance = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B); return luminance > 0.4; }
    private static Color Lighten(Color c, double a) { var r = (byte)Math.Clamp(c.R + (255 - c.R) * a, 0, 255); var g = (byte)Math.Clamp(c.G + (255 - c.G) * a, 0, 255); var b = (byte)Math.Clamp(c.B + (255 - c.B) * a, 0, 255); return Color.Parse($"#{r:X2}{g:X2}{b:X2}"); }
    private static Color Darken(Color c, double a) { var r = (byte)Math.Clamp(c.R * (1 - a), 0, 255); var g = (byte)Math.Clamp(c.G * (1 - a), 0, 255); var b = (byte)Math.Clamp(c.B * (1 - a), 0, 255); return Color.Parse($"#{r:X2}{g:X2}{b:X2}"); }
    private static Color Blend(Color a, Color b, double t) { var r = (byte)(a.R + (b.R - a.R) * t); var g = (byte)(a.G + (b.G - a.G) * t); var bl = (byte)(a.B + (b.B - a.B) * t); return Color.Parse($"#{r:X2}{g:X2}{bl:X2}"); }
    [SupportedOSPlatform("windows")] private static bool IsWindowsLightTheme() { try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); if (key?.GetValue("AppsUseLightTheme") is int raw) return raw != 0; } catch { } return false; }
    private static ThemeSettings LoadThemeSettings()
    {
        try
        {
            var path = KodoPaths.SettingsFilePath(SettingsFileName);
            if (!File.Exists(path)) return new ThemeSettings();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new ThemeSettings();
            return JsonSerializer.Deserialize<ThemeSettings>(json) ?? new ThemeSettings();
        }
        catch { return new ThemeSettings(); }
    }

}
