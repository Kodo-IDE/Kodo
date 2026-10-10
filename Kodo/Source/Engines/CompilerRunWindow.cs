// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Microsoft.Win32;
using Kodo.Models;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kodo;

internal sealed class CompilerRunWindow : Window
{
    private readonly ConsoleTerminal _terminal = new();
    private readonly string _exePath;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private readonly string _commandDisplay;
    private readonly Color _accent;
    private readonly Color _accentForeground;
    private readonly DialogThemePalette _palette;
    private TextBlock _statusText = null!;
    private Button _rerunButton = null!;

    public IReadOnlyDictionary<string, KeyGesture>? TerminalKeybinds
    {
        get => _terminal.Keybinds;
        set => _terminal.Keybinds = value;
    }

    public CompilerRunWindow(
        string title,
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
        _commandDisplay = commandDisplay;
        _exePath = exePath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        _accent = accent;
        _accentForeground = accentForeground;
        _palette = ThemeResolver.GetCurrentPalette();
        _terminal.ApplyKodoTheme(terminalBackground, terminalForeground, accent, muted);

        Title = title;
        Width = 780;
        Height = 500;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(_palette.Background);
        Content = new Border
        {
            Background = new SolidColorBrush(_palette.SurfaceDeep),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.CardRadius,
            Margin = new Thickness(16),
            ClipToBounds = true,
            Child = BuildContent(),
        };

        Opened += OnOpened;
        Closed += (_, _) => _terminal.Stop();
    }

    private Control BuildContent()
    {
        _statusText = new TextBlock
        {
            Text = "Starting...",
            FontSize = 12,
            Foreground = new SolidColorBrush(_palette.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _rerunButton = new Button
        {
            Content = "Re-run",
            Padding = new Thickness(14, 6),
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
            Padding = new Thickness(14, 6),
            Background = new SolidColorBrush(_palette.BadgeBg),
            Foreground = new SolidColorBrush(_palette.TextMuted),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Focusable = false,
        };
        exitButton.Click += (_, _) => Close();

        var commandText = new TextBlock
        {
            Text = _commandDisplay,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(commandText, _commandDisplay);

        var commandBlock = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { commandText, _statusText },
        };

        var header = new Border
        {
            Background = new SolidColorBrush(_palette.Background),
            BorderBrush = new SolidColorBrush(_palette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    new Border
                    {
                        Width = 3,
                        Height = 28,
                        CornerRadius = new CornerRadius(2),
                        Background = new SolidColorBrush(_accent),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    SetGridColumn(commandBlock, 1),
                    SetGridColumn(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _rerunButton, exitButton },
                    }, 2),
                },
            },
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { header, SetGridRow(_terminal, 1) },
        };

        return layout;
    }

    private void OnOpened(object? sender, EventArgs e) => RunCommand();

    private void RunCommand()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _statusText.Text = "Terminal is only available on Windows and Linux.";
            return;
        }

        _statusText.Text = "Running...";
        _rerunButton.IsEnabled = false;

        _terminal.Start(_exePath, _arguments, _workingDirectory);

        _terminal.SessionExited += OnSessionExited;

        void OnSessionExited(object? s, TerminalProcessHandle exitedHandle)
        {
            _terminal.SessionExited -= OnSessionExited;
            Dispatcher.UIThread.Post(() =>
            {
                _statusText.Text = "Finished - the command exited. You can inspect the output below or re-run it.";
                _rerunButton.IsEnabled = true;
            });
        }

        Dispatcher.UIThread.Post(() => _terminal.Focus(), DispatcherPriority.Input);
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
