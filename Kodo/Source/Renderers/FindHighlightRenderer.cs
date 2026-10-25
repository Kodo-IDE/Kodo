// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Kodo.Models;

internal sealed class FindHighlightRenderer : IBackgroundRenderer
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(80, 255, 210, 0));
    private List<(int Offset, int Length)> _matches = new();
    private readonly SimpleSegment _segment = new();

    public KnownLayer Layer => KnownLayer.Background;

    public void AddMatch(int offset, int length) => _matches.Add((offset, length));

    public void SetMatches(List<(int Offset, int Length)> matches) => _matches = matches;

    public bool HasMatches => _matches.Count > 0;

    public void Clear() => _matches.Clear();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView is null || !textView.VisualLinesValid || _matches.Count == 0)
            return;

        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0)
            return;

        var viewStart = visualLines[0].FirstDocumentLine.Offset;
        var viewEnd = visualLines[^1].LastDocumentLine.EndOffset;

        var geoBuilder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            CornerRadius = 2
        };

        var low = 0;
        var high = _matches.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var match = _matches[middle];
            if ((long)match.Offset + match.Length < viewStart) low = middle + 1;
            else high = middle;
        }

        var segmentStart = -1;
        var segmentEnd = -1;
        for (var index = low; index < _matches.Count && _matches[index].Offset <= viewEnd; index++)
        {
            var (offset, length) = _matches[index];
            if (length <= 0 || offset + length < viewStart)
                continue;
            var end = offset + length;
            if (segmentStart < 0)
            {
                segmentStart = offset;
                segmentEnd = end;
            }
            else if (offset <= segmentEnd)
            {
                segmentEnd = Math.Max(segmentEnd, end);
            }
            else
            {
                AddGeometrySegment(textView, geoBuilder, segmentStart, segmentEnd);
                segmentStart = offset;
                segmentEnd = end;
            }
        }
        if (segmentStart >= 0)
            AddGeometrySegment(textView, geoBuilder, segmentStart, segmentEnd);

        var geometry = geoBuilder.CreateGeometry();
        if (geometry is not null)
            drawingContext.DrawGeometry(HighlightBrush, null, geometry);
    }

    private void AddGeometrySegment(TextView textView, BackgroundGeometryBuilder geometry, int start, int end)
    {
        _segment.Offset = start;
        _segment.Length = end - start;
        geometry.AddSegment(textView, _segment);
    }

    private sealed class SimpleSegment : ISegment
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public int EndOffset => Offset + Length;
    }
}
