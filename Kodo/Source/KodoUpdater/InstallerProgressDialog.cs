using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace KodoUpdater;

// Shown by KodoUpdater while the Inno Setup installer replaces Kodo's files.
// Matches the palette and accent colour of Kodo's own dialogs via ThemeResolver.
internal sealed class InstallerProgressDialog : Window
{
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;

    public InstallerProgressDialog()
    {
        var palette = ThemeResolver.GetCurrentPalette();
        var (accentColor, accentForeground) = AccentResolver.GetCurrentAccent();

        Title  = "Kodo - Updating";
        Width  = 420;
        SizeToContent = SizeToContent.Height;
        CanResize  = false;
        Background = new SolidColorBrush(palette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var iconBadge = new Border
        {
            Background      = new SolidColorBrush(accentColor),
            CornerRadius    = new CornerRadius(8),
            Width           = 40,
            Height          = 40,
            Child = new TextBlock
            {
                Text                = "\u2191",
                FontSize            = 20,
                Foreground          = new SolidColorBrush(accentForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };

        var titleText = new TextBlock
        {
            Text       = "Installing update\u2026",
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(palette.Text),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 12,
            Children    = { iconBadge, titleText },
        };

        _statusText = new TextBlock
        {
            Text         = "Kodo is being updated. This will only take a moment.",
            FontSize     = 13,
            Foreground   = new SolidColorBrush(palette.TextMuted),
            TextWrapping = TextWrapping.Wrap,
        };

        _progressBar = new ProgressBar
        {
            Minimum       = 0,
            Maximum       = 1,
            Value         = 0,
            Height        = 8,
            IsVisible     = true,
            // Indeterminate until the first real percentage comes in from the
            // installer (there's a brief gap while it starts up) - SetProgress
            // flips this off.
            IsIndeterminate = true,
            Foreground    = new SolidColorBrush(accentColor),
            Background    = new SolidColorBrush(palette.BadgeBg),
            CornerRadius  = new CornerRadius(4),
        };

        var content = new StackPanel
        {
            Spacing = 14,
            Children = { headerRow, _statusText, _progressBar },
        };

        Content = new Border
        {
            Background      = new SolidColorBrush(palette.SurfaceDeep),
            BorderBrush     = new SolidColorBrush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(20),
            Padding         = new Thickness(22),
            Margin          = new Thickness(16),
            Child           = content,
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        base.OnClosing(e);
    }

    public void SetStatus(string text)
    {
        Dispatcher.UIThread.Post(() => _statusText.Text = text);
    }

    // Drives the bar from the installer's real progress (0-100), as reported
    // by KodoInstaller.iss's CurInstallProgressChanged via the shared
    // install-progress.json file. Converted to a 0-1 fraction to match the
    // Value domain every other progress bar in Kodo uses (see UpdateDialog).
    public void SetProgress(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        Dispatcher.UIThread.Post(() =>
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = clamped / 100.0;
        });
    }
}

// Full colour palette for code-built dialogs, resolved from the user's active theme.
// Duplicate of the class in Updater.cs so KodoUpdater (a separate process) can use it.
internal sealed record DialogThemePalette(
    Color Background,
    Color SurfaceDeep,
    Color Border,
    Color BadgeBg,
    Color Text,
    Color TextMuted,
    Color TextDim);

internal static class ThemeResolver
{
    private const string SettingsFileName = "kodosettings.json";

    private static readonly DialogThemePalette DarkPalette = new(
        Background: Color.Parse("#1E1E1E"),
        SurfaceDeep: Color.Parse("#1A1A1A"),
        Border: Color.Parse("#3A3A3A"),
        BadgeBg: Color.Parse("#2B2B2B"),
        Text: Color.Parse("#F4F4F4"),
        TextMuted: Color.Parse("#A0A0A0"),
        TextDim: Color.Parse("#606060"));

    private static readonly DialogThemePalette LightPalette = new(
        Background: Color.Parse("#F3F3F3"),
        SurfaceDeep: Color.Parse("#FFFFFF"),
        Border: Color.Parse("#D7DCE5"),
        BadgeBg: Color.Parse("#E3E8F1"),
        Text: Color.Parse("#202124"),
        TextMuted: Color.Parse("#5F6B7A"),
        TextDim: Color.Parse("#8A8A8A"));

    public static DialogThemePalette GetCurrentPalette()
    {
        var settings = LoadThemeSettings();

        return settings.ThemeName switch
        {
            "Light"  => LightPalette,
            "Dark"   => DarkPalette,
#pragma warning disable CA1416 // Kodo targets Windows only; System theme check is Windows-only by design.
            "System" => IsWindowsLightTheme() ? LightPalette : DarkPalette,
#pragma warning restore CA1416
            _        => ResolveExtensionPalette(settings),
        };
    }

    private static DialogThemePalette ResolveExtensionPalette(ThemeSettings settings)
    {
        var bgHex = settings.CachedThemeWindowBackgroundHex;
        if (string.IsNullOrWhiteSpace(bgHex))
            return DarkPalette;

        Color bg;
        try { bg = Color.Parse(bgHex); }
        catch { return DarkPalette; }

        var isLight = IsLightColor(bg);

        return isLight
            ? new DialogThemePalette(
                Background: bg,
                SurfaceDeep: Lighten(bg, 0.04),
                Border: Blend(Color.Parse("#D7DCE5"), bg, 0.5),
                BadgeBg: Blend(Color.Parse("#E3E8F1"), bg, 0.4),
                Text: Color.Parse("#202124"),
                TextMuted: Color.Parse("#5F6B7A"),
                TextDim: Color.Parse("#8A8A8A"))
            : new DialogThemePalette(
                Background: bg,
                SurfaceDeep: Darken(bg, 0.04),
                Border: Blend(Color.Parse("#3A3A3A"), bg, 0.5),
                BadgeBg: Blend(Color.Parse("#2B2B2B"), bg, 0.4),
                Text: Color.Parse("#F4F4F4"),
                TextMuted: Color.Parse("#A0A0A0"),
                TextDim: Color.Parse("#606060"));
    }

    private static bool IsLightColor(Color c)
    {
        static double Lin(byte ch)
        {
            var s = ch / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        return luminance > 0.4;
    }

    private static Color Lighten(Color c, double amount)
    {
        var r = (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255);
        var g = (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255);
        var b = (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255);
        return Color.Parse($"#{r:X2}{g:X2}{b:X2}");
    }

    private static Color Darken(Color c, double amount)
    {
        var r = (byte)Math.Clamp(c.R * (1 - amount), 0, 255);
        var g = (byte)Math.Clamp(c.G * (1 - amount), 0, 255);
        var b = (byte)Math.Clamp(c.B * (1 - amount), 0, 255);
        return Color.Parse($"#{r:X2}{g:X2}{b:X2}");
    }

    private static Color Blend(Color a, Color b, double t)
    {
        var r = (byte)(a.R + (b.R - a.R) * t);
        var g = (byte)(a.G + (b.G - a.G) * t);
        var bl = (byte)(a.B + (b.B - a.B) * t);
        return Color.Parse($"#{r:X2}{g:X2}{bl:X2}");
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int raw)
                return raw != 0;
        }
        catch { /* Registry unavailable */ }
        return false;
    }

    private static ThemeSettings LoadThemeSettings()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kodo", SettingsFileName);

            if (!File.Exists(path)) return new ThemeSettings();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new ThemeSettings();

            return JsonSerializer.Deserialize<ThemeSettings>(json) ?? new ThemeSettings();
        }
        catch
        {
            return new ThemeSettings();
        }
    }

    private sealed class ThemeSettings
    {
        public string ThemeName { get; set; } = "Dark";
        public string? CachedThemeWindowBackgroundHex { get; set; }
    }
}

// Resolves accent colour from kodosettings.json, matching Kodo's own AccentResolver.
internal static class AccentResolver
{
    private const string DefaultAccentHex = "#8C00FF";
    private const string SettingsFileName = "kodosettings.json";

    public static (Color Accent, Color Foreground) GetCurrentAccent()
    {
        var hex = ResolveAccentHex();
        Color accent;
        try { accent = Color.Parse(hex); }
        catch { accent = Color.Parse(DefaultAccentHex); }

        return (accent, GetAccentForeground(accent));
    }

    private static string ResolveAccentHex()
    {
        var settings = LoadAccentSettings();

        return settings.AccentColorMode switch
        {
            "theme"   => string.IsNullOrWhiteSpace(settings.CachedThemeAccentHex)
                ? DefaultAccentHex : settings.CachedThemeAccentHex,
            "windows" => GetWindowsAccentColor() ?? "#0078D4",
            "custom"  => string.IsNullOrWhiteSpace(settings.CustomAccentHex)
                ? DefaultAccentHex : settings.CustomAccentHex,
            _         => DefaultAccentHex,
        };
    }

    private static AccentSettings LoadAccentSettings()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kodo", SettingsFileName);

            if (!File.Exists(path)) return new AccentSettings();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AccentSettings();

            return JsonSerializer.Deserialize<AccentSettings>(json) ?? new AccentSettings();
        }
        catch
        {
            return new AccentSettings();
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsAccentColorWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int raw)
            {
                var r = raw & 0xFF;
                var g = (raw >> 8) & 0xFF;
                var b = (raw >> 16) & 0xFF;
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { /* Registry unavailable */ }
        return null;
    }

    private static string? GetWindowsAccentColor()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        return GetWindowsAccentColorWindows();
    }

    private static Color GetAccentForeground(Color accent)
    {
        static double Lin(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Lin(accent.R) + 0.7152 * Lin(accent.G) + 0.0722 * Lin(accent.B);
        var whiteContrast = 1.05 / (luminance + 0.05);
        var blackContrast = (luminance + 0.05) / 0.05;

        return whiteContrast >= blackContrast ? Colors.White : Colors.Black;
    }

    private sealed class AccentSettings
    {
        public string AccentColorMode { get; set; } = "kodo";
        public string CustomAccentHex { get; set; } = DefaultAccentHex;
        public string? CachedThemeAccentHex { get; set; }
    }
}