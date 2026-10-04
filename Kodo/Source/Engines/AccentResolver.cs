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

internal static class AccentResolver
{
    private const string DefaultAccentHex = "#8C00FF";
    public static (Color Accent, Color Foreground) GetCurrentAccent()
    {
        var hex = ResolveAccentHex();
        Color accent;
        try { accent = Color.Parse(hex); } catch { accent = Color.Parse(DefaultAccentHex); }
        return (accent, GetAccentForeground(accent));
    }
    private static string ResolveAccentHex()
    {
        var settings = LoadAccentSettings();
        return settings.AccentColorMode switch
        {
            "theme" => string.IsNullOrWhiteSpace(settings.CachedThemeAccentHex) ? DefaultAccentHex : settings.CachedThemeAccentHex,
            "windows" => SystemThemeHelper.GetSystemAccentHex()
                ?? (string.IsNullOrWhiteSpace(settings.CachedThemeAccentHex) ? DefaultAccentHex : settings.CachedThemeAccentHex),
            "custom" => string.IsNullOrWhiteSpace(settings.CustomAccentHex) ? DefaultAccentHex : settings.CustomAccentHex,
            _ => DefaultAccentHex,
        };
    }
    private static AccentSettings LoadAccentSettings() => SettingsStore.Accent;
    private static Color GetAccentForeground(Color accent)
    {
        static double Lin(byte ch) { var s = ch / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Lin(accent.R) + 0.7152 * Lin(accent.G) + 0.0722 * Lin(accent.B);
        var whiteContrast = 1.05 / (luminance + 0.05);
        var blackContrast = (luminance + 0.05) / 0.05;
        return whiteContrast >= blackContrast ? Colors.White : Colors.Black;
    }
}
