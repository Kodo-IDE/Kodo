// Licensed under GPL-v3.0
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{

    private void NetworkChange_OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Dispatcher.UIThread.Post(() => RefreshMarketplaceConnectivityState());

    private static bool HasActiveWirelessConnection() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(networkInterface =>
            networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
            networkInterface.OperationalStatus == OperationalStatus.Up &&
            networkInterface.GetIPProperties().UnicastAddresses.Any(address => !System.Net.IPAddress.IsLoopback(address.Address)));

    private static bool HasActiveInternetConnection() =>
        NetworkInterface.GetIsNetworkAvailable() &&
        NetworkInterface.GetAllNetworkInterfaces().Any(networkInterface =>
            networkInterface.OperationalStatus == OperationalStatus.Up &&
            networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
            networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Tunnel);

    private static bool IsGitHubRateLimitException(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests };

    private static bool IsOfflineException(Exception exception)
    {
        if (!HasActiveInternetConnection())
            return true;
        if (exception is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                if (IsOfflineException(inner))
                    return true;
        }
        var current = exception;
        while (current is not null)
        {
            if (current is System.Net.Sockets.SocketException)
                return true;
            if (current is HttpRequestException hre && hre.InnerException is System.Net.Sockets.SocketException)
                return true;
            if (current is HttpRequestException hre2 && hre2.InnerException is not null && IsOfflineException(hre2.InnerException))
                return true;
            if (current.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("No internet", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Unable to resolve", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && current.Message.Contains("host", StringComparison.OrdinalIgnoreCase))
                return true;
            current = current.InnerException;
        }
        return false;
    }

    private static string DescribeFetchFailure(Exception exception)
    {
        if (IsGitHubRateLimitException(exception))
            return "GitHub's API rate limit was hit. Wait a few minutes, then try again.";
        if (IsOfflineException(exception))
            return "No internet connection. Marketplace is offline - using cached data if available.";
        return exception.Message;
    }

    private async Task CutEditorSelectionAsync()
    {
        if (EditorTextBox?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var text = sel.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
        sel.ReplaceSelectionWithText(string.Empty);
    }

    private async Task<UnsavedTabAction> ShowUnsavedTabDialogAsync(EditorTab tab)
    {
        var result = UnsavedTabAction.Cancel;
        Window? dialog = null;
        dialog = new Window
        {
            Width = 440,
            SizeToContent = SizeToContent.Height,
            MinWidth = 400,
            MaxHeight = 320,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Unsaved Changes",
            Background = WindowBackgroundBrush,
            Content = new Border
            {
                Background = CardBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                Child = BuildUnsavedTabDialogContent(
                    tab,
                    () => { result = UnsavedTabAction.Save; dialog!.Close(); },
                    () => { result = UnsavedTabAction.Discard; dialog!.Close(); },
                    () => { result = UnsavedTabAction.Cancel; dialog!.Close(); })
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private Control BuildUnsavedTabDialogContent(
        EditorTab tab,
        Action saveAction,
        Action discardAction,
        Action cancelAction)
    {
        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border
                {
                    Width = 3,
                    Height = 16,
                    Background = AccentBrush,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "Save changes before closing?",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var divider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush, cancelAction),
                CreateDialogButton("Discard", ButtonHoverBrush, SurfaceBorderBrush, PrimaryTextBrush, discardAction),
                CreateDialogButton("Save", AccentBrush, AccentBrush, AccentForegroundBrush, saveAction)
            }
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                headerRow,
                new TextBlock
                {
                    Text = $"{tab.DisplayName} has unsaved changes.",
                    Foreground = PrimaryTextBrush,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Choose Save to keep them, Discard to close without saving, or Cancel to keep editing.",
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap
                },
                divider,
                buttonRow
            }
        };
    }

    private Task ShowTutorialAsync()
    {
        try
        {
            _tutorialOpenedFromSettings = false;
            TutorialStepIndex = 0;
            if (!_hasAcceptedPrivacyPolicy)
                ResetPrivacyPolicyScrollState();
            NavigateTo(AppPage.Tutorial);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    private async Task ShowNotFoundDialogAsync(string path, bool isFolder)
    {
        try
        {
            var kind = isFolder ? "Folder" : "File";

            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 3,
                        Height = 16,
                        Background = AccentBrush,
                        CornerRadius = new CornerRadius(2),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = $"{kind} Not Found",
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PrimaryTextBrush,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            var bodyText = new TextBlock
            {
                Text = $"This {kind.ToLowerInvariant()} couldn't be opened because it isn't currently accessible. " +
                               $"It may be on a drive that isn't connected, or it may have been moved or deleted.\n\n{path}",
                FontSize = 13,
                Foreground = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var pathBorder = new Border
            {
                Background = WindowBackgroundBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8),
                Child = new SelectableTextBlock
                {
                    Text = path,
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            };

            var removeButton = new Button
            {
                Content = "Remove from Recents",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8),
                Background = ButtonBrush,
                Foreground = MutedTextBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };

            var dismissButton = new Button
            {
                Content = "OK",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(28, 8),
                Background = AccentBrush,
                Foreground = AccentForegroundBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
            };

            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            buttonRow.Children.Add(removeButton);
            Grid.SetColumn(dismissButton, 1);
            buttonRow.Children.Add(dismissButton);

            var divider = new Border
            {
                Height = 1,
                Background = SurfaceBorderBrush,
                Opacity = 0.9,
                Margin = new Thickness(0, 4)
            };

            var content = new StackPanel
            {
                Spacing = 12,
                Children = { headerRow, bodyText, pathBorder, divider, buttonRow },
            };

            var outer = new Border
            {
                Background = CardBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                Child = content
            };

            Window? dialog = null;
            dialog = new Window
            {
                Title = "Kodo - Not Found",
                Width = 500,
                SizeToContent = SizeToContent.Height,
                MinWidth = 380,
                MaxHeight = 460,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = WindowBackgroundBrush,
                Content = outer,
            };

            removeButton.Click += (_, _) => { RemoveRecentFile(path); dialog!.Close(); };
            dismissButton.Click += (_, _) => dialog!.Close();
            await dialog.ShowDialog(this);
        }
        catch (Exception dialogEx)
        {
            KodoDiagnostics.LogDebug("ShowNotFoundDialogAsync failed to display.", dialogEx);
        }
    }

    private async Task<bool> ShowConfirmationDialogAsync(
        string title,
        string body,
        string confirmLabel = "Confirm",
        string cancelLabel = "Cancel",
        bool isDestructive = false)
    {
        try
        {
            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 3,
                        Height = 16,
                        Background = isDestructive ? new SolidColorBrush(Color.Parse("#C4302B")) : AccentBrush,
                        CornerRadius = new CornerRadius(2),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PrimaryTextBrush,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            var bodyText = new TextBlock
            {
                Text = body,
                FontSize = 13,
                Foreground = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var cancelButton = new Button
            {
                Content = cancelLabel,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8),
                Background = ButtonBrush,
                Foreground = MutedTextBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };

            var confirmButton = new Button
            {
                Content = confirmLabel,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(20, 8),
                Background = isDestructive ? new SolidColorBrush(Color.Parse("#C4302B")) : AccentBrush,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
            };

            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            buttonRow.Children.Add(cancelButton);
            Grid.SetColumn(confirmButton, 1);
            buttonRow.Children.Add(confirmButton);

            var divider = new Border
            {
                Height = 1,
                Background = SurfaceBorderBrush,
                Opacity = 0.9,
                Margin = new Thickness(0, 4)
            };

            var content = new StackPanel
            {
                Spacing = 12,
                Children = { headerRow, bodyText, divider, buttonRow },
            };

            var outer = new Border
            {
                Background = CardBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                Child = content
            };

            Window? dialog = null;
            dialog = new Window
            {
                Title = "Kodo",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                MinWidth = 360,
                MaxHeight = 360,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = WindowBackgroundBrush,
                Content = outer,
            };

            var result = false;
            cancelButton.Click += (_, _) => { result = false; dialog!.Close(); };
            confirmButton.Click += (_, _) => { result = true; dialog!.Close(); };
            await dialog.ShowDialog(this);
            return result;
        }
        catch (Exception dialogEx)
        {
            KodoDiagnostics.LogDebug($"ShowConfirmationDialogAsync failed to display for '{title}'.", dialogEx);
            return false;
        }
    }

    private async Task ShowWarningDialogAsync(string context, Exception exception, bool isCritical = false)
    {
        isCritical = isCritical
            || context.StartsWith("File save", StringComparison.OrdinalIgnoreCase)
            || context.StartsWith("Auto-save", StringComparison.OrdinalIgnoreCase);

        var source = isCritical ? "MainWindow.Warning.Critical" : "MainWindow.Warning";
        KodoDiagnostics.LogWarning(source, exception, operation: context);

        // offline: suppress modal, use banner
        if (!isCritical && IsOfflineException(exception) &&
            (context.Contains("Marketplace", StringComparison.OrdinalIgnoreCase) ||
             context.Contains("Plugins", StringComparison.OrdinalIgnoreCase) ||
             context.Contains("Compiler", StringComparison.OrdinalIgnoreCase) ||
             context.Contains("News", StringComparison.OrdinalIgnoreCase) ||
             context.Contains("Announcements", StringComparison.OrdinalIgnoreCase) ||
             context.Contains("Latest release", StringComparison.OrdinalIgnoreCase) ||
             context.Contains("Icon fetch", StringComparison.OrdinalIgnoreCase)))
        {
            KodoDiagnostics.LogDebug($"Suppressed offline warning dialog for '{context}' - using banner/cached data.", exception);
            return;
        }

        if (ShouldSuppressWarningDialog(context, exception))
        {
            KodoDiagnostics.LogDebug($"Suppressed duplicate warning dialog for '{context}'.", exception);
            return;
        }

        try
        {
            var titleLabel = isCritical ? "Action required" : "Something went wrong";
            var subtitleMessage = isCritical
                ? "Kodo could not complete this file operation. Your in-editor content is still intact - try saving again or use Save As to choose a different location."
                : "Kodo ran into a problem with this operation. No data was lost - you can try again.";
            var windowTitle = isCritical ? "Kodo - Warning" : "Kodo - Notice";
            var logPath = KodoDiagnostics.MainLogFilePath;

            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Width = 3,
                        Height = 16,
                        Background = isCritical ? new SolidColorBrush(Color.Parse("#FFA040")) : AccentBrush,
                        CornerRadius = new CornerRadius(2),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = titleLabel,
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PrimaryTextBrush,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            var subtitleText = new TextBlock
            {
                Text = subtitleMessage,
                FontSize = 13,
                Foreground = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var headerDivider = new Border
            {
                Height = 1,
                Background = SurfaceBorderBrush,
                Opacity = 0.9,
                Margin = new Thickness(0, 4)
            };

            var criticalBanner = new Border
            {
                IsVisible = isCritical,
                Background = new SolidColorBrush(Color.Parse("#2D1F00")),
                BorderBrush = new SolidColorBrush(Color.Parse("#6B4800")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Child = new TextBlock
                {
                    Text = "⚠ This operation affects file data. Check the log if the problem persists.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#FFA040")),
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            var contextBadge = new Border
            {
                Background = ButtonBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = context,
                    FontSize = 12,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                    Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                },
            };

            var metadataText = new SelectableTextBlock
            {
                Text = KodoDiagnostics.BuildDiagnosticSummary(source, false, context),
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                Foreground = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var errorMessageText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(exception.Message)
                                   ? "An unexpected error occurred."
                                   : DescribeFetchFailure(exception),
                FontSize = 13,
                Foreground = PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var exceptionText = new SelectableTextBlock
            {
                Text = KodoDiagnostics.BuildDiagnosticPayload(source, exception, false, KodoSeverity.Warning, context, redactPaths: true),
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                Foreground = new SolidColorBrush(Color.Parse("#CE9178")),
                TextWrapping = TextWrapping.Wrap,
            };

            var exceptionScroll = new ScrollViewer
            {
                Content = exceptionText,
                MaxHeight = 200,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };

            var exceptionBorder = new Border
            {
                Background = WindowBackgroundBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = exceptionScroll,
            };

            var logPathText = new TextBlock
            {
                Text = "Logged to: %AppData%\\Kodo\\kodo.log",
                FontSize = 11,
                Foreground = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var copyButton = new Button
            {
                Content = "Copy to Clipboard",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8),
                Background = ButtonBrush,
                Foreground = MutedTextBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };

            var dismissButton = new Button
            {
                Content = "Dismiss",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(20, 8),
                Background = AccentBrush,
                Foreground = AccentForegroundBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
            };

            var reportButton = new Button
            {
                Content = "Report on GitHub",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8),
                Background = ButtonBrush,
                Foreground = MutedTextBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(8, 0, 0, 0),
            };

            var leftButtons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children = { copyButton, reportButton },
            };

            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            buttonRow.Children.Add(leftButtons);
            Grid.SetColumn(dismissButton, 1);
            buttonRow.Children.Add(dismissButton);

            var footerDivider = new Border
            {
                Height = 1,
                Background = SurfaceBorderBrush,
                Opacity = 0.9,
                Margin = new Thickness(0, 4)
            };

            var content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    headerRow,
                    subtitleText,
                    headerDivider,
                    criticalBanner,
                    contextBadge,
                    metadataText,
                    errorMessageText,
                    exceptionBorder,
                    logPathText,
                    footerDivider,
                    buttonRow,
                },
            };

            var outer = new Border
            {
                Background = CardBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                Child = content
            };

            Window? dialog = null;
            dialog = new Window
            {
                Title = windowTitle,
                Width = 560,
                SizeToContent = SizeToContent.Height,
                MinWidth = 400,
                MinHeight = 200,
                MaxHeight = 700,
                CanResize = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = WindowBackgroundBrush,
                Content = outer,
            };

            copyButton.Click += async (_, _) =>
            {
                try
                {
                    var clip = TopLevel.GetTopLevel(dialog)?.Clipboard;
                    if (clip is not null)
                    {
                        var text = KodoDiagnostics.BuildDiagnosticPayload(source, exception, false, KodoSeverity.Warning, context, redactPaths: true);
                        await clip.SetTextAsync(text);
                        copyButton.Content = "Copied!";
                        copyButton.Foreground = PrimaryTextBrush;
                    }
                }
                catch
                {
                }
            };

            reportButton.Click += (_, _) =>
            {
                try
                {
                    var title = Uri.EscapeDataString($"[Warning] {context}: {exception.Message}"
                        .Replace("\r", "").Replace("\n", " ").Trim());
                    var body = Uri.EscapeDataString(KodoDiagnostics.BuildDiagnosticPayload(source, exception, false, KodoSeverity.Warning, context, redactPaths: true));
                    var url = $"https://github.com/Kodo-IDE/Kodo/issues/new?title={title}&body={body}&labels=bug&template=bug_report.md";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                }
            };

            dismissButton.Click += (_, _) => dialog!.Close();
            await dialog.ShowDialog(this);
        }
        catch (Exception dialogEx)
        {
            KodoDiagnostics.LogWarning(source, dialogEx, operation: $"Warning dialog failed to display for context '{context}'");
            KodoDiagnostics.LogDebug($"ShowWarningDialogAsync failed to display for context '{context}'.", dialogEx);
        }
    }

    private static async Task<T> RunWithGitHubTimeoutAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> factory)
    {
        using var cts = new CancellationTokenSource(GitHubOperationTimeout);
        try
        {
            return await factory(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"GitHub operation '{operationName}' did not complete within " +
                $"{GitHubOperationTimeout.TotalSeconds:0} seconds and was cancelled.");
        }
    }

    private static async Task RunWithGitHubTimeoutAsync(
        string operationName,
        Func<CancellationToken, Task> factory)
    {
        await RunWithGitHubTimeoutAsync<bool>(
            operationName,
            async ct => { await factory(ct).ConfigureAwait(false); return true; })
            .ConfigureAwait(false);
    }

    private bool ShouldSuppressWarningDialog(string context, Exception exception)
    {
        var key = $"{context}|{exception.GetType().FullName}|{exception.Message}";
        var now = DateTime.UtcNow;
        if (_warningDialogCooldowns.TryGetValue(key, out var lastShownUtc) &&
            now - lastShownUtc < WarningDialogCooldown)
        {
            return true;
        }

        _warningDialogCooldowns[key] = now;
        return false;
    }

}
