// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Input;

namespace Kodo.Models;


internal sealed class AppSettings
{
    public const double DefaultTerminalPanelHeight = 300;
    public const double DefaultExplorerPanelWidth = 260;

    public string ThemeName { get; set; } = "Dark";
    public bool AutoSaveEnabled { get; set; }
    public bool DiscordRichPresenceEnabled { get; set; }
    public bool DiscordImprovedRpcEnabled { get; set; }
    public bool DeveloperOptionsVisible { get; set; }
    public bool VerboseLoggingEnabled { get; set; }
    public bool StatusBarFilePathVisible { get; set; } = true;
    public bool WordWrapEnabled { get; set; }
    public bool InsightEnabled { get; set; } = true;
    public bool InsightCodeSuggestionsEnabled { get; set; } = true;
    public bool InsightDeadCodeEnabled { get; set; } = true;
    public bool InsightErrorDetectionEnabled { get; set; } = true;
    // Comma-separated file extensions (e.g. ".txt,.md") where Insight is disabled.
    public string InsightBlacklistExtensions { get; set; } = ".txt,.md";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CodePredictEnabled
    {
        get => InsightEnabled;
        set => InsightEnabled = value;
    }
    public int TabSize { get; set; } = 4;
    public int EditorFontSize { get; set; } = 14;
    public bool ConfirmBeforeClosingUnsavedTabsEnabled { get; set; } = true;
    public bool RestoreOpenTabsOnLaunchEnabled { get; set; }
    public bool AutoUpdateExtensionsEnabled { get; set; }
    public bool AutoUpdateExtensionsInBackgroundEnabled { get; set; }
    public bool AutoUpdateAppEnabled { get; set; } = true;
    public bool AutoUpdateAppInBackgroundEnabled { get; set; }
    public string? PreferredTerminalShellId { get; set; }
    public bool PSReadLinePredictionEnabled { get; set; }
    public bool TerminalVisible { get; set; }
    public double TerminalPanelHeight { get; set; } = DefaultTerminalPanelHeight;
    public double ExplorerPanelWidth { get; set; } = DefaultExplorerPanelWidth;
    public List<string> OpenTabPaths { get; set; } = [];
    public string? ActiveTabPath { get; set; }
    public string? LastOpenedFolderPath { get; set; }
    public List<RecentFileEntry> RecentFiles { get; set; } = [];
    public bool HasCompletedTutorial { get; set; }
    public string AccentColorMode { get; set; } = "kodo";
    public string CustomAccentHex { get; set; } = "#8C00FF";
    public string? CachedThemeAccentHex { get; set; }
    public string? CachedThemeWindowBackgroundHex { get; set; }
    public string? UserCountry { get; set; }
    public int UserHemisphere { get; set; }
    public string? UserTimezoneOffset { get; set; }
    public string? UserName { get; set; }
    public string? LastSeenVersion { get; set; }
    public bool AllowDataTracking { get; set; }
    public bool PerformanceModeEnabled { get; set; }
    public bool NewsDisabled { get; set; }
    public bool WhatsNewDisabled { get; set; }
    public bool DebouncedSearchEnabled { get; set; }
    public bool HasRespondedToDataTrackingPrompt { get; set; }
    public bool HasAcceptedPrivacyPolicy { get; set; }

    public Dictionary<string, string> CustomRunCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomBuildCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CompilerOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomBuildScripts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> CustomKeybinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DismissedDiagnostics { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RecentFileEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public DateTime LastOpened { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }
}

public sealed class RecentFileItem : INotifyPropertyChanged
{
    private bool _isPinned;

    public RecentFileItem(string path, bool isFolder, DateTime lastOpened, bool isPinned = false)
    {
        Path       = path;
        IsFolder   = isFolder;
        LastOpened = lastOpened;
        _isPinned  = isPinned;
    }

    public string Path { get; }
    public bool IsFolder { get; }
    public DateTime LastOpened { get; set; }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinTooltipText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinnedBadgeText)));
        }
    }

    public string PinButtonText => IsPinned ? "Unpin" : "Pin";

    public string PinTooltipText => IsPinned ? "Unpin this item" : "Pin this item";

    public string PinnedBadgeText => "Pinned";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName
    {
        get
        {
            if (IsFolder)
                return System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var name = System.IO.Path.GetFileName(Path);
            var dot = name.IndexOf("."[0]);
            return dot > 0 ? name[..dot] : name;
        }
    }

    public string DirectoryPath => IsFolder
        ? System.IO.Path.GetDirectoryName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? string.Empty
        : System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public string FileTypeName
    {
        get
        {
            if (IsFolder) return "Folder";
            var ext = System.IO.Path.GetExtension(Path);
            if (string.IsNullOrEmpty(ext))
            {
                var name = System.IO.Path.GetFileName(Path);
                return string.IsNullOrWhiteSpace(name) ? "File" : $"{name} file";
            }
            return $"{ext.ToLowerInvariant()} file";
        }
    }

    public string LastOpenedText
    {
        get
        {
            var diff = DateTime.Now - LastOpened;
            if (diff.TotalMinutes < 1)  return "Just now";
            if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays    < 30) return $"{(int)diff.TotalDays}d ago";
            return LastOpened.ToString("MMM d");
        }
    }

    public string LastOpenedLongText
    {
        get
        {
            var diff = DateTime.Now - LastOpened;
            if (diff.TotalMinutes < 1)  return "just now";
            if (diff.TotalMinutes < 2)  return "1 minute ago";
            if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes} minutes ago";
            if (diff.TotalHours   < 2)  return "1 hour ago";
            if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours} hours ago";
            if (diff.TotalDays    < 2)  return "yesterday";
            if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays} days ago";
            if (diff.TotalDays    < 14) return "1 week ago";
            if (diff.TotalDays    < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
            if (diff.TotalDays    < 60) return "1 month ago";
            if (diff.TotalDays    < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
            if (diff.TotalDays    < 730) return "1 year ago";
            return $"{(int)(diff.TotalDays / 365)} years ago";
        }
    }
}

internal sealed record KeybindDefinition(string Id, string Description, KeyGesture Default, string Category, bool IsContextLocal = false);

internal enum AppPage
{
    Home,
    Editor,
    Settings,
    Extensions,
    Tutorial,
    WhatsNew
}

internal sealed record TutorialStep(
    string SectionTitle,
    string Title,
    string Body,
    string Shortcut,
    string SpotlightTitle,
    string HighlightOne,
    string HighlightTwo,
    string HighlightThree);
