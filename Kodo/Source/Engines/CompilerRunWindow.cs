// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kodo;

internal sealed class CompilerRunWindow : Window
{
    private static readonly TimeSpan StateTransition = TimeSpan.FromMilliseconds(220);

    private readonly ConsoleTerminal _terminal = new();
    private readonly string _exePath;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private readonly Color _accent;
    private readonly Color _accentForeground;
    private readonly DialogThemePalette _palette;
    private readonly Border _surface;
    private readonly Border _headerBadge;
    private readonly Border _statusPill;
    private readonly Border _statusDot;
    private readonly TextBlock _statusText;
    private readonly Button _rerunButton;

    public IReadOnlyDictionary<string, KeyGesture>? TerminalKeybinds
    {
        get => _terminal.Keybinds;
        set => _terminal.Keybinds = value;
    }

    public CompilerRunWindow(
        string action,
        string toolLabel,
        string commandDisplay,
        string exePath,
        string arguments,
        string workingDirectory,
        Color terminalBackground,
        Color terminalForeground,
        Color accent,
        Color accentForeground,
        Color muted)
    {
        _exePath = exePath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        _accent = accent;
        _accentForeground = accentForeground;
        _palette = ThemeResolver.GetCurrentPalette();
        _terminal.ApplyKodoTheme(terminalBackground, terminalForeground, accent, muted);

        Title = $"Kodo - {action} ({toolLabel})";
        Width = 820;
        Height = 520;
        MinWidth = 520;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(_palette.Background);

        var headline = new TextBlock
        {
            Text = action,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text),
        };

        var commandText = new TextBlock
        {
            Text = commandDisplay,
            FontSize = 11.5,
            FontFamily = KodoFonts.MonoFamily,
            Foreground = new SolidColorBrush(_palette.TextMuted),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(commandText, commandDisplay);

        var titleBlock = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { headline, commandText },
        };

        _headerBadge = DialogHeader.CreateBadge(
            new SolidColorBrush(_accent),
            new SolidColorBrush(_accentForeground),
            KodoIcons.CommandLine,
            tile: 40,
            glyph: 22);
        _headerBadge.Transitions = new Transitions
        {
            new BrushTransition { Property = Border.BackgroundProperty, Duration = StateTransition },
        };

        _rerunButton = new Button
        {
            Content = "Re-run",
            Padding = new Thickness(18, 8),
            Background = new SolidColorBrush(_accent),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Focusable = false,
        };
        _rerunButton.Click += (_, _) => RunCommand();

        var exitButton = new Button
        {
            Content = "Exit",
            Padding = new Thickness(16, 8),
            Background = new SolidColorBrush(_palette.BadgeBg),
            Foreground = new SolidColorBrush(_palette.TextMuted),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Focusable = false,
        };
        exitButton.Click += (_, _) => Close();

        AnimationKit.EnableHoverLift(_rerunButton);
        AnimationKit.EnableHoverLift(exitButton);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _rerunButton, exitButton },
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        headerGrid.Children.Add(_headerBadge);
        headerGrid.Children.Add(SetGridColumn(titleBlock, 1));
        headerGrid.Children.Add(SetGridColumn(headerActions, 2));

        var header = new Border
        {
            Background = new SolidColorBrush(_palette.Background),
            CornerRadius = new CornerRadius(
                KodoDesignTokens.CardRadius.TopLeft,
                KodoDesignTokens.CardRadius.TopRight,
                0,
                0),
            Padding = new Thickness(14, 12),
            Child = headerGrid,
        };

        var headerDivider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(_palette.Border),
            Opacity = 0.9,
        };

        _statusDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(_accent),
            VerticalAlignment = VerticalAlignment.Center,
            Transitions = new Transitions
            {
                new BrushTransition { Property = Border.BackgroundProperty, Duration = StateTransition },
            },
        };

        _statusText = new TextBlock
        {
            Text = "Preparing",
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Transitions = new Transitions
            {
                new BrushTransition { Property = TextBlock.ForegroundProperty, Duration = StateTransition },
            },
        };

        _statusPill = new Border
        {
            Background = new SolidColorBrush(_palette.BadgeBg),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.PillRadius,
            Padding = new Thickness(10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { _statusDot, _statusText },
            },
        };

        var directoryText = new TextBlock
        {
            Text = workingDirectory,
            FontSize = 10.5,
            FontFamily = KodoFonts.MonoFamily,
            Foreground = new SolidColorBrush(_palette.TextDim),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
        };
        ToolTip.SetTip(directoryText, workingDirectory);

        var statusGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        statusGrid.Children.Add(_statusPill);
        statusGrid.Children.Add(SetGridColumn(directoryText, 2));

        var statusStrip = new Border
        {
            Padding = new Thickness(14, 8),
            Child = statusGrid,
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Children =
            {
                header,
                SetGridRow(headerDivider, 1),
                SetGridRow(statusStrip, 2),
                SetGridRow(_terminal, 3),
            },
        };

        var surfaceContent = new Grid
        {
            Children =
            {
                layout,
                new Border
                {
                    BorderBrush = new SolidColorBrush(_palette.Border),
                    BorderThickness = new Thickness(1),
                    CornerRadius = KodoDesignTokens.CardRadius,
                    IsHitTestVisible = false,
                },
            },
        };

        _surface = new Border
        {
            Background = new SolidColorBrush(_palette.SurfaceDeep),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.CardRadius,
            Margin = new Thickness(16),
            ClipToBounds = true,
            Child = surfaceContent,
        };
        AnimationKit.PresetFade(_surface, 10);
        Content = _surface;

        Opened += OnOpened;
        Closed += (_, _) => _terminal.Stop();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        AnimationKit.FadeIn(_surface, 190, 10);
        RunCommand();
    }

    private void RunCommand()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ApplyState(failed: true);
            _statusText.Text = "Unavailable on this platform";
            return;
        }

        ApplyState(failed: null);
        _statusText.Text = "Running";
        SetBusy(busy: true);

        _terminal.Start(_exePath, _arguments, _workingDirectory);
        _terminal.SessionExited += OnSessionExited;

        Dispatcher.UIThread.Post(() => _terminal.Focus(), DispatcherPriority.Input);
    }

    private void OnSessionExited(object? sender, TerminalProcessHandle exitedHandle)
    {
        _terminal.SessionExited -= OnSessionExited;
        var exitCode = exitedHandle.ExitCode;
        Dispatcher.UIThread.Post(() =>
        {
            var failed = exitCode is int code && code != 0;
            ApplyState(failed);
            SetBusy(busy: false);
            _statusText.Text = exitCode is int value
                ? (failed ? $"Failed - exit {value}" : $"Finished - exit {value}")
                : (failed ? "Failed" : "Finished");
        });
    }

    private void SetBusy(bool busy)
    {
        _rerunButton.IsEnabled = !busy;
        _rerunButton.Opacity = busy ? KodoDesignTokens.DisabledOpacity : 1d;
    }

    private void ApplyState(bool? failed)
    {
        var stateBrush = failed switch
        {
            true => KodoDesignTokens.DangerBrush,
            false => KodoDesignTokens.SuccessBrush,
            _ => new SolidColorBrush(_accent),
        };
        _headerBadge.Background = stateBrush;
        _statusDot.Background = stateBrush;
        _statusPill.Background = failed switch
        {
            true => KodoDesignTokens.DangerFillBrush,
            false => KodoDesignTokens.SuccessFillBrush,
            _ => new SolidColorBrush(_palette.BadgeBg),
        };
        _statusPill.BorderBrush = failed switch
        {
            true => KodoDesignTokens.DangerStrokeBrush,
            false => KodoDesignTokens.SuccessStrokeBrush,
            _ => new SolidColorBrush(_palette.Border),
        };
        _statusText.Foreground = stateBrush;
        _rerunButton.Background = stateBrush;
        _rerunButton.Foreground = failed is true ? Brushes.White : new SolidColorBrush(_accentForeground);
    }

    private static Control SetGridColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control SetGridRow(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }
}
