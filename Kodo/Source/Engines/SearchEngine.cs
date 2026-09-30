// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Kodo.Models;

namespace Kodo;

/// <summary>
/// Pure search primitives: whole-word matching, file enumeration and scored
/// filename lookup. Deliberately free of MainWindow/UI state so it can run on
/// background threads; <see cref="MainWindow"/> owns the orchestration and the
/// view-bound <see cref="SearchResultItem"/> presentation.
/// </summary>
internal static class SearchEngine
{
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
                    if (!visited.Add(real))
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

    internal static List<SearchResultItem> SearchFilesByName(string query, string root, bool matchCase, bool useRegex, List<string> files, CancellationToken token)
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
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
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

        scoredResults.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scoredResults.Select(r => r.Item).ToList();
    }

    internal static (int Offset, int Length) FindNextMatch(string text, string needle, int startIndex, bool forward, StringComparison comparison, bool wholeWord, Regex? regex = null)
    {
        if (regex is not null)
        {
            if (forward)
            {
                var searchFrom = Math.Max(0, startIndex);
                while (searchFrom <= text.Length)
                {
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
            var index = text.LastIndexOf(needle, searchTo, comparison);
            if (index < 0) return (-1, 0);
            if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                return (index, needle.Length);
            searchTo = index - 1;
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

    internal static IEnumerable<(int Offset, int Length)> EnumerateFindMatches(string text, string needle, StringComparison cmp, bool wholeWord, Regex? regex) { var idx = 0; while (idx <= text.Length) { var m = FindNextMatch(text, needle, idx, true, cmp, wholeWord, regex); if (m.Offset < 0) yield break; yield return m; idx = m.Offset + Math.Max(1, m.Length); } }

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
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return System.Text.Encoding.Unicode;
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode;
            if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                return System.Text.Encoding.UTF32;

            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }
}
