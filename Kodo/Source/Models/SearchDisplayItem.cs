// Licensed under GPL-v3.0
namespace Kodo.Models;

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
