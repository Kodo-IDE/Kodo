// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Input;

namespace Kodo.Models;

/// The full schema Kodo persists to <c>kodosettings.json</c>. Pure data - no I/O, no
/// defaults resolution logic beyond simple property initializers. MainWindow owns
/// reading/writing this to disk (LoadSettings / BuildSettingsSnapshot / PersistSettingsSnapshot);
/// this type only defines the shape.

internal sealed class AppSettings
{
    // Single source of truth for the default terminal panel height.
    public const double DefaultTerminalPanelHeight = 300;
    // Single source of truth for the default file explorer panel width.
    public const double DefaultExplorerPanelWidth = 260;

    public string ThemeName { get; set; } = "Dark";
    public bool AutoSaveEnabled { get; set; }
    public bool DiscordRichPresenceEnabled { get; set; }
    public bool DiscordImprovedRpcEnabled { get; set; }
    public bool DeveloperOptionsVisible { get; set; }
    public bool VerboseLoggingEnabled { get; set; }
    public bool StatusBarFilePathVisible { get; set; } = true;
    public bool WordWrapEnabled { get; set; }
    // Predictive completion (Insight). Defaults to true; on unless the user disables it.
    public bool InsightEnabled { get; set; } = true;
    // Sub-toggles shown in Settings only while InsightEnabled is on. Both default to true so
    // existing behavior is unchanged for users who already had Insight enabled.
    public bool InsightCodeSuggestionsEnabled { get; set; } = true;
    public bool InsightDeadCodeEnabled { get; set; } = true;
    // Basic error detection: unmatched/unclosed brackets, unterminated strings, missing
    // ';'/':' , and extension-driven misspelled-keyword flags. Defaults to true.
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
    // Sub-setting under AutoUpdateExtensionsEnabled - see
    // IsAutoUpdateExtensionsInBackgroundEnabled for what it controls.
    public bool AutoUpdateExtensionsInBackgroundEnabled { get; set; }
    // Defaults to true - most users want Kodo to stay current without thinking about it.
    public bool AutoUpdateAppEnabled { get; set; } = true;
    // Sub-setting: defaults to false so Update Now/Later still shows.
    public bool AutoUpdateAppInBackgroundEnabled { get; set; }
    public string? PreferredTerminalShellId { get; set; }
    // PSReadLine's predictive IntelliSense in the PowerShell terminal. Off by default - it
    // used to cause rendering glitches in the ConPTY terminal; users can opt back in.
    public bool PSReadLinePredictionEnabled { get; set; }
    public bool TerminalVisible { get; set; }
    public double TerminalPanelHeight { get; set; } = DefaultTerminalPanelHeight;
    public double ExplorerPanelWidth { get; set; } = DefaultExplorerPanelWidth;
    public List<string> OpenTabPaths { get; set; } = [];
    public string? ActiveTabPath { get; set; }
    // The folder open at last shutdown, restored alongside OpenTabPaths when
    // RestoreOpenTabsOnLaunchEnabled is on and at least one restored tab lived inside it.
    public string? LastOpenedFolderPath { get; set; }
    public List<RecentFileEntry> RecentFiles { get; set; } = [];
    // False on first launch (settings file didn't exist yet); set to true after the
    // tutorial is dismissed so it never shows again on subsequent launches.
    public bool HasCompletedTutorial { get; set; }
    public string AccentColorMode { get; set; } = "kodo";
    public string CustomAccentHex { get; set; } = "#8C00FF";
    // Last-resolved accent hex for the active extension theme (AccentColorMode == "theme").
    // Lets standalone dialogs (crash dialog, updater) match the theme's accent without
    // loading the extension system - see AccentResolver in Updater.cs.
    public string? CachedThemeAccentHex { get; set; }
    // Last-resolved window-background hex for the active extension theme.
    // Lets standalone dialogs (updater progress) match the theme's background without
    // loading the extension system - see ThemeResolver in Updater.cs.
    public string? CachedThemeWindowBackgroundHex { get; set; }
    // Personalization - optional; empty/0 means "use OS defaults".
    public string? UserCountry { get; set; }
    public int UserHemisphere { get; set; }
    public string? UserTimezoneOffset { get; set; }
    public string? UserName { get; set; }
    public string? LastSeenVersion { get; set; }
    // Anonymous usage-analytics opt-in. False (no tracking) until the user
    // has explicitly responded to the consent prompt at least once.
    public bool AllowDataTracking { get; set; }
    // Performance mode: disables News & Announcements entirely - no local cache reads,
    // no GitHub fetches, no home-screen display, and the refresh button is disabled.
    public bool PerformanceModeEnabled { get; set; }
    public bool NewsDisabled { get; set; }
    public bool WhatsNewDisabled { get; set; }
    // Performance: debounce search filtering so results only refresh after the user
    // stops typing (Extensions/Compilers/Languages/Installed and Settings search).
    public bool DebouncedSearchEnabled { get; set; }
    public bool HasRespondedToDataTrackingPrompt { get; set; }
    // Acknowledgment of the embedded Privacy Policy text - separate from the data-tracking
    // opt-in above. There's no decline path; this only tracks whether the user has scrolled
    // through and accepted the terms at least once.
    public bool HasAcceptedPrivacyPolicy { get; set; }

    // Custom Run/Build commands set from the editor header dropdowns, keyed by compiler id.
    // They override whatever the compiler index provides (useful when a compiler has no
    // command template, or when the user wants to change how a tool is invoked).
    public Dictionary<string, string> CustomRunCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomBuildCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // File extension (lowercased) -> compiler id. An explicit user choice made from the
    // compiler icon button; wins over automatic detection for that file type.
    public Dictionary<string, string> CompilerOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // File extension (lowercased) -> absolute path of a custom build script (.bat,
    // .cmd, .ps1, .sh, .py, ...). When set for an extension, it overrides the normal
    // compiler resolution and appears as "Custom Build Script" in the compiler switcher.
    public Dictionary<string, string> CustomBuildScripts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // User-modified keybinds, keyed by the rebindable command id (see MainWindow's
    // KeybindDefinitions) with values serialized as "Modifiers|Key" (e.g. "Control|OemComma").
    // Only entries that differ from their built-in default are stored here; anything absent
    // falls back to the default gesture for that command.
    public Dictionary<string, string> CustomKeybinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// One entry in AppSettings.RecentFiles - a recently opened file or folder.
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

// A small, fixed set of app-level commands whose gesture can be changed from
// Settings -> Help -> View Shortcuts.
// IsContextLocal marks bindings that only fire while a specific control is focused
// (currently the terminal). Two bindings are considered a conflict only when they share
// the same scope.
// Extracted from MainWindow.axaml.cs:7049
internal sealed record KeybindDefinition(string Id, string Description, KeyGesture Default, string Category, bool IsContextLocal = false);

// Extracted from MainWindow.axaml.cs:9200
// Renamed to AppPage to avoid ambiguity with Avalonia.Controls.Page
internal enum AppPage
{
    Home,
    Editor,
    Settings,
    Extensions,
    Tutorial,
    WhatsNew
}

// Extracted from MainWindow.axaml.cs:14726
internal sealed record TutorialStep(
    string SectionTitle,
    string Title,
    string Body,
    string Shortcut,
    string SpotlightTitle,
    string HighlightOne,
    string HighlightTwo,
    string HighlightThree);
