// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Kodo.Models;
using System;

namespace Kodo;

internal static class DialogHeader
{
    public static StackPanel Create(IBrush badge, IBrush title, IBrush icon, string svg, string titleText,
        double tile = 36, double glyph = 20, double titleFontSize = 17)
    {
        var headerTitle = new TextBlock
        {
            Text = titleText,
            FontSize = titleFontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = title,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { CreateBadge(badge, icon, svg, tile, glyph), headerTitle },
        };
    }

    public static Border CreateBadge(IBrush badge, IBrush icon, string svg, double tile = 36, double glyph = 20)
    {
        var iconColor = (icon as ISolidColorBrush)?.Color ?? Color.Parse("#FFFFFF");
        var badgeBorder = new Border
        {
            Width = tile,
            Height = tile,
            CornerRadius = KodoDesignTokens.ControlRadius,
            Background = badge,
            VerticalAlignment = VerticalAlignment.Center,
            Child = BuildImage(svg, iconColor, glyph),
        };
        if (SettingsStore.Current.Fluent2ThemeEnabled)
            badgeBorder.Bind(Border.BoxShadowProperty, new Binding(nameof(Border.Background))
            {
                Source = badgeBorder,
                Converter = DepthShadowConverter.Instance,
            });
        return badgeBorder;
    }

    public static Control WithDepth(Button button)
    {
        if (!SettingsStore.Current.Fluent2ThemeEnabled)
            return button;
        button.Classes.Add("depth");
        var inset = button.Margin;
        button.Margin = default;
        var depth = new Border
        {
            CornerRadius = button.CornerRadius,
            Padding = KodoDesignTokens.DepthPadding,
            Margin = new Thickness(inset.Left, inset.Top, inset.Right, inset.Bottom - KodoDesignTokens.DepthLift),
            BoxShadow = KodoDesignTokens.DepthShadow,
            Child = button,
        };
        depth.Bind(Border.BackgroundProperty, new Binding(nameof(Button.Background))
        {
            Source = button,
            Converter = DepthBrushConverter.Instance,
        });
        return depth;
    }

    internal static Color DepthColor(IBrush? brush)
    {
        var color = (brush as ISolidColorBrush)?.Color ?? Color.Parse("#16151C");
        return Color.FromRgb(
            (byte)Math.Round(color.R * 0.45),
            (byte)Math.Round(color.G * 0.45),
            (byte)Math.Round(color.B * 0.45));
    }

    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Border CreateDivider(IBrush borderBrush, Thickness? margin = null) =>
        new Border
        {
            Height = 1,
            Background = borderBrush,
            Opacity = 0.9,
            Margin = margin ?? new Thickness(0, 4),
        };

    public static Border CreateSurface(Control content, IBrush card, IBrush border, Thickness padding, Thickness margin, bool clip = false) =>
        new Border
        {
            Background = card,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.CardRadius,
            Padding = padding,
            Margin = margin,
            ClipToBounds = clip,
            Child = content,
        };

    private static Avalonia.Svg.Skia.SvgSource LoadTinted(string svg, Color color)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var tinted = svg.Replace("currentColor", hex, StringComparison.Ordinal);
        return Avalonia.Svg.Skia.SvgSource.LoadFromSvg(tinted)!;
    }

    private static Image BuildImage(string svg, Color color, double size) =>
        new Image
        {
            Source = new Avalonia.Svg.Skia.SvgImage { Source = LoadTinted(svg, color) },
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
}
