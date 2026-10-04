// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Kodo;

internal static class SystemThemeHelper
{
    private static readonly object CacheLock = new();

    public static string? GetSystemAccentHex()
    {
        if (OperatingSystem.IsWindows()) return GetWindowsAccentHex();
        if (OperatingSystem.IsLinux()) return GetLinuxAccentHex();
        return null;
    }

    public static bool? GetIsLightTheme()
    {
        if (OperatingSystem.IsWindows()) return GetWindowsIsLightTheme();
        if (OperatingSystem.IsLinux()) return GetLinuxIsLightTheme();
        return null;
    }

    public static bool SupportsAccent =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static string AccentModeDisplayName(bool isAmericanEnglish) =>
        OperatingSystem.IsWindows() ? "Windows" : "System";

    public static string AccentModeTooltip(bool isAmericanEnglish)
    {
        var baseText = isAmericanEnglish
            ? $"Use {AccentSourceNoun()} accent color"
            : $"Use {AccentSourceNoun()} accent colour";
        return baseText;
    }

    private static string AccentSourceNoun() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "your desktop's" : "your system's";

    public static string SystemThemeTooltip(bool isAmericanEnglish) =>
        isAmericanEnglish
            ? $"Match your {SystemThemeSource()} light/dark setting"
            : $"Match your {SystemThemeSource()} light/dark setting";

    private static string SystemThemeSource() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "desktop's" : "system's";

    public static bool SupportsSystemTheme => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static string RevealInFileManagerText(bool isAmericanEnglish) =>
        OperatingSystem.IsWindows() ? "Reveal in File Explorer"
        : OperatingSystem.IsMacOS() ? "Reveal in Finder"
        : isAmericanEnglish ? "Reveal in File Manager" : "Reveal in File Manager";


    [SupportedOSPlatform("windows")]
    private static string? GetWindowsAccentHex()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int raw)
            {
                var r = raw & 0xFF; var g = (raw >> 8) & 0xFF; var b = (raw >> 16) & 0xFF;
                return $"#{r:X2}{g:X2}{b:X2}";
            }
            if (key?.GetValue("AccentColor") is int rawDword)
            {
                var b = rawDword & 0xFF; var g = (rawDword >> 8) & 0xFF; var r = (rawDword >> 16) & 0xFF;
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool? GetWindowsIsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return null;
    }


    private static readonly string[] GSettingsKeys =
        ["accent-bg-color", "accent-color", "color-scheme", "gtk-theme"];

    private static readonly Dictionary<string, string> GnomeAccentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blue"] = "#3584E4",
        ["teal"] = "#2190A4",
        ["green"] = "#3A944A",
        ["yellow"] = "#C88800",
        ["orange"] = "#ED5B00",
        ["red"] = "#E62D42",
        ["pink"] = "#D56199",
        ["purple"] = "#9141AC",
        ["slate"] = "#6F8396",
    };

    private static bool _linuxCacheValid;
    private static string? _linuxCacheKey;
    private static string? _linuxAccent;
    private static bool? _linuxIsLight;

    private static string? GetLinuxAccentHex()
    {
        EnsureLinuxCache();
        return _linuxAccent;
    }

    private static bool? GetLinuxIsLightTheme()
    {
        EnsureLinuxCache();
        return _linuxIsLight;
    }

    private static void EnsureLinuxCache()
    {
        var key = LinuxConfigFingerprint();
        lock (CacheLock)
        {
            if (_linuxCacheValid && _linuxCacheKey == key) return;
        }

        var accent = ReadLinuxAccent();
        var isLight = ReadLinuxIsLightTheme();

        lock (CacheLock)
        {
            if (_linuxCacheValid && _linuxCacheKey == key) return;
            _linuxCacheKey = key;
            _linuxAccent = accent;
            _linuxIsLight = isLight;
            _linuxCacheValid = true;
        }
    }

    private static string? ReadLinuxAccent()
    {
        foreach (var version in new[] { "4.0", "3.0" })
        {
            var settings = Path.Combine(LinuxConfigDir(), "gtk-" + version, "settings.ini");
            foreach (var k in new[] { "accent-color", "accent-bg-color", "accent-background-color" })
            {
                if (TryReadIniValue(settings, "Settings", k, out var raw) && TryParseColor(raw, out var hex))
                    return hex;
            }
        }

        var gs = ReadGSettings();
        if (gs.TryGetValue("accent-bg-color", out var accentBg) && TryParseColor(accentBg, out var gnomeHex))
            return gnomeHex;
        if (gs.TryGetValue("accent-color", out var accentName))
        {
            var name = accentName.Trim().Trim('\'', '"');
            if (GnomeAccentNames.TryGetValue(name, out var mapped)) return mapped;
        }

        var kde = Path.Combine(LinuxConfigDir(), "kdeglobals");
        if (TryReadIniValue(kde, "Colors:Window", "Foreground", out var fg) &&
            TryParseColor(fg, out var kdeHex))
            return kdeHex;

        return null;
    }

    private static bool? ReadLinuxIsLightTheme()
    {
        foreach (var version in new[] { "4.0", "3.0" })
        {
            var settings = Path.Combine(LinuxConfigDir(), "gtk-" + version, "settings.ini");
            if (TryReadIniValue(settings, "Settings", "gtk-application-prefer-dark-theme", out var dark) ||
                TryReadIniValue(settings, "Settings", "prefer-dark-theme", out dark))
            {
                if (TryParseBool(dark, out var prefersDark))
                    return !prefersDark;
            }
        }

        var gs0 = ReadGSettings();
        foreach (var theme in new[]
        {
            TryReadIniValue(Path.Combine(LinuxConfigDir(), "gtk-4.0", "settings.ini"), "Settings", "gtk-theme", out var t1) ? t1 : null,
            TryReadIniValue(Path.Combine(LinuxConfigDir(), "gtk-3.0", "settings.ini"), "Settings", "gtk-theme", out var t2) ? t2 : null,
            gs0.GetValueOrDefault("gtk-theme"),
            Environment.GetEnvironmentVariable("GTK_THEME"),
        })
        {
            if (string.IsNullOrWhiteSpace(theme)) continue;
            if (theme.Contains("dark", StringComparison.OrdinalIgnoreCase)) return false;
            if (theme.Contains("light", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var gs = gs0;
        if (gs.TryGetValue("color-scheme", out var scheme))
        {
            var s = scheme.Trim().Trim('\'', '"');
            if (s.Equals("prefer-dark", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("dark", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Equals("prefer-light", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("light", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("default", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var kde = Path.Combine(LinuxConfigDir(), "kdeglobals");
        if (TryReadIniValue(kde, "Looks", "colorScheme", out var kdeScheme))
        {
            var s = kdeScheme.Trim();
            if (s.Equals("Day", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("Bark", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Dark", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Night", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return null;
    }

    private static Dictionary<string, string> ReadGSettings()
    {
        lock (CacheLock)
        {
            if (_gSettingsValid && _gSettingsKey == LinuxConfigFingerprint())
                return _gSettings;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var exe = FindOnPath("gsettings");
            if (exe is not null)
            {
                if (!TryReadGSettingsRecursive(exe, values))
                {
                    foreach (var key in GSettingsKeys)
                    {
                        var value = RunCapture(exe, $"get org.gnome.desktop.interface {key}");
                        if (!string.IsNullOrWhiteSpace(value))
                            values[key] = value.Trim();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("[Theme] gsettings probe failed.", ex);
        }

        lock (CacheLock)
        {
            _gSettingsKey = LinuxConfigFingerprint();
            _gSettings = values;
            _gSettingsValid = true;
            return _gSettings;
        }
    }

    private static Dictionary<string, string> _gSettings = new(StringComparer.OrdinalIgnoreCase);
    private static string? _gSettingsKey;
    private static bool _gSettingsValid;

    private static bool TryReadGSettingsRecursive(string exe, Dictionary<string, string> into)
    {
        var output = RunCapture(exe, "list-recursively org.gnome.desktop.interface");
        if (string.IsNullOrWhiteSpace(output)) return false;

        var found = 0;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            var key = line[..tab];
            var value = line[(tab + 1)..].Trim();
            if (value.Length == 0) continue;

            foreach (var wanted in GSettingsKeys)
            {
                if (!string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                into[wanted] = value;
                found++;
                break;
            }
        }

        return found > 0;
    }

    private static string? RunCapture(string fileName, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000)) return null;
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static string? FindOnPath(string command)
    {
        try
        {
            var fileName = command.Trim();
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return fileName;
            foreach (var rawDir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(rawDir.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string LinuxConfigDir()
    {
        var baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(baseDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : baseDir;
    }

    private static string LinuxConfigFingerprint()
    {
        try
        {
            var root = LinuxConfigDir();
            var parts = new List<string>(8);
            foreach (var relative in new[]
            {
                Path.Combine("gtk-4.0", "settings.ini"),
                Path.Combine("gtk-3.0", "settings.ini"),
                Path.Combine("kdeglobals"),
                Path.Combine("dconf", "user"),
            })
            {
                try
                {
                    var info = new FileInfo(Path.Combine(root, relative));
                    parts.Add(info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-");
                }
                catch { parts.Add("?"); }
            }

            parts.Add(Environment.GetEnvironmentVariable("GTK_THEME") ?? string.Empty);
            parts.Add(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty);
            return string.Join('|', parts);
        }
        catch { return string.Empty; }
    }

    private static bool TryReadIniValue(string path, string section, string key, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;

            var inSection = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;

                if (line[0] == '[' && line.EndsWith(']'))
                {
                    inSection = line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                value = line[(eq + 1)..].Trim();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        result = false;
        var v = value.Trim().Trim('"').Trim('\'');
        if (v.Equals("1", StringComparison.Ordinal) || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }
        if (v.Equals("0", StringComparison.Ordinal) || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }
        return false;
    }

    private static bool TryParseColor(string value, out string? hex)
    {
        hex = null;
        var v = Unquote(value);
        if (v.Length == 0) return false;

        if (v[0] == '#')
        {
            var digits = v[1..];
            if (digits.Length == 6 && IsHex(digits)) { hex = "#" + digits.ToUpperInvariant(); return true; }
            if (digits.Length == 3 && IsHex(digits))
            {
                hex = $"#{digits[0]}{digits[0]}{digits[1]}{digits[1]}{digits[2]}{digits[2]}".ToUpperInvariant();
                return true;
            }
            return false;
        }

        if (v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
            return TryParseRgb(v[4..].TrimEnd(')'), out hex);

        if (v.Contains(','))
            return TryParseRgb(v, out hex);

        return false;
    }

    private static bool TryParseRgb(string body, out string? hex)
    {
        hex = null;
        var parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var g) || !int.TryParse(parts[2], out var b))
            return false;
        if (r is < 0 or > 255 || g is < 0 or > 255 || b is < 0 or > 255) return false;
        hex = $"#{r:X2}{g:X2}{b:X2}";
        return true;
    }

    private static string Unquote(string value) => value.Trim().Trim('"').Trim('\'').Trim();

    private static bool IsHex(string digits)
    {
        foreach (var c in digits)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }


    public static Avalonia.Media.IBrush GetReadableForeground(Avalonia.Media.Color bg)
    {
        static double Lum(Avalonia.Media.Color c) { double To(double ch) { ch /= 255; return ch <= 0.03928 ? ch / 12.92 : Math.Pow((ch + 0.055) / 1.055, 2.4); } return 0.2126 * To(c.R) + 0.7152 * To(c.G) + 0.0722 * To(c.B); }
        var l = Lum(bg);
        return (1.05 / (l + 0.05)) >= ((l + 0.05) / 0.05) ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.Black;
    }

    public static Avalonia.Media.Color Lighten(Avalonia.Media.Color c, double a) { byte Adj(byte ch) => (byte)Math.Clamp(ch + (255 - ch) * a, 0, 255); return Avalonia.Media.Color.FromArgb(c.A, Adj(c.R), Adj(c.G), Adj(c.B)); }
    public static Avalonia.Media.Color Darken(Avalonia.Media.Color c, double a) { byte Adj(byte ch) => (byte)Math.Clamp(ch * (1 - a), 0, 255); return Avalonia.Media.Color.FromArgb(c.A, Adj(c.R), Adj(c.G), Adj(c.B)); }
}
