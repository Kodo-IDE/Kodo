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
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Kodo.Models;

// Extracted from MainWindow.axaml.cs:14683
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

    // Disabled for unsaved (untitled) files and plain-text files (.txt / .log / .text)
    // so that smart visual features don't activate where no language context exists.
    public bool IsEnabled { get; set; } = true;

    public IBrush GuideBrush { get; set; } = new SolidColorBrush(Color.Parse("#808080"), 0.4);

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

        // Pre-compute indent depth (in tab-stop levels) for every document line.
        var totalLines = document.LineCount;
        var lineDepths = new int[totalLines + 1]; // 1-based index

        for (var i = 1; i <= totalLines; i++)
        {
            var docLine = document.GetLineByNumber(i);
            var text    = document.GetText(docLine);
            lineDepths[i] = string.IsNullOrWhiteSpace(text) ? -1 : GetIndentColumns(text) / TabSize;
        }

        // Fill blank lines from surrounding context so guides are continuous
        for (var i = 1; i <= totalLines; i++)
        {
            if (lineDepths[i] != -1) continue;
            var above = 0;
            for (var a = i - 1; a >= 1; a--)
                if (lineDepths[a] >= 0) { above = lineDepths[a]; break; }
            var below = 0;
            for (var b = i + 1; b <= totalLines; b++)
                if (lineDepths[b] >= 0) { below = lineDepths[b]; break; }
            lineDepths[i] = Math.Min(above, below);
        }

        // Measures true text-start X, subtracting ScrollOffset.
        var scrollX = textView.ScrollOffset.X;
        var scrollY = textView.ScrollOffset.Y;

        var refLine = textView.VisualLines[0].FirstDocumentLine;
        var originX = textView.GetVisualPosition(
            new AvaloniaEdit.TextViewPosition(refLine.LineNumber, 1),
            VisualYPosition.LineTop).X - scrollX;

        // Dashed pen matching VS Code-style indent guides
        var dashStyle = new DashStyle([2, 2], 0);
        var pen = new Pen(GuideBrush, 1, dashStyle);

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            var depth = lineDepths[lineNumber];
            if (depth <= 0) continue;

            // VisualTop is document-absolute; subtract scrollY for screen coords
            var top    = visualLine.VisualTop - scrollY;
            var bottom = top + visualLine.Height;

            for (var level = 1; level <= depth; level++)
            {
                // Each guide sits at the first character of its indent level: column TabSize, 2*TabSize, etc.
                var x = originX + (level * TabSize - 1) * spaceWidth;
                if (x < 0 || x > textView.Bounds.Width) continue;

                drawingContext.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            }
        }
    }

    private int GetIndentColumns(string lineText)
    {
        var columns = 0;
        foreach (var ch in lineText)
        {
            if      (ch == ' ')  columns++;
            else if (ch == '\t') columns += TabSize - (columns % TabSize);
            else break;
        }
        return columns;
    }
}

/// <summary>
/// Highlights all search matches in the editor when Find-in-file is active.
/// </summary>
// Whole-line/whole-section grey highlighting for Insight's dead code detection
// (unused variables, unused functions, unreachable code). Deliberately muted rather
// than alarm-red - dead code isn't an error, just something the editor thinks is inert.
// Brightens the syntax-highlighted text sitting on top of a dead-code grey overlay, so it
// stays readable instead of washing out against the highlight. Lightens whatever foreground
// color the syntax colorizer already picked, rather than overriding it with a flat color, so
// each token keeps its own hue - just brighter.
internal sealed class DeadCodeTextBrightener : DocumentColorizingTransformer
{
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    // Instance (not static/const) so ApplyThemeToEditor can swap it per theme. Unconditional
    // override rather than lightening the existing brush - plain identifiers often carry no
    // explicit brush at all (null, inherited from the editor default), so a "lighten if solid"
    // check silently skipped them, which is why only accent-colored tokens (numbers, keywords)
    // were changing while the rest of the line stayed dim.
    public IBrush TextBrush { get; set; } = new SolidColorBrush(Color.Parse("#F5F5F5"));

    private IReadOnlyList<InsightEngine.DeadCodeSpan> _spans = Array.Empty<InsightEngine.DeadCodeSpan>();

    public void SetSpans(IReadOnlyList<InsightEngine.DeadCodeSpan> spans) => _spans = spans;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.Count == 0) return;

        foreach (var span in _spans)
        {
            var start = Math.Max(span.StartOffset, line.Offset);
            // DocumentLine.EndOffset already excludes the line delimiter - subtracting
            // DelimiterLength again here was crushing "end" down near "start" for short
            // lines, which is why only a sliver near the start of the line was affected.
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
    // Instance (not static/const) so ApplyThemeToEditor can swap it for a darker overlay
    // in light themes, where a translucent mid-grey barely shows up against white.
    public IBrush HighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.16);
    private IReadOnlyList<InsightEngine.DeadCodeSpan> _spans = Array.Empty<InsightEngine.DeadCodeSpan>();

    // Current spans, shared with ErrorLineHighlightRenderer so error lines covered by dead
    // code can switch their whole-line highlight to grey/red stripes.
    public IReadOnlyList<InsightEngine.DeadCodeSpan> Spans => _spans;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpans(IReadOnlyList<InsightEngine.DeadCodeSpan> spans) => _spans = spans;

    // Returns the reason string for the dead-code span containing the given document
    // offset, or null if the offset isn't inside one - used to drive the hover tooltip.
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

        // One full-width rectangle per visible line covered by a dead-code span. Manual
        // rects reaching the viewport edge rather than BackgroundGeometryBuilder - its
        // ExtendToFullWidthAtLineEnd only stretches to the document's widest line, which
        // left short lines highlighted just around their text.
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

// Forces legible black text over error-only lines (red wash, no dead-code grey) in
// light themes, where the default syntax colors become hard to read against the
// translucent red background. Dead-code lines keep their own brighter text treatment.
internal sealed class ErrorTextDarkener : DocumentColorizingTransformer
{
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    // Instance (not static/const) so ApplyThemeToEditor can swap it per theme.
    public IBrush TextBrush { get; set; } = Brushes.Black;

    // Only darkens in light themes - in dark themes the red wash preserves contrast.
    public bool IsLightTheme { get; set; }

    private IReadOnlyList<InsightEngine.ErrorSpan> _errorSpans = Array.Empty<InsightEngine.ErrorSpan>();
    private IReadOnlyList<InsightEngine.DeadCodeSpan> _deadCodeSpans = Array.Empty<InsightEngine.DeadCodeSpan>();

    public void SetSpans(
        IReadOnlyList<InsightEngine.ErrorSpan> errorSpans,
        IReadOnlyList<InsightEngine.DeadCodeSpan> deadCodeSpans)
    {
        _errorSpans = errorSpans;
        _deadCodeSpans = deadCodeSpans;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!IsLightTheme) return;
        if (_errorSpans.Count == 0) return;

        // Covered by dead-code? Skip - DeadCodeTextBrightener already handles it.
        foreach (var deadSpan in _deadCodeSpans)
        {
            if (deadSpan.StartOffset < line.EndOffset && deadSpan.StartOffset + deadSpan.Length > line.Offset)
                return;
        }

        // Has any error span? Force black text (light theme only - see ApplyThemeToEditor).
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

// Whole-line red highlight for Insight's basic error detection (unmatched/unclosed
// brackets, unterminated strings, missing ';'/':' , misspelled keywords). Each line
// containing an error is washed with a translucent full-width red highlight; when such
// a line is ALSO covered by a dead-code finding (e.g. an unused variable), the wash
// becomes alternating grey/red vertical stripes spanning the whole line, so both
// findings stay visible on the same row.
internal sealed class ErrorLineHighlightRenderer : IBackgroundRenderer
{
    // Whole-line wash behind error-only lines. Kept translucent so the syntax colors
    // underneath stay readable.
    public IBrush LineHighlightBrush { get; set; } = new SolidColorBrush(Color.Parse("#E5484D"), 0.18);
    // Red half of the stripes on error+dead-code lines. Stronger than the plain wash so
    // it reads as clearly red next to the grey.
    public IBrush StripeRedBrush { get; set; } = new SolidColorBrush(Color.Parse("#E5484D"), 0.40);
    // Grey half of the stripes - themed to match DeadCodeHighlightRenderer.HighlightBrush
    // (see ApplyThemeToEditor) so mixed lines tie back to pure dead-code lines.
    public IBrush StripeGreyBrush { get; set; } = new SolidColorBrush(Color.Parse("#9AA0A6"), 0.22);

    private const double StripeWidth = 8.0;

    private IReadOnlyList<InsightEngine.ErrorSpan> _spans = Array.Empty<InsightEngine.ErrorSpan>();
    private IReadOnlyList<InsightEngine.DeadCodeSpan> _deadCodeSpans = Array.Empty<InsightEngine.DeadCodeSpan>();

    public IReadOnlyList<InsightEngine.ErrorSpan> Spans => _spans;

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetSpans(IReadOnlyList<InsightEngine.ErrorSpan> spans) => _spans = spans;

    public void SetDeadCodeSpans(IReadOnlyList<InsightEngine.DeadCodeSpan> spans) => _deadCodeSpans = spans;

    // All error messages whose spans touch the given line, newline-separated - drives the
    // hover tooltip. Line-based (not offset-based) because highlighting is line-wide, so
    // hovering anywhere on the line should surface its message(s).
    public string? GetMessageForLine(int lineStart, int lineEnd)
    {
        List<string>? messages = null;
        foreach (var span in _spans)
        {
            if (span.StartOffset < lineEnd && span.StartOffset + span.Length > lineStart)
                (messages ??= []).Add(span.Message);
        }
        return messages is null ? null : string.Join(Environment.NewLine, messages);
    }

    // True when any dead-code span overlaps the line's content - switches the whole-line
    // highlight from a solid red wash to grey/red stripes.
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
        if (textView?.Document is null || !textView.VisualLinesValid || _spans.Count == 0)
            return;

        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0)
            return;

        var scrollY = textView.ScrollOffset.Y;
        var width = textView.Bounds.Width;

        // One full-width rectangle per visible line touched by an error span: solid red
        // wash normally, grey/red stripes when a dead-code finding covers the same line.
        // Manual rects reaching the viewport edge - BackgroundGeometryBuilder's
        // ExtendToFullWidthAtLineEnd only stretches to the document's widest line.
        foreach (var visualLine in visualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            if (!SpanTouchesLine(docLine))
                continue;

            var y1 = visualLine.VisualTop - scrollY;
            var height = visualLine.Height;
            if (height <= 0) continue;

            if (LineOverlapsDeadCode(docLine))
                DrawStripes(drawingContext, y1, height, width);
            else
                drawingContext.DrawRectangle(LineHighlightBrush, null, new Rect(0, y1, width, height));
        }
    }

    private bool SpanTouchesLine(DocumentLine line)
    {
        foreach (var span in _spans)
        {
            if (span.StartOffset < line.EndOffset && span.StartOffset + span.Length > line.Offset)
                return true;
        }
        return false;
    }

    // Alternating grey/red vertical bars spanning the full editor width of one line -
    // the combined "dead code + error" treatment. Starts with grey so the pattern reads
    // as a dead-code line first, then flags the error on top.
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

// Replaces the default LinkElementGenerator: genuine URLs only, Ctrl+click to open.
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

// ---------------------------------------------------------------------------
// Consolidated from previously fragmented files:
// EditorTab.cs, ExplorerClipboardMode.cs, FileNode.cs, FileTreeItem.cs
// ---------------------------------------------------------------------------

public class EditorTab : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isDirty;
    private bool _isSelected;
    private IBrush _backgroundBrush = Brushes.Transparent;
    private IBrush _foregroundBrush = Brushes.White;

    public EditorTab(string path, string displayName, string content, bool isUntitled = false)
    {
        Path = path;
        DisplayName = displayName;
        _content = content;
        IsUntitled = isUntitled;
    }

    public string Path { get; set; }

    public string DisplayName { get; private set; }

    public bool IsUntitled { get; set; }

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

    // 1-based line number for scroll-position restore
    public int TopLineNumber { get; set; } = 1;

    // Pixel-exact scroll offset
    public double ScrollOffsetY { get; set; } = 0.0;

    // Caret position for this tab
    public int CaretOffset { get; set; } = 0;

    public void Rename(string path, string displayName)
    {
        Path = path;
        DisplayName = displayName;
        IsUntitled = false;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabTitle));
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

// Represents a single row in the file explorer tree
public class FileTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public int Depth { get; init; }

    // Pixel indentation based on nesting depth
    public double IndentWidth => Depth * 14.0;

    // Chevron shown next to directories; blank for files
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

    // Icon varies between open/closed folder vs file
    public string Icon => IsDirectory ? (_isExpanded ? "\U0001F4C2" : "\U0001F4C1") : GetFileIcon(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Returns a simple file-type icon based on extension.
    internal static string GetFileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
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
            _ => "..",
        };
    }
}
