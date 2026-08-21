// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Kodo.Models;

public record class LoadedExtension : INotifyPropertyChanged
{
    private bool _isUpdateAvailable;

    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string[] Extensions { get; init; } = [];
    public string[] Keywords { get; set; } = [];
    public string[] Types { get; set; } = [];
    public string[] Functions { get; set; } = [];
    public string[] Properties { get; set; } = [];
    public string[] Namespaces { get; set; } = [];
    public string[] Blacklist { get; set; } = [];
    // Names the dead-code scanner should never flag (unused variable/function), e.g.
    // framework-called hooks or conventionally-unused names for this language.
    public string[] DeadCodeIgnore { get; set; } = [];
    // Extra implicit entry-point function names for this language, added on top of
    // Insight's built-in list (main, WinMain, etc).
    public string[] DeadCodeEntryPoints { get; set; } = [];
    public string CommentLine { get; set; } = "//";
    public string CommentBlockStart { get; set; } = "/*";
    public string CommentBlockEnd { get; set; } = "*/";
    public string[] StringDelimiters { get; set; } = ["\"", "'"];
    public string[] MultiLineStringDelimiters { get; set; } = [];
    // True: use a precise char-literal regex instead of an open string span (disableSingleQuoteStrings in language.json).
    public bool DisableSingleQuoteStrings { get; set; }
    public Dictionary<string, string> ColorTokens { get; set; } = new();
    public List<LanguageSyntaxProfile> SyntaxProfiles { get; } = [];
    public IBrush AccentBrush { get; set; } = Brush.Parse("#8C00FF");
    public IBrush CardBrush { get; set; } = Brush.Parse("#252526");
    public IBrush PrimaryTextBrush { get; set; } = Brush.Parse("#F4F4F4");
    public IBrush SurfaceBorderBrush { get; set; } = Brush.Parse("#2B2B2B");
    public IBrush MutedTextBrush { get; set; } = Brush.Parse("#A0A0A0");
    public string SourcePath { get; set; } = string.Empty;
    public bool IsDirectorySource { get; set; }
    public string? PluginAssemblyFileName { get; set; }
    public string? PluginFolderPath { get; set; }
    public bool HasPlugin => PluginAssemblyFileName is not null && PluginFolderPath is not null;
    public DateTime? InstalledOnUtc { get; set; }
    public ExtensionThemeDefinition? ThemeDefinition { get; set; }
    public string ThemeCardThemeId => ThemeDefinition?.ThemeId ?? string.Empty;
    public string ThemeCardDisplayName => ThemeDefinition?.DisplayName ?? Name;
    public string ThemeCardPreviewBackground => ThemeDefinition?.PreviewBackground ?? "#000000";
    public string ThemeCardPreviewBorder => ThemeDefinition?.PreviewBorder ?? "#4A4A4A";
    public string ThemeCardAccent => ThemeDefinition?.Accent ?? "#8C00FF";
    // True for the 2nd, 3rd, etc. entries split out of a multi-theme array -
    // they appear in ThemeExtensions but are hidden from the Installed list.
    public bool IsThemeSubEntry { get; init; }
    // Raw PNG or SVG bytes read from icon.png / icon.svg on the background scan thread.
    // Decoded into IconImage (PNG) or SvgData (SVG) on the UI thread by ApplyLoadedExtensionsResult.
    public byte[]? IconBytes { get; set; }
    // Optional icon loaded from icon.png / icon.svg inside the .kox / folder
    public Bitmap? IconImage { get; set; }
    // SVG text for icons sourced from the marketplace index or local icon.svg
    public string? SvgData { get; set; }
    // Fallback: first two letters of the name, shown when no icon is present
    public string NameAbbreviation => Name.Length >= 2 ? Name[..2] : Name;
    public bool HasIcon => IconImage is not null || SvgData is not null;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (_isUpdateAvailable == value) return;
            _isUpdateAvailable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdateAvailable)));
        }
    }

    private bool _isActiveTheme;
    public bool IsActiveTheme
    {
        get => _isActiveTheme;
        set
        {
            if (_isActiveTheme == value) return;
            _isActiveTheme = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActiveTheme)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyAllBrushesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryTextBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurfaceBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MutedTextBrush)));
    }

    public void NotifyIconChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SvgData)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
    }
}