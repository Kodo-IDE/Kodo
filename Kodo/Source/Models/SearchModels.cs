// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Kodo.Models;

// Modes for the unified search panel opened by Ctrl+F / Ctrl+Shift+F / the status bar.
// Extracted from MainWindow.axaml.cs:9181
internal enum SearchMode
{
    FindInFile,
    FileByName,
    ProjectSearch
}

/// <summary>
/// Minimal .gitignore parser. Collects patterns from .gitignore files and
/// answers whether a given path should be excluded from project search.
/// Extracted from MainWindow.axaml.cs:11812
/// </summary>
internal sealed class SearchIgnoreRules
{
    // Built-in directory names that are always skipped during project search,
    // regardless of .gitignore contents. Was MainWindow.DefaultIgnoreDirectories.
    private static readonly HashSet<string> DefaultIgnoreDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs",
    };

    private readonly List<(string Pattern, string Root, bool Negated)> _rules = new();
    private readonly List<string> _includePatterns = new();
    private readonly List<string> _excludePatterns = new();
    public string IncludeFilterSnapshot { get; set; } = "";
    public string ExcludeFilterSnapshot { get; set; } = "";

    /// <summary>
    /// Creates rules by walking from <paramref name="projectRoot"/> upward
    /// to the filesystem root, loading every .gitignore encountered.
    /// Optional user-defined include/exclude glob patterns are layered on top.
    /// </summary>
    public static SearchIgnoreRules Load(string projectRoot, string? includeFilter = null, string? excludeFilter = null)
    {
        var rules = new SearchIgnoreRules();
        var dir = projectRoot;
        while (!string.IsNullOrEmpty(dir))
        {
            var gitignore = Path.Combine(dir, ".gitignore");
            if (File.Exists(gitignore))
                rules.LoadFile(gitignore, dir);

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        // Parse comma-separated user include/exclude patterns.
        if (!string.IsNullOrWhiteSpace(includeFilter))
        {
            foreach (var pat in includeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                rules._includePatterns.Add(pat);
        }
        if (!string.IsNullOrWhiteSpace(excludeFilter))
        {
            foreach (var pat in excludeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                rules._excludePatterns.Add(pat);
        }

        return rules;
    }

    private void LoadFile(string gitignorePath, string rootDir)
    {
        try
        {
            foreach (var raw in File.ReadLines(gitignorePath))
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;

                var negated = line[0] == '!';
                if (negated) line = line[1..];
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Strip leading slash (anchored to .gitignore directory).
                if (line[0] is '/' or '\\')
                    line = line[1..];
                if (string.IsNullOrWhiteSpace(line)) continue;

                _rules.Add((line, rootDir, negated));
            }
        }
        catch { /* unreadable .gitignore - skip */ }
    }

    /// <summary>
    /// Returns true if the directory should be skipped entirely.
    /// Checks default ignore names, .gitignore directory patterns, and
    /// hidden/system file attributes.
    /// </summary>
    public bool ShouldSkipDirectory(string dirPath)
    {
        var name = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(name)) return false;

        if (DefaultIgnoreDirectories.Contains(name)) return true;
        if (IsHiddenOnDisk(dirPath)) return true;

        foreach (var (pattern, root, negated) in _rules)
        {
            // Directory-only rule (trailing /)
            if (pattern.Length > 0 && pattern[^1] == '/')
            {
                var p = pattern[..^1];
                if (MatchesFileName(p, name))
                    return !negated;
                continue;
            }

            if (MatchesAnyPathComponent(pattern, name))
                return !negated;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the file should be excluded from results.
    /// Checks hidden/system attributes and .gitignore file patterns.
    /// </summary>
    public bool ShouldSkipFile(string filePath)
    {
        if (IsHiddenOnDisk(filePath)) return true;

        var name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var (pattern, root, negated) in _rules)
        {
            if (MatchesFilePath(pattern, root, filePath))
                return !negated;
        }

        // User-defined exclude patterns: skip if file matches any.
        if (_excludePatterns.Count > 0)
        {
            var relPath = Path.GetRelativePath(_excludePatterns.Count > 0 ? Path.GetDirectoryName(filePath)! : "", filePath);
            foreach (var pat in _excludePatterns)
            {
                if (MatchesGlob(pat, name) || MatchesGlob(pat, relPath.Replace('\\', '/')))
                    return true;
            }
        }

        // User-defined include patterns: skip if file matches NONE.
        if (_includePatterns.Count > 0)
        {
            var relPath2 = Path.GetRelativePath(_includePatterns.Count > 0 ? Path.GetDirectoryName(filePath)! : "", filePath);
            var included = false;
            foreach (var pat in _includePatterns)
            {
                if (MatchesGlob(pat, name) || MatchesGlob(pat, relPath2.Replace('\\', '/')))
                {
                    included = true;
                    break;
                }
            }
            if (!included) return true;
        }

        return false;
    }

    private static bool MatchesAnyPathComponent(string pattern, string componentName)
    {
        // Pattern without slash - match against a single path component.
        if (!pattern.Contains('/') && !pattern.Contains('\\'))
            return MatchesFileName(pattern, componentName);

        return false;
    }

    private static bool MatchesFilePath(string pattern, string ruleRoot, string fullPath)
    {
        if (!pattern.Contains('/') && !pattern.Contains('\\'))
        {
            // Filename-only pattern - match against just the filename.
            return MatchesFileName(pattern, Path.GetFileName(fullPath));
        }

        // Path pattern - match against relative path from the .gitignore root.
        try
        {
            var relative = Path.GetRelativePath(ruleRoot, fullPath)
                .Replace('\\', '/');
            return MatchesGlob(pattern, relative);
        }
        catch { return false; }
    }

    private static bool MatchesFileName(string pattern, string name) =>
        MatchesGlob(pattern, name);

    internal static bool MatchesGlob(string pattern, string value)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

        return GlobMatch(pattern.AsSpan(), value.AsSpan());
    }

    private static bool GlobMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        var pi = 0;
        var vi = 0;
        var starPi = -1;
        var starVi = -1;

        while (vi < value.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(value[vi])))
            {
                pi++;
                vi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                starPi = pi++;
                starVi = vi;
            }
            else if (starPi >= 0)
            {
                pi = starPi + 1;
                vi = ++starVi;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    private static bool IsHiddenOnDisk(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Highlights all search matches in the editor when Find-in-file is active.
/// Extracted from MainWindow.axaml.cs:15361
/// </summary>
internal sealed class FindHighlightRenderer : IBackgroundRenderer
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(80, 255, 210, 0));
    private readonly List<(int Offset, int Length)> _matches = new();

    public KnownLayer Layer => KnownLayer.Background;

    public void AddMatch(int offset, int length) => _matches.Add((offset, length));

    public void Clear() => _matches.Clear();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView is null || !textView.VisualLinesValid || _matches.Count == 0)
            return;

        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0)
            return;

        var viewStart = visualLines[0].FirstDocumentLine.Offset;
        var viewEnd = visualLines[^1].LastDocumentLine.EndOffset;

        var geoBuilder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            CornerRadius = 2
        };

        foreach (var (offset, length) in _matches)
        {
            if (offset + length < viewStart || offset > viewEnd)
                continue;
            geoBuilder.AddSegment(textView, new SimpleSegment(offset, length));
        }

        var geometry = geoBuilder.CreateGeometry();
        if (geometry is not null)
            drawingContext.DrawGeometry(HighlightBrush, null, geometry);
    }

    private sealed class SimpleSegment : ISegment
    {
        public SimpleSegment(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        public int Offset { get; }
        public int Length { get; }
        public int EndOffset => Offset + Length;
    }
}

// ---------------------------------------------------------------------------
// Consolidated from previously fragmented files:
// FuzzyMatch.cs, SearchDisplayItem.cs, SearchFileGroup.cs, SearchResultItem.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Fuzzy subsequence matching with scoring. Used for "Find files by name"
/// to rank results by relevance rather than simple substring containment.
/// </summary>
internal static class FuzzyMatch
{
    /// <summary>
    /// Returns a relevance score and the matched character indices.
    /// Returns (-1, empty) if the query does not match as a subsequence.
    /// </summary>
    public static (int Score, IReadOnlyList<int> Indices) Match(string query, string value, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(value))
            return (-1, Array.Empty<int>());

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Fast path: exact substring match - high base score.
        var exactIndex = value.IndexOf(query, comparison);
        if (exactIndex >= 0)
        {
            var exactIndices = new List<int>(query.Length);
            for (var i = 0; i < query.Length; i++)
                exactIndices.Add(exactIndex + i);

            var exactScore = 1000 + (exactIndex == 0 ? 200 : 0);
            if (string.Compare(query, 0, value, exactIndex, query.Length, comparison) == 0
                && query.Length == value.Substring(exactIndex, query.Length).Length)
                exactScore += 100;

            return (exactScore, exactIndices);
        }

        // Fuzzy subsequence match.
        var qi = 0;
        var vi = 0;
        var fuzzyIndices = new List<int>(query.Length);
        var fuzzyScore = 0;
        var lastMatchVi = -1;
        var consecutiveStreak = 0;

        while (qi < query.Length && vi < value.Length)
        {
            if (char.ToUpperInvariant(query[qi]) == char.ToUpperInvariant(value[vi]))
            {
                fuzzyIndices.Add(vi);

                if (lastMatchVi == vi - 1)
                {
                    consecutiveStreak++;
                    fuzzyScore += consecutiveStreak * 15;
                }
                else
                {
                    consecutiveStreak = 0;
                }

                if (vi == 0)
                {
                    fuzzyScore += 150;
                }
                else
                {
                    var prev = value[vi - 1];
                    if (prev is '_' or '-' or '.' or '/' or '\\')
                        fuzzyScore += 100;
                    else if (char.IsUpper(value[vi]) && char.IsLower(prev))
                        fuzzyScore += 80;
                }

                if (query[qi] == value[vi])
                    fuzzyScore += 5;

                lastMatchVi = vi;
                qi++;
            }
            vi++;
        }

        if (qi < query.Length)
            return (-1, Array.Empty<int>());

        var span = fuzzyIndices[^1] - fuzzyIndices[0];
        fuzzyScore -= span * 2;
        fuzzyScore -= (value.Length - query.Length) * 3;

        return (fuzzyScore, fuzzyIndices);
    }
}

/// <summary>
/// View-layer wrapper for the search results ListBox. Each item in the
/// flat display list is either a file-group header or an individual result.
/// This keeps the ListBox's flat ItemsSource while visually grouping
/// results by file for project-wide search.
/// </summary>
public sealed class SearchDisplayItem
{
    /// <summary>True when this item is a collapsible file-group header row.</summary>
    public bool IsGroupHeader { get; init; }

    /// <summary>The file group this header represents (non-null when <see cref="IsGroupHeader"/> is true).</summary>
    public SearchFileGroup? Group { get; init; }

    /// <summary>The individual search result (non-null when <see cref="IsGroupHeader"/> is false).</summary>
    public SearchResultItem? Result { get; init; }
}

/// <summary>
/// Represents a group of search results belonging to the same file.
/// Tracks the expanded/collapsed state for the collapsible header row.
/// </summary>
public sealed class SearchFileGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public int MatchCount { get; init; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public string ChevronGlyph => IsExpanded ? "\u25BC" : "\u25B6";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// A single row in the unified search panel's results list. Used both for
// "find files by name" (LineNumber/PreviewText empty) and project-wide text
// search (LineNumber/PreviewText point at a specific match).
public class SearchResultItem
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string PreviewText { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public bool HasPreview => !string.IsNullOrEmpty(PreviewText);

    /// <summary>Character indices in <see cref="DisplayName"/> that matched the query (for highlighting).</summary>
    public IReadOnlyList<int> MatchedIndices { get; init; } = System.Array.Empty<int>();

    /// <summary>Character indices in <see cref="PreviewText"/> that matched the query (for highlighting).</summary>
    public IReadOnlyList<int> MatchedPreviewIndices { get; init; } = System.Array.Empty<int>();

    /// <summary>Fuzzy match score - higher is better. Used for sorting File-by-name results.</summary>
    public int Score { get; init; }
}
