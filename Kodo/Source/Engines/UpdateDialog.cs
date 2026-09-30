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

internal sealed class UpdateDialog : Window
{
    private readonly DialogThemePalette _palette;
    private readonly Color _accentColor;
    private readonly Color _accentForeground;
    private readonly UpdateInfo? _update;
    private string? _stagedInstallerPath;
    private readonly HotfixCandidate? _hotfix;
    private string? _stagedHotfixTxPath;
    private string? _stagedManualHotfixPackagePath;
    private readonly bool _isHotfixMode;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly Button _primaryButton;
    private readonly Button _laterButton;
    private bool _canClose = true;
    private bool _isDownloading;
    private bool _isReady;

    private readonly bool _linuxManualOnly;

    public UpdateDialog(UpdateInfo update, string? stagedInstallerPath = null)
    {
        _update = update;
        _stagedInstallerPath = stagedInstallerPath;
        _linuxManualOnly = UpdateService.IsLinuxNotifyOnly ||
            (OperatingSystem.IsLinux() && !UpdateService.IsLinuxAutoInstallAsset(update.AssetName));
        var isBeta = UpdateService.IsBetaVersionTag(update.Version);
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
            Width = 40, Height = 40,
            Child = new TextBlock { Text = "↑", FontSize = 20, Foreground = new SolidColorBrush(_accentForeground), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var titleText = new TextBlock
        {
            Text = $"Kodo {update.Version} is available",
            FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { iconBadge, titleText } };

        _statusText = new TextBlock
        {
            Text = isBeta
                ? "This is an unstable beta release. It is entirely optional and will only be downloaded if you choose Download Update."
                : stagedInstallerPath is not null && File.Exists(stagedInstallerPath)
                ? (_linuxManualOnly
                    ? UpdateService.LinuxReadyBlurb(stagedInstallerPath)
                    : "Update downloaded and ready to install. Choose Restart & Update when you're ready.")
                : "A new version of Kodo has been published. Update now to get the latest fixes and features.",
            FontSize = 13, Foreground = new SolidColorBrush(_palette.TextMuted), TextWrapping = TextWrapping.Wrap,
        };
        var notesLink = new TextBlock { Text = "View release notes", FontSize = 12, Foreground = new SolidColorBrush(_accentColor), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        notesLink.PointerPressed += (_, _) => OpenUrl(update.ReleaseNotesUrl);

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 8, IsVisible = false, Foreground = new SolidColorBrush(_accentColor), Background = new SolidColorBrush(_palette.BadgeBg), CornerRadius = new CornerRadius(4) };

        _laterButton = new Button { Content = "Later", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8), Background = new SolidColorBrush(_palette.BadgeBg), Foreground = new SolidColorBrush(_palette.TextMuted), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        _laterButton.Click += (_, _) => Close();

        _primaryButton = new Button
        {
            Content = stagedInstallerPath is not null
                ? (_linuxManualOnly ? "Show in Folder" : "Restart & Update")
                : "Download Update",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(_accentColor),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        };
        _primaryButton.Click += async (_, _) => await OnPrimaryClickAsync();

        if (stagedInstallerPath is not null && File.Exists(stagedInstallerPath)) _isReady = true;

        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(_laterButton);
        Grid.SetColumn(_primaryButton, 1);
        buttonRow.Children.Add(_primaryButton);

        var headerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };
        var footerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };

        var content = new StackPanel { Spacing = 12, Children = { headerRow, headerDivider, _statusText, notesLink, _progressBar, footerDivider, buttonRow } };
        Content = new Border { Background = new SolidColorBrush(_palette.SurfaceDeep), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(16), Child = content };
    }

    protected override void OnClosing(WindowClosingEventArgs e) { if (!_canClose) e.Cancel = true; base.OnClosing(e); }

    public static void ShowFor(UpdateInfo update, string? stagedPath = null)
    {
        Dispatcher.UIThread.Post(() => ShowDialog(new UpdateDialog(update, stagedPath)));
    }

    public static void ShowForHotfix(HotfixCandidate hotfix, string? stagedTxPath = null)
    {
        Dispatcher.UIThread.Post(() => ShowDialog(new UpdateDialog(hotfix, stagedTxPath)));
    }

    private static void ShowDialog(UpdateDialog dialog)
    {
        Window? owner = null;
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = desktop.MainWindow;
            if (main is { IsVisible: true }) owner = main;
        }
        if (owner is not null) dialog.Show(owner); else dialog.Show();
    }

    public UpdateDialog(HotfixCandidate hotfix, string? stagedTxPath = null)
    {
        _hotfix = hotfix;
        _isHotfixMode = true;
        _stagedHotfixTxPath = stagedTxPath;
        var manualPackage = hotfix.RequiresManualPackageInstall;
        _palette = ThemeResolver.GetCurrentPalette();
        (_accentColor, _accentForeground) = AccentResolver.GetCurrentAccent();
        var display = HotfixVersion.Format(hotfix.BaseVersion, hotfix.HotfixLevel);
        var isBeta = HotfixVersion.NormalizeBaseVersion(hotfix.BaseVersion)?
            .EndsWith("-BETA", StringComparison.OrdinalIgnoreCase) == true;

        Title = "Kodo - Critical Hotfix Available";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = new SolidColorBrush(_palette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var iconBadge = new Border
        {
            Background = new SolidColorBrush(_accentColor),
            CornerRadius = new CornerRadius(8),
            Width = 40, Height = 40,
            Child = new TextBlock { Text = "↑", FontSize = 20, Foreground = new SolidColorBrush(_accentForeground), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var titleText = new TextBlock
        {
            Text = $"Critical Kodo {display} hotfix is available",
            FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { iconBadge, titleText } };

        _statusText = new TextBlock
        {
            Text = isBeta
                ? manualPackage
                    ? $"This is an optional hotfix for the unstable Kodo {hotfix.BaseVersion} beta. Download the rebuilt package only if you choose to use this beta; then install it using the instructions below."
                    : $"This hotfix is for the unstable Kodo {hotfix.BaseVersion} beta. Beta updates are optional and will only be installed if you choose Download Hotfix."
                : manualPackage
                ? hotfix.AssetName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
                    ? $"This critical hotfix fixes an important issue. Download the rebuilt Debian package, then install it with your software manager or `sudo apt install ./<package>.deb`."
                    : "This critical hotfix fixes an important issue. Download the rebuilt AppImage, then replace your current AppImage with the downloaded file."
                : stagedTxPath is not null && File.Exists(stagedTxPath)
                ? "Hotfix downloaded and verified. Choose Restart & Update when you're ready."
                : $"A critical hotfix for Kodo {display} is available and fixes an important issue. Download it now.",
            FontSize = 13, Foreground = new SolidColorBrush(_palette.TextMuted), TextWrapping = TextWrapping.Wrap,
        };
        var notesLink = new TextBlock { Text = "View release notes", FontSize = 12, Foreground = new SolidColorBrush(_accentColor), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        notesLink.PointerPressed += (_, _) => OpenUrl(hotfix.ReleaseNotesUrl);

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 8, IsVisible = false, Foreground = new SolidColorBrush(_accentColor), Background = new SolidColorBrush(_palette.BadgeBg), CornerRadius = new CornerRadius(4) };

        _laterButton = new Button { Content = "Later", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8), Background = new SolidColorBrush(_palette.BadgeBg), Foreground = new SolidColorBrush(_palette.TextMuted), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        _laterButton.Click += (_, _) => Close();

        _primaryButton = new Button
        {
            Content = manualPackage ? "Download Full Package" : stagedTxPath is not null ? "Restart & Update" : "Download Hotfix",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(_accentColor),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        };
        _primaryButton.Click += async (_, _) => await OnPrimaryClickAsync();

        if (stagedTxPath is not null && File.Exists(stagedTxPath)) _isReady = true;

        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(_laterButton);
        Grid.SetColumn(_primaryButton, 1);
        buttonRow.Children.Add(_primaryButton);

        var headerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };
        var footerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };

        var content = new StackPanel { Spacing = 12, Children = { headerRow, headerDivider, _statusText, notesLink, _progressBar, footerDivider, buttonRow } };
        Content = new Border { Background = new SolidColorBrush(_palette.SurfaceDeep), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(16), Child = content };
    }

    private async Task OnPrimaryClickAsync()
    {
        if (_isHotfixMode)
        {
            await OnHotfixPrimaryClickAsync();
            return;
        }
        if (_isReady && _stagedInstallerPath is not null && File.Exists(_stagedInstallerPath))
        {
            if (_linuxManualOnly)
            {
                UpdateService.OpenFolderInFileManager(_stagedInstallerPath);
                _statusText.Text = UpdateService.LinuxReadyBlurb(_stagedInstallerPath);
                return;
            }
            _canClose = false;
            _primaryButton.IsEnabled = false;
            _laterButton.IsEnabled = false;
            _statusText.Text = "Launching updater… Kodo will restart shortly.";
            _progressBar.IsVisible = true;
            _progressBar.IsIndeterminate = true;
            await Task.Delay(300);
            try { UpdateService.PrepareAndLaunchUpdate(_stagedInstallerPath, _update!.Version, restartAfterUpdate: true, expectedSha256: _update!.Sha256); }
            catch (Exception ex)
            {
                _statusText.Text = $"Couldn't start updater: {ex.Message}";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
            }
            return;
        }
        await BeginDownloadAsync();
    }

    private async Task BeginDownloadAsync()
    {
        if (_isDownloading) return;
        _isDownloading = true;
        _canClose = false;
        _primaryButton.IsEnabled = false;
        _laterButton.IsEnabled = false;
        _primaryButton.Content = "Downloading…";
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _statusText.Text = "Downloading the update…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            _progressBar.Value = p.Fraction;
            _statusText.Text = $"Downloading… {p.Label}";
        });

        try
        {
            _stagedInstallerPath = await UpdateService.DownloadInstallerAsync(_update!, progress);
            _isDownloading = false;
            _isReady = true;
            _progressBar.IsVisible = false;
            if (_linuxManualOnly)
            {
                _statusText.Text = UpdateService.LinuxReadyBlurb(_stagedInstallerPath);
                _primaryButton.Content = "Show in Folder";
            }
            else
            {
                _statusText.Text = "Download complete. Ready to install – choose Restart & Update when you're ready.";
                _primaryButton.Content = "Restart & Update";
            }
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _laterButton.Content = "Later";
            _canClose = true;
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            _statusText.Text = ex is InvalidDataException && ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase)
                ? "The update failed integrity verification and was discarded. Try again; if it persists, download manually from the releases page."
                : "The update couldn't be downloaded. Check your connection and try again.";
            _primaryButton.Content = "Retry";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _progressBar.IsVisible = false;
            _canClose = true;
            KodoDiagnostics.WriteDiagnosticLog("UpdateDialog.BeginDownloadAsync", ex, false, "Warning", "AutoUpdate");
        }
    }

    private async Task OnHotfixPrimaryClickAsync()
    {
        if (_hotfix is null) return;
        if (_stagedManualHotfixPackagePath is not null && File.Exists(_stagedManualHotfixPackagePath))
        {
            UpdateService.OpenFolderInFileManager(_stagedManualHotfixPackagePath);
            _statusText.Text = _hotfix.AssetName.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
                ? "Install the downloaded Debian package with your software manager or `sudo apt install ./<package>.deb`, then relaunch Kodo."
                : "Replace your current AppImage with the downloaded file, keep it executable, then relaunch Kodo. This full package includes the critical hotfix.";
            return;
        }
        if (_isReady && _stagedHotfixTxPath is not null && File.Exists(_stagedHotfixTxPath))
        {
            _canClose = false;
            _primaryButton.IsEnabled = false;
            _laterButton.IsEnabled = false;
            _statusText.Text = "Launching updater… Kodo will restart shortly.";
            _progressBar.IsVisible = true;
            _progressBar.IsIndeterminate = true;
            await Task.Delay(300);
            try { UpdateService.LaunchUpdaterAndExit(_stagedHotfixTxPath); }
            catch (Exception ex)
            {
                _statusText.Text = $"Couldn't start updater: {ex.Message}";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
            }
            return;
        }
        await BeginHotfixDownloadAsync();
    }

    private async Task BeginHotfixDownloadAsync()
    {
        if (_hotfix is null) return;
        if (_isDownloading) return;
        _isDownloading = true;
        _canClose = false;
        _primaryButton.IsEnabled = false;
        _laterButton.IsEnabled = false;
        _primaryButton.Content = "Downloading…";
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _statusText.Text = "Downloading the hotfix…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            _progressBar.Value = p.Fraction;
            _statusText.Text = $"Downloading… {p.Label}";
        });

        try
        {
            var prepared = await HotfixStaging.PrepareAsync(_hotfix, progress);
            if (prepared.ManualPackagePath is not null)
            {
                _stagedManualHotfixPackagePath = prepared.ManualPackagePath;
                _isDownloading = false;
                _isReady = true;
                _progressBar.IsVisible = false;
                _statusText.Text = "Critical hotfix package downloaded and checksum verified. Open its folder to install it.";
                _primaryButton.Content = "Show in Folder";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
                return;
            }
            if (prepared.AlreadyInstalled || prepared.TransactionPath is null)
            {
                _statusText.Text = "This hotfix is already installed.";
                _primaryButton.Content = "Close";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
                _isDownloading = false;
                return;
            }
            _stagedHotfixTxPath = prepared.TransactionPath;
            _isDownloading = false;
            _isReady = true;
            _progressBar.IsVisible = false;
            _statusText.Text = "Hotfix downloaded and verified. Ready to apply – choose Restart & Update when you're ready.";
            _primaryButton.Content = "Restart & Update";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _laterButton.Content = "Later";
            _canClose = true;
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            _statusText.Text = ex is UnauthorizedAccessException
                ? ex.Message
                : ex is InvalidDataException
                    ? "The hotfix failed verification and was discarded. The current installation is untouched."
                    : "The hotfix couldn't be downloaded. Check your connection and try again.";
            _primaryButton.Content = "Retry";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _progressBar.IsVisible = false;
            _canClose = true;
            KodoDiagnostics.WriteDiagnosticLog("UpdateDialog.BeginHotfixDownloadAsync", ex, false, "Warning", "HotfixUpdate");
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }
}
