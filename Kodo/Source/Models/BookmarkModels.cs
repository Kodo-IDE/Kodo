// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Kodo.Models;

public sealed class CodeBookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RelativePath { get; set; } = string.Empty;
    public int Line { get; set; }
    public int? Column { get; set; }
    public string? Label { get; set; }
    public string? Preview { get; set; }
    public string? LineFingerprint { get; set; }
    public string? ContextFingerprint { get; set; }
    public bool Unresolved { get; set; }

    public override string ToString() => $"{RelativePath}:{Line}";
}

public sealed class CodeBookmarkSnapshot
{
    public int Version { get; set; } = 1;
    public List<CodeBookmark> Bookmarks { get; set; } = new();
}

public sealed class CodeBookmarkListItem : INotifyPropertyChanged
{
    private string _editLabel = string.Empty;
    private bool _isEditingLabel;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public bool IsEditingLabel { get => _isEditingLabel; set { if (_isEditingLabel == value) return; _isEditingLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLabelVisible)); } }
    public string EditLabel { get => _editLabel; set { if (_editLabel == value) return; _editLabel = value; OnPropertyChanged(); } }
    public CodeBookmark Bookmark { get; init; } = null!;

    public string FileName =>
        Bookmark.RelativePath.Split('/', '\\').LastOrDefault() is { Length: > 0 } name
            ? name
            : Bookmark.RelativePath;

    public string DisplayPath => Bookmark.RelativePath;

    public string LocationText => $"{Bookmark.RelativePath}:{Bookmark.Line}";

    public string LineNumberText => $"Line {Bookmark.Line}";

    public string Label => Bookmark.Label ?? string.Empty;

    public bool HasLabel => !string.IsNullOrWhiteSpace(Bookmark.Label);

    public bool IsLabelVisible => HasLabel && !IsEditingLabel;

    public string Preview => Bookmark.Preview ?? string.Empty;

    public bool HasPreview => !string.IsNullOrWhiteSpace(Bookmark.Preview);

    public bool IsUnresolved => Bookmark.Unresolved;

    public string StatusText => Bookmark.Unresolved ? "Location needs attention" : string.Empty;
}

public sealed class BookmarkFileGroup
{
    public string RelativePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DisplayPath { get; init; } = string.Empty;
    public string CountText { get; set; } = string.Empty;
    public ObservableCollection<CodeBookmarkListItem> Items { get; } = new();
}
