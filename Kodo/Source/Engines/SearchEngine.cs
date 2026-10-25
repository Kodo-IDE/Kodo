// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using AvaloniaEdit.Document;
using Kodo.Models;

namespace Kodo;

internal static class SearchEngine
{
    private const int FindChunkSize = 64 * 1024;

    internal static string GetRelativePathOrName(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            return string.IsNullOrEmpty(rel) ? Path.GetFileName(path) : rel;
        }
        catch
        {
            return path;
        }
    }

    internal static void EnumerateProjectFiles(string root, List<string> files, SearchIgnoreRules ignoreRules, HashSet<string>? visited = null, int depth = 0)
    {
        visited ??= new HashSet<string>(FileSystemPaths.Comparer);
        if (depth > 64) return;
        try
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!visited.Add(normalized))
                return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                if (ignoreRules.ShouldSkipDirectory(dir)) continue;
                try
                {
                    var real = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                    if (visited.Contains(real))
                        continue;
                }
                catch { continue; }
                EnumerateProjectFiles(dir, files, ignoreRules, visited, depth + 1);
            }
            foreach (var file in Directory.GetFiles(root))
            {
                if (ignoreRules.ShouldSkipFile(file)) continue;
                files.Add(file);
            }
        }
        catch
        {
        }
    }

    internal static List<SearchResultItem> SearchFilesByName(string query, string root, bool matchCase, bool useRegex, List<string> files, CancellationToken token, Action<double>? reportProgress = null)
    {
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
            }
            catch
            {
                return new List<SearchResultItem>();
            }
        }

        var scoredResults = new List<(SearchResultItem Item, int Score)>();
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            token.ThrowIfCancellationRequested();
            if (fileIndex % 16 == 0)
                reportProgress?.Invoke((double)fileIndex / Math.Max(1, files.Count));
            var file = files[fileIndex];
            var name = Path.GetFileName(file);

            if (regex is not null)
            {
                Match match;
                try { match = regex.Match(name); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!match.Success) continue;

                var matchIndices = new List<int>();
                foreach (Group g in match.Groups)
                    foreach (Capture c in g.Captures)
                        for (var i = 0; i < c.Length; i++)
                            matchIndices.Add(c.Index + i);

                scoredResults.Add((new SearchResultItem
                {
                    Path = file,
                    DisplayName = name,
                    RelativePath = GetRelativePathOrName(root, file),
                    Icon = FileTreeItem.GetFileIcon(name),
                    MatchedIndices = matchIndices,
                    Score = match.Index == 0 ? 2000 : 1000,
                }, match.Index == 0 ? 2000 : 1000));
            }
            else
            {
                var (score, indices) = FuzzyMatch.Match(query, name, matchCase);
                if (score < 0) continue;

                scoredResults.Add((new SearchResultItem
                {
                    Path = file,
                    DisplayName = name,
                    RelativePath = GetRelativePathOrName(root, file),
                    Icon = FileTreeItem.GetFileIcon(name),
                    MatchedIndices = indices,
                    Score = score,
                }, score));
            }
        }

        reportProgress?.Invoke(1);

        scoredResults.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scoredResults.Select(r => r.Item).ToList();
    }

    internal static (int Offset, int Length) FindNextMatch(string text, string needle, int startIndex, bool forward, StringComparison comparison, bool wholeWord, Regex? regex = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (regex is not null)
        {
            if (forward)
            {
                var searchFrom = Math.Max(0, startIndex);
                while (searchFrom <= text.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var m = regex.Match(text, searchFrom);
                    if (!m.Success) return (-1, 0);
                    if (!wholeWord || IsWholeWordMatch(text, m.Index, m.Length))
                        return (m.Index, m.Length);
                    searchFrom = m.Index + Math.Max(1, m.Length);
                }
                return (-1, 0);
            }
            else
            {
                (int Offset, int Length) last = (-1, 0);
                foreach (Match m in regex.Matches(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (m.Index > startIndex) break;
                    if (wholeWord && !IsWholeWordMatch(text, m.Index, m.Length)) continue;
                    last = (m.Index, m.Length);
                }
                return last;
            }
        }

        if (string.IsNullOrEmpty(needle))
            return (-1, 0);

        if (forward)
        {
            var index = Math.Max(0, startIndex);
            while (index <= text.Length - needle.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index = text.IndexOf(needle, index, comparison);
                if (index < 0) return (-1, 0);
                if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                    return (index, needle.Length);
                index += Math.Max(1, needle.Length);
            }

            return (-1, 0);
        }

        var searchTo = Math.Min(Math.Max(0, startIndex), text.Length - 1);
        while (searchTo >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = text.LastIndexOf(needle, searchTo, comparison);
            if (index < 0) return (-1, 0);
            if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                return (index, needle.Length);
            searchTo = index - 1;
        }

        return (-1, 0);
    }

    internal static (int Offset, int Length) FindNextMatch(ITextSource source, string needle, int startIndex, bool forward, StringComparison comparison, bool wholeWord, Regex? regex = null, CancellationToken cancellationToken = default)
    {
        if (regex is not null)
            return FindNextMatch(source.Text, needle, startIndex, forward, comparison, wholeWord, regex, cancellationToken);
        if (string.IsNullOrEmpty(needle)) return (-1, 0);

        var textLength = source.TextLength;
        if (forward)
        {
            var cursor = Math.Clamp(startIndex, 0, textLength);
            while (cursor <= textLength - needle.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateEnd = Math.Min(textLength - needle.Length + 1, cursor + FindChunkSize);
                var readLength = Math.Min(textLength - cursor, candidateEnd - cursor + needle.Length + 1);
                var chunk = source.GetText(cursor, readLength);
                var searchFrom = 0;
                while (searchFrom <= chunk.Length - needle.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = chunk.IndexOf(needle, searchFrom, comparison);
                    if (index < 0 || index >= candidateEnd - cursor) break;
                    if (!wholeWord || IsWholeWordMatch(source, chunk, cursor, index, needle.Length))
                        return (cursor + index, needle.Length);
                    searchFrom = index + Math.Max(1, needle.Length);
                }
                cursor = candidateEnd;
            }
            return (-1, 0);
        }

        var lastCandidate = Math.Min(Math.Max(0, startIndex), textLength - needle.Length);
        while (lastCandidate >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkStart = Math.Max(0, lastCandidate - FindChunkSize + 1);
            var candidateCount = lastCandidate - chunkStart + 1;
            var readLength = Math.Min(textLength - chunkStart, candidateCount + needle.Length + 1);
            var chunk = source.GetText(chunkStart, readLength);
            var searchTo = Math.Min(lastCandidate - chunkStart, chunk.Length - needle.Length);
            while (searchTo >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = chunk.LastIndexOf(needle, searchTo, comparison);
                if (index < 0) break;
                if (!wholeWord || IsWholeWordMatch(source, chunk, chunkStart, index, needle.Length))
                    return (chunkStart + index, needle.Length);
                searchTo = index - 1;
            }
            lastCandidate = chunkStart - 1;
        }
        return (-1, 0);
    }

    internal static bool IsWholeWordMatch(string text, int index, int length)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        var beforeOk = index == 0 || !IsWordChar(text[index - 1]);
        var afterIndex = index + length;
        var afterOk = afterIndex >= text.Length || !IsWordChar(text[afterIndex]);
        return beforeOk && afterOk;
    }

    private static bool IsWholeWordMatch(ITextSource source, string chunk, int chunkOffset, int index, int length)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        var absoluteStart = chunkOffset + index;
        var beforeIsWord = absoluteStart > 0 &&
            (index > 0 ? IsWordChar(chunk[index - 1]) : IsWordChar(source.GetText(absoluteStart - 1, 1)[0]));
        var localAfter = index + length;
        var absoluteAfter = absoluteStart + length;
        var afterIsWord = absoluteAfter < source.TextLength &&
            (localAfter < chunk.Length ? IsWordChar(chunk[localAfter]) : IsWordChar(source.GetText(absoluteAfter, 1)[0]));
        return !beforeIsWord && !afterIsWord;
    }

    internal static IEnumerable<(int Offset, int Length)> EnumerateFindMatches(string text, string needle, StringComparison cmp, bool wholeWord, Regex? regex, CancellationToken cancellationToken = default)
    {
        var idx = 0;
        while (idx <= text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = FindNextMatch(text, needle, idx, true, cmp, wholeWord, regex, cancellationToken);
            if (match.Offset < 0) yield break;
            yield return match;
            idx = match.Offset + Math.Max(1, match.Length);
        }
    }

    internal static IEnumerable<(int Offset, int Length)> EnumerateFindMatches(ITextSource source, string needle, StringComparison comparison, bool wholeWord, Regex? regex, CancellationToken cancellationToken = default)
    {
        if (regex is not null)
            return EnumerateFindMatches(source.Text, needle, comparison, wholeWord, regex, cancellationToken);
        if (string.IsNullOrEmpty(needle)) return Array.Empty<(int Offset, int Length)>();

        var matches = new List<(int Offset, int Length)>();
        var textLength = source.TextLength;
        var cursor = 0;
        while (cursor <= textLength - needle.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateEnd = Math.Min(textLength - needle.Length + 1, cursor + FindChunkSize);
            var readLength = Math.Min(textLength - cursor, candidateEnd - cursor + needle.Length + 1);
            var chunk = source.GetText(cursor, readLength);
            var searchFrom = 0;
            while (searchFrom <= chunk.Length - needle.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = chunk.IndexOf(needle, searchFrom, comparison);
                if (index < 0 || index >= candidateEnd - cursor) break;
                if (!wholeWord || IsWholeWordMatch(source, chunk, cursor, index, needle.Length))
                    matches.Add((cursor + index, needle.Length));
                searchFrom = index + Math.Max(1, needle.Length);
            }
            cursor = candidateEnd;
        }
        return matches;
    }

    internal static string ReplaceAll(ITextSource source, IReadOnlyList<(int Offset, int Length)> matches, string replacement, CancellationToken cancellationToken = default)
    {
        var removedLength = 0L;
        foreach (var match in matches) removedLength += match.Length;
        var capacity = (int)Math.Clamp((long)source.TextLength + (long)matches.Count * replacement.Length - removedLength, 0, int.MaxValue);
        var builder = new StringBuilder(Math.Min(capacity, 1_048_576));
        var position = 0;
        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Offset > position)
                AppendRange(builder, source, position, match.Offset - position, cancellationToken);
            builder.Append(replacement);
            position = match.Offset + match.Length;
            if (match.Length == 0) position = Math.Min(source.TextLength, position + 1);
        }
        if (position < source.TextLength)
            AppendRange(builder, source, position, source.TextLength - position, cancellationToken);
        return builder.ToString();
    }

    private static void AppendRange(StringBuilder builder, ITextSource source, int offset, int length, CancellationToken cancellationToken)
    {
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(length, FindChunkSize);
            builder.Append(source.GetText(offset, count));
            offset += count;
            length -= count;
        }
    }

    internal static bool LineContainsWholeWord(string line, string needle, StringComparison comparison)
    {
        var idx = 0;
        while (idx <= line.Length - needle.Length)
        {
            idx = line.IndexOf(needle, idx, comparison);
            if (idx < 0) return false;
            if (IsWholeWordMatch(line, idx, needle.Length))
                return true;
            idx += Math.Max(1, needle.Length);
        }
        return false;
    }

    internal static string TrimSearchPreview(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 140 ? trimmed : trimmed[..140];
    }

    internal static System.Text.Encoding DetectFileEncoding(string path)
    {
        try
        {
            Span<byte> bom = stackalloc byte[4];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = fs.Read(bom);

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00)
                return System.Text.Encoding.UTF32;
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return System.Text.Encoding.Unicode;
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode;
            if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                return new System.Text.UTF32Encoding(bigEndian: true, byteOrderMark: true);

            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }
}
