// Licensed under GPL-v3.0
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kodo.Models;

/// <summary>
/// Represents a group of search results belonging to the same file.
/// Tracks the expanded/collapsed state for the collapsible header row.
/// </summary>
public sealed class SearchFileGroup : INotifyPropertyChanged
{
    private bool _isExpanded = true;

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
