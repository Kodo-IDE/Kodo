// Licensed under GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

namespace Kodo;

internal sealed record UpdateInfo(
    string Version,        // e.g. "v1.2.0" (raw tag_name from GitHub)
    string ReleaseNotesUrl,
    string AssetDownloadUrl,
    string AssetName,
    long AssetSizeBytes);

internal sealed record UpdateDownloadProgress(double Fraction, string Label);

internal static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Kodo-IDE/Kodo/releases/latest";
    private const string ReleaseNotesUrl = "https://github.com/Kodo-IDE/Kodo/releases";
    private const string UserAgent = "Kodo/2.0.0-DEV (https://github.com/Kodo-IDE/Kodo)";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }


    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, ct)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            if (release.Draft || release.Prerelease)
                return null;

            if (!IsNewerVersion(release.TagName, KodoDiagnostics.AppVersion))
                return null;

            // Inno Setup output is a plain .exe - grab the first .exe asset.
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (asset is null)
                return null;

            return new UpdateInfo(
                Version: release.TagName,
                ReleaseNotesUrl: release.HtmlUrl ?? ReleaseNotesUrl,
                AssetDownloadUrl: asset.BrowserDownloadUrl,
                AssetName: asset.Name,
                AssetSizeBytes: asset.Size);
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsNewerVersion(string remote, string local)
    {
        var remoteParts = ParseVersionParts(remote);
        var localParts = ParseVersionParts(local);

        if (remoteParts is null || localParts is null)
            return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < Math.Max(remoteParts.Length, localParts.Length); i++)
        {
            var r = i < remoteParts.Length ? remoteParts[i] : 0;
            var l = i < localParts.Length ? localParts[i] : 0;
            if (r != l) return r > l;
        }

        return false;
    }

    private static int[]? ParseVersionParts(string tag)
    {
        var core = tag.Trim();
        if (core.Length > 0 && (core[0] == 'v' || core[0] == 'V'))
            core = core[1..];

        var dashIndex = core.IndexOf('-');
        if (dashIndex >= 0) core = core[..dashIndex];
        var plusIndex = core.IndexOf('+');
        if (plusIndex >= 0) core = core[..plusIndex];

        var segments = core.Split('.');
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out parts[i]))
                return null;
        }

        return parts.Length > 0 ? parts : null;
    }


    private static bool ReadAutoUpdateFlag(Func<AutoUpdateSettings, bool> sel, bool fallback)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "kodosettings.json");
            if (!File.Exists(path)) return fallback;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            var settings = JsonSerializer.Deserialize<AutoUpdateSettings>(json);
            return settings is null ? fallback : sel(settings);
        }
        catch { return fallback; }
    }
    public static bool IsAutoUpdateEnabledInSettings() => ReadAutoUpdateFlag(s => s.AutoUpdateAppEnabled, true);
    public static bool IsAutoUpdateInBackgroundEnabledInSettings() => ReadAutoUpdateFlag(s => s.AutoUpdateAppInBackgroundEnabled, false);

    // Minimal subset of MainWindow's AppSettings needed to read this one flag.
    private sealed class AutoUpdateSettings
    {
        public bool AutoUpdateAppEnabled { get; set; } = true;
        public bool AutoUpdateAppInBackgroundEnabled { get; set; }
    }


    [SupportedOSPlatform("windows")]
    public static void EnsureAutostartRegistered()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var updaterPath = Path.Combine(exeDir, "KodoUpdater.exe");
            if (!File.Exists(updaterPath))
                return;

            RunSchtasks(
                "/Create /F " +
                $"/TN \"{AutostartTaskName}\" " +
                $"/TR \"\\\"{updaterPath}\\\"\" " +
                "/SC ONLOGON /RL LIMITED /DELAY 0000:30");
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("UpdateService.EnsureAutostartRegistered", ex, operation: "AutoUpdate");
        }
    }

    [SupportedOSPlatform("windows")]
    public static void RemoveAutostartRegistration()
    {
        try
        {
            RunSchtasks($"/Delete /TN \"{AutostartTaskName}\" /F");
        }
        catch
        {
        }
    }

    private const string AutostartTaskName = "Kodo-KodoUpdater-Autostart";

    [SupportedOSPlatform("windows")]
    private static void RunSchtasks(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        proc?.WaitForExit(5000);
    }


    // Downloads the installer to a temp path with progress reporting.
    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var destinationDir = Path.Combine(Path.GetTempPath(), "Kodo-Update");
        Directory.CreateDirectory(destinationDir);

        var destinationPath = Path.Combine(destinationDir, update.AssetName);

        using var response = await Http.GetAsync(
            update.AssetDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? update.AssetSizeBytes;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;

            if (progress is not null && totalBytes > 0)
            {
                var fraction = Math.Clamp((double)readTotal / totalBytes, 0, 1);
                var label = $"{FormatBytes(readTotal)} / {FormatBytes(totalBytes)}";
                progress.Report(new UpdateDownloadProgress(fraction, label));
            }
        }

        return destinationPath;
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024 * 1024;
        return bytes >= mb
            ? $"{bytes / mb:0.#} MB"
            : $"{bytes / 1024.0:0} KB";
    }


    public static void LaunchInstallerAndExit(string installerPath, bool reopenAfterInstall = false)
    {
        if (reopenAfterInstall)
        {
            var kodoExePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(kodoExePath))
            {
                try
                {
                    var batPath = Path.Combine(Path.GetTempPath(), "Kodo-Update", "restart-kodo.bat");
                    Directory.CreateDirectory(Path.GetDirectoryName(batPath)!);
                    File.WriteAllText(batPath,
                        $"@echo off\r\n" +
                        $"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS\r\n" +
                        $"start \"\" \"{kodoExePath}\"\r\n" +
                        $"del \"%~f0\"\r\n");

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = batPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });

                    Thread.Sleep(1500);
                    Environment.Exit(0);
                    return;
                }
                catch
                {
                    // Fall through to the direct-launch fallback below.
                }
            }
        }

        // Background-update path (or fallback): launch installer, no restart.
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true,
        });

        Thread.Sleep(1500);
        Environment.Exit(0);
    }

    public static async Task SilentlyInstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        try
        {
            var installerPath = await DownloadInstallerAsync(update, progress: null, ct).ConfigureAwait(false);
            LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.WriteDiagnosticLog(
                source: "UpdateService.SilentlyInstallAsync",
                exception: ex,
                isTerminating: false,
                severity: "Warning",
                operation: "AutoUpdate");
        }
    }

    public static async Task<UpdateInfo?> CheckAndHandleUpdateAsync(
        bool installInBackground,
        Action<UpdateInfo>? onUpdateFound = null,
        CancellationToken ct = default)
    {
        var update = await CheckForUpdateAsync(ct).ConfigureAwait(false);
        if (update is null) return null;

        onUpdateFound?.Invoke(update);

        if (installInBackground)
            await SilentlyInstallAsync(update, ct).ConfigureAwait(false);
        else
            UpdateDialog.ShowFor(update);

        return update;
    }


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

internal sealed class UpdateDialog : Window
{
    private readonly DialogThemePalette _palette;

    // Resolved from the user's active accent setting, same as MainWindow.
    private readonly Color _accentColor;
    private readonly Color _accentForeground;

    private readonly UpdateInfo _update;
    private readonly string? _preDownloadedInstallerPath;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly Button _primaryButton;
    private readonly Button _laterButton;
    private readonly StackPanel _content;
    private bool _canClose = true;

    public UpdateDialog(UpdateInfo update, string? preDownloadedInstallerPath = null)
    {
        _update = update;
        _preDownloadedInstallerPath = preDownloadedInstallerPath;
        _palette = ThemeResolver.GetCurrentPalette();
        (_accentColor, _accentForeground) = AccentResolver.GetCurrentAccent();

        Title = "Kodo - Update Available";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = new SolidColorBrush(_palette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var iconBadge = new Border
        {
            Background = new SolidColorBrush(_accentColor),
            CornerRadius = new CornerRadius(8),
            Width = 40,
            Height = 40,
            Child = new TextBlock
            {
                Text = "↑",
                FontSize = 20,
                Foreground = new SolidColorBrush(_accentForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var titleText = new TextBlock
        {
            Text = $"Kodo {update.Version} is available",
            FontSize = 16,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { iconBadge, titleText },
        };

        _statusText = new TextBlock
        {
            Text = preDownloadedInstallerPath is not null
                ? "A new version of Kodo has already been downloaded and is ready to install."
                : "A new version of Kodo has been published. Update now to get the latest fixes and features.",
            FontSize = 13,
            Foreground = new SolidColorBrush(_palette.TextMuted),
            TextWrapping = TextWrapping.Wrap,
        };

        var notesLink = new TextBlock
        {
            Text = "View release notes",
            FontSize = 12,
            Foreground = new SolidColorBrush(_accentColor),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        notesLink.PointerPressed += (_, _) => OpenUrl(update.ReleaseNotesUrl);

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 8,
            IsVisible = false,
            Foreground = new SolidColorBrush(_accentColor),
            Background = new SolidColorBrush(_palette.BadgeBg),
            CornerRadius = new CornerRadius(4),
        };

        _laterButton = new Button
        {
            Content = "Later",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8),
            Background = new SolidColorBrush(_palette.BadgeBg),
            Foreground = new SolidColorBrush(_palette.TextMuted),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        _laterButton.Click += (_, _) => Close();

        _primaryButton = new Button
        {
            Content = "Update Now",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(_accentColor),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        _primaryButton.Click += async (_, _) => await BeginUpdateAsync();

        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(_laterButton);
        Grid.SetColumn(_primaryButton, 1);
        buttonRow.Children.Add(_primaryButton);

        var headerDivider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(_palette.Border),
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        var footerDivider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(_palette.Border),
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        _content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                headerRow,
                headerDivider,
                _statusText,
                notesLink,
                _progressBar,
                footerDivider,
                buttonRow,
            },
        };

        Content = new Border
        {
            Background = new SolidColorBrush(_palette.SurfaceDeep),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(16),
            Child = _content,
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_canClose)
            e.Cancel = true;
        base.OnClosing(e);
    }

    public static void ShowFor(UpdateInfo update, string? installerPath = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var dialog = new UpdateDialog(update, installerPath);

            Window? owner = null;
            if (Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var main = desktop.MainWindow;
                if (main is { IsVisible: true })
                    owner = main;
            }

            if (owner is not null)
                dialog.Show(owner);
            else
                dialog.Show();
        });
    }

    private async Task BeginUpdateAsync()
    {
        _canClose = false;
        _primaryButton.IsEnabled = false;
        _laterButton.IsEnabled = false;

        if (_preDownloadedInstallerPath is not null && File.Exists(_preDownloadedInstallerPath))
        {
            _progressBar.IsVisible = true;
            _progressBar.IsIndeterminate = true;
            _primaryButton.Content = "Installing…";
            _statusText.Text = "Installing update… Kodo will restart shortly.";
            await Task.Delay(400);
            UpdateService.LaunchInstallerAndExit(_preDownloadedInstallerPath, reopenAfterInstall: true);
            return;
        }

        _primaryButton.Content = "Downloading…";
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = false;
        _statusText.Text = "Downloading the update…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            _progressBar.Value = p.Fraction;
            _statusText.Text = $"Downloading… {p.Label}";
        });

        try
        {
            var installerPath = await UpdateService.DownloadInstallerAsync(_update, progress);

            _progressBar.IsIndeterminate = true;
            _statusText.Text = "Installing update… Kodo will restart shortly.";
            _primaryButton.Content = "Installing…";

            await Task.Delay(600);

            UpdateService.LaunchInstallerAndExit(installerPath, reopenAfterInstall: true);
        }
        catch (Exception ex)
        {
            _statusText.Text = "The update couldn't be downloaded. Check your connection and try again.";
            _primaryButton.Content = "Retry";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _progressBar.IsVisible = false;
            _progressBar.IsIndeterminate = false;
            _canClose = true;

            KodoDiagnostics.WriteDiagnosticLog(
                source: "UpdateDialog.BeginUpdateAsync",
                exception: ex,
                isTerminating: false,
                severity: "Warning",
                operation: "AutoUpdate");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Opening the browser is a convenience action; never let it
        }
    }
}
internal static class DialogPalette
{
    public static readonly Color Surface = Color.Parse("#1E1E1E");
    public static readonly Color SurfaceDeep = Color.Parse("#1A1A1A");
    public static readonly Color Border = Color.Parse("#3A3A3A");
    public static readonly Color BadgeBg = Color.Parse("#2B2B2B");
    public static readonly Color Text = Color.Parse("#F4F4F4");
    public static readonly Color TextMuted = Color.Parse("#A0A0A0");
    public static readonly Color TextDim = Color.Parse("#606060");
    public static readonly Color TokenBlue = Color.Parse("#9CDCFE");  // source badge
    public static readonly Color TokenOrange = Color.Parse("#CE9178");  // stack trace
}

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
            "theme" => string.IsNullOrWhiteSpace(settings.CachedThemeAccentHex)
                ? DefaultAccentHex : settings.CachedThemeAccentHex,
            "windows" => GetWindowsAccentColor() ?? "#0078D4",
            "custom" => string.IsNullOrWhiteSpace(settings.CustomAccentHex)
                ? DefaultAccentHex : settings.CustomAccentHex,
            _ => DefaultAccentHex, // "kodo" (and any unrecognised value)
        };
    }

    private static AccentSettings LoadAccentSettings()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kodo",
                SettingsFileName);

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
            "Light" => LightPalette,
            "Dark" => DarkPalette,
#pragma warning disable CA1416 // Kodo targets Windows only; System theme check is Windows-only by design.
            "System" => IsWindowsLightTheme() ? LightPalette : DarkPalette,
#pragma warning restore CA1416
            _ => ResolveExtensionPalette(settings),
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
        return false; // Default to dark.
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

internal static class PendingUpdateService
{
    private static string FilePath => Path.Combine(Path.GetTempPath(), "Kodo-Update", "pending.json");

    public static (string Version, string InstallerPath)? TryGetPendingUpdate()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var json = File.ReadAllText(FilePath);
            var record = JsonSerializer.Deserialize<PendingUpdateRecord>(json);
            if (record is null) { Clear(); return null; }

            if (!File.Exists(record.InstallerPath))
            {
                Clear();
                return null;
            }

            if (!UpdateService.IsNewerVersion(record.Version, KodoDiagnostics.AppVersion))
            {
                // Already on this version or newer - the download is stale.
                Clear();
                return null;
            }

            return (record.Version, record.InstallerPath);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { /* best-effort cleanup */ }
    }

    private sealed record PendingUpdateRecord(string Version, string InstallerPath, DateTime DownloadedAtUtc);
}
internal sealed class AppUpdateScheduler
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isManualCheckInProgress;
    private readonly Func<bool> _installInBackground;
    public AppUpdateScheduler(Func<bool> isEnabled, Func<bool> isManualCheckInProgress, Func<bool> installInBackground)
    {
        _isEnabled = isEnabled;
        _isManualCheckInProgress = isManualCheckInProgress;
        _installInBackground = installInBackground;
        _timer.Tick += async (_, _) => await OnTickAsync().ConfigureAwait(true);
    }
    public void UpdateLifecycle()
    {
        _timer.Stop();
        if (_isEnabled())
            _timer.Start();
    }
    public void Stop() => _timer.Stop();

    // Fires every six hours while enabled; skips while a manual check is in
    private async Task OnTickAsync()
    {
        if (!_isEnabled() || _isManualCheckInProgress())
            return;

        if (_installInBackground())
            return;

        try
        {
            await UpdateService.CheckAndHandleUpdateAsync(installInBackground: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Silent background check - this must never surface as a crash.
            KodoDiagnostics.LogDebug("Periodic app update check failed", ex);
        }
    }
}
