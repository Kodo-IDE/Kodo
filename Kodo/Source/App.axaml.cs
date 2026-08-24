// Licensed under GPL-v3.0
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

public partial class App : Application
{
    // Guards against multiple concurrent crash dialogs.
    private static int _isCrashDialogOpen;

    // Shared colours from DialogPalette.
    private static readonly Color KodoDarkSurface     = DialogPalette.Surface;
    private static readonly Color KodoDarkSurfaceDeep = DialogPalette.SurfaceDeep;
    private static readonly Color KodoDarkBorder      = DialogPalette.Border;
    private static readonly Color KodoDarkBadgeBg     = DialogPalette.BadgeBg;
    private static readonly Color KodoTextMuted       = DialogPalette.TextMuted;
    private static readonly Color KodoTextDim         = DialogPalette.TextDim;
    private static readonly Color KodoTokenBlue       = DialogPalette.TokenBlue;  // source badge
    private static readonly Color KodoTokenOrange     = DialogPalette.TokenOrange;  // stack trace

    // Initialization

    // Loads AXAML resources/styles and wires up global exception handlers.
    public override void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException       += TaskScheduler_OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException      += DispatcherUiThread_OnUnhandledException;
        AvaloniaXamlLoader.Load(this);
    }

    // Called once the framework finishes initializing; creates the main window.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // desktop.Args[0] is the file path when launched via "Open with" / double-click.
            var startupFilePath = desktop.Args?.Length > 0 ? desktop.Args[0] : null;
            var mainWindow = new MainWindow(startupFilePath);
            desktop.MainWindow = mainWindow;

            SingleInstance.StartListening(handoffPath =>
                Dispatcher.UIThread.Post(() => mainWindow.ActivateFromSecondaryInstance(handoffPath)));

            AptabaseClient.TrackEvent("app_launched");
            desktop.Exit += async (_, _) => await AptabaseClient.FlushAsync();
        }

#if !DEBUG
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RegisterFileAssociations();
        }
#endif
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!CheckPendingUpdateSentinel())
                CheckForUpdatesInBackground();

            LaunchStandaloneUpdaterIfNeeded();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Auto-update

    [SupportedOSPlatform("windows")]
    private static void LaunchStandaloneUpdaterIfNeeded()
    {
        try
        {
            if (!UpdateService.IsAutoUpdateEnabledInSettings())
            {
                // User has auto-update off entirely; make sure no logon task is left resident.
                UpdateService.RemoveAutostartRegistration();
                return;
            }

            var exeDir = AppContext.BaseDirectory;
            var updaterPath = Path.Combine(exeDir, "KodoUpdater.exe");
            if (!File.Exists(updaterPath))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = exeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            UpdateService.EnsureAutostartRegistered();
        }
        catch (Exception ex)
        {
            // Best-effort - if this fails, updates simply fall back to the in-app
            KodoDiagnostics.LogWarning("App.LaunchStandaloneUpdaterIfNeeded", ex, operation: "AutoUpdate");
        }
    }

    private static bool CheckPendingUpdateSentinel()
    {
        try
        {
            var pending = PendingUpdateService.TryGetPendingUpdate();
            if (pending is null) return false;

            var (version, installerPath) = pending.Value;
            var update = new UpdateInfo(
                Version: version,
                ReleaseNotesUrl: "https://github.com/Kodo-IDE/Kodo/releases",
                AssetDownloadUrl: string.Empty, // unused: installer is already on disk
                AssetName: Path.GetFileName(installerPath),
                AssetSizeBytes: 0);

            UpdateDialog.ShowFor(update, installerPath);
            return true;
        }
        catch
        {
            // Best-effort; falls through to the normal live update check.
            return false;
        }
    }

    private static void CheckForUpdatesInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!UpdateService.IsAutoUpdateEnabledInSettings())
                    return;

                await Task.Delay(TimeSpan.FromSeconds(4));

                if (UpdateService.IsAutoUpdateInBackgroundEnabledInSettings())
                    return;

                await UpdateService.CheckAndHandleUpdateAsync(installInBackground: false);
            }
            catch
            {
                // Update checking must never crash the app.
            }
        });
    }

    // Windows file-association registration

    [SupportedOSPlatform("windows")]
    private static void RegisterFileAssociations()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            var command = $"\"{exe}\" \"%1\"";

            // Registers the application under HKCU.
            using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Kodo.exe"))
            {
                appKey.SetValue("FriendlyAppName", "Kodo");
                using var openKey = appKey.CreateSubKey(@"shell\open\command");
                openKey.SetValue("", command);
            }

            // Extensions to appear in the "Open with" menu.
            string[] extensions =
            [
                ".txt", ".md", ".cs", ".fs", ".vb",
                ".js", ".ts", ".jsx", ".tsx",
                ".html", ".htm", ".css", ".scss", ".sass",
                ".json", ".xml", ".yaml", ".yml", ".toml", ".jsonc", ".jsonl",
                ".py", ".rb", ".go", ".rs", ".cpp", ".c", ".h",
                ".sh", ".bat", ".ps1",
                ".sln", ".csproj", ".fsproj",
                ".gitignore", ".env", ".ini", ".cfg", ".config",
                ".log",
            ];

            foreach (var ext in extensions)
            {
                using var extKey = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{ext}\OpenWithList\Kodo.exe");
                extKey?.SetValue("", "");
            }
        }
        catch
        {
            // Registration failure must never crash the app.
        }
    }

    // Global exception handlers

    private static void CurrentDomain_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception) return;

        AptabaseClient.TrackEvent("app_crash", exception.Message);

        // Critical: unhandled AppDomain exception - may terminate the process.
        KodoDiagnostics.LogCritical("AppDomain.UnhandledException", exception, e.IsTerminating);
        ShowCrashDialog("AppDomain.UnhandledException", exception, isTerminating: e.IsTerminating);
    }

    private static void TaskScheduler_OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Unobserved Task exception - recoverable; logged as a Warning, no crash.log.
        KodoDiagnostics.LogWarning("TaskScheduler.UnobservedTaskException", e.Exception, operation: "Background task");
        // Marks the exception as observed.
        e.SetObserved();
        // Shows the crash-style dialog, marked non-terminating.
        ShowCrashDialog("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
    }

    private static void DispatcherUiThread_OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Critical: exception on the UI thread - may leave the UI in a broken state.
        KodoDiagnostics.LogCritical("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);
        ShowCrashDialog("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);
        e.Handled = true;
    }

    // ShowCrashDialog dispatches a modal error dialog to the UI thread.

    private static void ShowCrashDialog(string source, Exception exception, bool isTerminating)
    {
        if (Interlocked.CompareExchange(ref _isCrashDialogOpen, 1, 0) != 0)
            return;

        try
        {
            var logPath = KodoDiagnostics.MainLogFilePath;

            if (Dispatcher.UIThread.CheckAccess())
            {
                // Already on the UI thread - fire and forget.
                _ = ShowCrashDialogOnUiThreadAsync(source, exception, logPath, isTerminating);
                return;
            }

            // Posts to the UI thread rather than blocking.
            if (isTerminating)
            {
                // Non-blocking post.
                Dispatcher.UIThread.Post(
                    () => _ = ShowCrashDialogOnUiThreadAsync(source, exception, logPath, isTerminating),
                    DispatcherPriority.MaxValue);

                // Spin-waits up to 30s for the dialog to show/dismiss.
                for (var i = 0; i < 300 && _isCrashDialogOpen == 1; i++)
                    Thread.Sleep(100);
            }
            else
            {
                // Recoverable crash - posts without blocking the caller.
                Dispatcher.UIThread.Post(
                    () => _ = ShowCrashDialogOnUiThreadAsync(source, exception, logPath, isTerminating),
                    DispatcherPriority.MaxValue);
            }
        }
        catch
        {
            // Dispatcher invocation must never crash the app.
        }
    }

    private static async Task ShowCrashDialogOnUiThreadAsync(string source, Exception exception, string logPath, bool isTerminating)
    {
        // Marks the dialog as open.
        Interlocked.Exchange(ref _isCrashDialogOpen, 1);
        try
        {
            // Uses the main window as owner only when it is still open and visible.
            Window? owner = null;
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var main = desktop.MainWindow;
                if (main is { IsVisible: true })
                    owner = main;
            }

            var dialog = BuildCrashDialog(source, exception, logPath, isTerminating, owner);

            // Falls back to Show() + TCS when no owner is available.
            if (owner is not null)
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                var tcs = new TaskCompletionSource<bool>();
                dialog.Closed += (_, _) => tcs.TrySetResult(true);
                dialog.Show();
                await tcs.Task;
            }
        }
        catch
        {
            // The crash dialog itself must never crash the app.
        }
        finally
        {
            // Marks the dialog as dismissed.
            Interlocked.Exchange(ref _isCrashDialogOpen, 0);
        }
    }

    // Builds the crash dialog entirely in code, with no AXAML dependency.
    private static Window BuildCrashDialog(
        string  source,
        Exception exception,
        string  logPath,
        bool    isTerminating,
        Window? owner)
    {
        // Header
        var titleText = new TextBlock
        {
            Text        = "Kodo crashed",
            FontSize    = 16,
            FontWeight  = FontWeight.SemiBold,
            Foreground  = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };

        var subtitleText = new TextBlock
        {
            Text = isTerminating
                ? "An unrecoverable error occurred and Kodo will now close. The crash details have been saved."
                : "An unexpected error occurred, but Kodo may still be running. The crash details have been saved.",
            FontSize    = 13,
            Foreground  = new SolidColorBrush(KodoTextMuted),
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 4, 0, 0),
        };

        // Terminating warning, shown only when isTerminating is true.
        var terminatingBanner = new Border
        {
            IsVisible        = isTerminating,
            Background       = new SolidColorBrush(Color.Parse("#3D1A00")),
            BorderBrush      = new SolidColorBrush(Color.Parse("#7A3A00")),
            BorderThickness  = new Thickness(1),
            CornerRadius     = new CornerRadius(6),
            Padding          = new Thickness(10, 6),
            Child = new TextBlock
            {
                Text        = "⚠ The application will close after you dismiss this dialog.",
                FontSize    = 12,
                Foreground  = new SolidColorBrush(Color.Parse("#FFA040")),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        // Source badge
        var sourceBadge = new Border
        {
            Background       = new SolidColorBrush(KodoDarkBadgeBg),
            BorderBrush      = new SolidColorBrush(KodoDarkBorder),
            BorderThickness  = new Thickness(1),
            CornerRadius     = new CornerRadius(6),
            Padding          = new Thickness(10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text       = source,
                FontSize   = 12,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                Foreground = new SolidColorBrush(KodoTokenBlue),
            },
        };

        var metadataText = new SelectableTextBlock
        {
            Text         = KodoDiagnostics.BuildDiagnosticSummary(source, isTerminating),
            FontSize     = 11,
            FontFamily   = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            Foreground   = new SolidColorBrush(KodoTextMuted),
            TextWrapping = TextWrapping.Wrap,
        };

        // Exception details (scrollable, selectable)
        var exceptionText = new SelectableTextBlock
        {
            Text       = KodoDiagnostics.BuildDiagnosticPayload(source, exception, isTerminating, KodoSeverity.Critical, redactPaths: true),
            FontSize   = 12,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            Foreground = new SolidColorBrush(KodoTokenOrange),
            TextWrapping = TextWrapping.Wrap,
        };

        var exceptionScroll = new ScrollViewer
        {
            Content  = exceptionText,
            MaxHeight = 260,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var exceptionBorder = new Border
        {
            Background      = new SolidColorBrush(KodoDarkSurfaceDeep),
            BorderBrush     = new SolidColorBrush(KodoDarkBorder),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(12),
            Child           = exceptionScroll,
        };

        // Log path note
        var logPathText = new TextBlock
        {
            Text         = "Full details in: %AppData%\\Kodo\\kodo.log",
            FontSize     = 11,
            Foreground   = new SolidColorBrush(KodoTextDim),
            TextWrapping = TextWrapping.Wrap,
        };

        // Action buttons
        var copyButton = new Button
        {
            Content             = "Copy to Clipboard",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(16, 8),
            Background          = new SolidColorBrush(KodoDarkBadgeBg),
            Foreground          = new SolidColorBrush(KodoTextMuted),
            BorderBrush         = new SolidColorBrush(KodoDarkBorder),
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
        };

        var (accentColor, accentForeground) = AccentResolver.GetCurrentAccent();

        var reportButton = new Button
        {
            Content             = "Report on GitHub",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(16, 8),
            Background          = new SolidColorBrush(KodoDarkBadgeBg),
            Foreground          = new SolidColorBrush(KodoTextMuted),
            BorderBrush         = new SolidColorBrush(KodoDarkBorder),
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
            Margin              = new Thickness(8, 0, 0, 0),
        };

        var dismissButton = new Button
        {
            Content             = isTerminating ? "Close" : "Dismiss",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding             = new Thickness(20, 8),
            Background          = new SolidColorBrush(accentColor),
            Foreground          = new SolidColorBrush(accentForeground),
            BorderThickness     = new Thickness(0),
            CornerRadius        = new CornerRadius(8),
        };

        // Left side: Copy + Report. Right side: Dismiss.
        var leftButtons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children    = { copyButton, reportButton },
        };

        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(leftButtons);
        Grid.SetColumn(dismissButton, 1);
        buttonRow.Children.Add(dismissButton);

        // Layout
        var content = new StackPanel
        {
            Spacing  = 12,
            Margin   = new Thickness(20),
            Children =
            {
                titleText,
                subtitleText,
                terminatingBanner,
                sourceBadge,
                metadataText,
                exceptionBorder,
                logPathText,
                buttonRow,
            },
        };

        var dialog = new Window
        {
            Title  = "Kodo - Crash Report",
            Width  = 560,
            SizeToContent = SizeToContent.Height,
            MinWidth  = 400,
            MinHeight = 200,
            MaxHeight = 740,
            CanResize = true,
            WindowStartupLocation = owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(KodoDarkSurface),
            Content    = content,
        };

        // Copies the full exception + source to the clipboard.
        copyButton.Click += async (_, _) =>
        {
            try
            {
                var clip = TopLevel.GetTopLevel(dialog)?.Clipboard;
                if (clip is not null)
                {
                    var text = KodoDiagnostics.BuildDiagnosticPayload(source, exception, isTerminating, KodoSeverity.Critical, redactPaths: true);
                    await clip.SetTextAsync(text);
                    copyButton.Content   = "Copied!";
                    copyButton.Foreground = Brushes.White;
                }
            }
            catch
            {
                // Clipboard failures must not crash the crash dialog.
            }
        };

        reportButton.Click += (_, _) =>
        {
            try
            {
                // Pre-fills a GitHub issue with the exception type as the title.
                var title = Uri.EscapeDataString($"[Crash] {exception.GetType().Name}: {exception.Message}"
                    .Replace("\r", "").Replace("\n", " ").Trim());
                var url = "https://github.com/Kodo-IDE/Kodo/issues/new?title=" +
                          Uri.EscapeDataString($"[Crash] {exception.GetType().Name}: {exception.Message}"
                              .Replace("\r", "").Replace("\n", " ").Trim()) +
                          "&body=" + Uri.EscapeDataString(
                    KodoDiagnostics.BuildDiagnosticPayload(source, exception, isTerminating, KodoSeverity.Critical, redactPaths: true)) +
                    "&labels=bug&template=bug_report.md";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Opening the browser must not crash the crash dialog.
            }
        };

        dismissButton.Click += (_, _) => dialog.Close();

        return dialog;
    }

}