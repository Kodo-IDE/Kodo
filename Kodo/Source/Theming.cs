// Licensed under GPL-v3.0
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using Microsoft.Win32;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Animation;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using DiscordAssetsModel = DiscordRPC.Assets;
using DiscordRpcClient = DiscordRPC.DiscordRpcClient;
using DiscordRichPresenceModel = DiscordRPC.RichPresence;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{
    private void RaiseMany(params string[] names) { foreach (var n in names) OnPropertyChanged(n); }

    private static string GetThemeColor(JsonElement theme, string propertyName, string fallback) =>
        theme.TryGetProperty(propertyName, out var value) ? value.GetString() ?? fallback : fallback;

    private void ApplyThemeToEditor()
    {
        if (EditorTextBox is null) return;
        _colorSwatchGenerator.PanelBrush = WindowBackgroundBrush;
        _colorSwatchGenerator.BorderBrush = SurfaceBorderBrush;
        _colorSwatchGenerator.TextBrush = PrimaryTextBrush;
        _colorSwatchGenerator.AccentBrush = AccentBrush;
        EditorTextBox.Background = EditorBackgroundBrush;
        EditorTextBox.Foreground = PrimaryTextBrush;
        EditorTextBox.LineNumbersForeground = MutedTextBrush;
        EditorTextBox.TextArea.SelectionBrush = AccentBrush.ToImmutable() is ISolidColorBrush b
            ? new SolidColorBrush(b.Color, 0.3)
            : new SolidColorBrush(Color.Parse("#8C00FF"), 0.3);
        EditorTextBox.TextArea.SelectionForeground = PrimaryTextBrush;
        EditorTextBox.TextArea.TextView.LinkTextForegroundBrush = Brush.Parse("#5BA3D9");
        EditorTextBox.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;
        _indentGuideRenderer.GuideBrush = MutedTextBrush.ToImmutable() is ISolidColorBrush mutedBrush
            ? new SolidColorBrush(mutedBrush.Color, 0.4)
            : new SolidColorBrush(Color.Parse("#808080"), 0.4);
        IBrush deadCodeGrey = IsLightThemeActive
            ? new SolidColorBrush(Color.Parse("#5F6B7A"), 0.30)
            : new SolidColorBrush(Color.Parse("#9AA0A6"), 0.22);
        _deadCodeHighlightRenderer.HighlightBrush = deadCodeGrey;
        _errorHighlightRenderer.StripeGreyBrush = deadCodeGrey;
        var basePrimary = PrimaryTextBrush.ToImmutable() is ISolidColorBrush primarySolid
            ? primarySolid.Color
            : Color.Parse(IsLightThemeActive ? "#202124" : "#F4F4F4");
        _deadCodeTextBrightener.TextBrush = new SolidColorBrush(
            PushTowardExtreme(basePrimary, towardWhite: !IsLightThemeActive, amount: 0.3));
        _errorTextDarkener.IsLightTheme = IsLightThemeActive;
        _errorTextDarkener.TextBrush = new SolidColorBrush(Color.Parse("#000000"));
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        EditorTextBox.TextArea.TextView.Redraw();

    }

    private static IBrush GetAccentForeground(IBrush accent)
    {
        if (accent.ToImmutable() is not ISolidColorBrush solid)
            return Brushes.White;
        return GetReadableForeground(solid.Color);
    }

    private static void SyncSystemAccentResources(IBrush accent)
    {
        if (accent.ToImmutable() is not ISolidColorBrush solid) return;
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var c = solid.Color;
        resources["SystemAccentColor"] = c;
        resources["SystemAccentColorLight1"] = LightenColor(c, 0.15);
        resources["SystemAccentColorLight2"] = LightenColor(c, 0.30);
        resources["SystemAccentColorLight3"] = LightenColor(c, 0.45);
        resources["SystemAccentColorDark1"]  = DarkenColor(c, 0.15);
        resources["SystemAccentColorDark2"]  = DarkenColor(c, 0.30);
        resources["SystemAccentColorDark3"]  = DarkenColor(c, 0.45);
    }

    private static Color LightenColor(Color c, double amount) => WindowsThemeHelper.Lighten(c, amount);
    private static IBrush GetReadableForeground(Color background) => WindowsThemeHelper.GetReadableForeground(background);

    private static IBrush EnsureReadableTextBrush(IBrush candidate, params IBrush[] backgrounds)
    {
        const double MinimumReadableContrast = 4.5; // WCAG AA, normal text

        if (candidate.ToImmutable() is not ISolidColorBrush candidateSolid)
            return candidate;

        var worstContrast = double.MaxValue;
        Color worstBackground = default;
        var foundSolidBackground = false;

        foreach (var background in backgrounds)
        {
            if (background.ToImmutable() is not ISolidColorBrush backgroundSolid)
                continue;
            foundSolidBackground = true;
            var contrast = GetContrastRatio(candidateSolid.Color, backgroundSolid.Color);
            if (contrast < worstContrast)
            {
                worstContrast = contrast;
                worstBackground = backgroundSolid.Color;
            }
        }

        if (!foundSolidBackground || worstContrast >= MinimumReadableContrast)
            return candidate;

        KodoDiagnostics.LogDebug(
            $"Theme text colour failed contrast check ({worstContrast:0.00}:1, needs {MinimumReadableContrast:0.0}:1) " +
            "against its own background - falling back to a safe colour so text stays readable.");

        return GetReadableForeground(worstBackground);
    }

    private IBrush GetCachedBrush(string colorValue)
    {
        if (_brushCache.TryGetValue(colorValue, out var brush))
            return brush;

        brush = Brush.Parse(colorValue);
        _brushCache[colorValue] = brush;
        return brush;
    }

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["WindowBackground"]="#F3F3F3", ["TopBar"]="#FFFFFF", ["Sidebar"]="#EFF2F7", ["Button"]="#E3E8F1",
        ["ButtonHover"]="#D5DDE9", ["EditorBackground"]="#FFFFFF", ["Card"]="#F7F9FC", ["PrimaryText"]="#202124",
        ["MutedText"]="#5F6B7A", ["SurfaceBorder"]="#D7DCE5", ["Accent"]="#8C00FF"
    };
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["WindowBackground"]="#1E1E1E", ["TopBar"]="#181818", ["Sidebar"]="#181818", ["Button"]="#252526",
        ["ButtonHover"]="#313437", ["EditorBackground"]="#1E1E1E", ["Card"]="#252526", ["PrimaryText"]="#F4F4F4",
        ["MutedText"]="#A0A0A0", ["SurfaceBorder"]="#2B2B2B", ["Accent"]="#8C00FF"
    };

    private void SetThemeBrushesCore(string themeName)
    {
        _requestedThemeName = themeName;
        var effectiveThemeName = string.Equals(themeName, "System", StringComparison.OrdinalIgnoreCase) ? ResolveSystemThemeName() : themeName;
        var extensionTheme = ThemeExtensions.Select(e => e.ThemeDefinition!).FirstOrDefault(t => string.Equals(t.ThemeId, effectiveThemeName, StringComparison.OrdinalIgnoreCase));
        if (extensionTheme is not null)
        {
            CurrentThemeName = extensionTheme.ThemeId;
            Application.Current!.RequestedThemeVariant = string.Equals(extensionTheme.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;
            WindowBackgroundBrush = GetCachedBrush(extensionTheme.WindowBackground);
            TopBarBrush = GetCachedBrush(extensionTheme.TopBar);
            SidebarBrush = GetCachedBrush(extensionTheme.Sidebar);
            ButtonBrush = GetCachedBrush(extensionTheme.Button);
            ButtonHoverBrush = GetCachedBrush(extensionTheme.ButtonHover);
            EditorBackgroundBrush = GetCachedBrush(extensionTheme.EditorBackground);
            CardBrush = GetCachedBrush(extensionTheme.Card);
            PrimaryTextBrush = GetCachedBrush(extensionTheme.PrimaryText);
            MutedTextBrush = GetCachedBrush(extensionTheme.MutedText);
            PrimaryTextBrush = EnsureReadableTextBrush(PrimaryTextBrush, CardBrush, WindowBackgroundBrush, EditorBackgroundBrush, SidebarBrush, TopBarBrush, ButtonBrush);
            MutedTextBrush = EnsureReadableTextBrush(MutedTextBrush, CardBrush, WindowBackgroundBrush, EditorBackgroundBrush, SidebarBrush, TopBarBrush, ButtonBrush);
            SurfaceBorderBrush = GetCachedBrush(extensionTheme.SurfaceBorder);
            AccentBrush = GetCachedBrush(extensionTheme.Accent);
            _themeAccentHex = extensionTheme.Accent; _hasThemeAccent = true;
            _windowBackgroundHex = extensionTheme.WindowBackground; _hasWindowBackground = true;
            ThemeAccentPreviewBrush = GetCachedBrush(extensionTheme.Accent);
        }
        else
        {
            CurrentThemeName = effectiveThemeName == "Light" ? "Light" : "Dark";
            Application.Current!.RequestedThemeVariant = CurrentThemeName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            var pal = CurrentThemeName == "Light" ? LightPalette : DarkPalette;
            WindowBackgroundBrush = GetCachedBrush(pal["WindowBackground"]);
            TopBarBrush = GetCachedBrush(pal["TopBar"]);
            SidebarBrush = GetCachedBrush(pal["Sidebar"]);
            ButtonBrush = GetCachedBrush(pal["Button"]);
            ButtonHoverBrush = GetCachedBrush(pal["ButtonHover"]);
            EditorBackgroundBrush = GetCachedBrush(pal["EditorBackground"]);
            CardBrush = GetCachedBrush(pal["Card"]);
            PrimaryTextBrush = GetCachedBrush(pal["PrimaryText"]);
            MutedTextBrush = GetCachedBrush(pal["MutedText"]);
            SurfaceBorderBrush = GetCachedBrush(pal["SurfaceBorder"]);
            AccentBrush = GetCachedBrush(pal["Accent"]);
            _themeAccentHex = pal["Accent"]; _windowBackgroundHex = pal["WindowBackground"];
            _hasThemeAccent = false; _hasWindowBackground = false;
            ThemeAccentPreviewBrush = GetCachedBrush(pal["Accent"]);
        }
        var windowsHex = GetWindowsAccentColor() ?? "#0078D4";
        try { WindowsAccentPreviewBrush = GetCachedBrush(windowsHex); } catch { WindowsAccentPreviewBrush = GetCachedBrush("#0078D4"); }
        var resolvedAccent = _accentColorMode switch { "theme" => _themeAccentHex, "windows" => windowsHex, "custom" => _customAccentHex, _ => "#8C00FF" };
        try { AccentBrush = GetCachedBrush(resolvedAccent); } catch { AccentBrush = GetCachedBrush("#8C00FF"); }
        AccentForegroundBrush = GetAccentForeground(AccentBrush);
        SyncSystemAccentResources(AccentBrush);
    }

    private void ApplyThemeBrushes(string themeName) { SetThemeBrushesCore(themeName); RefreshSystemThemePreview(); }

    private void ApplyTheme(string themeName)
    {
        SetThemeBrushesCore(themeName);
        RaiseMany(nameof(WindowBackgroundBrush), nameof(TopBarBrush), nameof(SidebarBrush), nameof(ButtonBrush), nameof(ButtonHoverBrush), nameof(EditorBackgroundBrush), nameof(CardBrush), nameof(PrimaryTextBrush), nameof(MutedTextBrush), nameof(SurfaceBorderBrush), nameof(HasThemeAccent), nameof(IsAccentKodo), nameof(IsAccentTheme), nameof(ThemeAccentPreviewBrush), nameof(IsSystemThemeActive), nameof(IsDarkThemeActive), nameof(IsLightThemeActive));
        RefreshSystemThemePreview();
        ApplyAccentOverride(); ApplyThemeToEditor(); SaveSettings(); RefreshState(fullRefresh: true); RefreshExtensionTheme();
    }

    private void ApplyAccentOverride()
    {
        var windowsHex = GetWindowsAccentColor() ?? "#0078D4";
        try { WindowsAccentPreviewBrush = GetCachedBrush(windowsHex); }
        catch { WindowsAccentPreviewBrush = GetCachedBrush("#0078D4"); }
        OnPropertyChanged(nameof(WindowsAccentPreviewBrush));

        // In "kodo" mode, always use the fixed Kodo purple regardless of any active theme.
        if (_accentColorMode == "kodo")
        {
            try { AccentBrush = GetCachedBrush("#8C00FF"); }
            catch { AccentBrush = GetCachedBrush("#8C00FF"); }
            AccentForegroundBrush = GetAccentForeground(AccentBrush);
            SyncSystemAccentResources(AccentBrush);
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(AccentForegroundBrush));
            ApplyThemeToEditor();
            return;
        }

        var hex = _accentColorMode switch
        {
            "theme"   => _themeAccentHex,
            "windows" => windowsHex,
            "custom"  => _customAccentHex,
            _         => "#8C00FF"
        };
        try { AccentBrush = GetCachedBrush(hex); }
        catch { AccentBrush = GetCachedBrush("#8C00FF"); }
        AccentForegroundBrush = GetAccentForeground(AccentBrush);
        SyncSystemAccentResources(AccentBrush);
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(AccentForegroundBrush));
        ApplyThemeToEditor();
    }

    private static string? GetWindowsAccentColor() => WindowsThemeHelper.GetWindowsAccentHex();
    private static bool? GetWindowsAppsUseLightTheme() => WindowsThemeHelper.GetIsLightTheme();

    private static string ResolveSystemThemeName() =>
        GetWindowsAppsUseLightTheme() == true ? "Light" : "Dark";

    private void RefreshSystemThemePreview()
    {
        var isLight = GetWindowsAppsUseLightTheme() == true;
        SystemThemePreviewBackground = GetCachedBrush(isLight ? "#FFFFFF" : "#1E1E1E");
        SystemThemePreviewBorder     = GetCachedBrush(isLight ? "#D7DCE5" : "#2B2B2B");
        OnPropertyChanged(nameof(SystemThemePreviewBackground));
        OnPropertyChanged(nameof(SystemThemePreviewBorder));
    }

    private async void AccentColorPickerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Window? dialog = null;
        var confirmed = false;

        var initialColor = Color.Parse("#8C00FF");
        try { initialColor = Color.Parse(_customAccentHex); } catch { /* use fallback */ }

        RgbToHsv(initialColor.R, initialColor.G, initialColor.B,
            out var hue, out var sat, out var val);

        var hueCanvas = new Canvas { Width = 300, Height = 20 };
        var hueGrad   = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        foreach (var (offset, h) in new (double, double)[]
            { (0,0),(1/6d,60),(2/6d,120),(3/6d,180),(4/6d,240),(5/6d,300),(1,360) })
        {
            HsvToRgb(h, 1, 1, out var hr2, out var hg2, out var hb2);
            hueGrad.GradientStops.Add(new GradientStop(Color.FromRgb(hr2, hg2, hb2), offset));
        }
        var hueRect = new Avalonia.Controls.Shapes.Rectangle
            { Width = 300, Height = 20, Fill = hueGrad, RadiusX = 4, RadiusY = 4 };
        hueCanvas.Children.Add(hueRect);

        var hueCursor = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 4, Height = 24, Fill = Brushes.White, RadiusX = 2, RadiusY = 2,
            Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1,
        };
        Canvas.SetTop(hueCursor, -2);
        Canvas.SetLeft(hueCursor, hue / 360.0 * 296);
        hueCanvas.Children.Add(hueCursor);

        const double svSize   = 300.0;
        const double svHeight = 180.0;
        var svCanvas = new Canvas { Width = svSize, Height = svHeight };

        var svHueFill      = new Avalonia.Controls.Shapes.Rectangle { Width = svSize, Height = svHeight, RadiusX = 4, RadiusY = 4 };
        var svWhiteOverlay = new Avalonia.Controls.Shapes.Rectangle { Width = svSize, Height = svHeight, RadiusX = 4, RadiusY = 4 };
        var svBlackOverlay = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = svSize, Height = svHeight, RadiusX = 4, RadiusY = 4,
            Fill  = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0,   0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 1),
                },
            },
        };

        void RefreshSvSquare()
        {
            HsvToRgb(hue, 1, 1, out var hr3, out var hg3, out var hb3);
            svHueFill.Fill = new SolidColorBrush(Color.FromRgb(hr3, hg3, hb3));
            svWhiteOverlay.Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0,   255, 255, 255), 1),
                },
            };
        }
        RefreshSvSquare();

        svCanvas.Children.Add(svHueFill);
        svCanvas.Children.Add(svWhiteOverlay);
        svCanvas.Children.Add(svBlackOverlay);

        var svCursor = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            Stroke = Brushes.White, StrokeThickness = 2,
            Fill   = new SolidColorBrush(Colors.Transparent),
        };
        Canvas.SetLeft(svCursor, sat * svSize   - 6);
        Canvas.SetTop (svCursor, (1 - val) * svHeight - 6);
        svCanvas.Children.Add(svCursor);

        var previewBorder = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
            BorderBrush = SurfaceBorderBrush, BorderThickness = new Thickness(1),
        };

        var hexInput = new TextBox
        {
            Text            = _customAccentHex,
            PlaceholderText = "#RRGGBB",
            MaxLength       = 7,
            Foreground      = PrimaryTextBrush,
            Background      = ButtonBrush,
            BorderBrush     = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(8, 6),
            FontSize        = 13,
            CaretBrush      = PrimaryTextBrush,
            Width           = 110,
        };

        void UpdateAll()
        {
            HsvToRgb(hue, sat, val, out var r2, out var g2, out var b2);
            var c = Color.FromRgb(r2, g2, b2);
            previewBorder.Background = new SolidColorBrush(c);
            hexInput.Text = $"#{r2:X2}{g2:X2}{b2:X2}";
            Canvas.SetLeft(hueCursor, hue / 360.0 * 296);
            Canvas.SetLeft(svCursor,  sat * svSize   - 6);
            Canvas.SetTop (svCursor,  (1 - val) * svHeight - 6);
            RefreshSvSquare();
        }
        UpdateAll();

        hueCanvas.PointerPressed  += (_, pe) =>
        {
            pe.Pointer.Capture(hueCanvas);
            hue = Math.Clamp(pe.GetPosition(hueCanvas).X / 300.0 * 360, 0, 360);
            UpdateAll();
        };
        hueCanvas.PointerMoved    += (_, pe) =>
        {
            if (pe.Pointer.Captured != hueCanvas) return;
            hue = Math.Clamp(pe.GetPosition(hueCanvas).X / 300.0 * 360, 0, 360);
            UpdateAll();
        };
        hueCanvas.PointerReleased += (_, pe) => pe.Pointer.Capture(null);

        svCanvas.PointerPressed  += (_, pe) =>
        {
            pe.Pointer.Capture(svCanvas);
            var p = pe.GetPosition(svCanvas);
            sat = Math.Clamp(p.X / svSize,        0, 1);
            val = Math.Clamp(1 - p.Y / svHeight,  0, 1);
            UpdateAll();
        };
        svCanvas.PointerMoved    += (_, pe) =>
        {
            if (pe.Pointer.Captured != svCanvas) return;
            var p = pe.GetPosition(svCanvas);
            sat = Math.Clamp(p.X / svSize,        0, 1);
            val = Math.Clamp(1 - p.Y / svHeight,  0, 1);
            UpdateAll();
        };
        svCanvas.PointerReleased += (_, pe) => pe.Pointer.Capture(null);

        hexInput.TextChanged += (_, _) =>
        {
            try
            {
                var t = hexInput.Text?.Trim() ?? "";
                if (!t.StartsWith('#')) t = "#" + t;
                var c = Color.Parse(t);
                RgbToHsv(c.R, c.G, c.B, out hue, out sat, out val);
                previewBorder.Background = new SolidColorBrush(c);
                Canvas.SetLeft(hueCursor, hue / 360.0 * 296);
                Canvas.SetLeft(svCursor,  sat * svSize   - 6);
                Canvas.SetTop (svCursor,  (1 - val) * svHeight - 6);
                RefreshSvSquare();
            }
            catch { /* wait for valid hex */ }
        };

        hexInput.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)  { confirmed = true; dialog!.Close(); }
            if (ke.Key == Key.Escape) { dialog!.Close(); }
        };

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
                    Text = IsAmericanEnglish ? "Custom Accent Color" : "Custom Accent Colour",
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var headerDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        var footerDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        var inner = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                headerRow,
                new TextBlock { Text = IsAmericanEnglish ? "Pick a colour for highlights, selections and buttons" : "Pick a colour for highlights, selections and buttons", FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap, Opacity = 0.92 },
                headerDivider,
                new Border
                {
                    Background = WindowBackgroundBrush,
                    BorderBrush = SurfaceBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Child = new StackPanel
                    {
                        Spacing = 12,
                        Children = { svCanvas, hueCanvas }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { previewBorder, hexInput },
                },
                footerDivider,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush,
                            () => dialog!.Close()),
                        CreateDialogButton("Apply", AccentBrush, AccentBrush, AccentForegroundBrush,
                            () => { confirmed = true; dialog!.Close(); }),
                    }
                }
            }
        };

        dialog = new Window
        {
            Width = 360,
            SizeToContent = SizeToContent.Height,
            MinWidth = 340,
            MaxHeight = 560,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = IsAmericanEnglish ? "Custom Accent Color" : "Custom Accent Colour",
            Background = WindowBackgroundBrush,
            Content = new Border
            {
                Background = CardBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                Child = inner
            }
        };

        dialog.Opened += (_, _) => { hexInput.Focus(); hexInput.SelectAll(); };
        await dialog.ShowDialog(this);

        if (!confirmed) return;
        var hex = hexInput.Text?.Trim() ?? string.Empty;
        if (!hex.StartsWith('#')) hex = "#" + hex;
        try
        {
            Brush.Parse(hex);
            _customAccentHex = hex;
            CustomAccentHex  = hex;
            AccentColorMode  = "custom";
            ApplyAccentOverride();
            SaveSettings();
        }
        catch { /* invalid hex - ignore */ }
    }

    private static void RgbToHsv(byte r, byte g, byte b,
        out double h, out double s, out double v)
    {
        var rf = r / 255.0; var gf = g / 255.0; var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        v = max;
        s = max == 0 ? 0 : delta / max;
        if (delta == 0) { h = 0; return; }
        if      (max == rf) h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
        else                h = 60 * (((rf - gf) / delta) + 4);
        if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v,
        out byte r, out byte g, out byte b)
    {
        if (s == 0) { r = g = b = (byte)(v * 255); return; }
        var i = (int)(h / 60) % 6;
        var f = h / 60 - Math.Floor(h / 60);
        var p = v * (1 - s); var q = v * (1 - f * s); var t = v * (1 - (1 - f) * s);
        var (rf, gf, bf) = i switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        r = (byte)(rf * 255); g = (byte)(gf * 255); b = (byte)(bf * 255);
    }

    private void AccentKodoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AccentColorMode = "kodo";
        ApplyAccentOverride();
        SaveSettings();
    }

    private void AccentThemeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AccentColorMode = "theme";
        ApplyAccentOverride();
        SaveSettings();
    }

    private void AccentWindowsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AccentColorMode = "windows";
        ApplyAccentOverride();
        SaveSettings();
    }

    private void WindowsAccentPollTimer_OnTick(object? sender, EventArgs e)
    {
        var current = GetWindowsAccentColor() ?? string.Empty;
        if (current == _lastSeenWindowsAccentHex) return;
        _lastSeenWindowsAccentHex = current;
        ApplyAccentOverride();
        if (_accentColorMode == "windows")
        {
            ApplyThemeToEditor();
            RefreshExtensionTheme();
        }
    }

    private void WindowsThemePollTimer_OnTick(object? sender, EventArgs e)
    {
        var current = ResolveSystemThemeName();
        if (current == _lastSeenWindowsThemeName) return;
        _lastSeenWindowsThemeName = current;
        RefreshSystemThemePreview();
        if (IsSystemThemeActive)
            ApplyTheme("System");
    }

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string themeName })
            ApplyTheme(themeName);
    }

    private void ThemeGroupHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ThemeExtensionGroup group })
            group.IsExpanded = !group.IsExpanded;
    }

    private void ThemeDarkButton_OnClick(object? sender, RoutedEventArgs e)  => ApplyTheme("Dark");

    private void ThemeLightButton_OnClick(object? sender, RoutedEventArgs e) => ApplyTheme("Light");

    private void ThemesTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenMarketplaceTab(ExtensionsTabModes.Themes);

}
