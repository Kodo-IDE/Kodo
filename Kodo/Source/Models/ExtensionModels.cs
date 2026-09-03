// Licensed under GPL-v3.0
using System;
using Avalonia.Media;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace Kodo.Models;

internal readonly record struct IconResult(Bitmap? Bitmap, string? SvgData)
{
    public bool HasValue => Bitmap is not null || SvgData is not null;
}

internal sealed class VersionNumberSequenceComparer : IComparer<int[]>
{
    public static VersionNumberSequenceComparer Instance { get; } = new();

    public int Compare(int[]? left, int[]? right)
    {
        left ??= [0];
        right ??= [0];

        var maxLength = Math.Max(left.Length, right.Length);
        for (var i = 0; i < maxLength; i++)
        {
            var leftPart = i < left.Length ? left[i] : 0;
            var rightPart = i < right.Length ? right[i] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }
}

public static class InstalledContentFilters
{
    public const string All = "All";
    public const string Languages = "Languages";
    public const string Plugins = "Plugins";
    public const string Themes = "Themes";
    public const string Compilers = "Compilers";
    public const string Extensions = "Extensions";
}

internal sealed record ExtensionScanResult(
    List<LoadedExtension> Extensions,
    List<string> LoadErrors);

public static class ExtensionSortModes
{
    public const string Alphabetical = "A-Z";
    public const string ReverseAlphabetical = "Z-A";
    public const string RecentlyInstalled = "Recently Installed";
    public const string UpdatesAvailable = "Updates Available";
}

public static class ExtensionsTabModes
{
    public const string Installed = "Installed";
    public const string Languages = "Languages";
    public const string Themes = "Themes";
    public const string Plugins = "Plugins";
    public const string Compilers = "Compilers";
}

public static class KodoExtensionIds
{
    public const string Markdown = "markdown-kodo-extension";

    public static bool IsMarkdown(string? extensionId) =>
        string.Equals(extensionId, Markdown, StringComparison.OrdinalIgnoreCase);
}

public sealed class LanguageSyntaxProfile
{
    public string[] Extensions { get; init; } = [];
    public string[] Keywords { get; init; } = [];
    public string[] Types { get; init; } = [];
    public string[] Functions { get; init; } = [];
    public string[] Properties { get; init; } = [];
    public string[] Namespaces { get; init; } = [];
    public string[] Blacklist { get; init; } = [];
    public string[] DeadCodeIgnore { get; init; } = [];
    public string[] DeadCodeEntryPoints { get; init; } = [];
    public string? CommentLine { get; init; }
    public string? CommentBlockStart { get; init; }
    public string? CommentBlockEnd { get; init; }
    public string[]? StringDelimiters { get; init; }
    public string[]? MultiLineStringDelimiters { get; init; }
    public bool? DisableSingleQuoteStrings { get; init; }
    public Dictionary<string, string> ColorTokens { get; init; } = new();
}

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
    public string[] DeadCodeIgnore { get; set; } = [];
    public string[] DeadCodeEntryPoints { get; set; } = [];
    public string CommentLine { get; set; } = "//";
    public string CommentBlockStart { get; set; } = "/*";
    public string CommentBlockEnd { get; set; } = "*/";
    public string[] StringDelimiters { get; set; } = ["\"", "'"];
    public string[] MultiLineStringDelimiters { get; set; } = [];
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
    public bool IsThemeSubEntry { get; init; }
    public byte[]? IconBytes { get; set; }
    public Bitmap? IconImage { get; set; }
    public string? SvgData { get; set; }
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

public class MarketplaceExtension : INotifyPropertyChanged
{
    private bool _isInstalling;
    private string _installButtonText = "Install";
    private bool _isUpdateAvailable;
    private string _installedVersion = string.Empty;
    private DateTime? _installedOnUtc;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;

    private string _version = string.Empty;
    public string Version
    {
        get => _version;
        set { if (_version == value) return; _version = value; OnPropertyChanged(); }
    }

    private string _downloadUrl = string.Empty;
    public string DownloadUrl
    {
        get => _downloadUrl;
        set { if (_downloadUrl == value) return; _downloadUrl = value; OnPropertyChanged(); }
    }

    private string _fileName = string.Empty;
    public string FileName
    {
        get => _fileName;
        set { if (_fileName == value) return; _fileName = value; OnPropertyChanged(); }
    }

    public string[] FileExtensions { get; init; } = [];
    public string[] LanguageExtensionIds { get; init; } = [];
    public string? RunCommandTemplate { get; init; }
    public string? BuildCommandTemplate { get; init; }
    public IReadOnlyDictionary<string, (string? Run, string? Build)>? FileCommands { get; init; }

    private Bitmap? _iconImage;
    public Bitmap? IconImage
    {
        get => _iconImage;
        set
        {
            if (_iconImage == value) return;
            _iconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    private string? _svgData;
    public string? SvgData
    {
        get => _svgData;
        set
        {
            if (_svgData == value) return;
            _svgData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => IconImage is not null || SvgData is not null;
    public string NameAbbreviation => Name.Length >= 2 ? Name[..2] : Name;

    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value) return;
            _isInstalling = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInstallEnabled));
        }
    }

    public bool IsInstalled { get; private set; }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set
        {
            if (_isUpdateAvailable == value) return;
            _isUpdateAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInstalledBadge));
        }
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        private set
        {
            if (_installedVersion == value) return;
            _installedVersion = value;
            OnPropertyChanged();
        }
    }

    public DateTime? InstalledOnUtc
    {
        get => _installedOnUtc;
        private set
        {
            if (_installedOnUtc == value) return;
            _installedOnUtc = value;
            OnPropertyChanged();
        }
    }

    public bool IsInstallEnabled => !IsInstalling && (!IsInstalled || IsUpdateAvailable) && !string.IsNullOrWhiteSpace(DownloadUrl);

    public bool ShowInstalledBadge => IsInstalled && !IsUpdateAvailable;

    public string InstallButtonText
    {
        get => _installButtonText;
        set
        {
            if (_installButtonText == value) return;
            _installButtonText = value;
            OnPropertyChanged();
        }
    }

    public void SetInstalledState(LoadedExtension? installedExtension, bool isUpdateAvailable)
    {
        var isInstalled = installedExtension is not null;
        ApplyInstalledState(isInstalled, installedExtension?.Version ?? string.Empty, installedExtension?.InstalledOnUtc, isUpdateAvailable);
    }

    public void SetCompilerInstalledState(string? installedVersion, DateTime? installedOnUtc, bool isUpdateAvailable) =>
        ApplyInstalledState(installedVersion is not null, installedVersion ?? string.Empty, installedOnUtc, isUpdateAvailable);

    private void ApplyInstalledState(bool isInstalled, string installedVersion, DateTime? installedOnUtc, bool isUpdateAvailable)
    {
        InstalledVersion = installedVersion;
        InstalledOnUtc = installedOnUtc;
        IsUpdateAvailable = isUpdateAvailable;

        if (IsInstalled != isInstalled)
        {
            IsInstalled = isInstalled;
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(ShowInstalledBadge));
        }

        if (!IsInstalling)
        {
            InstallButtonText = isInstalled
                ? (isUpdateAvailable ? "Update" : "Installed")
                : "Install";
        }

        OnPropertyChanged(nameof(IsInstallEnabled));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
