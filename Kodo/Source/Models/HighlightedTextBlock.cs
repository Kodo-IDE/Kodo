// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Kodo.Models;

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
