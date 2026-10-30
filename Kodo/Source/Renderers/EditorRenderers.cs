// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;
using Kodo.Editor;

namespace Kodo.Models;

public sealed class LspInlayHintRenderer : VisualLineElementGenerator
{
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private IReadOnlyList<(int Offset, string Label)> _hints = Array.Empty<(int, string)>();
    private TextDocument? _document;

    public bool HasHints => _hints.Count > 0;

    public void SetDocument(TextDocument? document)
    {
        if (ReferenceEquals(_document, document)) return;
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _document = document;
        if (_document is not null) _document.Changed += OnDocumentChanged;
        _hints = Array.Empty<(int, string)>();
    }

    public void ClearHints() => _hints = Array.Empty<(int, string)>();

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (ReferenceEquals(sender, _document))
            ClearHints();
    }

    public (int Offset, int Length)? SetHints(IReadOnlyList<(int Offset, string Label)> hints)
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

        var same = normalized.Count == _hints.Count;
        if (same)
        {
            for (var index = 0; index < normalized.Count; index++)
            {
                if (normalized[index] != _hints[index])
                {
                    same = false;
                    break;
                }
            }
        }
        if (same) return null;

        var firstChangedOffset = int.MaxValue;
        var lastChangedOffset = 0;
        foreach (var hint in _hints)
        {
            firstChangedOffset = Math.Min(firstChangedOffset, hint.Offset);
            lastChangedOffset = Math.Max(lastChangedOffset, hint.Offset);
        }
        foreach (var hint in normalized)
        {
            firstChangedOffset = Math.Min(firstChangedOffset, hint.Offset);
            lastChangedOffset = Math.Max(lastChangedOffset, hint.Offset);
        }

        _hints = normalized.ToArray();
        return firstChangedOffset == int.MaxValue
            ? null
            : (firstChangedOffset, Math.Max(1, lastChangedOffset - firstChangedOffset + 1));
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
        var content = new TextBlock
        {
            Text = label,
            Foreground = HintBrush,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(3, 0)
        };
        return new InlineObjectElement(0, content);
    }
}

public sealed class LspSemanticTokenRenderer : IBackgroundRenderer
{
    private IReadOnlyList<(int Offset, int Length, IBrush Brush)> _tokens = Array.Empty<(int, int, IBrush)>();
    private readonly Segment _segment = new();
    private readonly Dictionary<IBrush, BackgroundGeometryBuilder> _geometryByBrush = new(ReferenceEqualityComparer.Instance);
    public KnownLayer Layer => KnownLayer.Background;
    public bool HasTokens => _tokens.Count > 0;
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
        _geometryByBrush.Clear();
        for (var index = low; index < _tokens.Count && _tokens[index].Offset <= viewEnd; index++)
        {
            var token = _tokens[index];
            if (token.Offset >= textLength) continue;
            var start = Math.Max(0, token.Offset);
            var end = Math.Min(token.Offset + token.Length, textLength);
            if (end <= start) continue;
            if (end <= viewStart || start > viewEnd) continue;
            if (!_geometryByBrush.TryGetValue(token.Brush, out var geometry))
            {
                geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 1 };
                _geometryByBrush.Add(token.Brush, geometry);
            }
            _segment.Offset = start;
            _segment.Length = end - start;
            geometry.AddSegment(textView, _segment);
        }

        foreach (var (brush, geometry) in _geometryByBrush)
        {
            var shape = geometry.CreateGeometry();
            if (shape is not null) drawingContext.DrawGeometry(brush, null, shape);
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
    private const int MaxGuideIndentColumns = 512;

    public KnownLayer Layer => KnownLayer.Background;

    public int TabSize { get; set; } = 4;

    public bool IsEnabled { get; set; } = true;

    public IBrush GuideBrush { get; set; } = new SolidColorBrush(Color.Parse("#808080"), 0.4);

    private static readonly DashStyle GuideDashStyle = new([2, 2], 0);

    private Pen? _cachedPen;
    private IBrush? _cachedBrush;

    private const int MaxIndentationDependencyLines = 32;
    private const int MaxIncrementalInvalidationLines = 256;

    private readonly Dictionary<AvaloniaEdit.Document.DocumentLine, int> _depthCache = new();
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
                var depth = GetVisibleLineDepth(document, visualLine.FirstDocumentLine);
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
            if (_depthCacheDocument is not null)
                _depthCacheDocument.Changed -= OnDepthCacheDocumentChanged;
            _depthCache.Clear();
            _depthCacheDocument = document;
            _depthCacheTabSize = TabSize;
            document.Changed += OnDepthCacheDocumentChanged;
        }
    }

    private void OnDepthCacheDocumentChanged(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e)
    {
        if (sender is not AvaloniaEdit.Document.TextDocument document || !ReferenceEquals(document, _depthCacheDocument))
            return;

        var insertedText = e.InsertedText.Text;
        if (e.RemovalLength > 4096 || insertedText.Length > 4096)
        {
            _depthCache.Clear();
            return;
        }

        var insertedLineBreaks = 0;
        for (var i = 0; i < insertedText.Length; i++)
        {
            if (insertedText[i] == '\r')
            {
                insertedLineBreaks++;
                if (i + 1 < insertedText.Length && insertedText[i + 1] == '\n') i++;
            }
            else if (insertedText[i] == '\n')
            {
                insertedLineBreaks++;
            }
            if (insertedLineBreaks > MaxIncrementalInvalidationLines)
                break;
        }

        if (insertedLineBreaks > MaxIncrementalInvalidationLines)
        {
            _depthCache.Clear();
            return;
        }

        var startOffset = Math.Clamp(e.Offset, 0, document.TextLength);
        var endOffset = Math.Clamp(e.Offset + insertedText.Length, startOffset, document.TextLength);
        var firstLine = document.GetLineByOffset(startOffset).LineNumber;
        var lastLine = document.GetLineByOffset(endOffset).LineNumber;
        if (lastLine - firstLine > MaxIncrementalInvalidationLines)
        {
            _depthCache.Clear();
            return;
        }

        firstLine = Math.Max(1, firstLine - MaxIndentationDependencyLines);
        lastLine = Math.Min(document.LineCount, lastLine + MaxIndentationDependencyLines);
        for (var lineNumber = firstLine; lineNumber <= lastLine; lineNumber++)
            _depthCache.Remove(document.GetLineByNumber(lineNumber));
    }

    private int GetVisibleLineDepth(AvaloniaEdit.Document.TextDocument document, AvaloniaEdit.Document.DocumentLine line)
    {
        if (_depthCache.TryGetValue(line, out var cached)) return cached;
        var depth = ComputeVisibleLineDepth(document, line.LineNumber);
        if (_depthCache.Count >= MaxDepthCacheEntries) _depthCache.Clear();
        _depthCache[line] = depth;
        return depth;
    }

    private int ComputeVisibleLineDepth(AvaloniaEdit.Document.TextDocument document, int lineNumber)
    {
        var docLine = document.GetLineByNumber(lineNumber);
        var text = document.GetTextAsMemory(docLine.Offset, docLine.Length).Span;
        var ownColumns = GetIndentColumns(text, out var hasContent);
        var ownDepth = ownColumns / TabSize;
        if (hasContent || ownColumns >= MaxGuideIndentColumns)
            return ownDepth;

        const int maxLookAround = 32;
        var above = 0;
        for (var a = lineNumber - 1; a >= 1 && lineNumber - a <= maxLookAround; a--)
        {
            var aboveLine = document.GetLineByNumber(a);
            var aboveText = document.GetTextAsMemory(aboveLine.Offset, aboveLine.Length).Span;
            var aboveColumns = GetIndentColumns(aboveText, out var aboveHasContent);
            if (!aboveHasContent) continue;
            above = aboveColumns / TabSize;
            break;
        }
        var below = 0;
        for (var b = lineNumber + 1; b <= document.LineCount && b - lineNumber <= maxLookAround; b++)
        {
            var belowLine = document.GetLineByNumber(b);
            var belowText = document.GetTextAsMemory(belowLine.Offset, belowLine.Length).Span;
            var belowColumns = GetIndentColumns(belowText, out var belowHasContent);
            if (!belowHasContent) continue;
            below = belowColumns / TabSize;
            break;
        }
        if (above == 0 && below == 0) return 0;
        if (above == 0) return below;
        if (below == 0) return above;
        return Math.Min(above, below);
    }

    private int GetIndentColumns(ReadOnlySpan<char> lineText, out bool hasContent)
    {
        var columns = 0;
        hasContent = false;
        foreach (var ch in lineText)
        {
            if (ch == ' ')
            {
                if (columns >= MaxGuideIndentColumns) break;
                columns++;
            }
            else if (ch == '\t')
            {
                columns = Math.Min(MaxGuideIndentColumns, columns + TabSize - (columns % TabSize));
                if (columns >= MaxGuideIndentColumns) break;
            }
            else
            {
                hasContent = true;
                break;
            }
        }
        return columns;
    }
}

internal sealed class DeadCodeTextBrightener : DocumentColorizingTransformer
{

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
                VisualLineElementAccess.SetTextRunProperties(element, properties);
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
    internal SpanOverlapIndex SpanIndex => _spanIndex;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<DeadCodeSpan> spans)
        => SetSpans(spans, spans.Count == 0 ? SpanOverlapIndex.Empty : SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1));

    public void SetSpans(IReadOnlyList<DeadCodeSpan> spans, SpanOverlapIndex spanIndex)
    {
        _spans = spans;
        _spanIndex = spanIndex;
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
        => SetSpans(
            errorSpans,
            errorSpans.Count == 0 ? SpanOverlapIndex.Empty : SpanOverlapIndex.Build(errorSpans, span => span.StartOffset, span => (long)span.StartOffset + Math.Max(1, span.Length)),
            deadCodeSpans,
            deadCodeSpans.Count == 0 ? SpanOverlapIndex.Empty : SpanOverlapIndex.Build(deadCodeSpans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1));

    public void SetSpans(
        IReadOnlyList<ErrorSpan> errorSpans,
        SpanOverlapIndex errorIndex,
        IReadOnlyList<DeadCodeSpan> deadCodeSpans,
        SpanOverlapIndex deadCodeIndex)
    {
        _errorSpans = errorSpans;
        _deadCodeSpans = deadCodeSpans;
        _errorIndex = errorIndex;
        _deadCodeIndex = deadCodeIndex;
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
                    VisualLineElementAccess.SetTextRunProperties(element, properties);
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
    internal SpanOverlapIndex SpanIndex => _spanIndex;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<ErrorSpan> spans)
        => SetSpans(spans, spans.Count == 0 ? SpanOverlapIndex.Empty : SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + Math.Max(1, span.Length)));

    public void SetSpans(IReadOnlyList<ErrorSpan> spans, SpanOverlapIndex spanIndex)
    {
        _spans = spans;
        _spanIndex = spanIndex;
        _candidates.Clear();
    }

    public void SetDeadCodeSpans(IReadOnlyList<DeadCodeSpan> spans)
        => SetDeadCodeSpans(spans, spans.Count == 0 ? SpanOverlapIndex.Empty : SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1));

    public void SetDeadCodeSpans(IReadOnlyList<DeadCodeSpan> spans, SpanOverlapIndex spanIndex)
    {
        _deadCodeSpans = spans;
        _deadCodeIndex = spanIndex;
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
    private AvaloniaEdit.Document.TextDocument? _cachedDocument;
    private AvaloniaEdit.Document.ITextSourceVersion? _cachedVersion;
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
        EnsureCachedLine(document, line.FirstDocumentLine);

        var relativeOffset = offset - line.FirstDocumentLine.Offset;
        foreach (var span in _cachedSpans)
        {
            if (relativeOffset != span.Start)
                continue;

            var url = _cachedLineText.Substring(span.Start, span.Length).TrimEnd(TrailingPunctuation);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var linkText = new VisualLineLinkText(line, url.Length);
            linkText.NavigateUri = uri;
            linkText.RequireControlModifierForClick = RequireControlModifierForClick;
            return linkText;
        }

        return null;
    }

    private void EnsureCachedLine(AvaloniaEdit.Document.TextDocument document, AvaloniaEdit.Document.DocumentLine line)
    {
        var version = document.Version;
        if (ReferenceEquals(_cachedDocument, document) && ReferenceEquals(_cachedVersion, version) &&
            _cachedLineNumber == line.LineNumber)
            return;

        _cachedDocument = document;
        _cachedVersion = version;
        _cachedLineNumber = line.LineNumber;
        if (line.Length > PerformanceBudget.MaximumCustomColorizationLineLength)
        {
            _cachedLineText = string.Empty;
            _cachedSpans = [];
            return;
        }
        _cachedLineText = EditorLineTextCache.GetText(document, line);
        _cachedSpans = ParseUrlSpans(_cachedLineText);
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
