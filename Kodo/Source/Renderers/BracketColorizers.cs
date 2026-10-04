// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using Kodo.Models;

namespace Kodo;
public sealed class RainbowBracketColorizer : DocumentColorizingTransformer
{
    private static readonly Dictionary<char, char> OpeningToClosing = new()
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}'
    };

    private static readonly Dictionary<char, char> ClosingToOpening = new()
    {
        [')'] = '(',
        [']'] = '[',
        ['}'] = '{'
    };

    private static readonly IBrush[] RainbowBrushes =
    [
        Brush.Parse("#FFD700"),
        Brush.Parse("#DA70D6"),
        Brush.Parse("#4FC1FF"),
        Brush.Parse("#C586C0"),
        Brush.Parse("#9CDCFE"),
        Brush.Parse("#D7BA7D")
    ];
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    private ParseSnapshot? _snapshot;
    private string _commentLine = "//";
    private string _commentBlockStart = "/*";
    private string _commentBlockEnd = "*/";
    private string[] _stringDelimiters = ["\"", "'"];
    private string[] _multiLineStringDelimiters = [];
    private bool _isBatch;

    public bool IsEnabled { get; set; } = true;

    private static bool IsMarkupLikeExtension(LoadedExtension ext) =>
        ext.CommentBlockStart == "<!--" ||
        ext.Extensions.Any(e =>
            string.Equals(e, ".html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, ".htm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, ".xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, ".svg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, ".xaml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, ".axaml", StringComparison.OrdinalIgnoreCase));

    public void UpdateSyntax(LoadedExtension? extension)
    {
        if (extension is null)
        {
            IsEnabled = false;
            _commentLine = "//";
            _commentBlockStart = "/*";
            _commentBlockEnd = "*/";
            _stringDelimiters = ["\"", "'"];
            _multiLineStringDelimiters = [];
            _isBatch = false;
        }
        else if (IsMarkupLikeExtension(extension))
        {
            IsEnabled = false;
            _commentLine = extension.CommentLine;
            _commentBlockStart = extension.CommentBlockStart;
            _commentBlockEnd = extension.CommentBlockEnd;
            _stringDelimiters = extension.StringDelimiters
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToArray();
            _multiLineStringDelimiters = extension.MultiLineStringDelimiters
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToArray();
            _isBatch = false;
        }
        else
        {
            IsEnabled = true;
            _commentLine = extension.CommentLine;
            _commentBlockStart = extension.CommentBlockStart;
            _commentBlockEnd = extension.CommentBlockEnd;
            _stringDelimiters = extension.StringDelimiters
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToArray();
            _multiLineStringDelimiters = extension.MultiLineStringDelimiters
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToArray();
            _isBatch = extension.Extensions.Any(ext => string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase)) ||
                       extension.Id.IndexOf("batch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        InvalidateCache();
    }

    private static readonly Regex BatchEchoPattern = new(
        @"^\s*@?echo[\.:\(]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex BatchLabelPattern = new(
        @"^\s*::",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex BatchRemPattern = new(
        @"^\s*rem\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private readonly Stack<char> _lineStack = new();

    public void InvalidateCache()
    {
        _snapshot = null;
        DocumentTextCache.Invalidate();
    }

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (!IsEnabled)
            return;

        var document = CurrentContext.Document;
        if (document is null || line.Length <= 0)
            return;
        if (document.TextLength > PerformanceBudget.ViewportCullThreshold)
        {
            var tv = CurrentContext.TextView;
            if (tv != null && tv.VisualLinesValid && tv.VisualLines.Count > 0)
            {
                var first = tv.VisualLines.First().FirstDocumentLine.LineNumber;
                var last = tv.VisualLines.Last().FirstDocumentLine.LineNumber;
                const int buffer = 80;
                if (line.LineNumber < first - buffer || line.LineNumber > last + buffer)
                    return;
            }
            if (document.TextLength > PerformanceBudget.SnapshotSkipThreshold)
                return;
        }

        var snapshot = _snapshot ??= BuildSnapshot(DocumentTextCache.Get(document));
        var lineState = snapshot.GetLineState(line.LineNumber);
        var text = document.GetText(line.Offset, line.Length);
        int batchEchoContentStart = -1;
        if (_isBatch)
        {
            var echoMatch = BatchEchoPattern.Match(text);
            if (echoMatch.Success)
                batchEchoContentStart = echoMatch.Length;
            else if (BatchLabelPattern.IsMatch(text))
                return;
            else if (BatchRemPattern.IsMatch(text))
                return;
        }
        _lineStack.Clear();
        foreach (var ch in lineState.BracketStack)
            _lineStack.Push(ch);
        var stack = _lineStack;
        var mode = lineState.Mode;
        var activeDelimiter = lineState.Delimiter;

        for (var index = 0; index < text.Length; index++)
        {
            if (batchEchoContentStart >= 0 && index >= batchEchoContentStart)
                break;

            var nextMode = mode;
            var nextDelimiter = activeDelimiter;

            if (mode == ScanMode.LineComment)
                break;

            if (TryConsumeLineBreak(text, ref index, ref nextMode, ref nextDelimiter))
            {
                mode = nextMode;
                activeDelimiter = nextDelimiter;
                continue;
            }

            if (mode == ScanMode.BlockComment)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) && MatchesAt(text, index, activeDelimiter))
                {
                    index += activeDelimiter.Length - 1;
                    mode = ScanMode.Normal;
                    activeDelimiter = null;
                }

                continue;
            }

            if (mode == ScanMode.String)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) &&
                    MatchesAt(text, index, activeDelimiter) &&
                    !IsEscaped(text, index))
                {
                    index += activeDelimiter.Length - 1;
                    mode = ScanMode.Normal;
                    activeDelimiter = null;
                }

                continue;
            }

            if (mode == ScanMode.MultiLineString)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) &&
                    MatchesAt(text, index, activeDelimiter) &&
                    !IsEscaped(text, index))
                {
                    index += activeDelimiter.Length - 1;
                    mode = ScanMode.Normal;
                    activeDelimiter = null;
                }

                continue;
            }

            if (!string.IsNullOrEmpty(_commentLine) && MatchesAt(text, index, _commentLine))
            {
                mode = ScanMode.LineComment;
                index += _commentLine.Length - 1;
                continue;
            }

            if (!string.IsNullOrEmpty(_commentBlockStart) && MatchesAt(text, index, _commentBlockStart))
            {
                mode = ScanMode.BlockComment;
                activeDelimiter = _commentBlockEnd;
                index += _commentBlockStart.Length - 1;
                continue;
            }

            var multiLineDelimiter = MatchDelimiter(text, index, _multiLineStringDelimiters);
            if (multiLineDelimiter is not null)
            {
                mode = ScanMode.MultiLineString;
                activeDelimiter = multiLineDelimiter;
                index += multiLineDelimiter.Length - 1;
                continue;
            }

            var stringDelimiter = MatchDelimiter(text, index, _stringDelimiters);
            if (stringDelimiter is not null)
            {
                mode = ScanMode.String;
                activeDelimiter = stringDelimiter;
                index += stringDelimiter.Length - 1;
                continue;
            }

            var ch = text[index];
            var offset = line.Offset + index;

            if (OpeningToClosing.ContainsKey(ch))
            {
                ApplyBracketColor(offset, GetBrushForDepth(stack.Count));
                stack.Push(ch);
                continue;
            }

            if (ClosingToOpening.TryGetValue(ch, out var opening) &&
                stack.Count > 0 &&
                stack.Peek() == opening)
            {
                ApplyBracketColor(offset, GetBrushForDepth(stack.Count - 1));
                stack.Pop();
            }
        }
    }

    private void ApplyBracketColor(int offset, IBrush brush)
    {
        ChangeLinePart(offset, offset + 1, element =>
        {
            var properties = element.TextRunProperties.Clone();
            properties.SetForegroundBrush(brush);
            SetTextRunPropertiesMethod?.Invoke(element, [properties]);
        });
    }

    private IBrush GetBrushForDepth(int depth) => RainbowBrushes[Math.Abs(depth) % RainbowBrushes.Length];

    private ParseSnapshot BuildSnapshot(string text)
    {
        if (text.IndexOfAny(['(', ')', '[', ']', '{', '}']) < 0)
        {
            var emptyLineCount = 1;
            for (var c = 0; c < text.Length; c++)
                if (text[c] == '\n') emptyLineCount++;
            var emptyStates = new List<LineState>(emptyLineCount);
            emptyStates.Add(new(string.Empty, ScanMode.Normal, null));
            for (var i = 1; i < emptyLineCount; i++)
                emptyStates.Add(new(string.Empty, ScanMode.Normal, null));
            return new ParseSnapshot(text, emptyStates);
        }

        var lineStates = new List<LineState> { new(string.Empty, ScanMode.Normal, null) };
        var stack = new char[256];
        var depth = 0;
        var lastEmittedStack = string.Empty;
        var stackDirty = true;
        var mode = ScanMode.Normal;
        string? activeDelimiter = null;

        for (var index = 0; index < text.Length; index++)
        {
            if (TryConsumeLineBreak(text, ref index, ref mode, ref activeDelimiter))
            {
                if (stackDirty)
                {
                    lastEmittedStack = new string(stack, 0, depth);
                    stackDirty = false;
                }

                lineStates.Add(new(lastEmittedStack, mode, activeDelimiter));
                continue;
            }

            if (mode == ScanMode.LineComment)
                continue;

            if (mode == ScanMode.BlockComment)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) && MatchesAt(text, index, activeDelimiter))
                    ExitDelimitedState(ref index, ref mode, ref activeDelimiter);

                continue;
            }

            if (mode == ScanMode.String)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) && MatchesAt(text, index, activeDelimiter) && !IsEscaped(text, index))
                    ExitDelimitedState(ref index, ref mode, ref activeDelimiter);

                continue;
            }

            if (mode == ScanMode.MultiLineString)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) && MatchesAt(text, index, activeDelimiter) && !IsEscaped(text, index))
                    ExitDelimitedState(ref index, ref mode, ref activeDelimiter);

                continue;
            }

            if (!string.IsNullOrEmpty(_commentLine) && MatchesAt(text, index, _commentLine))
            {
                mode = ScanMode.LineComment;
                index += _commentLine.Length - 1;
                continue;
            }

            if (!string.IsNullOrEmpty(_commentBlockStart) && MatchesAt(text, index, _commentBlockStart))
            {
                mode = ScanMode.BlockComment;
                activeDelimiter = _commentBlockEnd;
                index += _commentBlockStart.Length - 1;
                continue;
            }

            var multiLineDelimiter = MatchDelimiter(text, index, _multiLineStringDelimiters);
            if (multiLineDelimiter is not null)
            {
                mode = ScanMode.MultiLineString;
                activeDelimiter = multiLineDelimiter;
                index += multiLineDelimiter.Length - 1;
                continue;
            }

            var stringDelimiter = MatchDelimiter(text, index, _stringDelimiters);
            if (stringDelimiter is not null)
            {
                mode = ScanMode.String;
                activeDelimiter = stringDelimiter;
                index += stringDelimiter.Length - 1;
                continue;
            }

            var ch = text[index];
            if (OpeningToClosing.ContainsKey(ch))
            {
                if (depth == stack.Length) Array.Resize(ref stack, stack.Length * 2);
                stack[depth++] = ch;
                stackDirty = true;
            }
            else if (ClosingToOpening.TryGetValue(ch, out var opening) &&
                     depth > 0 &&
                     stack[depth - 1] == opening)
            {
                depth--;
                stackDirty = true;
            }
        }

        return new ParseSnapshot(text, lineStates);
    }

    private static void ExitDelimitedState(ref int index, ref ScanMode mode, ref string? activeDelimiter)
    {
        index += activeDelimiter!.Length - 1;
        mode = ScanMode.Normal;
        activeDelimiter = null;
    }

    private static bool TryConsumeLineBreak(string text, ref int index, ref ScanMode mode, ref string? activeDelimiter)
    {
        if (text[index] == '\r')
        {
            if (index + 1 < text.Length && text[index + 1] == '\n')
                index++;

            if (mode is ScanMode.LineComment or ScanMode.String)
            {
                mode = ScanMode.Normal;
                activeDelimiter = null;
            }

            return true;
        }

        if (text[index] == '\n')
        {
            if (mode is ScanMode.LineComment or ScanMode.String)
            {
                mode = ScanMode.Normal;
                activeDelimiter = null;
            }

            return true;
        }

        return false;
    }

    private static bool MatchesAt(string text, int index, string token) =>
        index + token.Length <= text.Length &&
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static string? MatchDelimiter(string text, int index, string[] delimiters)
    {
        for (var i = 0; i < delimiters.Length; i++)
        {
            var delimiter = delimiters[i];
            if (MatchesAt(text, index, delimiter)) return delimiter;
        }

        return null;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashCount++;

        return slashCount % 2 == 1;
    }

    private sealed record ParseSnapshot(string Text, List<LineState> LineStates)
    {
        public LineState GetLineState(int lineNumber) =>
            lineNumber > 0 && lineNumber <= LineStates.Count
                ? LineStates[lineNumber - 1]
                : new LineState(string.Empty, ScanMode.Normal, null);
    }

    private readonly record struct LineState(string BracketStack, ScanMode Mode, string? Delimiter);

    private enum ScanMode
    {
        Normal,
        LineComment,
        BlockComment,
        String,
        MultiLineString
    }
}

public sealed class EmojiTypefaceColorizer : DocumentColorizingTransformer
{
    private static readonly Typeface EmojiTypeface = new(new FontFamily("Noto Color Emoji,Segoe UI Emoji,Apple Color Emoji"));
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        var document = CurrentContext.Document;
        if (document is null || line.Length <= 0)
            return;

        var text = document.GetText(line.Offset, line.Length);
        foreach (var (start, end) in EnumerateEmojiRanges(text))
        {
            ChangeLinePart(line.Offset + start, line.Offset + end, element =>
            {
                var properties = element.TextRunProperties.Clone();
                properties.SetTypeface(EmojiTypeface);
                SetTextRunPropertiesMethod?.Invoke(element, [properties]);
            });
        }
    }

    private static IEnumerable<(int Start, int End)> EnumerateEmojiRanges(string text)
    {
        var rangeStart = -1;
        var index = 0;

        while (index < text.Length)
        {
            var codePoint = char.ConvertToUtf32(text, index);
            var length = char.IsSurrogatePair(text, index) ? 2 : 1;
            var isEmoji = IsEmojiCodePoint(codePoint) ||
                          codePoint == 0x200D ||
                          codePoint == 0xFE0F ||
                          codePoint == 0x20E3 ||
                          IsRegionalIndicator(codePoint) ||
                          IsEmojiModifier(codePoint);

            if (isEmoji)
            {
                if (rangeStart < 0)
                    rangeStart = index;
            }
            else if (rangeStart >= 0)
            {
                yield return (rangeStart, index);
                rangeStart = -1;
            }

            index += length;
        }

        if (rangeStart >= 0)
            yield return (rangeStart, text.Length);
    }

    private static bool IsEmojiCodePoint(int codePoint) =>
        codePoint is >= 0x1F000 and <= 0x1FAFF ||
        codePoint is >= 0x2600 and <= 0x27BF ||
        codePoint is >= 0x2300 and <= 0x23FF ||
        codePoint is >= 0x2B00 and <= 0x2BFF;

    private static bool IsRegionalIndicator(int codePoint) =>
        codePoint is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsEmojiModifier(int codePoint) =>
        codePoint is >= 0x1F3FB and <= 0x1F3FF;
}
