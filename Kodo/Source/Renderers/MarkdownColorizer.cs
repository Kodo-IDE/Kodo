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
    private static readonly IBrush[] MarkdownBulletBrushes =
    [
        Brush.Parse("#4FC1FF"),
        Brush.Parse("#C586C0"),
        Brush.Parse("#FFD700"),
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
    private LangRulesAdapter? _langRules;
    private Func<string, CompiledSyntaxProfile?>? _languageResolver;
    private Func<string, LoadedExtension?>? _inlineLanguageResolver;
    private readonly Dictionary<string, EmbeddedSyntaxProfile> _embeddedProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmbeddedSyntaxProfile?> _inlineProfileCache = new(StringComparer.Ordinal);
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

    public void InvalidateCache()
    {
        _snapshot = null;
        DocumentTextCache.Invalidate();
    }

    public void UpdateSyntax(LoadedExtension? extension, Func<string, CompiledSyntaxProfile?>? languageResolver, Func<string, LoadedExtension?>? inlineLanguageResolver)
    {
        _snapshot = null;
        _embeddedProfileCache.Clear();
        _inlineProfileCache.Clear();
        _htmlEmbeddedProfileCache.Clear();
        _languageResolver = languageResolver;
        _inlineLanguageResolver = inlineLanguageResolver;

        if (extension is null || !IsMarkdownExtension(extension))
        {
            IsEnabled = false;
            return;
        }

        IsEnabled = true;
        _langRules = extension.LangRules;
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
        }

        var text = document.GetText(line.Offset, line.Length);
        var state = (_snapshot ??= BuildSnapshot(DocumentTextCache.Get(document))).GetLineState(line.LineNumber);

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

    private EmbeddedSyntaxProfile? ResolveInlineEmbeddedProfile(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || _inlineLanguageResolver is null)
            return null;

        if (_inlineProfileCache.TryGetValue(content, out var memoised))
            return memoised;

        var extension = _inlineLanguageResolver(content);
        EmbeddedSyntaxProfile? profile;
        if (extension is null || IsMarkdownExtension(extension))
        {
            profile = null;
        }
        else
        {
            var cacheKey = $"{extension.Id}|{extension.Version}";
            if (!_embeddedProfileCache.TryGetValue(cacheKey, out profile))
            {
                profile = EmbeddedSyntaxProfile.Create(CompiledSyntaxProfile.Create(extension));
                _embeddedProfileCache[cacheKey] = profile;
            }
        }

        if (_inlineProfileCache.Count >= 512)
            _inlineProfileCache.Clear();
        _inlineProfileCache[content] = profile;
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

    private static Regex BuildHtmlCloseTagRegex(string tagName) => EmbeddedTagContent.GetCloseTagRegex(tagName);

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
