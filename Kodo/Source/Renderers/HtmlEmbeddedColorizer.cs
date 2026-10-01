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

    public void InvalidateCache()
    {
        _snapshot = null;
        DocumentTextCache.Invalidate();
    }

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
        var snapshot = _snapshot ??= BuildSnapshot(DocumentTextCache.Get(document));
        var state = snapshot.GetLineState(line.LineNumber);

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

    private HtmlSnapshot BuildSnapshot(string text)
    {
        if (text.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("<style", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("<x:code", StringComparison.OrdinalIgnoreCase) < 0)
        {
            var lineCount = 1;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') lineCount++;
            var emptyStates = new List<HtmlLineState>(lineCount);
            for (var i = 0; i < lineCount; i++)
                emptyStates.Add(new HtmlLineState([]));
            return new HtmlSnapshot(text, emptyStates);
        }

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

    private static Regex BuildCloseTagRegex(string tagName) => EmbeddedTagContent.GetCloseTagRegex(tagName);

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
