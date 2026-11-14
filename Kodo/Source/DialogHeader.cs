// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace Kodo;

internal static class DialogHeader
{
    public static StackPanel Create(IBrush badge, IBrush title, IBrush icon, string svg, string titleText,
        double tile = 36, double glyph = 20, double titleFontSize = 17)
    {
        var iconColor = (icon as ISolidColorBrush)?.Color ?? Color.Parse("#FFFFFF");
        var headerBadge = new Border
        {
            Width = tile,
            Height = tile,
            CornerRadius = KodoDesignTokens.ControlRadius,
            Background = badge,
            VerticalAlignment = VerticalAlignment.Center,
            Child = BuildImage(svg, iconColor, glyph),
        };
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
            Children = { headerBadge, headerTitle },
        };
    }

    public static Border CreateDivider(IBrush borderBrush) =>
        new Border
        {
            Height = 1,
            Background = borderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4),
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
