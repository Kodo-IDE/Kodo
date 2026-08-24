// Licensed under GPL-v3.0
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

public sealed class CompiledSyntaxProfile
{
    private const string MarkupTextToken = "markupText";
    private const string MarkupTextFallback = "#eeeeee";
    private const string VariableIdentifierBodyPattern = "[\\p{L}_][\\p{L}\\p{Nd}_]*";
    private const string CommonStringPrefixPattern =
        "(?i)(?<![\\p{L}\\p{Nd}_])(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)(?=(?:\\\"\\\"\\\"|'''|\\\"|'|#+\\\"))";

    public LoadedExtension Extension { get; }
    public IReadOnlyList<CompiledSyntaxRule> TokenRules { get; }
    public IReadOnlyList<Regex> StringRegexes { get; }
    public Regex? SingleLineCommentRegex { get; }

    private CompiledSyntaxProfile(
        LoadedExtension extension,
        IReadOnlyList<CompiledSyntaxRule> tokenRules,
        IReadOnlyList<Regex> stringRegexes,
        Regex? singleLineCommentRegex)
    {
        Extension = extension;
        TokenRules = tokenRules;
        StringRegexes = stringRegexes;
        SingleLineCommentRegex = singleLineCommentRegex;
    }

    public static CompiledSyntaxProfile Create(LoadedExtension extension)
    {
        var rules = new List<CompiledSyntaxRule>();
        var traits = SyntaxLanguageTraits.From(extension);

        if (extension.Keywords.Length > 0)
            rules.Add(CreateTokenRule(extension.Keywords, traits, SyntaxTokenKind.Keyword, "keyword", "#569CD6"));
        if (extension.Types.Length > 0)
            rules.Add(CreateTokenRule(extension.Types, traits, SyntaxTokenKind.Type, "type", "#4EC9B0"));
        if (extension.Namespaces.Length > 0)
            rules.Add(CreateTokenRule(extension.Namespaces, traits, SyntaxTokenKind.Namespace, "namespace", "#4FC1FF"));
        if (extension.Properties.Length > 0)
            rules.Add(CreateTokenRule(extension.Properties, traits, SyntaxTokenKind.Property, "property", "#9CDCFE"));
        if (extension.Functions.Length > 0)
            rules.Add(CreateTokenRule(extension.Functions, traits, SyntaxTokenKind.Function, "function", "#DCDCAA"));

        if (extension.StringDelimiters.Contains("\"") ||
            extension.StringDelimiters.Contains("'") ||
            extension.MultiLineStringDelimiters.Contains("\"\"\"") ||
            extension.MultiLineStringDelimiters.Contains("'''"))
        {
            rules.Add(new(new Regex(CommonStringPrefixPattern, RegexOptions.Compiled), "string", "#CE9178"));
        }

        if (traits.IsMarkupLike)
            rules.Add(new(new Regex(@"(?<=>)\s*v?\d[\d._-]*[A-Za-z0-9]*\s*(?=<)", RegexOptions.Compiled), "number", "#B5CEA8"));

        rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_])(?:0[xX][0-9A-Fa-f]+|0[bB][01]+|0[oO][0-7]+|\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?)(?![\p{L}\p{Nd}_])", RegexOptions.Compiled), "number", "#B5CEA8"));

        if (traits.IsMarkupLike)
            rules.Add(new(new Regex(@"(?<=>)[^<>]+(?=<)", RegexOptions.Compiled), MarkupTextToken, MarkupTextFallback));

        if (traits.IsCssLike)
        {
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_-])#[0-9A-Fa-f]{3}(?:[0-9A-Fa-f]{1}|[0-9A-Fa-f]{3}|[0-9A-Fa-f]{5})?(?![\p{L}\p{Nd}_-])", RegexOptions.Compiled), "number", "#B5CEA8"));
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_-])(?:\d+(?:\.\d+)?|\.\d+)(?:px|r?em|ex|ch|lh|rlh|vw|vh|vmin|vmax|vb|vi|svw|svh|lvw|lvh|dvw|dvh|cqw|cqh|cqi|cqb|cqmin|cqmax|cm|mm|Q|in|pc|pt|deg|grad|rad|turn|s|ms|Hz|kHz|dpi|dpcm|dppx|fr|%)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "number", "#B5CEA8"));
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_-])--[\p{L}_-][\p{L}\p{Nd}_-]*(?![\p{L}\p{Nd}_-])", RegexOptions.Compiled), "property", "#9CDCFE"));
            rules.Add(new(new Regex(@"(?<=:):?[\p{L}_-][\p{L}\p{Nd}_-]*(?![\p{L}\p{Nd}_-])", RegexOptions.Compiled), "keyword", "#569CD6"));
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_-])@[\p{L}_-][\p{L}\p{Nd}_-]*(?![\p{L}\p{Nd}_-])", RegexOptions.Compiled), "preprocessor", "#C586C0"));
        }

        if (traits.IsMarkupLike)
        {
            rules.Add(new(new Regex(@"</?|/?>|=", RegexOptions.Compiled), MarkupTextToken, MarkupTextFallback));
        }
        else
        {
            // Rule order matters: specific rules must precede the catch-all "variable" rule.
            rules.Add(new(new Regex(
                traits.IsCssLike
                    ? @"(?<![\p{L}\p{Nd}_-])@[\p{L}_-][\p{L}\p{Nd}_-]*(?![\p{L}\p{Nd}_-])"
                    : @"(?<![\p{L}\p{Nd}_])[@#][\p{L}_][\p{L}\p{Nd}_-]*",
                RegexOptions.Compiled), "preprocessor", "#C586C0"));
            rules.Add(new(new Regex(@"(?<=\[)[\p{L}_][\p{L}\p{Nd}_:.]*(?=[,\]\(])|(?<=<)[\p{L}_][\p{L}\p{Nd}_:-]*(?=[^>]*>)", RegexOptions.Compiled), "attribute", "#C586C0"));
            // Function calls must score before property-by-dot, so `a.b.Method()` colours "Method" as a function despite the preceding dot.
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_])[\p{L}_][\p{L}\p{Nd}_]*(?=\s*\()", RegexOptions.Compiled), "function", "#DCDCAA"));
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_])[\p{L}_][\p{L}\p{Nd}_]*(?=\.)", RegexOptions.Compiled), "namespace", "#4FC1FF"));
            rules.Add(new(new Regex(@"(?<=\.|->|::)[\p{L}_][\p{L}\p{Nd}_:-]*", RegexOptions.Compiled), "property", "#9CDCFE"));
            rules.Add(new(new Regex(@"=>|->|::|\+\+|--|\+=|-=|\*=|/=|%=|&&|\|\||<<|>>|<=|>=|==|!=|=|\+|-|\*|/|%|!|\?|:|<|>|&|\||\^|~", RegexOptions.Compiled), "operator", "#D4D4D4"));
            rules.Add(new(new Regex(@"[{}\[\]();,.]", RegexOptions.Compiled), "punctuation", "#D4D4D4"));
            rules.Add(new(new Regex(@"(?<=\b(?:using|import|include|require|use|from)\b\s+(?:[\p{L}_][\p{L}\p{Nd}_./\\]*\s*[./\\]\s*)?)[\p{L}_][\p{L}\p{Nd}_]*(?=\s*(?:;|$))", RegexOptions.Compiled), "namespace", "#4FC1FF"));
            rules.Add(new(BuildVariableRegex(extension.Keywords.Concat(extension.Types).Concat(extension.Functions).Concat(extension.Properties).Concat(extension.Namespaces)), "variable", "#A0DBFD"));
        }

        if (traits.IsCssLike)
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_-])#[0-9A-Fa-f]{3}(?:[0-9A-Fa-f]{1}|[0-9A-Fa-f]{3}|[0-9A-Fa-f]{5})?(?![\p{L}\p{Nd}_-])", RegexOptions.Compiled), "number", "#B5CEA8"));

        if (extension.DisableSingleQuoteStrings)
            rules.Add(new(new Regex(@"'(?:\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|x[0-9A-Fa-f]{1,4}|[0-7]{1,3}|[abfnrtv\\""'0])|[^\\'])'", RegexOptions.Compiled), "charLiteral", "#CE9178"));

        var stringRegexes = new List<Regex>();
        foreach (var delimiter in extension.MultiLineStringDelimiters.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            var escaped = Regex.Escape(delimiter);
            stringRegexes.Add(new Regex($@"{escaped}.*?{escaped}", RegexOptions.Compiled));
        }
        foreach (var delimiter in extension.StringDelimiters.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            if (delimiter == "'" && extension.DisableSingleQuoteStrings)
                continue;
            stringRegexes.Add(BuildSingleLineStringRegex(delimiter));
        }

        var singleLineCommentRegex = string.IsNullOrWhiteSpace(extension.CommentLine)
            ? null
            : new Regex(Regex.Escape(extension.CommentLine) + @".*$", RegexOptions.Compiled);

        return new CompiledSyntaxProfile(extension, rules, stringRegexes, singleLineCommentRegex);
    }

    private static CompiledSyntaxRule CreateTokenRule(IEnumerable<string> tokens, SyntaxLanguageTraits traits, SyntaxTokenKind kind, string tokenName, string fallback) =>
        new(traits.IsMarkupLike ? BuildMarkupTokenRegex(tokens, kind) : BuildTokenRegex(tokens, traits), tokenName, fallback);

    private static Regex BuildTokenRegex(IEnumerable<string> tokens, SyntaxLanguageTraits traits)
    {
        var boundary = traits.IsCssLike ? @"[\p{L}\p{Nd}_-]" : @"[\p{L}\p{Nd}_]";
        return new Regex(@"(?<!" + boundary + @")(" + BuildTokenAlternation(tokens) + @")(?!" + boundary + @")", RegexOptions.Compiled);
    }

    private static Regex BuildMarkupTokenRegex(IEnumerable<string> tokens, SyntaxTokenKind kind)
    {
        var alternation = BuildTokenAlternation(tokens);
        var pattern = kind switch
        {
            SyntaxTokenKind.Keyword => @"(?<=</?|<!)(" + alternation + @")(?![\p{L}\p{Nd}_:-])",
            SyntaxTokenKind.Property => @"(?<=\s)(" + alternation + @")(?=\s*=)",
            SyntaxTokenKind.Namespace => @"(?<=\s)(" + alternation + @")(?=:[\p{L}_-][\p{L}\p{Nd}_-]*\s*=)",
            _ => @"(?!)"
        };

        return new Regex(pattern, RegexOptions.Compiled);
    }

    private static string BuildTokenAlternation(IEnumerable<string> tokens) =>
        string.Join("|", tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct()
            .OrderByDescending(token => token.Length)
            .Select(Regex.Escape));

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
        return new Regex($"(?<![.\\p{{L}}\\p{{Nd}}_]){reservedPrefix}{VariableIdentifierBodyPattern}(?!\\s*[\\.(\"'`]|[\\p{{L}}\\p{{Nd}}_])", RegexOptions.Compiled);
    }

    private static Regex BuildSingleLineStringRegex(string delimiter)
    {
        var escaped = Regex.Escape(delimiter);
        return delimiter switch
        {
            "\"" => new Regex("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled),
            "'" => new Regex("'(?:\\\\.|[^'\\\\])*'", RegexOptions.Compiled),
            "`" => new Regex("`(?:\\\\.|[^`\\\\])*`", RegexOptions.Compiled),
            _ => new Regex($@"{escaped}.*?{escaped}", RegexOptions.Compiled)
        };
    }

    private readonly record struct SyntaxLanguageTraits(bool IsCssLike, bool IsMarkupLike)
    {
        public static SyntaxLanguageTraits From(LoadedExtension extension) =>
            new(
                extension.Extensions.Any(ext => string.Equals(ext, ".css", StringComparison.OrdinalIgnoreCase)),
                extension.CommentBlockStart == "<!--" ||
                extension.Extensions.Any(ext =>
                    string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".axaml", StringComparison.OrdinalIgnoreCase)));

    }

    private enum SyntaxTokenKind
    {
        Keyword,
        Type,
        Namespace,
        Property,
        Function
    }
}

public readonly record struct CompiledSyntaxRule(Regex Regex, string ColorTokenName, string FallbackHex);

// Shared embedded-tag content extraction, used by HtmlEmbeddedColorizer and MarkdownColorizer.
public enum EmbeddedBlockContentMode
{
    AwaitingContent,
    Raw,
    InCData
}

public static class EmbeddedTagContent
{
    private const string CDataStart = "<![CDATA[";
    private const string CDataEnd = "]]>";
    public static bool TryExtract(
        string line,
        int start,
        int end,
        EmbeddedBlockContentMode mode,
        out int contentStart,
        out int contentEnd,
        out EmbeddedBlockContentMode nextMode)
    {
        contentStart = start;
        contentEnd = start;
        nextMode = mode;

        if (start >= end)
            return false;

        var currentStart = start;
        if (mode != EmbeddedBlockContentMode.InCData)
        {
            var nonWhitespace = currentStart;
            while (nonWhitespace < end && char.IsWhiteSpace(line[nonWhitespace]))
                nonWhitespace++;

            if (nonWhitespace >= end)
            {
                nextMode = EmbeddedBlockContentMode.AwaitingContent;
                return false;
            }

            if (nonWhitespace + CDataStart.Length <= end &&
                string.CompareOrdinal(line, nonWhitespace, CDataStart, 0, CDataStart.Length) == 0)
            {
                currentStart = nonWhitespace + CDataStart.Length;
                nextMode = EmbeddedBlockContentMode.InCData;
            }
            else
            {
                currentStart = start;
                nextMode = EmbeddedBlockContentMode.Raw;
            }
        }

        var cdataEndIndex = line.IndexOf(CDataEnd, currentStart, StringComparison.Ordinal);
        if (cdataEndIndex >= 0 && cdataEndIndex < end)
        {
            contentStart = currentStart;
            contentEnd = cdataEndIndex;
            nextMode = EmbeddedBlockContentMode.Raw;
            return contentEnd > contentStart;
        }

        contentStart = currentStart;
        contentEnd = end;
        return contentEnd > contentStart;
    }
}

public enum EmbeddedSyntaxScanMode
{
    Normal,
    LineComment,
    BlockComment,
    String,
    MultiLineString
}

public readonly record struct EmbeddedSyntaxState(
    string BracketStack,
    EmbeddedSyntaxScanMode Mode,
    string? Delimiter,
    bool IsVerbatimString,
    bool CanSpanMultipleLines)
{
    public static EmbeddedSyntaxState Empty { get; } = new(string.Empty, EmbeddedSyntaxScanMode.Normal, null, false, false);
}

public sealed class EmbeddedSyntaxProfile
{
    private static readonly string[] CommonStringPrefixes =
    [
        "fr", "rf", "br", "rb", "ur", "ru", "cr", "rc",
        "f", "r", "u", "b", "c"
    ];

    public LoadedExtension Extension { get; }
    public IBrush CommentBrush { get; }
    public IBrush StringBrush { get; }
    public IReadOnlyList<(Regex Regex, IBrush Brush, string ColorTokenName)> TokenRules { get; }

    private EmbeddedSyntaxProfile(
        LoadedExtension extension,
        IBrush commentBrush,
        IBrush stringBrush,
        IReadOnlyList<(Regex Regex, IBrush Brush, string ColorTokenName)> tokenRules)
    {
        Extension = extension;
        CommentBrush = commentBrush;
        StringBrush = stringBrush;
        TokenRules = tokenRules;
    }

    public static EmbeddedSyntaxProfile Create(CompiledSyntaxProfile syntaxProfile)
    {
        var tokenRules = syntaxProfile.TokenRules
            .Select(rule => (
                rule.Regex,
                Brush.Parse(syntaxProfile.Extension.ColorTokens.TryGetValue(rule.ColorTokenName, out var hex)
                    ? hex
                    : rule.FallbackHex),
                rule.ColorTokenName))
            .ToArray();

        return new EmbeddedSyntaxProfile(
            syntaxProfile.Extension,
            Brush.Parse(syntaxProfile.Extension.ColorTokens.TryGetValue("comment", out var commentHex) ? commentHex : "#6A9955"),
            Brush.Parse(syntaxProfile.Extension.ColorTokens.TryGetValue("string", out var stringHex) ? stringHex : "#CE9178"),
            tokenRules);
    }

    public EmbeddedSyntaxState Advance(string text, EmbeddedSyntaxState initialState) =>
        Process(text, initialState, applyBrush: null, rainbowBrushResolver: null);

    public void Colorize(
        string text,
        int lineOffset,
        EmbeddedSyntaxState initialState,
        Action<int, int, IBrush> applyBrush,
        Func<int, IBrush> rainbowBrushResolver)
    {
        _ = Process(text, initialState, (start, end, brush) => applyBrush(lineOffset + start, lineOffset + end, brush), rainbowBrushResolver);
    }

    private EmbeddedSyntaxState Process(
        string text,
        EmbeddedSyntaxState initialState,
        Action<int, int, IBrush>? applyBrush,
        Func<int, IBrush>? rainbowBrushResolver)
    {
        if (string.IsNullOrEmpty(text))
            return initialState;

        var protectedRanges = new bool[text.Length];
        var stack = new Stack<char>(initialState.BracketStack.Reverse());
        var mode = initialState.Mode;
        var activeDelimiter = initialState.Delimiter;
        var isVerbatimString = initialState.IsVerbatimString;
        var canSpanMultipleLines = initialState.CanSpanMultipleLines;
        var segmentStart = mode == EmbeddedSyntaxScanMode.Normal ? -1 : 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (mode == EmbeddedSyntaxScanMode.LineComment)
            {
                if (text[index] is '\r' or '\n')
                {
                    ReserveAndColor(segmentStart, index, CommentBrush);
                    mode = EmbeddedSyntaxScanMode.Normal;
                    activeDelimiter = null;
                    segmentStart = -1;
                }
                continue;
            }

            if (mode == EmbeddedSyntaxScanMode.BlockComment)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) && MatchesAt(text, index, activeDelimiter))
                {
                    var end = index + activeDelimiter.Length;
                    ReserveAndColor(segmentStart, end, CommentBrush);
                    index = end - 1;
                    mode = EmbeddedSyntaxScanMode.Normal;
                    activeDelimiter = null;
                    segmentStart = -1;
                }
                continue;
            }

            if (mode is EmbeddedSyntaxScanMode.String or EmbeddedSyntaxScanMode.MultiLineString)
            {
                if (!string.IsNullOrEmpty(activeDelimiter) &&
                    IsStringTerminator(text, index, activeDelimiter, isVerbatimString))
                {
                    var end = index + activeDelimiter.Length;
                    ReserveAndColor(segmentStart, end, StringBrush);
                    index = end - 1;
                    mode = EmbeddedSyntaxScanMode.Normal;
                    activeDelimiter = null;
                    isVerbatimString = false;
                    canSpanMultipleLines = false;
                    segmentStart = -1;
                    continue;
                }

                if (!canSpanMultipleLines && text[index] is '\r' or '\n')
                {
                    ReserveAndColor(segmentStart, index, StringBrush);
                    mode = EmbeddedSyntaxScanMode.Normal;
                    activeDelimiter = null;
                    isVerbatimString = false;
                    canSpanMultipleLines = false;
                    segmentStart = -1;
                }
                continue;
            }

            if (!string.IsNullOrEmpty(Extension.CommentLine) && MatchesAt(text, index, Extension.CommentLine))
            {
                mode = EmbeddedSyntaxScanMode.LineComment;
                activeDelimiter = null;
                segmentStart = index;
                index += Extension.CommentLine.Length - 1;
                continue;
            }

            if (!string.IsNullOrEmpty(Extension.CommentBlockStart) && MatchesAt(text, index, Extension.CommentBlockStart))
            {
                mode = EmbeddedSyntaxScanMode.BlockComment;
                activeDelimiter = Extension.CommentBlockEnd;
                segmentStart = index;
                index += Extension.CommentBlockStart.Length - 1;
                continue;
            }

            if (TryMatchStringStart(text, index, out var stringStart))
            {
                mode = stringStart.CanSpanMultipleLines ? EmbeddedSyntaxScanMode.MultiLineString : EmbeddedSyntaxScanMode.String;
                activeDelimiter = stringStart.Delimiter;
                isVerbatimString = stringStart.IsVerbatim;
                canSpanMultipleLines = stringStart.CanSpanMultipleLines;
                segmentStart = index;
                index += stringStart.MatchedLength - 1;
                continue;
            }
        }

        if (mode == EmbeddedSyntaxScanMode.LineComment)
        {
            ReserveAndColor(segmentStart, text.Length, CommentBrush);
            mode = EmbeddedSyntaxScanMode.Normal;
            activeDelimiter = null;
        }
        else if (mode is EmbeddedSyntaxScanMode.String or EmbeddedSyntaxScanMode.BlockComment or EmbeddedSyntaxScanMode.MultiLineString)
        {
            if (segmentStart >= 0)
            {
                ReserveAndColor(segmentStart, text.Length, mode == EmbeddedSyntaxScanMode.BlockComment ? CommentBrush : StringBrush);
            }

            if (mode == EmbeddedSyntaxScanMode.String)
            {
                mode = EmbeddedSyntaxScanMode.Normal;
                activeDelimiter = null;
                isVerbatimString = false;
                canSpanMultipleLines = false;
            }
        }

        foreach (var (regex, brush, colorTokenName) in TokenRules)
        {
            // Punctuation doesn't reserve its range, so rainbow bracket color (below) can still repaint it. Every other rule reserves its range.
            var isPunctuation = string.Equals(colorTokenName, "punctuation", StringComparison.Ordinal);

            foreach (Match match in regex.Matches(text))
            {
                if (isPunctuation)
                {
                    if (IsProtected(protectedRanges, match.Index, match.Index + match.Length))
                        continue;

                    applyBrush?.Invoke(match.Index, match.Index + match.Length, brush);
                    continue;
                }

                if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                    continue;

                applyBrush?.Invoke(match.Index, match.Index + match.Length, brush);
            }
        }

        if (rainbowBrushResolver is not null)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (protectedRanges[index])
                    continue;

                var ch = text[index];
                if (TryGetClosingBracket(ch, out _))
                {
                    applyBrush?.Invoke(index, index + 1, rainbowBrushResolver(stack.Count));
                    stack.Push(ch);
                    continue;
                }

                if (TryGetOpeningBracket(ch, out var opening) &&
                    stack.Count > 0 &&
                    stack.Peek() == opening)
                {
                    applyBrush?.Invoke(index, index + 1, rainbowBrushResolver(stack.Count - 1));
                    stack.Pop();
                }
            }
        }

        return new EmbeddedSyntaxState(new string(stack.Reverse().ToArray()), mode, activeDelimiter, isVerbatimString, canSpanMultipleLines);

        void ReserveAndColor(int start, int end, IBrush brush)
        {
            if (!TryReserveRange(protectedRanges, start, end))
                return;

            applyBrush?.Invoke(start, end, brush);
        }
    }

    private bool TryMatchStringStart(string text, int index, out EmbeddedStringStart stringStart)
    {
        foreach (var candidate in Extension.MultiLineStringDelimiters
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct()
                     .OrderByDescending(value => value.Length))
        {
            if (MatchesStringStart(text, index, candidate, out var matchedLength, out var isVerbatim, out var canSpanMultipleLines))
            {
                stringStart = new EmbeddedStringStart(candidate, matchedLength, isVerbatim, true);
                return true;
            }
        }

        foreach (var candidate in Extension.StringDelimiters
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct()
                     .Where(value => !(Extension.DisableSingleQuoteStrings && value == "'"))
                     .OrderByDescending(value => value.Length))
        {
            if (MatchesStringStart(text, index, candidate, out var matchedLength, out var isVerbatim, out var canSpanMultipleLines))
            {
                stringStart = new EmbeddedStringStart(candidate, matchedLength, isVerbatim, canSpanMultipleLines);
                return true;
            }
        }

        stringStart = default;
        return false;
    }

    private bool MatchesStringStart(string text, int index, string delimiter, out int matchedLength, out bool isVerbatim, out bool canSpanMultipleLines)
    {
        if (MatchesAt(text, index, delimiter))
        {
            matchedLength = delimiter.Length;
            isVerbatim = false;
            canSpanMultipleLines = false;
            return true;
        }

        if (MatchesPrefixedDelimiter(text, index, delimiter, out matchedLength))
        {
            isVerbatim = false;
            canSpanMultipleLines = false;
            return true;
        }

        if (delimiter == "\"" && TryMatchCSharpStringPrefix(text, index, out matchedLength, out isVerbatim, out canSpanMultipleLines))
            return true;

        matchedLength = 0;
        isVerbatim = false;
        canSpanMultipleLines = false;
        return false;
    }

    private static bool MatchesPrefixedDelimiter(string text, int index, string delimiter, out int matchedLength)
    {
        foreach (var prefix in CommonStringPrefixes)
        {
            var candidate = prefix + delimiter;
            if (MatchesAt(text, index, candidate))
            {
                matchedLength = candidate.Length;
                return true;
            }
        }

        matchedLength = 0;
        return false;
    }

    private bool TryMatchCSharpStringPrefix(string text, int index, out int matchedLength, out bool isVerbatim, out bool canSpanMultipleLines)
    {
        if (!Extension.DisableSingleQuoteStrings)
        {
            matchedLength = 0;
            isVerbatim = false;
            canSpanMultipleLines = false;
            return false;
        }

        foreach (var candidate in new[] { "$@\"", "@$\"", "$\"", "@\"" })
        {
            if (MatchesAt(text, index, candidate))
            {
                matchedLength = candidate.Length;
                isVerbatim = candidate.Contains('@');
                canSpanMultipleLines = isVerbatim;
                return true;
            }
        }

        matchedLength = 0;
        isVerbatim = false;
        canSpanMultipleLines = false;
        return false;
    }

    private static bool IsStringTerminator(string text, int index, string delimiter, bool isVerbatimString)
    {
        if (!MatchesAt(text, index, delimiter))
            return false;

        if (!isVerbatimString || delimiter != "\"")
            return !IsEscaped(text, index);

        return !(index + 1 < text.Length && text[index + 1] == '"');
    }

    private static bool MatchesAt(string text, int index, string token) =>
        index >= 0 &&
        index + token.Length <= text.Length &&
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--)
            slashCount++;

        return (slashCount & 1) == 1;
    }

    private static bool TryReserveRange(bool[] protectedRanges, int start, int end)
    {
        if (start < 0 || end > protectedRanges.Length || start >= end)
            return false;

        if (IsProtected(protectedRanges, start, end))
            return false;

        for (var index = start; index < end; index++)
            protectedRanges[index] = true;

        return true;
    }

    private static bool IsProtected(bool[] protectedRanges, int start, int end)
    {
        for (var index = Math.Max(0, start); index < end && index < protectedRanges.Length; index++)
        {
            if (protectedRanges[index])
                return true;
        }

        return false;
    }

    private static bool TryGetClosingBracket(char ch, out char closing)
    {
        switch (ch)
        {
            case '(':
                closing = ')';
                return true;
            case '[':
                closing = ']';
                return true;
            case '{':
                closing = '}';
                return true;
            default:
                closing = default;
                return false;
        }
    }

    private static bool TryGetOpeningBracket(char ch, out char opening)
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

    private readonly record struct EmbeddedStringStart(string Delimiter, int MatchedLength, bool IsVerbatim, bool CanSpanMultipleLines);
}

// Decides whether a Markdown inline-code span gets language-specific highlighting or plain string coloring.
public static class InlineCodeLanguageDetector
{
    // Common CLI verbs - a bare command line opening with one of these is treated as shell/console, never a language match.
    private static readonly HashSet<string> ShellVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "dir", "cp", "mv", "rm", "del", "mkdir", "md", "rmdir", "rd",
        "cat", "type", "echo", "pwd", "touch", "chmod", "chown", "sudo",
        "git", "npm", "npx", "yarn", "pnpm", "pip", "pip3", "dotnet", "python",
        "python3", "node", "curl", "wget", "tar", "zip", "unzip", "grep", "find",
        "ssh", "scp", "docker", "kubectl", "code", "explorer", "open", "start",
        "where", "which", "set", "export", "cls", "clear"
    };

    // Punctuation/operators that suggest genuine source code, not prose/paths.
    private static readonly Regex CodePunctuationRegex =
        new(@"[{}();]|=>|::|->|\b(?:&&|\|\|)\b|(?<![<>=!])=(?!=)", RegexOptions.Compiled);

    // A bare path segment chain, e.g. `path\to\thing` or `path/to/thing`.
    private static readonly Regex PathSegmentRegex =
        new(@"^[\w.~-]+(?:[\\/][\w.~-]+){1,}$", RegexOptions.Compiled);

    // A bare HTML/XML tag mention (e.g. `<style>`) is prose referencing markup vocabulary, not a code sample - must never be highlighted as one.
    private static readonly Regex BareMarkupTagRegex =
        new(@"^</?[A-Za-z][\w:-]*(?:\s+[^<>]*)?\s*/?>$", RegexOptions.Compiled);

    public static LoadedExtension? Resolve(IEnumerable<LoadedExtension> extensions, string codeSnippet)
    {
        if (string.IsNullOrWhiteSpace(codeSnippet))
            return null;

        var snippet = codeSnippet.Trim();
        if (snippet.Length < 2)
            return null;

        if (LooksLikeNonCodeProse(snippet))
            return null;

        var hasCodePunctuation = CodePunctuationRegex.IsMatch(snippet);

        var bestMatch = extensions
            .Where(extension =>
                extension.Type == "language" &&
                !KodoExtensionIds.IsMarkdown(extension.Id))
            .Select(extension => Score(extension, snippet, hasCodePunctuation))
            .Where(result => result.Extension is not null)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Extension!.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return bestMatch.Extension;
    }

    // "Looks like non-code prose" (skip language matching) covers: a bare tag
    // mention, a shell command with no code punctuation, or a bare path.
    private static bool LooksLikeNonCodeProse(string snippet)
    {
        if (BareMarkupTagRegex.IsMatch(snippet))
            return true;

        if (CodePunctuationRegex.IsMatch(snippet))
            return false;

        var tokens = snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        if (ShellVerbs.Contains(tokens[0]))
            return true;

        return tokens.All(token =>
            PathSegmentRegex.IsMatch(token) ||
            token.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }

    private static (LoadedExtension? Extension, int Score) Score(LoadedExtension extension, string snippet, bool hasCodePunctuation)
    {
        var keywordHits = CountTokenMatches(snippet, extension.Keywords);
        var typeHits = CountTokenMatches(snippet, extension.Types);
        var functionHits = CountTokenMatches(snippet, extension.Functions);
        var propertyHits = CountTokenMatches(snippet, extension.Properties);
        var namespaceHits = CountTokenMatches(snippet, extension.Namespaces);

        var score = keywordHits * 5 + typeHits * 4 + functionHits * 4 + propertyHits * 3 + namespaceHits * 3;

        if (!string.IsNullOrWhiteSpace(extension.CommentLine) &&
            snippet.Contains(extension.CommentLine, StringComparison.Ordinal))
        {
            score += 2;
        }

        if (extension.DisableSingleQuoteStrings && snippet.Contains("=>", StringComparison.Ordinal))
            score += 2;

        var distinctKindsMatched = new[] { keywordHits, typeHits, functionHits, propertyHits, namespaceHits }.Count(hits => hits > 0);

        // Requires real code punctuation or multiple independent token-kind
        // matches - a single stray keyword hit is no longer enough.
        var isCredible = score > 0 && (hasCodePunctuation || distinctKindsMatched >= 2);

        return isCredible ? (extension, score) : (null, 0);
    }

    private static int CountTokenMatches(string snippet, IEnumerable<string> tokens)
    {
        var total = 0;

        foreach (var token in tokens.Where(token => !string.IsNullOrWhiteSpace(token)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var escaped = Regex.Escape(token);
            var regex = new Regex($@"(?<![\p{{L}}\p{{Nd}}_]){escaped}(?![\p{{L}}\p{{Nd}}_])", RegexOptions.IgnoreCase);
            if (regex.IsMatch(snippet))
                total++;
        }

        return total;
    }
}

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

    // Disabled for unsaved (untitled) files and plain-text files (.txt / .log / .text)
    // so that smart visual features don't activate where no language context exists.
    public bool IsEnabled { get; set; } = true;

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
        }

        InvalidateCache();
    }

    public void InvalidateCache() => _snapshot = null;

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (!IsEnabled)
            return;

        var document = CurrentContext.Document;
        if (document is null || line.Length <= 0)
            return;

        var snapshot = EnsureSnapshot(document.Text ?? string.Empty);
        var lineState = snapshot.GetLineState(line.LineNumber);
        var text = document.GetText(line.Offset, line.Length);
        var stack = new Stack<char>(lineState.BracketStack);
        var mode = lineState.Mode;
        var activeDelimiter = lineState.Delimiter;

        for (var index = 0; index < text.Length; index++)
        {
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

    private ParseSnapshot EnsureSnapshot(string text)
    {
        if (_snapshot is { } snapshot && string.Equals(snapshot.Text, text, StringComparison.Ordinal))
            return snapshot;

        _snapshot = BuildSnapshot(text);
        return _snapshot;
    }

    private ParseSnapshot BuildSnapshot(string text)
    {
        var lineStates = new List<LineState> { new(string.Empty, ScanMode.Normal, null) };
        var stack = new Stack<char>();
        var mode = ScanMode.Normal;
        string? activeDelimiter = null;

        for (var index = 0; index < text.Length; index++)
        {
            if (TryConsumeLineBreak(text, ref index, ref mode, ref activeDelimiter))
            {
                lineStates.Add(new(new string(stack.Reverse().ToArray()), mode, activeDelimiter));
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
                stack.Push(ch);
            }
            else if (ClosingToOpening.TryGetValue(ch, out var opening) &&
                     stack.Count > 0 &&
                     stack.Peek() == opening)
            {
                stack.Pop();
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

    private static string? MatchDelimiter(string text, int index, IEnumerable<string> delimiters) =>
        delimiters.FirstOrDefault(delimiter => MatchesAt(text, index, delimiter));

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
    private static readonly Typeface EmojiTypeface = new(new FontFamily("Segoe UI Emoji"));
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

// Builds an AvaloniaEdit IHighlightingDefinition from a LoadedExtension's rules.
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

        var snapshot = EnsureSnapshot(document.Text ?? string.Empty);
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

    private InterpolationSnapshot EnsureSnapshot(string text)
    {
        if (_snapshot is { } snapshot && string.Equals(snapshot.Text, text, StringComparison.Ordinal))
            return snapshot;

        _snapshot = BuildSnapshot(text);
        return _snapshot;
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

        // Reserves each match's range so specific rules can't be overwritten by generic ones.
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

public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    public int TabSize { get; set; } = 4;

    private const string CommonStringPrefixPattern =
        "(?i)(?<![\\p{L}\\p{Nd}_])(?:fr|rf|br|rb|ur|ru|cr|rc|f|r|u|b|c)(?=(?:\\\"\\\"\\\"|'''|\\\"|'|#+\\\"))";
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
    // Markdown bullets: blue -> magenta -> yellow -> blue ... (distinct from bracket rainbow)
    private static readonly IBrush[] MarkdownBulletBrushes =
    [
        Brush.Parse("#4FC1FF"), // blue
        Brush.Parse("#C586C0"), // magenta
        Brush.Parse("#FFD700"), // yellow
    ];
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Regex HeadingRegex = new(@"^(?<indent>\s{0,3})(?<marker>#{1,6})(?<space>\s+)(?<content>.+?)\s*(?:#+\s*)?$", RegexOptions.Compiled);
    private static readonly Regex SetextHeadingUnderlineRegex = new(@"^\s{0,3}(?:=+|-+)\s*$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRuleRegex = new(@"^\s{0,3}(?:([-*_])(?:\s*\1){2,})\s*$", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"^\s*\d+[.)]\s+", RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new(@"^\s*[-+*]\s+", RegexOptions.Compiled);
    private static readonly Regex TaskListRegex = new(@"^(\s*[-+*]\s+)\[(?<state>[ xX])\]\s+", RegexOptions.Compiled);
    private static readonly Regex BlockquoteRegex = new(@"^(?<indent>\s{0,3})(?<markers>(?:>\s?)+)", RegexOptions.Compiled);
    private static readonly Regex TablePipeRegex = new(@"\|", RegexOptions.Compiled);
    private static readonly Regex TableAlignmentRegex = new(@"^\s*\|?(?:\s*:?-{3,}:?\s*\|)+\s*:?-{3,}:?\s*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"(?<!`)(`+)(?!`)(?<content>.*?[^`])\1(?!`)", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"!\[(?<alt>[^\]]*)\]\((?<image>[^)\r\n]+)\)|\[(?<label>[^\]]+)\]\((?<url>[^)\r\n]+)\)|\[(?<refLabel>[^\]]+)\]\[(?<refId>[^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex LinkReferenceDefinitionRegex = new(@"^\s{0,3}\[(?<id>[^\]]+)\]:\s*(?<url>\S+)(?:\s+(?<title>""[^""]*""|'[^']*'|\([^)]*\)))?\s*$", RegexOptions.Compiled);
    private static readonly Regex AutoLinkRegex = new(@"(?<!\()https?://[^\s)>\]]+|<(?<auto>(?:https?://|mailto:)[^>\r\n]+)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StrongEmphasisRegex = new(@"(?<!\*)\*\*\*(?=\S)(?<content>.+?)(?<=\S)\*\*\*(?!\*)|(?<!_)___(?=\S)(?<content2>.+?)(?<=\S)___(?!_)", RegexOptions.Compiled);
    private static readonly Regex StrongRegex = new(@"(?<!\*)\*\*(?=\S)(?<content>.+?)(?<=\S)\*\*(?!\*)|(?<!_)__(?=\S)(?<content2>.+?)(?<=\S)__(?!_)", RegexOptions.Compiled);
    private static readonly Regex EmphasisRegex = new(@"(?<!\*)\*(?=\S)(?<content>.+?)(?<=\S)\*(?!\*)|(?<!_)_(?=\S)(?<content2>.+?)(?<=\S)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex StrikethroughRegex = new(@"~~(?=\S)(?<content>.+?)(?<=\S)~~", RegexOptions.Compiled);
    private static readonly Regex HtmlCommentRegex = new(@"<!--.*?-->", RegexOptions.Compiled);
    private static readonly Regex InlineHtmlTagRegex = new(@"</?[\p{L}_][\p{L}\p{Nd}_:-]*(?:\s+[^>\r\n]*)?/?>", RegexOptions.Compiled);
    private static readonly Regex InlineHtmlAttributeStringRegex = new(@"\b[\p{L}_:][\p{L}\p{Nd}_:.-]*\s*=\s*(?<value>""[^""]*""|'[^']*')", RegexOptions.Compiled);
    private static readonly Regex HtmlEmbeddedOpenTagRegex =
        new(@"<(?<tag>script|style)\b(?<attrs>[^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlEmbeddedTypeAttributeRegex =
        new(@"\btype\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private MarkdownSnapshot? _snapshot;
    private Func<string, CompiledSyntaxProfile?>? _languageResolver;
    private Func<string, LoadedExtension?>? _inlineLanguageResolver;
    private readonly Dictionary<string, EmbeddedSyntaxProfile> _embeddedProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmbeddedSyntaxProfile?> _htmlEmbeddedProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private IBrush _keywordBrush = Brushes.White;
    private IBrush _typeBrush = Brushes.White;
    private IBrush _stringBrush = Brushes.White;
    private IBrush _commentBrush = Brushes.White;
    private IBrush _operatorBrush = Brushes.White;
    private IBrush _punctuationBrush = Brushes.White;
    private IBrush _variableBrush = Brushes.White;
    private IBrush _mutedBrush = Brushes.White;

    public bool IsEnabled { get; private set; }

    public void InvalidateCache() => _snapshot = null;

    public void UpdateSyntax(LoadedExtension? extension, Func<string, CompiledSyntaxProfile?>? languageResolver, Func<string, LoadedExtension?>? inlineLanguageResolver)
    {
        _snapshot = null;
        _embeddedProfileCache.Clear();
        _htmlEmbeddedProfileCache.Clear();
        _languageResolver = languageResolver;
        _inlineLanguageResolver = inlineLanguageResolver;

        if (extension is null || !IsMarkdownExtension(extension))
        {
            IsEnabled = false;
            return;
        }

        IsEnabled = true;
        _keywordBrush = BrushFor(extension, "keyword", "#569CD6");
        _typeBrush = BrushFor(extension, "type", "#BAE6FD");
        _stringBrush = BrushFor(extension, "string", "#CE9178");
        _commentBrush = BrushFor(extension, "comment", "#6A9955");
        _operatorBrush = BrushFor(extension, "operator", "#C586C0");
        _punctuationBrush = BrushFor(extension, "punctuation", "#569CD6");
        _variableBrush = BrushFor(extension, "variable", "#F4F4F4");
        _mutedBrush = Brush.Parse("#7A7A7A");
    }

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (!IsEnabled)
            return;

        var document = CurrentContext.Document;
        if (document is null)
            return;

        var text = document.GetText(line.Offset, line.Length);
        var state = EnsureSnapshot(document.Text ?? string.Empty).GetLineState(line.LineNumber);

        if (state.Delimiter is not null)
        {
            ColorizeFenceDelimiter(text, line.Offset, state.Delimiter.Value);
            return;
        }

        if (state.ActiveFence is { } fence)
        {
            ColorizeEmbeddedCode(text, line.Offset, fence);
            foreach (var segment in state.HtmlSegments)
                ColorizeHtmlEmbeddedSegment(text, line.Offset, segment);
            return;
        }

        if (state.HtmlSegments.Count > 0)
        {
            ColorizeMarkdownLine(text, line.Offset);
            foreach (var segment in state.HtmlSegments)
                ColorizeHtmlEmbeddedSegment(text, line.Offset, segment);
            return;
        }

        ColorizeMarkdownLine(text, line.Offset);
    }

    private void ColorizeMarkdownLine(string text, int lineOffset)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var protectedRanges = new bool[text.Length];

        var headingMatch = HeadingRegex.Match(text);
        if (headingMatch.Success)
        {
            ApplyBrush(lineOffset, headingMatch.Groups["marker"].Index, headingMatch.Groups["marker"].Index + headingMatch.Groups["marker"].Length, _keywordBrush);
            ApplyBrush(lineOffset, headingMatch.Groups["content"].Index, headingMatch.Groups["content"].Index + headingMatch.Groups["content"].Length, _keywordBrush);
            var trailingMarkerStart = headingMatch.Groups["content"].Index + headingMatch.Groups["content"].Length;
            if (trailingMarkerStart < text.Length)
                ApplyBrush(lineOffset, trailingMarkerStart, text.Length, _mutedBrush);
            return;
        }

        if (SetextHeadingUnderlineRegex.IsMatch(text))
        {
            ApplyBrush(lineOffset, 0, text.Length, _keywordBrush);
            return;
        }

        if (HorizontalRuleRegex.IsMatch(text))
        {
            ApplyBrush(lineOffset, 0, text.Length, _mutedBrush);
            return;
        }

        if (TableAlignmentRegex.IsMatch(text))
        {
            ApplyBrush(lineOffset, 0, text.Length, _mutedBrush);
            foreach (Match match in TablePipeRegex.Matches(text))
                ApplyBrush(lineOffset, match.Index, match.Index + match.Length, _punctuationBrush);
            return;
        }

        var linkReferenceDefinitionMatch = LinkReferenceDefinitionRegex.Match(text);
        if (linkReferenceDefinitionMatch.Success)
        {
            ApplyBrush(lineOffset, 0, text.Length, _variableBrush);
            ApplyBrush(lineOffset, linkReferenceDefinitionMatch.Groups["id"].Index, linkReferenceDefinitionMatch.Groups["id"].Index + linkReferenceDefinitionMatch.Groups["id"].Length, _typeBrush);
            ApplyBrush(lineOffset, linkReferenceDefinitionMatch.Groups["url"].Index, linkReferenceDefinitionMatch.Groups["url"].Index + linkReferenceDefinitionMatch.Groups["url"].Length, _stringBrush);
            if (linkReferenceDefinitionMatch.Groups["title"].Success)
            {
                ApplyBrush(lineOffset, linkReferenceDefinitionMatch.Groups["title"].Index, linkReferenceDefinitionMatch.Groups["title"].Index + linkReferenceDefinitionMatch.Groups["title"].Length, _stringBrush);
            }
            foreach (var index in AllIndexesOf(text, ':'))
                ApplyBrush(lineOffset, index, index + 1, _punctuationBrush);
            return;
        }

        var taskMatch = TaskListRegex.Match(text);
        if (taskMatch.Success)
        {
            var taskDepth = GetListDepth(text);
            ApplyBrush(lineOffset, 0, taskMatch.Groups[1].Length, GetBulletBrush(taskDepth));
            ApplyBrush(lineOffset, taskMatch.Groups[1].Length, taskMatch.Length, _punctuationBrush);
            var stateGroup = taskMatch.Groups["state"];
            ApplyBrush(lineOffset, stateGroup.Index, stateGroup.Index + stateGroup.Length, _stringBrush);
            MarkRange(protectedRanges, 0, taskMatch.Length);
        }
        else
        {
            var orderedMatch = OrderedListRegex.Match(text);
            if (orderedMatch.Success)
            {
                var orderedDepth = GetListDepth(text);
                ApplyBrush(lineOffset, 0, orderedMatch.Length, GetBulletBrush(orderedDepth));
                MarkRange(protectedRanges, 0, orderedMatch.Length);
            }

            var unorderedMatch = UnorderedListRegex.Match(text);
            if (unorderedMatch.Success)
            {
                var unorderedDepth = GetListDepth(text);
                ApplyBrush(lineOffset, 0, unorderedMatch.Length, GetBulletBrush(unorderedDepth));
                MarkRange(protectedRanges, 0, unorderedMatch.Length);
            }
        }

        var quoteMatch = BlockquoteRegex.Match(text);
        if (quoteMatch.Success)
        {
            var markers = quoteMatch.Groups["markers"];
            ApplyBrush(lineOffset, markers.Index, markers.Index + markers.Length, _commentBrush);
            MarkRange(protectedRanges, markers.Index, markers.Index + markers.Length);
        }

        // Code spans bind tighter than any inline construct, so they're colorized first.
        foreach (Match match in InlineCodeRegex.Matches(text))
        {
            if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                continue;

            ColorizeInlineCode(match, lineOffset);
        }

        foreach (Match match in HtmlCommentRegex.Matches(text))
        {
            if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                continue;

            ApplyBrush(lineOffset, match.Index, match.Index + match.Length, _commentBrush);
        }

        foreach (Match tagMatch in InlineHtmlTagRegex.Matches(text))
        {
            foreach (Match attrMatch in InlineHtmlAttributeStringRegex.Matches(tagMatch.Value))
            {
                var valueGroup = attrMatch.Groups["value"];
                if (!valueGroup.Success)
                    continue;

                var start = tagMatch.Index + valueGroup.Index;
                var end = start + valueGroup.Length;
                if (!TryReserveRange(protectedRanges, start, end))
                    continue;

                ApplyBrush(lineOffset, start, end, _stringBrush);
            }
        }

        foreach (Match match in LinkRegex.Matches(text))
        {
            if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                continue;

            ApplyBrush(lineOffset, match.Index, match.Index + match.Length, _variableBrush);

            if (match.Value.StartsWith("!", StringComparison.Ordinal))
                ApplyBrush(lineOffset, match.Index, match.Index + 1, _punctuationBrush);

            var labelGroup = match.Groups["label"];
            if (labelGroup.Success)
                ApplyBrush(lineOffset, labelGroup.Index, labelGroup.Index + labelGroup.Length, _typeBrush);

            var altGroup = match.Groups["alt"];
            if (altGroup.Success)
                ApplyBrush(lineOffset, altGroup.Index, altGroup.Index + altGroup.Length, _typeBrush);

            var urlGroup = match.Groups["url"];
            if (urlGroup.Success)
                ApplyBrush(lineOffset, urlGroup.Index, urlGroup.Index + urlGroup.Length, _stringBrush);

            var imageGroup = match.Groups["image"];
            if (imageGroup.Success)
                ApplyBrush(lineOffset, imageGroup.Index, imageGroup.Index + imageGroup.Length, _stringBrush);

            var refIdGroup = match.Groups["refId"];
            if (refIdGroup.Success)
                ApplyBrush(lineOffset, refIdGroup.Index, refIdGroup.Index + refIdGroup.Length, _stringBrush);


        }

        foreach (Match match in AutoLinkRegex.Matches(text))
        {
            if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                continue;

            ApplyBrush(lineOffset, match.Index, match.Index + match.Length, _stringBrush);
            if (match.Value.StartsWith("<", StringComparison.Ordinal) && match.Value.EndsWith(">", StringComparison.Ordinal))
            {
                ApplyBrush(lineOffset, match.Index, match.Index + 1, _punctuationBrush);
                ApplyBrush(lineOffset, match.Index + match.Length - 1, match.Index + match.Length, _punctuationBrush);
            }
        }

        // Table-pipe coloring runs last and respects ranges already claimed.
        foreach (Match match in TablePipeRegex.Matches(text))
        {
            if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                continue;

            ApplyBrush(lineOffset, match.Index, match.Index + match.Length, _mutedBrush);
        }

        ApplyDelimitedMarkdownRegex(text, lineOffset, protectedRanges, StrongEmphasisRegex, _operatorBrush);
        ApplyDelimitedMarkdownRegex(text, lineOffset, protectedRanges, StrongRegex, _keywordBrush);
        ApplyDelimitedMarkdownRegex(text, lineOffset, protectedRanges, EmphasisRegex, _operatorBrush);
        ApplyDelimitedMarkdownRegex(text, lineOffset, protectedRanges, StrikethroughRegex, _mutedBrush);
    }

    private void ColorizeFenceDelimiter(string text, int lineOffset, FenceDelimiterInfo delimiter)
    {
        ApplyBrush(lineOffset, 0, text.Length, _mutedBrush);

        var trimmedStart = text.Length - text.TrimStart().Length;
        var markerLength = delimiter.MarkerLength;
        ApplyBrush(lineOffset, trimmedStart, trimmedStart + markerLength, _punctuationBrush);

        if (!string.IsNullOrWhiteSpace(delimiter.LanguageLabel))
        {
            var languageStart = trimmedStart + markerLength;
            while (languageStart < text.Length && char.IsWhiteSpace(text[languageStart]))
                languageStart++;

            var languageEnd = languageStart + delimiter.LanguageLabel.Length;
            if (languageEnd <= text.Length)
                ApplyBrush(lineOffset, languageStart, languageEnd, _typeBrush);
        }
    }

    private void ColorizeEmbeddedCode(string text, int lineOffset, FenceState fence)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (fence.Profile is null)
        {
            // Unknown/plain fenced language: paint the whole line white so no other rules bleed in.
            ApplyBrush(lineOffset, 0, text.Length, Brushes.White);
            return;
        }

        fence.Profile.Colorize(
            text,
            lineOffset,
            fence.State,
            (start, end, brush) => ApplyBrush(0, start, end, brush),
            GetRainbowBrush);
    }

    private void ColorizeHtmlEmbeddedSegment(string text, int lineOffset, MarkdownHtmlSegment segment)
    {
        if (segment.Profile is null || segment.End <= segment.Start || segment.Start >= text.Length)
            return;

        var start = Math.Max(0, Math.Min(segment.Start, text.Length));
        var end = Math.Max(start, Math.Min(segment.End, text.Length));
        if (end <= start)
            return;

        segment.Profile.Colorize(
            text[start..end],
            lineOffset + start,
            segment.State,
            (tokenStart, tokenEnd, brush) => ApplyBrush(0, tokenStart, tokenEnd, brush),
            GetRainbowBrush);
    }

    private void ApplyDelimitedMarkdownRegex(string text, int lineOffset, bool[] protectedRanges, Regex regex, IBrush brush)
    {
        foreach (Match match in regex.Matches(text))
        {
            if (!TryReserveRange(protectedRanges, match.Index, match.Index + match.Length))
                continue;

            ApplyBrush(lineOffset, match.Index, match.Index + match.Length, brush);
        }
    }

    private MarkdownSnapshot EnsureSnapshot(string text)
    {
        if (_snapshot is { } snapshot && string.Equals(snapshot.Text, text, StringComparison.Ordinal))
            return snapshot;

        _snapshot = BuildSnapshot(text);
        return _snapshot;
    }

    private MarkdownSnapshot BuildSnapshot(string text)
    {
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var states = new List<MarkdownLineState>(lines.Length);
        FenceState? activeFence = null;
        MarkdownHtmlBlock? activeHtmlBlock = null;
        var currentState = EmbeddedSyntaxState.Empty;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var htmlSegments = activeFence is null
                ? BuildMarkdownHtmlSegments(line, ref activeHtmlBlock)
                : [];

            if (activeFence is null)
            {
                if (TryParseFenceOpening(trimmed, out var opening))
                {
                    states.Add(new MarkdownLineState(null, opening, htmlSegments));
                    activeFence = new FenceState(
                        opening.MarkerChar,
                        opening.MarkerLength,
                        opening.LanguageLabel,
                        ResolveEmbeddedProfile(opening.LanguageLabel),
                        EmbeddedSyntaxState.Empty,
                        null);
                    currentState = EmbeddedSyntaxState.Empty;
                    continue;
                }

                states.Add(new MarkdownLineState(null, null, htmlSegments));
                continue;
            }

            if (TryParseFenceClosing(trimmed, activeFence, out var closing))
            {
                states.Add(new MarkdownLineState(null, closing, []));
                activeFence = null;
                currentState = EmbeddedSyntaxState.Empty;
                continue;
            }

            var fenceHtmlBlock = activeFence.HtmlBlock;
            var fenceHtmlSegments = SupportsMarkdownNestedHtml(activeFence.Profile)
                ? BuildMarkdownHtmlSegments(line, ref fenceHtmlBlock)
                : [];
            var lineFenceState = activeFence with { State = currentState, HtmlBlock = fenceHtmlBlock };
            states.Add(new MarkdownLineState(lineFenceState, null, fenceHtmlSegments));
            activeFence = activeFence with { HtmlBlock = fenceHtmlBlock };
            if (activeFence.Profile is not null)
                currentState = activeFence.Profile.Advance(line, currentState);
        }

        return new MarkdownSnapshot(text, states);
    }

    private void ColorizeInlineCode(Match match, int lineOffset)
    {
        var openingTicks = match.Groups[1];
        var content = match.Groups["content"];
        var closingTicks = match.Groups[1];
        var closingIndex = match.Index + match.Length - closingTicks.Length;

        ApplyBrush(lineOffset, openingTicks.Index, openingTicks.Index + openingTicks.Length, _stringBrush);
        ApplyBrush(lineOffset, closingIndex, closingIndex + closingTicks.Length, _stringBrush);

        if (!content.Success || string.IsNullOrWhiteSpace(content.Value))
        {
            ApplyBrush(lineOffset, match.Index, match.Index + match.Length, _stringBrush);
            return;
        }

        // Tries to recognize the snippet language, falling back to a flat color.
        var profile = ResolveInlineEmbeddedProfile(content.Value);
        if (profile is null)
        {
            ApplyBrush(lineOffset, content.Index, content.Index + content.Length, _stringBrush);
            return;
        }

        profile.Colorize(
            content.Value,
            lineOffset + content.Index,
            EmbeddedSyntaxState.Empty,
            (start, end, brush) => ApplyBrush(0, start, end, brush),
            GetRainbowBrush);
    }

    private EmbeddedSyntaxProfile? ResolveEmbeddedProfile(string? languageLabel)
    {
        if (string.IsNullOrWhiteSpace(languageLabel) || _languageResolver is null)
            return null;

        var profile = _languageResolver(languageLabel);
        return ResolveEmbeddedProfile(profile);
    }

    private EmbeddedSyntaxProfile? ResolveEmbeddedProfile(CompiledSyntaxProfile? syntaxProfile)
    {
        if (syntaxProfile is null || IsMarkdownExtension(syntaxProfile.Extension))
            return null;

        var cacheKey = $"{syntaxProfile.Extension.Id}|{syntaxProfile.Extension.Version}";
        if (_embeddedProfileCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var profile = EmbeddedSyntaxProfile.Create(syntaxProfile);
        _embeddedProfileCache[cacheKey] = profile;
        return profile;
    }

    // Detects an inline snippet's language and resolves the matching syntax profile.
    private EmbeddedSyntaxProfile? ResolveInlineEmbeddedProfile(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || _inlineLanguageResolver is null)
            return null;

        var extension = _inlineLanguageResolver(content);
        if (extension is null || IsMarkdownExtension(extension))
            return null;

        var cacheKey = $"{extension.Id}|{extension.Version}";
        if (_embeddedProfileCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var profile = EmbeddedSyntaxProfile.Create(CompiledSyntaxProfile.Create(extension));
        _embeddedProfileCache[cacheKey] = profile;
        return profile;
    }

    private List<MarkdownHtmlSegment> BuildMarkdownHtmlSegments(string line, ref MarkdownHtmlBlock? active)
    {
        var segments = new List<MarkdownHtmlSegment>();
        var cursor = 0;

        while (cursor <= line.Length)
        {
            if (active is not null)
            {
                var closeMatch = BuildHtmlCloseTagRegex(active.TagName).Match(line, cursor);
                var segmentEnd = closeMatch.Success ? closeMatch.Index : line.Length;
                if (active.Profile is not null && segmentEnd > cursor)
                {
                    if (EmbeddedTagContent.TryExtract(line, cursor, segmentEnd, active.ContentMode, out var contentStart, out var contentEnd, out var nextContentMode))
                    {
                        var segmentText = line[contentStart..contentEnd];
                        segments.Add(new MarkdownHtmlSegment(contentStart, contentEnd, active.Profile, active.State));
                        active = active with
                        {
                            State = active.Profile.Advance(segmentText, active.State),
                            ContentMode = nextContentMode
                        };
                    }
                    else
                    {
                        active = active with { ContentMode = nextContentMode };
                    }
                }

                if (!closeMatch.Success)
                    break;

                active = null;
                cursor = closeMatch.Index + closeMatch.Length;
                continue;
            }

            var openMatch = HtmlEmbeddedOpenTagRegex.Match(line, cursor);
            if (!openMatch.Success)
                break;

            var tagName = openMatch.Groups["tag"].Value;
            var openEnd = openMatch.Index + openMatch.Length;
            var profile = ResolveMarkdownHtmlEmbeddedProfile(tagName, ExtractHtmlTypeAttribute(openMatch.Groups["attrs"].Value));
            var inlineCloseMatch = BuildHtmlCloseTagRegex(tagName).Match(line, openEnd);

            if (inlineCloseMatch.Success)
            {
                if (profile is not null && inlineCloseMatch.Index > openEnd &&
                    EmbeddedTagContent.TryExtract(line, openEnd, inlineCloseMatch.Index, EmbeddedBlockContentMode.AwaitingContent, out var inlineContentStart, out var inlineContentEnd, out _))
                {
                    segments.Add(new MarkdownHtmlSegment(inlineContentStart, inlineContentEnd, profile, EmbeddedSyntaxState.Empty));
                }

                cursor = inlineCloseMatch.Index + inlineCloseMatch.Length;
                continue;
            }

            active = new MarkdownHtmlBlock(tagName, profile, EmbeddedSyntaxState.Empty, EmbeddedBlockContentMode.AwaitingContent);
            if (profile is not null && openEnd < line.Length)
            {
                if (EmbeddedTagContent.TryExtract(line, openEnd, line.Length, active.ContentMode, out var contentStart, out var contentEnd, out var nextContentMode))
                {
                    var segmentText = line[contentStart..contentEnd];
                    segments.Add(new MarkdownHtmlSegment(contentStart, contentEnd, profile, EmbeddedSyntaxState.Empty));
                    active = active with
                    {
                        State = profile.Advance(segmentText, EmbeddedSyntaxState.Empty),
                        ContentMode = nextContentMode
                    };
                }
                else
                {
                    active = active with { ContentMode = nextContentMode };
                }
            }
            break;
        }

        return segments;
    }

    private EmbeddedSyntaxProfile? ResolveMarkdownHtmlEmbeddedProfile(string tagName, string? typeAttribute)
    {
        if (_languageResolver is null)
            return null;

        var cacheKey = $"{tagName}|{typeAttribute ?? string.Empty}";
        if (_htmlEmbeddedProfileCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var normalizedTag = tagName.Trim().ToLowerInvariant();
        var normalizedType = (typeAttribute ?? string.Empty).Trim();
        string languageLabel;

        if (normalizedTag == "style")
        {
            languageLabel = "css";
        }
        else if (string.IsNullOrWhiteSpace(normalizedType) ||
                 string.Equals(normalizedType, "module", StringComparison.OrdinalIgnoreCase) ||
                 normalizedType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                 normalizedType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase) ||
                 normalizedType.Contains("jscript", StringComparison.OrdinalIgnoreCase))
        {
            languageLabel = "js";
        }
        else if (normalizedType.Contains("typescript", StringComparison.OrdinalIgnoreCase))
        {
            languageLabel = "ts";
        }
        else if (normalizedType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                 normalizedType.Contains("importmap", StringComparison.OrdinalIgnoreCase))
        {
            languageLabel = "json";
        }
        else if (normalizedType.Contains("css", StringComparison.OrdinalIgnoreCase))
        {
            languageLabel = "css";
        }
        else
        {
            _htmlEmbeddedProfileCache[cacheKey] = null;
            return null;
        }

        var profile = ResolveEmbeddedProfile(_languageResolver(languageLabel));
        _htmlEmbeddedProfileCache[cacheKey] = profile;
        return profile;
    }

    private static string? ExtractHtmlTypeAttribute(string attrs)
    {
        var match = HtmlEmbeddedTypeAttributeRegex.Match(attrs);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static Regex BuildHtmlCloseTagRegex(string tagName) =>
        new($@"</\s*{Regex.Escape(tagName)}\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool SupportsMarkdownNestedHtml(EmbeddedSyntaxProfile? profile) =>
        profile?.Extension.Extensions.Any(ext =>
            string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".axaml", StringComparison.OrdinalIgnoreCase)) == true;

    private static IBrush GetRainbowBrush(int depth) => RainbowBrushes[Math.Abs(depth) % RainbowBrushes.Length];

    private static int GetIndentWidth(string line)
    {
        var w = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') w++;
            else if (ch == '\t') w += 4;
            else break;
        }
        return w;
    }

    private int GetListDepth(string line)
    {
        var tabs = line.TakeWhile(c => c == '\t').Count();
        if (tabs > 0) return tabs;
        return GetIndentWidth(line) / TabSize;
    }

    private static IBrush GetBulletBrush(int depth) => MarkdownBulletBrushes[Math.Abs(depth) % MarkdownBulletBrushes.Length];

    private void ApplyBrush(int lineOffset, int startOffset, int endOffset, IBrush brush)
    {
        if (endOffset <= startOffset)
            return;

        ChangeLinePart(lineOffset + startOffset, lineOffset + endOffset, element =>
        {
            var properties = element.TextRunProperties.Clone();
            properties.SetForegroundBrush(brush);
            SetTextRunPropertiesMethod?.Invoke(element, [properties]);
        });
    }

    private static bool TryParseFenceOpening(string trimmed, out FenceDelimiterInfo delimiter)
    {
        delimiter = default;
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var markerChar = trimmed[0];
        if (markerChar is not ('`' or '~'))
            return false;

        var markerLength = 0;
        while (markerLength < trimmed.Length && trimmed[markerLength] == markerChar)
            markerLength++;

        if (markerLength < 3)
            return false;

        var info = trimmed[markerLength..].Trim();
        // CommonMark: info string for backtick fences cannot contain backticks.
        if (markerChar == '`' && info.Contains('`'))
            return false;
        delimiter = new FenceDelimiterInfo(markerChar, markerLength, info);
        return true;
    }

    private static bool TryParseFenceClosing(string trimmed, FenceState activeFence, out FenceDelimiterInfo delimiter)
    {
        delimiter = default;
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed[0] != activeFence.MarkerChar)
            return false;

        var markerLength = 0;
        while (markerLength < trimmed.Length && trimmed[markerLength] == activeFence.MarkerChar)
            markerLength++;

        if (markerLength < activeFence.MarkerLength || !string.IsNullOrWhiteSpace(trimmed[markerLength..]))
            return false;

        delimiter = new FenceDelimiterInfo(activeFence.MarkerChar, markerLength, string.Empty);
        return true;
    }

    private static bool IsMarkdownExtension(LoadedExtension? extension) =>
        KodoExtensionIds.IsMarkdown(extension?.Id);

    private static IBrush BrushFor(LoadedExtension extension, string tokenName, string fallback)
    {
        var hex = extension.ColorTokens.TryGetValue(tokenName, out var value) ? value : fallback;
        return Brush.Parse(hex);
    }

    private static bool TryReserveRange(bool[] protectedRanges, int start, int end)
    {
        if (IsProtected(protectedRanges, start, end))
            return false;

        MarkRange(protectedRanges, start, end);
        return true;
    }

    private static bool IsProtected(bool[] protectedRanges, int start, int end)
    {
        for (var index = start; index < end && index < protectedRanges.Length; index++)
        {
            if (protectedRanges[index])
                return true;
        }

        return false;
    }

    private static void MarkRange(bool[] protectedRanges, int start, int end)
    {
        for (var index = start; index < end && index < protectedRanges.Length; index++)
            protectedRanges[index] = true;
    }

    private static IEnumerable<int> AllIndexesOf(string text, char character)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == character)
                yield return index;
        }
    }

    private sealed record MarkdownSnapshot(string Text, List<MarkdownLineState> LineStates)
    {
        public MarkdownLineState GetLineState(int lineNumber) =>
            lineNumber > 0 && lineNumber <= LineStates.Count
                ? LineStates[lineNumber - 1]
                : new MarkdownLineState(null, null, []);
    }

    private readonly record struct MarkdownLineState(FenceState? ActiveFence, FenceDelimiterInfo? Delimiter, IReadOnlyList<MarkdownHtmlSegment> HtmlSegments);
    private readonly record struct FenceDelimiterInfo(char MarkerChar, int MarkerLength, string? LanguageLabel);
    private sealed record FenceState(char MarkerChar, int MarkerLength, string? LanguageLabel, EmbeddedSyntaxProfile? Profile, EmbeddedSyntaxState State, MarkdownHtmlBlock? HtmlBlock);
    private sealed record MarkdownHtmlSegment(int Start, int End, EmbeddedSyntaxProfile? Profile, EmbeddedSyntaxState State);
    private sealed record MarkdownHtmlBlock(string TagName, EmbeddedSyntaxProfile? Profile, EmbeddedSyntaxState State, EmbeddedBlockContentMode ContentMode);
}

internal sealed class HtmlEmbeddedColorizer : DocumentColorizingTransformer
{
    private static readonly IBrush[] RainbowBrushes =
    [
        Brush.Parse("#FFD700"),
        Brush.Parse("#DA70D6"),
        Brush.Parse("#4FC1FF"),
        Brush.Parse("#C586C0"),
        Brush.Parse("#9CDCFE"),
        Brush.Parse("#D7BA7D")
    ];

    private static readonly Regex OpenTagRegex =
        new(@"<(?<tag>script|style|x:code)\b(?<attrs>[^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeAttributeRegex =
        new(@"\btype\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly MethodInfo? SetTextRunPropertiesMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);

    private HtmlSnapshot? _snapshot;
    private Func<string, string?, CompiledSyntaxProfile?>? _languageResolver;
    private readonly Dictionary<string, EmbeddedSyntaxProfile?> _profileCache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled { get; private set; }

    public void InvalidateCache() => _snapshot = null;

    public void UpdateSyntax(LoadedExtension? extension, Func<string, string?, CompiledSyntaxProfile?>? languageResolver)
    {
        _snapshot = null;
        _profileCache.Clear();
        _languageResolver = languageResolver;
        IsEnabled = extension is not null && SupportsEmbeddedLanguageTags(extension);
    }

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (!IsEnabled)
            return;

        var document = CurrentContext.Document;
        if (document is null || line.Length <= 0)
            return;

        var text = document.GetText(line.Offset, line.Length);
        var state = EnsureSnapshot(document.Text ?? string.Empty).GetLineState(line.LineNumber);

        foreach (var segment in state.Segments)
        {
            if (segment.Profile is null || segment.End <= segment.Start || segment.Start >= text.Length)
                continue;

            var start = Math.Max(0, Math.Min(segment.Start, text.Length));
            var end = Math.Max(start, Math.Min(segment.End, text.Length));
            if (end <= start)
                continue;

            ColorizeEmbeddedSegment(
                text[start..end],
                line.Offset + start,
                segment.Profile,
                segment.State);
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

    private HtmlSnapshot EnsureSnapshot(string text)
    {
        if (_snapshot is { } snapshot && string.Equals(snapshot.Text, text, StringComparison.Ordinal))
            return snapshot;

        _snapshot = BuildSnapshot(text);
        return _snapshot;
    }

    private HtmlSnapshot BuildSnapshot(string text)
    {
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var states = new List<HtmlLineState>(lines.Length);
        ActiveHtmlBlock? active = null;

        foreach (var line in lines)
            states.Add(BuildLineState(line, ref active));

        return new HtmlSnapshot(text, states);
    }

    private HtmlLineState BuildLineState(string line, ref ActiveHtmlBlock? active)
    {
        var segments = new List<HtmlEmbeddedSegment>();
        var cursor = 0;

        while (cursor <= line.Length)
        {
            if (active is not null)
            {
                var closeMatch = BuildCloseTagRegex(active.TagName).Match(line, cursor);
                var segmentEnd = closeMatch.Success ? closeMatch.Index : line.Length;

                if (active.Profile is not null && segmentEnd > cursor)
                {
                    if (EmbeddedTagContent.TryExtract(line, cursor, segmentEnd, active.ContentMode, out var contentStart, out var contentEnd, out var nextContentMode))
                    {
                        var segmentText = line[contentStart..contentEnd];
                        segments.Add(new HtmlEmbeddedSegment(
                            contentStart,
                            contentEnd,
                            active.Profile,
                            active.State));
                        active = active with
                        {
                            State = active.Profile.Advance(segmentText, active.State),
                            ContentMode = nextContentMode
                        };
                    }
                    else
                    {
                        active = active with { ContentMode = nextContentMode };
                    }
                }

                if (!closeMatch.Success)
                    break;

                active = null;
                cursor = closeMatch.Index + closeMatch.Length;
                continue;
            }

            var openMatch = OpenTagRegex.Match(line, cursor);
            if (!openMatch.Success)
                break;

            var tagName = openMatch.Groups["tag"].Value;
            var attrs = openMatch.Groups["attrs"].Value;
            var openEnd = openMatch.Index + openMatch.Length;
            var profile = ResolveEmbeddedProfile(tagName, ExtractTypeAttribute(attrs));
            var inlineCloseMatch = BuildCloseTagRegex(tagName).Match(line, openEnd);

            if (inlineCloseMatch.Success)
            {
                if (profile is not null && inlineCloseMatch.Index > openEnd &&
                    EmbeddedTagContent.TryExtract(line, openEnd, inlineCloseMatch.Index, EmbeddedBlockContentMode.AwaitingContent, out var inlineContentStart, out var inlineContentEnd, out _))
                {
                    segments.Add(new HtmlEmbeddedSegment(
                        inlineContentStart,
                        inlineContentEnd,
                        profile,
                        EmbeddedSyntaxState.Empty));
                }

                cursor = inlineCloseMatch.Index + inlineCloseMatch.Length;
                continue;
            }

            active = new ActiveHtmlBlock(tagName, profile, EmbeddedSyntaxState.Empty, EmbeddedBlockContentMode.AwaitingContent);
            if (profile is not null && openEnd < line.Length)
            {
                if (EmbeddedTagContent.TryExtract(line, openEnd, line.Length, active.ContentMode, out var contentStart, out var contentEnd, out var nextContentMode))
                {
                    var segmentText = line[contentStart..contentEnd];
                    segments.Add(new HtmlEmbeddedSegment(
                        contentStart,
                        contentEnd,
                        profile,
                        EmbeddedSyntaxState.Empty));
                    active = active with
                    {
                        State = profile.Advance(segmentText, EmbeddedSyntaxState.Empty),
                        ContentMode = nextContentMode
                    };
                }
                else
                {
                    active = active with { ContentMode = nextContentMode };
                }
            }
            break;
        }

        return new HtmlLineState(segments);
    }

    private EmbeddedSyntaxProfile? ResolveEmbeddedProfile(string tagName, string? typeAttribute)
    {
        if (_languageResolver is null)
            return null;

        var cacheKey = $"{tagName}|{typeAttribute ?? string.Empty}";
        if (_profileCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var syntaxProfile = _languageResolver(tagName, typeAttribute);
        if (syntaxProfile is null || SupportsEmbeddedLanguageTags(syntaxProfile.Extension))
        {
            _profileCache[cacheKey] = null;
            return null;
        }

        var profile = EmbeddedSyntaxProfile.Create(syntaxProfile);
        _profileCache[cacheKey] = profile;
        return profile;
    }

    private void ColorizeEmbeddedSegment(string text, int lineOffset, EmbeddedSyntaxProfile profile, EmbeddedSyntaxState state)
    {
        if (string.IsNullOrEmpty(text))
            return;

        profile.Colorize(
            text,
            lineOffset,
            state,
            (start, end, brush) => ApplyBrush(0, start, end, brush),
            GetRainbowBrush);
    }

    private static bool SupportsEmbeddedLanguageTags(LoadedExtension extension) =>
        extension.Extensions.Any(ext =>
            string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".axaml", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractTypeAttribute(string attrs)
    {
        var match = TypeAttributeRegex.Match(attrs);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static Regex BuildCloseTagRegex(string tagName) =>
        new($@"</\s*{Regex.Escape(tagName)}\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IBrush GetRainbowBrush(int depth) => RainbowBrushes[Math.Abs(depth) % RainbowBrushes.Length];

    private void ApplyBrush(int lineOffset, int startOffset, int endOffset, IBrush brush)
    {
        if (endOffset <= startOffset)
            return;

        ChangeLinePart(lineOffset + startOffset, lineOffset + endOffset, element =>
        {
            var properties = element.TextRunProperties.Clone();
            properties.SetForegroundBrush(brush);
            SetTextRunPropertiesMethod?.Invoke(element, [properties]);
        });
    }

    private sealed record HtmlSnapshot(string Text, IReadOnlyList<HtmlLineState> LineStates)
    {
        public HtmlLineState GetLineState(int lineNumber) =>
            lineNumber > 0 && lineNumber <= LineStates.Count
                ? LineStates[lineNumber - 1]
                : new HtmlLineState([]);
    }

    private sealed record HtmlLineState(IReadOnlyList<HtmlEmbeddedSegment> Segments);
    private sealed record HtmlEmbeddedSegment(int Start, int End, EmbeddedSyntaxProfile? Profile, EmbeddedSyntaxState State);
    private sealed record ActiveHtmlBlock(string TagName, EmbeddedSyntaxProfile? Profile, EmbeddedSyntaxState State, EmbeddedBlockContentMode ContentMode);
}

// Builds an AvaloniaEdit IHighlightingDefinition from a LoadedExtension's rules.
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

    private static HighlightingRuleSet BuildRuleSet(LoadedExtension ext, CompiledSyntaxProfile syntaxProfile)
    {
        var commentColor     = ColorFor(ext, "comment",      "#6A9955");
        var stringColor      = ColorFor(ext, "string",       "#CE9178");
        var charLiteralColor = ColorFor(ext, "charLiteral",  "#CE9178");
        var keywordColor     = ColorFor(ext, "keyword",      "#569CD6");
        var typeColor        = ColorFor(ext, "type",         "#4EC9B0");
        var numberColor      = ColorFor(ext, "number",       "#B5CEA8");
        var functionColor    = ColorFor(ext, "function",     "#DCDCAA");
        var namespaceColor   = ColorFor(ext, "namespace",    "#4FC1FF");
        var propertyColor    = ColorFor(ext, "property",     "#9CDCFE");
        var attributeColor   = ColorFor(ext, "attribute",    "#C586C0");
        var operatorColor    = ColorFor(ext, "operator",     "#D4D4D4");
        var punctuationColor = ColorFor(ext, "punctuation",  "#D4D4D4");
        var preprocessorColor = ColorFor(ext, "preprocessor", "#C586C0");
        var variableColor    = ColorFor(ext, "variable",      "#A0DBFD");
        var supportsCommonStringPrefixes =
            ext.StringDelimiters.Contains("\"") ||
            ext.StringDelimiters.Contains("'") ||
            ext.MultiLineStringDelimiters.Contains("\"\"\"") ||
            ext.MultiLineStringDelimiters.Contains("'''");

        var isMarkdown = KodoExtensionIds.IsMarkdown(ext.Id);

        // Inner rulesets: codeRuleSet holds keyword/type/number rules (kept for clarity, unused directly);
        // emptyRuleSet is empty so keyword/number rules can't fire inside comment/string spans.
        var codeRuleSet  = new HighlightingRuleSet();
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
            // Markdown-specific inline rules
            // Bold/bold-italic markers: ** *** __ ___
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"\*{2,3}|_{2,3}", RegexOptions.Compiled),
                Color = operatorColor
            });
            // Italic markers: single * or _ not adjacent to another
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"(?<!\*)\*(?!\*)|(?<!_)_(?!_)", RegexOptions.Compiled),
                Color = operatorColor
            });
            // Strikethrough markers: ~~
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"~~", RegexOptions.Compiled),
                Color = operatorColor
            });
            // Link/image opening bracket only - closing ] ) are left as default text colour
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"!?\[", RegexOptions.Compiled),
                Color = punctuationColor
            });
            // Table pipe separators
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"\|", RegexOptions.Compiled),
                Color = punctuationColor
            });
            // Unordered list markers at start of line: - + *
            codeRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new Regex(@"(?<=^|\n)[ \t]*[-+*](?=[ \t])", RegexOptions.Compiled),
                Color = operatorColor
            });
        }

        // Char-literal rule for disableSingleQuoteStrings is handled above; this guard is a no-op for Markdown.

        // Main ruleset: spans checked in order, first match wins.
        var mainRuleSet = new HighlightingRuleSet();

        // Block comment /* … */ - added first so it takes priority over // on the same line.
        if (!string.IsNullOrEmpty(ext.CommentBlockStart) && !string.IsNullOrEmpty(ext.CommentBlockEnd))
        {
            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression        = new Regex(Regex.Escape(ext.CommentBlockStart), RegexOptions.Compiled),
                EndExpression          = new Regex(Regex.Escape(ext.CommentBlockEnd),   RegexOptions.Compiled),
                SpanColor              = commentColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd   = true,
                RuleSet                = emptyRuleSet
            });
        }

        // Added before the generic comment-line span so #-headings get keywordColor, not commentColor.
        if (isMarkdown)
        {
            // Fenced code blocks are NOT handled here - MarkdownColorizer (BuildSnapshot/TryParseFenceOpening/Closing)
            // owns fence state with correct indent (0-3 spaces), marker char, and length >= opening checks.
            // The old loose Regex @"^(?:`{3,}|~{3,})" allowed ~~~ to close ``` and missed indented fences,
            // causing Highlighting vs Colorizer divergence that persists to EOF in long files.

            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression        = new Regex(@"^#{1,6}(?=\s)", RegexOptions.Compiled | RegexOptions.Multiline),
                EndExpression          = new Regex(@"$", RegexOptions.Compiled),
                SpanColor              = keywordColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd   = false,
                RuleSet                = emptyRuleSet
            });
        }

        // Single-line comment to end-of-line; $ anchors so the whole remainder is colored.
        if (!isMarkdown && !string.IsNullOrEmpty(ext.CommentLine))
        {
            mainRuleSet.Spans.Add(new HighlightingSpan
            {
                StartExpression        = new Regex(Regex.Escape(ext.CommentLine), RegexOptions.Compiled),
                EndExpression          = new Regex("$", RegexOptions.Compiled),
                SpanColor              = commentColor,
                SpanColorIncludesStart = true,
                SpanColorIncludesEnd   = false,
                RuleSet                = emptyRuleSet
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
            // Interpolated verbatim strings escape only via doubled quotes, never backslash.
            mainRuleSet.Spans.Add(CreateRegexStringSpan(@"(?:\$@|@\$)""", @"""(?!"")", stringColor, emptyRuleSet, allowEndOfLineFallback: false, isVerbatim: true));
            mainRuleSet.Spans.Add(CreateRegexStringSpan(@"\$""", @"""", stringColor, emptyRuleSet, allowEndOfLineFallback: true));

            // Bare verbatim strings use doubled quotes for escaping and can span multiple lines.
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

        // Markdown inline code (`...`) is owned by MarkdownColorizer (InlineCodeRegex + language detection).
        // A generic string span for "`" with allowEndOfLineFallback:true would paint an unclosed ` to EOL
        // and clash with that logic, causing flicker in long files.
        if (isMarkdown)
            stringDelimiters.RemoveAll(d => d == "`");

        foreach (var delimiter in stringDelimiters)
            mainRuleSet.Spans.Add(CreateStringSpan(delimiter, stringColor, emptyRuleSet, allowEndOfLineFallback: true));

        // Copies keyword/type/number/char rules into mainRuleSet.
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
            $"(?<![.\\p{{L}}\\p{{Nd}}_]){reservedPrefix}{VariableIdentifierBodyPattern}(?!\\s*[\\.(\"'`]|[\\p{{L}}\\p{{Nd}}_])",
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
        // A closing delimiter ends a string only after an even number of backslashes.
        // Mirrors EmbeddedSyntaxProfile's escape logic; verbatim strings don't use it.
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
    // Named ruleset lookups are unused - this definition only uses anonymous inline rulesets.
    public HighlightingRuleSet GetNamedRuleSet(string name) => new HighlightingRuleSet();
}