// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;

namespace Kodo.Models;

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

        // Use document hash + linecount + tabsize to invalidate cheap cache without per-char scan of whole doc
        var docVersion = document.TextLength ^ document.LineCount ^ TabSize;
        if (docVersion != _cachedVersion || document.LineCount != _cachedLineCount)
        {
            // Keep cache size bounded: only keep visible range + lookaround margin
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

    public IReadOnlyList<DeadCodeSpan> Spans => _spans;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<DeadCodeSpan> spans) => _spans = spans;

    public string? GetReasonAt(int offset)
    {
        foreach (var span in _spans)
        {
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
        foreach (var span in _spans)
        {
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

    public void SetSpans(
        IReadOnlyList<ErrorSpan> errorSpans,
        IReadOnlyList<DeadCodeSpan> deadCodeSpans)
    {
        _errorSpans = errorSpans;
        _deadCodeSpans = deadCodeSpans;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!IsLightTheme) return;
        if (_errorSpans.Count == 0) return;

        foreach (var deadSpan in _deadCodeSpans)
        {
            if (deadSpan.StartOffset < line.EndOffset && deadSpan.StartOffset + deadSpan.Length > line.Offset)
                return;
        }

        foreach (var errorSpan in _errorSpans)
        {
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

    public IReadOnlyList<ErrorSpan> Spans => _spans;

    // Keep diagnostics on the background layer so multiple extension
    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<ErrorSpan> spans) => _spans = spans;

    public void SetDeadCodeSpans(IReadOnlyList<DeadCodeSpan> spans) => _deadCodeSpans = spans;

    public string? GetMessageForLine(int lineStart, int lineEnd)
    {
        List<string>? messages = null;
        foreach (var span in _spans)
        {
            // Inclusive EOL: missing semicolon at EndOffset must still count as touching line - every error gets underline somewhere on line
            if (span.StartOffset <= lineEnd && span.StartOffset + Math.Max(1, span.Length) > lineStart)
            {
                var label = span.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" :
                            span.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "Warning" :
                            span.Severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? "Info" : "Hint";
                (messages ??= []).Add($"{label}: {span.Message}");
            }
        }
        if (messages is null) return null;
        // Keep hover text compact and predictable when several extensions
        return string.Join(Environment.NewLine, messages
            .GroupBy(message => message.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(group => group.First()));
    }

    public string? GetMessageAt(int offset)
    {
        List<string>? messages = null;
        foreach (var span in _spans)
        {
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
        foreach (var deadSpan in _deadCodeSpans)
        {
            if (deadSpan.StartOffset < line.EndOffset && deadSpan.StartOffset + deadSpan.Length > line.Offset)
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

            // Always draw error underlines first, then dead-code stripes behind - ensures underlines are never hidden
            // Collect underlines to draw after stripes
            var hasDeadOverlap = LineOverlapsDeadCode(docLine);
            if (hasDeadOverlap)
                DrawStripes(drawingContext, y1, height, width);
            foreach (var span in _spans)
            {
                // Inclusive EOL: every error must have underline somewhere on affected line
                bool isEol = span.StartOffset >= docLine.EndOffset && span.StartOffset <= docLine.EndOffset + 1 && docLine.Length > 0;
                if (!isEol && (span.StartOffset > docLine.EndOffset || span.StartOffset + span.Length <= docLine.Offset))
                    continue;
                int start, end;
                if (isEol)
                {
                    // Missing semicolon / insertion at EOL: underline last 3-4 chars of line
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
                        // Fallback: draw across visible line width proportionally
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
                    // Guarantee minimum visible underline for EOL errors even if right-left is tiny
                    if (isEol) underlineWidth = Math.Max(underlineWidth, Math.Min(40, Math.Max(12, docLine.Length * textView.WideSpaceWidth * 0.25)));
                    // Draw with higher opacity for visibility on dark/light themes
                    drawingContext.DrawRectangle(brush, null, new Rect(left, underlineY, underlineWidth, thickness));
                    // Second thin highlight for extra contrast when overlapping dead-code stripes
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
        foreach (var span in _spans)
        {
            // Inclusive EOL ensures missing-semicolon at EndOffset still counts as touching line - every error gets line underline
            if (span.StartOffset <= line.EndOffset && span.StartOffset + Math.Max(1, span.Length) > line.Offset)
                return true;
        }
        return false;
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

    /// <summary>Severity ranking for tab tint: 3=error, 2=warning, 1=info/unused, 0=none</summary>
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

    /// <summary>One entry per ancestor level, used to draw a vertical indent guide line for each.</summary>
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