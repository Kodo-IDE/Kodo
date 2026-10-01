
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

    private static bool IsBatchExtension(LoadedExtension extension) =>
        extension.Extensions.Any(ext => string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase)) ||
        extension.Id.IndexOf("batch", StringComparison.OrdinalIgnoreCase) >= 0;

    public static CompiledSyntaxProfile Create(LoadedExtension extension)
    {
        var rules = new List<CompiledSyntaxRule>();
        var traits = SyntaxLanguageTraits.From(extension);
        var isBatch = IsBatchExtension(extension);
        if (extension.LangRules is { } generated)
        {
            var colors = generated.GetColors();
            foreach (var pair in generated.GetRegexes())
                rules.Add(new(pair.Value, pair.Key, colors.TryGetValue(pair.Key, out var color) ? color : "#D4D4D4"));
            if (rules.Count > 0)
                return CreateGeneratedProfile(extension, rules);
        }

        if (rules.Count == 0 && traits.IsMarkupLike)
        {
            rules.Add(new(new Regex(@"(?<=</?|<!)[\p{L}_:-][\p{L}\p{Nd}_:-]*", RegexOptions.Compiled), "keyword", "#569CD6"));
            rules.Add(new(new Regex(@"(?<=\s)[\p{L}_:-][\p{L}\p{Nd}_:-]*(?:[:.][\p{L}_:-][\p{L}\p{Nd}_:-]*)*(?=\s*=)", RegexOptions.Compiled), "property", "#9CDCFE"));
            rules.Add(new(new Regex(@"(?<=\s)[\p{L}_-][\p{L}\p{Nd}_-]*(?=:[^<>]*\s*=)", RegexOptions.Compiled), "namespace", "#4FC1FF"));
        }
        else if (rules.Count == 0)
        {
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
        }

        if (extension.StringDelimiters.Contains("\"") ||
            extension.StringDelimiters.Contains("'") ||
            extension.MultiLineStringDelimiters.Contains("\"\"\"") ||
            extension.MultiLineStringDelimiters.Contains("'''"))
        {
            rules.Add(new(new Regex(CommonStringPrefixPattern, RegexOptions.Compiled), "string", "#CE9178"));
        }

        rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_])(?:0[xX][0-9A-Fa-f]+|0[bB][01]+|0[oO][0-7]+|\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?)(?![\p{L}\p{Nd}_])", RegexOptions.Compiled), "number", "#B5CEA8"));

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
            rules.Add(new(new Regex(
                traits.IsCssLike
                    ? @"(?<![\p{L}\p{Nd}_-])@[\p{L}_-][\p{L}\p{Nd}_-]*(?![\p{L}\p{Nd}_-])"
                    : @"(?<![\p{L}\p{Nd}_])[@#][\p{L}_][\p{L}\p{Nd}_-]*",
                RegexOptions.Compiled), "preprocessor", "#C586C0"));
            rules.Add(new(new Regex(@"(?<=\[)[\p{L}_][\p{L}\p{Nd}_:.]*(?=[,\]\(])|(?<=<)[\p{L}_][\p{L}\p{Nd}_:-]*(?=[^>]*>)", RegexOptions.Compiled), "attribute", "#C586C0"));
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_])[\p{L}_][\p{L}\p{Nd}_]*(?=\s*\()", RegexOptions.Compiled), "function", "#DCDCAA"));
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_])[\p{L}_][\p{L}\p{Nd}_]*(?=\.)", RegexOptions.Compiled), "namespace", "#4FC1FF"));
            rules.Add(new(new Regex(@"(?<=\.|->|::)[\p{L}_][\p{L}\p{Nd}_:-]*", RegexOptions.Compiled), "property", "#9CDCFE"));
            rules.Add(new(new Regex(@"=>|->|::|\+\+|--|\+=|-=|\*=|/=|%=|&&|\|\||<<|>>|<=|>=|==|!=|=|\+|-|\*|/|%|!|\?|:|<|>|&|\||\^|~", RegexOptions.Compiled), "operator", "#D4D4D4"));
            rules.Add(new(new Regex(@"[{}\[\]();,.]", RegexOptions.Compiled), "punctuation", "#D4D4D4"));
            rules.Add(new(new Regex(@"(?<=\b(?:using|import|include|require|use|from)\b\s+(?:[\p{L}_][\p{L}\p{Nd}_./\\]*\s*[./\\]\s*)?)[\p{L}_][\p{L}\p{Nd}_]*(?=\s*(?:;|$))", RegexOptions.Compiled), "namespace", "#4FC1FF"));
            if (isBatch)
            {
                rules.Add(new(new Regex(@"%[0-9*]|%~[a-zA-Z\$]*\d*|%[A-Za-z_][A-Za-z0-9_]*%|![A-Za-z_][A-Za-z0-9_]*!|%%[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled | RegexOptions.IgnoreCase), "variable", "#A0DBFD"));
            }
            else
            {
                rules.Add(new(BuildVariableRegex(extension.Keywords.Concat(extension.Types).Concat(extension.Functions).Concat(extension.Properties).Concat(extension.Namespaces)), "variable", "#A0DBFD"));
            }
        }

        if (traits.IsCssLike)
            rules.Add(new(new Regex(@"(?<![\p{L}\p{Nd}_-])#[0-9A-Fa-f]{3}(?:[0-9A-Fa-f]{1}|[0-9A-Fa-f]{3}|[0-9A-Fa-f]{5})?(?![\p{L}\p{Nd}_-])", RegexOptions.Compiled), "number", "#B5CEA8"));

        if (extension.DisableSingleQuoteStrings)
            rules.Add(new(new Regex(@"'(?:\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|x[0-9A-Fa-f]{1,4}|[0-7]{1,3}|[abfnrtv\\""'0])|[^\\'])'", RegexOptions.Compiled), "charLiteral", "#CE9178"));

        var stringRegexes = new List<Regex>();
        foreach (var delimiter in extension.MultiLineStringDelimiters.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            var escaped = Regex.Escape(delimiter);
            stringRegexes.Add(new Regex($@"{escaped}.*?{escaped}", RegexOptions.Compiled | RegexOptions.Singleline));
        }
        foreach (var delimiter in extension.StringDelimiters.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            if (delimiter == "'" && extension.DisableSingleQuoteStrings)
                continue;
            stringRegexes.Add(BuildSingleLineStringRegex(delimiter));
        }

        Regex? singleLineCommentRegex;
        if (string.IsNullOrWhiteSpace(extension.CommentLine))
        {
            singleLineCommentRegex = null;
        }
        else if (isBatch)
        {
            singleLineCommentRegex = new Regex(@"(?:(?i)rem|::).*$", RegexOptions.Compiled | RegexOptions.Multiline);
        }
        else
        {
            singleLineCommentRegex = new Regex(Regex.Escape(extension.CommentLine) + @".*$", RegexOptions.Compiled | RegexOptions.Multiline);
        }

        return new CompiledSyntaxProfile(extension, rules, stringRegexes, singleLineCommentRegex);
    }

    private static CompiledSyntaxProfile CreateGeneratedProfile(LoadedExtension extension, List<CompiledSyntaxRule> rules)
    {
        var stringRegexes = new List<Regex>();
        foreach (var delimiter in extension.MultiLineStringDelimiters.Concat(extension.StringDelimiters)
                     .Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            var escaped = Regex.Escape(delimiter);
            stringRegexes.Add(new Regex($"{escaped}.*?{escaped}", RegexOptions.Compiled | RegexOptions.Singleline));
        }

        Regex? commentRegex = null;
        if (!string.IsNullOrWhiteSpace(extension.CommentLine))
            commentRegex = new Regex(Regex.Escape(extension.CommentLine) + @".*$", RegexOptions.Compiled | RegexOptions.Multiline);

        return new CompiledSyntaxProfile(extension, rules, stringRegexes, commentRegex);
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
        return new Regex($"(?<![.\\p{{L}}\\p{{Nd}}_#@\\$]){reservedPrefix}{VariableIdentifierBodyPattern}(?!\\s*[:\\.(\"'`]|[\\p{{L}}\\p{{Nd}}_])", RegexOptions.Compiled);
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

public static class EmbeddedTagContent
{
    private static readonly Regex ScriptCloseRegex = new(@"</\s*script\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StyleCloseRegex = new(@"</\s*style\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CodeCloseRegex = new(@"</\s*x:code\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static Regex GetCloseTagRegex(string tagName) => tagName.ToLowerInvariant() switch
    {
        "script" => ScriptCloseRegex,
        "style" => StyleCloseRegex,
        "x:code" => CodeCloseRegex,
        _ => new Regex($@"</\s*{Regex.Escape(tagName)}\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

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

    private static bool IsBatchExtension(LoadedExtension ext) =>
        ext.Extensions.Any(e => string.Equals(e, ".bat", StringComparison.OrdinalIgnoreCase) || string.Equals(e, ".cmd", StringComparison.OrdinalIgnoreCase)) ||
        ext.Id.IndexOf("batch", StringComparison.OrdinalIgnoreCase) >= 0;

    private EmbeddedSyntaxState Process(
        string text,
        EmbeddedSyntaxState initialState,
        Action<int, int, IBrush>? applyBrush,
        Func<int, IBrush>? rainbowBrushResolver)
    {
        if (string.IsNullOrEmpty(text))
            return initialState;

        var isBatch = IsBatchExtension(Extension);
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

            if (!string.IsNullOrEmpty(Extension.CommentLine) && (isBatch ? MatchesAtIgnoreCase(text, index, Extension.CommentLine) : MatchesAt(text, index, Extension.CommentLine)))
            {
                mode = EmbeddedSyntaxScanMode.LineComment;
                activeDelimiter = null;
                segmentStart = index;
                index += Extension.CommentLine.Length - 1;
                continue;
            }

            if (isBatch && MatchesAt(text, index, "::"))
            {
                mode = EmbeddedSyntaxScanMode.LineComment;
                activeDelimiter = null;
                segmentStart = index;
                index += 1;
                continue;
            }

            if (isBatch && IsBatchEchoStart(text, index))
            {
                var lineEnd = text.IndexOf('\n', index);
                if (lineEnd < 0) lineEnd = text.Length;
                else if (lineEnd > 0 && text[lineEnd - 1] == '\r') lineEnd--;
                var echoPrefixLen = GetBatchEchoPrefixLength(text, index);
                var contentStart = index + echoPrefixLen;
                if (contentStart < lineEnd)
                    TryReserveRange(protectedRanges, contentStart, lineEnd);
                index = lineEnd - 1;
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

    private static bool MatchesAtIgnoreCase(string text, int index, string token) =>
        index >= 0 &&
        index + token.Length <= text.Length &&
        string.Compare(text, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsBatchEchoStart(string text, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r')
            lineStart--;
        for (var i = lineStart; i < index; i++)
            if (!char.IsWhiteSpace(text[i]))
                return false;
        var remaining = text.Length - index;
        if (remaining < 4) return false;
        var atOffset = 0;
        if (text[index] == '@')
        {
            atOffset = 1;
            if (remaining < 5) return false;
        }
        if (string.Compare(text, index + atOffset, "echo", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var after = index + atOffset + 4;
        if (after < text.Length && text[after] is '.' or ':' or '(')
            after++;
        return true;
    }

    private static int GetBatchEchoPrefixLength(string text, int index)
    {
        var len = 0;
        var atOffset = text[index] == '@' ? 1 : 0;
        len = atOffset + 4;
        var after = index + len;
        if (after < text.Length && text[after] is '.' or ':' or '(')
            len++;
        return len;
    }

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

public static class InlineCodeLanguageDetector
{
    private static readonly HashSet<string> ShellVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "dir", "cp", "mv", "rm", "del", "mkdir", "md", "rmdir", "rd",
        "cat", "type", "echo", "pwd", "touch", "chmod", "chown", "sudo",
        "git", "npm", "npx", "yarn", "pnpm", "pip", "pip3", "dotnet", "python",
        "python3", "node", "curl", "wget", "tar", "zip", "unzip", "grep", "find",
        "ssh", "scp", "docker", "kubectl", "code", "explorer", "open", "start",
        "where", "which", "set", "export", "cls", "clear"
    };

    private static readonly Regex CodePunctuationRegex =
        new(@"[{}();]|=>|::|->|\b(?:&&|\|\|)\b|(?<![<>=!])=(?!=)", RegexOptions.Compiled);

    private static readonly Regex PathSegmentRegex =
        new(@"^[\w.~-]+(?:[\\/][\w.~-]+){1,}$", RegexOptions.Compiled);

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
