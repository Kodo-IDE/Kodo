// Licensed under GPL v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;

namespace Kodo.Models;

internal sealed class SpanOverlapIndex
{
    public static readonly SpanOverlapIndex Empty = new([], [], [], []);

    private readonly int[] _starts;
    private readonly long[] _ends;
    private readonly int[] _order;
    private readonly long[] _subtreeMaxEnd;

    private SpanOverlapIndex(int[] starts, long[] ends, int[] order, long[] subtreeMaxEnd)
    {
        _starts = starts;
        _ends = ends;
        _order = order;
        _subtreeMaxEnd = subtreeMaxEnd;
    }

    public static SpanOverlapIndex Build<T>(IReadOnlyList<T> spans, Func<T, int> startSelector, Func<T, long> endSelector)
    {
        var count = spans.Count;
        var order = Enumerable.Range(0, count).OrderBy(i => startSelector(spans[i])).ToArray();
        var starts = new int[count];
        var ends = new long[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = startSelector(spans[order[i]]);
            ends[i] = endSelector(spans[order[i]]);
        }
        var subtreeMaxEnd = new long[count * 4];
        var index = new SpanOverlapIndex(starts, ends, order, subtreeMaxEnd);
        if (count > 0) index.Build(1, 0, count);
        return index;
    }

    public bool Overlaps(long start, int endInclusive)
    {
        var limit = UpperBound(endInclusive);
        return limit > 0 && Overlaps(1, 0, _starts.Length, limit, start);
    }

    public void Collect(long start, int endInclusive, List<int> result)
    {
        result.Clear();
        var limit = UpperBound(endInclusive);
        if (limit > 0) Collect(1, 0, _starts.Length, limit, start, result);
        if (result.Count > 1) result.Sort();
    }

    private long Build(int node, int left, int right)
    {
        if (right - left == 1) return _subtreeMaxEnd[node] = _ends[left];
        var middle = left + (right - left) / 2;
        return _subtreeMaxEnd[node] = Math.Max(Build(node * 2, left, middle), Build(node * 2 + 1, middle, right));
    }

    private int UpperBound(int endInclusive)
    {
        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_starts[middle] <= endInclusive) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private bool Overlaps(int node, int left, int right, int limit, long start)
    {
        if (left >= limit || _subtreeMaxEnd[node] <= start) return false;
        if (right - left == 1) return true;
        var middle = left + (right - left) / 2;
        return Overlaps(node * 2, left, middle, limit, start) || Overlaps(node * 2 + 1, middle, right, limit, start);
    }

    private void Collect(int node, int left, int right, int limit, long start, List<int> result)
    {
        if (left >= limit || _subtreeMaxEnd[node] <= start) return;
        if (right - left == 1)
        {
            result.Add(_order[left]);
            return;
        }
        var middle = left + (right - left) / 2;
        Collect(node * 2, left, middle, limit, start, result);
        Collect(node * 2 + 1, middle, right, limit, start, result);
    }
}

public sealed class LspInlayHintRenderer : IBackgroundRenderer
{
    private const int FormattedTextCacheLimit = 256;
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private readonly Dictionary<string, FormattedText> _formattedTextCache = new();
    private IReadOnlyList<(int Offset, string Label)> _hints = Array.Empty<(int, string)>();
    public KnownLayer Layer => KnownLayer.Text;
    public void SetHints(IReadOnlyList<(int Offset, string Label)> hints)
    {
        _hints = hints;
        _formattedTextCache.Clear();
    }
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_hints.Count == 0 || !textView.VisualLinesValid || textView.Document is null) return;
        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0) return;
        var viewStart = visualLines[0].FirstDocumentLine.Offset;
        var viewEnd = visualLines[^1].LastDocumentLine.EndOffset;
        var textLength = textView.Document.TextLength;
        foreach (var hint in _hints)
        {
            var offset = Math.Clamp(hint.Offset, 0, textLength);
            if (offset < viewStart || offset > viewEnd) continue;
            var line = textView.Document.GetLineByOffset(offset);
            var column = Math.Clamp(offset - line.Offset + 1, 1, line.Length + 1);
            var pos = textView.GetVisualPosition(new TextViewPosition(line.LineNumber, column), VisualYPosition.LineBottom);
            if (!_formattedTextCache.TryGetValue(hint.Label, out var formatted))
            {
                if (_formattedTextCache.Count >= FormattedTextCacheLimit) _formattedTextCache.Clear();
                formatted = new FormattedText($"  {hint.Label}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, HintBrush);
                _formattedTextCache[hint.Label] = formatted;
            }
            drawingContext.DrawText(formatted, new Point(pos.X + 3, pos.Y));
        }
    }
}

public sealed class LspSemanticTokenRenderer : IBackgroundRenderer
{
    private IReadOnlyList<(int Offset, int Length, IBrush Brush)> _tokens = Array.Empty<(int, int, IBrush)>();
    public KnownLayer Layer => KnownLayer.Background;
    public void SetTokens(IReadOnlyList<(int Offset, int Length, IBrush Brush)> tokens) => _tokens = tokens;
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_tokens.Count == 0 || !textView.VisualLinesValid || textView.Document is null) return;
        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0) return;
        var viewStart = visualLines[0].FirstDocumentLine.Offset;
        var viewEnd = visualLines[^1].LastDocumentLine.EndOffset;
        var textLength = textView.Document.TextLength;
        foreach (var token in _tokens)
        {
            if (token.Offset >= textLength) continue;
            var start = Math.Max(0, token.Offset);
            var end = Math.Min(token.Offset + token.Length, textLength);
            if (end <= start) continue;
            if (end <= viewStart || start > viewEnd) continue;
            var geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 1 };
            geometry.AddSegment(textView, new Segment(start, end - start));
            var shape = geometry.CreateGeometry();
            if (shape is not null) drawingContext.DrawGeometry(token.Brush, null, shape);
        }
    }
    private sealed class Segment : ISegment
    {
        public Segment(int offset, int length) { Offset = offset; Length = length; }
        public int Offset { get; }
        public int Length { get; }
        public int EndOffset => Offset + Length;
    }
}

public enum LineEnding
{
    LF,
    CRLF
}

internal enum UnsavedTabAction
{
    Save,
    Discard,
    Cancel
}

public sealed class IndentGuideBackgroundRenderer : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public int TabSize { get; set; } = 4;

    public bool IsEnabled { get; set; } = true;

    public IBrush GuideBrush { get; set; } = new SolidColorBrush(Color.Parse("#808080"), 0.4);

    private static readonly DashStyle GuideDashStyle = new([2, 2], 0);
    private Pen? _cachedPen;
    private IBrush? _cachedBrush;
    private readonly Dictionary<int, int> _depthCache = new();
    private int _cachedVersion = -1;
    private int _cachedLineCount = -1;
    private int _cachedTabSize = -1;

    public void InvalidateCache()
    {
        _cachedVersion = -1;
        _depthCache.Clear();
    }

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

        if (!textView.VisualLines.Any())
            return;

        var scrollX = textView.ScrollOffset.X;
        var scrollY = textView.ScrollOffset.Y;

        var refLine = textView.VisualLines[0].FirstDocumentLine;
        var originX = textView.GetVisualPosition(
            new AvaloniaEdit.TextViewPosition(refLine.LineNumber, 1),
            VisualYPosition.LineTop).X - scrollX;

        if (_cachedPen is null || !ReferenceEquals(_cachedBrush, GuideBrush) || _cachedTabSize != TabSize)
        {
            _cachedBrush = GuideBrush;
            _cachedTabSize = TabSize;
            _cachedPen = new Pen(GuideBrush, 1, GuideDashStyle);
        }
        var pen = _cachedPen;

        var docVersion = document.TextLength ^ document.LineCount ^ TabSize;
        if (docVersion != _cachedVersion || document.LineCount != _cachedLineCount)
        {
            if (_depthCache.Count > 400)
                _depthCache.Clear();
            _cachedVersion = docVersion;
            _cachedLineCount = document.LineCount;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            int depth;
            if (!_depthCache.TryGetValue(lineNumber, out depth))
            {
                depth = GetVisibleLineDepth(document, lineNumber);
                _depthCache[lineNumber] = depth;
            }
            if (depth <= 0) continue;

            var top = visualLine.VisualTop - scrollY;
            var bottom = top + visualLine.Height;

            for (var level = 1; level <= depth; level++)
            {
                var x = originX + (level * TabSize - 1) * spaceWidth;
                if (x < 0 || x > textView.Bounds.Width) continue;

                drawingContext.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            }
        }
    }

    private int GetVisibleLineDepth(AvaloniaEdit.Document.TextDocument document, int lineNumber)
    {
        var docLine = document.GetLineByNumber(lineNumber);
        var text = document.GetText(docLine);
        if (!string.IsNullOrWhiteSpace(text))
            return GetIndentColumns(text) / TabSize;

        const int maxLookAround = 32;
        var above = 0;
        for (var a = lineNumber - 1; a >= 1 && lineNumber - a <= maxLookAround; a--)
        {
            var aboveLine = document.GetLineByNumber(a);
            var aboveText = document.GetText(aboveLine);
            if (!string.IsNullOrWhiteSpace(aboveText))
            {
                above = GetIndentColumns(aboveText) / TabSize;
                break;
            }
        }
        var below = 0;
        for (var b = lineNumber + 1; b <= document.LineCount && b - lineNumber <= maxLookAround; b++)
        {
            var belowLine = document.GetLineByNumber(b);
            var belowText = document.GetText(belowLine);
            if (!string.IsNullOrWhiteSpace(belowText))
            {
                below = GetIndentColumns(belowText) / TabSize;
                break;
            }
        }
        if (above == 0 && below == 0) return 0;
        if (above == 0) return below;
        if (below == 0) return above;
        return Math.Min(above, below);
    }

    private int GetIndentColumns(string lineText)
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

    public void SetSpans(IReadOnlyList<DeadCodeSpan> spans) => _spans = spans;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.Count == 0) return;

        foreach (var span in _spans)
        {
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
    public IBrush LineHighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#E5484D"), 0.18);
    public IBrush StripeRedBrush { get; set; } = new SolidColorBrush(Color.Parse("#E5484D"), 0.40);
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

public class EditorTab : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isDirty;
    private bool _isSelected;
    private IBrush _backgroundBrush = Brushes.Transparent;
    private IBrush _foregroundBrush = Brushes.White;

    public EditorTab(string path, string displayName, string content, bool isUntitled = false, LineEnding lineEnding = LineEnding.CRLF)
    {
        Path = path;
        DisplayName = displayName;
        _content = content;
        IsUntitled = isUntitled;
        LineEnding = lineEnding;
    }

    public string Path { get; set; }

    public string DisplayName { get; private set; }

    public string Icon => FileTreeItem.GetFileIcon(DisplayName);

    public bool IsUntitled { get; set; }

    public LineEnding LineEnding { get; set; }

    public System.Text.Encoding Encoding { get; set; } = System.Text.Encoding.UTF8;

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value)
            {
                return;
            }

            _content = value;
            OnPropertyChanged();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabTitle));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public IBrush BackgroundBrush
    {
        get => _backgroundBrush;
        set
        {
            if (Equals(_backgroundBrush, value))
            {
                return;
            }

            _backgroundBrush = value;
            OnPropertyChanged();
        }
    }

    public IBrush ForegroundBrush
    {
        get => _foregroundBrush;
        set
        {
            if (Equals(_foregroundBrush, value))
            {
                return;
            }

            _foregroundBrush = value;
            OnPropertyChanged();
        }
    }

    public string TabTitle => IsDirty ? $"{DisplayName} •" : DisplayName;

    public int TopLineNumber { get; set; } = 1;

    public double ScrollOffsetY { get; set; } = 0.0;

    public int CaretOffset { get; set; } = 0;

    private int _errorCount;
    private int _warningCount;
    private int _infoCount;
    private int _unusedCount;
    private string _diagnosticsText = string.Empty;
    private string _diagnosticsTooltip = string.Empty;

    public int ErrorCount => _errorCount;
    public int WarningCount => _warningCount;
    public int InfoCount => _infoCount;
    public int UnusedCount => _unusedCount;

    public bool HasDiagnostics => _errorCount > 0 || _warningCount > 0 || _infoCount > 0 || _unusedCount > 0;
    public bool HasErrorDiagnostics => _errorCount > 0;

    public string DiagnosticsText => _diagnosticsText;
    public string DiagnosticsTooltip => _diagnosticsTooltip;

    public int DiagnosticsSeverity
    {
        get
        {
            if (_errorCount > 0) return 3;
            if (_warningCount > 0) return 2;
            if (_infoCount > 0 || _unusedCount > 0) return 1;
            return 0;
        }
    }

    public void UpdateDiagnostics(int errors, int warnings, int infos, int unused)
    {
        if (_errorCount == errors && _warningCount == warnings && _infoCount == infos && _unusedCount == unused)
            return;
        _errorCount = errors;
        _warningCount = warnings;
        _infoCount = infos;
        _unusedCount = unused;

        if (errors == 0 && warnings == 0 && infos == 0 && unused == 0)
        {
            _diagnosticsText = string.Empty;
            _diagnosticsTooltip = "No problems";
        }
        else
        {
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            if (infos > 0) parts.Add($"{infos} info");
            if (unused > 0) parts.Add($"{unused} unused");
            _diagnosticsText = string.Join(" • ", parts);
            _diagnosticsTooltip = string.Join(Environment.NewLine, parts.Select(p => $"• {p}"));
        }

        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(UnusedCount));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasErrorDiagnostics));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(DiagnosticsTooltip));
        OnPropertyChanged(nameof(DiagnosticsSeverity));
    }

    public void ClearDiagnostics() => UpdateDiagnostics(0, 0, 0, 0);

    public void Rename(string path, string displayName)
    {
        Path = path;
        DisplayName = displayName;
        IsUntitled = false;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(Icon));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum ExplorerClipboardMode
{
    Copy,
    Cut
}

public class FileNode
{
    public FileNode(string name, string path, bool isDirectory)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsDirectory { get; }

    public string Icon => IsDirectory ? "▸" : "•";

    public ObservableCollection<FileNode> Children { get; } = [];
}

public class FileTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isRenaming;
    private string _renameText = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public int Depth { get; init; }

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming == value) return; _isRenaming = value; OnPropertyChanged(); }
    }

    public string RenameText
    {
        get => _renameText;
        set { if (_renameText == value) return; _renameText = value; OnPropertyChanged(); }
    }

    public void ApplyRenamedName(string name)
    {
        Name = name;
        RenameText = name;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Icon));
    }

    public double IndentWidth => Depth * 14.0;

    public IEnumerable<int> GuideLevels => Enumerable.Range(0, Depth);

    public string ChevronText => IsDirectory ? (_isExpanded ? "↓" : "→") : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChevronText));
            OnPropertyChanged(nameof(Icon));
        }
    }

    public string Icon => IsDirectory ? (_isExpanded ? "\U0001F4C2" : "\U0001F4C1") : GetFileIcon(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    internal static string GetFileIcon(string fileName)
    {
        if (_iconCache.TryGetValue(fileName, out var cached)) return cached;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var icon = ext switch
        {
            ".cs" or ".csproj" or ".axaml.cs" or ".csx" => "C#",
            ".xml" => "XML",
            ".axaml" or ".xaml" => "XAML",
            ".html" or ".htm" => "HTML",
            ".json" or ".yaml" or ".yml" or ".toml" or ".jsonc" or ".jsonl" => "JSON",
            ".txt" or ".rst" or ".log" => "TXT",
            ".md" or ".markdown" => "MD",
            ".png" => "PNG",
            ".jpg" or ".jpeg" => "JPG",
            ".gif" => "GIF",
            ".svg" => "SVG",
            ".ico" => "ICO",
            ".webp" => "WBP",
            ".bmp" => "BMP",
            ".py" => "PY",
            ".js" or ".jsx" => "JS",
            ".ts" or ".tsx" => "TS",
            ".vue" or ".svelte" => "UI",
            ".css" or ".scss" or ".less" => "CSS",
            ".sh" => "SH",
            ".bat" => "BAT",
            ".ps1" => "PS1",
            ".zip" or ".tar" or ".gz" or ".rar" => "ZIP",
            ".cpp" or ".cc" or ".cxx" => "C++",
            ".c" => "C",
            ".h" or ".hpp" or ".hxx" => "C++",
            ".rs" => "RS",
            ".go" => "GO",
            ".rb" => "RB",
            ".java" => "JAVA",
            ".kt" or ".kts" => "KT",
            ".swift" => "SW",
            ".fs" or ".fsi" or ".fsx" => "F#",
            ".sql" => "DB",
            ".lua" => "LUA",
            ".r" => "R",
            ".lock" => "Lk",
            ".csv" or ".tsv" => "CSV",
            ".nova" => "NOVA",
            ".kox" => "KOX",
            ".exe" => "EXE",
            ".dll" => "DLL",
            ".gitignore" => "IGNR",
            ".shine" => "SHINE",
            ".asm" or ".s" or ".S" => "ASM",
            ".iss" => "ISS",
            _ => "..",
        };
        _iconCache.TryAdd(fileName, icon);
        return icon;
    }
}
