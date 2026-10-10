// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Kodo.Models;

internal sealed class FindHighlightRenderer : IBackgroundRenderer
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(80, 255, 210, 0));
    private readonly List<(int Offset, int Length)> _matches = new();
    private readonly SimpleSegment _segment = new();

    public KnownLayer Layer => KnownLayer.Background;

    public void AddMatch(int offset, int length) => _matches.Add((offset, length));

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

        foreach (var (offset, length) in _matches)
        {
            if (offset + length < viewStart || offset > viewEnd)
                continue;
            _segment.Offset = offset;
            _segment.Length = length;
            geoBuilder.AddSegment(textView, _segment);
        }

        var geometry = geoBuilder.CreateGeometry();
        if (geometry is not null)
            drawingContext.DrawGeometry(HighlightBrush, null, geometry);
    }

    private sealed class SimpleSegment : ISegment
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public int EndOffset => Offset + Length;
    }
}
