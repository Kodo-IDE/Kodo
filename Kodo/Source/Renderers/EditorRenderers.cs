// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;

namespace Kodo.Models;

public sealed class LspInlayHintRenderer : VisualLineElementGenerator
{
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private IReadOnlyList<(int Offset, string Label)> _hints = Array.Empty<(int, string)>();

    public void SetHints(IReadOnlyList<(int Offset, string Label)> hints)
    {
        var normalized = new List<(int Offset, string Label)>(hints.Count);
        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint.Label)) continue;
            var label = hint.Label.Trim();
            if (normalized.Count > 0 && normalized[^1].Offset == hint.Offset)
                normalized[^1] = (hint.Offset, $"{normalized[^1].Label}, {label}");
            else
                normalized.Add((hint.Offset, label));
        }
        _hints = normalized.ToArray();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var low = 0;
        var high = _hints.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_hints[middle].Offset < startOffset) low = middle + 1;
            else high = middle;
        }
        return low < _hints.Count ? _hints[low].Offset : -1;
    }

    public override VisualLineElement ConstructElement(int offset)
    {
        var low = 0;
        var high = _hints.Count - 1;
        var label = string.Empty;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var hint = _hints[middle];
            if (hint.Offset == offset)
            {
                label = hint.Label;
                break;
            }
            if (hint.Offset < offset) low = middle + 1;
            else high = middle - 1;
        }
        var content = new Border
        {
            Padding = new Thickness(3, 0),
            Child = new TextBlock
            {
                Text = label,
                Foreground = HintBrush,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            }
        };
        return new InlineObjectElement(0, content);
    }
}

public sealed class LspSemanticTokenRenderer : IBackgroundRenderer
{
    private IReadOnlyList<(int Offset, int Length, IBrush Brush)> _tokens = Array.Empty<(int, int, IBrush)>();
    private readonly Segment _segment = new();
    public KnownLayer Layer => KnownLayer.Background;
    public void SetTokens(IReadOnlyList<(int Offset, int Length, IBrush Brush)> tokens)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        _tokens = tokens;
    }
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_tokens.Count == 0 || !textView.VisualLinesValid || textView.Document is null) return;
        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0) return;
        var viewStart = visualLines[0].FirstDocumentLine.Offset;
        var viewEnd = visualLines[^1].LastDocumentLine.EndOffset;
        var textLength = textView.Document.TextLength;
        var low = 0;
        var high = _tokens.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if ((long)_tokens[middle].Offset + _tokens[middle].Length <= viewStart) low = middle + 1;
            else high = middle;
        }
        for (var index = low; index < _tokens.Count && _tokens[index].Offset <= viewEnd; index++)
        {
            var token = _tokens[index];
            if (token.Offset >= textLength) continue;
            var start = Math.Max(0, token.Offset);
            var end = Math.Min(token.Offset + token.Length, textLength);
            if (end <= start) continue;
            if (end <= viewStart || start > viewEnd) continue;
            var geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 1 };
            _segment.Offset = start;
            _segment.Length = end - start;
            geometry.AddSegment(textView, _segment);
            var shape = geometry.CreateGeometry();
            if (shape is not null) drawingContext.DrawGeometry(token.Brush, null, shape);
        }
    }
    private sealed class Segment : ISegment
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public int EndOffset => Offset + Length;
    }
}

public sealed class IndentGuideBackgroundRenderer : IBackgroundRenderer
{
    private const int MaxDepthCacheEntries = 2048;

    public KnownLayer Layer => KnownLayer.Background;

    public int TabSize { get; set; } = 4;

    public bool IsEnabled { get; set; } = true;

    public IBrush GuideBrush { get; set; } = new SolidColorBrush(Color.Parse("#808080"), 0.4);

    private static readonly DashStyle GuideDashStyle = new([2, 2], 0);

    private Pen? _cachedPen;
    private IBrush? _cachedBrush;

    private readonly Dictionary<int, int> _depthCache = new();
    private ITextSourceVersion? _depthCacheVersion;
    private AvaloniaEdit.Document.TextDocument? _depthCacheDocument;
    private int _depthCacheTabSize = -1;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!IsEnabled)
            return;

        if (!textView.VisualLinesValid || TabSize <= 0)
            return;

        var spaceWidth = textView.WideSpaceWidth;
        if (spaceWidth <= 0)
            return;

        var document = textView.Document;
        if (document is null || document.LineCount == 0)
            return;

        var visualLines = textView.VisualLines;
        var lineCount = visualLines.Count;
        if (lineCount == 0)
            return;

        var scrollX = textView.ScrollOffset.X;
        var scrollY = textView.ScrollOffset.Y;

        var refLine = visualLines[0].FirstDocumentLine;
        var originX = textView.GetVisualPosition(
            new AvaloniaEdit.TextViewPosition(refLine.LineNumber, 1),
            VisualYPosition.LineTop).X - scrollX;
        if (double.IsNaN(originX) || double.IsInfinity(originX))
            return;

        var viewportWidth = textView.Bounds.Width;
        SyncDepthCache(document);

        var geometry = new StreamGeometry();
        var drew = false;
        using (var context = geometry.Open())
        {
            for (var i = 0; i < lineCount; i++)
            {
                var visualLine = visualLines[i];
                var depth = GetVisibleLineDepth(document, visualLine.FirstDocumentLine.LineNumber);
                if (depth <= 0) continue;

                var top = visualLine.VisualTop - scrollY;
                var bottom = top + visualLine.Height;
                if (bottom <= top) continue;

                for (var level = 1; level <= depth; level++)
                {
                    var x = originX + (level * TabSize - 1) * spaceWidth;
                    if (double.IsNaN(x) || double.IsInfinity(x)) continue;
                    if (x < 0 || x > viewportWidth) continue;

                    context.BeginFigure(new Point(x, top), false);
                    context.LineTo(new Point(x, bottom), true);
                    context.EndFigure(false);
                    drew = true;
                }
            }
        }

        if (!drew) return;
        var pen = ResolvePen();
        if (pen is not null)
            drawingContext.DrawGeometry(null, pen, geometry);
    }

    private Pen? ResolvePen()
    {
        if (_cachedPen is null || !ReferenceEquals(_cachedBrush, GuideBrush))
        {
            _cachedBrush = GuideBrush;
            _cachedPen = new Pen(GuideBrush, 1, GuideDashStyle);
        }
        return _cachedPen;
    }

    private void SyncDepthCache(AvaloniaEdit.Document.TextDocument document)
    {
        if (!ReferenceEquals(_depthCacheDocument, document) || _depthCacheTabSize != TabSize)
        {
            _depthCache.Clear();
            _depthCacheDocument = document;
            _depthCacheVersion = null;
            _depthCacheTabSize = TabSize;
            return;
        }

        if (_depthCacheVersion is null)
        {
            _depthCacheVersion = document.Version;
            return;
        }

        if (_depthCacheVersion.CompareAge(document.Version) < 0)
        {
            _depthCache.Clear();
            _depthCacheVersion = document.Version;
        }
    }

    private int GetVisibleLineDepth(AvaloniaEdit.Document.TextDocument document, int lineNumber)
    {
        if (_depthCache.TryGetValue(lineNumber, out var cached)) return cached;
        var depth = ComputeVisibleLineDepth(document, lineNumber);
        if (_depthCache.Count >= MaxDepthCacheEntries) _depthCache.Clear();
        _depthCache[lineNumber] = depth;
        return depth;
    }

    private int ComputeVisibleLineDepth(AvaloniaEdit.Document.TextDocument document, int lineNumber)
    {
        var docLine = document.GetLineByNumber(lineNumber);
        var text = document.GetTextAsMemory(docLine.Offset, docLine.Length).Span;
        if (!text.IsWhiteSpace())
            return GetIndentColumns(text) / TabSize;

        const int maxLookAround = 32;
        var above = 0;
        for (var a = lineNumber - 1; a >= 1 && lineNumber - a <= maxLookAround; a--)
        {
            var aboveLine = document.GetLineByNumber(a);
            var aboveText = document.GetTextAsMemory(aboveLine.Offset, aboveLine.Length).Span;
            if (aboveText.IsWhiteSpace()) continue;
            above = GetIndentColumns(aboveText) / TabSize;
            break;
        }
        var below = 0;
        for (var b = lineNumber + 1; b <= document.LineCount && b - lineNumber <= maxLookAround; b++)
        {
            var belowLine = document.GetLineByNumber(b);
            var belowText = document.GetTextAsMemory(belowLine.Offset, belowLine.Length).Span;
            if (belowText.IsWhiteSpace()) continue;
            below = GetIndentColumns(belowText) / TabSize;
            break;
        }
        if (above == 0 && below == 0) return 0;
        if (above == 0) return below;
        if (below == 0) return above;
        return Math.Min(above, below);
    }

    private int GetIndentColumns(ReadOnlySpan<char> lineText)
    {
        var columns = 0;
        foreach (var ch in lineText)
        {
            if (ch == ' ') columns++;
            else if (ch == '\t') columns += TabSize - (columns % TabSize);
            else break;
        }
        return columns;
    }
}

internal sealed class DeadCodeTextBrightener : DocumentColorizingTransformer
{
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    public IBrush TextBrush { get; set; } = new SolidColorBrush(Color.Parse("#F5F5F5"));

    private IReadOnlyList<DeadCodeSpan> _spans = Array.Empty<DeadCodeSpan>();
    private SpanOverlapIndex _spanIndex = SpanOverlapIndex.Empty;
    private readonly List<int> _candidates = new();

    public void SetSpans(IReadOnlyList<DeadCodeSpan> spans)
    {
        _spans = spans;
        _spanIndex = SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1);
        _candidates.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.Count == 0) return;

        _spanIndex.Collect(line.Offset, line.EndOffset, _candidates);
        foreach (var index in _candidates)
        {
            var span = _spans[index];
            var start = Math.Max(span.StartOffset, line.Offset);
            var end = Math.Min(span.StartOffset + span.Length, line.EndOffset);
            if (start >= end) continue;

            ChangeLinePart(start, end, element =>
            {
                var properties = element.TextRunProperties.Clone();
                properties.SetForegroundBrush(TextBrush);
                SetTextRunPropertiesMethod?.Invoke(element, [properties]);
            });
        }
    }
}

internal sealed class DeadCodeHighlightRenderer : IBackgroundRenderer
{
    public IBrush HighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.16);
    private IReadOnlyList<DeadCodeSpan> _spans = Array.Empty<DeadCodeSpan>();
    private SpanOverlapIndex _spanIndex = SpanOverlapIndex.Empty;
    private readonly List<int> _candidates = new();

    public IReadOnlyList<DeadCodeSpan> Spans => _spans;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<DeadCodeSpan> spans)
    {
        _spans = spans;
        _spanIndex = SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1);
        _candidates.Clear();
    }

    public string? GetReasonAt(int offset)
    {
        _spanIndex.Collect(offset, offset, _candidates);
        foreach (var index in _candidates)
        {
            var span = _spans[index];
            if (offset >= span.StartOffset && offset <= span.StartOffset + span.Length)
                return span.Reason;
        }
        return null;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView?.Document is null || !textView.VisualLinesValid || _spans.Count == 0)
            return;

        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0)
            return;

        var scrollY = textView.ScrollOffset.Y;
        var width = textView.Bounds.Width;

        foreach (var visualLine in visualLines)
        {
            if (!SpanCoversLine(visualLine.FirstDocumentLine))
                continue;

            var y1 = visualLine.VisualTop - scrollY;
            var height = visualLine.Height;
            if (height <= 0) continue;

            drawingContext.DrawRectangle(HighlightBrush, null, new Rect(0, y1, width, height));
        }
    }

    private bool SpanCoversLine(DocumentLine line)
    {
        _spanIndex.Collect(line.Offset, line.EndOffset, _candidates);
        foreach (var index in _candidates)
        {
            var span = _spans[index];
            if (span.StartOffset < line.EndOffset && span.StartOffset + span.Length > line.Offset)
                return true;
        }
        return false;
    }
}

internal sealed class ErrorTextDarkener : DocumentColorizingTransformer
{
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    public IBrush TextBrush { get; set; } = Brushes.Black;

    public bool IsLightTheme { get; set; }

    private IReadOnlyList<ErrorSpan> _errorSpans = Array.Empty<ErrorSpan>();
    private IReadOnlyList<DeadCodeSpan> _deadCodeSpans = Array.Empty<DeadCodeSpan>();
    private SpanOverlapIndex _errorIndex = SpanOverlapIndex.Empty;
    private SpanOverlapIndex _deadCodeIndex = SpanOverlapIndex.Empty;
    private readonly List<int> _candidates = new();

    public void SetSpans(
        IReadOnlyList<ErrorSpan> errorSpans,
        IReadOnlyList<DeadCodeSpan> deadCodeSpans)
    {
        _errorSpans = errorSpans;
        _deadCodeSpans = deadCodeSpans;
        _errorIndex = SpanOverlapIndex.Build(errorSpans, span => span.StartOffset, span => (long)span.StartOffset + Math.Max(1, span.Length));
        _deadCodeIndex = SpanOverlapIndex.Build(deadCodeSpans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1);
        _candidates.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!IsLightTheme) return;
        if (_errorSpans.Count == 0) return;

        _deadCodeIndex.Collect(line.Offset, line.EndOffset, _candidates);
        foreach (var index in _candidates)
        {
            var deadSpan = _deadCodeSpans[index];
            if (deadSpan.StartOffset < line.EndOffset && deadSpan.StartOffset + deadSpan.Length > line.Offset)
                return;
        }

        _errorIndex.Collect(line.Offset, line.EndOffset, _candidates);
        foreach (var index in _candidates)
        {
            var errorSpan = _errorSpans[index];
            if (errorSpan.StartOffset < line.EndOffset && errorSpan.StartOffset + errorSpan.Length > line.Offset)
            {
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    var properties = element.TextRunProperties.Clone();
                    properties.SetForegroundBrush(TextBrush);
                    SetTextRunPropertiesMethod?.Invoke(element, [properties]);
                });
                return;
            }
        }
    }
}

internal sealed class ErrorLineHighlightRenderer : IBackgroundRenderer
{
    public IBrush LineHighlightBrush { get; set; } = new SolidColorBrush(KodoDesignTokens.DangerColor, 0.18);
    public IBrush StripeRedBrush { get; set; } = new SolidColorBrush(KodoDesignTokens.DangerColor, 0.40);
    public IBrush StripeGreyBrush { get; set; } = new SolidColorBrush(Color.Parse("#9AA0A6"), 0.22);
    public IBrush WarningHighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#CCA700"), 0.18);
    public IBrush InfoHighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#3794FF"), 0.14);
    public IBrush HintHighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#8A8A8A"), 0.12);

    private const double StripeWidth = 8.0;

    private IReadOnlyList<ErrorSpan> _spans = Array.Empty<ErrorSpan>();
    private IReadOnlyList<DeadCodeSpan> _deadCodeSpans = Array.Empty<DeadCodeSpan>();
    private SpanOverlapIndex _spanIndex = SpanOverlapIndex.Empty;
    private SpanOverlapIndex _deadCodeIndex = SpanOverlapIndex.Empty;
    private readonly List<int> _candidates = new();

    public IReadOnlyList<ErrorSpan> Spans => _spans;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<ErrorSpan> spans)
    {
        _spans = spans;
        _spanIndex = SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + Math.Max(1, span.Length));
        _candidates.Clear();
    }

    public void SetDeadCodeSpans(IReadOnlyList<DeadCodeSpan> spans)
    {
        _deadCodeSpans = spans;
        _deadCodeIndex = SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1);
    }

    public string? GetMessageForLine(int lineStart, int lineEnd)
    {
        List<string>? messages = null;
        _spanIndex.Collect(lineStart, lineEnd, _candidates);
        foreach (var index in _candidates)
        {
            var span = _spans[index];
            if (span.StartOffset <= lineEnd && span.StartOffset + Math.Max(1, span.Length) > lineStart)
            {
                var label = span.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" :
                            span.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "Warning" :
                            span.Severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? "Info" : "Hint";
                (messages ??= []).Add($"{label}: {span.Message}");
            }
        }
        if (messages is null) return null;
        return string.Join(Environment.NewLine, messages
            .GroupBy(message => message.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(group => group.First()));
    }

    public string? GetMessageAt(int offset)
    {
        List<string>? messages = null;
        _spanIndex.Collect(offset, offset, _candidates);
        foreach (var index in _candidates)
        {
            var span = _spans[index];
            if (offset >= span.StartOffset && offset < span.StartOffset + span.Length)
            {
                var label = span.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" :
                            span.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "Warning" :
                            span.Severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? "Info" : "Hint";
                (messages ??= []).Add($"{label}: {span.Message}");
            }
        }
        if (messages is null) return null;
        return string.Join(Environment.NewLine, messages
            .GroupBy(message => message.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(group => group.First()));
    }

    private bool LineOverlapsDeadCode(DocumentLine line)
    {
        _deadCodeIndex.Collect(line.Offset, line.EndOffset, _candidates);
        foreach (var index in _candidates)
        {
            var deadSpan = _deadCodeSpans[index];
            if (deadSpan.StartOffset < line.EndOffset && deadSpan.StartOffset + deadSpan.Length > line.Offset)
                return true;
        }
        return false;
    }

    public bool HasEolSpanOnLine(int lineStart, int lineEnd)
    {
        _spanIndex.Collect(lineStart, lineEnd + 1, _candidates);
        foreach (var index in _candidates)
        {
            var span = _spans[index];
            if (span.StartOffset >= lineEnd && span.StartOffset <= lineEnd + 1 && span.StartOffset + Math.Max(1, span.Length) > lineStart)
                return true;
            if (span.StartOffset <= lineEnd && span.StartOffset + Math.Max(1, span.Length) > lineStart && span.StartOffset >= lineEnd - 4)
                return true;
        }
        return false;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView?.Document is null || _spans.Count == 0)
            return;

        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;

        var visualLines = textView.VisualLines;
        var scrollY = textView.ScrollOffset.Y;
        var width = textView.Bounds.Width;

        foreach (var visualLine in visualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            if (!SpanTouchesLine(docLine))
                continue;

            var y1 = visualLine.VisualTop - scrollY;
            var height = visualLine.Height;
            if (height <= 0) continue;

            var hasDeadOverlap = LineOverlapsDeadCode(docLine);
            if (hasDeadOverlap)
                DrawStripes(drawingContext, y1, height, width);
            _spanIndex.Collect(docLine.Offset, docLine.EndOffset + 1, _candidates);
            foreach (var index in _candidates)
            {
                var span = _spans[index];
                bool isEol = span.StartOffset >= docLine.EndOffset && span.StartOffset <= docLine.EndOffset + 1 && docLine.Length > 0;
                if (!isEol && (span.StartOffset > docLine.EndOffset || span.StartOffset + span.Length <= docLine.Offset))
                    continue;
                int start, end;
                if (isEol)
                {
                    var eolLen = Math.Min(4, Math.Max(1, docLine.Length));
                    start = Math.Max(docLine.Offset, docLine.EndOffset - eolLen);
                    end = docLine.EndOffset;
                }
                else
                {
                    start = Math.Max(span.StartOffset, docLine.Offset);
                    end = Math.Min(span.StartOffset + span.Length, docLine.EndOffset);
                    if (end <= start) end = Math.Min(docLine.EndOffset, start + 1);
                }
                try
                {
                    var startColumn = Math.Clamp(start - docLine.Offset + 1, 1, docLine.Length + 1);
                    var endColumn = Math.Clamp(Math.Max(startColumn + 1, end - docLine.Offset + 1), startColumn + 1, docLine.Length + 1);
                    var left = textView.GetVisualPosition(new TextViewPosition(docLine.LineNumber, startColumn), VisualYPosition.LineBottom).X;
                    var right = textView.GetVisualPosition(new TextViewPosition(docLine.LineNumber, endColumn), VisualYPosition.LineBottom).X;
                    if (double.IsNaN(left) || double.IsNaN(right) || double.IsInfinity(left) || double.IsInfinity(right))
                    {
                        var fallbackWidth = Math.Max(6, Math.Min(width * 0.6, (end - start) * textView.WideSpaceWidth));
                        left = textView.GetVisualPosition(new TextViewPosition(docLine.LineNumber, 1), VisualYPosition.LineBottom).X;
                        right = left + fallbackWidth;
                    }
                    var brush = UnderlineBrushForSeverity(span.Severity);
                    var isHint = span.Severity.Equals("hint", StringComparison.OrdinalIgnoreCase);
                    var isInfo = span.Severity.Equals("info", StringComparison.OrdinalIgnoreCase);
                    var thickness = isHint ? 2.0 : isInfo ? 2.5 : 3.0;
                    var underlineY = y1 + height - 2.5;
                    var underlineWidth = Math.Max(6, right - left);
                    if (isEol) underlineWidth = Math.Max(underlineWidth, Math.Min(40, Math.Max(12, docLine.Length * textView.WideSpaceWidth * 0.25)));
                    drawingContext.DrawRectangle(brush, null, new Rect(left, underlineY, underlineWidth, thickness));
                    if (hasDeadOverlap)
                    {
                        var highlight = BrushForSeverity(span.Severity);
                        drawingContext.DrawRectangle(highlight, null, new Rect(left, y1, underlineWidth, height) { });
                    }
                }
                catch { }
            }
        }
    }

    private bool SpanTouchesLine(DocumentLine line)
    {
        return _spanIndex.Overlaps(line.Offset, line.EndOffset);
    }

    private string GetHighestSeverityForLine(DocumentLine line)
    {
        var best = "hint";
        foreach (var span in _spans)
        {
            if (span.StartOffset >= line.EndOffset || span.StartOffset + span.Length <= line.Offset)
                continue;
            if (SeverityRank(span.Severity) > SeverityRank(best))
                best = span.Severity;
        }
        return best;
    }

    private IBrush BrushForSeverity(string severity) => severity switch
    {
        "warning" => WarningHighlightBrush,
        "info" => InfoHighlightBrush,
        "hint" => HintHighlightBrush,
        _ => LineHighlightBrush,
    };

    private static readonly IBrush WarningUnderlineBrush = new SolidColorBrush(Color.Parse("#F2C94C"));
    private static readonly IBrush InfoUnderlineBrush = new SolidColorBrush(Color.Parse("#5BA7FF"));
    private static readonly IBrush HintUnderlineBrush = new SolidColorBrush(Color.Parse("#D4D8E0"));
    private static readonly IBrush ErrorUnderlineBrush = new SolidColorBrush(Color.Parse("#FF5C67"));

    private static IBrush UnderlineBrushForSeverity(string severity) => severity switch
    {
        "warning" => WarningUnderlineBrush,
        "info" => InfoUnderlineBrush,
        "hint" => HintUnderlineBrush,
        _ => ErrorUnderlineBrush,
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "error" => 4,
        "warning" => 3,
        "info" => 2,
        _ => 1,
    };

    private void DrawStripes(DrawingContext drawingContext, double y, double height, double width)
    {
        var x = 0.0;
        var index = 0;
        while (x < width)
        {
            var stripeWidth = Math.Min(StripeWidth, width - x);
            var brush = index % 2 == 0 ? StripeGreyBrush : StripeRedBrush;
            drawingContext.DrawRectangle(brush, null, new Rect(x, y, stripeWidth, height));
            x += stripeWidth;
            index++;
        }
    }
}

public sealed class StrictLinkElementGenerator : LinkElementGenerator
{
    private static readonly char[] TrailingPunctuation = [')', ']', '}', '.', ',', ':', ';', '!', '?', '\'', '"'];
    private const string HttpPrefix = "http";

    private int _cachedLineNumber = -1;
    private string _cachedLineText = string.Empty;
    private List<(int Start, int Length)> _cachedSpans = [];

    public StrictLinkElementGenerator()
    {
        RequireControlModifierForClick = true;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.VisualLine;
        var document = CurrentContext.Document;
        var lineNumber = line.FirstDocumentLine.LineNumber;
        var lineText = document.GetText(line.FirstDocumentLine.Offset, line.FirstDocumentLine.Length);

        if (lineNumber != _cachedLineNumber || !string.Equals(lineText, _cachedLineText, StringComparison.Ordinal))
        {
            _cachedLineNumber = lineNumber;
            _cachedLineText = lineText;
            _cachedSpans = ParseUrlSpans(lineText);
        }

        var relativeOffset = offset - line.FirstDocumentLine.Offset;
        foreach (var span in _cachedSpans)
        {
            if (relativeOffset != span.Start)
                continue;

            var url = lineText.Substring(span.Start, span.Length).TrimEnd(TrailingPunctuation);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var linkText = new VisualLineLinkText(line, url.Length);
            linkText.NavigateUri = uri;
            linkText.RequireControlModifierForClick = RequireControlModifierForClick;
            return linkText;
        }

        return null;
    }

    internal static bool TryGetLinkSpan(string lineText, int columnOffset, out int start, out int length)
    {
        foreach (var span in ParseUrlSpans(lineText))
        {
            if (columnOffset < span.Start || columnOffset >= span.Start + span.Length)
                continue;

            start = span.Start;
            length = span.Length;
            return true;
        }

        start = 0;
        length = 0;
        return false;
    }

    private static List<(int Start, int Length)> ParseUrlSpans(string lineText)
    {
        var spans = new List<(int Start, int Length)>();
        if (string.IsNullOrWhiteSpace(lineText))
            return spans;

        var index = 0;
        while (index < lineText.Length)
        {
            var httpIndex = lineText.IndexOf(HttpPrefix, index, StringComparison.OrdinalIgnoreCase);
            if (httpIndex < 0)
                break;

            if (httpIndex > 0 && IsUrlChar(lineText[httpIndex - 1]))
            {
                index = httpIndex + 4;
                continue;
            }

            var end = httpIndex;
            while (end < lineText.Length && IsUrlChar(lineText[end]))
                end++;

            var url = lineText[httpIndex..end].TrimEnd(TrailingPunctuation);
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                spans.Add((httpIndex, url.Length));

            index = Math.Max(end, httpIndex + 4);
        }

        return spans;
    }

    private static bool IsUrlChar(char ch) =>
        !char.IsWhiteSpace(ch) &&
        ch is not '<' and not '>' and not '"' and not '\'' and not '[' and not ']' and not '(' and not ')' and not '{' and not '}' and not '|' and not '\\' and not '^' and not '`';
}
