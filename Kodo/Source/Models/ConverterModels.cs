// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using Avalonia.Controls;

namespace Kodo.Models;

// Computes how much room a file-tree row's name TextBlock has to work with, so
// long names truncate less (or not at all) as the user widens the explorer panel
// via ExplorerPanelSplitter, instead of being capped at a fixed pixel width.
// Extracted from MainWindow.axaml.cs:56
public sealed class ExplorerItemNameMaxWidthConverter : IMultiValueConverter
{
    public static readonly ExplorerItemNameMaxWidthConverter Instance = new();

    private const double MinWidth = 40;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double panelWidth || values[1] is not double indentWidth)
            return MinWidth;

        return Math.Max(MinWidth, panelWidth - indentWidth - Kodo.MainWindow.FileTreeRowFixedOverhead);
    }
}

// Converts bold flag to FontWeight for the release-notes run template.
// Extracted from MainWindow.axaml.cs:15542
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeight.SemiBold : FontWeight.Regular;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MarketplaceTileWidthConverter : IValueConverter
{
    public static readonly MarketplaceTileWidthConverter Instance = new();

    private const int Columns = 5;
    private const double HorizontalPadding = 48; // ScrollViewer Padding="24" on each side
    private const double ScrollbarGutter = 20; // room for the vertical scrollbar when visible
    private const double TileMargin = 12; // Border.extensiontile Margin="6" on each side
    private const double MinTileWidth = 120; // floor so tiles never collapse to nothing

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width) return MinTileWidth;

        var available = width - HorizontalPadding - ScrollbarGutter;
        var perColumn = available / Columns - TileMargin;
        return Math.Max(MinTileWidth, perColumn);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// ---------------------------------------------------------------------------
// Consolidated from previously fragmented files:
// BoolInverter.cs, HighlightedTextBlock.cs, InlinesBehavior.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Inverts a boolean value. Used to toggle visibility of mutually
/// exclusive layouts inside a single DataTemplate.
/// </summary>
public sealed class BoolInverter : IValueConverter
{
    public static readonly BoolInverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}

/// <summary>
/// Converts (string text, IReadOnlyList&lt;int&gt; indices) into an
/// InlineCollection for a TextBlock, highlighting the specified character positions.
/// Implements IMultiValueConverter for use with MultiBinding in XAML.
/// </summary>
public sealed class HighlightConverter : IMultiValueConverter
{
    public static readonly HighlightConverter Instance = new();

    public static IBrush DefaultHighlightBrush { get; set; } =
        new SolidColorBrush(Color.FromArgb(100, 255, 210, 0));

    public static FontWeight HighlightFontWeight { get; set; } = FontWeight.Bold;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Count: 2 } || values[0] is not string text || values[1] is not IReadOnlyList<int> indices)
            return null;

        var inlines = new InlineCollection();
        if (string.IsNullOrEmpty(text) || indices is not { Count: > 0 })
        {
            inlines.Add(new Run(text ?? string.Empty));
            return inlines;
        }

        var highlightSet = new HashSet<int>(indices);
        var segmentStart = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var currentIsHighlight = !atEnd && highlightSet.Contains(i);

            if (i == segmentStart)
                continue;

            var prevIsHighlight = highlightSet.Contains(segmentStart);
            if (!atEnd && currentIsHighlight == prevIsHighlight)
                continue;

            var segment = text.Substring(segmentStart, i - segmentStart);
            inlines.Add(prevIsHighlight
                ? new Run(segment) { FontWeight = HighlightFontWeight, Foreground = DefaultHighlightBrush }
                : new Run(segment));

            segmentStart = i;
        }

        if (inlines.Count == 0)
            inlines.Add(new Run(text));

        return inlines;
    }
}

/// <summary>
/// Same as <see cref="HighlightConverter"/> but uses a subtler brush and
/// normal weight for preview-line matches (as opposed to filename matches).
/// </summary>
public sealed class PreviewHighlightConverter : IMultiValueConverter
{
    public static readonly PreviewHighlightConverter Instance = new();

    private static readonly IBrush PreviewBrush =
        new SolidColorBrush(Color.FromArgb(60, 255, 210, 0));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Count: 2 } || values[0] is not string text || values[1] is not IReadOnlyList<int> indices)
            return null;

        var inlines = new InlineCollection();
        if (string.IsNullOrEmpty(text) || indices is not { Count: > 0 })
        {
            inlines.Add(new Run(text ?? string.Empty));
            return inlines;
        }

        var highlightSet = new HashSet<int>(indices);
        var segmentStart = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var currentIsHighlight = !atEnd && highlightSet.Contains(i);

            if (i == segmentStart)
                continue;

            var prevIsHighlight = highlightSet.Contains(segmentStart);
            if (!atEnd && currentIsHighlight == prevIsHighlight)
                continue;

            var segment = text.Substring(segmentStart, i - segmentStart);
            inlines.Add(prevIsHighlight
                ? new Run(segment) { Foreground = PreviewBrush }
                : new Run(segment));

            segmentStart = i;
        }

        if (inlines.Count == 0)
            inlines.Add(new Run(text));

        return inlines;
    }
}

public static class InlinesBehavior
{
    public static readonly AttachedProperty<IEnumerable<Inline>?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IEnumerable<Inline>?>(
            "Source", typeof(InlinesBehavior));

    static InlinesBehavior()
    {
        SourceProperty.Changed.AddClassHandler<TextBlock>((textBlock, e) =>
        {
            textBlock.Inlines?.Clear();

            if (e.NewValue is IEnumerable<Inline> inlines)
                textBlock.Inlines?.AddRange(inlines);
        });
    }

    public static void SetSource(TextBlock element, IEnumerable<Inline>? value) =>
        element.SetValue(SourceProperty, value);

    public static IEnumerable<Inline>? GetSource(TextBlock element) =>
        element.GetValue(SourceProperty);
}
