// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kodo;
using Xunit;

namespace Kodo.Tests;

public sealed class DialogChromeTests
{
    [Fact]
    public void CreateDivider_UsesSharedHairlineGeometry()
    {
        var divider = DialogHeader.CreateDivider(Brushes.Gray);

        Assert.Equal(1, divider.Height);
        Assert.Equal(0.9, divider.Opacity);
        Assert.Equal(new Thickness(0, 4), divider.Margin);
        Assert.Same(Brushes.Gray, divider.Background);
    }

    [Fact]
    public void CreateDivider_HonoursExplicitMargin()
    {
        var divider = DialogHeader.CreateDivider(Brushes.Gray, new Thickness(0, 6));

        Assert.Equal(new Thickness(0, 6), divider.Margin);
    }

    [Fact]
    public void CreateSurface_AppliesCardChromeAndDefaults()
    {
        var content = new TextBlock { Text = "body" };

        var surface = DialogHeader.CreateSurface(content, Brushes.Black, Brushes.Gray, new Thickness(20), new Thickness(16));

        Assert.Same(content, surface.Child);
        Assert.Same(Brushes.Black, surface.Background);
        Assert.Same(Brushes.Gray, surface.BorderBrush);
        Assert.Equal(new Thickness(1), surface.BorderThickness);
        Assert.Equal(KodoDesignTokens.CardRadius, surface.CornerRadius);
        Assert.Equal(new Thickness(20), surface.Padding);
        Assert.Equal(new Thickness(16), surface.Margin);
        Assert.False(surface.ClipToBounds);
    }

    [Fact]
    public void CreateSurface_CanClipForFadingDialogs()
    {
        var surface = DialogHeader.CreateSurface(new TextBlock(), Brushes.Black, Brushes.Gray, default, new Thickness(12), clip: true);

        Assert.True(surface.ClipToBounds);
        Assert.Equal(new Thickness(0), surface.Padding);
        Assert.Equal(new Thickness(12), surface.Margin);
    }

    [Fact]
    public void DepthTokens_DescribeTheStripGeometry()
    {
        Assert.Equal(new Thickness(0, 0, 0, 3), KodoDesignTokens.DepthPadding);
        Assert.Equal(new Thickness(0, 0, 0, -3), KodoDesignTokens.DepthInset);
        Assert.Equal(3, KodoDesignTokens.DepthLift);
        Assert.Equal(BoxShadows.Parse("0 4 12 0 #70000000"), KodoDesignTokens.DepthShadow);
    }
}
