// Licensed under GPL-v3.0
namespace Kodo.Models;

using System.Collections.Generic;

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

    /// <summary>Fuzzy match score — higher is better. Used for sorting File-by-name results.</summary>
    public int Score { get; init; }
}
