// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kodo.Models;

/// A named group of theme cards from one extension; multi-theme groups render as a collapsible section.
public class ThemeExtensionGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string GroupName { get; }
    public IReadOnlyList<LoadedExtension> Themes { get; }

    /// True when this extension packs more than one theme.
    public bool IsMultiTheme => Themes.Count > 1;

    /// Whether the card row is expanded; single-theme groups are always shown.
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
        }
    }

    /// ▶ when collapsed, ▼ when expanded.
    public string ChevronGlyph => _isExpanded ? "▾" : "▸";

    public ThemeExtensionGroup(string groupName, IReadOnlyList<LoadedExtension> themes)
    {
        GroupName = groupName;
        Themes    = themes;
        // Multi-theme groups start collapsed; single-theme groups are always open.
        _isExpanded = !IsMultiTheme;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}