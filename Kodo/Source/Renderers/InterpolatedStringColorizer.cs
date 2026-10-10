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

public sealed class InterpolatedStringColorizer : DocumentColorizingTransformer
{
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);
    private const string VariableIdentifierBodyPattern =
        "[\\p{L}_][\\p{L}\\p{Nd}_]*";
    private static readonly IBrush[] RainbowBrushes =
    [
        Brush.Parse("#FFD700"),
        Brush.Parse("#DA70D6"),
        Brush.Parse("#4FC1FF"),
        Brush.Parse("#C586C0"),
        Brush.Parse("#9CDCFE"),
        Brush.Parse("#D7BA7D")
    ];

    private readonly List<SyntaxBrushRule> _rules = [];
    private InterpolationSnapshot? _snapshot;
    private InterpolationSupport _support;
    private IBrush _keywordBrush = Brushes.White;
    private IBrush _punctuationBrush = Brushes.White;

    public bool IsEnabled { get; set; }

    public void InvalidateCache()
    {
        _snapshot = null;
        DocumentTextCache.Invalidate();
    }

    public void UpdateSyntax(CompiledSyntaxProfile? syntaxProfile)
    {
        _rules.Clear();
        _snapshot = null;
        _support = default;

        if (syntaxProfile is null)
        {
            IsEnabled = false;
            _keywordBrush = Brushes.White;
            _punctuationBrush = Brushes.White;
            return;
        }

        IsEnabled = true;
        _keywordBrush = BrushFor(syntaxProfile.Extension, "keyword", "#569CD6");
        _punctuationBrush = BrushFor(syntaxProfile.Extension, "punctuation", "#D4D4D4");
        BuildRules(syntaxProfile);
        _support = BuildSupport(syntaxProfile.Extension);
    }

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (!IsEnabled || !_support.HasAny)
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

        var snapshot = _snapshot;
        if (snapshot is null)
        {
            var snapshotWatch = System.Diagnostics.Stopwatch.StartNew();
            snapshot = _snapshot = BuildSnapshot(DocumentTextCache.Get(document));
            snapshotWatch.Stop();
            KodoDiagnostics.ReportSlowStage("interpolation snapshot", snapshotWatch.ElapsedMilliseconds, 500, $"len={document.TextLength}");
        }

        var lineState = snapshot.GetLineState(line.LineNumber);
        var text = document.GetText(line.Offset, line.Length);
        ScanLine(text, line.Offset, lineState.ActiveInterpolation);
    }

    private void BuildRules(CompiledSyntaxProfile syntaxProfile)
    {
        foreach (var rule in syntaxProfile.TokenRules)
            _rules.Add(new SyntaxBrushRule(rule.Regex, BrushFor(syntaxProfile.Extension, rule.ColorTokenName, rule.FallbackHex), rule.ColorTokenName));
    }

    private static InterpolationSupport BuildSupport(LoadedExtension extension)
    {
        var stringDelimiters = extension.StringDelimiters
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet(StringComparer.Ordinal);
        var multiLineDelimiters = extension.MultiLineStringDelimiters
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet(StringComparer.Ordinal);

        var supportsJavaScriptTemplate = multiLineDelimiters.Contains("`") &&
            !KodoExtensionIds.IsMarkdown(extension.Id);
        var supportsPythonStyleInterpolation =
            string.Equals(extension.CommentLine, "#", StringComparison.Ordinal) &&
            !extension.DisableSingleQuoteStrings &&
            (stringDelimiters.Contains("\"") || stringDelimiters.Contains("'")) &&
            (multiLineDelimiters.Contains("\"\"\"") || multiLineDelimiters.Contains("'''"));
        var supportsDollarPrefixedInterpolation =
            extension.DisableSingleQuoteStrings &&
            stringDelimiters.Contains("\"");

        return new InterpolationSupport(supportsJavaScriptTemplate, supportsPythonStyleInterpolation, supportsDollarPrefixedInterpolation);
    }

    private InterpolationSnapshot BuildSnapshot(string text)
    {
        var lineStates = new List<InterpolationLineState> { new(null) };
        ActiveInterpolation? active = null;

        for (var index = 0; index < text.Length; index++)
        {
            if (TryConsumeLineBreak(text, ref index))
            {
                lineStates.Add(new(active));
                continue;
            }

            if (active is not null)
            {
                var activeValue = active.Value;
                if (IsInterpolationTerminator(text, index, activeValue))
                {
                    index += activeValue.Quote.Length - 1;
                    active = null;
                }

                continue;
            }

            if (TryMatchInterpolationStart(text, index, out var interpolation, out var contentStart))
            {
                if (interpolation.CanSpanMultipleLines)
                {
                    active = interpolation;
                    index = contentStart - 1;
                }
            }
        }

        return new InterpolationSnapshot(text, lineStates);
    }

    private void ScanLine(string text, int lineOffset, ActiveInterpolation? initialState)
    {
        var index = 0;
        var active = initialState;

        while (index < text.Length)
        {
            if (active is not null)
            {
                var activeValue = active.Value;
                var closingIndex = FindClosingIndex(text, index, activeValue);
                var stringEnd = closingIndex >= 0 ? closingIndex : text.Length;
                ColorizeInterpolationSegments(text, lineOffset, activeValue, index, stringEnd);

                if (closingIndex >= 0)
                {
                    index = closingIndex + activeValue.Quote.Length;
                    active = null;
                    continue;
                }

                break;
            }

            if (TryMatchInterpolationStart(text, index, out var interpolation, out var contentStart))
            {
                ApplyInterpolationPrefix(lineOffset, index, contentStart, interpolation.Quote.Length);
                var closingIndex = FindClosingIndex(text, contentStart, interpolation);
                var stringEnd = closingIndex >= 0 ? closingIndex : text.Length;
                ColorizeInterpolationSegments(text, lineOffset, interpolation, contentStart, stringEnd);

                if (closingIndex >= 0)
                {
                    index = closingIndex + interpolation.Quote.Length;
                    continue;
                }

                break;
            }

            index++;
        }
    }

    private void ColorizeInterpolationSegments(string text, int lineOffset, ActiveInterpolation interpolation, int contentStart, int stringEnd)
    {
        if (interpolation.Kind == InterpolationKind.JavaScriptTemplate)
        {
            for (var index = contentStart; index < stringEnd - 1; index++)
            {
                if (text[index] == '\\')
                {
                    index++;
                    continue;
                }

                if (text[index] == '$' && text[index + 1] == '{')
                {
                    var closeIndex = FindClosingBrace(text, index + 2, allowDoubledEscapes: false, limit: stringEnd);
                    if (closeIndex > index + 1)
                    {
                        ApplyBrush(lineOffset + index + 1, lineOffset + index + 2, GetRainbowBrush(0));
                        ApplyBrush(lineOffset + closeIndex, lineOffset + closeIndex + 1, GetRainbowBrush(0));
                        ApplyExpressionRules(text, lineOffset, index + 2, closeIndex);
                        index = closeIndex;
                    }
                }
            }

            return;
        }

        for (var index = contentStart; index < stringEnd; index++)
        {
            if (text[index] == '{')
            {
                if (index + 1 < stringEnd && text[index + 1] == '{')
                {
                    index++;
                    continue;
                }

                var closeIndex = FindClosingBrace(text, index + 1, allowDoubledEscapes: true, limit: stringEnd);
                if (closeIndex > index)
                {
                    ApplyBrush(lineOffset + index, lineOffset + index + 1, GetRainbowBrush(0));
                    ApplyBrush(lineOffset + closeIndex, lineOffset + closeIndex + 1, GetRainbowBrush(0));
                    ApplyExpressionRules(text, lineOffset, index + 1, closeIndex);
                    index = closeIndex;
                }
            }
            else if (!interpolation.IsVerbatim && text[index] == '\\')
            {
                index++;
            }
        }
    }

    private void ApplyExpressionRules(string text, int lineOffset, int expressionStart, int expressionEnd)
    {
        if (expressionEnd <= expressionStart)
            return;

        var expressionText = text.Substring(expressionStart, expressionEnd - expressionStart);

        var protectedRanges = new bool[expressionText.Length];

        foreach (var rule in _rules)
        {
            foreach (Match match in rule.Regex.Matches(expressionText))
            {
                if (!match.Success || match.Length <= 0)
                    continue;

                if (!TryReserveExpressionRange(protectedRanges, match.Index, match.Index + match.Length))
                    continue;

                ApplyBrush(
                    lineOffset + expressionStart + match.Index,
                    lineOffset + expressionStart + match.Index + match.Length,
                    rule.Brush);
            }
        }

        ApplyRainbowBrackets(text, lineOffset, expressionStart, expressionEnd);
    }

    private static bool TryReserveExpressionRange(bool[] protectedRanges, int start, int end)
    {
        if (start < 0 || end > protectedRanges.Length || start >= end)
            return false;

        for (var index = start; index < end; index++)
        {
            if (protectedRanges[index])
                return false;
        }

        for (var index = start; index < end; index++)
            protectedRanges[index] = true;

        return true;
    }

    private void ApplyInterpolationPrefix(int lineOffset, int startIndex, int contentStart, int quoteLength)
    {
        var prefixLength = contentStart - startIndex - quoteLength;
        if (prefixLength <= 0)
            return;

        ApplyBrush(lineOffset + startIndex, lineOffset + startIndex + prefixLength, _keywordBrush);
    }

    private void ApplyRainbowBrackets(string text, int lineOffset, int expressionStart, int expressionEnd)
    {
        var stack = new Stack<char>();
        for (var index = expressionStart; index < expressionEnd; index++)
        {
            var ch = text[index];
            if (ch is '(' or '[' or '{')
            {
                ApplyBrush(lineOffset + index, lineOffset + index + 1, GetRainbowBrush(stack.Count));
                stack.Push(ch);
                continue;
            }

            if (TryGetMatchingOpeningBracket(ch, out var opening) &&
                stack.Count > 0 &&
                stack.Peek() == opening)
            {
                ApplyBrush(lineOffset + index, lineOffset + index + 1, GetRainbowBrush(stack.Count - 1));
                stack.Pop();
            }
        }
    }

    private static bool TryGetMatchingOpeningBracket(char ch, out char opening)
    {
        switch (ch)
        {
            case ')':
                opening = '(';
                return true;
            case ']':
                opening = '[';
                return true;
            case '}':
                opening = '{';
                return true;
            default:
                opening = default;
                return false;
        }
    }

    private bool TryMatchInterpolationStart(string text, int index, out ActiveInterpolation interpolation, out int contentStart)
    {
        if (_support.SupportsJavaScriptTemplate && text[index] == '`')
        {
            interpolation = new ActiveInterpolation(InterpolationKind.JavaScriptTemplate, "`", false, true);
            contentStart = index + 1;
            return true;
        }

        if (_support.SupportsPythonStyle && TryMatchPythonFStringStart(text, index, out var quote, out contentStart))
        {
            interpolation = new ActiveInterpolation(InterpolationKind.PythonFString, quote, false, quote.Length > 1);
            return true;
        }

        if (_support.SupportsDollarPrefixed && TryMatchDollarPrefixedStart(text, index, out var isVerbatim, out contentStart))
        {
            interpolation = new ActiveInterpolation(InterpolationKind.DollarPrefixed, "\"", isVerbatim, isVerbatim);
            return true;
        }

        interpolation = default;
        contentStart = 0;
        return false;
    }

    private static int FindClosingIndex(string text, int index, ActiveInterpolation interpolation)
    {
        for (var i = index; i <= text.Length - interpolation.Quote.Length; i++)
        {
            if (!interpolation.IsVerbatim && text[i] == '\\')
            {
                i++;
                continue;
            }

            if (interpolation.IsVerbatim && interpolation.Quote == "\"" &&
                i + 1 < text.Length &&
                text[i] == '"' &&
                text[i + 1] == '"')
            {
                i++;
                continue;
            }

            if (IsInterpolationTerminator(text, i, interpolation))
                return i;
        }

        return -1;
    }

    private static bool IsInterpolationTerminator(string text, int index, ActiveInterpolation interpolation)
    {
        if (index < 0 || index + interpolation.Quote.Length > text.Length)
            return false;

        if (interpolation.Kind == InterpolationKind.JavaScriptTemplate)
            return text[index] == '`';

        if (!interpolation.IsVerbatim && IsEscaped(text, index))
            return false;

        return string.CompareOrdinal(text, index, interpolation.Quote, 0, interpolation.Quote.Length) == 0;
    }

    private static int FindClosingBrace(string text, int index, bool allowDoubledEscapes, int limit)
    {
        var depth = 0;
        for (var i = index; i < limit; i++)
        {
            if (allowDoubledEscapes && i + 1 < limit && text[i] == '{' && text[i + 1] == '{')
            {
                i++;
                continue;
            }

            if (allowDoubledEscapes && i + 1 < limit && text[i] == '}' && text[i + 1] == '}')
            {
                i++;
                continue;
            }

            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                if (depth == 0)
                    return i;

                depth--;
            }
        }

        return -1;
    }

    private static bool TryMatchPythonFStringStart(string text, int index, out string quote, out int contentStart)
    {
        quote = string.Empty;
        contentStart = 0;

        if (index >= text.Length)
            return false;

        var prefixLength = 0;
        if ((text[index] == 'f' || text[index] == 'F') &&
            index + 1 < text.Length &&
            (text[index + 1] == 'r' || text[index + 1] == 'R'))
        {
            prefixLength = 2;
        }
        else if ((text[index] == 'r' || text[index] == 'R') &&
                 index + 1 < text.Length &&
                 (text[index + 1] == 'f' || text[index + 1] == 'F'))
        {
            prefixLength = 2;
        }
        else if (text[index] == 'f' || text[index] == 'F')
        {
            prefixLength = 1;
        }

        if (prefixLength == 0)
            return false;

        var quoteIndex = index + prefixLength;
        if (quoteIndex + 2 < text.Length && text[quoteIndex] == '"' && text[quoteIndex + 1] == '"' && text[quoteIndex + 2] == '"')
        {
            quote = "\"\"\"";
            contentStart = quoteIndex + 3;
            return true;
        }

        if (quoteIndex + 2 < text.Length && text[quoteIndex] == '\'' && text[quoteIndex + 1] == '\'' && text[quoteIndex + 2] == '\'')
        {
            quote = "'''";
            contentStart = quoteIndex + 3;
            return true;
        }

        if (quoteIndex < text.Length && (text[quoteIndex] == '"' || text[quoteIndex] == '\''))
        {
            quote = text[quoteIndex].ToString();
            contentStart = quoteIndex + 1;
            return true;
        }

        return false;
    }

    private static bool TryMatchDollarPrefixedStart(string text, int index, out bool isVerbatim, out int contentStart)
    {
        isVerbatim = false;
        contentStart = 0;

        if (index + 1 >= text.Length)
            return false;

        if (text[index] == '$' && text[index + 1] == '"')
        {
            contentStart = index + 2;
            return true;
        }

        if (index + 2 < text.Length && text[index] == '$' && text[index + 1] == '@' && text[index + 2] == '"')
        {
            isVerbatim = true;
            contentStart = index + 3;
            return true;
        }

        if (index + 2 < text.Length && text[index] == '@' && text[index + 1] == '$' && text[index + 2] == '"')
        {
            isVerbatim = true;
            contentStart = index + 3;
            return true;
        }

        return false;
    }

    private static bool TryConsumeLineBreak(string text, ref int index)
    {
        if (text[index] == '\r')
        {
            if (index + 1 < text.Length && text[index + 1] == '\n')
                index++;

            return true;
        }

        return text[index] == '\n';
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashCount++;

        return slashCount % 2 == 1;
    }

    private static IBrush GetRainbowBrush(int depth) => RainbowBrushes[Math.Abs(depth) % RainbowBrushes.Length];

    private void ApplyBrush(int startOffset, int endOffset, IBrush brush)
    {
        if (endOffset <= startOffset)
            return;

        ChangeLinePart(startOffset, endOffset, element =>
        {
            var properties = element.TextRunProperties.Clone();
            properties.SetForegroundBrush(brush);
            SetTextRunPropertiesMethod?.Invoke(element, [properties]);
        });
    }

    private static IBrush BrushFor(LoadedExtension extension, string tokenName, string fallback)
    {
        var hex = extension.ColorTokens.TryGetValue(tokenName, out var value) ? value : fallback;
        return Brush.Parse(hex);
    }

    private static Regex BuildTokenRegex(IEnumerable<string> tokens)
    {
        var distinctTokens = tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct()
            .OrderByDescending(token => token.Length)
            .Select(Regex.Escape)
            .ToArray();

        return new Regex(
            @"(?<![\p{L}\p{Nd}_])(" + string.Join("|", distinctTokens) + @")(?![\p{L}\p{Nd}_])",
            RegexOptions.Compiled);
    }

    private readonly record struct SyntaxBrushRule(Regex Regex, IBrush Brush, string ColorTokenName);
    private readonly record struct InterpolationSupport(bool SupportsJavaScriptTemplate, bool SupportsPythonStyle, bool SupportsDollarPrefixed)
    {
        public bool HasAny => SupportsJavaScriptTemplate || SupportsPythonStyle || SupportsDollarPrefixed;
    }

    private sealed record InterpolationSnapshot(string Text, List<InterpolationLineState> LineStates)
    {
        public InterpolationLineState GetLineState(int lineNumber) =>
            lineNumber > 0 && lineNumber <= LineStates.Count
                ? LineStates[lineNumber - 1]
                : new InterpolationLineState(null);
    }

    private readonly record struct InterpolationLineState(ActiveInterpolation? ActiveInterpolation);
    private readonly record struct ActiveInterpolation(InterpolationKind Kind, string Quote, bool IsVerbatim, bool CanSpanMultipleLines);

    private enum InterpolationKind
    {
        JavaScriptTemplate,
        PythonFString,
        DollarPrefixed
    }
}
