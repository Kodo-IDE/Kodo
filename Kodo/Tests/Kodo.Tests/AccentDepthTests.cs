// Licensed under the GNU GPL-v3.0
using Avalonia.Media;
using Kodo.Models;
using System.Globalization;
using Xunit;

namespace Kodo.Tests;

public sealed class AccentDepthTests
{
    [Fact]
    public void DepthBrush_DarkensTheSourceColour()
    {
        var source = new SolidColorBrush(Color.Parse("#20C060"));

        var value = DepthBrushConverter.Instance.Convert(source, typeof(IBrush), null, CultureInfo.InvariantCulture);

        var brush = Assert.IsType<SolidColorBrush>(value);
        Assert.Equal(Color.FromRgb(14, 86, 43), brush.Color);
        Assert.Equal(255, brush.Color.A);
    }

    [Fact]
    public void DepthShadow_UsesSourceColourAndKeepsSoftShadow()
    {
        var source = new SolidColorBrush(Color.Parse("#2010C0"));

        var value = DepthShadowConverter.Instance.Convert(source, typeof(BoxShadows), null, CultureInfo.InvariantCulture);

        var shadows = Assert.IsType<BoxShadows>(value);
        Assert.Equal(2, shadows.Count);
        Assert.Equal(Color.FromRgb(14, 7, 86), shadows[0].Color);
        Assert.Equal(3, shadows[0].OffsetY);
        Assert.Equal(Color.Parse("#70000000"), shadows[1].Color);
    }

    [Fact]
    public void DepthBrush_FollowsAccentChanges()
    {
        var purple = Assert.IsType<SolidColorBrush>(
            DepthBrushConverter.Instance.Convert(new SolidColorBrush(Color.Parse("#8C00FF")), typeof(IBrush), null, CultureInfo.InvariantCulture));
        var green = Assert.IsType<SolidColorBrush>(
            DepthBrushConverter.Instance.Convert(new SolidColorBrush(Color.Parse("#20C060")), typeof(IBrush), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(63, 0, 115), purple.Color);
        Assert.NotEqual(purple.Color, green.Color);
    }
}
