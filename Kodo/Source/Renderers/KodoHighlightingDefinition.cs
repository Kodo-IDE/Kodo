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
public sealed class KodoHighlightingDefinition : IHighlightingDefinition
{
    private const string VariableIdentifierBodyPattern =
        "[\\p{L}_][\\p{L}\\p{Nd}_]*";
    private const string CommonStringPrefixPattern =
        "(?i)(?<![\\p{L}\\p{Nd}_])(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)(?=(?:\\\"\\\"\\\"|'''|\\\"|'|#+\\\"))";

    private readonly HighlightingRuleSet _mainRuleSet;

    public string Name { get; }
    public HighlightingRuleSet MainRuleSet => _mainRuleSet;
    public IEnumerable<HighlightingColor> NamedHighlightingColors => [];
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

    public KodoHighlightingDefinition(LoadedExtension ext, CompiledSyntaxProfile syntaxProfile)
    {
        Name = ext.Name;
        _mainRuleSet = BuildRuleSet(ext, syntaxProfile);
    }

    private static HighlightingColor ColorFor(LoadedExtension ext, string tokenName, string fallback)
    {
        var hex = ext.ColorTokens.TryGetValue(tokenName, out var h) ? h : fallback;
        return new HighlightingColor { Foreground = new SimpleHighlightingBrush(Color.Parse(hex)) };
    }

    private static bool IsBatchExtension(LoadedExtension ext) =>
        ext.Extensions.Any(e => string.Equals(e, ".bat", StringComparison.OrdinalIgnoreCase) || string.Equals(e, ".cmd", StringComparison.OrdinalIgnoreCase)) ||
        ext.Id.IndexOf("batch", StringComparison.OrdinalIgnoreCase) >= 0;

    private static HighlightingRuleSet BuildRuleSet(LoadedExtension ext, CompiledSyntaxProfile syntaxProfile)
    {
        var commentColor = ColorFor(ext, "comment", "#6A9955");
        var stringColor = ColorFor(ext, "string", "#CE9178");
        var charLiteralColor = ColorFor(ext, "charLiteral", "#CE9178");
        var keywordColor = ColorFor(ext, "keyword", "#569CD6");
        var typeColor = ColorFor(ext, "type", "#4EC9B0");
        var numberColor = ColorFor(ext, "number", "#B5CEA8");
        var functionColor = ColorFor(ext, "function", "#DCDCAA");
        var namespaceColor = ColorFor(ext, "namespace", "#4FC1FF");
        var propertyColor = ColorFor(ext, "property", "#9CDCFE");
        var attributeColor = ColorFor(ext, "attribute", "#C586C0");
        var operatorColor = ColorFor(ext, "operator", "#D4D4D4");
        var punctuationColor = ColorFor(ext, "punctuation", "#D4D4D4");
        var preprocessorColor = ColorFor(ext, "preprocessor", "#C586C0");
        var variableColor = ColorFor(ext, "variable", "#A0DBFD");
        var isBatch = IsBatchExtension(ext);
        var supportsCommonStringPrefixes =
            ext.StringDelimiters.Contains("\"") ||
            ext.StringDelimiters.Contains("'") ||
            ext.MultiLineStringDelimiters.Contains("\"\"\"") ||
            ext.MultiLineStringDelimiters.Contains("'''");

        var isMarkdown = KodoExtensionIds.IsMarkdown(ext.Id);

        var codeRuleSet = new HighlightingRuleSet();
        var emptyRuleSet = new HighlightingRuleSet();

        if (!isMarkdown)
        {
            foreach (var rule in syntaxProfile.TokenRules)
            {
                codeRuleSet.Rules.Add(new HighlightingRule
                {
                    Regex = rule.Regex,
                    Color = ColorFor(ext, rule.ColorTokenName, rule.FallbackHex)
                });
            }
        }
        else
        {
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"\*{2,3}|_{2,3}", RegexOptions.Compiled),
                Color = operatorColor
            });
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"(?<!\*)\*(?!\*)|(?<!_)_(?!_)", RegexOptions.Compiled),
                Color = operatorColor
            });
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"~~", RegexOptions.Compiled),
                Color = operatorColor
            });
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"!?\[", RegexOptions.Compiled),
                Color = punctuationColor
            });
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"\|", RegexOptions.Compiled),
                Color = punctuationColor
            });
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"(?<=^|\n)[ \t]*[-+*](?=[ \t])", RegexOptions.Compiled),
                Color = operatorColor
            });
        }

        var mainRuleSet = new HighlightingRuleSet();

        if (!string.IsNullOrEmpty(ext.CommentBlockStart) && !string.IsNullOrEmpty(ext.CommentBlockEnd))
        {
            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression = new Regex(Regex.Escape(ext.CommentBlockStart), RegexOptions.Compiled),
                EndExpression = new Regex(Regex.Escape(ext.CommentBlockEnd), RegexOptions.Compiled),
                SpanColor = commentColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd = true,
                RuleSet = emptyRuleSet
            });
        }

        if (isMarkdown)
        {

            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression = new Regex(@"^#{1,6}(?=\s)", RegexOptions.Compiled | RegexOptions.Multiline),
                EndExpression = new Regex(@"$", RegexOptions.Compiled),
                SpanColor = keywordColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd = false,
                RuleSet = emptyRuleSet
            });
        }

        if (!isMarkdown && !string.IsNullOrEmpty(ext.CommentLine))
        {
            var commentOptions = isBatch ? RegexOptions.IgnoreCase : RegexOptions.None;
            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression = new Regex(Regex.Escape(ext.CommentLine), RegexOptions.Compiled | commentOptions),
                EndExpression = new Regex("$", RegexOptions.Compiled),
                SpanColor = commentColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd = false,
                RuleSet = emptyRuleSet
            });

            if (isBatch)
            {
                mainRuleSet.Spans.Add(new HighlightingSpan
                {
                    StartExpression = new Regex(@"::", RegexOptions.Compiled),
                    EndExpression = new Regex("$", RegexOptions.Compiled),
                    SpanColor = commentColor,
                    SpanColorIncludesStart = true,
                    SpanColorIncludesEnd = false,
                    RuleSet = emptyRuleSet
                });

                mainRuleSet.Spans.Add(new HighlightingSpan
                {
                    StartExpression = new Regex(@"(?m)^\s*@?echo[\.:\(]?", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                    EndExpression = new Regex("$", RegexOptions.Compiled),
                    SpanColor = ColorFor(ext, "plain", "#D4D4D4"),
                    SpanColorIncludesStart = false,
                    SpanColorIncludesEnd = false,
                    RuleSet = emptyRuleSet
                });

            }
        }
        else if (!isMarkdown && isBatch)
        {
            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression = new Regex(@"::", RegexOptions.Compiled),
                EndExpression = new Regex("$", RegexOptions.Compiled),
                SpanColor = commentColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd = false,
                RuleSet = emptyRuleSet
            });
            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression = new Regex(@"(?m)^\s*@?echo[\.:\(]?", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                EndExpression = new Regex("$", RegexOptions.Compiled),
                SpanColor = ColorFor(ext, "plain", "#D4D4D4"),
                SpanColorIncludesStart = false,
                SpanColorIncludesEnd = false,
                RuleSet = emptyRuleSet
            });
        }

        if (supportsCommonStringPrefixes)
        {
            if (ext.MultiLineStringDelimiters.Contains("\"\"\""))
                mainRuleSet.Spans.Add(CreateRegexStringSpan("(?i)(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)\\\"\\\"\\\"", "\\\"\\\"\\\"", stringColor, emptyRuleSet, allowEndOfLineFallback: false));

            if (ext.MultiLineStringDelimiters.Contains("'''"))
                mainRuleSet.Spans.Add(CreateRegexStringSpan(@"(?i)(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)'''", @"'''", stringColor, emptyRuleSet, allowEndOfLineFallback: false));

            if (ext.StringDelimiters.Contains("\""))
                mainRuleSet.Spans.Add(CreateRegexStringSpan("(?i)(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)\\\"", "\\\"", stringColor, emptyRuleSet, allowEndOfLineFallback: true));

            if (ext.StringDelimiters.Contains("'"))
                mainRuleSet.Spans.Add(CreateRegexStringSpan(@"(?i)(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)'", @"'", stringColor, emptyRuleSet, allowEndOfLineFallback: true));
        }

        if (ext.DisableSingleQuoteStrings && ext.StringDelimiters.Contains("\""))
        {
            mainRuleSet.Spans.Add(CreateRegexStringSpan(@"(?:\$@|@\$)""", @"""(?!"")", stringColor, emptyRuleSet, allowEndOfLineFallback: false, isVerbatim: true));
            mainRuleSet.Spans.Add(CreateRegexStringSpan(@"\$""", @"""", stringColor, emptyRuleSet, allowEndOfLineFallback: true));

            mainRuleSet.Spans.Add(CreateRegexStringSpan(@"@""", @"""(?!"")", stringColor, emptyRuleSet, allowEndOfLineFallback: false, isVerbatim: true));
        }

        foreach (var delimiter in ext.MultiLineStringDelimiters.Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            mainRuleSet.Spans.Add(CreateStringSpan(delimiter, stringColor, emptyRuleSet, allowEndOfLineFallback: false));
        }

        var stringDelimiters = ext.StringDelimiters
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToList();

        if (ext.DisableSingleQuoteStrings)
            stringDelimiters.RemoveAll(d => d == "'");

        if (isMarkdown)
            stringDelimiters.RemoveAll(d => d == "`");

        foreach (var delimiter in stringDelimiters)
            mainRuleSet.Spans.Add(CreateStringSpan(delimiter, stringColor, emptyRuleSet, allowEndOfLineFallback: true));

        foreach (var rule in codeRuleSet.Rules)
            mainRuleSet.Rules.Add(rule);

        return mainRuleSet;
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

    private static Regex BuildVariableRegex(IEnumerable<string> reservedTokens)
    {
        var reserved = reservedTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(token => token.Length)
            .Select(Regex.Escape)
            .ToArray();

        var reservedPrefix = reserved.Length > 0
            ? $"(?!{string.Join("|", reserved.Select(r => r + "(?![\\p{L}\\p{Nd}_])"))})"
            : string.Empty;

        return new Regex(
            $"(?<![.\\p{{L}}\\p{{Nd}}_#@\\$]){reservedPrefix}{VariableIdentifierBodyPattern}(?!\\s*[:\\.(\"'`]|[\\p{{L}}\\p{{Nd}}_])",
            RegexOptions.Compiled);
    }

    private static HighlightingSpan CreateStringSpan(
        string delimiter,
        HighlightingColor stringColor,
        HighlightingRuleSet emptyRuleSet,
        bool allowEndOfLineFallback)
    {
        var escapedDelimiter = Regex.Escape(delimiter);
        return CreateRegexStringSpan(escapedDelimiter, escapedDelimiter, stringColor, emptyRuleSet, allowEndOfLineFallback);
    }

    private static HighlightingSpan CreateRegexStringSpan(
        string startPattern,
        string endDelimiterPattern,
        HighlightingColor stringColor,
        HighlightingRuleSet emptyRuleSet,
        bool allowEndOfLineFallback,
        bool isVerbatim = false)
    {
        var unescapedDelimiterGuard = isVerbatim ? string.Empty : @"(?<=(?:^|[^\\])(?:\\\\)*)";
        var endPattern = allowEndOfLineFallback
            ? $@"{unescapedDelimiterGuard}{endDelimiterPattern}|$"
            : $@"{unescapedDelimiterGuard}{endDelimiterPattern}";

        return new HighlightingSpan
        {
            StartExpression = new Regex(startPattern, RegexOptions.Compiled),
            EndExpression = new Regex(endPattern, RegexOptions.Compiled),
            SpanColor = stringColor,
            SpanColorIncludesStart = true,
            SpanColorIncludesEnd = true,
            RuleSet = emptyRuleSet
        };
    }

    public HighlightingColor GetNamedColor(string name) => new();
    public HighlightingRuleSet GetNamedRuleSet(string name) => new HighlightingRuleSet();
}
