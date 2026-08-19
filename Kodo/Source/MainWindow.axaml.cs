// Licensed under GPL-v3.0
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using Microsoft.Win32;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Animation;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using DiscordAssetsModel = DiscordRPC.Assets;
using DiscordRpcClient = DiscordRPC.DiscordRpcClient;
using DiscordRichPresenceModel = DiscordRPC.RichPresence;
using Kodo.Models;

namespace Kodo;

// Computes how much room a file-tree row's name TextBlock has to work with, so
// long names truncate less (or not at all) as the user widens the explorer panel
// via ExplorerPanelSplitter, instead of being capped at a fixed pixel width.
internal sealed class ExplorerItemNameMaxWidthConverter : IMultiValueConverter
{
    public static readonly ExplorerItemNameMaxWidthConverter Instance = new();

    private const double MinWidth = 40;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double panelWidth || values[1] is not double indentWidth)
            return MinWidth;

        return Math.Max(MinWidth, panelWidth - indentWidth - MainWindow.FileTreeRowFixedOverhead);
    }
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MaxRecentFiles = 6;
    private const string DefaultDiscordClientId = "1495509170756255744";
    private const string DefaultDiscordLargeImageKey = "kodo_logo";
    private const string DefaultDiscordLargeImageText = "Kodo";
    private const string SettingsFileName = "kodosettings.json";
    // Bounds for the drag-resizable terminal panel. Kept in sync with the
    // RowDefinition's MaxHeight in MainWindow.axaml - if one changes, change both.
    // Default lives on AppSettings (Models/AppSettings.cs) - it's a settings value, not
    // just a UI bound, so the model is the single source of truth for it.
    private const string DiscordClientIdEnvironmentVariable = "KODO_DISCORD_CLIENT_ID";
    private const string AutoSaveSavedMessage = "Saved.";
    private const string AutoSaveSavingMessage = "Saving...";
    private const string AutoSaveFailedMessagePrefix = "Save failed:";
    // App version read from <InformationalVersion> in Kodo.csproj (bump only that tag).
    private static readonly string CurrentAppVersion = KodoDiagnostics.AppVersion;
    public string CopyrightText => $"© {DateTime.Now.Year} Kodo, built by KerbalMissile and SS-YYC. Licensed under the GNU GPL-v3.0.";
    // GitHub Contents API endpoint for the extension index JSON, fetched with the raw+json Accept header for direct file bytes.
    private static readonly string[] MarketplaceIndexUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/ExtensionsIndex.json",
    ];
    // Same repo/format as the extension index, but lists standalone compiler installers instead.
    private static readonly string[] CompilerIndexUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/CompilerIndex.json",
    ];
    private static readonly string[] LatestReleaseApiUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo/releases/latest",
    ];
    private static readonly string[] ReleasesApiUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo/releases",
    ];
    private static readonly string ReleasesPageUrl = "https://github.com/Kodo-IDE/Kodo/releases";
    private static readonly string PrivacyPolicyUrl = "https://github.com/Kodo-IDE/Kodo/blob/main/Policies/PRIVACY%20POLICY.txt";
    private const string DiscordServerUrl = "https://discord.gg/cUQ6C88Z9C";
    private const string WebsiteUrl = "https://kodo-ide.github.io/Kodo-Website/";
    // GitHub Contents API endpoint for ANNOUNCEMENTS.md, same raw+json Accept header as the marketplace index.
    private static readonly string AnnouncementsUrl = "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Announcements/ANNOUNCEMENTS.md";
    private static readonly string NewsCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kodo",
        "news-cache.json");

    private bool _isNewsLoading = true;
    private bool _isNewsError;

    private string? _currentFilePath;
    // Encoding detected (or chosen) for the currently open file. Defaults to UTF-8.
    private System.Text.Encoding _currentFileEncoding = System.Text.Encoding.UTF8;
    private string? _currentFolderPath;
    private DiscordRpcClient? _discordRpcClient;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _autoSaveStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _discordReconnectTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _editorStateRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly DispatcherTimer _wordCountRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(175) };
    private readonly DispatcherTimer _InsightRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _settingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    // Coalesces concurrent background saves into a single writer that always ends on the latest snapshot.
    private readonly object _settingsWriteLock = new();
    private AppSettings? _pendingSettingsSnapshot;
    private bool _isPersistingSettings;
    // Polls the Windows accent registry key so the blob and active accent stay
    // live without requiring the Microsoft.Win32.SystemEvents NuGet package.
    private readonly DispatcherTimer _windowsAccentPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastSeenWindowsAccentHex = string.Empty;
    // Polls Windows' light/dark registry setting so the System Default preview swatch and active palette track it live.
    private readonly DispatcherTimer _windowsThemePollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastSeenWindowsThemeName = string.Empty;
    private readonly RainbowBracketColorizer _rainbowBracketColorizer = new();
    private readonly InterpolatedStringColorizer _interpolatedStringColorizer = new();
    private readonly HtmlEmbeddedColorizer _htmlEmbeddedColorizer = new();
    private readonly MarkdownColorizer _markdownColorizer = new();
    private readonly EmojiTypefaceColorizer _emojiTypefaceColorizer = new();
    // Predictive Insight: engine tracks per-file variables + language candidates,
    // _completionWindow is the currently-open popup (null when nothing is showing).
    private readonly InsightEngine _InsightEngine = new();
    private CompletionWindow? _completionWindow;
    private EditorTab? _activeEditorTab;
    private int _nextUntitledTabNumber = 1;
    private string? _autoSaveStatusMessage;
    private bool _isAutoSaveEnabled;
    private bool _isDirty;
    private bool _isSaving;
    private bool _isDiscordRichPresenceEnabled;
    private bool _isDiscordImprovedRpcEnabled;
    private bool _hasUntitledDocument;
    private bool _isRefreshingExtensions;
    private bool _isUpdatingAllExtensions;
    // Guards the silent background sweep (AutoUpdateExtensionsIfEnabledAsync)
    // so it never overlaps with itself or with the manual "Update All" button.
    private bool _isAutoUpdatingExtensions;
    private bool _isAutoUpdateExtensionsEnabled;
    // Sub-setting of auto-update extensions: silent sweeps skip updating ExtensionsStatusText so nothing visibly changes.
    private bool _isAutoUpdateExtensionsInBackgroundEnabled;
    // Whether whole-app updates (not just extensions) auto-install; kept separate since users may want one without the other.
    private bool _isAutoUpdateAppEnabled = true;
    // Sub-setting: installs a found app update immediately instead of showing the Update Now/Later prompt.
    private bool _isAutoUpdateAppInBackgroundEnabled;
    // Backs the manual Check for Updates button; separate from the silent startup check and the auto-update toggle.
    private bool _isCheckingForUpdatesManually;
    private string _checkForUpdatesStatusText = string.Empty;
    private string _developerOptionsStatusText = string.Empty;
    private bool _isRefreshingLatestRelease;
    private bool _isSettingsPageVisible;
    private bool _isExtensionsPageVisible;
    private bool _isTutorialPageVisible;
    private bool _tutorialOpenedFromSettings;
    private bool _isWhatsNewPageVisible;
    private bool _isWhatsNewExpanded;
    // Whether the opening splash (release notes and/or consent ask) is on screen.
    private bool _isUpdateSplashVisible;
    // Whether this showing of the opening splash includes release notes vs. a consent-only ask.
    private bool _openingSplashShowsReleaseNotes;
    // Version running at last launch; if older than current, release notes show once in the opening splash.
    private string _lastSeenVersion = string.Empty;
    private bool _isHomePageVisible;
    private bool _isFileExplorerVisible;
    private bool _isFileTreeExpanded;
    private bool _isStatusBarFilePathVisible = true;
    private bool _isWordWrapEnabled;
    // Defaults to true - predictive completion is on unless the user turns it off.
    private bool _isInsightEnabled = true;
    private string _insightBlacklistExtensions = ".txt,.md";
    private HashSet<string> _insightBlacklistSet = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };
    private bool _suppressExplorerWidthRefresh;
    private bool _isConfirmBeforeClosingUnsavedTabsEnabled = true;
    private bool _isRestoreOpenTabsOnLaunchEnabled;
    private string _selectedExtensionsTab = ExtensionsTabModes.Installed;
    private bool _suppressDirtyTracking;
    // True during startup so incidental SaveSettings() calls can't overwrite just-loaded settings.
    private bool _suppressSettingsSave;
    private bool _isDeveloperOptionsVisible;
    private bool _isVerboseLoggingEnabled;
    private int _tabSize = 4;
    private int _editorFontSize = 14;
    private string _accentColorMode = "kodo";   // "kodo" | "windows" | "custom"
    private string _customAccentHex = "#8C00FF";
    // The accent colour supplied by the active theme; restored when switching back to "kodo" mode.
    private string _themeAccentHex = "#8C00FF";
    private bool   _hasThemeAccent  = false;
    private string _windowBackgroundHex = "#1E1E1E";
    private bool   _hasWindowBackground = false;
    private string _currentThemeName = "Dark";
    private string _requestedThemeName = "Dark";
    private string _editorStatsText = "0 lines";
    private string _wordCountText = string.Empty;
    private bool _pendingFullStateRefresh = true;
    private string _lastDiscordPresenceDetails = string.Empty;
    private string _lastDiscordPresenceState = string.Empty;
    private (string?, string?, int, string?, bool, bool, bool, bool) _lastDiscordPresenceKey;
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    // True when settings.json did not exist on this launch - used to show the tutorial once.
    private bool _isFirstLaunch;
    private bool _hasCompletedTutorial;
    // Anonymous analytics opt-in; defaults to false until the user answers the consent prompt, changeable later in Settings.
    private bool _isDataTrackingEnabled;
    private bool _hasRespondedToDataTrackingPrompt;
    // Privacy Policy acknowledgment - separate from the data-tracking opt-in above. No decline
    // path, so this only ever flips true; gates the same tutorial/splash flow as the consent card.
    private bool _hasAcceptedPrivacyPolicy;
    private bool _isPrivacyPolicyScrolledToBottom;
    private string? _privacyPolicyText;
    private string _extensionsStatusText = "Drop .kox extension files into the Extensions folder to install them.";
    private string _latestReleaseStatusText = "Loading latest release...";
    private string _marketplaceConnectivityMessage = string.Empty;
    private bool _isMarketplaceConnectivityWarningVisible;
    private ReleaseInfo? _latestRelease;
    private LoadedExtension? _currentLanguageExtension;
    private Bitmap? _currentImagePreview;
    private double _imageZoomLevel = 1.0;
    private const double ImageZoomMin = 0.1;
    private const double ImageZoomMax = 10.0;
    private const double ImageZoomStep = 0.25;
    private FileSystemWatcher? _extensionsFolderWatcher;
    private FileSystemWatcher? _projectExtensionsFolderWatcher;

    // Watches the currently open project folder so the explorer tree stays in
    // sync with changes made outside Kodo (git checkout/pull, other editors, etc.).
    private FileSystemWatcher? _projectFolderWatcher;
    // Coalesces bursts of filesystem events (e.g. a git checkout touching hundreds
    // of files) into a single tree rebuild instead of one per event.
    private readonly DispatcherTimer _fileTreeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _extensionsRefreshDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // Periodic background check for extension updates; only runs when auto-update extensions is enabled.
    private readonly DispatcherTimer _extensionAutoUpdateTimer = new() { Interval = TimeSpan.FromHours(6) };
    // Periodic check for new Kodo releases, handled by AppUpdateScheduler.
    private readonly AppUpdateScheduler _appUpdateScheduler;
    // Refreshes the marketplace listing hourly; always runs regardless of the auto-update-extensions setting.
    private readonly DispatcherTimer _marketplaceRefreshTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly IndentGuideBackgroundRenderer _indentGuideRenderer = new();
    private readonly List<string> _startupOpenTabPaths = [];
    private string? _startupFolderPath;
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _marketplaceIconBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _warningDialogCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconFetchSemaphore = new(4, 4);
    private static readonly TimeSpan ExtensionsRefreshCooldown = TimeSpan.FromSeconds(8);
    // Disk cache for the marketplace index JSON, alongside its ETag so an unchanged index returns a free 304.
    private string MarketplaceIndexCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "marketplace-index.json");
    private string MarketplaceIndexETagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "marketplace-index.json.etag");
    // In-memory ETag kept in sync with every successful 200 response.
    // Null means no cache exists yet; loaded lazily from disk on first fetch.
    private string? _marketplaceIndexETag;
    // Same disk-cache pattern as the marketplace index, for CompilerIndex.json.
    private string CompilerIndexCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "compiler-index.json");
    private string CompilerIndexETagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "compiler-index.json.etag");
    private string? _compilerIndexETag;
    // Parsed CompilerIndex.json entries from the last compiler load - lets a failed install
    // re-resolve a single compiler's download on demand (see RefreshSingleCompilerResolutionAsync).
    private List<CompilerIndexEntry> _compilerIndexEntries = [];
    // Compilers install to a folder of their own (they're standalone toolchains, not Kodo
    // extensions) - this JSON file is the source of truth for "is compiler X installed".
    private string CompilerInstallRegistryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "installed-compilers.json");
    private string CompilersFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "Compilers");
    // Manually-added/auto-detected compilers (not from CompilerIndex.json) - see ManualCompilerRecord.
    private string ManualCompilersRegistryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "manual-compilers.json");
    private Dictionary<string, ManualCompilerRecord> _manualCompilers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _autoDetectDismissedIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasRunCompilerAutoDetect;
    private Dictionary<string, InstalledCompilerRecord> _installedCompilers =
        new(StringComparer.OrdinalIgnoreCase);
    // Debounces duplicate error dialogs (same context/exception/message) within a short burst.
    private static readonly TimeSpan WarningDialogCooldown   = TimeSpan.FromSeconds(3);

    // Timeout ceiling for any GitHub network op; past this we cancel, log, and show the standard error dialog.
    private static readonly TimeSpan GitHubOperationTimeout  = TimeSpan.FromSeconds(7);
    private static readonly HashSet<string> ImagePreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif",
        ".webp", ".ico", ".cur", ".tif", ".tiff"
    };
    private DateTime _lastExtensionsRefreshUtc = DateTime.MinValue;
    private string? _startupActiveTabPath;
    private string? _startupFilePath;
    private string _extensionSearchText = string.Empty;
    private string _settingsSearchText = string.Empty;
    private string _selectedInstalledExtensionSort = ExtensionSortModes.Alphabetical;
    private string _selectedMarketplaceExtensionSort = ExtensionSortModes.Alphabetical;
    // Personalization, persisted in settings.json (empty/0 = auto-detect).
    private string _userCountry = string.Empty;
    private int    _userHemisphere = 0;
    private string _userTimezoneOffset = string.Empty;
    private string _userName = string.Empty;
    private bool _isSearchPanelVisible;
    private SearchMode _searchMode = SearchMode.FindInFile;
    private readonly ObservableCollection<SearchResultItem> _searchResults = new();
    private readonly ObservableCollection<SearchDisplayItem> _searchDisplayItems = new();
    private readonly List<SearchFileGroup> _fileGroups = new();
    private string _searchStatusText = string.Empty;
    private bool _isSearchBusy;
    private CancellationTokenSource? _searchCancellation;
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Caches the enumerated file list (with ignore rules) for the currently
    // open folder so repeated searches don't re-walk the tree.
    private (List<string> Files, SearchIgnoreRules Rules)? _searchFileCache;
    // Find-in-file highlight state: tracks all match offsets for live highlighting.
    private readonly FindHighlightRenderer _findHighlightRenderer = new();
    private List<int> _findMatchOffsets = new();
    private int _currentFindMatchIndex = -1;
    // MRU search history: per-mode lists of recent queries (most recent first).
    private readonly List<string> _findInFileHistory = new();
    private readonly List<string> _fileByNameHistory = new();
    private readonly List<string> _projectSearchHistory = new();
    private int _historyIndex = -1;
    private string _savedFindText = string.Empty;
    // Per-search include/exclude glob filters for Project search mode.
    private string _searchIncludeFilter = string.Empty;
    private string _searchExcludeFilter = string.Empty;
    private bool _isFilterRowVisible;
    private bool _isFileCorrupted;
    private bool _isPointerOverEditorLink;
    private readonly HashSet<EditorTab> _corruptedTabs = new(ReferenceEqualityComparer.Instance);
    private TerminalSession? _activeTerminalSession;
    // Tracks the subscribed SessionExited handler so it can be unsubscribed before a new one attaches on Start().
    private EventHandler<IntPtr>? _activeSessionExitedHandler;
    private TerminalShellOption? _selectedTerminalShell;
    private bool _isTerminalVisible;
    private bool _isTerminalSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    // Off by default - see AppSettings.PSReadLinePredictionEnabled.
    private bool _isPSReadLinePredictionEnabled;
    private double _terminalPanelHeight = AppSettings.DefaultTerminalPanelHeight;
    private bool _isResizingTerminalPanel;
    private double _terminalPanelDragStartPointerY;
    private double _terminalPanelDragStartHeight;

    private double _explorerPanelWidth = AppSettings.DefaultExplorerPanelWidth;
    private bool _isResizingExplorerPanel;
    private double _explorerPanelDragStartPointerX;
    private double _explorerPanelDragStartWidth;

    // Caches compiled KodoHighlightingDefinition per extension; building one compiles several regexes, expensive per tab switch.
    private readonly Dictionary<LoadedExtension, KodoHighlightingDefinition> _highlightingCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LoadedExtension, CompiledSyntaxProfile> _compiledSyntaxProfileCache =
        new(ReferenceEqualityComparer.Instance);
    // Caches content-sniffed language per file path to avoid re-reading extensionless files on every tab switch.
    private readonly Dictionary<string, LoadedExtension?> _contentSniffCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ColorSwatchElementGenerator _colorSwatchGenerator = new();

    private string _findText = string.Empty;
    private string _replaceText = string.Empty;
    private bool _isSearchMatchCaseEnabled;
    private bool _isSearchWholeWordEnabled;
    private bool _isSearchRegexEnabled;
    private int _tutorialStepIndex;

    // File tree clipboard state (cut/copy/paste)
    private string? _clipboardItemPath;
    private bool _clipboardItemIsDirectory;
    private bool _clipboardIsCut;
    private event PropertyChangedEventHandler? ViewModelPropertyChanged;
    private static readonly HttpClient MarketplaceHttpClient = CreateHttpClient();
    private static readonly TutorialStep[] TutorialSteps =
    [
        new(
            "",
            "Welcome to Kodo!",
            "A fast, focused code editor built to stay out of your way. This short tutorial will walk you through the essentials - it only takes a minute.",
            "",
            "",
            "",
            "",
            ""
        ),
        new(
            "Welcome",
            "Meet your workspace",
            "Kodo starts on Home so you can jump straight into a recent project, create a new file, or open an existing folder without hunting through menus.",
            "Ctrl+H",
            "Home is your launchpad",
            "Open recent files and folders in one click.",
            "Start fresh work quickly with New File or Open Folder.",
            "Use the keyboard shortcut chips on Home for quick access to the most common actions."
        ),
        new(
            "Editing",
            "Create and work fast",
            "Use the editor for scratch files or full projects, then lean on autosave, tab restore, and the file explorer when you are moving across a bigger codebase.",
            "Ctrl+N / Ctrl+O / Ctrl+K",
            "Core editing flow",
            "Create a file instantly with Ctrl+N.",
            "Open a file with Ctrl+O or a folder with Ctrl+K.",
            "Keep momentum with tabs, autosave, and explorer tools."
        ),
        new(
            "Marketplace",
            "Install language support and themes",
            "The Marketplace is where Kodo pulls syntax highlighting packs, language definitions, and theme extensions from the web so you can tailor the app to your stack.",
            "Ctrl+E",
            "Extensions, themes, updates",
            "Browse installable extensions from inside the app.",
            "Update installed packages when newer versions appear.",
            "Watch for connectivity warnings if downloads cannot reach the internet."
        ),
        new(
            "Settings",
            "Tune Kodo to your workflow",
            "Adjust themes, font size, tab width, autosave, and other quality-of-life settings so the editor feels right for the way you work.",
            "Ctrl+,",
            "Personalize the experience",
            "Switch between built-in and extension themes.",
            "Set editor font size and tab behavior.",
            "Control autosave and launch preferences."
        ),
        new(
            "Set up",
            "Make Kodo yours",
            "Pick a theme and accent colour, then tell Kodo a little about yourself so the welcome message on Home feels personal. You can change any of these later in Settings.",
            "Ctrl+,  ·  Settings",
            "Why personalise?",
            "Your name makes greetings feel like they're written for you.",
            "Country and hemisphere keep seasonal messages accurate.",
            "Theme and accent colour apply instantly across the whole app."
        )
    ];

    // Auto-completion

    // Maps each opening character to its closing pair
    private static readonly Dictionary<char, char> BracketPairs = new()
    {
        { '(', ')' },
        { '[', ']' },
        { '{', '}' },
        { '<', '>' },
        { '"', '"' },
        { '\'', '\'' },
        { '`', '`' },
    };

    // Closing characters - when typed over an existing auto-inserted closer, skip past it
    private static readonly HashSet<char> ClosingChars = new() { ')', ']', '}', '>', '"', '\'', '`' };
    private static readonly Dictionary<string, string> FenceLanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c#"] = "cs",
        ["csharp"] = "cs",
        ["f#"] = "fs",
        ["fsharp"] = "fs",
        ["js"] = "js",
        ["javascript"] = "js",
        ["ts"] = "ts",
        ["typescript"] = "ts",
        ["py"] = "py",
        ["python"] = "py",
        ["rb"] = "rb",
        ["ruby"] = "rb",
        ["rs"] = "rs",
        ["rust"] = "rs",
        ["ps"] = "ps1",
        ["powershell"] = "ps1",
        ["shell"] = "sh",
        ["bash"] = "sh",
        ["zsh"] = "sh",
        ["yml"] = "yml",
        ["yaml"] = "yml",
        ["jsonc"] = "json",
        ["md"] = "md",
        ["markdown"] = "md",
        // Explicit plain-text markers - map to empty string so no extension is matched
        ["text"] = "",
        ["plain"] = "",
        ["txt"] = "",
        ["plaintext"] = "",
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kodo/2.0.0-DEV (https://github.com/Kodo-IDE/Kodo)");
        return client;
    }

    private string ExtensionsFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kodo", "Extensions");
    private string ProjectExtensionsFolderPath =>
        Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Extensions"));

    // Flat list that backs the ItemsControl – directories insert/remove their children in-place
    public ObservableCollection<FileTreeItem> FileTreeItems { get; } = new();
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();
    public ObservableCollection<EditorTab> OpenTabs { get; } = new();
    public ObservableCollection<TerminalSession> TerminalSessions { get; } = new();
    public ObservableCollection<LoadedExtension> LoadedExtensions { get; } = new();
    public ObservableCollection<MarketplaceExtension> MarketplaceExtensions { get; } = new();
    // Compilers (e.g. Shine) are standalone installer downloads, not Kodo extensions -
    // tracked separately from MarketplaceExtensions and installed via CompilerIndex.json.
    public ObservableCollection<MarketplaceExtension> CompilerExtensions { get; } = new();
    // Compilers the user pointed Kodo at by filepath, or that Kodo found on this machine on its
    // own (see AutoDetectDefaultCompilersAsync) - not part of CompilerIndex.json, so they're kept
    // out of CompilerExtensions to avoid being wiped out whenever that index refreshes. Merged
    // into the Installed tab's compiler list in FilteredInstalledCompilerExtensions.
    public ObservableCollection<MarketplaceExtension> ManualCompilerExtensions { get; } = new();
    public ObservableCollection<string> ExtensionLoadErrors { get; } = new();
    public ObservableCollection<TerminalShellOption> AvailableTerminalShells { get; } = new();
    public ObservableCollection<NewsItem> NewsItems { get; } = new();

    public bool IsNewsLoading
    {
        get => _isNewsLoading;
        private set { if (_isNewsLoading == value) return; _isNewsLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNewsContentVisible)); OnPropertyChanged(nameof(IsNewsEmpty)); }
    }

    public bool IsNewsError
    {
        get => _isNewsError;
        private set { if (_isNewsError == value) return; _isNewsError = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNewsContentVisible)); OnPropertyChanged(nameof(IsNewsEmpty)); }
    }

    public bool IsNewsContentVisible => !IsNewsLoading && !IsNewsError && NewsItems.Count > 0;
    public bool IsNewsEmpty => !IsNewsLoading && !IsNewsError && NewsItems.Count == 0;

    public LoadedExtension? CurrentLanguageExtension
    {
        get => _currentLanguageExtension;
        private set
        {
            if (_currentLanguageExtension == value) return;
            _currentLanguageExtension = value;
            OnPropertyChanged();
        }
    }

    public Bitmap? CurrentImagePreview
    {
        get => _currentImagePreview;
        private set
        {
            if (ReferenceEquals(_currentImagePreview, value))
                return;

            var previousPreview = _currentImagePreview;
            _currentImagePreview = value;
            previousPreview?.Dispose();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImagePreview));
            OnPropertyChanged(nameof(IsImagePreviewVisible));
            OnPropertyChanged(nameof(IsTextEditorVisible));
            OnPropertyChanged(nameof(ImageZoomedWidth));
            OnPropertyChanged(nameof(ImageZoomedHeight));
            OnPropertyChanged(nameof(ImageZoomPercent));
        }
    }

    public double ImageZoomLevel
    {
        get => _imageZoomLevel;
        private set
        {
            var clamped = Math.Clamp(value, ImageZoomMin, ImageZoomMax);
            if (Math.Abs(_imageZoomLevel - clamped) < 0.001) return;
            _imageZoomLevel = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageZoomPercent));
            OnPropertyChanged(nameof(ImageZoomedWidth));
            OnPropertyChanged(nameof(ImageZoomedHeight));
        }
    }

    public string ImageZoomPercent => $"{(int)Math.Round(_imageZoomLevel * 100)}%";

    public double ImageZoomedWidth =>
        CurrentImagePreview is not null ? CurrentImagePreview.PixelSize.Width * _imageZoomLevel : 0;

    public double ImageZoomedHeight =>
        CurrentImagePreview is not null ? CurrentImagePreview.PixelSize.Height * _imageZoomLevel : 0;

    public EditorTab? ActiveEditorTab
    {
        get => _activeEditorTab;
        private set
        {
            if (ReferenceEquals(_activeEditorTab, value))
                return;

            if (_activeEditorTab is not null)
                _activeEditorTab.IsSelected = false;

            _activeEditorTab = value;

            if (_activeEditorTab is not null)
                _activeEditorTab.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOpenEditors));
            OnPropertyChanged(nameof(IsEditorTabsVisible));
            SaveSettings();
        }
    }

    public MainWindow() : this(null) { }

    public MainWindow(string? startupFilePath)
    {
        // Suppresses SaveSettings() for the whole constructor + OnOpened sequence; cleared in OnOpened's finally block.
        _suppressSettingsSave = true;

        var trimmedStartupPath = startupFilePath?.Trim().Trim('"');
        _startupFilePath = !string.IsNullOrWhiteSpace(trimmedStartupPath) && File.Exists(trimmedStartupPath)
            ? trimmedStartupPath
            : null;
        InitializeComponent();
        NotifySettingsSearchChanged();
        LoadWindowIcon();
        EditorTextBox.LineNumbersMargin = new Thickness(8, 0, 8, 0);
        EditorTextBox.TextArea.TextView.Options.AllowScrollBelowDocument = false;
        var dottedLineMargin = DottedLineMargin.Create();
        dottedLineMargin.VerticalAlignment = VerticalAlignment.Top;
        EditorTextBox.TextArea.LeftMargins.Add(dottedLineMargin);
        // DottedLineMargin stretches to fill its container (the full viewport) by default,
        // so on short files the separator runs past the last line into empty scroll space.
        // Clamp it to the smaller of the document's real height or the viewport height.
        EditorTextBox.TextArea.TextView.VisualLinesChanged += (_, _) =>
        {
            var textView = EditorTextBox.TextArea.TextView;
            dottedLineMargin.Height = Math.Min(textView.DocumentHeight, textView.Bounds.Height);
        };
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_indentGuideRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_findHighlightRenderer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_rainbowBracketColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_interpolatedStringColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_htmlEmbeddedColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_markdownColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_emojiTypefaceColorizer);
        EditorTextBox.TextArea.TextView.LinkTextForegroundBrush = Brush.Parse("#5BA3D9");
        EditorTextBox.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;
        // Replaces the default LinkElementGenerator with one that trims trailing punctuation from link spans.
        var defaultLinkGen = EditorTextBox.TextArea.TextView.ElementGenerators.OfType<LinkElementGenerator>().FirstOrDefault();
        if (defaultLinkGen is not null)
            EditorTextBox.TextArea.TextView.ElementGenerators.Remove(defaultLinkGen);
        EditorTextBox.TextArea.TextView.ElementGenerators.Add(new StrictLinkElementGenerator());
        EditorTextBox.TextArea.TextView.ElementGenerators.Add(_colorSwatchGenerator);
        // Shows a Ctrl+click tooltip over URLs, using the same regex as StrictLinkElementGenerator.
        EditorTextBox.TextArea.TextView.PointerMoved += EditorTextView_OnPointerMoved;
        EditorTextBox.TextArea.TextView.PointerExited += EditorTextView_OnPointerExited;
        OpenTabs.CollectionChanged += OpenTabs_CollectionChanged;
        TerminalSessions.CollectionChanged += TerminalSessions_CollectionChanged;
        // TerminalHostControl is shared across sessions (snapshot/restore), so one subscription for the window's lifetime is enough.
        // Not subscribing to TerminalHostControl.TitleChanged: tab titles are derived from the
        // workspace/working directory (see CreateTerminalSession), not the shell's self-reported
        // OSC 0/2 title (which for PowerShell is just its own exe path, e.g. "...\v1.0\powershell.exe").
        TerminalHostControl.WorkingDirectoryChanged += TerminalHostControl_OnWorkingDirectoryChanged;
        FileTreeItems.CollectionChanged += FileTreeItems_CollectionChanged;
        _fileTreeRefreshTimer.Tick += FileTreeRefreshTimer_OnTick;
        _searchDebounceTimer.Tick += SearchDebounceTimer_OnTick;
        // TextEditor uses EventHandler (not RoutedEventHandler), so hook up in code-behind
        EditorTextBox.TextChanged += EditorTextBox_OnTextChanged;
        EditorTextBox.TextArea.Caret.PositionChanged += (_, _) => QueueRefreshState();
		// Auto-completion: insert closing bracket/quote after opener, skip-over when typing a closer
        EditorTextBox.TextArea.TextEntering += EditorTextArea_OnTextEntering;
        EditorTextBox.TextArea.TextEntered  += EditorTextArea_OnTextEntered;
        AddHandler(InputElement.KeyDownEvent, MainWindow_EditorKeyIntercept_OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Registers Ctrl+wheel zoom in the tunnel phase so it fires before ScrollViewer consumes the scroll.
        ImageScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            ImageScrollViewer_OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        _isFirstLaunch = !File.Exists(SettingsFilePath);
        var settings = LoadSettings();
        InitializeKeybinds(settings);
        _requestedThemeName = string.IsNullOrWhiteSpace(settings.ThemeName) ? "Dark" : settings.ThemeName;
        _isAutoSaveEnabled = settings.AutoSaveEnabled;
        _isDiscordRichPresenceEnabled = settings.DiscordRichPresenceEnabled;
        _isDiscordImprovedRpcEnabled  = settings.DiscordImprovedRpcEnabled;
        _isDeveloperOptionsVisible = settings.DeveloperOptionsVisible;
        _isVerboseLoggingEnabled = settings.VerboseLoggingEnabled;
        KodoDiagnostics.VerboseLoggingEnabled = _isVerboseLoggingEnabled;
        _isStatusBarFilePathVisible = settings.StatusBarFilePathVisible;
        _isWordWrapEnabled = settings.WordWrapEnabled;
        _isInsightEnabled = settings.InsightEnabled;
        _insightBlacklistExtensions = string.IsNullOrWhiteSpace(settings.InsightBlacklistExtensions) ? ".txt,.md" : settings.InsightBlacklistExtensions;
        RebuildInsightBlacklist();
        _isConfirmBeforeClosingUnsavedTabsEnabled = settings.ConfirmBeforeClosingUnsavedTabsEnabled;
        _isRestoreOpenTabsOnLaunchEnabled = settings.RestoreOpenTabsOnLaunchEnabled;
        _isAutoUpdateExtensionsEnabled = settings.AutoUpdateExtensionsEnabled;
        _isAutoUpdateExtensionsInBackgroundEnabled = settings.AutoUpdateExtensionsInBackgroundEnabled;
        _isAutoUpdateAppEnabled = settings.AutoUpdateAppEnabled;
        _isAutoUpdateAppInBackgroundEnabled = settings.AutoUpdateAppInBackgroundEnabled;
        _hasCompletedTutorial = settings.HasCompletedTutorial;
        _isDataTrackingEnabled = settings.AllowDataTracking;
        _hasRespondedToDataTrackingPrompt = settings.HasRespondedToDataTrackingPrompt;
        _hasAcceptedPrivacyPolicy = settings.HasAcceptedPrivacyPolicy;
        // Syncs the Aptabase client with the loaded consent choice (it defaults to disabled until now).
        AptabaseClient.SetEnabled(_isDataTrackingEnabled);
        _accentColorMode = settings.AccentColorMode is "kodo" or "windows" or "custom" or "theme"
            ? settings.AccentColorMode : "kodo";
        // Migration: upgrade legacy "kodo" mode with an active theme to "theme" mode so the accent preference isn't lost.
        _customAccentHex = string.IsNullOrWhiteSpace(settings.CustomAccentHex)
            ? "#8C00FF" : settings.CustomAccentHex;
        _tabSize = NormalizeTabSize(settings.TabSize);
        _editorFontSize = settings.EditorFontSize is >= 8 and <= 32 ? settings.EditorFontSize : 14;
        _terminalPanelHeight = TerminalShellSupport.NormalizeTerminalPanelHeight(settings.TerminalPanelHeight);
        _explorerPanelWidth = NormalizeExplorerPanelWidth(settings.ExplorerPanelWidth);
        _userCountry = string.IsNullOrWhiteSpace(settings.UserCountry)
            ? DetectCountryCode()
            : settings.UserCountry.ToUpperInvariant();
        _userHemisphere     = settings.UserHemisphere is >= 0 and <= 2 ? settings.UserHemisphere : 0;
        _userTimezoneOffset = settings.UserTimezoneOffset ?? string.Empty;
        _userName           = settings.UserName ?? string.Empty;
        _lastSeenVersion    = settings.LastSeenVersion ?? string.Empty;
        _isTerminalVisible = false; // always start hidden; user opens it manually
        _startupOpenTabPaths.AddRange(settings.OpenTabPaths
            .Where(path => File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        _startupActiveTabPath = settings.ActiveTabPath;
        _startupFolderPath = !string.IsNullOrWhiteSpace(settings.LastOpenedFolderPath) &&
                              Directory.Exists(settings.LastOpenedFolderPath)
            ? settings.LastOpenedFolderPath
            : null;
        LoadRecentFiles(settings.RecentFiles);
        _isPSReadLinePredictionEnabled = settings.PSReadLinePredictionEnabled;
        foreach (var pair in settings.CustomRunCommands)
            _customRunCommands[pair.Key] = pair.Value;
        foreach (var pair in settings.CustomBuildCommands)
            _customBuildCommands[pair.Key] = pair.Value;
        foreach (var pair in settings.CompilerOverrides)
            _compilerOverrides[pair.Key] = pair.Value;
        RefreshAvailableTerminalShells(settings.PreferredTerminalShellId);
        _autoSaveTimer.Tick += AutoSaveTimer_OnTick;
        _autoSaveStatusTimer.Tick += AutoSaveStatusTimer_OnTick;
        _discordReconnectTimer.Tick += DiscordReconnectTimer_OnTick;
        _editorStateRefreshTimer.Tick += EditorStateRefreshTimer_OnTick;
        _wordCountRefreshTimer.Tick += WordCountRefreshTimer_OnTick;
        _InsightRefreshTimer.Tick += InsightRefreshTimer_OnTick;
        _settingsSaveDebounceTimer.Tick += SettingsSaveDebounceTimer_OnTick;
        _extensionsRefreshDebounceTimer.Tick += ExtensionsRefreshDebounceTimer_OnTick;
        _extensionAutoUpdateTimer.Tick += ExtensionAutoUpdateTimer_OnTick;
        _appUpdateScheduler = new AppUpdateScheduler(
            isEnabled: () => IsAutoUpdateAppEnabled,
            isManualCheckInProgress: () => IsCheckingForUpdatesManually,
            installInBackground: () => IsAutoUpdateAppInBackgroundEnabled);
        _marketplaceRefreshTimer.Tick += MarketplaceRefreshTimer_OnTick;

        // Extensions load before ApplyTheme, both before DataContext = this, avoiding a startup flash.
        EnsureExtensionsFolder();
        SetupExtensionFolderWatchers();
        LoadExtensions();
        ApplyThemeBrushes(_requestedThemeName);

        // Migration: legacy "kodo" mode with an active theme is upgraded to "theme" mode.
        if (_accentColorMode == "kodo" && _hasThemeAccent)
            _accentColorMode = "theme";

        DataContext = this;
        IsHomePageVisible = true;

        // Kicks off the async marketplace refresh; LoadExtensions() already populated the theme synchronously.
        UpdateDiscordRichPresenceLifecycle();
        UpdateExtensionAutoUpdateLifecycle();
        _appUpdateScheduler.UpdateLifecycle();
        _marketplaceRefreshTimer.Start();
        _ = RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
        ApplyEditorSettings();
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkChange_OnNetworkAddressChanged;
        RefreshMarketplaceConnectivityState();
        // Poll the Windows accent registry every 2 s so the blob preview and
        // active accent stay live without the Microsoft.Win32.SystemEvents package.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _lastSeenWindowsAccentHex = GetWindowsAccentColor() ?? string.Empty;
            _windowsAccentPollTimer.Tick += WindowsAccentPollTimer_OnTick;
            _windowsAccentPollTimer.Start();

            // Same approach for the System Default theme blob - poll the
            // light/dark registry value every 2 s so it stays live too.
            _lastSeenWindowsThemeName = ResolveSystemThemeName();
            _windowsThemePollTimer.Tick += WindowsThemePollTimer_OnTick;
            _windowsThemePollTimer.Start();
        }
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        RefreshState(fullRefresh: true);
    }

    // Extension loading

    private void EnsureExtensionsFolder()
    {
        if (!Directory.Exists(ExtensionsFolderPath))
            Directory.CreateDirectory(ExtensionsFolderPath);
    }

    private void SetupExtensionFolderWatchers()
    {
        DisposeExtensionFolderWatchers();
        _extensionsFolderWatcher = CreateExtensionFolderWatcher(ExtensionsFolderPath);

        if (Directory.Exists(ProjectExtensionsFolderPath) &&
            !string.Equals(ProjectExtensionsFolderPath, ExtensionsFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            _projectExtensionsFolderWatcher = CreateExtensionFolderWatcher(ProjectExtensionsFolderPath);
        }
    }

    private FileSystemWatcher CreateExtensionFolderWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
        };

        watcher.Created += ExtensionFolderWatcher_OnChanged;
        watcher.Deleted += ExtensionFolderWatcher_OnChanged;
        watcher.Renamed += ExtensionFolderWatcher_OnRenamed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void DisposeExtensionFolderWatchers()
    {
        DisposeExtensionFolderWatcher(_extensionsFolderWatcher);
        DisposeExtensionFolderWatcher(_projectExtensionsFolderWatcher);
        _extensionsFolderWatcher = null;
        _projectExtensionsFolderWatcher = null;
    }

    private void DisposeExtensionFolderWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
            return;

        watcher.EnableRaisingEvents = false;
        watcher.Created -= ExtensionFolderWatcher_OnChanged;
        watcher.Deleted -= ExtensionFolderWatcher_OnChanged;
        watcher.Renamed -= ExtensionFolderWatcher_OnRenamed;
        watcher.Dispose();
    }

    private static bool IsExtensionFilePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".kox", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(extension);
    }

    private void ExtensionFolderWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsExtensionFilePath(e.FullPath))
            return;

        QueueExtensionsRefresh();
    }

    private void ExtensionFolderWatcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsExtensionFilePath(e.OldFullPath) && !IsExtensionFilePath(e.FullPath))
            return;

        QueueExtensionsRefresh();
    }

    private void QueueExtensionsRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _extensionsRefreshDebounceTimer.Stop();
            _extensionsRefreshDebounceTimer.Start();
        });
    }

    private async void ExtensionsRefreshDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _extensionsRefreshDebounceTimer.Stop();
        await RefreshExtensionsDataAsync();
    }

    private async Task RefreshExtensionsDataAsync(bool force = false, bool suppressWatchdog = false)
    {
        if (_isRefreshingExtensions)
            return;

        if (!force && DateTime.UtcNow - _lastExtensionsRefreshUtc < ExtensionsRefreshCooldown)
            return;

        IsRefreshingExtensions = true;
        ExtensionsStatusText = "Refreshing extensions...";

        // Refresh watchdog: warns if the full refresh doesn't finish within GitHubOperationTimeout.
        // suppressWatchdog=true when called from a step that already owns its own timeout handling.
        using var watchdogCts = new CancellationTokenSource();
        var watchdogToken = watchdogCts.Token;
        if (!suppressWatchdog)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(GitHubOperationTimeout, watchdogToken);
                }
                catch (OperationCanceledException)
                {
                    // Work finished in time - nothing to do.
                    return;
                }

                // Still refreshing after 7 s: build a descriptive TimeoutException
                // and surface it through the standard Kodo warning dialog + log.
                var timeoutEx = new TimeoutException(
                    $"Marketplace refresh did not complete within " +
                    $"{GitHubOperationTimeout.TotalSeconds:0} seconds. " +
                    "This may indicate a slow or stalled network connection, " +
                    "a slow disk scan, or a hung extension operation.");

                KodoDiagnostics.LogWarning(
                    source: "MainWindow.RefreshExtensionsDataAsync.Watchdog",
                    exception: timeoutEx,
                    operation: "Marketplace refresh watchdog");

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // Update status bar so the panel itself reflects the stall.
                    ExtensionsStatusText = "Marketplace refresh is taking too long. Check your connection.";
                    await ShowWarningDialogAsync("Marketplace refresh", timeoutEx);
                });
            }, watchdogToken);
        }

        try
        {
            // ScanInstalledExtensions runs off the UI thread; everything after is marshalled via InvokeAsync.
            var extensionScan = await Task.Run(ScanInstalledExtensions);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyLoadedExtensionsResult(extensionScan));
            await LoadMarketplaceExtensionsAsync();
            await LoadCompilerExtensionsAsync(forceResolve: force);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(CurrentThemeName, _requestedThemeName, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(_requestedThemeName, "Light", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(_requestedThemeName, "Dark", StringComparison.OrdinalIgnoreCase) ||
                     ThemeExtensions.Any(t => string.Equals(t.ThemeDefinition!.ThemeId, _requestedThemeName, StringComparison.OrdinalIgnoreCase))))
                {
                    ApplyTheme(_requestedThemeName);
                }
                var updateCount = MarketplaceExtensions.Count(e => e.IsUpdateAvailable);
                var installedCount = VisibleLoadedExtensions.Count();
                var marketplaceCount = MarketplaceExtensions.Count;
                var installedWord = installedCount == 1 ? "extension" : "extensions";
                var marketplaceWord = marketplaceCount == 1 ? "extension" : "extensions";
                var updateWord = updateCount == 1 ? "update" : "updates";
                ExtensionsStatusText = updateCount > 0
                    ? $"Found {installedCount} installed {installedWord} and {marketplaceCount} in the marketplace. {updateCount} {updateWord} available."
                    : $"Found {installedCount} installed {installedWord} and {marketplaceCount} in the marketplace.";
                _lastExtensionsRefreshUtc = DateTime.UtcNow;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = "Couldn't refresh extensions. Check your connection and try again.");
            await Dispatcher.UIThread.InvokeAsync(async () => await ShowWarningDialogAsync("Marketplace fetch", ex));
        }
        finally
        {
            // Cancel the watchdog whether we succeeded, timed out, or threw - it
            // must not fire after IsRefreshingExtensions has been cleared.
            await watchdogCts.CancelAsync();
            await Dispatcher.UIThread.InvokeAsync(() => IsRefreshingExtensions = false);
        }
    }

    private void LoadExtensions()
    {
        ApplyLoadedExtensionsResult(ScanInstalledExtensions());
    }

    private ExtensionScanResult ScanInstalledExtensions()
    {
        // Drops cached highlighting definitions on reload since old LoadedExtension instances are now orphaned.
        var loadedExtensions = new List<LoadedExtension>();
        var extensionLoadErrors = new List<string>();
        var searchPaths = GetExtensionSearchPaths().ToList();

        var anyFolderFound = false;
        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath)) continue;
            anyFolderFound = true;

            foreach (var koxFile in Directory.GetFiles(searchPath, "*.kox"))
            {
                try
                {
                    foreach (var ext in LoadExtensionsFromKox(koxFile))
                    {
                        AddOrReplaceLoadedExtension(loadedExtensions, ext);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Extensions] Failed to load '{Path.GetFileName(koxFile)}': {ex.Message}");
                    extensionLoadErrors.Add($"Failed to load '{Path.GetFileName(koxFile)}': {ex.Message}");
                }
            }

            foreach (var dir in Directory.GetDirectories(searchPath))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, "manifest.json")))
                    {
                        foreach (var ext in LoadExtensionsFromFolder(dir))
                        {
                            AddOrReplaceLoadedExtension(loadedExtensions, ext);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Extensions] Failed to load folder extension '{Path.GetFileName(dir)}': {ex.Message}");
                    extensionLoadErrors.Add($"Failed to load folder extension '{Path.GetFileName(dir)}': {ex.Message}");
                }
            }
        }

        if (!anyFolderFound)
            extensionLoadErrors.Add($"Extensions folder not found. Expected: {ExtensionsFolderPath}");

        return new ExtensionScanResult(loadedExtensions, extensionLoadErrors);
    }

    private void ApplyLoadedExtensionsResult(ExtensionScanResult result)
    {
        _highlightingCache.Clear();
        _compiledSyntaxProfileCache.Clear();
        _contentSniffCache.Clear();
        SyncObservableCollection(LoadedExtensions, result.Extensions, ext => ext.Id);
        SyncObservableCollection(ExtensionLoadErrors, result.LoadErrors, error => error);

        // Decodes icon bitmaps on the UI thread, then clears the staged raw bytes.
        foreach (var ext in LoadedExtensions)
        {
            if (ext.IconImage is null && ext.SvgData is null && ext.IconBytes is not null)
            {
                if (IsSvgContent(ext.IconBytes))
                {
                    try { ext.SvgData = System.Text.Encoding.UTF8.GetString(ext.IconBytes); }
                    catch { /* malformed SVG - leave icon absent */ }
                }
                else
                {
                    ext.IconImage = DecodeBitmapOnUiThread(ext.IconBytes);
                }
                ext.IconBytes = null;
                ext.NotifyIconChanged();
            }
        }

        // Re-stamps IsActiveTheme since new LoadedExtension instances may have joined since it was set.
        foreach (var ext in ThemeExtensions)
            ext.IsActiveTheme = string.Equals(ext.ThemeCardThemeId, _currentThemeName, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(ExtensionLoadErrors));
        OnPropertyChanged(nameof(VisibleLoadedExtensions));
        NotifyExtensionFiltersChanged();
        OnPropertyChanged(nameof(IsNoExtensionsVisible));
        OnPropertyChanged(nameof(ThemeExtensions));
        OnPropertyChanged(nameof(HasThemeExtensions));
        OnPropertyChanged(nameof(GroupedThemeExtensions));
        OnPropertyChanged(nameof(HasGroupedThemeExtensions));
        RefreshExtensionTheme();
        SyncMarketplaceInstallStates();
        SyncActivePlugins();
    }

    private async Task LoadMarketplaceExtensionsAsync()
    {
        var marketplaceExtensions = new List<MarketplaceExtension>();
        var extensionLoadErrors = new List<string>();

        await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState());

        // Seeds the marketplace list from the disk cache so it appears immediately, before the network round-trip completes.
        var diskJson = TryReadMarketplaceIndexCache();
        if (diskJson is not null)
            ParseAndApplyMarketplaceIndex(diskJson, marketplaceExtensions, extensionLoadErrors);

        try
        {
            // Sends If-None-Match so an unchanged index returns a free 304.
            // Only attaches the conditional header when we already have marketplace data to fall back on.
            _marketplaceIndexETag ??= TryReadMarketplaceIndexETag();
            var hasLocalDataToReuseOn304 = marketplaceExtensions.Count > 0;
            foreach (var indexUrl in MarketplaceIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (hasLocalDataToReuseOn304 && _marketplaceIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _marketplaceIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Marketplace index fetch",
                    async ct =>
                    {
                        using var indexResponse = await MarketplaceHttpClient.SendAsync(indexRequest, ct);
                        if ((int)indexResponse.StatusCode == 304)
                            return (304, (string?)null, (string?)null);
                        if (!indexResponse.IsSuccessStatusCode)
                            return (0, (string?)null, (string?)null);
                        var body = await indexResponse.Content.ReadAsStringAsync(ct);
                        var etag = indexResponse.Headers.ETag?.Tag;
                        return (200, body, etag);
                    });

                if (statusCode == 304)
                {
                    KodoDiagnostics.LogDebug("Marketplace index: 304 Not Modified - reusing cached data.");
                    break;
                }

                if (remoteJson is null)
                    continue;

                var parsedExtensions = new List<MarketplaceExtension>();
                var parsedErrors = new List<string>();
                ParseAndApplyMarketplaceIndex(remoteJson, parsedExtensions, parsedErrors);

                if (parsedExtensions.Count == 0)
                {
                    extensionLoadErrors.Add($"Marketplace index at {indexUrl} did not contain any extensions.");
                    continue;
                }

                marketplaceExtensions.Clear();
                extensionLoadErrors.Clear();
                marketplaceExtensions.AddRange(parsedExtensions);
                extensionLoadErrors.AddRange(parsedErrors);
                TryWriteMarketplaceIndexCache(remoteJson);
                if (newETag is not null)
                {
                    _marketplaceIndexETag = newETag;
                    TryWriteMarketplaceIndexETag(newETag);
                }
                break;
            }
        }
        catch (Exception ex)
        {
            if (diskJson is not null)
            {
                // Network failed but a cached copy exists - the marketplace was
                // already seeded above, so stay usable.  Log without a dialog.
                extensionLoadErrors.Add($"Marketplace index fetch failed (using cached copy): {DescribeFetchFailure(ex)}");
                KodoDiagnostics.LogDebug("Marketplace index fetch failed; using disk cache.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState("Marketplace fetch", ex));
            }
            else
            {
                // No cache at all - propagate so the caller shows the error dialog.
                extensionLoadErrors.Add($"Failed to load remote marketplace index: {DescribeFetchFailure(ex)}");
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState("Marketplace fetch", ex));
                throw;
            }
        }

        // All ObservableCollection mutations and PropertyChanged notifications must
        // run on the UI thread - Avalonia's binding engine requires it.
        Dictionary<string, string> marketplaceIconMap = [];
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SyncMarketplaceExtensionCollection(MarketplaceExtensions, marketplaceExtensions);
            SyncObservableCollection(
                ExtensionLoadErrors,
                ExtensionLoadErrors.Concat(extensionLoadErrors).Distinct().ToList(),
                error => error);

            SyncMarketplaceInstallStates();
            OnPropertyChanged(nameof(ExtensionLoadErrors));
            OnPropertyChanged(nameof(IsMarketplaceUnavailableVisible));
            OnPropertyChanged(nameof(IsMarketplacePartialErrorVisible));
            OnPropertyChanged(nameof(IsMarketplaceEmptyVisible));
            NotifyExtensionFiltersChanged();

            marketplaceIconMap = MarketplaceExtensions
                .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
                .ToDictionary(entry => entry.Id, entry => entry.IconUrl, StringComparer.OrdinalIgnoreCase);
        });
        _ = FetchMarketplaceIconsAsync(marketplaceIconMap);
        _ = FetchInstalledExtensionIconsAsync(marketplaceIconMap);
    }

    private async Task FetchInstalledExtensionIconsAsync(IReadOnlyDictionary<string, string> marketplaceIconMap)
    {
        var tasks = LoadedExtensions
            .Select(ext => (ext, iconUrl: marketplaceIconMap.TryGetValue(ext.Id, out var iconUrl) ? iconUrl : string.Empty))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.iconUrl))
            .Select(async pair =>
            {
                try
                {
                    var icon = await GetCachedIconAsync(pair.iconUrl);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (icon.HasValue)
                        {
                            // Index icon fetched successfully - use it, replacing any kox icon.
                            ReplaceLoadedExtensionIcon(pair.ext, icon);
                        }
                        // else: fetch returned nothing (bad URL, corrupt bytes, etc.) -
                        // leave whatever the kox provided in place.
                    });
                }
                catch (Exception ex)
                {
                    // Network failure for this icon - leave the kox icon (or abbreviation) in place.
                    KodoDiagnostics.LogDebug($"Icon fetch failed for installed extension '{pair.ext.Id}': {pair.iconUrl}", ex);
                }
            });

        await Task.WhenAll(tasks);
    }
    private async Task FetchMarketplaceIconsAsync(
        IReadOnlyDictionary<string, string> marketplaceIconMap,
        ObservableCollection<MarketplaceExtension>? targetCollection = null)
    {
        var entries = targetCollection ?? MarketplaceExtensions;

        // Applies already-cached icon bytes synchronously, skipping an async round-trip.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var entry in entries)
            {
                if (entry.IconImage is not null || entry.SvgData is not null)
                    continue;

                if (!marketplaceIconMap.TryGetValue(entry.Id, out var cachedUrl))
                    continue;

                if (!_marketplaceIconBytesCache.TryGetValue(cachedUrl, out var cachedBytes))
                    continue;

                var icon = DecodeCachedIconBytes(cachedBytes);
                if (icon.HasValue)
                    ReplaceMarketplaceIcon(entry, icon);
            }
        });

        var iconFailures = 0;
        var iconAttempts = 0;
        Exception? lastIconException = null;

        var tasks = entries
            .Where(entry => entry.IconImage is null && entry.SvgData is null && marketplaceIconMap.TryGetValue(entry.Id, out _))
            .Select(async entry =>
            {
                Interlocked.Increment(ref iconAttempts);
                try
                {
                    var icon = await GetCachedIconAsync(marketplaceIconMap[entry.Id]);
                    if (!icon.HasValue)
                    {
                        KodoDiagnostics.LogDebug($"Icon fetch returned no data for marketplace extension '{entry.Id}': {marketplaceIconMap[entry.Id]}");
                        Interlocked.Increment(ref iconFailures);
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ReplaceMarketplaceIcon(entry, icon);
                    });
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Icon fetch failed for marketplace extension '{entry.Id}': {marketplaceIconMap[entry.Id]}", ex);
                    Interlocked.Increment(ref iconFailures);
                    Interlocked.Exchange(ref lastIconException, ex);
                }
            });

        await Task.WhenAll(tasks);

        // Logs a warning if every icon fetch failed, but doesn't show a dialog since icons are purely decorative.
        if (iconAttempts > 0 && iconFailures == iconAttempts && lastIconException is not null)
        {
            KodoDiagnostics.LogDebug(
                $"All {iconAttempts} marketplace icon fetch(es) failed; icons will show abbreviations.",
                lastIconException);
        }
    }

    // Discriminated result from GetCachedIconAsync.
    private readonly record struct IconResult(Bitmap? Bitmap, string? SvgData)
    {
        public bool HasValue => Bitmap is not null || SvgData is not null;
    }

    private static bool IsSvgContent(byte[] bytes)
    {
        // SVG files start with either a UTF-8 BOM + '<' or directly with '<'.
        // Check for the <?xml or <svg opening tag in the first 512 bytes.
        var header = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return header.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || header.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IconResult> GetCachedIconAsync(string iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return default;

        // Fast path: bytes already cached.
        if (_marketplaceIconBytesCache.TryGetValue(iconUrl, out var bytes))
            return DecodeCachedIconBytes(bytes);

        // Cache miss - fetch under semaphore to avoid duplicate requests.
        await _iconFetchSemaphore.WaitAsync();
        try
        {
            if (!_marketplaceIconBytesCache.TryGetValue(iconUrl, out bytes))
            {
                // Per-request timeout (GitHubOperationTimeout) so one stalled icon fetch can't hold up the whole Task.WhenAll.
                using var cts = new CancellationTokenSource(GitHubOperationTimeout);

                // Kodo-hosted icon URLs go through the Contents API with the raw+json header; third-party URLs are plain GETs.
                using var request = new HttpRequestMessage(HttpMethod.Get, iconUrl);
                if (IsGitHubContentsApiUrl(iconUrl))
                    request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");

                using var response = await MarketplaceHttpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                _marketplaceIconBytesCache[iconUrl] = bytes;
            }
        }
        finally
        {
            _iconFetchSemaphore.Release();
        }

        return DecodeCachedIconBytes(bytes);
    }

    private static IconResult DecodeCachedIconBytes(byte[] bytes)
    {
        if (IsSvgContent(bytes))
        {
            try
            {
                return new IconResult(null, System.Text.Encoding.UTF8.GetString(bytes));
            }
            catch { return default; }
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            return new IconResult(new Bitmap(ms), null);
        }
        catch { return default; }
    }

    private static void ReplaceLoadedExtensionIcon(LoadedExtension extension, IconResult icon)
    {
        if (icon.Bitmap is not null)
        {
            if (ReferenceEquals(extension.IconImage, icon.Bitmap)) return;
            extension.IconImage?.Dispose();
            extension.IconImage = icon.Bitmap;
            extension.SvgData = null;
        }
        else if (icon.SvgData is not null)
        {
            extension.IconImage?.Dispose();
            extension.IconImage = null;
            extension.SvgData = icon.SvgData;
        }
        extension.NotifyIconChanged();
    }

    private static void ReplaceMarketplaceIcon(MarketplaceExtension extension, IconResult icon)
    {
        if (icon.Bitmap is not null)
        {
            if (ReferenceEquals(extension.IconImage, icon.Bitmap)) return;
            extension.IconImage?.Dispose();
            extension.IconImage = icon.Bitmap;
            extension.SvgData = null;
        }
        else if (icon.SvgData is not null)
        {
            extension.IconImage?.Dispose();
            extension.IconImage = null;
            extension.SvgData = icon.SvgData;
        }
    }

    private async Task RefreshLatestReleaseAsync()
    {
        if (_isRefreshingLatestRelease)
            return;

        _isRefreshingLatestRelease = true;
        OnPropertyChanged(nameof(IsRefreshingLatestRelease));
        OnPropertyChanged(nameof(RefreshLatestReleaseButtonText));
        LatestReleaseStatusText = "Loading latest release...";

        try
        {
            LatestRelease = await FetchLatestReleaseInfoAsync();

            LatestReleaseStatusText = HasLatestRelease
                ? $"Latest release: {LatestReleaseDisplayName}"
                : "No releases found.";
        }
        catch (Exception ex)
        {
            LatestRelease = null;
            LatestReleaseStatusText = $"Could not load release info: {DescribeFetchFailure(ex)}";

            // Log and surface the Kodo warning dialog so the user knows why the
            // release panel is empty (timeout, rate-limit, no connectivity, etc.).
            KodoDiagnostics.LogDebug("Failed to fetch latest release info", ex);
            await ShowWarningDialogAsync("Latest release info fetch", ex);
        }
        finally
        {
            _isRefreshingLatestRelease = false;
            OnPropertyChanged(nameof(IsRefreshingLatestRelease));
            OnPropertyChanged(nameof(RefreshLatestReleaseButtonText));
        }
    }

    private async Task FetchAnnouncementsAsync(bool forceNetwork)
    {
        IsNewsLoading = true;
        IsNewsError = false;
        NewsItems.Clear();

        try
        {
            if (!forceNetwork && LoadCachedAnnouncements())
                return;

            foreach (var url in new[]
            {
                "https://raw.githubusercontent.com/Kodo-IDE/Kodo/main/Announcements/ANNOUNCEMENTS.md",
                AnnouncementsUrl
            })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (IsGitHubContentsApiUrl(url))
                    request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };

                var md = await RunWithGitHubTimeoutAsync<string?>(
                    "News / Announcements fetch",
                    async ct =>
                    {
                        try
                        {
                            using var resp = await MarketplaceHttpClient.SendAsync(request, ct);
                            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                                return null;
                            if (!resp.IsSuccessStatusCode)
                                return null;

                            return await resp.Content.ReadAsStringAsync(ct);
                        }
                        catch
                        {
                            return null;
                        }
                    });

                if (string.IsNullOrWhiteSpace(md))
                    continue;

                var items = ParseAnnouncementsMd(md);
                foreach (var item in items)
                    NewsItems.Add(item);
                if (NewsItems.Count > 0)
                {
                    SaveNewsCache();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to fetch announcements", ex);
            IsNewsError = false;
        }
        finally
        {
            IsNewsLoading = false;
            OnPropertyChanged(nameof(IsNewsContentVisible));
            OnPropertyChanged(nameof(IsNewsEmpty));
        }
    }

    private bool LoadCachedAnnouncements()
    {
        try
        {
            if (!File.Exists(NewsCachePath))
                return false;

            var json = File.ReadAllText(NewsCachePath);
            var items = JsonSerializer.Deserialize<List<NewsItem>>(json);
            if (items is null || items.Count == 0)
                return false;

            foreach (var item in items)
                NewsItems.Add(item);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveNewsCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(NewsCachePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(NewsItems.ToList());
            File.WriteAllText(NewsCachePath, json);
        }
        catch
        {
            // Best-effort cache only.
        }
    }

    // Parses ANNOUNCEMENTS.md: "## Title", optional "> yyyy-MM-dd" date blockquote, body lines, then "---" between posts.
    // Returned in reverse order so the latest entry appears first.
    private static List<NewsItem> ParseAnnouncementsMd(string md)
    {
        var items = new List<NewsItem>();
        // Split on the horizontal rule separator
        var sections = md.Split(["---"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var section in sections)
        {
            var lines = section.Split('\n')
                               .Select(l => l.TrimEnd('\r').Trim())
                               .ToList();

            string? title = null;
            string? updatedAt = null;
            var bodyLines = new List<string>();

            foreach (var line in lines)
            {
                if (title is null && line.StartsWith("## "))
                {
                    title = line[3..].Trim();
                }
                else if (title is not null && updatedAt is null && line.StartsWith("> "))
                {
                    // Blockquote after the heading is the date; parsed as yyyy-MM-dd, or kept raw.
                    var raw = line[2..].Trim();
                    updatedAt = DateTime.TryParseExact(
                        raw,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var parsed)
                        ? parsed.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)
                        : raw;
                }
                else if (title is not null && line.Length > 0)
                {
                    bodyLines.Add(line);
                }
            }

            if (title is null && bodyLines.Count == 0) continue;

            items.Add(new NewsItem
            {
                Title     = title ?? string.Empty,
                Body      = string.Join("\n", bodyLines).Trim(),
                UpdatedAt = updatedAt ?? string.Empty,
            });
        }

        // Reverse so the last-written entry in the file surfaces at the top.
        items.Reverse();
        return items;
    }

    private async Task<ReleaseInfo?> FetchLatestReleaseInfoAsync()
    {
        var latestRelease = await TryFetchLatestStableReleaseAsync();
        if (latestRelease is not null)
            return latestRelease;

        return await TryFetchLatestListedReleaseAsync();
    }

    private async Task<ReleaseInfo?> TryFetchLatestStableReleaseAsync()
    {
        foreach (var url in LatestReleaseApiUrls)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            var release = await RunWithGitHubTimeoutAsync<ReleaseInfo?>(
                "Latest stable release fetch",
                async ct =>
                {
                    using var response = await MarketplaceHttpClient.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    return ParseReleaseInfo(doc.RootElement);
                });

            if (release is not null)
                return release;
        }

        return null;
    }

    private async Task<ReleaseInfo?> TryFetchLatestListedReleaseAsync()
    {
        foreach (var url in ReleasesApiUrls)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            var release = await RunWithGitHubTimeoutAsync<ReleaseInfo?>(
                "Releases list fetch",
                async ct =>
                {
                    using var response = await MarketplaceHttpClient.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return null;

                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        var parsedRelease = ParseReleaseInfo(release);
                        if (parsedRelease is not null)
                            return parsedRelease;
                    }

                    return null;
                });

            if (release is not null)
                return release;
        }

        return null;
    }

    private static ReleaseInfo? ParseReleaseInfo(JsonElement releaseElement)
    {
        if (releaseElement.ValueKind != JsonValueKind.Object)
            return null;

        var name = releaseElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        var tag = releaseElement.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;
        var notes = releaseElement.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetString() ?? string.Empty
            : string.Empty;
        var url = releaseElement.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString() ?? ReleasesPageUrl
            : ReleasesPageUrl;

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(tag) && string.IsNullOrWhiteSpace(notes))
            return null;

        return new ReleaseInfo
        {
            Name = name,
            Tag = tag,
            Notes = notes,
            Url = url
        };
    }

    // Extracts marketplace entries from raw JSON; shared by the disk-cache seed and live-fetch paths.
    private static void ParseAndApplyMarketplaceIndex(
        string json,
        List<MarketplaceExtension> marketplaceExtensions,
        List<string> extensionLoadErrors,
        string rootPropertyName = "extensions")
    {
        var jsonOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };
        using var doc = JsonDocument.Parse(json, jsonOptions);
        if (!doc.RootElement.TryGetProperty(rootPropertyName, out var extensionsElement) ||
            extensionsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in extensionsElement.EnumerateArray())
        {
            try
            {
                var entry = ParseMarketplaceExtension(item);
                if (string.IsNullOrWhiteSpace(entry.Id) || marketplaceExtensions.Any(e => e.Id == entry.Id))
                    continue;
                marketplaceExtensions.Add(entry);
            }
            catch (Exception itemEx)
            {
                var entryId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "?" : "?";
                extensionLoadErrors.Add($"Skipped malformed marketplace entry '{entryId}': {itemEx.Message}");
                KodoDiagnostics.LogDebug($"Skipped malformed marketplace entry '{entryId}'", itemEx);
            }
        }
    }


    private string? TryReadMarketplaceIndexCache()
    {
        try { return File.Exists(MarketplaceIndexCachePath) ? File.ReadAllText(MarketplaceIndexCachePath, System.Text.Encoding.UTF8) : null; }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not read marketplace index cache.", ex); return null; }
    }

    private void TryWriteMarketplaceIndexCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarketplaceIndexCachePath)!);
            File.WriteAllText(MarketplaceIndexCachePath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not write marketplace index cache.", ex); }
    }

    private string? TryReadMarketplaceIndexETag()
    {
        try { return File.Exists(MarketplaceIndexETagPath) ? File.ReadAllText(MarketplaceIndexETagPath).Trim() : null; }
        catch { return null; }
    }

    private void TryWriteMarketplaceIndexETag(string etag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarketplaceIndexETagPath)!);
            File.WriteAllText(MarketplaceIndexETagPath, etag);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not write marketplace index ETag.", ex); }
    }

    private static MarketplaceExtension ParseMarketplaceExtension(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        var declaredVersion = item.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
        var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        var author = item.TryGetProperty("author", out var authorElement) ? authorElement.GetString() ?? string.Empty : string.Empty;
        var description = item.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty;
        var rawDownloadUrl = NormalizeGitHubBlobViewerUrl(
            item.TryGetProperty("downloadUrl", out var downloadUrlElement) ? downloadUrlElement.GetString() ?? string.Empty : string.Empty);
        var declaredFileName = item.TryGetProperty("fileName", out var fileNameElement) ? fileNameElement.GetString() ?? string.Empty : string.Empty;
        var iconUrl = NormalizeGitHubUrl(
            item.TryGetProperty("iconUrl", out var iconUrlElement) ? iconUrlElement.GetString() ?? string.Empty : string.Empty);
        var urlFileName = TryGetFileNameFromUrl(rawDownloadUrl);
        var bestKnownVersion = GetHighestKnownExtensionVersion(declaredVersion, declaredFileName, urlFileName);
        // ^ declaredVersion is trusted as-is; declaredFileName/urlFileName only contribute if a
        // v1.2.3-style version can be extracted from them (see GetHighestKnownExtensionVersion).
        var canonicalFileName = GetCanonicalMarketplaceFileName(declaredFileName, urlFileName, bestKnownVersion);
        var canonicalDownloadUrl = NormalizeMarketplaceDownloadUrl(rawDownloadUrl, canonicalFileName);

        return new MarketplaceExtension
        {
            Id = id,
            Version = bestKnownVersion,
            Name = name,
            Type = type,
            Author = author,
            Description = description,
            DownloadUrl = canonicalDownloadUrl,
            FileName = canonicalFileName,
            IconUrl = iconUrl
        };
    }

    private void SyncMarketplaceInstallStates()
    {
        foreach (var installedExtension in LoadedExtensions)
            installedExtension.IsUpdateAvailable = false;

        foreach (var entry in MarketplaceExtensions)
        {
            var localExt = GetPreferredLoadedExtension(entry.Id);
            var isUpdateAvailable = localExt is not null && CompareExtensionVersions(entry.Version, localExt.Version) > 0;

            entry.SetInstalledState(localExt, isUpdateAvailable);
            if (localExt is not null)
                localExt.IsUpdateAvailable = isUpdateAvailable;
        }

        OnPropertyChanged(nameof(AvailableExtensionUpdatesCount));
        OnPropertyChanged(nameof(IsExtensionUpdateBannerVisible));
        OnPropertyChanged(nameof(ExtensionUpdatesBannerText));
        OnPropertyChanged(nameof(AutoUpdateExtensionsStatusText));
        NotifyExtensionActionStateChanged();
        NotifyExtensionFiltersChanged();
    }

    private void NotifyExtensionFiltersChanged()
    {
        OnPropertyChanged(nameof(FilteredInstalledExtensions));
        OnPropertyChanged(nameof(FilteredMarketplaceExtensions));
        OnPropertyChanged(nameof(FilteredCompilerExtensions));
        OnPropertyChanged(nameof(FilteredInstalledCompilerExtensions));
        OnPropertyChanged(nameof(IsNoExtensionsVisible));
        OnPropertyChanged(nameof(IsInstalledSearchEmptyVisible));
        OnPropertyChanged(nameof(IsMarketplaceSearchEmptyVisible));
        OnPropertyChanged(nameof(IsMarketplaceEmptyVisible));
        OnPropertyChanged(nameof(InstalledExtensionsCount));
        OnPropertyChanged(nameof(InstalledCompilersCount));
        OnPropertyChanged(nameof(MarketplaceEmptyStateText));
        OnPropertyChanged(nameof(HasVisibleInstalledExtensions));
        OnPropertyChanged(nameof(HasVisibleMarketplaceExtensions));
        OnPropertyChanged(nameof(HasVisibleCompilerExtensions));
        OnPropertyChanged(nameof(HasVisibleInstalledCompilerExtensions));
        OnPropertyChanged(nameof(HasVisibleInstalledExtensionsOrCompilers));
        OnPropertyChanged(nameof(HasVisibleInstalledExtensionsAndCompilers));
        OnPropertyChanged(nameof(IsInstalledCompilersEmptyStateVisible));
    }

    private void NotifyExtensionActionStateChanged()
    {
        OnPropertyChanged(nameof(CanUpdateAllExtensions));
        OnPropertyChanged(nameof(UpdateAllExtensionsButtonText));
    }

    private MarketplaceExtension? GetMarketplaceExtensionForInstalled(LoadedExtension extension) =>
        MarketplaceExtensions.FirstOrDefault(entry =>
            entry.Id.Equals(extension.Id, StringComparison.OrdinalIgnoreCase));

    private void AddOrReplaceLoadedExtension(IList<LoadedExtension> extensions, LoadedExtension extension)
    {
        var existingIndex = extensions
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => x.item.Id.Equals(extension.Id, StringComparison.OrdinalIgnoreCase));

        if (existingIndex is null)
        {
            extensions.Add(extension);
            return;
        }

        if (ShouldReplaceLoadedExtension(existingIndex.item, extension))
            extensions[existingIndex.index] = extension;
    }

    private static void SyncObservableCollection<T, TKey>(
        ObservableCollection<T> target,
        IList<T> source,
        Func<T, TKey> keySelector)
        where TKey : notnull
    {
        var sourceByKey = source.ToDictionary(keySelector);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            var key = keySelector(target[i]);
            if (!sourceByKey.ContainsKey(key))
                target.RemoveAt(i);
        }

        var targetIndexByKey = new Dictionary<TKey, int>();
        for (var i = 0; i < target.Count; i++)
            targetIndexByKey[keySelector(target[i])] = i;

        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var key = keySelector(item);
            var existingIndex = targetIndexByKey.TryGetValue(key, out var foundIndex) ? foundIndex : -1;

            if (existingIndex == -1)
            {
                target.Insert(Math.Min(i, target.Count), item);
                for (var j = i; j < target.Count; j++)
                    targetIndexByKey[keySelector(target[j])] = j;
                continue;
            }

            if (existingIndex != i)
            {
                target.Move(existingIndex, i);
                var start = Math.Min(existingIndex, i);
                for (var j = start; j < target.Count; j++)
                    targetIndexByKey[keySelector(target[j])] = j;
            }

            if (!ReferenceEquals(target[i], item))
            {
                target[i] = item;
                targetIndexByKey[key] = i;
            }
        }
    }

    // Like SyncObservableCollection, but carries over the already-fetched icon bitmap.
    private static void SyncMarketplaceExtensionCollection(
        ObservableCollection<MarketplaceExtension> target,
        IList<MarketplaceExtension> source)
    {
        var sourceByKey = source.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!sourceByKey.ContainsKey(target[i].Id))
            {
                target[i].IconImage?.Dispose();
                target.RemoveAt(i);
            }
        }

        var targetIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < target.Count; i++)
            targetIndexByKey[target[i].Id] = i;

        for (var i = 0; i < source.Count; i++)
        {
            var incoming = source[i];
            var key = incoming.Id;
            var existingIndex = targetIndexByKey.TryGetValue(key, out var foundIndex) ? foundIndex : -1;

            if (existingIndex == -1)
            {
                target.Insert(Math.Min(i, target.Count), incoming);
                for (var j = i; j < target.Count; j++)
                    targetIndexByKey[target[j].Id] = j;
                continue;
            }

            if (existingIndex != i)
            {
                target.Move(existingIndex, i);
                var start = Math.Min(existingIndex, i);
                for (var j = start; j < target.Count; j++)
                    targetIndexByKey[target[j].Id] = j;
            }

            var existing = target[i];
            if (!ReferenceEquals(existing, incoming))
            {
                // Transfer the already-decoded bitmap/SVG so the UI keeps showing the icon
                // while the rest of the object is refreshed with updated metadata.
                if (existing.IconImage is not null && incoming.IconImage is null)
                    incoming.IconImage = existing.IconImage;
                else
                    existing.IconImage?.Dispose();

                if (existing.SvgData is not null && incoming.SvgData is null)
                    incoming.SvgData = existing.SvgData;

                target[i] = incoming;
                targetIndexByKey[key] = i;
            }
        }
    }

    private LoadedExtension? GetPreferredLoadedExtension(string extensionId) =>
        LoadedExtensions
            .Where(ext => ext.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetLoadedExtensionSourcePriority)
            .ThenByDescending(ext => ParseVersionNumbers(ext.Version), VersionNumberSequenceComparer.Instance)
            .FirstOrDefault();

    private bool ShouldReplaceLoadedExtension(LoadedExtension current, LoadedExtension candidate)
    {
        var currentPriority = GetLoadedExtensionSourcePriority(current);
        var candidatePriority = GetLoadedExtensionSourcePriority(candidate);
        if (candidatePriority != currentPriority)
            return candidatePriority > currentPriority;

        return CompareExtensionVersions(candidate.Version, current.Version) > 0;
    }

    private int GetLoadedExtensionSourcePriority(LoadedExtension extension)
    {
        if (!string.IsNullOrWhiteSpace(extension.SourcePath))
        {
            var sourcePath = Path.GetFullPath(extension.SourcePath);
            if (IsPathInsideDirectory(sourcePath, ExtensionsFolderPath))
                return 2;

            if (IsPathInsideDirectory(sourcePath, ProjectExtensionsFolderPath))
                return 1;
        }

        return 0;
    }

    private async Task InstallMarketplaceExtensionAsync(MarketplaceExtension marketplaceExtension)
    {
        if (marketplaceExtension.IsInstalling || (marketplaceExtension.IsInstalled && !marketplaceExtension.IsUpdateAvailable))
            return;

        RefreshMarketplaceConnectivityState();
        marketplaceExtension.IsInstalling = true;
        NotifyExtensionActionStateChanged();
        var action = marketplaceExtension.IsUpdateAvailable ? "Updating" : "Installing";
        marketplaceExtension.InstallButtonText = $"{action}...";
        ExtensionsStatusText = $"{action} {marketplaceExtension.Name}...";

        try
        {
            EnsureExtensionsFolder();
            var wasUpdate = marketplaceExtension.IsUpdateAvailable;
            var installedExtension = GetPreferredLoadedExtension(marketplaceExtension.Id);
            var outputPath = ResolveExtensionInstallPath(marketplaceExtension, installedExtension);

            // Downloads the package under GitHubOperationTimeout so a stall can't silently block the install pipeline.
            var bytes = await RunWithGitHubTimeoutAsync(
                $"Extension download - {marketplaceExtension.Name}",
                async ct =>
                {
                    var downloadUrls = BuildExtensionDownloadUrlCandidates(marketplaceExtension.DownloadUrl);
                    foreach (var downloadUrl in downloadUrls)
                    {

                        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                        // Contents API URLs require raw+json to receive file bytes directly
                        // instead of a base64-wrapped JSON envelope.
                        if (IsGitHubContentsApiUrl(downloadUrl))
                            downloadRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");

                        using var downloadResponse = await MarketplaceHttpClient.SendAsync(
                            downloadRequest, HttpCompletionOption.ResponseContentRead, ct);
                        if (!downloadResponse.IsSuccessStatusCode)
                            continue;

                        return await downloadResponse.Content.ReadAsByteArrayAsync(ct);
                    }

                    throw new HttpRequestException($"Unable to download extension package from any known location for {marketplaceExtension.Name}.");
                });

            ValidateDownloadedExtensionPackage(marketplaceExtension, bytes);
            DeleteInstalledExtensionSources(marketplaceExtension.Id, outputPath);
            await File.WriteAllBytesAsync(outputPath, bytes);
            NormalizeKoxManifestVersion(outputPath);

            // suppressWatchdog=true: the download already has its own timeout guard.
            await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
            ExtensionsStatusText = $"{marketplaceExtension.Name} {(wasUpdate ? "updated" : "installed")}.";
        }
        catch (Exception ex)
        {
            marketplaceExtension.SetInstalledState(
                GetPreferredLoadedExtension(marketplaceExtension.Id),
                marketplaceExtension.IsUpdateAvailable);
            RefreshMarketplaceConnectivityState($"Extension install - {marketplaceExtension.Name}", ex);
            ExtensionsStatusText = $"Failed to install {marketplaceExtension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Extension install - {marketplaceExtension.Name}", ex);
        }
        finally
        {
            marketplaceExtension.IsInstalling = false;
            NotifyExtensionActionStateChanged();
            SyncMarketplaceInstallStates();
        }
    }

    private static void ValidateDownloadedExtensionPackage(MarketplaceExtension marketplaceExtension, byte[] packageBytes)
    {
        using var ms = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException($"Downloaded package for {marketplaceExtension.Name} is missing manifest.json.");

        using var manifestStream = manifestEntry.Open();
        using var manifestDoc = JsonDocument.Parse(manifestStream);
        var manifest = manifestDoc.RootElement;
        var manifestId = manifest.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        var manifestVersion = manifest.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;

        if (!string.Equals(manifestId, marketplaceExtension.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded package id '{manifestId}' does not match expected id '{marketplaceExtension.Id}'.");
        }

        if (CompareExtensionVersions(manifestVersion, marketplaceExtension.Version) < 0)
        {
            throw new InvalidDataException(
                $"Downloaded package version '{manifestVersion}' is older than the marketplace version '{marketplaceExtension.Version}'.");
        }
    }

    private static IEnumerable<string> BuildExtensionDownloadUrlCandidates(string downloadUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                candidates.Add(url);
        }

        Add(downloadUrl);

        if (TryParseGitHubContentsUrl(downloadUrl, out _, out _, out var path))
        {
            var suffix = GetExtensionPackageRelativePath(path);
            Add(BuildGitHubContentsUrl("Kodo-IDE", "Kodo-Extensions", PrefixExtensionPath("Extensions", suffix)));
        }
        else if (TryParseGitHubRawUrl(downloadUrl, out _, out _, out var rawPath))
        {
            var suffix = GetExtensionPackageRelativePath(rawPath);
            Add(BuildGitHubContentsUrl("Kodo-IDE", "Kodo-Extensions", PrefixExtensionPath("Extensions", suffix)));
        }

        return candidates;
    }


    private static bool TryParseGitHubContentsUrl(string url, out string owner, out string repo, out string path)
    {
        owner = string.Empty;
        repo = string.Empty;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        if (segments.Length < 6 ||
            !segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase) ||
            !segments[3].Equals("contents", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        owner = segments[1];
        repo = segments[2];
        path = string.Join("/", segments, 4, segments.Length - 4);
        return true;
    }

    private static bool TryParseGitHubRawUrl(string url, out string owner, out string repo, out string path)
    {
        owner = string.Empty;
        repo = string.Empty;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        if (segments.Length < 3)
            return false;

        owner = segments[0];
        repo = segments[1];
        path = string.Join("/", segments, 2, segments.Length - 2);
        return true;
    }

    private static string GetExtensionPackageRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', 2);
        if (segments.Length == 2 &&
            (segments[0].Equals("Extensions", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("Official_Extensions", StringComparison.OrdinalIgnoreCase)))
        {
            return segments[1];
        }

        return normalized;
    }

    private static string PrefixExtensionPath(string folderName, string suffix)
    {
        suffix = suffix.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(suffix) ? folderName : $"{folderName}/{suffix}";
    }

    private static string BuildGitHubContentsUrl(string owner, string repo, string path) =>
        $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";

    private async Task UninstallExtensionAsync(LoadedExtension extension)
    {
        if (string.IsNullOrWhiteSpace(extension.SourcePath))
        {
            ExtensionsStatusText = $"Cannot uninstall {extension.Name}: missing source path.";
            return;
        }

        try
        {
            var resolvedPath = Path.GetFullPath(extension.SourcePath);
            if (!IsPathInsideDirectory(resolvedPath, ExtensionsFolderPath) &&
                !IsPathInsideDirectory(resolvedPath, ProjectExtensionsFolderPath))
            {
                ExtensionsStatusText = $"Cannot uninstall {extension.Name}: source is outside the Extensions folders.";
                return;
            }

            if (extension.IsDirectorySource)
            {
                if (Directory.Exists(resolvedPath))
                    Directory.Delete(resolvedPath, recursive: true);
            }
            else
            {
                if (File.Exists(resolvedPath))
                    File.Delete(resolvedPath);
            }

            // suppressWatchdog: true - uninstall is a local disk operation with its
            // own error handling above; the watchdog is not meaningful here.
            await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
            ExtensionsStatusText = $"{extension.Name} uninstalled.";
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Failed to uninstall {extension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Extension uninstall - {extension.Name}", ex);
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetExtensionSearchPaths()
    {
        yield return ExtensionsFolderPath;

        // Also search the project source tree when running from the build output directory
        var projectRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..");
        var srcPath = Path.GetFullPath(Path.Combine(projectRoot, "Extensions"));
        if (!string.Equals(srcPath, ExtensionsFolderPath, StringComparison.OrdinalIgnoreCase))
            yield return srcPath;
    }

    private IEnumerable<(string Path, bool IsDirectory)> EnumerateInstalledExtensionSources(string extensionId)
    {
        foreach (var searchPath in GetExtensionSearchPaths())
        {
            if (!Directory.Exists(searchPath))
                continue;

            foreach (var koxFile in Directory.GetFiles(searchPath, "*.kox"))
            {
                if (ExtensionSourceMatchesId(koxFile, extensionId, isDirectory: false))
                    yield return (koxFile, false);
            }

            foreach (var dir in Directory.GetDirectories(searchPath))
            {
                if (ExtensionSourceMatchesId(dir, extensionId, isDirectory: true))
                    yield return (dir, true);
            }
        }
    }

    private void DeleteInstalledExtensionSources(string extensionId, string? pathToKeep = null)
    {
        foreach (var source in EnumerateInstalledExtensionSources(extensionId))
        {
            var resolvedPath = Path.GetFullPath(source.Path);
            if (!string.IsNullOrWhiteSpace(pathToKeep) &&
                string.Equals(resolvedPath, Path.GetFullPath(pathToKeep), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsPathInsideDirectory(resolvedPath, ExtensionsFolderPath) &&
                !IsPathInsideDirectory(resolvedPath, ProjectExtensionsFolderPath))
            {
                continue;
            }

            if (source.IsDirectory)
            {
                if (Directory.Exists(resolvedPath))
                    Directory.Delete(resolvedPath, recursive: true);
            }
            else
            {
                if (File.Exists(resolvedPath))
                    File.Delete(resolvedPath);
            }
        }
    }

    private string ResolveExtensionInstallPath(MarketplaceExtension marketplaceExtension, LoadedExtension? installedExtension)
    {
        if (installedExtension is not null &&
            !installedExtension.IsDirectorySource &&
            !string.IsNullOrWhiteSpace(installedExtension.SourcePath))
        {
            var sourcePath = Path.GetFullPath(installedExtension.SourcePath);
            if (IsPathInsideDirectory(sourcePath, ExtensionsFolderPath) ||
                IsPathInsideDirectory(sourcePath, ProjectExtensionsFolderPath))
            {
                return sourcePath;
            }
        }

        var fileName = string.IsNullOrWhiteSpace(marketplaceExtension.FileName)
            ? TryGetFileNameFromUrl(marketplaceExtension.DownloadUrl)
            : marketplaceExtension.FileName;

        return Path.Combine(ExtensionsFolderPath, fileName);
    }

    private static string TryGetFileNameFromUrl(string url)
    {
        try
        {
            return Path.GetFileName(new Uri(url).AbsolutePath);
        }
        catch
        {
            return "extension.kox";
        }
    }

    // The first candidate is the declared "version" field and is trusted as-is, even if it
    // doesn't match the v1.2.3 pattern (e.g. "8.0.404"). Every candidate after that is a
    // fileName/URL - those are only used if a v1.2.3-style version can be *extracted* from
    // them. Falling back to the raw fileName (as this used to do) meant a whole installer
    // fileName like "dotnet-sdk-8.0.404-win-x64.exe" could out-compare "8.0.404" (its stray
    // digits, e.g. the "64" in "x64", made it look like a "higher" version) and get displayed
    // in place of the real version.
    private static string GetHighestKnownExtensionVersion(string declaredVersion, params string[] fileNameCandidates)
    {
        var bestVersion = declaredVersion ?? string.Empty;

        foreach (var candidate in fileNameCandidates)
        {
            var extractedVersion = ExtractVersionFromName(candidate);
            if (string.IsNullOrWhiteSpace(extractedVersion))
                continue;

            if (CompareExtensionVersions(extractedVersion, bestVersion) > 0)
                bestVersion = extractedVersion;
        }

        return bestVersion;
    }

    private static string GetCanonicalMarketplaceFileName(string declaredFileName, string urlFileName, string bestKnownVersion)
    {
        var baseFileName = !string.IsNullOrWhiteSpace(declaredFileName)
            ? declaredFileName
            : !string.IsNullOrWhiteSpace(urlFileName) && !string.Equals(urlFileName, "extension.kox", StringComparison.OrdinalIgnoreCase)
                ? urlFileName
                : string.Empty;

        if (string.IsNullOrWhiteSpace(baseFileName))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(bestKnownVersion))
            return baseFileName;

        var fileVersion = ExtractVersionFromName(baseFileName);
        if (string.IsNullOrWhiteSpace(fileVersion))
            return baseFileName;

        return string.Equals(fileVersion, bestKnownVersion, StringComparison.OrdinalIgnoreCase)
            ? baseFileName
            : ReplaceVersionInValue(baseFileName, fileVersion, bestKnownVersion);
    }

    private static string NormalizeMarketplaceDownloadUrl(string rawDownloadUrl, string canonicalFileName)
    {
        if (string.IsNullOrWhiteSpace(rawDownloadUrl) || string.IsNullOrWhiteSpace(canonicalFileName))
            return rawDownloadUrl;

        if (!Uri.TryCreate(rawDownloadUrl, UriKind.Absolute, out var uri))
            return rawDownloadUrl;

        var absolutePath = uri.AbsolutePath;
        var lastSlashIndex = absolutePath.LastIndexOf('/');
        if (lastSlashIndex < 0)
            return rawDownloadUrl;

        var pathPrefix = absolutePath[..(lastSlashIndex + 1)];
        var normalizedPath = pathPrefix + Uri.EscapeDataString(canonicalFileName);
        var builder = new UriBuilder(uri) { Path = normalizedPath };
        return builder.Uri.ToString();
    }

    /// Converts a GitHub blob-viewer URL to the Contents API form for raw bytes.
    private static string NormalizeGitHubBlobViewerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        // Only rewrite github.com /blob/ viewer URLs - everything else is already fetchable.
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return url;

        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        if (segments.Length >= 5 && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            var owner = segments[0];
            var repo  = segments[1];
            var path  = string.Join("/", segments, 4, segments.Length - 4);
            return $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
        }

        return url; // non-blob github.com URL (e.g. releases page) - leave alone
    }


    // Owner/repo Kodo's own marketplace and compiler icons live in (see MarketplaceIndexUrls /
    // CompilerIndexUrls). Only URLs pointing into this repo get rewritten to the Contents API -
    // that's what lets Kodo-hosted icons be fetched with the raw+json Accept header. Third-party
    // extension/compiler authors (e.g. Shine's own website repo) host their own icons and must be
    // left as plain URLs: raw.githubusercontent.com already serves raw bytes directly, so routing
    // it through api.github.com instead just trades a working CDN request for one that's subject
    // to GitHub's unauthenticated API rate limit and can 404 on branch/path mismatches.
    private const string KodoExtensionsOwner = "Kodo-IDE";
    private const string KodoExtensionsRepo = "Kodo-Extensions";

    /// Normalises GitHub URLs pointing at Kodo's own extensions repo to the Contents API form.
    /// URLs for any other owner/repo (third-party icons) are returned unchanged.
    private static string NormalizeGitHubUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        // github.com/blob viewer URL
        // /{owner}/{repo}/blob/{branch}/{...path} -> Contents API
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segments.Length >= 5 &&
                segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) &&
                IsKodoExtensionsRepo(segments[0], segments[1]))
            {
                var owner = segments[0];
                var repo  = segments[1];
                var path  = string.Join("/", segments, 4, segments.Length - 4);
                return $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
            }
            return url; // non-blob or third-party github.com URL - leave alone
        }

        // raw.githubusercontent.com CDN URL
        // /{owner}/{repo}/{branch}/{...path} -> Contents API, but only for Kodo's own repo.
        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segments.Length >= 4 && IsKodoExtensionsRepo(segments[0], segments[1]))
            {
                var owner = segments[0];
                var repo  = segments[1];
                // segments[2] is the branch - omitted from the Contents API path
                var path  = string.Join("/", segments, 3, segments.Length - 3);
                return $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
            }
            return url; // third-party raw.githubusercontent.com URL - already serves raw bytes, leave alone
        }

        // Already a Contents API URL or a non-GitHub third-party URL - leave unchanged.
        return url;
    }

    private static bool IsKodoExtensionsRepo(string owner, string repo) =>
        owner.Equals(KodoExtensionsOwner, StringComparison.OrdinalIgnoreCase) &&
        repo.Equals(KodoExtensionsRepo, StringComparison.OrdinalIgnoreCase);


    /// True when the URL is a GitHub Contents API endpoint, used to decide whether to add the raw+json Accept header.

    private static bool IsGitHubContentsApiUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.StartsWith("https://api.github.com/repos/", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("/contents/", StringComparison.OrdinalIgnoreCase);

    private static string ReplaceVersionInValue(string value, string oldVersion, string newVersion)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.IsNullOrWhiteSpace(oldVersion) ||
            string.IsNullOrWhiteSpace(newVersion))
        {
            return value;
        }

        return Regex.Replace(
            value,
            Regex.Escape(oldVersion),
            newVersion,
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
    }

    private static bool ExtensionSourceMatchesId(string path, string extensionId, bool isDirectory)
    {
        try
        {
            var manifest = isDirectory
                ? ReadManifestFromFolder(path)
                : ReadManifestFromKox(path);

            return manifest.TryGetProperty("id", out var id) &&
                   string.Equals(id.GetString(), extensionId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement ReadManifestFromFolder(string folderPath)
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folderPath, "manifest.json")));
        return manifestDoc.RootElement.Clone();
    }

    private static JsonElement ReadManifestFromKox(string koxPath)
    {
        using var archive = ZipFile.OpenRead(koxPath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException($"Missing manifest.json in '{koxPath}'.");
        using var manifestStream = manifestEntry.Open();
        using var manifestDoc = JsonDocument.Parse(manifestStream);
        return manifestDoc.RootElement.Clone();
    }

    private static void NormalizeKoxManifestVersion(string koxPath)
    {
        try
        {
            var inferredVersion = ExtractVersionFromName(Path.GetFileName(koxPath));
            if (string.IsNullOrWhiteSpace(inferredVersion) || !File.Exists(koxPath))
                return;

            using var archive = ZipFile.Open(koxPath, ZipArchiveMode.Update);
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null)
                return;

            JsonObject? manifestObject;
            using (var manifestStream = manifestEntry.Open())
            using (var reader = new StreamReader(manifestStream))
            {
                manifestObject = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
            }

            if (manifestObject is null)
                return;

            var manifestVersion = manifestObject["version"]?.GetValue<string>() ?? string.Empty;
            if (CompareExtensionVersions(inferredVersion, manifestVersion) <= 0)
                return;

            manifestObject["version"] = inferredVersion;
            manifestEntry.Delete();
            var newManifestEntry = archive.CreateEntry("manifest.json");
            using var outputStream = newManifestEntry.Open();
            using var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = true });
            manifestObject.WriteTo(writer);
            writer.Flush();
        }
        catch
        {
            // If we cannot normalize the package metadata, fall back to reading it as-is.
        }
    }

    private static int CompareExtensionVersions(string left, string right)
    {
        var leftParts = ParseVersionNumbers(left);
        var rightParts = ParseVersionNumbers(right);
        return VersionNumberSequenceComparer.Instance.Compare(leftParts, rightParts);
    }

    private static string GetBestKnownExtensionVersion(string manifestVersion, string sourcePath)
    {
        var inferredVersion = ExtractVersionFromName(Path.GetFileName(sourcePath));
        return CompareExtensionVersions(inferredVersion, manifestVersion) > 0
            ? inferredVersion
            : manifestVersion;
    }

    private static DateTime? GetExtensionSourceActivityUtc(string path, bool isDirectory)
    {
        try
        {
            var createdUtc = isDirectory ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            var modifiedUtc = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            var activityUtc = createdUtc > modifiedUtc ? createdUtc : modifiedUtc;
            return activityUtc == DateTime.MinValue ? null : activityUtc;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractVersionFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Captures an optional pre-release suffix (-BETA, -rc1, -alpha.2, ...) along with the
        // numeric version. Without this, extracting from "Shine-v0.8.0-BETA-Installer.exe" would
        // return just "v0.8.0", dropping "-BETA" - which made this look like a stale/mismatched
        // filename versus the declared "v0.8.0-BETA" version and triggered a find/replace that
        // duplicated the suffix ("...v0.8.0-BETA-BETA-Installer.exe"), pointing the download URL
        // at an asset that doesn't exist and causing an HTTP 404 on install.
        var match = Regex.Match(name, @"(?i)(v\d+(?:\.\d+)+(?:-[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)?)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static int[] ParseVersionNumbers(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return [0];

        var matches = Regex.Matches(version, @"\d+");
        if (matches.Count == 0)
            return [0];

        return matches
            .Select(match => int.TryParse(match.Value, out var part) ? part : 0)
            .ToArray();
    }

    private sealed class VersionNumberSequenceComparer : IComparer<int[]>
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

    private IEnumerable<LoadedExtension> LoadExtensionsFromFolder(string folderPath)
    {
        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(manifestPath)) yield break;

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var baseExt = ParseManifest(manifestDoc.RootElement);
        baseExt = baseExt with { Version = GetBestKnownExtensionVersion(baseExt.Version, folderPath) };
        baseExt.SourcePath = folderPath;
        baseExt.IsDirectorySource = true;
        baseExt.InstalledOnUtc = GetExtensionSourceActivityUtc(folderPath, isDirectory: true);
        if (baseExt.PluginAssemblyFileName is not null &&
            File.Exists(Path.Combine(folderPath, baseExt.PluginAssemblyFileName)))
            baseExt.PluginFolderPath = folderPath;

        foreach (var languageFileName in EnumerateLanguageProfileNames())
        {
            var langPath = Path.Combine(folderPath, languageFileName);
            if (!File.Exists(langPath)) continue;

            using var langDoc = JsonDocument.Parse(File.ReadAllText(langPath));
            ParseLanguage(langDoc.RootElement, baseExt);
        }

        var iconPath = Path.Combine(folderPath, "icon.png");
        if (File.Exists(iconPath))
        {
            using var iconStream = File.OpenRead(iconPath);
            baseExt.IconBytes = ReadIconBytesFromStream(iconStream);
        }
        else
        {
            var svgIconPath = Path.Combine(folderPath, "icon.svg");
            if (File.Exists(svgIconPath))
            {
                using var iconStream = File.OpenRead(svgIconPath);
                baseExt.IconBytes = ReadIconBytesFromStream(iconStream);
            }
        }

        var themePath = Path.Combine(folderPath, "theme.json");
        if (!File.Exists(themePath))
        {
            // No theme file - yield the extension as-is (language extension, etc.)
            yield return baseExt;
            yield break;
        }

        using var themeDoc = JsonDocument.Parse(File.ReadAllText(themePath));
        var root = themeDoc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            // One LoadedExtension per theme entry in the array
            var index = 0;
            foreach (var themeElement in root.EnumerateArray())
            {
                var def = ParseTheme(themeElement, baseExt);
                var entry = CloneBaseExtension(baseExt);
                entry.ThemeDefinition = def;
                // Make the Id unique so duplicate-checking works correctly.
                // Mark index > 0 entries so they're hidden from the Installed list.
                if (index > 0)
                    entry = entry with { Id = $"{baseExt.Id}_{def.ThemeId}", IsThemeSubEntry = true };
                yield return entry;
                index++;
            }
        }
        else
        {
            baseExt.ThemeDefinition = ParseTheme(root, baseExt);
            yield return baseExt;
        }
    }

    // Shallow-clones a LoadedExtension so each theme entry gets its own object
    private static LoadedExtension CloneBaseExtension(LoadedExtension src) => new()
    {
        Id                = src.Id,
        Version           = src.Version,
        Name              = src.Name,
        Type              = src.Type,
        Author            = src.Author,
        Description       = src.Description,
        Extensions        = src.Extensions,
        Keywords          = src.Keywords,
        Types             = src.Types,
        Functions         = src.Functions,
        Properties        = src.Properties,
        Namespaces        = src.Namespaces,
        CommentLine       = src.CommentLine,
        CommentBlockStart = src.CommentBlockStart,
        CommentBlockEnd   = src.CommentBlockEnd,
        StringDelimiters  = src.StringDelimiters.ToArray(),
        MultiLineStringDelimiters = src.MultiLineStringDelimiters.ToArray(),
        DisableSingleQuoteStrings = src.DisableSingleQuoteStrings,
        ColorTokens       = new Dictionary<string, string>(src.ColorTokens),
        SourcePath        = src.SourcePath,
        IsDirectorySource = src.IsDirectorySource,
        InstalledOnUtc    = src.InstalledOnUtc,
        PluginAssemblyFileName = src.PluginAssemblyFileName,
        PluginFolderPath  = src.PluginFolderPath,
        IconImage         = src.IconImage,
        IconBytes         = src.IconBytes,
    };

    // Loads a PNG from a stream and scales it to 48x48 if it is square,
    // otherwise returns null so the text fallback is used.
    private static byte[]? ReadIconBytesFromStream(Stream stream)
    {
        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // Must be called on the UI thread. Decodes raw PNG bytes into an Avalonia Bitmap
    // and validates that the image is square (non-square icons are rejected).
    private static Bitmap? DecodeBitmapOnUiThread(byte[]? iconBytes)
    {
        if (iconBytes is null) return null;
        try
        {
            using var ms = new MemoryStream(iconBytes);
            var bmp = new Bitmap(ms);
            if (bmp.PixelSize.Width != bmp.PixelSize.Height) return null;
            return bmp;
        }
        catch { return null; }
    }

    private IEnumerable<LoadedExtension> LoadExtensionsFromKox(string koxPath)
    {
        using var archive = ZipFile.OpenRead(koxPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is null) yield break;

        using var manifestStream = manifestEntry.Open();
        using var manifestDoc = JsonDocument.Parse(manifestStream);
        var baseExt = ParseManifest(manifestDoc.RootElement);
        baseExt = baseExt with { Version = GetBestKnownExtensionVersion(baseExt.Version, koxPath) };
        baseExt.SourcePath = koxPath;
        baseExt.IsDirectorySource = false;
        baseExt.InstalledOnUtc = GetExtensionSourceActivityUtc(koxPath, isDirectory: false);
        if (baseExt.PluginAssemblyFileName is not null &&
            archive.GetEntry(baseExt.PluginAssemblyFileName) is not null)
            baseExt.PluginFolderPath = ExtractKoxPluginFiles(archive, baseExt.Id, baseExt.Version);

        foreach (var languageFileName in EnumerateLanguageProfileNames())
        {
            var langEntry = archive.GetEntry(languageFileName);
            if (langEntry is null) continue;

            using var langStream = langEntry.Open();
            using var langDoc = JsonDocument.Parse(langStream);
            ParseLanguage(langDoc.RootElement, baseExt);
        }

        var iconEntry = archive.GetEntry("icon.png") ?? archive.GetEntry("icon.svg");
        if (iconEntry is not null)
        {
            using var iconStream = iconEntry.Open();
            baseExt.IconBytes = ReadIconBytesFromStream(iconStream);
        }

        var themeEntry = archive.GetEntry("theme.json");
        if (themeEntry is null)
        {
            yield return baseExt;
            yield break;
        }

        // ZipArchiveEntry streams are forward-only - read to memory first so we can
        // enumerate the JSON array without the stream closing under us.
        using var themeStream = themeEntry.Open();
        using var ms = new MemoryStream();
        themeStream.CopyTo(ms);
        ms.Position = 0;
        using var themeDoc = JsonDocument.Parse(ms);
        var root = themeDoc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var themeElement in root.EnumerateArray())
            {
                var def = ParseTheme(themeElement, baseExt);
                var entry = CloneBaseExtension(baseExt);
                entry.ThemeDefinition = def;
                if (index > 0)
                    entry = entry with { Id = $"{baseExt.Id}_{def.ThemeId}", IsThemeSubEntry = true };
                yield return entry;
                index++;
            }
        }
        else
        {
            baseExt.ThemeDefinition = ParseTheme(root, baseExt);
            yield return baseExt;
        }
    }
    private static IEnumerable<string> EnumerateLanguageProfileNames()
    {
        yield return "language.json";
        yield return "language1.json";
        yield return "language2.json";
        yield return "language3.json";
        yield return "language4.json";
        yield return "language5.json";
    }
    private static LoadedExtension ParseManifest(JsonElement manifest) => new()
    {
        Id          = manifest.TryGetProperty("id",          out var id)   ? id.GetString()   ?? "" : "",
        Version     = manifest.TryGetProperty("version",     out var ver)  ? ver.GetString()  ?? "" : "",
        Name        = manifest.TryGetProperty("name",        out var name) ? name.GetString() ?? "" : "",
        Type        = manifest.TryGetProperty("type",        out var type) ? type.GetString() ?? "" : "",
        Author      = manifest.TryGetProperty("author",      out var auth) ? auth.GetString() ?? "" : "",
        Description = manifest.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
        Extensions  = manifest.TryGetProperty("extensions",  out var exts)
            ? exts.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [],
        PluginAssemblyFileName = manifest.TryGetProperty("plugin", out var plugin) ? plugin.GetString() : null
    };

    private static void ParseLanguage(JsonElement lang, LoadedExtension ext)
    {
        var profile = ParseLanguageProfile(lang);
        if (profile.Extensions.Length > 0)
        {
            ext.SyntaxProfiles.Add(profile);
            return;
        }

        ApplyLanguageProfile(ext, profile);
    }

    private static LanguageSyntaxProfile ParseLanguageProfile(JsonElement lang)
    {
        var profile = new LanguageSyntaxProfile
        {
            Extensions = ReadStringArray(lang, "extensions"),
            Keywords = ReadStringArray(lang, "keywords"),
            Types = ReadStringArray(lang, "types"),
            Functions = ReadStringArray(lang, "functions"),
            Properties = ReadStringArray(lang, "properties"),
            Namespaces = ReadStringArray(lang, "namespaces"),
            Blacklist = ReadStringArray(lang, "blacklist"),
            CommentLine = lang.TryGetProperty("commentLine", out var cl) ? NormalizeSyntaxToken(cl.GetString()) : null,
            CommentBlockStart = lang.TryGetProperty("commentBlockStart", out var cbs) ? NormalizeSyntaxToken(cbs.GetString()) : null,
            CommentBlockEnd = lang.TryGetProperty("commentBlockEnd", out var cbe) ? NormalizeSyntaxToken(cbe.GetString()) : null,
            StringDelimiters = lang.TryGetProperty("stringDelimiters", out var sd) ? ReadStringArray(sd) : null,
            MultiLineStringDelimiters = lang.TryGetProperty("multiLineStringDelimiters", out var msd) ? ReadStringArray(msd) : null,
            DisableSingleQuoteStrings = lang.TryGetProperty("disableSingleQuoteStrings", out var dsqs) && dsqs.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? dsqs.GetBoolean()
                : null,
            ColorTokens = ReadColorTokens(lang)
        };

        return profile;
    }

    private static void ApplyLanguageProfile(LoadedExtension ext, LanguageSyntaxProfile profile)
    {
        ext.Keywords = ext.Keywords.Union(profile.Keywords).ToArray();
        ext.Types = ext.Types.Union(profile.Types).ToArray();
        ext.Functions = ext.Functions.Union(profile.Functions).ToArray();
        ext.Properties = ext.Properties.Union(profile.Properties).ToArray();
        ext.Namespaces = ext.Namespaces.Union(profile.Namespaces).ToArray();
        ext.Blacklist = ext.Blacklist.Union(profile.Blacklist).ToArray();

        if (profile.CommentLine is not null)
            ext.CommentLine = profile.CommentLine;
        if (profile.CommentBlockStart is not null)
            ext.CommentBlockStart = profile.CommentBlockStart;
        if (profile.CommentBlockEnd is not null)
            ext.CommentBlockEnd = profile.CommentBlockEnd;
        if (profile.StringDelimiters is not null)
            ext.StringDelimiters = profile.StringDelimiters.ToArray();
        if (profile.MultiLineStringDelimiters is not null)
            ext.MultiLineStringDelimiters = profile.MultiLineStringDelimiters.ToArray();
        if (profile.DisableSingleQuoteStrings.HasValue)
            ext.DisableSingleQuoteStrings = profile.DisableSingleQuoteStrings.Value;

        foreach (var (key, value) in profile.ColorTokens)
            ext.ColorTokens[key] = value;
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) ? ReadStringArray(value) : [];

    private static string? NormalizeSyntaxToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] ReadStringArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToArray()
            : [];

    private static Dictionary<string, string> ReadColorTokens(JsonElement lang)
    {
        var colorTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!lang.TryGetProperty("colorTokens", out var ct) || ct.ValueKind != JsonValueKind.Object)
            return colorTokens;

        foreach (var prop in ct.EnumerateObject())
            colorTokens[prop.Name] = prop.Value.GetString() ?? "#FFFFFF";

        return colorTokens;
    }

    private static ExtensionThemeDefinition ParseTheme(JsonElement theme, LoadedExtension ext) => new()
    {
        ThemeId = theme.TryGetProperty("themeId", out var themeId) ? themeId.GetString() ?? ext.Id : ext.Id,
        DisplayName = theme.TryGetProperty("displayName", out var displayName) ? displayName.GetString() ?? ext.Name : ext.Name,
        BaseTheme = theme.TryGetProperty("baseTheme", out var baseTheme) ? baseTheme.GetString() ?? "Dark" : "Dark",
        WindowBackground = GetThemeColor(theme, "windowBackground", "#000000"),
        TopBar = GetThemeColor(theme, "topBar", "#0E0E0E"),
        Sidebar = GetThemeColor(theme, "sidebar", "#0E0E0E"),
        Button = GetThemeColor(theme, "button", "#242424"),
        ButtonHover = GetThemeColor(theme, "buttonHover", "#343434"),
        EditorBackground = GetThemeColor(theme, "editorBackground", "#000000"),
        Card = GetThemeColor(theme, "card", "#121212"),
        PrimaryText = GetThemeColor(theme, "primaryText", "#FFFFFF"),
        MutedText = GetThemeColor(theme, "mutedText", "#BDBDBD"),
        SurfaceBorder = GetThemeColor(theme, "surfaceBorder", "#4A4A4A"),
        Accent = GetThemeColor(theme, "accent", "#8C00FF"),
        PreviewBackground = GetThemeColor(theme, "previewBackground", GetThemeColor(theme, "editorBackground", "#000000")),
        PreviewBorder = GetThemeColor(theme, "previewBorder", GetThemeColor(theme, "surfaceBorder", "#4A4A4A"))
    };

    private static string GetThemeColor(JsonElement theme, string propertyName, string fallback) =>
        theme.TryGetProperty(propertyName, out var value) ? value.GetString() ?? fallback : fallback;

    private void RefreshExtensionTheme()
    {
        foreach (var ext in LoadedExtensions)
        {
            ext.AccentBrush        = AccentBrush;
            ext.CardBrush          = CardBrush;
            ext.PrimaryTextBrush   = PrimaryTextBrush;
            ext.SurfaceBorderBrush = SurfaceBorderBrush;
            ext.MutedTextBrush     = MutedTextBrush;
            ext.NotifyAllBrushesChanged();
        }

        // Keeps the active-theme dot in sync after brushes refresh, covering newly-added LoadedExtension instances.
        foreach (var ext in ThemeExtensions)
            ext.IsActiveTheme = string.Equals(ext.ThemeCardThemeId, _currentThemeName, StringComparison.OrdinalIgnoreCase);
    }

    private LoadedExtension? GetLanguageExtension(string filePath)
    {
        if (IsPlainTextFile(filePath))
            return null;

        var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
        var extension = LoadedExtensions.FirstOrDefault(e =>
            e.Type == "language" &&
            e.Extensions.Any(ex => ex.Equals(fileExt, StringComparison.OrdinalIgnoreCase)));

        if (extension is null)
        {
            // No extension matched - try to detect the language from file content.
            // Result is cached per path so we only read the file once per session.
            if (!_contentSniffCache.TryGetValue(filePath, out var sniffed))
            {
                sniffed = TryDetectLanguageFromContent(filePath);
                _contentSniffCache[filePath] = sniffed;
            }
            if (sniffed is null)
                return null;

            // Content-sniffed match: use the base extension as-is (no profile narrowing).
            return sniffed;
        }

        var matchingProfiles = extension.SyntaxProfiles
            .Where(profile => profile.Extensions.Any(ex => ex.Equals(fileExt, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matchingProfiles.Count == 0)
            return extension;

        var effectiveExtension = CloneBaseExtension(extension);
        if (matchingProfiles.Any(profile => profile.Keywords.Length > 0))
            effectiveExtension.Keywords = [];
        if (matchingProfiles.Any(profile => profile.Types.Length > 0))
            effectiveExtension.Types = [];
        if (matchingProfiles.Any(profile => profile.Functions.Length > 0))
            effectiveExtension.Functions = [];
        if (matchingProfiles.Any(profile => profile.Properties.Length > 0))
            effectiveExtension.Properties = [];
        if (matchingProfiles.Any(profile => profile.Namespaces.Length > 0))
            effectiveExtension.Namespaces = [];

        foreach (var profile in matchingProfiles)
            ApplyLanguageProfile(effectiveExtension, profile);

        return effectiveExtension;
    }

    // Peeks at the first line to match known XML/MSBuild root elements, so ambiguous files still get highlighting.
    private LoadedExtension? TryDetectLanguageFromContent(string filePath)
    {
        try
        {
            string? firstLine = null;
            using (var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        firstLine = trimmed;
                        break;
                    }
                }
            }

            if (firstLine is null)
                return null;

            // Map root-element signatures to a representative extension that an installed
            // language extension already claims, so we reuse the full profile lookup.
            string? syntheticExt = null;

            if (firstLine.StartsWith("<Project", StringComparison.OrdinalIgnoreCase))
                syntheticExt = ".csproj";
            else if (firstLine.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                     firstLine.StartsWith("<", StringComparison.OrdinalIgnoreCase))
                syntheticExt = ".xml";

            if (syntheticExt is null)
                return null;

            return LoadedExtensions.FirstOrDefault(e =>
                e.Type == "language" &&
                e.Extensions.Any(ex => ex.Equals(syntheticExt, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlainTextFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".text", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMarkdownFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImagePreviewFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ImagePreviewExtensions.Contains(ext);
    }

    // Binary detection: sample up to 8 KB and flag null bytes, the standard git/editor heuristic.
    private static bool IsBinaryContent(string path)
    {
        try
        {
            const int sampleSize = 8192;
            Span<byte> buffer = stackalloc byte[sampleSize];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = fs.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0x00)
                    return true;
            }
            return false;
        }
        catch
        {
            // If we can't read the file at all, treat it as corrupted.
            return true;
        }
    }

    private static Bitmap? TryLoadImagePreview(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || !IsImagePreviewFile(filePath))
            return null;

        try
        {
            using var stream = File.OpenRead(filePath);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private bool IsPlainTextMode()
    {
        if (HasImagePreview)
            return true;

        if (IsPlainTextFile(_currentFilePath))
            return true;

        return _currentFilePath is null && _hasUntitledDocument;
    }

    private bool IsSmartSyntaxEnabled() =>
        !IsPlainTextMode() &&
        HasFileOpen &&
        ActiveEditorTab is { IsUntitled: false };

    // Theme / editor appearance

    private void ApplyThemeToEditor()
    {
        if (EditorTextBox is null) return;
        EditorTextBox.Background = EditorBackgroundBrush;
        EditorTextBox.Foreground = PrimaryTextBrush;
        EditorTextBox.LineNumbersForeground = MutedTextBrush;
        EditorTextBox.TextArea.SelectionBrush = AccentBrush.ToImmutable() is ISolidColorBrush b
            ? new SolidColorBrush(b.Color, 0.3)
            : new SolidColorBrush(Color.Parse("#8C00FF"), 0.3);
        EditorTextBox.TextArea.SelectionForeground = PrimaryTextBrush;
        EditorTextBox.TextArea.TextView.LinkTextForegroundBrush = Brush.Parse("#5BA3D9");
        EditorTextBox.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;
        _indentGuideRenderer.GuideBrush = MutedTextBrush.ToImmutable() is ISolidColorBrush mutedBrush
            ? new SolidColorBrush(mutedBrush.Color, 0.4)
            : new SolidColorBrush(Color.Parse("#808080"), 0.4);
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

    }

    private void ApplyEditorSettings()
    {
        if (EditorTextBox is null)
        {
            return;
        }

        EditorTextBox.WordWrap = IsWordWrapEnabled;
        EditorTextBox.Options.IndentationSize = TabSize;
        EditorTextBox.FontSize = EditorFontSize;
        _indentGuideRenderer.TabSize = TabSize;
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

    }

    private void ApplySyntaxHighlighting(LoadedExtension ext)
    {
        if (EditorTextBox is null) return;
        var syntaxProfile = ResolveCompiledSyntaxProfile(ext);
        if (!_highlightingCache.TryGetValue(ext, out var definition))
        {
            definition = new KodoHighlightingDefinition(ext, syntaxProfile);
            _highlightingCache[ext] = definition;
        }
        EditorTextBox.SyntaxHighlighting = definition;
        ConfigureRainbowBrackets(ext);
        ConfigureInterpolatedStrings(syntaxProfile);
        ConfigureHtmlEmbeddedHighlighting(ext);
        ConfigureMarkdownHighlighting(ext);
    }

    private void RefreshCurrentFileSyntaxHighlighting()
    {
        if (EditorTextBox is null)
            return;

        RefreshRunBuildState();

        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            CurrentLanguageExtension = null;
            ClearEditorSyntaxState();
            _indentGuideRenderer.IsEnabled = false;
            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return;
        }

        var langExt = GetLanguageExtension(_currentFilePath);
        CurrentLanguageExtension = langExt;

        // Indent guides are only meaningful when a language extension is active.
        // Plain-text files (.txt / .log / .text) return null from GetLanguageExtension.
        _indentGuideRenderer.IsEnabled = langExt is not null;
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        if (langExt is null)
        {
            ClearEditorSyntaxState();
        }
        else
            ApplySyntaxHighlighting(langExt);
    }

    private void UpdateCurrentDocumentPresentation()
    {
        var imagePreview = TryLoadImagePreview(_currentFilePath);
        if (!ReferenceEquals(CurrentImagePreview, imagePreview))
            ImageZoomLevel = 1.0;
        CurrentImagePreview = imagePreview;

        if (imagePreview is not null)
        {
            SetFileCorrupted(false);
            CurrentLanguageExtension = null;
            ClearEditorSyntaxState();
            return;
        }

        RefreshCurrentFileSyntaxHighlighting();
    }

    private void ClearEditorSyntaxState()
    {
        if (EditorTextBox is null)
            return;

        EditorTextBox.SyntaxHighlighting = null;
        ConfigureRainbowBrackets(null);
        ConfigureInterpolatedStrings(null);
        ConfigureHtmlEmbeddedHighlighting(null);
        ConfigureMarkdownHighlighting(null);
    }

    private CompiledSyntaxProfile ResolveCompiledSyntaxProfile(LoadedExtension extension)
    {
        if (_compiledSyntaxProfileCache.TryGetValue(extension, out var cached))
            return cached;

        var profile = CompiledSyntaxProfile.Create(extension);
        _compiledSyntaxProfileCache[extension] = profile;
        return profile;
    }

    private void ConfigureHtmlEmbeddedHighlighting(LoadedExtension? extension)
    {
        _htmlEmbeddedColorizer.UpdateSyntax(extension, ResolveHtmlEmbeddedSyntaxProfile);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private void ConfigureMarkdownHighlighting(LoadedExtension? extension)
    {
        _markdownColorizer.UpdateSyntax(extension, ResolveFenceLanguageSyntaxProfile, ResolveInlineCodeLanguageExtension);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private CompiledSyntaxProfile? ResolveHtmlEmbeddedSyntaxProfile(string blockTag, string? typeAttribute)
    {
        static bool ContainsToken(string value, params string[] needles) =>
            needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

        var normalizedTag = blockTag.Trim().ToLowerInvariant();
        var normalizedType = (typeAttribute ?? string.Empty).Trim();

        if (normalizedTag == "x:code")
            return FindLanguageSyntaxProfileForFileExtension(".cs");

        if (normalizedTag == "style")
            return FindLanguageSyntaxProfileForFileExtension(".css");

        if (normalizedTag != "script")
            return null;

        if (string.IsNullOrWhiteSpace(normalizedType) ||
            string.Equals(normalizedType, "module", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "text/javascript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "application/javascript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "application/ecmascript", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "text/ecmascript", StringComparison.OrdinalIgnoreCase))
        {
            return FindLanguageSyntaxProfileForFileExtension(".js");
        }

        if (ContainsToken(normalizedType, "typescript"))
            return FindLanguageSyntaxProfileForFileExtension(".ts");

        if (ContainsToken(normalizedType, "json", "importmap"))
            return FindLanguageSyntaxProfileForFileExtension(".json");

        if (ContainsToken(normalizedType, "javascript", "ecmascript", "jscript"))
            return FindLanguageSyntaxProfileForFileExtension(".js");

        if (ContainsToken(normalizedType, "css"))
            return FindLanguageSyntaxProfileForFileExtension(".css");

        return null;
    }

    private CompiledSyntaxProfile? FindLanguageSyntaxProfileForFileExtension(string extension)
    {
        var loadedExtension = LoadedExtensions.FirstOrDefault(loadedExtension =>
            string.Equals(loadedExtension.Type, "language", StringComparison.OrdinalIgnoreCase) &&
            loadedExtension.Extensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)));

        return loadedExtension is null ? null : ResolveCompiledSyntaxProfile(loadedExtension);
    }

    private CompiledSyntaxProfile? ResolveFenceLanguageSyntaxProfile(string fenceLanguage)
    {
        var extension = ResolveFenceLanguageExtension(fenceLanguage);
        return extension is null ? null : ResolveCompiledSyntaxProfile(extension);
    }

    private LoadedExtension? ResolveFenceLanguageExtension(string fenceLanguage)
    {
        if (string.IsNullOrWhiteSpace(fenceLanguage))
            return null;

        var token = fenceLanguage.Trim();
        if (token.StartsWith("{", StringComparison.Ordinal) && token.EndsWith("}", StringComparison.Ordinal) && token.Length > 2)
            token = token[1..^1];

        token = token.Split([' ', '\t', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim().TrimStart('.').ToLowerInvariant();
        if (FenceLanguageAliases.TryGetValue(normalized, out var alias))
        {
            if (string.IsNullOrEmpty(alias))
                return null; // explicit plain-text marker - no syntax profile
            normalized = alias;
        }

        var bestMatch = LoadedExtensions
            .Where(extension =>
                extension.Type == "language" &&
                !KodoExtensionIds.IsMarkdown(extension.Id))
            .Select(extension => new
            {
                Extension = extension,
                Score = ScoreFenceLanguageMatch(extension, normalized, token)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Extension.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return bestMatch?.Extension;
    }

    private static int ScoreFenceLanguageMatch(LoadedExtension extension, string normalizedFenceLanguage, string rawFenceLanguage)
    {
        if (string.IsNullOrWhiteSpace(normalizedFenceLanguage))
            return 0;

        var score = 0;
        var normalizedCompact = normalizedFenceLanguage.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        var rawCompact = rawFenceLanguage.Trim().TrimStart('.').Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (extension.Extensions.Any(ext =>
                ext.TrimStart('.').Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 100);
        }

        var normalizedId = extension.Id
            .Replace("-kodo-extension", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-language-support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalizedId.Equals(normalizedCompact, StringComparison.OrdinalIgnoreCase) ||
            normalizedId.Equals(rawCompact, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 90);
        }

        var compactName = extension.Name
            .Replace("Language Support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (compactName.Equals(normalizedCompact, StringComparison.OrdinalIgnoreCase) ||
            compactName.Equals(rawCompact, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 80);
        }

        if (extension.SyntaxProfiles.Any(profile =>
                profile.Extensions.Any(ext =>
                    ext.TrimStart('.').Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase))))
        {
            score = Math.Max(score, 70);
        }

        if (extension.Types.Any(type =>
                type.Equals(rawFenceLanguage, StringComparison.OrdinalIgnoreCase) ||
                type.Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 60);
        }

        if (extension.Keywords.Any(keyword =>
                keyword.Equals(rawFenceLanguage, StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 40);
        }

        return score;
    }

    // Inline-code language detection now lives in SyntaxColorEngine.cs; this just supplies the loaded extensions.
    private LoadedExtension? ResolveInlineCodeLanguageExtension(string codeSnippet) =>
        InlineCodeLanguageDetector.Resolve(LoadedExtensions, codeSnippet);

    // Sets the corrupted/unsupported state and fires all dependent property notifications.
    private void SetFileCorrupted(bool corrupted)
    {
        if (_isFileCorrupted == corrupted) return;
        _isFileCorrupted = corrupted;
        OnPropertyChanged(nameof(IsCorruptedFileViewVisible));
        OnPropertyChanged(nameof(IsTextEditorVisible));
        OnPropertyChanged(nameof(CanShowFindInFile));
        OnPropertyChanged(nameof(CanShowSearchPanel));
        OnPropertyChanged(nameof(IsSearchPanelActive));
        OnPropertyChanged(nameof(CanShowSaveActions));
    }

    private void ConfigureRainbowBrackets(LoadedExtension? ext)
    {
        // Rainbow brackets have no meaning in plain text or markdown prose.
        // Markdown fenced code blocks are colourised by _markdownColorizer independently.
        var isMarkdown = KodoExtensionIds.IsMarkdown(ext?.Id);
        _rainbowBracketColorizer.UpdateSyntax(isMarkdown ? null : ext);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private void ConfigureInterpolatedStrings(CompiledSyntaxProfile? syntaxProfile)
    {
        _interpolatedStringColorizer.UpdateSyntax(syntaxProfile);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    // Properties

    public bool IsSettingsPageVisible
    {
        get => _isSettingsPageVisible;
        set
        {
            if (_isSettingsPageVisible == value) return;
            _isSettingsPageVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditorPageVisible));
            OnPropertyChanged(nameof(IsSearchPanelActive));
        }
    }

    public bool IsExtensionsPageVisible
    {
        get => _isExtensionsPageVisible;
        set
        {
            if (_isExtensionsPageVisible == value) return;
            _isExtensionsPageVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditorPageVisible));
            OnPropertyChanged(nameof(IsSearchPanelActive));
        }
    }

    public bool IsTutorialPageVisible
    {
        get => _isTutorialPageVisible;
        set
        {
            if (_isTutorialPageVisible == value) return;
            _isTutorialPageVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditorPageVisible));
            OnPropertyChanged(nameof(IsSearchPanelActive));
        }
    }

    public bool IsWhatsNewPageVisible
    {
        get => _isWhatsNewPageVisible;
        set
        {
            if (_isWhatsNewPageVisible == value) return;
            _isWhatsNewPageVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditorPageVisible));
            OnPropertyChanged(nameof(IsSearchPanelActive));
        }
    }

    public bool IsHomePageVisible
    {
        get => _isHomePageVisible;
        set
        {
            if (_isHomePageVisible == value) return;
            _isHomePageVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmptyStateVisible));
            OnPropertyChanged(nameof(IsDocumentViewVisible));
            OnPropertyChanged(nameof(FileSummaryText));
            OnPropertyChanged(nameof(FilePathText));
            OnPropertyChanged(nameof(LanguageDisplayText));
            OnPropertyChanged(nameof(CanShowSaveActions));
        }
    }

    public bool IsWhatsNewExpanded
    {
        get => _isWhatsNewExpanded;
        set
        {
            if (_isWhatsNewExpanded == value) return;
            _isWhatsNewExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WhatsNewToggleText));
            OnPropertyChanged(nameof(WhatsNewToggleGlyph));
        }
    }

    // Master visibility flag for the opening splash (release notes and/or consent ask).
    public bool IsUpdateSplashVisible
    {
        get => _isUpdateSplashVisible;
        private set
        {
            if (_isUpdateSplashVisible == value) return;
            _isUpdateSplashVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReleaseNotesSectionVisible));
            OnPropertyChanged(nameof(OpeningSplashTitleText));
            OnPropertyChanged(nameof(OpeningSplashSubtitleText));
        }
    }

    // Gates the release-notes section of the opening splash; false means a consent-only showing.
    public bool IsReleaseNotesSectionVisible => _isUpdateSplashVisible && _openingSplashShowsReleaseNotes;

    public string OpeningSplashTitleText => _openingSplashShowsReleaseNotes
        ? "What's New in Kodo"
        : "Help Improve Kodo";

    public string OpeningSplashSubtitleText => _openingSplashShowsReleaseNotes
        ? LatestReleaseDisplayName
        : "One quick question before you get back to it.";

    // Backs the "Help improve Kodo" consent checkbox; any interaction counts as answered.
    public bool IsDataTrackingEnabled
    {
        get => _isDataTrackingEnabled;
        set
        {
            var valueChanged = _isDataTrackingEnabled != value;
            _isDataTrackingEnabled = value;
            AptabaseClient.SetEnabled(value);

            if (valueChanged)
                OnPropertyChanged();

            if (!_hasRespondedToDataTrackingPrompt)
            {
                _hasRespondedToDataTrackingPrompt = true;
                OnPropertyChanged(nameof(IsDataTrackingPromptVisible));
                OnPropertyChanged(nameof(AreAllConsentPromptsResolved));
            }

            SaveSettings();
        }
    }

    // True until the user has answered the consent prompt at least once.
    public bool IsDataTrackingPromptVisible => !_hasRespondedToDataTrackingPrompt;

    // True until the user has scrolled through and accepted the embedded Privacy Policy.
    // Unlike the data-tracking card, there's no decline path - this only ever flips true.
    public bool IsPrivacyPolicyPromptVisible => !_hasAcceptedPrivacyPolicy;

    // Gates the update splash's plain "Got it" dismiss button - only shown once neither
    // consent card has anything left pending.
    public bool AreAllConsentPromptsResolved => !IsDataTrackingPromptVisible && !IsPrivacyPolicyPromptVisible;

    // Flips true once the bound Privacy Policy ScrollViewer reaches the bottom of its
    // extent (see PrivacyPolicyScrollViewer_OnScrollChanged); gates the Accept button.
    public bool IsPrivacyPolicyScrolledToBottom
    {
        get => _isPrivacyPolicyScrolledToBottom;
        set
        {
            if (_isPrivacyPolicyScrolledToBottom == value) return;
            _isPrivacyPolicyScrolledToBottom = value;
            OnPropertyChanged();
        }
    }

    // Resets the scroll gate so re-showing the card (fresh tutorial run, or a returning-user
    // splash) always requires scrolling again before Accept is enabled.
    private void ResetPrivacyPolicyScrollState() => IsPrivacyPolicyScrolledToBottom = false;

    // Lazily loads PRIVACY_POLICY.txt, bundled as an AvaloniaResource under Assets/, so it can
    // render in-app instead of only linking out to the canonical hosted copy on GitHub.
    public string PrivacyPolicyText
    {
        get
        {
            if (_privacyPolicyText is not null)
                return _privacyPolicyText;

            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://Kodo/Assets/PRIVACY_POLICY.txt"));
                using var reader = new System.IO.StreamReader(stream);
                _privacyPolicyText = reader.ReadToEnd();
            }
            catch
            {
                _privacyPolicyText = "The Privacy Policy could not be loaded. You can read it at " +
                                      PrivacyPolicyUrl;
            }

            return _privacyPolicyText;
        }
    }

    public double ExplorerPanelWidth
    {
        get => _explorerPanelWidth;
        set
        {
            var normalized = NormalizeExplorerPanelWidth(value);
            if (_explorerPanelWidth == normalized) return;
            _explorerPanelWidth = normalized;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private const double MinExplorerPanelWidth = 180;
    private const double MaxExplorerPanelWidth = 600;

    private static double NormalizeExplorerPanelWidth(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinExplorerPanelWidth, MaxExplorerPanelWidth)
            : AppSettings.DefaultExplorerPanelWidth;

    // Manual drag handling, mirroring TerminalPanelSplitter_OnPointer* - the splitter
    // sits on the panel's right edge, so dragging right grows it.
    private void ExplorerPanelSplitter_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not InputElement element) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;

        _isResizingExplorerPanel = true;
        _explorerPanelDragStartPointerX = e.GetPosition(this).X;
        _explorerPanelDragStartWidth = ExplorerPanelWidth;
        e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void ExplorerPanelSplitter_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        var deltaX = e.GetPosition(this).X - _explorerPanelDragStartPointerX;
        ExplorerPanelWidth = _explorerPanelDragStartWidth + deltaX;
        e.Handled = true;
    }

    private void ExplorerPanelSplitter_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        _isResizingExplorerPanel = false;
        e.Pointer.Capture(null);
        // Flushes the final width immediately instead of waiting on the debounce timer, in case of a quick resize-then-close.
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    // Guards against a stray PointerCaptureLost leaving the panel stuck in resize mode.
    private void ExplorerPanelSplitter_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        _isResizingExplorerPanel = false;
        SaveSettings(immediate: true);
    }

    // Chevron (16) + its margin (2) + icon viewbox (32) + icon/name spacing (6) +
    // ItemsControl margin (8) + a little breathing room so text never touches the
    // scrollbar. Shared with ExplorerItemNameMaxWidthConverter so the live column
    // width and the auto-fit width agree on the same chrome.
    internal const double FileTreeRowFixedOverhead = 16 + 2 + 32 + 6 + 8 + 10;

    // Double-click the splitter to snap the panel to fit the widest currently-visible
    // entry, the way VS Code's sidebar splitter does.
    private void ExplorerPanelSplitter_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ExplorerPanelWidth = ComputeAutoFitExplorerPanelWidth();
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private double ComputeAutoFitExplorerPanelWidth()
    {
        if (FileTreeItems.Count == 0) return AppSettings.DefaultExplorerPanelWidth;

        var typeface = new Typeface("Cascadia Code,Consolas,Menlo,Monospace");
        var widest = 0.0;

        foreach (var item in FileTreeItems)
        {
            var formatted = new FormattedText(
                item.Name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black);

            var total = item.IndentWidth + formatted.Width;
            if (total > widest) widest = total;
        }

        return NormalizeExplorerPanelWidth(widest + FileTreeRowFixedOverhead);
    }

    public bool IsFileExplorerVisible
    {
        get => _isFileExplorerVisible;
        private set
        {
            if (_isFileExplorerVisible == value) return;
            _isFileExplorerVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsEditorPageVisible => !IsSettingsPageVisible && !IsExtensionsPageVisible && !IsTutorialPageVisible && !IsWhatsNewPageVisible;

    public bool HasDocumentOpen => _currentFilePath is not null || _hasUntitledDocument;

    public bool HasOpenEditors => OpenTabs.Count > 0;

    public bool HasMultipleOpenEditors => OpenTabs.Count > 1;

    public bool IsDocumentViewVisible => HasDocumentOpen && IsEditorPageVisible && !IsHomePageVisible;

    public bool HasImagePreview => CurrentImagePreview is not null;

    public bool IsImagePreviewVisible => IsDocumentViewVisible && HasImagePreview;

    public bool IsCorruptedFileViewVisible => IsDocumentViewVisible && !HasImagePreview && _isFileCorrupted;

    public bool IsTextEditorVisible => IsDocumentViewVisible && !HasImagePreview && !_isFileCorrupted;

    public bool CanShowFindInFile => IsTextEditorVisible;

    public bool CanShowSearchPanel => CanShowFindInFile || IsFolderOpen;

    public bool IsSearchPanelActive => IsSearchPanelVisible && CanShowSearchPanel && IsEditorPageVisible;

    public bool IsEditorTabsVisible => OpenTabs.Count >= 1 && IsEditorPageVisible && !IsHomePageVisible;

    public bool CanShowSaveActions => IsTextEditorVisible;

    public string WhatsNewToggleText => IsWhatsNewExpanded ? "Hide release notes" : "Show release notes";

    public string WhatsNewToggleGlyph => IsWhatsNewExpanded ? "▾" : "▸";

    public bool HasFileOpen => _currentFilePath is not null;

    public bool IsFolderOpen => _currentFolderPath is not null;

    public bool IsEmptyStateVisible => IsHomePageVisible || (IsEditorPageVisible && !HasDocumentOpen);

    public bool HasRecentFiles => RecentFiles.Count > 0;

    // Sub-entries from multi-theme arrays are hidden from the Installed list
    public IEnumerable<LoadedExtension> VisibleLoadedExtensions =>
        LoadedExtensions.Where(e => !e.IsThemeSubEntry);

    // Only the true "nothing installed" empty state - hidden while a search is
    // active so it can't stomp on the "no matches" state below.
    public bool IsNoExtensionsVisible =>
        _selectedInstalledContentFilter != InstalledContentFilters.Compilers &&
        !VisibleLoadedExtensions.Any() && string.IsNullOrWhiteSpace(_extensionSearchText);

    public int InstalledExtensionsCount => VisibleLoadedExtensions.Count();
    // Installed compilers use their own registry (installed-compilers.json) rather than
    // LoadedExtension records - see SyncCompilerInstallStates - so this counts CompilerExtensions
    // flagged IsInstalled instead of reusing InstalledExtensionsCount.
    public int InstalledCompilersCount => CompilerExtensions.Count(e => e.IsInstalled) + ManualCompilerExtensions.Count;
    // Drives whether the Installed/Marketplace list cards render at all, so an
    // empty result (no installed extensions yet, or a search with zero matches)
    // doesn't leave a hollow, padded card floating above the empty-state message.
    public bool HasVisibleInstalledExtensions => FilteredInstalledExtensions.Any();
    public bool HasVisibleMarketplaceExtensions => FilteredMarketplaceExtensions.Any();
    public bool HasVisibleCompilerExtensions => FilteredCompilerExtensions.Any();
    // Combined visibility for the single Installed-tab list card that now holds both
    // installed extensions and installed compilers as one continuous list.
    public bool HasVisibleInstalledExtensionsOrCompilers =>
        HasVisibleInstalledExtensions || HasVisibleInstalledCompilerExtensions;
    public bool HasVisibleInstalledExtensionsAndCompilers =>
        HasVisibleInstalledExtensions && HasVisibleInstalledCompilerExtensions;

    public IEnumerable<LoadedExtension> ThemeExtensions =>
        LoadedExtensions.Where(e => e.Type.Equals("theme", StringComparison.OrdinalIgnoreCase) && e.ThemeDefinition is not null);

    public bool HasThemeExtensions => ThemeExtensions.Any();


    /// ThemeExtensions grouped by name; multi-theme extensions collapse into one group.
    /// Bind Settings/tutorial theme lists to this instead of <see cref="ThemeExtensions"/>.

    public IEnumerable<ThemeExtensionGroup> GroupedThemeExtensions =>
        ThemeExtensions
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ThemeExtensionGroup(g.Key, g.ToList()));

    public bool HasGroupedThemeExtensions => ThemeExtensions.Any();

    public bool IsRefreshingExtensions
    {
        get => _isRefreshingExtensions;
        private set
        {
            if (_isRefreshingExtensions == value) return;
            _isRefreshingExtensions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefreshExtensionsButtonText));
            OnPropertyChanged(nameof(CanUpdateAllExtensions));
            OnPropertyChanged(nameof(IsMarketplaceUnavailableVisible));
            OnPropertyChanged(nameof(IsMarketplacePartialErrorVisible));
            OnPropertyChanged(nameof(IsMarketplaceEmptyVisible));
        }
    }

    private void SetSelectedExtensionsTab(string tab)
    {
        if (string.Equals(_selectedExtensionsTab, tab, StringComparison.Ordinal)) return;
        _selectedExtensionsTab = tab;
        OnPropertyChanged(nameof(IsInstalledTabSelected));
        OnPropertyChanged(nameof(IsLanguagesTabSelected));
        OnPropertyChanged(nameof(IsThemesTabSelected));
        OnPropertyChanged(nameof(IsPluginsTabSelected));
        OnPropertyChanged(nameof(IsCompilersTabSelected));
        OnPropertyChanged(nameof(IsMarketplaceSectionTabSelected));
        OnPropertyChanged(nameof(SelectedExtensionSort));
        NotifyExtensionFiltersChanged();
    }

    public bool IsInstalledTabSelected
    {
        get => _selectedExtensionsTab == ExtensionsTabModes.Installed;
        private set { if (value) SetSelectedExtensionsTab(ExtensionsTabModes.Installed); }
    }

    public bool IsLanguagesTabSelected
    {
        get => _selectedExtensionsTab == ExtensionsTabModes.Languages;
        private set { if (value) SetSelectedExtensionsTab(ExtensionsTabModes.Languages); }
    }

    public bool IsThemesTabSelected
    {
        get => _selectedExtensionsTab == ExtensionsTabModes.Themes;
        private set { if (value) SetSelectedExtensionsTab(ExtensionsTabModes.Themes); }
    }

    public bool IsPluginsTabSelected
    {
        get => _selectedExtensionsTab == ExtensionsTabModes.Plugins;
        private set { if (value) SetSelectedExtensionsTab(ExtensionsTabModes.Plugins); }
    }

    // Compilers tab, to the right of Plugins - lists standalone toolchain installers
    // (e.g. the Shine compiler) sourced from CompilerIndex.json rather than Kodo extensions.
    public bool IsCompilersTabSelected
    {
        get => _selectedExtensionsTab == ExtensionsTabModes.Compilers;
        private set { if (value) SetSelectedExtensionsTab(ExtensionsTabModes.Compilers); }
    }

    // The Languages/Themes/Plugins tabs share the marketplace browse UI (search, sort, tiles).
    public bool IsMarketplaceSectionTabSelected =>
        _selectedExtensionsTab is ExtensionsTabModes.Languages or ExtensionsTabModes.Themes or ExtensionsTabModes.Plugins;

    // Extension type the active marketplace tab lists; empty on non-marketplace tabs.
    private string ActiveMarketplaceTabType => _selectedExtensionsTab switch
    {
        ExtensionsTabModes.Languages => "language",
        ExtensionsTabModes.Themes => "theme",
        ExtensionsTabModes.Plugins => "plugin",
        _ => string.Empty
    };

    public IReadOnlyList<string> ExtensionSortOptions { get; } =
    [
        ExtensionSortModes.Alphabetical,
        ExtensionSortModes.ReverseAlphabetical,
        ExtensionSortModes.RecentlyInstalled,
        ExtensionSortModes.UpdatesAvailable
    ];

    public string SelectedExtensionSort
    {
        get => IsMarketplaceSectionTabSelected ? _selectedMarketplaceExtensionSort : _selectedInstalledExtensionSort;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? ExtensionSortModes.Alphabetical : value;
            if (IsMarketplaceSectionTabSelected)
            {
                if (string.Equals(_selectedMarketplaceExtensionSort, normalized, StringComparison.Ordinal))
                    return;

                _selectedMarketplaceExtensionSort = normalized;
            }
            else
            {
                if (string.Equals(_selectedInstalledExtensionSort, normalized, StringComparison.Ordinal))
                    return;

                _selectedInstalledExtensionSort = normalized;
            }

            OnPropertyChanged();
            NotifyExtensionFiltersChanged();
        }
    }

    public string ExtensionsStatusText
    {
        get => _extensionsStatusText;
        private set
        {
            if (_extensionsStatusText == value) return;
            _extensionsStatusText = value;
            OnPropertyChanged();
        }
    }

    public string LatestReleaseStatusText
    {
        get => _latestReleaseStatusText;
        private set
        {
            if (_latestReleaseStatusText == value) return;
            _latestReleaseStatusText = value;
            OnPropertyChanged();
        }
    }

    public ReleaseInfo? LatestRelease
    {
        get => _latestRelease;
        private set
        {
            if (_latestRelease == value) return;
            _latestRelease = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLatestRelease));
            OnPropertyChanged(nameof(LatestReleaseDisplayName));
            OnPropertyChanged(nameof(LatestReleaseTag));
            OnPropertyChanged(nameof(LatestReleaseNotes));
            OnPropertyChanged(nameof(LatestReleaseFormatted));
            OnPropertyChanged(nameof(LatestReleasePreview));
            OnPropertyChanged(nameof(LatestReleaseUrl));
            OnPropertyChanged(nameof(LatestReleaseLinks));
            OnPropertyChanged(nameof(HasLatestReleaseLinks));
            OnPropertyChanged(nameof(IsNewerVersionAvailable));
            OnPropertyChanged(nameof(IsAppUpdateAvailable));
        }
    }

    public bool HasLatestRelease => LatestRelease is not null;

    public string CurrentAppVersionDisplay => CurrentAppVersion;

    private bool _updateBannerDismissed;
    private bool _extensionUpdateBannerDismissed;

    // Returns true if the current build is a -DEV build.
    // DEV builds suppress all app update UI (but extension updating still works).
    private static bool IsDevBuild =>
        CurrentAppVersion.Contains("-DEV", StringComparison.OrdinalIgnoreCase);

    // Pre-release suffix ranking: -DEV < -ALPHA < -BETA < unrecognized < -RC < stable.
    private static int VersionPriority(string tag)
    {
        var dash = tag.IndexOf('-');
        if (dash < 0) return 5; // stable: no suffix at all

        var suffix = tag[dash..];
        if (suffix.Contains("dev",   StringComparison.OrdinalIgnoreCase)) return 0;
        if (suffix.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return 1;
        if (suffix.Contains("beta",  StringComparison.OrdinalIgnoreCase)) return 2;
        if (suffix.Contains("rc",    StringComparison.OrdinalIgnoreCase)) return 4;

        return 3; // unrecognized pre-release suffix
    }

    private static string StripPreRelease(string tag)
    {
        var t = tag.TrimStart('v');
        var dash = t.IndexOf('-');
        return dash >= 0 ? t[..dash] : t;
    }

    private static bool IsCurrentNewerThanLastSeen(string lastSeen)
    {
        if (string.IsNullOrWhiteSpace(lastSeen)) return false;

        if (!Version.TryParse(StripPreRelease(lastSeen),          out var seen))    return false;
        if (!Version.TryParse(StripPreRelease(CurrentAppVersion), out var current)) return false;

        if (current != seen) return current > seen;

        // Same numeric version - current is "newer" only if its suffix has higher priority
        // (e.g. upgrading from v1.2.0-BETA to v1.2.0 stable should still show the splash).
        return VersionPriority(CurrentAppVersion) > VersionPriority(lastSeen);
    }

    // Core version check ignores dismissal; -DEV builds never report updates. Priority: stable > beta > dev.
    public bool IsNewerVersionAvailable
    {
        get
        {
            if (IsDevBuild) return false;
            if (!HasLatestRelease || string.IsNullOrWhiteSpace(LatestReleaseTag)) return false;

            if (!Version.TryParse(StripPreRelease(CurrentAppVersion), out var current)) return false;
            if (!Version.TryParse(StripPreRelease(LatestReleaseTag),  out var latest))  return false;

            if (latest != current) return latest > current;

            // Same numeric version - compare by suffix priority
            return VersionPriority(LatestReleaseTag) > VersionPriority(CurrentAppVersion);
        }
    }

    // Banner visibility - collapses when dismissed, reappears if the app restarts.
    public bool IsAppUpdateAvailable => IsNewerVersionAvailable && !_updateBannerDismissed;
    public int AvailableExtensionUpdatesCount => MarketplaceExtensions.Count(e => e.IsUpdateAvailable);
    public bool IsExtensionUpdateBannerVisible => AvailableExtensionUpdatesCount > 0 && !_extensionUpdateBannerDismissed;
    public string ExtensionUpdatesBannerText =>
        $"{AvailableExtensionUpdatesCount} extension{(AvailableExtensionUpdatesCount == 1 ? string.Empty : "s")} {(AvailableExtensionUpdatesCount == 1 ? "has" : "have")} updates available";
    public bool CanUpdateAllExtensions =>
        !IsUpdatingAllExtensions &&
        !IsRefreshingExtensions &&
        !MarketplaceExtensions.Any(e => e.IsInstalling) &&
        MarketplaceExtensions.Any(e => e.IsUpdateAvailable && !e.IsInstalling);
    public string UpdateAllExtensionsButtonText =>
        IsUpdatingAllExtensions
            ? "Updating..."
            : AvailableExtensionUpdatesCount > 0
                ? $"Update All ({AvailableExtensionUpdatesCount})"
                : "Update All";

    public string LatestReleaseDisplayName =>
        !string.IsNullOrWhiteSpace(LatestRelease?.Name)
            ? LatestRelease.Name
            : !string.IsNullOrWhiteSpace(LatestRelease?.Tag)
                ? LatestRelease.Tag
                : "Latest Release";

    public string LatestReleaseTag => LatestRelease?.Tag ?? string.Empty;

    public string LatestReleaseNotes => string.IsNullOrWhiteSpace(LatestRelease?.Notes)
        ? "No release notes available."
        : ConvertMarkdownToDisplayText(LatestRelease.Notes);

    // Structured release notes: paragraphs of bold/normal runs, used by both the Settings and splash templates.
    public IReadOnlyList<FormattedParagraph> LatestReleaseFormatted =>
        string.IsNullOrWhiteSpace(LatestRelease?.Notes)
            ? [new FormattedParagraph { Runs = [new FormattedRun { Text = "No release notes available." }] }]
            : ParseMarkdownParagraphs(LatestRelease.Notes);

    public string LatestReleasePreview
    {
        get
        {
            var notes = LatestReleaseNotes.Replace("\r\n", "\n").Replace('\r', '\n');
            var preview = notes.Length > 220 ? notes[..220].TrimEnd() + "..." : notes;
            return preview;
        }
    }

    public string LatestReleaseUrl => LatestRelease?.Url ?? ReleasesPageUrl;

    public IReadOnlyList<ReleaseLinkItem> LatestReleaseLinks =>
        ExtractReleaseLinks(LatestRelease?.Notes ?? string.Empty);

    public bool HasLatestReleaseLinks => LatestReleaseLinks.Count > 0;

    private static string ConvertMarkdownToDisplayText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        text = MdCodeFenceRegex.Replace(text, string.Empty);
        text = text.Replace("```", string.Empty);
        text = MdInlineCodeRegex.Replace(text, "$1");
        text = MdImageRegex.Replace(text, "$1");
        text = MdLinkRegex.Replace(text, "$1");
        text = MdHeadingRegex.Replace(text, string.Empty);
        text = MdBlockquoteRegex.Replace(text, string.Empty);
        text = MdHrRegex.Replace(text, string.Empty);
        text = MdBulletRegex.Replace(text, "• ");
        text = MdOrderedListRegex.Replace(text, "$1. ");
        text = MdBoldRegex.Replace(text, "$1");
        text = MdItalicRegex.Replace(text, "$1");
        text = MdBoldUnderscoreRegex.Replace(text, "$1");
        text = MdItalicUnderscoreRegex.Replace(text, "$1");
        text = MdStrikethroughRegex.Replace(text, "$1");
        text = MdTableLeadingPipeRegex.Replace(text, string.Empty);
        text = MdTableTrailingPipeRegex.Replace(text, string.Empty);
        text = MdTablePipeRegex.Replace(text, " | ");
        text = MdExcessNewlinesRegex.Replace(text, "\n\n");

        return text.Trim();
    }

    // Pre-compiled regex patterns used by ConvertMarkdownToDisplayText - compiled
    // once at class load time so repeated calls don't recompile on every access.
    private static readonly Regex MdCodeFenceRegex         = new(@"```(?:[\w#+.-]+)?\n?",         RegexOptions.Compiled);
    private static readonly Regex MdInlineCodeRegex        = new(@"`([^`]+)`",                    RegexOptions.Compiled);
    private static readonly Regex MdImageRegex             = new(@"!\[([^\]]*)\]\([^)]+\)",        RegexOptions.Compiled);
    private static readonly Regex MdLinkRegex              = new(@"\[(.*?)\]\((.*?)\)",            RegexOptions.Compiled);
    private static readonly Regex MdHeadingRegex           = new(@"(?m)^\s{0,3}#{1,6}\s*",        RegexOptions.Compiled);
    private static readonly Regex MdBlockquoteRegex        = new(@"(?m)^\s{0,3}>\s?",             RegexOptions.Compiled);
    private static readonly Regex MdHrRegex                = new(@"(?m)^\s*[-*_]{3,}\s*$",        RegexOptions.Compiled);
    private static readonly Regex MdBulletRegex            = new(@"(?m)^\s*[-*+]\s+",             RegexOptions.Compiled);
    private static readonly Regex MdOrderedListRegex       = new(@"(?m)^\s*(\d+)\.\s+",           RegexOptions.Compiled);
    private static readonly Regex MdBoldRegex              = new(@"(?<!\*)\*\*(?!\*)(.*?)\*\*(?<!\*)", RegexOptions.Compiled);
    private static readonly Regex MdItalicRegex            = new(@"(?<!\*)\*(?!\*)(.*?)\*(?<!\*)", RegexOptions.Compiled);
    private static readonly Regex MdBoldUnderscoreRegex    = new(@"__(.*?)__",                    RegexOptions.Compiled);
    private static readonly Regex MdItalicUnderscoreRegex  = new(@"(?<!_)_(?!_)(.*?)(?<!_)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex MdStrikethroughRegex     = new(@"~~(.*?)~~",                    RegexOptions.Compiled);
    private static readonly Regex MdTableLeadingPipeRegex  = new(@"(?m)^\s*\|",                   RegexOptions.Compiled);
    private static readonly Regex MdTableTrailingPipeRegex = new(@"(?m)\|\s*$",                   RegexOptions.Compiled);
    private static readonly Regex MdTablePipeRegex         = new(@"\|",                           RegexOptions.Compiled);
    private static readonly Regex MdExcessNewlinesRegex    = new(@"\n{3,}",                       RegexOptions.Compiled);

    // Matches **bold** and __bold__ spans for inline run splitting.
    private static readonly Regex MdInlineBoldRegex = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled | RegexOptions.Singleline);

    // Parses raw GitHub markdown into bold/normal runs for AXAML rendering.
    private static IReadOnlyList<FormattedParagraph> ParseMarkdownParagraphs(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var raw = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        // Strip code fences entirely.
        raw = MdCodeFenceRegex.Replace(raw, string.Empty);
        raw = raw.Replace("```", string.Empty);

        var paragraphs = new List<FormattedParagraph>();

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd();

            // Skip horizontal rules and separator lines.
            if (MdHrRegex.IsMatch(line)) continue;

            // Blank line → skip (spacing is handled by TopMargin).
            if (string.IsNullOrWhiteSpace(line)) continue;

            var isBullet   = false;
            var isOrdered  = false;
            string? orderedPrefix = null;

            // Bullet list item.
            var bulletMatch = MdBulletRegex.Match(line);
            if (bulletMatch.Success)
            {
                isBullet = true;
                line = line[bulletMatch.Length..];
            }
            else
            {
                // Ordered list item.
                var orderedMatch = MdOrderedListRegex.Match(line);
                if (orderedMatch.Success)
                {
                    isOrdered     = true;
                    orderedPrefix = orderedMatch.Groups[1].Value + ".";
                    line          = line[orderedMatch.Length..];
                }
                else
                {
                    // Heading → strip markers, treat content as bold paragraph.
                    var headingMatch = MdHeadingRegex.Match(line);
                    if (headingMatch.Success)
                        line = line[headingMatch.Length..].Trim();
                }
            }

            // Strip blockquote markers.
            line = MdBlockquoteRegex.Replace(line, string.Empty);

            // Strip images, resolve links to display text only.
            line = MdImageRegex.Replace(line, "$1");
            line = MdLinkRegex.Replace(line, "$1");

            // Strip inline code backticks (keep the text).
            line = MdInlineCodeRegex.Replace(line, "$1");

            // Strip strikethrough.
            line = MdStrikethroughRegex.Replace(line, "$1");

            // Strip italic (single * or _) without consuming bold markers.
            line = MdItalicRegex.Replace(line, "$1");
            line = MdItalicUnderscoreRegex.Replace(line, "$1");

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Splits the line into bold/normal runs; the list marker is tracked separately.
            var runs = new List<FormattedRun>();

            var marker = isBullet ? "•"
                : isOrdered && orderedPrefix is not null ? orderedPrefix
                : string.Empty;

            // Bullets need a narrow column; ordered markers ("1." .. "99.")
            // need a little more room so two-digit numbers don't clip.
            var markerColumnWidth = isBullet ? 18.0
                : isOrdered ? 28.0
                : 0.0;

            var pos = 0;
            foreach (Match m in MdInlineBoldRegex.Matches(line))
            {
                // Normal text before this bold span.
                if (m.Index > pos)
                    runs.Add(new FormattedRun { Text = line[pos..m.Index], IsBold = false });

                // The bold text itself (group 1 = **, group 2 = __).
                var boldText = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(boldText))
                    runs.Add(new FormattedRun { Text = boldText, IsBold = true });

                pos = m.Index + m.Length;
            }

            // Trailing normal text after the last bold span.
            if (pos < line.Length)
                runs.Add(new FormattedRun { Text = line[pos..], IsBold = false });

            if (runs.Count == 0) continue;

            paragraphs.Add(new FormattedParagraph
            {
                Runs              = runs,
                TopMargin         = isBullet || isOrdered ? new Thickness(0, 2, 0, 0) : new Thickness(0, 6, 0, 0),
                Marker            = marker,
                MarkerColumnWidth = markerColumnWidth,
            });
        }

        return paragraphs;
    }

    private static IReadOnlyList<ReleaseLinkItem> ExtractReleaseLinks(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var links = new List<ReleaseLinkItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(markdown, @"\[(.*?)\]\((https?://[^\s)]+)\)"))
        {
            var label = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
                continue;

            links.Add(new ReleaseLinkItem
            {
                Label = string.IsNullOrWhiteSpace(label) ? url : label,
                Url = url
            });
        }

        foreach (Match match in Regex.Matches(markdown, @"https?://[^\s)]+"))
        {
            var url = match.Value.Trim().TrimEnd('.', ',', ';');
            if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
                continue;

            links.Add(new ReleaseLinkItem
            {
                Label = url,
                Url = url
            });
        }

        return links;
    }

    public bool IsRefreshingLatestRelease => _isRefreshingLatestRelease;

    public string RefreshLatestReleaseButtonText => IsRefreshingLatestRelease ? "Refreshing..." : "Refresh";

    public string RefreshExtensionsButtonText => IsRefreshingExtensions ? "Refreshing..." : "Refresh";

    // True when the marketplace has NO entries and there was a connectivity error.
    public bool IsMarketplaceUnavailableVisible => MarketplaceExtensions.Count == 0 && IsMarketplaceConnectivityWarningVisible;
    // True when the marketplace has entries but some failed to load (partial error).
    public bool IsMarketplacePartialErrorVisible => MarketplaceExtensions.Count > 0 && IsMarketplaceConnectivityWarningVisible;

    // Search produced zero results but the underlying list isn't actually empty -
    // distinct from IsNoExtensionsVisible/IsMarketplaceEmptyVisible above, which
    // previously fired (or failed to fire) regardless of the active search text.
    public bool IsInstalledSearchEmptyVisible =>
        _selectedInstalledContentFilter != InstalledContentFilters.Compilers &&
        !string.IsNullOrWhiteSpace(_extensionSearchText) &&
        VisibleLoadedExtensions.Any() &&
        !FilteredInstalledExtensions.Any();

    public bool IsMarketplaceSearchEmptyVisible =>
        !string.IsNullOrWhiteSpace(_extensionSearchText) &&
        MarketplaceExtensions.Any() &&
        !FilteredMarketplaceExtensions.Any();

    // True when the active marketplace tab has no entries of its own type (no search text,
    // no connectivity error, not mid-refresh) - distinct from IsMarketplaceSearchEmptyVisible.
    public bool IsMarketplaceEmptyVisible =>
        !IsRefreshingExtensions &&
        !IsMarketplaceConnectivityWarningVisible &&
        string.IsNullOrWhiteSpace(_extensionSearchText) &&
        !FilteredMarketplaceExtensions.Any();

    public string MarketplaceEmptyStateText => ActiveMarketplaceTabType switch
    {
        "language" => "No language extensions in the Marketplace right now.",
        "theme"    => "No themes in the Marketplace right now.",
        "plugin"   => "No plugins in the Marketplace right now.",
        _          => "The Marketplace has no extensions listed right now."
    };

    public bool IsMarketplaceConnectivityWarningVisible
    {
        get => _isMarketplaceConnectivityWarningVisible;
        private set
        {
            if (_isMarketplaceConnectivityWarningVisible == value) return;
            _isMarketplaceConnectivityWarningVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMarketplaceUnavailableVisible));
            OnPropertyChanged(nameof(IsMarketplacePartialErrorVisible));
            OnPropertyChanged(nameof(IsMarketplaceEmptyVisible));
        }
    }

    // The welcome step (index 0) is not counted in "Step X of Y" - only the content steps are.
    public string TutorialStepLabel => $"Step {TutorialStepIndex} of {TutorialSteps.Length - 1}";

    public string TutorialProgressDotsText =>
        string.Join(" ", Enumerable.Range(0, TutorialSteps.Length).Select(index => index <= TutorialStepIndex ? "●" : "○"));

    public string TutorialSectionTitle => CurrentTutorialStep.SectionTitle;

    public string TutorialTitle => CurrentTutorialStep.Title;

    // Step 5 ("Set up") body contains "accent colour" - swap for US users.
    public string TutorialBody => IsAmericanEnglish
        ? CurrentTutorialStep.Body.Replace("accent colour", "accent color")
        : CurrentTutorialStep.Body;

    public string TutorialShortcutText => CurrentTutorialStep.Shortcut;

    // Swaps the regional wording for the Settings/Setup tutorial step titles.
    public string TutorialSpotlightTitle => TutorialStepIndex switch
    {
        4 => IsAmericanEnglish ? "Personalize the experience"  : "Personalise the experience",
        5 => IsAmericanEnglish ? "Why personalize?"            : "Why personalise?",
        _ => CurrentTutorialStep.SpotlightTitle,
    };

    public string TutorialHighlightOne => CurrentTutorialStep.HighlightOne;

    // Step 4 ("Settings") HighlightTwo contains "tab behavior" - swap for non-US users.
    public string TutorialHighlightTwo => (!IsAmericanEnglish && TutorialStepIndex == 4)
        ? CurrentTutorialStep.HighlightTwo.Replace("tab behavior", "tab behaviour")
        : CurrentTutorialStep.HighlightTwo;

    // Step 5 ("Set up") HighlightThree contains "accent colour" - swap for US users.
    public string TutorialHighlightThree => IsAmericanEnglish
        ? CurrentTutorialStep.HighlightThree.Replace("accent colour", "accent color")
        : CurrentTutorialStep.HighlightThree;

    public bool CanGoToPreviousTutorialStep => TutorialStepIndex > 0;

    // True only on the final "Set up Kodo" step so the AXAML can show
    // interactive personalisation controls instead of the spotlight text panel.
    public bool IsTutorialSetupStep => TutorialStepIndex == TutorialSteps.Length - 1;
    public bool IsNotTutorialSetupStep => !IsTutorialSetupStep;

    // True only on the very first "Welcome to Kodo!" splash step.
    public bool IsTutorialWelcomeStep => TutorialStepIndex == 0;
    public bool IsNotTutorialWelcomeStep => !IsTutorialWelcomeStep;

    // Show the "Tutorial" page header only when opened deliberately from Settings,
    // not on first-launch where the welcome splash is the first thing seen.
    public bool IsTutorialHeaderVisible => _tutorialOpenedFromSettings;

    public string TutorialPrimaryButtonText => TutorialStepIndex >= TutorialSteps.Length - 1 ? "Finish tutorial" : "Next";

    public int TutorialStepIndex
    {
        get => _tutorialStepIndex;
        private set
        {
            var clamped = Math.Clamp(value, 0, TutorialSteps.Length - 1);
            if (_tutorialStepIndex == clamped) return;
            _tutorialStepIndex = clamped;
            OnTutorialStepChanged();
        }
    }

    public string MarketplaceConnectivityMessage
    {
        get => _marketplaceConnectivityMessage;
        private set
        {
            if (string.Equals(_marketplaceConnectivityMessage, value, StringComparison.Ordinal)) return;
            _marketplaceConnectivityMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsDiscordRichPresenceEnabled
    {
        get => _isDiscordRichPresenceEnabled;
        set
        {
            if (_isDiscordRichPresenceEnabled == value) return;
            _isDiscordRichPresenceEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            UpdateDiscordRichPresenceLifecycle();
            OnPropertyChanged(nameof(DiscordRichPresenceStatusText));
            OnPropertyChanged(nameof(IsDiscordImprovedRpcEnabled));
        }
    }

    public bool IsDiscordImprovedRpcEnabled
    {
        get => _isDiscordImprovedRpcEnabled;
        set
        {
            if (_isDiscordImprovedRpcEnabled == value) return;
            _isDiscordImprovedRpcEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            UpdateDiscordPresence();
            OnPropertyChanged(nameof(DiscordRichPresenceStatusText));
        }
    }

    // Developer Options

    public bool IsDeveloperOptionsVisible
    {
        get => _isDeveloperOptionsVisible;
        set
        {
            if (_isDeveloperOptionsVisible == value) return;
            _isDeveloperOptionsVisible = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }


    /// When on, also appends Debug-level traces to kodo.log; useful for diagnosing issues but noisy, so off by default.

    public bool IsVerboseLoggingEnabled
    {
        get => _isVerboseLoggingEnabled;
        set
        {
            if (_isVerboseLoggingEnabled == value) return;
            _isVerboseLoggingEnabled = value;
            KodoDiagnostics.VerboseLoggingEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // Feedback shown under the Developer Options buttons after an action like
    // "Copy Diagnostic Info" or "Clear Logs" completes. Empty until used.
    public string DeveloperOptionsStatusText
    {
        get => _developerOptionsStatusText;
        private set
        {
            if (_developerOptionsStatusText == value) return;
            _developerOptionsStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDeveloperOptionsStatus));
        }
    }

    public bool HasDeveloperOptionsStatus => !string.IsNullOrWhiteSpace(DeveloperOptionsStatusText);

    /// Path to kodo.log (main log), shown in developer options.
    public string MainLogFilePath => KodoDiagnostics.MainLogFilePath;

    /// Path to crash.log, shown in developer options.
    public string CrashLogFilePath => KodoDiagnostics.CrashLogFilePath;

    /// Path to the folder that contains kodo.log and crash.log.    
    public string CrashLogFolderPath => KodoDiagnostics.LogDirectoryPath;

    /// Path to the folder that contains kodosettings.json, shown in the button tooltip.
    public string SettingsFolderPath =>
        Path.GetDirectoryName(SettingsFilePath) ?? string.Empty;

    public bool IsAutoSaveEnabled
    {
        get => _isAutoSaveEnabled;
        set
        {
            if (_isAutoSaveEnabled == value) return;
            _isAutoSaveEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoSaveStatusText));
            if (!_isAutoSaveEnabled)
                _autoSaveTimer.Stop();
            else
                RestartAutoSaveTimerIfNeeded();
            SaveSettings();
        }
    }

    public bool IsStatusBarFilePathVisible
    {
        get => _isStatusBarFilePathVisible;
        set
        {
            if (_isStatusBarFilePathVisible == value) return;
            _isStatusBarFilePathVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilePathText));
            OnPropertyChanged(nameof(LanguageDisplayText));
            OnPropertyChanged(nameof(StatusBarFilePathVisibilityText));
            OnPropertyChanged(nameof(ActiveTerminalWorkingDirectory));
            OnPropertyChanged(nameof(ActiveTerminalFooterText));
            SaveSettings();
        }
    }

    public bool IsWordWrapEnabled
    {
        get => _isWordWrapEnabled;
        set
        {
            if (_isWordWrapEnabled == value) return;
            _isWordWrapEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditorBehaviorStatusText));
            ApplyEditorSettings();
            SaveSettings();
        }
    }

    public bool IsInsightEnabled
    {
        get => _isInsightEnabled;
        set
        {
            if (_isInsightEnabled == value) return;
            _isInsightEnabled = value;
            OnPropertyChanged();
            if (!_isInsightEnabled)
                CloseCompletionWindow();
            SaveSettings();
        }
    }

    public string InsightBlacklistExtensions
    {
        get => _insightBlacklistExtensions;
        set
        {
            if (_insightBlacklistExtensions == value) return;
            _insightBlacklistExtensions = value;
            RebuildInsightBlacklist();
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private void RebuildInsightBlacklist()
    {
        _insightBlacklistSet.Clear();
        foreach (var part in _insightBlacklistExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            _insightBlacklistSet.Add(ext);
        }
    }

    private bool IsInsightBlacklisted(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _insightBlacklistSet.Contains(ext);
    }

    public bool IsPSReadLinePredictionEnabled
    {
        get => _isPSReadLinePredictionEnabled;
        set
        {
            if (_isPSReadLinePredictionEnabled == value) return;
            _isPSReadLinePredictionEnabled = value;
            OnPropertyChanged();
            // Rebuild the shell list so the launch arguments pick up the new setting;
            // sessions already running are unaffected until restarted.
            RefreshAvailableTerminalShells(SelectedTerminalShell?.Id);
            SaveSettings();
        }
    }

    public int TabSize
    {
        get => _tabSize;
        set
        {
            var normalizedValue = NormalizeTabSize(value);
            if (_tabSize == normalizedValue) return;
            _tabSize = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditorBehaviorStatusText));
            ApplyEditorSettings();
            SaveSettings();
        }
    }

    public int TabSizeIndex
    {
        get => TabSize switch
        {
            2 => 0,
            8 => 2,
            _ => 1
        };
        set => TabSize = value switch
        {
            0 => 2,
            2 => 8,
            _ => 4
        };
    }

    public int EditorFontSize
    {
        get => _editorFontSize;
        set
        {
            var clamped = Math.Clamp(value, 8, 32);
            if (_editorFontSize == clamped) return;
            _editorFontSize = clamped;
            OnPropertyChanged();
            ApplyEditorSettings();
            SaveSettings();
        }
    }

    public string ExtensionSearchText
    {
        get => _extensionSearchText;
        set
        {
            if (_extensionSearchText == value) return;
            _extensionSearchText = value;
            OnPropertyChanged();
            NotifyExtensionFiltersChanged();
        }
    }

    public string SettingsSearchText
    {
        get => _settingsSearchText;
        set
        {
            if (_settingsSearchText == value) return;
            _settingsSearchText = value;
            OnPropertyChanged();
            NotifySettingsSearchChanged();
        }
    }

    // Automatic settings search: instead of a hand-maintained keyword list per
    // card, we walk each card's live visual tree and pull out every bit of text
    // a user could actually read (labels, content, headers, tooltips, watermarks) -
    // the same way a search engine indexes a page's rendered text rather than a
    // curated meta-keywords tag. New cards/controls are searchable automatically
    // as soon as they're named; nothing here needs to be updated for them.
    private static void CollectSearchableText(StyledElement element, StringBuilder sb)
    {
        if (element is TextBlock { Text: { Length: > 0 } text })
            sb.Append(text).Append(' ');

        if (element is HeaderedContentControl { Header: string header } && !string.IsNullOrWhiteSpace(header))
            sb.Append(header).Append(' ');

        if (element is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
            sb.Append(content).Append(' ');

        if (element is TextBox { PlaceholderText: { Length: > 0 } placeholder })
            sb.Append(placeholder).Append(' ');

        if (element is Control control && ToolTip.GetTip(control) is string tip && !string.IsNullOrWhiteSpace(tip))
            sb.Append(tip).Append(' ');
    }

    // Rebuilt on every call rather than cached, so status text that changes at
    // runtime (e.g. "Enable Insight" hints, version numbers) stays searchable -
    // these cards are small, so re-walking them per keystroke is cheap.
    private static string GetSettingsCardSearchText(Control? card)
    {
        if (card is null)
            return string.Empty;

        var sb = new StringBuilder();
        CollectSearchableText(card, sb);
        foreach (var descendant in card.GetVisualDescendants())
        {
            if (descendant is StyledElement styled)
                CollectSearchableText(styled, sb);
        }

        return sb.ToString();
    }

    private bool MatchesSettingsSearchCard(Control? card)
    {
        return string.IsNullOrWhiteSpace(_settingsSearchText) ||
               GetSettingsCardSearchText(card).Contains(_settingsSearchText, StringComparison.OrdinalIgnoreCase);
    }

    // Search active, but every card was filtered out - lets the empty-state
    // placeholder tell the difference from "Settings just hasn't loaded yet".
    private bool _isSettingsSearchEmpty;
    public bool IsSettingsSearchEmptyVisible => _isSettingsSearchEmpty;
    private void NotifySettingsSearchChanged()
    {
        var cards = SettingsCardsPanel.Children
            .OfType<Control>()
            .Where(c => c.Name != "SettingsSearchEmptyPlaceholder" &&
                        (c.Name is null || !c.Name.StartsWith("SectionHeader", StringComparison.Ordinal)))
            .ToList();
        var groupVisible = cards
            .GroupBy(SettingsCardGroupKey)
            .ToDictionary(g => g.Key, g => g.Any(MatchesSettingsSearchCard));

        var anyVisible = false;
        foreach (var card in cards)
        {
            var visible = groupVisible[SettingsCardGroupKey(card)];
            card.IsVisible = visible;
            anyVisible |= visible;
        }

        _isSettingsSearchEmpty = !string.IsNullOrWhiteSpace(_settingsSearchText) && !anyVisible;
        OnPropertyChanged(nameof(IsSettingsSearchEmptyVisible));
    }

    private static object SettingsCardGroupKey(Control card) => card.Tag ?? (object)card;

    public bool IsUpdatingAllExtensions
    {
        get => _isUpdatingAllExtensions;
        private set
        {
            if (_isUpdatingAllExtensions == value) return;
            _isUpdatingAllExtensions = value;
            OnPropertyChanged();
            NotifyExtensionActionStateChanged();
        }
    }

    // Settings toggle: silently install extension updates instead of requiring a manual click.
    public bool IsAutoUpdateExtensionsEnabled
    {
        get => _isAutoUpdateExtensionsEnabled;
        set
        {
            if (_isAutoUpdateExtensionsEnabled == value) return;
            _isAutoUpdateExtensionsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoUpdateExtensionsStatusText));
            SaveSettings();
            UpdateExtensionAutoUpdateLifecycle();
            if (value)
                _ = AutoUpdateExtensionsIfEnabledAsync();
        }
    }

    // Sub-setting: suppresses the "Auto-updating..." progress text so the silent sweep makes no visible change.
    public bool IsAutoUpdateExtensionsInBackgroundEnabled
    {
        get => _isAutoUpdateExtensionsInBackgroundEnabled;
        set
        {
            if (_isAutoUpdateExtensionsInBackgroundEnabled == value) return;
            _isAutoUpdateExtensionsInBackgroundEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoUpdateExtensionsStatusText));
            SaveSettings();
        }
    }

    // Settings toggle: check GitHub Releases for a newer build after launch and offer to install it.
    public bool IsAutoUpdateAppEnabled
    {
        get => _isAutoUpdateAppEnabled;
        set
        {
            if (_isAutoUpdateAppEnabled == value) return;
            _isAutoUpdateAppEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoUpdateAppStatusText));
            SaveSettings();
            _appUpdateScheduler.UpdateLifecycle();

            // Keep KodoUpdater's logon-autostart registration in sync immediately, rather than waiting
            // for Kodo to relaunch - flipping this off should stop it being resident right away, and
            // flipping it on should make it survive the next reboot right away.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (value) Task.Run(UpdateService.EnsureAutostartRegistered);
                else Task.Run(UpdateService.RemoveAutostartRegistration);
            }
        }
    }

    // Sub-setting: installs a found app update immediately, no Update Now/Later prompt.
    public bool IsAutoUpdateAppInBackgroundEnabled
    {
        get => _isAutoUpdateAppInBackgroundEnabled;
        set
        {
            if (_isAutoUpdateAppInBackgroundEnabled == value) return;
            _isAutoUpdateAppInBackgroundEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoUpdateAppStatusText));
            SaveSettings();
        }
    }

    // Drives the Check for Updates button's disabled/label state while a manual check is running.
    public bool IsCheckingForUpdatesManually
    {
        get => _isCheckingForUpdatesManually;
        private set
        {
            if (_isCheckingForUpdatesManually == value) return;
            _isCheckingForUpdatesManually = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CheckForUpdatesButtonText));
        }
    }

    public string CheckForUpdatesButtonText => IsCheckingForUpdatesManually ? "Checking…" : "Check for Updates";

    // Result of the most recent manual check ("You're up to date", "vX.Y.Z is
    // available", or a failure message). Empty until the button is clicked.
    public string CheckForUpdatesStatusText
    {
        get => _checkForUpdatesStatusText;
        private set
        {
            if (_checkForUpdatesStatusText == value) return;
            _checkForUpdatesStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCheckForUpdatesStatus));
        }
    }

    public bool HasCheckForUpdatesStatus => !string.IsNullOrWhiteSpace(CheckForUpdatesStatusText);

    public IEnumerable<LoadedExtension> FilteredInstalledExtensions
    {
        get
        {
            if (_selectedInstalledContentFilter == InstalledContentFilters.Compilers)
                return [];

            var source = string.IsNullOrWhiteSpace(_extensionSearchText)
                ? VisibleLoadedExtensions
                : VisibleLoadedExtensions.Where(e =>
                    e.Name.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Description.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase));
            return SortInstalledExtensions(source);
        }
    }

    public IEnumerable<MarketplaceExtension> FilteredMarketplaceExtensions
    {
        get
        {
            IEnumerable<MarketplaceExtension> source = MarketplaceExtensions;

            // Each marketplace tab (Languages/Themes/Plugins) shows only its own extension type.
            var tabType = ActiveMarketplaceTabType;
            if (!string.IsNullOrEmpty(tabType))
            {
                source = source.Where(e =>
                    string.Equals(e.Type, tabType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(_extensionSearchText))
            {
                source = source.Where(e =>
                    e.Name.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Description.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Author.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase));
            }

            return SortMarketplaceExtensions(source);
        }
    }

    public static class InstalledContentFilters
    {
        public const string All = "All";
        public const string Extensions = "Extensions";
        public const string Compilers = "Compilers";
    }

    public IReadOnlyList<string> InstalledContentFilterOptions { get; } =
    [
        InstalledContentFilters.All,
        InstalledContentFilters.Extensions,
        InstalledContentFilters.Compilers
    ];

    private string _selectedInstalledContentFilter = InstalledContentFilters.All;
    public string SelectedInstalledContentFilter
    {
        get => _selectedInstalledContentFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? InstalledContentFilters.All : value;
            if (string.Equals(_selectedInstalledContentFilter, normalized, StringComparison.Ordinal))
                return;

            _selectedInstalledContentFilter = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInstalledCompilersFilterSelected));
            NotifyExtensionFiltersChanged();
        }
    }

    // Drives visibility of the "add a compiler by path" bar - only shown in the Installed tab
    // while its content filter is scoped to Compilers, not All or Extensions.
    public bool IsInstalledCompilersFilterSelected => _selectedInstalledContentFilter == InstalledContentFilters.Compilers;

    private string _manualCompilerPathText = string.Empty;
    public string ManualCompilerPathText
    {
        get => _manualCompilerPathText;
        set
        {
            if (_manualCompilerPathText == value) return;
            _manualCompilerPathText = value;
            OnPropertyChanged();
        }
    }

    // Installed compilers, surfaced in the Installed tab alongside LoadedExtensions -
    // filtered by SelectedInstalledContentFilter (All / Extensions / Compilers) and search text.
    // Combines index-installed compilers with manually-added/auto-detected ones (see
    // ManualCompilerExtensions) into one list.
    public IEnumerable<MarketplaceExtension> FilteredInstalledCompilerExtensions
    {
        get
        {
            if (_selectedInstalledContentFilter == InstalledContentFilters.Extensions)
                return [];

            IEnumerable<MarketplaceExtension> source = CompilerExtensions.Where(e => e.IsInstalled)
                .Concat(ManualCompilerExtensions);

            if (!string.IsNullOrWhiteSpace(_extensionSearchText))
            {
                source = source.Where(e =>
                    e.Name.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Description.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Author.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase));
            }

            return SortMarketplaceExtensions(source);
        }
    }

    public bool HasVisibleInstalledCompilerExtensions => FilteredInstalledCompilerExtensions.Any();
    public bool IsInstalledCompilersEmptyStateVisible =>
        !HasVisibleInstalledCompilerExtensions && _selectedInstalledContentFilter != InstalledContentFilters.Extensions;

    // Compilers tab source list - same search behaviour as Marketplace, plus the
    // Installed-only filter toggle (compilers have no Type/Theme/Language split).
    public IEnumerable<MarketplaceExtension> FilteredCompilerExtensions
    {
        get
        {
            IEnumerable<MarketplaceExtension> source = CompilerExtensions;

            if (!string.IsNullOrWhiteSpace(_extensionSearchText))
            {
                source = source.Where(e =>
                    e.Name.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Description.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Author.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase));
            }

            return SortMarketplaceExtensions(source);
        }
    }

    private IEnumerable<LoadedExtension> SortInstalledExtensions(IEnumerable<LoadedExtension> source) =>
        SelectedExtensionSort switch
        {
            ExtensionSortModes.ReverseAlphabetical => source.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.RecentlyInstalled => source
                .OrderByDescending(e => e.InstalledOnUtc ?? DateTime.MinValue)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.UpdatesAvailable => source
                .OrderByDescending(e => e.IsUpdateAvailable)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };

    private IEnumerable<MarketplaceExtension> SortMarketplaceExtensions(IEnumerable<MarketplaceExtension> source) =>
        SelectedExtensionSort switch
        {
            ExtensionSortModes.ReverseAlphabetical => source.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.RecentlyInstalled => source
                .OrderByDescending(e => e.InstalledOnUtc.HasValue)
                .ThenByDescending(e => e.InstalledOnUtc ?? DateTime.MinValue)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.UpdatesAvailable => source
                .OrderBy(GetMarketplaceUpdatePriority)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };

    private static int GetMarketplaceUpdatePriority(MarketplaceExtension extension)
    {
        if (extension.IsUpdateAvailable)
            return 0;
        if (!extension.IsInstalled)
            return 1;
        return 2;
    }

    public bool IsSearchPanelVisible
    {
        get => _isSearchPanelVisible;
        set
        {
            if (_isSearchPanelVisible == value) return;
            _isSearchPanelVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchPanelActive));
            if (!value)
            {
                _searchDebounceTimer.Stop();
                _searchCancellation?.Cancel();
                _findHighlightRenderer.Clear();
                _findMatchOffsets.Clear();
                _currentFindMatchIndex = -1;
                ResetHistoryIndex();
                SearchPanelBorder.MinWidth = 0;
                EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            }
        }
    }

    public bool IsFindInFileSearchMode => _searchMode == SearchMode.FindInFile;
    public bool IsFileByNameSearchMode => _searchMode == SearchMode.FileByName;
    public bool IsProjectSearchMode => _searchMode == SearchMode.ProjectSearch;
    public bool IsSearchResultsVisible => _searchMode != SearchMode.FindInFile;
    public bool IsSearchMatchCaseEnabled
    {
        get => _isSearchMatchCaseEnabled;
        set
        {
            if (_isSearchMatchCaseEnabled == value) return;
            _isSearchMatchCaseEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsSearchWholeWordEnabled
    {
        get => _isSearchWholeWordEnabled;
        set
        {
            if (_isSearchWholeWordEnabled == value) return;
            _isSearchWholeWordEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsSearchRegexEnabled
    {
        get => _isSearchRegexEnabled;
        set
        {
            if (_isSearchRegexEnabled == value) return;
            _isSearchRegexEnabled = value;
            OnPropertyChanged();
        }
    }

    public string SearchPlaceholderText => _searchMode switch
    {
        SearchMode.FindInFile => "Find in file...",
        SearchMode.FileByName => "Find files by name...",
        SearchMode.ProjectSearch => "Search in project...",
        _ => "Search...",
    };

    public ObservableCollection<SearchResultItem> SearchResults => _searchResults;
    public ObservableCollection<SearchDisplayItem> SearchDisplayItems => _searchDisplayItems;

    public string SearchStatusText
    {
        get => _searchStatusText;
        private set
        {
            if (_searchStatusText == value) return;
            _searchStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchStatusVisible));
        }
    }

    public bool IsSearchStatusVisible => !string.IsNullOrEmpty(_searchStatusText);

    public bool IsSearchBusy
    {
        get => _isSearchBusy;
        private set
        {
            if (_isSearchBusy == value) return;
            _isSearchBusy = value;
            OnPropertyChanged();
        }
    }

    public string SearchIncludeFilter
    {
        get => _searchIncludeFilter;
        set
        {
            if (_searchIncludeFilter == value) return;
            _searchIncludeFilter = value;
            OnPropertyChanged();
            if (IsProjectSearchMode && IsSearchPanelActive)
                RestartSearchDebounce();
        }
    }

    public string SearchExcludeFilter
    {
        get => _searchExcludeFilter;
        set
        {
            if (_searchExcludeFilter == value) return;
            _searchExcludeFilter = value;
            OnPropertyChanged();
            if (IsProjectSearchMode && IsSearchPanelActive)
                RestartSearchDebounce();
        }
    }

    public bool IsFilterRowVisible
    {
        get => _isFilterRowVisible;
        set
        {
            if (_isFilterRowVisible == value) return;
            _isFilterRowVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsFilterRowSupported => _searchMode == SearchMode.ProjectSearch && IsFolderOpen;

    public bool IsTerminalVisible
    {
        get => _isTerminalVisible;
        set
        {
            if (_isTerminalVisible == value) return;
            _isTerminalVisible = value;
            OnPropertyChanged();
            RefreshTerminalStatusBindings();
            SaveSettings();
            if (_isTerminalVisible)
                FocusActiveTerminal();
            else
                RefreshTerminalWindows();
        }
    }

    public double TerminalPanelHeight
    {
        get => _terminalPanelHeight;
        set
        {
            var normalized = TerminalShellSupport.NormalizeTerminalPanelHeight(value);
            if (_terminalPanelHeight == normalized) return;
            _terminalPanelHeight = normalized;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // Manual drag handling so the value flows straight into TerminalPanelHeight.
    private void TerminalPanelSplitter_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not InputElement element) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;

        _isResizingTerminalPanel = true;
        _terminalPanelDragStartPointerY = e.GetPosition(this).Y;
        _terminalPanelDragStartHeight = TerminalPanelHeight;
        e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void TerminalPanelSplitter_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizingTerminalPanel) return;

        // The splitter sits above the panel, so dragging up should grow it - the inverse of the pointer's delta.
        var deltaY = _terminalPanelDragStartPointerY - e.GetPosition(this).Y;
        TerminalPanelHeight = _terminalPanelDragStartHeight + deltaY;
        e.Handled = true;
    }

    private void TerminalPanelSplitter_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingTerminalPanel) return;

        _isResizingTerminalPanel = false;
        e.Pointer.Capture(null);
        // Flushes the final height immediately instead of waiting on the debounce timer, in case of a quick resize-then-close.
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    // Guards against a stray PointerCaptureLost leaving the panel stuck in resize mode.
    private void TerminalPanelSplitter_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isResizingTerminalPanel) return;

        _isResizingTerminalPanel = false;
        SaveSettings(immediate: true);
    }


    public TerminalSession? ActiveTerminalSession
    {
        get => _activeTerminalSession;
        private set
        {
            if (ReferenceEquals(_activeTerminalSession, value))
                return;

            // Saves the outgoing session's screen buffer; the tab stays restorable and shows as paused instead of exited.
            if (_activeTerminalSession is not null)
            {
                if (TerminalHostControl.HasLiveProcess)
                    _activeTerminalSession.Snapshot = TerminalHostControl.SaveSnapshot();
                if (_activeTerminalSession.StatusText is not "Exited" && !_activeTerminalSession.StatusText.StartsWith("Failed", StringComparison.Ordinal))
                    _activeTerminalSession.StatusText = "Paused";
                _activeTerminalSession.IsSelected = false;
            }

            _activeTerminalSession = value;

            if (_activeTerminalSession is not null)
                _activeTerminalSession.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveTerminal));
            OnPropertyChanged(nameof(ActiveTerminalShellDisplayName));
            RefreshTerminalStatusBindings();
            if (_activeTerminalSession is not null)
            {
                // Cold-starts the incoming session; restores the snapshot after Start().
                var shell = AvailableTerminalShells.FirstOrDefault(s =>
                    string.Equals(s.Id, _activeTerminalSession.ShellId, StringComparison.OrdinalIgnoreCase))
                    ?? GetSelectedTerminalShellOrFallback();
                if (shell is not null)
                {
                    try
                    {
                        // Unhooks the previous handler before Start() so it can't fire on the new session.
                        if (_activeSessionExitedHandler is not null)
                        {
                            TerminalHostControl.SessionExited -= _activeSessionExitedHandler;
                            _activeSessionExitedHandler = null;
                        }

                        var hasSnapshot = _activeTerminalSession.Snapshot is not null;
                        TerminalHostControl.Start(shell.FileName, shell.Arguments,
                            _activeTerminalSession.WorkingDirectory,
                            suppressOutputUntilRestored: hasSnapshot);
                        _activeTerminalSession.IsRunning = true;
                        _activeTerminalSession.StatusText = "Ready";

                        // Captures the handle Start() launched to reject stale SessionExited posts.
                        var expectedHandle = TerminalHostControl.CurrentProcessHandle;

                        var watchedSession = _activeTerminalSession;
                        void OnExited(object? s, IntPtr exitedHandle)
                        {
                            // Rejects stale SessionExited posts by comparing the exiting process's handle to the one we started.
                            if (exitedHandle != expectedHandle)
                                return;

                            TerminalHostControl.SessionExited -= OnExited;
                            _activeSessionExitedHandler = null;
                            watchedSession.IsRunning = false;

                            CloseTerminalSession(watchedSession);
                            RefreshTerminalStatusBindings();
                        }
                        _activeSessionExitedHandler = OnExited;
                        TerminalHostControl.SessionExited += OnExited;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Terminal] Failed to start shell for '{_activeTerminalSession.Title}': {ex.Message}");
                        _activeTerminalSession.IsRunning = false;
                        _activeTerminalSession.StatusText = $"Failed to start: {ex.Message}";
                    }
                }

                // Restores the saved screen buffer once Start() finishes initialising the grid.
                if (_activeTerminalSession.Snapshot is not null)
                    TerminalHostControl.RestoreSnapshot(_activeTerminalSession.Snapshot);
            }
            else
            {
                TerminalHostControl.Stop();
            }
            RefreshTerminalWindows();
            SaveSettings();
        }
    }

    public TerminalShellOption? SelectedTerminalShell
    {
        get => _selectedTerminalShell;
        set
        {
            if (ReferenceEquals(_selectedTerminalShell, value))
                return;

            _selectedTerminalShell = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveTerminalShellDisplayName));
            SaveSettings();
        }
    }

    public string FindText
    {
        get => _findText;
        set
        {
            if (_findText == value) return;
            _findText = value;
            OnPropertyChanged();
            RestartSearchDebounce();
            if (IsFindInFileSearchMode)
                UpdateFindHighlights();
        }
    }

    public bool IsConfirmBeforeClosingUnsavedTabsEnabled
    {
        get => _isConfirmBeforeClosingUnsavedTabsEnabled;
        set
        {
            if (_isConfirmBeforeClosingUnsavedTabsEnabled == value) return;
            _isConfirmBeforeClosingUnsavedTabsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabRestoreStatusText));
            SaveSettings();
        }
    }

    public bool IsRestoreOpenTabsOnLaunchEnabled
    {
        get => _isRestoreOpenTabsOnLaunchEnabled;
        set
        {
            if (_isRestoreOpenTabsOnLaunchEnabled == value) return;
            _isRestoreOpenTabsOnLaunchEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabRestoreStatusText));
            SaveSettings();
        }
    }

    public string CurrentThemeName
    {
        get => _currentThemeName;
        private set
        {
            if (_currentThemeName == value) return;
            _currentThemeName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeStatusText));
            OnPropertyChanged(nameof(IsDarkThemeActive));
            OnPropertyChanged(nameof(IsLightThemeActive));
            OnPropertyChanged(nameof(IsSystemThemeActive));
            foreach (var ext in ThemeExtensions)
                ext.IsActiveTheme = string.Equals(ext.ThemeCardThemeId, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Displays the file name and Unsaved/autosave status in the top bar
    public string FileSummaryText => IsTutorialPageVisible
        ? "Tutorial"
        : IsHomePageVisible
        ? "Home"
        : HasDocumentOpen
        ? $"{GetDocumentDisplayName()}{GetDocumentStatusSuffix()}"
        : "Home";
    public bool IsTerminalSupported => _isTerminalSupported;
    public bool HasActiveTerminal => ActiveTerminalSession is not null;
    public bool HasTerminalSessions => TerminalSessions.Count > 0;
    public int TerminalSessionCount => TerminalSessions.Count;
    public string ActiveTerminalStatusText => ActiveTerminalSession?.StatusText ?? (IsTerminalSupported ? "No active terminal" : "Windows only");
    public string ActiveTerminalWorkingDirectory
    {
        get
        {
            var fullPath = ActiveTerminalSession?.WorkingDirectory ?? ResolveTerminalWorkingDirectory();
            if (IsStatusBarFilePathVisible) return fullPath;
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : fullPath;
        }
    }
    public string ActiveTerminalShellDisplayName => ActiveTerminalSession?.ShellDisplayName ?? SelectedTerminalShell?.DisplayName ?? "Terminal";
    public string ActiveTerminalFooterText => HasActiveTerminal
        ? $"{ActiveTerminalWorkingDirectory}  |  {ActiveTerminalStatusText}"
        : IsTerminalSupported ? "Choose a shell and open a terminal session." : "Embedded terminal is currently supported on Windows only.";
    public string TerminalStatusBarText => IsTerminalVisible
        ? $"Terminal ({TerminalSessionCount})"
        : TerminalSessionCount > 0 ? $"Show terminal ({TerminalSessionCount})" : "Terminal";

    public string FilePathText => IsTutorialPageVisible
        ? "Getting started with Kodo"
        : IsHomePageVisible
        ? "Welcome to Kodo!"
        : HasFileOpen
        ? (IsStatusBarFilePathVisible ? _currentFilePath! : GetDocumentDisplayName())
        : HasDocumentOpen ? "Unsaved file"
        : IsFolderOpen ? (IsStatusBarFilePathVisible
            ? $"📂 {_currentFolderPath}"
            : $"📂 {Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar))}")
        : "No file open";

    public string ExplorerHeaderText => IsFolderOpen
        ? Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar)).ToUpperInvariant()
        : "EXPLORER";

    // Header row chrome that competes with the header text: border padding (12+12),
    // accent bar (3) + its spacing to the text (6), and the four 28px activity
    // buttons (new file/new folder/collapse-all/close, all visible together whenever
    // a folder - and therefore a real folder name - is open).
    private const double ExplorerHeaderFixedChrome = 12 + 12 + 3 + 6 + 4 * 28;

    private static readonly Typeface ExplorerHeaderTypeface = new("Segoe UI", weight: FontWeight.SemiBold);

    // Keeps the header text from being clipped under the activity buttons when the
    // panel is dragged narrow: floors MinWidth at however wide ExplorerHeaderText
    // actually renders, on top of the surrounding chrome.
    public double ExplorerPanelMinWidth
    {
        get
        {
            var formatted = new FormattedText(
                ExplorerHeaderText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ExplorerHeaderTypeface,
                11,
                Brushes.Black);

            return Math.Min(MaxExplorerPanelWidth,
                Math.Max(MinExplorerPanelWidth, formatted.Width + ExplorerHeaderFixedChrome));
        }
    }

    public string ExplorerHeaderTooltipText => IsFolderOpen
        ? Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar))
        : "Explorer";

    public string ThemeStatusText => IsSystemThemeActive
        ? $"Current theme: System Default ({CurrentThemeName})"
        : $"Current theme: {CurrentThemeName}";

    // Personalization settings

    /// ISO country code for the user, used to pick region-appropriate holiday messages.
    public string UserCountry
    {
        get => _userCountry;
        set
        {
            if (_userCountry == value) return;
            _userCountry = value.ToUpperInvariant();
            _welcomeMessagesCache = null;
            _selectedWelcomeMessage = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAmericanEnglish));
            OnPropertyChanged(nameof(LabelAccentColour));
            OnPropertyChanged(nameof(TooltipAccentTheme));
            OnPropertyChanged(nameof(TooltipAccentWindows));
            OnPropertyChanged(nameof(TooltipAccentCustom));
            OnPropertyChanged(nameof(LabelPersonalization));
            OnPropertyChanged(nameof(LabelPersonalizationDescription));
            OnPropertyChanged(nameof(TutorialSpotlightTitle));
            OnPropertyChanged(nameof(TutorialBody));
            OnPropertyChanged(nameof(TutorialHighlightOne));
            OnPropertyChanged(nameof(TutorialHighlightThree));
            SaveSettings();
        }
    }


    /// True when the user's country is US, driving American spelling instead of British/Canadian.

    public bool IsAmericanEnglish => _userCountry == "US";

    // Regional spelling: US gets "Color"/"personalize"; everyone else gets "Colour"/"personalise".
    public string LabelAccentColour        => IsAmericanEnglish ? "Accent Color"      : "Accent Colour";
    public string TooltipAccentTheme       => IsAmericanEnglish ? "Use the accent color preset by the active theme" : "Use the accent colour preset by the active theme";
    public string TooltipAccentWindows     => IsAmericanEnglish ? "Use your Windows system accent color" : "Use your Windows system accent colour";
    public string TooltipAccentCustom      => IsAmericanEnglish ? "Choose a custom accent color" : "Choose a custom accent colour";
    public string LabelPersonalization     => IsAmericanEnglish ? "Personalization"   : "Personalisation";
    public string LabelPersonalizationDescription => IsAmericanEnglish
        ? "These settings personalize the welcome message on the Home screen. Your name is used in greetings when set. Country is auto-detected from your system if left blank. Hemisphere and time zone are also auto-detected when possible."
        : "These settings personalise the welcome message on the Home screen. Your name is used in greetings when set. Country is auto-detected from your system if left blank. Hemisphere and time zone are also auto-detected when possible.";


    /// Hemisphere override: 0 auto-detect, 1 north, 2 south; affects the inferred season for greetings.

    public int UserHemisphereIndex
    {
        get => _userHemisphere;
        set
        {
            if (_userHemisphere == value) return;
            _userHemisphere = value;
            _welcomeMessagesCache = null;
            _selectedWelcomeMessage = null;
            OnPropertyChanged();
            SaveSettings();
        }
    }


    /// UTC offset entered by the user; overrides the system clock for time-of-day greetings when set.

    public string UserTimezoneOffset
    {
        get => _userTimezoneOffset;
        set
        {
            if (_userTimezoneOffset == value) return;
            _userTimezoneOffset = value;
            _welcomeMessagesCache = null;
            _selectedWelcomeMessage = null;
            OnPropertyChanged();
            SaveSettings();
        }
    }


    /// Optional display name for personalised greetings; empty omits the name.

    public string UserName
    {
        get => _userName;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (_userName == trimmed) return;
            _userName = trimmed;
            _welcomeMessagesCache = null;
            _selectedWelcomeMessage = null;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // Welcome message


    /// Infers the user's country from OS regional settings; returns an ISO code or empty string if unknown.

    private static string DetectCountryCode()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion;
            return region.TwoLetterISORegionName.ToUpperInvariant();
        }
        catch { return string.Empty; }
    }

    // Holiday, sporting-event, and greeting-pool logic all live in WelcomeMessageBuilder.cs.

    // Populated by FetchSportingEventMessagesAsync and joined into the greeting pool once available.
    private List<string>? _sportingEventMessages;


    /// Best-effort fetch of sporting-event greeting lines.

    private async Task FetchSportingEventMessagesAsync()
    {
        var messages = await WelcomeMessageBuilder.FetchSportingEventMessagesAsync(MarketplaceHttpClient);
        if (messages is null) return;

        _sportingEventMessages = messages;

        // Only rebuilds the pool if Home hasn't shown a greeting yet, to avoid a flicker.
        if (_selectedWelcomeMessage is null)
        {
            _welcomeMessagesCache = null;
            OnPropertyChanged(nameof(WelcomeMessage));
        }
    }

    // Lazily constructed per-instance so it can incorporate the personalization settings
    // which are read from settings before DataContext is set.
    private string[]? _welcomeMessagesCache;

    // The message picked for this pool generation, cached to avoid re-rolling on every read.
    private string? _selectedWelcomeMessage;

    // Evaluated once per launch: true one in ten thousand times, showing the "Code fast. Stay light" tagline.
    private readonly bool _isTaglineGreeting = Random.Shared.Next(10_000) == 0;
    public bool IsTaglineGreeting => _isTaglineGreeting;

    // One-in-a-million chance per launch that the "KODO" wordmark reads "Kode-o" instead.
    private readonly bool _isRareWordmarkVariant = Random.Shared.Next(1_000_000) == 0;
    public string KodoWordmarkText => _isRareWordmarkVariant ? "KODE-O" : "KODO";

    // Birthday

    private static readonly DateTime _kodoBirthDate = new(2026, 4, 18);


    /// True on April 18, Kodo's birthday; drives celebratory UI accents throughout the app.

    public bool IsKodoBirthday
    {
        get
        {
            var today = DateTime.Now;
            return today.Month == _kodoBirthDate.Month && today.Day == _kodoBirthDate.Day;
        }
    }

    /// How old Kodo is today (whole years since April 18 2026).
    public int KodoBirthdayAge
    {
        get
        {
            var today = DateTime.Now;
            var age   = today.Year - _kodoBirthDate.Year;
            if (today.Month < _kodoBirthDate.Month ||
                (today.Month == _kodoBirthDate.Month && today.Day < _kodoBirthDate.Day))
                age--;
            return Math.Max(0, age);
        }
    }

    /// Short status-bar note shown on Kodo's birthday; empty every other day.
    public string StatusBarBirthdayText
    {
        get
        {
            if (!IsKodoBirthday) return string.Empty;
            var age = KodoBirthdayAge;
            return age == 1 ? "Kodo turns 1 today! 🎂" : $"Kodo turns {age} today! 🎂";
        }
    }

    // True only when StatusBarBirthdayText is non-empty (i.e. on the birthday).
    public bool IsStatusBarBirthdayVisible => IsKodoBirthday;

    public string GetStartedSubtitleText => "Open a file or folder to get started, or create something new.";

    public string WelcomeMessage
    {
        get
        {
            _welcomeMessagesCache ??= WelcomeMessageBuilder.BuildMessages(
                _userName,
                _userCountry,
                _userHemisphere,
                _userTimezoneOffset,
                IsKodoBirthday,
                KodoBirthdayAge,
                _sportingEventMessages);
            _selectedWelcomeMessage ??= _welcomeMessagesCache[Random.Shared.Next(_welcomeMessagesCache.Length)];
            return _selectedWelcomeMessage;
        }
    }

    public bool IsDarkThemeActive  => !IsSystemThemeActive && string.Equals(CurrentThemeName, "Dark",  StringComparison.OrdinalIgnoreCase);
    public bool IsLightThemeActive => !IsSystemThemeActive && string.Equals(CurrentThemeName, "Light", StringComparison.OrdinalIgnoreCase);

    // True when the user picked "follow Windows"; tracked off _requestedThemeName.
    public bool IsSystemThemeActive => string.Equals(_requestedThemeName, "System", StringComparison.OrdinalIgnoreCase);

    // Live preview for the System Default blob, reflecting Windows' current light/dark state regardless of the active theme.
    public IBrush SystemThemePreviewBackground { get; private set; } = Brush.Parse("#1E1E1E");
    public IBrush SystemThemePreviewBorder     { get; private set; } = Brush.Parse("#2B2B2B");

    public string LanguageDisplayText
    {
        get
        {
            if (HasImagePreview) return "Image Preview";
            if (!HasDocumentOpen) return string.Empty;
            if (!string.IsNullOrWhiteSpace(_currentFilePath))
            {
                var ext = Path.GetExtension(_currentFilePath);
                if (!string.IsNullOrWhiteSpace(ext))
                    return $"{ext.ToLowerInvariant()} file";
                var name = Path.GetFileName(_currentFilePath);
                return string.IsNullOrWhiteSpace(name) ? "Plain Text" : $"{name} file";
            }
            return "Plain Text";
        }
    }

    // Short human-readable label for the encoding of the active file, shown in the status bar.
    public string EncodingDisplayText
    {
        get
        {
            if (!HasFileOpen) return string.Empty;
            var cp = _currentFileEncoding.CodePage;
            return cp switch
            {
                65001 => _currentFileEncoding is System.Text.UTF8Encoding u && u.GetPreamble().Length > 0
                             ? "UTF-8 BOM"
                             : "UTF-8",
                1200  => "UTF-16 LE",
                1201  => "UTF-16 BE",
                12000 => "UTF-32",
                20127 => "ASCII",
                _     => _currentFileEncoding.WebName.ToUpperInvariant(),
            };
        }
    }

    public string DiscordRichPresenceStatusText => !IsDiscordRichPresenceEnabled
        ? "Discord Rich Presence is turned off."
        : IsDiscordImprovedRpcEnabled
            ? "Improved Discord Rich Presence is on when the Discord desktop app is running."
            : "Discord Rich Presence is on when the Discord desktop app is running.";

    public string AutoSaveStatusText =>
        !IsAutoSaveEnabled
            ? "Autosave is turned off."
            : HasFileOpen
                ? "Changes are saved automatically a couple seconds after you stop typing."
                : "Autosave will start working after the file has been saved once.";

    public string AutoUpdateExtensionsStatusText =>
        !IsAutoUpdateExtensionsEnabled
            ? "Extensions only update when you click Update in the Marketplace."
            : AvailableExtensionUpdatesCount > 0
                ? $"Checking periodically and installing updates automatically. {AvailableExtensionUpdatesCount} update{(AvailableExtensionUpdatesCount == 1 ? string.Empty : "s")} pending."
                : IsAutoUpdateExtensionsInBackgroundEnabled
                    ? "Checking periodically and installing new extension updates automatically, without showing progress."
                    : "Checking periodically and installing new extension updates automatically.";

    public string AutoUpdateAppStatusText =>
        !IsAutoUpdateAppEnabled
            ? "Kodo only updates when you download a new installer yourself."
            : IsAutoUpdateAppInBackgroundEnabled
                ? "Checking for new Kodo versions on launch and every few hours, and installing them automatically in the background once Kodo is closed."
                : "Checking for new Kodo versions on launch and every few hours, and prompting to install them.";

    public string StatusBarFilePathVisibilityText => IsStatusBarFilePathVisible
        ? "The status bar shows the full path for the current file or folder."
        : "The status bar keeps a shorter label instead of the full file or folder path.";

    public string EditorBehaviorStatusText => IsWordWrapEnabled
        ? $"Word wrap is on, and indentation guides are spaced every {TabSize} columns."
        : $"Word wrap is off, and indentation guides are spaced every {TabSize} columns.";

    public string TabRestoreStatusText => IsRestoreOpenTabsOnLaunchEnabled
        ? "File-backed tabs reopen on launch, and unsaved tabs ask for confirmation before closing."
        : IsConfirmBeforeClosingUnsavedTabsEnabled
            ? "Unsaved tabs ask for confirmation before closing."
            : "Tabs close immediately, and launch starts with a fresh editor session.";

    public string EditorStatsText
    {
        get => _editorStatsText;
        private set
        {
            if (_editorStatsText == value) return;
            _editorStatsText = value;
            OnPropertyChanged();
        }
    }

    public string ReplaceText
    {
        get => _replaceText;
        set
        {
            if (_replaceText == value) return;
            _replaceText = value;
            OnPropertyChanged();
        }
    }

    public string WordCountText
    {
        get => _wordCountText;
        private set
        {
            if (_wordCountText == value) return;
            _wordCountText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWordCountVisible));
        }
    }

    // Only visible for plain-text files (.txt / .text / .log) that are open in the editor.
    public bool IsWordCountVisible =>
        IsTextEditorVisible && IsPlainTextFile(_currentFilePath);

    public IBrush WindowBackgroundBrush { get; private set; } = Brush.Parse("#1E1E1E");
    public IBrush TopBarBrush           { get; private set; } = Brush.Parse("#181818");
    public IBrush SidebarBrush          { get; private set; } = Brush.Parse("#181818");
    public IBrush ButtonBrush           { get; private set; } = Brush.Parse("#252526");
    public IBrush ButtonHoverBrush      { get; private set; } = Brush.Parse("#313437");
    public IBrush EditorBackgroundBrush { get; private set; } = Brush.Parse("#1E1E1E");
    public IBrush CardBrush             { get; private set; } = Brush.Parse("#252526");
    public IBrush PrimaryTextBrush      { get; private set; } = Brush.Parse("#F4F4F4");
    public IBrush MutedTextBrush        { get; private set; } = Brush.Parse("#A0A0A0");
    public IBrush SurfaceBorderBrush    { get; private set; } = Brush.Parse("#2B2B2B");
    public IBrush AccentBrush           { get; private set; } = Brush.Parse("#8C00FF");

    // Black or white, whichever contrasts better against AccentBrush, for text/icons drawn on the accent color.
    public IBrush AccentForegroundBrush { get; private set; } = Brushes.White;

    // Returns Brushes.White or Brushes.Black depending on which gives better contrast
    // against the supplied brush, using the WCAG relative-luminance formula.
    private static IBrush GetAccentForeground(IBrush accent)
    {
        if (accent.ToImmutable() is not ISolidColorBrush solid)
            return Brushes.White;
        return GetReadableForeground(solid.Color);
    }

    private static void SyncSystemAccentResources(IBrush accent)
    {
        if (accent.ToImmutable() is not ISolidColorBrush solid) return;
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var c = solid.Color;
        resources["SystemAccentColor"] = c;
        resources["SystemAccentColorLight1"] = LightenColor(c, 0.15);
        resources["SystemAccentColorLight2"] = LightenColor(c, 0.30);
        resources["SystemAccentColorLight3"] = LightenColor(c, 0.45);
        resources["SystemAccentColorDark1"]  = DarkenColor(c, 0.15);
        resources["SystemAccentColorDark2"]  = DarkenColor(c, 0.30);
        resources["SystemAccentColorDark3"]  = DarkenColor(c, 0.45);
    }

    private static Color LightenColor(Color c, double amount)
    {
        byte Adjust(byte ch) => (byte)Math.Clamp(ch + (255 - ch) * amount, 0, 255);
        return Color.FromArgb(c.A, Adjust(c.R), Adjust(c.G), Adjust(c.B));
    }

    private static Color DarkenColor(Color c, double amount)
    {
        byte Adjust(byte ch) => (byte)Math.Clamp(ch * (1 - amount), 0, 255);
        return Color.FromArgb(c.A, Adjust(c.R), Adjust(c.G), Adjust(c.B));
    }

    // Shared relative-luminance calculation (WCAG 2.x), used both for picking
    // accent-button text and for the theme-pack text safety net below.
    private static double GetRelativeLuminance(Color c)
    {
        static double Lin(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    // Standard WCAG contrast ratio between two colours, always >= 1.0.
    private static double GetContrastRatio(Color a, Color b)
    {
        var l1 = GetRelativeLuminance(a);
        var l2 = GetRelativeLuminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    // Returns whichever of white/black contrasts best against the given colour.
    private static IBrush GetReadableForeground(Color background)
    {
        var L = GetRelativeLuminance(background);
        return (1.05 / (L + 0.05)) >= ((L + 0.05) / 0.05)
            ? Brushes.White
            : Brushes.Black;
    }

    // Checks the candidate text color against every surface it's on, falls back to WCAG-safe black/white.
    private static IBrush EnsureReadableTextBrush(IBrush candidate, params IBrush[] backgrounds)
    {
        const double MinimumReadableContrast = 4.5; // WCAG AA, normal text

        if (candidate.ToImmutable() is not ISolidColorBrush candidateSolid)
            return candidate;

        var worstContrast = double.MaxValue;
        Color worstBackground = default;
        var foundSolidBackground = false;

        foreach (var background in backgrounds)
        {
            if (background.ToImmutable() is not ISolidColorBrush backgroundSolid)
                continue;
            foundSolidBackground = true;
            var contrast = GetContrastRatio(candidateSolid.Color, backgroundSolid.Color);
            if (contrast < worstContrast)
            {
                worstContrast = contrast;
                worstBackground = backgroundSolid.Color;
            }
        }

        if (!foundSolidBackground || worstContrast >= MinimumReadableContrast)
            return candidate;

        KodoDiagnostics.LogDebug(
            $"Theme text colour failed contrast check ({worstContrast:0.00}:1, needs {MinimumReadableContrast:0.0}:1) " +
            "against its own background - falling back to a safe colour so text stays readable.");

        return GetReadableForeground(worstBackground);
    }

    // Always reflects the live Windows accent colour; used by the Windows blob
    // preview even when another accent mode is active.
    public IBrush WindowsAccentPreviewBrush { get; private set; } =
        Brush.Parse("#0078D4");

    public string AccentColorMode
    {
        get => _accentColorMode;
        set
        {
            if (_accentColorMode == value) return;
            _accentColorMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAccentKodo));
            OnPropertyChanged(nameof(IsAccentWindows));
            OnPropertyChanged(nameof(IsAccentCustom));
            OnPropertyChanged(nameof(IsAccentTheme));
        }
    }
    public bool IsAccentKodo    => _accentColorMode == "kodo";
    public bool IsAccentWindows => _accentColorMode == "windows";
    public bool IsAccentCustom  => _accentColorMode == "custom";
    // True when the active theme supplies a preset accent colour.
    // When true the "Theme" blob is shown alongside "Kodo", "Windows", and "Custom".
    public bool HasThemeAccent  => _hasThemeAccent;
    // The "Theme" blob is active when the user has explicitly chosen to follow
    // the theme-supplied accent (mode == "theme").
    public bool IsAccentTheme   => _accentColorMode == "theme";
    // Solid-colour preview brush for the Theme blob, always reflects the
    // accent supplied by the currently active extension theme.
    public IBrush ThemeAccentPreviewBrush { get; private set; } = Brush.Parse("#8C00FF");

    public string CustomAccentHex
    {
        get => _customAccentHex;
        set
        {
            if (_customAccentHex == value) return;
            _customAccentHex = value;
            OnPropertyChanged();
        }
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add    => ViewModelPropertyChanged += value;
        remove => ViewModelPropertyChanged -= value;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        ViewModelPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OpenTabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            IsFileExplorerVisible = true;
        OnPropertyChanged(nameof(HasOpenEditors));
        OnPropertyChanged(nameof(HasMultipleOpenEditors));
        OnPropertyChanged(nameof(IsEditorTabsVisible));
        SaveSettings();
    }

    private void FileTreeItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressExplorerWidthRefresh)
            OnPropertyChanged(nameof(ExplorerPanelWidth));
    }

    private void TerminalSessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<TerminalSession>())
                item.PropertyChanged += TerminalSession_OnPropertyChanged;
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<TerminalSession>())
            {
                item.PropertyChanged -= TerminalSession_OnPropertyChanged;
                item.Dispose();
            }
        }

        OnPropertyChanged(nameof(HasActiveTerminal));
        OnPropertyChanged(nameof(HasTerminalSessions));
        OnPropertyChanged(nameof(TerminalSessionCount));
        OnPropertyChanged(nameof(TerminalStatusBarText));
        OnPropertyChanged(nameof(ActiveTerminalFooterText));
        RefreshTerminalWindows();
        SaveSettings();
    }

    private void TerminalSession_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalSession.WorkingDirectory) or nameof(TerminalSession.StatusText) or nameof(TerminalSession.IsRunning) or nameof(TerminalSession.WindowHandle))
        {
            RefreshTerminalStatusBindings();
            RefreshTerminalWindows();
        }
    }

    private void RefreshTerminalStatusBindings()
    {
        OnPropertyChanged(nameof(ActiveTerminalWorkingDirectory));
        OnPropertyChanged(nameof(ActiveTerminalStatusText));
        OnPropertyChanged(nameof(ActiveTerminalFooterText));
        OnPropertyChanged(nameof(TerminalStatusBarText));
    }

    // State management

    private void RefreshState(bool fullRefresh = false)
    {
        RefreshCaretAndDocumentStats();

        if (!fullRefresh)
            return;

        _pendingFullStateRefresh = false;
        RefreshWordCount();
        RefreshNonCaretState();
    }

    private void RefreshCaretAndDocumentStats()
    {
        var document = EditorTextBox?.Document;
        if (HasDocumentOpen && !IsHomePageVisible)
        {
            if (CurrentImagePreview is not null)
            {
                EditorStatsText = $"{CurrentImagePreview.PixelSize.Width} x {CurrentImagePreview.PixelSize.Height}px";
            }
            else
            {
                var lines      = document?.LineCount ?? 1;
                var characters = document?.TextLength ?? 0;
                var caret      = EditorTextBox?.TextArea?.Caret;
                var ln         = caret?.Line ?? 1;
                var col        = caret?.Column ?? 1;
                EditorStatsText = $"Ln {ln}, Col {col}  |  {lines} lines  |  {characters} characters";
            }
        }
        else
        {
            EditorStatsText = string.Empty;
        }
    }

    private void RefreshNonCaretState()
    {
        Title = BuildWindowTitle();
        OnPropertyChanged(nameof(HasDocumentOpen));
        OnPropertyChanged(nameof(IsDocumentViewVisible));
        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(IsImagePreviewVisible));
        OnPropertyChanged(nameof(IsTextEditorVisible));
        OnPropertyChanged(nameof(CanShowFindInFile));
        OnPropertyChanged(nameof(CanShowSearchPanel));
        OnPropertyChanged(nameof(IsSearchPanelActive));
        OnPropertyChanged(nameof(CanShowSaveActions));
        OnPropertyChanged(nameof(IsWordCountVisible));
        OnPropertyChanged(nameof(HasFileOpen));
        OnPropertyChanged(nameof(IsFolderOpen));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(FilePathText));
        OnPropertyChanged(nameof(ExplorerHeaderText));
        OnPropertyChanged(nameof(ExplorerHeaderTooltipText));
        OnPropertyChanged(nameof(ExplorerPanelMinWidth));
        OnPropertyChanged(nameof(DiscordRichPresenceStatusText));
        OnPropertyChanged(nameof(AutoSaveStatusText));
        OnPropertyChanged(nameof(LanguageDisplayText));
        OnPropertyChanged(nameof(EncodingDisplayText));
        OnPropertyChanged(nameof(ActiveTerminalWorkingDirectory));
        OnPropertyChanged(nameof(ActiveTerminalFooterText));
        OnPropertyChanged(nameof(TerminalStatusBarText));
        UpdateDiscordPresence();
    }

    private void QueueRefreshState(bool fullRefresh = false)
    {
        _pendingFullStateRefresh |= fullRefresh;
        _editorStateRefreshTimer.Stop();
        _editorStateRefreshTimer.Start();
    }

    private void EditorStateRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _editorStateRefreshTimer.Stop();
        RefreshState(fullRefresh: _pendingFullStateRefresh);
    }

    private void QueueWordCountRefresh()
    {
        _wordCountRefreshTimer.Stop();
        _wordCountRefreshTimer.Start();
    }

    private void QueueInsightRefresh()
    {
        _InsightRefreshTimer.Stop();
        _InsightRefreshTimer.Start();
    }

    private void InsightRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _InsightRefreshTimer.Stop();
        UpdateInsight();
    }

    private void WordCountRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _wordCountRefreshTimer.Stop();
        RefreshWordCount();
        OnPropertyChanged(nameof(IsWordCountVisible));
    }

    private void RefreshWordCount()
    {
        if (!HasDocumentOpen || !IsPlainTextFile(_currentFilePath) || EditorTextBox?.Document is null)
        {
            WordCountText = string.Empty;
            return;
        }

        var text = EditorTextBox.Document.Text;
        var wordCount = string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        WordCountText = $"{wordCount} words";
    }

    private string GetDocumentStatusSuffix()
    {
        var text = GetDocumentStatusText();
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $" • {text}";
    }

    private string? GetDocumentStatusText()
    {
        if (!HasDocumentOpen) return null;

        if (IsAutoSaveEnabled && HasFileOpen)
        {
            if (!string.IsNullOrWhiteSpace(_autoSaveStatusMessage))
                return _autoSaveStatusMessage;

            if (_isSaving)
                return AutoSaveSavingMessage;

            if (_isDirty || _autoSaveTimer.IsEnabled)
                return "Unsaved";
        }

        return _isDirty ? "Unsaved" : null;
    }

    // Window icon

    private void LoadWindowIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Kodo/Assets/kodo-logo.png"));
        Icon = new WindowIcon(iconStream);
    }

    // Discord Rich Presence

    private string? GetDiscordApplicationId()
    {
        var overrideClientId = Environment.GetEnvironmentVariable(DiscordClientIdEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overrideClientId) ? DefaultDiscordClientId : overrideClientId;
    }

    private void UpdateDiscordRichPresenceLifecycle()
    {
        _discordReconnectTimer.Stop();
        try
        {
            var clientId = GetDiscordApplicationId();
            if (!IsDiscordRichPresenceEnabled || string.IsNullOrWhiteSpace(clientId))
            {
                DisposeDiscordPresence();
                return;
            }

            if (_discordRpcClient is null)
            {
                _discordRpcClient = new DiscordRpcClient(clientId);
                _discordRpcClient.Initialize();
            }

            UpdateDiscordPresence();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discord] Failed to initialise Rich Presence: {ex.Message}");
            ResetDiscordPresenceForReconnect();
            _discordReconnectTimer.Start();
        }
    }

    private void UpdateDiscordPresence()
    {
        if (_discordRpcClient is null || !IsDiscordRichPresenceEnabled) return;

        // Compares primitive inputs first to avoid rebuilding presence strings every tick.
        var currentKey = GetDiscordPresenceKey();
        if (currentKey == _lastDiscordPresenceKey) return;
        _lastDiscordPresenceKey = currentKey;

        try
        {
            var details = GetDiscordPresenceDetails();
            var state   = GetDiscordPresenceState();

            _discordRpcClient.SetPresence(new DiscordRichPresenceModel
            {
                Details    = details,
                State      = state,
                Assets     = new DiscordAssetsModel
                {
                    LargeImageKey  = DefaultDiscordLargeImageKey,
                    LargeImageText = DefaultDiscordLargeImageText
                },
                Timestamps = new DiscordRPC.Timestamps(_sessionStart)
            });
            _lastDiscordPresenceDetails = details;
            _lastDiscordPresenceState   = state;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discord] Failed to update Rich Presence: {ex.Message}");
            ResetDiscordPresenceForReconnect();
            _discordReconnectTimer.Start();
        }
    }

    // Cheap tuple key built from the primitive fields that drive presence strings.
    // Avoids allocating display strings on every 75 ms refresh tick.
    private (string? filePath, string? folderPath, int tabCount,
             string? language, bool settings, bool extensions, bool home,
             bool improved) GetDiscordPresenceKey() =>
        (_currentFilePath, _currentFolderPath, OpenTabs.Count,
         GetDiscordLanguageLabel(), _isSettingsPageVisible, _isExtensionsPageVisible,
         _isHomePageVisible, _isDiscordImprovedRpcEnabled);

    private string GetDiscordPresenceDetails() =>
        _isDiscordImprovedRpcEnabled
            ? GetDiscordPresenceDetailsImproved()
            : GetDiscordPresenceDetailsClassic();

    private string GetDiscordPresenceState() =>
        _isDiscordImprovedRpcEnabled
            ? GetDiscordPresenceStateImproved()
            : GetDiscordPresenceStateClassic();

    // Classic presence (original behaviour)

    private string GetDiscordPresenceDetailsClassic()
    {
        if (HasDocumentOpen) return $"Editing {GetDocumentDisplayName()}";
        return IsFolderOpen ? "Browsing project files" : "Idle in Kodo";
    }

    private string GetDiscordPresenceStateClassic()
    {
        if (HasFileOpen)          return GetDiscordWorkspaceLabel();
        if (_hasUntitledDocument) return GetDiscordWorkspaceLabel("Editing an Unsaved file");
        if (IsFolderOpen)         return GetDiscordWorkspaceLabel();
        return "Waiting for a file";
    }

    // Improved presence (experimental)

    private string GetDiscordPresenceDetailsImproved()
    {
        if (_isSettingsPageVisible)   return "Tweaking settings";
        if (_isExtensionsPageVisible) return "Browsing extensions";
        if (_isHomePageVisible)       return "On the home screen";
        if (_isTutorialPageVisible)   return "Following the tutorial";
        if (_isWhatsNewPageVisible)   return "Reading what's new";

        if (HasDocumentOpen)
        {
            var fileName = GetDocumentDisplayName();
            var lang     = GetDiscordLanguageLabel();
            return string.IsNullOrWhiteSpace(lang)
                ? $"Editing {fileName}"
                : $"Editing {fileName}  \u00b7  {lang}";
        }

        return IsFolderOpen ? "Browsing project files" : "Idle in Kodo";
    }

    private string? GetDiscordLanguageLabel()
    {
        var extension = CurrentLanguageExtension;
        if (extension is null)
            return null;

        var name = extension.Name
            .Replace("Language Support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return extension.Id
            .Replace("-kodo-extension", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-language-support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private string GetDiscordPresenceStateImproved()
    {
        if (HasFileOpen)          return GetDiscordWorkspaceLabelImproved();
        if (_hasUntitledDocument) return GetDiscordWorkspaceLabelImproved("Editing an unsaved file");
        if (IsFolderOpen)         return GetDiscordWorkspaceLabelImproved();
        return "Waiting for a file";
    }

    private string GetDiscordWorkspaceLabelImproved(string fallback = "Working in editor")
    {
        if (!IsFolderOpen) return fallback;
        var folderName = Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) return fallback;
        var tabCount = OpenTabs.Count;
        return tabCount > 1
            ? $"Workspace: {folderName}  ({tabCount} files open)"
            : $"Workspace: {folderName}";
    }

    private string GetDiscordWorkspaceLabel(string fallback = "Working in editor")
    {
        if (!IsFolderOpen) return fallback;
        var folderName = Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folderName) ? fallback : $"Workspace: {folderName}";
    }

    private void DisposeDiscordPresence(bool clearPresence = true)
    {
        _discordReconnectTimer.Stop();
        if (_discordRpcClient is null) return;
        try
        {
            if (clearPresence)
                _discordRpcClient.ClearPresence();
            _discordRpcClient.Dispose();
        }
        catch { /* Ignore cleanup failures. */ }
        finally
        {
            _discordRpcClient = null;
            _lastDiscordPresenceDetails = string.Empty;
            _lastDiscordPresenceState   = string.Empty;
            _lastDiscordPresenceKey     = default;
        }
    }

    private void ResetDiscordPresenceForReconnect()
    {
        if (_discordRpcClient is null)
            return;

        try
        {
            _discordRpcClient.Dispose();
        }
        catch { /* Ignore reconnect cleanup failures. */ }
        finally
        {
            _discordRpcClient = null;
        }
    }

    private void DiscordReconnectTimer_OnTick(object? sender, EventArgs e)
    {
        _discordReconnectTimer.Stop();
        UpdateDiscordRichPresenceLifecycle();
    }

    // Settings persistence

    // Keybinds -------------------------------------------------------------
    // A small, fixed set of app-level commands whose gesture can be changed from
    // Settings -> Help -> View Shortcuts. Editor-local single-key bindings (Tab, Enter,
    // Backspace) and context-dependent ones (terminal search navigation, image zoom) aren't
    // included - they either need a modifier-free key (which would collide with typing) or
    // only make sense in one specific context.
    //
    // IsContextLocal marks bindings that only fire while a specific control is focused
    // (currently the terminal). Two bindings are considered a conflict only when they share
    // the same scope, so e.g. Ctrl+V can be "Paste" in the editor and "Paste" in the terminal
    // at the same time.
    private sealed record KeybindDefinition(string Id, string Description, KeyGesture Default, string Category, bool IsContextLocal = false);

    private static readonly KeybindDefinition[] KeybindDefinitions =
    {
        new("GoHome",             "Go to Home",             new KeyGesture(Key.H, KeyModifiers.Control),                        "Navigation"),
        new("GoEditor",           "Go to Editor",            new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift),  "Navigation"),
        new("OpenSettings",       "Open Settings",           new KeyGesture(Key.OemComma, KeyModifiers.Control),                 "Navigation"),
        new("OpenExtensions",     "Open Marketplace",        new KeyGesture(Key.E, KeyModifiers.Control),                        "Navigation"),
        new("OpenExtensionsAlt",  "Open Marketplace (alternate)", new KeyGesture(Key.X, KeyModifiers.Control | KeyModifiers.Shift), "Navigation"),
        new("CloseOverlay",       "Close search / Settings / Extensions / Tutorial / What's New", new KeyGesture(Key.Escape, KeyModifiers.None), "Navigation"),
        new("NewFile",            "New file",                new KeyGesture(Key.N, KeyModifiers.Control),                        "Files & Tabs"),
        new("OpenFile",           "Open file",               new KeyGesture(Key.O, KeyModifiers.Control),                        "Files & Tabs"),
        new("OpenFolder",         "Open folder",             new KeyGesture(Key.K, KeyModifiers.Control),                        "Files & Tabs"),
        new("CloseFolder",        "Close folder",            new KeyGesture(Key.K, KeyModifiers.Control | KeyModifiers.Shift),  "Files & Tabs"),
        new("Save",               "Save",                    new KeyGesture(Key.S, KeyModifiers.Control),                        "Files & Tabs"),
        new("SaveAs",             "Save as",                 new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift),  "Files & Tabs"),
        new("CloseTab",           "Close tab",               new KeyGesture(Key.W, KeyModifiers.Control),                        "Files & Tabs"),
        new("FindInFile",         "Find in file",            new KeyGesture(Key.F, KeyModifiers.Control),                        "Editor"),
        new("FindInProject",      "Find in project",         new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift),  "Editor"),
        new("ToggleFileExplorer", "Toggle file explorer",    new KeyGesture(Key.B, KeyModifiers.Control),                        "Editor"),
        new("ToggleLineComment",  "Toggle line comment",     new KeyGesture(Key.Oem2, KeyModifiers.Control),                     "Editor"),
        new("Cut",                "Cut",                     new KeyGesture(Key.X, KeyModifiers.Control),                        "Editor"),
        new("Copy",               "Copy",                    new KeyGesture(Key.C, KeyModifiers.Control),                        "Editor"),
        new("Paste",              "Paste",                   new KeyGesture(Key.V, KeyModifiers.Control),                        "Editor"),
        new("ToggleTerminal",     "Toggle terminal panel",   new KeyGesture(Key.J, KeyModifiers.Control),                        "Terminal"),
        new("ToggleTerminalAlt",  "Toggle terminal panel (alternate)", new KeyGesture(Key.Oem3, KeyModifiers.Control),           "Terminal"),
        new("NewTerminalSession", "New terminal session",   new KeyGesture(Key.Oem3, KeyModifiers.Control | KeyModifiers.Shift),"Terminal"),
        new("TerminalCopy",       "Copy selection (in terminal)", new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift), "Terminal", IsContextLocal: true),
        new("TerminalPaste",      "Paste (in terminal)",    new KeyGesture(Key.V, KeyModifiers.Control),                        "Terminal", IsContextLocal: true),
        new("TerminalSearch",     "Search terminal output",  new KeyGesture(Key.F, KeyModifiers.Control),                        "Terminal", IsContextLocal: true),
        new("ZoomIn",             "Zoom in (image viewer)",  new KeyGesture(Key.OemPlus, KeyModifiers.Control),                  "Image viewer"),
        new("ZoomOut",            "Zoom out (image viewer)", new KeyGesture(Key.OemMinus, KeyModifiers.Control),                 "Image viewer"),
        new("ZoomReset",          "Reset zoom (image viewer)", new KeyGesture(Key.D0, KeyModifiers.Control),                     "Image viewer"),
    };

    private Dictionary<string, KeyGesture> _keybinds = new(StringComparer.OrdinalIgnoreCase);

    private static string SerializeGesture(KeyGesture gesture) => $"{gesture.KeyModifiers}|{gesture.Key}";

    private static KeyGesture? DeserializeGesture(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('|');
        if (parts.Length != 2) return null;
        if (!Enum.TryParse<KeyModifiers>(parts[0], out var modifiers)) return null;
        if (!Enum.TryParse<Key>(parts[1], out var key)) return null;
        return new KeyGesture(key, modifiers);
    }

    // Mirrors the capture dialog's rule (Settings -> Help -> View Shortcuts): a gesture is
    // only assignable if it carries at least one modifier key or is a bare function key.
    // Bare keys like Enter, Tab, or Escape are deliberately not customizable - they'd
    // otherwise collide with typing and (in the terminal) with keys the shell expects to
    // receive as raw input. Hand-edited settings files are validated against this too, so
    // an invalid gesture can never take effect even if it wasn't entered through the dialog.
    private static bool IsValidKeybindGesture(KeyGesture gesture) =>
        gesture.KeyModifiers != KeyModifiers.None ||
        (gesture.Key >= Key.F1 && gesture.Key <= Key.F24);

    private void InitializeKeybinds(AppSettings settings)
    {
        _keybinds = new Dictionary<string, KeyGesture>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in KeybindDefinitions)
        {
            var gesture = def.Default;
            if (settings.CustomKeybinds is not null &&
                settings.CustomKeybinds.TryGetValue(def.Id, out var raw) &&
                DeserializeGesture(raw) is { } custom &&
                IsValidKeybindGesture(custom))
            {
                gesture = custom;
            }
            _keybinds[def.Id] = gesture;
        }

        // Shares the live keybind table with the terminal so its copy/paste/search
        // gestures follow the user's customizations (and any in-place edits from the
        // shortcuts dialog) without re-wiring.
        TerminalHostControl.Keybinds = _keybinds;
    }

    // Only the deltas from default are persisted, so future default changes (e.g. a new
    // build remapping a command) still take effect for users who never touched that bind.
    private Dictionary<string, string> BuildCustomKeybindsSnapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in KeybindDefinitions)
        {
            if (_keybinds.TryGetValue(def.Id, out var gesture) &&
                (gesture.Key != def.Default.Key || gesture.KeyModifiers != def.Default.KeyModifiers))
            {
                result[def.Id] = SerializeGesture(gesture);
            }
        }
        return result;
    }

    // e.g. "Ctrl+Shift+F", ",", "Ctrl+/" - a friendlier rendering than KeyGesture.ToString()
    // for the Oem* keys that show up in our default bindings.
    private static string FormatGesture(KeyGesture gesture)
    {
        var parts = new List<string>();
        if ((gesture.KeyModifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((gesture.KeyModifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((gesture.KeyModifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((gesture.KeyModifiers & KeyModifiers.Meta) != 0) parts.Add("Win");
        parts.Add(FormatKey(gesture.Key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key) => key switch
    {
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.Oem3 => "`",
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        _ => key.ToString(),
    };

    // True if the pressed key event matches the current (possibly user-customized)
    // gesture for the given command id. Falls back to false for unknown ids.
    private bool MatchesKeybind(KeyEventArgs e, string id) =>
        _keybinds.TryGetValue(id, out var gesture) &&
        e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;

    private string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", SettingsFileName);

    private AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return new AppSettings();

            var json = File.ReadAllText(SettingsFilePath);

            // An empty or whitespace-only file means a previous write was interrupted.
            // Treat it like a missing file rather than overwriting with defaults.
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

            if (json.Contains("\"CodePredictEnabled\"", StringComparison.Ordinal))
                json = json.Replace("\"CodePredictEnabled\"", "\"InsightEnabled\"", StringComparison.Ordinal);

            // Cap recursion depth so a deeply-nested or adversarial settings file
            // cannot cause a StackOverflowException inside the deserializer.
            var opts = new JsonSerializerOptions { MaxDepth = 32 };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, opts);
            if (settings is null) return new AppSettings();

            settings.ThemeName = string.IsNullOrWhiteSpace(settings.ThemeName) ? "Dark" : settings.ThemeName;
            settings.RecentFiles = settings.RecentFiles?
                .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.IsPinned).ThenByDescending(e => e.LastOpened).First())
                .ToList() ?? [];
            settings.OpenTabPaths = settings.OpenTabPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            settings.TabSize = NormalizeTabSize(settings.TabSize);
            settings.TerminalPanelHeight = TerminalShellSupport.NormalizeTerminalPanelHeight(settings.TerminalPanelHeight);
            settings.ExplorerPanelWidth = NormalizeExplorerPanelWidth(settings.ExplorerPanelWidth);
            return settings;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("MainWindow.LoadSettings", ex, operation: $"Failed to load settings from '{SettingsFilePath}'");
            return new AppSettings();
        }
    }

    private void SaveSettings(bool immediate = false, bool synchronous = false)
    {
        if (_suppressSettingsSave) return;

        if (!immediate)
        {
            _settingsSaveDebounceTimer.Stop();
            _settingsSaveDebounceTimer.Start();
            return;
        }

        _settingsSaveDebounceTimer.Stop();
        PersistSettingsSnapshot(BuildSettingsSnapshot(), synchronous);
    }

    private void SettingsSaveDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _settingsSaveDebounceTimer.Stop();
        if (_suppressSettingsSave)
            return;

        PersistSettingsSnapshot(BuildSettingsSnapshot());
    }

    private AppSettings BuildSettingsSnapshot()
    {
        // Snapshot all UI-thread-owned state here, before the background task,
        // so we don't access ObservableCollections or bound properties from a background thread.
        return new AppSettings
        {
            ThemeName                              = _requestedThemeName,
            AutoSaveEnabled                        = IsAutoSaveEnabled,
            DiscordRichPresenceEnabled             = IsDiscordRichPresenceEnabled,
            DiscordImprovedRpcEnabled              = IsDiscordImprovedRpcEnabled,
            DeveloperOptionsVisible                = IsDeveloperOptionsVisible,
            VerboseLoggingEnabled                   = IsVerboseLoggingEnabled,
            StatusBarFilePathVisible               = IsStatusBarFilePathVisible,
            WordWrapEnabled                        = IsWordWrapEnabled,
            InsightEnabled                        = IsInsightEnabled,
            InsightBlacklistExtensions           = InsightBlacklistExtensions,
            TabSize                                = TabSize,
            EditorFontSize                         = EditorFontSize,
            ConfirmBeforeClosingUnsavedTabsEnabled  = IsConfirmBeforeClosingUnsavedTabsEnabled,
            RestoreOpenTabsOnLaunchEnabled          = IsRestoreOpenTabsOnLaunchEnabled,
            AutoUpdateExtensionsEnabled             = IsAutoUpdateExtensionsEnabled,
            AutoUpdateExtensionsInBackgroundEnabled = IsAutoUpdateExtensionsInBackgroundEnabled,
            AutoUpdateAppEnabled                    = IsAutoUpdateAppEnabled,
            AutoUpdateAppInBackgroundEnabled         = IsAutoUpdateAppInBackgroundEnabled,
            PreferredTerminalShellId                = SelectedTerminalShell?.Id,
            PSReadLinePredictionEnabled              = IsPSReadLinePredictionEnabled,
            TerminalVisible                         = IsTerminalVisible,
            TerminalPanelHeight                     = TerminalPanelHeight,
            ExplorerPanelWidth                      = ExplorerPanelWidth,
            HasCompletedTutorial                    = _hasCompletedTutorial,
            AccentColorMode                         = _accentColorMode,
            CustomAccentHex                         = _customAccentHex,
            CachedThemeAccentHex                    = _hasThemeAccent ? _themeAccentHex : null,
            CachedThemeWindowBackgroundHex           = _hasWindowBackground ? _windowBackgroundHex : null,
            UserCountry                             = _userCountry,
            UserHemisphere                          = _userHemisphere,
            UserTimezoneOffset                      = _userTimezoneOffset,
            UserName                                = _userName,
            LastSeenVersion                         = CurrentAppVersion,
            AllowDataTracking                       = _isDataTrackingEnabled,
            HasRespondedToDataTrackingPrompt        = _hasRespondedToDataTrackingPrompt,
            HasAcceptedPrivacyPolicy                = _hasAcceptedPrivacyPolicy,
            OpenTabPaths = OpenTabs
                .Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path))
                .Select(tab => tab.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ActiveTabPath = ActiveEditorTab is { IsUntitled: false } activeTab
                ? activeTab.Path
                : null,
            LastOpenedFolderPath = _currentFolderPath,
            RecentFiles = RecentFiles
                .Select(f => new RecentFileEntry { Path = f.Path, IsFolder = f.IsFolder, LastOpened = f.LastOpened, IsPinned = f.IsPinned })
                .ToList(),
            CustomRunCommands = new Dictionary<string, string>(_customRunCommands, StringComparer.OrdinalIgnoreCase),
            CustomBuildCommands = new Dictionary<string, string>(_customBuildCommands, StringComparer.OrdinalIgnoreCase),
            CompilerOverrides = new Dictionary<string, string>(_compilerOverrides, StringComparer.OrdinalIgnoreCase),
            CustomKeybinds = BuildCustomKeybindsSnapshot()
        };
    }

    private void PersistSettingsSnapshot(AppSettings snapshot, bool synchronous = false)
    {
        void WriteToDisk(AppSettings toWrite)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                // Writes to a temp file first, then atomically replaces the real file.
                var tempPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(toWrite));
                File.Move(tempPath, SettingsFilePath, overwrite: true);
            }
            catch (Exception ex) { KodoDiagnostics.LogWarning("MainWindow.PersistSettingsSnapshot", ex, operation: $"Failed to save settings to '{SettingsFilePath}'"); }
        }

        // Shutdown save runs synchronously, under the same lock as the background writer,
        // so the last write reaches disk before exit.
        if (synchronous)
        {
            lock (_settingsWriteLock)
            {
                WriteToDisk(snapshot);
                _pendingSettingsSnapshot = null;
            }
            return;
        }

        lock (_settingsWriteLock)
        {
            // Always replace, never queue multiple - only the most recent snapshot
            // matters, and this is how concurrent calls get coalesced into one write.
            _pendingSettingsSnapshot = snapshot;
            if (_isPersistingSettings) return; // a writer loop is already running and will pick this up
            _isPersistingSettings = true;
        }

        Task.Run(() =>
        {
            while (true)
            {
                AppSettings toWrite;
                lock (_settingsWriteLock)
                {
                    if (_pendingSettingsSnapshot is null)
                    {
                        _isPersistingSettings = false;
                        return;
                    }
                    toWrite = _pendingSettingsSnapshot;
                    _pendingSettingsSnapshot = null;
                }

                WriteToDisk(toWrite);
            }
        });
    }

    private IBrush GetCachedBrush(string colorValue)
    {
        if (_brushCache.TryGetValue(colorValue, out var brush))
            return brush;

        brush = Brush.Parse(colorValue);
        _brushCache[colorValue] = brush;
        return brush;
    }

    // Theme application


    /// Sets theme brushes and <see cref="Application.RequestedThemeVariant"/> without notifications, saves, or refresh.
    /// Call before <c>DataContext = this</c> so bindings read correct colors on first evaluation.

    private void ApplyThemeBrushes(string themeName)
    {
        _requestedThemeName = themeName;
        // "System" isn't a real palette - resolve it to Windows' current setting first.
        var effectiveThemeName = string.Equals(themeName, "System", StringComparison.OrdinalIgnoreCase)
            ? ResolveSystemThemeName()
            : themeName;
        var extensionTheme = ThemeExtensions
            .Select(e => e.ThemeDefinition!)
            .FirstOrDefault(t => string.Equals(t.ThemeId, effectiveThemeName, StringComparison.OrdinalIgnoreCase));

        if (extensionTheme is not null)
        {
            CurrentThemeName = extensionTheme.ThemeId;
            Application.Current!.RequestedThemeVariant = string.Equals(extensionTheme.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            WindowBackgroundBrush = GetCachedBrush(extensionTheme.WindowBackground);
            TopBarBrush           = GetCachedBrush(extensionTheme.TopBar);
            SidebarBrush          = GetCachedBrush(extensionTheme.Sidebar);
            ButtonBrush           = GetCachedBrush(extensionTheme.Button);
            ButtonHoverBrush      = GetCachedBrush(extensionTheme.ButtonHover);
            EditorBackgroundBrush = GetCachedBrush(extensionTheme.EditorBackground);
            CardBrush             = GetCachedBrush(extensionTheme.Card);
            PrimaryTextBrush      = GetCachedBrush(extensionTheme.PrimaryText);
            MutedTextBrush        = GetCachedBrush(extensionTheme.MutedText);
            // Theme-pack colors aren't guaranteed readable - verify and fall back to a safe color.
            PrimaryTextBrush = EnsureReadableTextBrush(PrimaryTextBrush, CardBrush, WindowBackgroundBrush, EditorBackgroundBrush, SidebarBrush, TopBarBrush, ButtonBrush);
            MutedTextBrush   = EnsureReadableTextBrush(MutedTextBrush, CardBrush, WindowBackgroundBrush, EditorBackgroundBrush, SidebarBrush, TopBarBrush, ButtonBrush);
            SurfaceBorderBrush    = GetCachedBrush(extensionTheme.SurfaceBorder);
            AccentBrush           = GetCachedBrush(extensionTheme.Accent);
            _themeAccentHex       = extensionTheme.Accent;
            _hasThemeAccent       = true;
            _windowBackgroundHex  = extensionTheme.WindowBackground;
            _hasWindowBackground  = true;
            ThemeAccentPreviewBrush = GetCachedBrush(extensionTheme.Accent);
        }
        else
        {
            CurrentThemeName = effectiveThemeName == "Light" ? "Light" : "Dark";
            Application.Current!.RequestedThemeVariant = CurrentThemeName == "Light"
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            if (CurrentThemeName == "Light")
            {
                WindowBackgroundBrush = GetCachedBrush("#F3F3F3");
                TopBarBrush           = GetCachedBrush("#FFFFFF");
                SidebarBrush          = GetCachedBrush("#EFF2F7");
                ButtonBrush           = GetCachedBrush("#E3E8F1");
                ButtonHoverBrush      = GetCachedBrush("#D5DDE9");
                EditorBackgroundBrush = GetCachedBrush("#FFFFFF");
                CardBrush             = GetCachedBrush("#F7F9FC");
                PrimaryTextBrush      = GetCachedBrush("#202124");
                MutedTextBrush        = GetCachedBrush("#5F6B7A");
                SurfaceBorderBrush    = GetCachedBrush("#D7DCE5");
                AccentBrush           = GetCachedBrush("#8C00FF");
                _themeAccentHex       = "#8C00FF";
                _windowBackgroundHex  = "#F3F3F3";
            }
            else
            {
                WindowBackgroundBrush = GetCachedBrush("#1E1E1E");
                TopBarBrush           = GetCachedBrush("#181818");
                SidebarBrush          = GetCachedBrush("#181818");
                ButtonBrush           = GetCachedBrush("#252526");
                ButtonHoverBrush      = GetCachedBrush("#313437");
                EditorBackgroundBrush = GetCachedBrush("#1E1E1E");
                CardBrush             = GetCachedBrush("#252526");
                PrimaryTextBrush      = GetCachedBrush("#F4F4F4");
                MutedTextBrush        = GetCachedBrush("#A0A0A0");
                SurfaceBorderBrush    = GetCachedBrush("#2B2B2B");
                AccentBrush           = GetCachedBrush("#8C00FF");
                _themeAccentHex       = "#8C00FF";
                _windowBackgroundHex  = "#1E1E1E";
            }
            _hasThemeAccent         = false;
            _hasWindowBackground    = false;
            ThemeAccentPreviewBrush = GetCachedBrush("#8C00FF");
        }

        // Silently resolves the accent hex without the full ApplyAccentOverride().
        // WindowsAccentPreviewBrush is initialised from the live registry here too.
        var windowsHex = GetWindowsAccentColor() ?? "#0078D4";
        try { WindowsAccentPreviewBrush = GetCachedBrush(windowsHex); }
        catch { WindowsAccentPreviewBrush = GetCachedBrush("#0078D4"); }

        var resolvedAccent = _accentColorMode switch
        {
            "theme"   => _themeAccentHex,
            "windows" => windowsHex,
            "custom"  => _customAccentHex,
            _         => "#8C00FF"   // "kodo" - always the fixed Kodo purple
        };
        try { AccentBrush = GetCachedBrush(resolvedAccent); }
        catch { AccentBrush = GetCachedBrush("#8C00FF"); }
        AccentForegroundBrush = GetAccentForeground(AccentBrush);
        SyncSystemAccentResources(AccentBrush);

        // Initialises the System Default preview from the registry now, not on the first poll tick.
        RefreshSystemThemePreview();
    }

    private void ApplyTheme(string themeName)
    {
        _requestedThemeName = themeName;
        // "System" isn't a real palette - resolve it to Windows' current reporting before the lookup below.
        var effectiveThemeName = string.Equals(themeName, "System", StringComparison.OrdinalIgnoreCase)
            ? ResolveSystemThemeName()
            : themeName;
        var extensionTheme = ThemeExtensions
            .Select(e => e.ThemeDefinition!)
            .FirstOrDefault(t => string.Equals(t.ThemeId, effectiveThemeName, StringComparison.OrdinalIgnoreCase));

        if (extensionTheme is not null)
        {
            CurrentThemeName = extensionTheme.ThemeId;
            Application.Current!.RequestedThemeVariant = string.Equals(extensionTheme.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            WindowBackgroundBrush = GetCachedBrush(extensionTheme.WindowBackground);
            TopBarBrush           = GetCachedBrush(extensionTheme.TopBar);
            SidebarBrush          = GetCachedBrush(extensionTheme.Sidebar);
            ButtonBrush           = GetCachedBrush(extensionTheme.Button);
            ButtonHoverBrush      = GetCachedBrush(extensionTheme.ButtonHover);
            EditorBackgroundBrush = GetCachedBrush(extensionTheme.EditorBackground);
            CardBrush             = GetCachedBrush(extensionTheme.Card);
            PrimaryTextBrush      = GetCachedBrush(extensionTheme.PrimaryText);
            MutedTextBrush        = GetCachedBrush(extensionTheme.MutedText);
            // Theme-pack colors aren't guaranteed readable against each other - verify and fall back to a safe color.
            PrimaryTextBrush = EnsureReadableTextBrush(PrimaryTextBrush, CardBrush, WindowBackgroundBrush, EditorBackgroundBrush, SidebarBrush, TopBarBrush, ButtonBrush);
            MutedTextBrush   = EnsureReadableTextBrush(MutedTextBrush, CardBrush, WindowBackgroundBrush, EditorBackgroundBrush, SidebarBrush, TopBarBrush, ButtonBrush);
            SurfaceBorderBrush    = GetCachedBrush(extensionTheme.SurfaceBorder);
            AccentBrush           = GetCachedBrush(extensionTheme.Accent);
            _themeAccentHex       = extensionTheme.Accent;
            _hasThemeAccent       = true;
            _windowBackgroundHex  = extensionTheme.WindowBackground;
            _hasWindowBackground  = true;
            ThemeAccentPreviewBrush = GetCachedBrush(extensionTheme.Accent);
        }
        else
        {
            CurrentThemeName = effectiveThemeName == "Light" ? "Light" : "Dark";
            Application.Current!.RequestedThemeVariant = CurrentThemeName == "Light"
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            if (CurrentThemeName == "Light")
            {
                WindowBackgroundBrush = GetCachedBrush("#F3F3F3");
                TopBarBrush           = GetCachedBrush("#FFFFFF");
                SidebarBrush          = GetCachedBrush("#EFF2F7");
                ButtonBrush           = GetCachedBrush("#E3E8F1");
                ButtonHoverBrush      = GetCachedBrush("#D5DDE9");
                EditorBackgroundBrush = GetCachedBrush("#FFFFFF");
                CardBrush             = GetCachedBrush("#F7F9FC");
                PrimaryTextBrush      = GetCachedBrush("#202124");
                MutedTextBrush        = GetCachedBrush("#5F6B7A");
                SurfaceBorderBrush    = GetCachedBrush("#D7DCE5");
                AccentBrush           = GetCachedBrush("#8C00FF");
                _themeAccentHex       = "#8C00FF";
                _windowBackgroundHex  = "#F3F3F3";
            }
            else
            {
                WindowBackgroundBrush = GetCachedBrush("#1E1E1E");
                TopBarBrush           = GetCachedBrush("#181818");
                SidebarBrush          = GetCachedBrush("#181818");
                ButtonBrush           = GetCachedBrush("#252526");
                ButtonHoverBrush      = GetCachedBrush("#313437");
                EditorBackgroundBrush = GetCachedBrush("#1E1E1E");
                CardBrush             = GetCachedBrush("#252526");
                PrimaryTextBrush      = GetCachedBrush("#F4F4F4");
                MutedTextBrush        = GetCachedBrush("#A0A0A0");
                SurfaceBorderBrush    = GetCachedBrush("#2B2B2B");
                AccentBrush           = GetCachedBrush("#8C00FF");
                _themeAccentHex       = "#8C00FF";
                _windowBackgroundHex  = "#1E1E1E";
            }
            _hasThemeAccent         = false;
            _hasWindowBackground    = false;
            ThemeAccentPreviewBrush = GetCachedBrush("#8C00FF");
        }

        OnPropertyChanged(nameof(WindowBackgroundBrush));
        OnPropertyChanged(nameof(TopBarBrush));
        OnPropertyChanged(nameof(SidebarBrush));
        OnPropertyChanged(nameof(ButtonBrush));
        OnPropertyChanged(nameof(ButtonHoverBrush));
        OnPropertyChanged(nameof(EditorBackgroundBrush));
        OnPropertyChanged(nameof(CardBrush));
        OnPropertyChanged(nameof(PrimaryTextBrush));
        OnPropertyChanged(nameof(MutedTextBrush));
        OnPropertyChanged(nameof(SurfaceBorderBrush));
        OnPropertyChanged(nameof(HasThemeAccent));
        OnPropertyChanged(nameof(IsAccentKodo));
        OnPropertyChanged(nameof(IsAccentTheme));
        OnPropertyChanged(nameof(ThemeAccentPreviewBrush));
        OnPropertyChanged(nameof(IsSystemThemeActive));
        OnPropertyChanged(nameof(IsDarkThemeActive));
        OnPropertyChanged(nameof(IsLightThemeActive));
        RefreshSystemThemePreview();
        // Always runs ApplyAccentOverride: updates AccentBrush for all three modes and keeps WindowsAccentPreviewBrush live.
        ApplyAccentOverride();
        ApplyThemeToEditor();
        SaveSettings();
        RefreshState(fullRefresh: true);
        RefreshExtensionTheme();
    }

    private void ApplyAccentOverride()
    {
        // Always keep the Windows preview brush current so the blob reflects
        // the real system colour regardless of which mode is active.
        var windowsHex = GetWindowsAccentColor() ?? "#0078D4";
        try { WindowsAccentPreviewBrush = GetCachedBrush(windowsHex); }
        catch { WindowsAccentPreviewBrush = GetCachedBrush("#0078D4"); }
        OnPropertyChanged(nameof(WindowsAccentPreviewBrush));

        // In "kodo" mode, always use the fixed Kodo purple regardless of any active theme.
        if (_accentColorMode == "kodo")
        {
            try { AccentBrush = GetCachedBrush("#8C00FF"); }
            catch { AccentBrush = GetCachedBrush("#8C00FF"); }
            AccentForegroundBrush = GetAccentForeground(AccentBrush);
            SyncSystemAccentResources(AccentBrush);
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(AccentForegroundBrush));
            ApplyThemeToEditor();
            return;
        }

        var hex = _accentColorMode switch
        {
            "theme"   => _themeAccentHex,
            "windows" => windowsHex,
            "custom"  => _customAccentHex,
            _         => "#8C00FF"
        };
        try { AccentBrush = GetCachedBrush(hex); }
        catch { AccentBrush = GetCachedBrush("#8C00FF"); }
        AccentForegroundBrush = GetAccentForeground(AccentBrush);
        SyncSystemAccentResources(AccentBrush);
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(AccentForegroundBrush));
        ApplyThemeToEditor();
    }

    private static string? GetWindowsAccentColor()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int raw)
            {
                // AccentColorMenu is stored as AABBGGRR
                var r = (raw)       & 0xFF;
                var g = (raw >> 8)  & 0xFF;
                var b = (raw >> 16) & 0xFF;
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { /* Registry unavailable */ }
        return null;
    }

    // Reads the same registry value Windows uses for light/dark chrome; null means unreadable, not a definite answer.
    private static bool? GetWindowsAppsUseLightTheme()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int raw)
                return raw != 0;
        }
        catch { /* Registry unavailable */ }
        return null;
    }

    // Resolves "System" to the concrete Light/Dark theme Windows currently reports, falling back to Dark.
    private static string ResolveSystemThemeName() =>
        GetWindowsAppsUseLightTheme() == true ? "Light" : "Dark";

    // Keeps the System Default preview live regardless of the active theme mode.
    private void RefreshSystemThemePreview()
    {
        var isLight = GetWindowsAppsUseLightTheme() == true;
        SystemThemePreviewBackground = GetCachedBrush(isLight ? "#FFFFFF" : "#1E1E1E");
        SystemThemePreviewBorder     = GetCachedBrush(isLight ? "#D7DCE5" : "#2B2B2B");
        OnPropertyChanged(nameof(SystemThemePreviewBackground));
        OnPropertyChanged(nameof(SystemThemePreviewBorder));
    }


    private async void AccentColorPickerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Window? dialog = null;
        var confirmed = false;

        var initialColor = Color.Parse("#8C00FF");
        try { initialColor = Color.Parse(_customAccentHex); } catch { /* use fallback */ }

        RgbToHsv(initialColor.R, initialColor.G, initialColor.B,
            out var hue, out var sat, out var val);

        // Hue strip
        var hueCanvas = new Canvas { Width = 300, Height = 20 };
        var hueGrad   = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        foreach (var (offset, h) in new (double, double)[]
            { (0,0),(1/6d,60),(2/6d,120),(3/6d,180),(4/6d,240),(5/6d,300),(1,360) })
        {
            HsvToRgb(h, 1, 1, out var hr2, out var hg2, out var hb2);
            hueGrad.GradientStops.Add(new GradientStop(Color.FromRgb(hr2, hg2, hb2), offset));
        }
        var hueRect = new Avalonia.Controls.Shapes.Rectangle
            { Width = 300, Height = 20, Fill = hueGrad, RadiusX = 4, RadiusY = 4 };
        hueCanvas.Children.Add(hueRect);

        var hueCursor = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 4, Height = 24, Fill = Brushes.White, RadiusX = 2, RadiusY = 2,
            Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1,
        };
        Canvas.SetTop(hueCursor, -2);
        Canvas.SetLeft(hueCursor, hue / 360.0 * 296);
        hueCanvas.Children.Add(hueCursor);

        // SV square
        const double svSize   = 300.0;
        const double svHeight = 180.0;
        var svCanvas = new Canvas { Width = svSize, Height = svHeight };

        var svHueFill      = new Avalonia.Controls.Shapes.Rectangle { Width = svSize, Height = svHeight, RadiusX = 4, RadiusY = 4 };
        var svWhiteOverlay = new Avalonia.Controls.Shapes.Rectangle { Width = svSize, Height = svHeight, RadiusX = 4, RadiusY = 4 };
        var svBlackOverlay = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = svSize, Height = svHeight, RadiusX = 4, RadiusY = 4,
            Fill  = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0,   0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 1),
                },
            },
        };

        void RefreshSvSquare()
        {
            HsvToRgb(hue, 1, 1, out var hr3, out var hg3, out var hb3);
            svHueFill.Fill = new SolidColorBrush(Color.FromRgb(hr3, hg3, hb3));
            svWhiteOverlay.Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0,   255, 255, 255), 1),
                },
            };
        }
        RefreshSvSquare();

        svCanvas.Children.Add(svHueFill);
        svCanvas.Children.Add(svWhiteOverlay);
        svCanvas.Children.Add(svBlackOverlay);

        var svCursor = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            Stroke = Brushes.White, StrokeThickness = 2,
            Fill   = new SolidColorBrush(Colors.Transparent),
        };
        Canvas.SetLeft(svCursor, sat * svSize   - 6);
        Canvas.SetTop (svCursor, (1 - val) * svHeight - 6);
        svCanvas.Children.Add(svCursor);

        // Preview swatch + hex input
        var previewBorder = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
            BorderBrush = SurfaceBorderBrush, BorderThickness = new Thickness(1),
        };

        var hexInput = new TextBox
        {
            Text            = _customAccentHex,
            PlaceholderText = "#RRGGBB",
            MaxLength       = 7,
            Foreground      = PrimaryTextBrush,
            Background      = ButtonBrush,
            BorderBrush     = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(8, 6),
            FontSize        = 13,
            CaretBrush      = PrimaryTextBrush,
            Width           = 110,
        };

        // Sync helpers
        void UpdateAll()
        {
            HsvToRgb(hue, sat, val, out var r2, out var g2, out var b2);
            var c = Color.FromRgb(r2, g2, b2);
            previewBorder.Background = new SolidColorBrush(c);
            hexInput.Text = $"#{r2:X2}{g2:X2}{b2:X2}";
            Canvas.SetLeft(hueCursor, hue / 360.0 * 296);
            Canvas.SetLeft(svCursor,  sat * svSize   - 6);
            Canvas.SetTop (svCursor,  (1 - val) * svHeight - 6);
            RefreshSvSquare();
        }
        UpdateAll();

        // Hue drag
        hueCanvas.PointerPressed  += (_, pe) =>
        {
            pe.Pointer.Capture(hueCanvas);
            hue = Math.Clamp(pe.GetPosition(hueCanvas).X / 300.0 * 360, 0, 360);
            UpdateAll();
        };
        hueCanvas.PointerMoved    += (_, pe) =>
        {
            if (pe.Pointer.Captured != hueCanvas) return;
            hue = Math.Clamp(pe.GetPosition(hueCanvas).X / 300.0 * 360, 0, 360);
            UpdateAll();
        };
        hueCanvas.PointerReleased += (_, pe) => pe.Pointer.Capture(null);

        // SV drag
        svCanvas.PointerPressed  += (_, pe) =>
        {
            pe.Pointer.Capture(svCanvas);
            var p = pe.GetPosition(svCanvas);
            sat = Math.Clamp(p.X / svSize,        0, 1);
            val = Math.Clamp(1 - p.Y / svHeight,  0, 1);
            UpdateAll();
        };
        svCanvas.PointerMoved    += (_, pe) =>
        {
            if (pe.Pointer.Captured != svCanvas) return;
            var p = pe.GetPosition(svCanvas);
            sat = Math.Clamp(p.X / svSize,        0, 1);
            val = Math.Clamp(1 - p.Y / svHeight,  0, 1);
            UpdateAll();
        };
        svCanvas.PointerReleased += (_, pe) => pe.Pointer.Capture(null);

        // Hex sync
        hexInput.TextChanged += (_, _) =>
        {
            try
            {
                var t = hexInput.Text?.Trim() ?? "";
                if (!t.StartsWith('#')) t = "#" + t;
                var c = Color.Parse(t);
                RgbToHsv(c.R, c.G, c.B, out hue, out sat, out val);
                previewBorder.Background = new SolidColorBrush(c);
                Canvas.SetLeft(hueCursor, hue / 360.0 * 296);
                Canvas.SetLeft(svCursor,  sat * svSize   - 6);
                Canvas.SetTop (svCursor,  (1 - val) * svHeight - 6);
                RefreshSvSquare();
            }
            catch { /* wait for valid hex */ }
        };

        hexInput.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)  { confirmed = true; dialog!.Close(); }
            if (ke.Key == Key.Escape) { dialog!.Close(); }
        };

        dialog = new Window
        {
            Width                 = 340,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            ShowInTaskbar         = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title                 = IsAmericanEnglish ? "Custom Accent Color" : "Custom Accent Colour",
            Background            = CardBrush,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child   = new StackPanel
                {
                    Spacing  = 14,
                    Children =
                    {
                        new TextBlock { Text = IsAmericanEnglish ? "Choose an accent color" : "Choose an accent colour", FontSize = 15,
                            FontWeight = FontWeight.SemiBold, Foreground = PrimaryTextBrush },
                        svCanvas,
                        hueCanvas,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing     = 10,
                            Children    = { previewBorder, hexInput },
                        },
                        new StackPanel
                        {
                            Orientation         = Orientation.Horizontal,
                            Spacing             = 10,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children =
                            {
                                CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush,
                                    () => dialog!.Close()),
                                CreateDialogButton("Apply", AccentBrush, AccentBrush, AccentForegroundBrush,
                                    () => { confirmed = true; dialog!.Close(); }),
                            }
                        }
                    }
                }
            }
        };

        dialog.Opened += (_, _) => { hexInput.Focus(); hexInput.SelectAll(); };
        await dialog.ShowDialog(this);

        if (!confirmed) return;
        var hex = hexInput.Text?.Trim() ?? string.Empty;
        if (!hex.StartsWith('#')) hex = "#" + hex;
        try
        {
            Brush.Parse(hex);
            _customAccentHex = hex;
            CustomAccentHex  = hex;
            AccentColorMode  = "custom";
            ApplyAccentOverride();
            SaveSettings();
        }
        catch { /* invalid hex - ignore */ }
    }

    // Converts RGB (0–255) to HSV (H: 0–360, S/V: 0–1).
    private static void RgbToHsv(byte r, byte g, byte b,
        out double h, out double s, out double v)
    {
        var rf = r / 255.0; var gf = g / 255.0; var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        v = max;
        s = max == 0 ? 0 : delta / max;
        if (delta == 0) { h = 0; return; }
        if      (max == rf) h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
        else                h = 60 * (((rf - gf) / delta) + 4);
        if (h < 0) h += 360;
    }

    // Converts HSV (H: 0–360, S/V: 0–1) to RGB (0–255).
    private static void HsvToRgb(double h, double s, double v,
        out byte r, out byte g, out byte b)
    {
        if (s == 0) { r = g = b = (byte)(v * 255); return; }
        var i = (int)(h / 60) % 6;
        var f = h / 60 - Math.Floor(h / 60);
        var p = v * (1 - s); var q = v * (1 - f * s); var t = v * (1 - (1 - f) * s);
        var (rf, gf, bf) = i switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        r = (byte)(rf * 255); g = (byte)(gf * 255); b = (byte)(bf * 255);
    }

    private void AccentKodoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AccentColorMode = "kodo";
        ApplyAccentOverride();
        SaveSettings();
    }

    private void AccentThemeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AccentColorMode = "theme";
        ApplyAccentOverride();
        SaveSettings();
    }

    private void AccentWindowsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AccentColorMode = "windows";
        ApplyAccentOverride();
        SaveSettings();
    }

    // File operations

    private void SaveCurrentEditorStateIntoTab()
    {
        if (ActiveEditorTab is null || EditorTextBox?.Document is null)
            return;

        if (HasImagePreview)
        {
            ActiveEditorTab.IsDirty = false;
            if (!ActiveEditorTab.IsUntitled && !string.IsNullOrWhiteSpace(_currentFilePath))
                ActiveEditorTab.Path = _currentFilePath;
            return;
        }

        ActiveEditorTab.Content = EditorTextBox.Document.Text;
        ActiveEditorTab.IsDirty = _isDirty;
        var scrollOffset = EditorTextBox.TextArea.TextView.ScrollOffset;
        ActiveEditorTab.TopLineNumber = EditorTextBox.TextArea.TextView.GetDocumentLineByVisualTop(
            scrollOffset.Y)?.LineNumber ?? 1;
        // Also save the exact pixel offset so restoration is sub-line-accurate.
        ActiveEditorTab.ScrollOffsetY = scrollOffset.Y;
        ActiveEditorTab.CaretOffset = EditorTextBox.TextArea.Caret.Offset;
        if (!ActiveEditorTab.IsUntitled && !string.IsNullOrWhiteSpace(_currentFilePath))
            ActiveEditorTab.Path = _currentFilePath;
    }

    private EditorTab CreateUntitledTab()
    {
        var displayName = $"untitled-{_nextUntitledTabNumber++}.txt";
        return new EditorTab(displayName, displayName, string.Empty, isUntitled: true);
    }

    private void ActivateTab(EditorTab tab, bool focusEditor = true, bool preserveCurrentState = true)
    {
        if (ReferenceEquals(ActiveEditorTab, tab))
        {
            // Force page state even if NavigateTo bails early due to no change
            _isHomePageVisible = false;
            NavigateTo(Page.Editor);
            RefreshState(fullRefresh: true);
            if (focusEditor)
                FocusEditor();
            return;
        }

        CloseCompletionWindow();
        if (preserveCurrentState)
            SaveCurrentEditorStateIntoTab();
        ActiveEditorTab = tab;
        _currentFilePath = tab.IsUntitled ? null : tab.Path;
        _hasUntitledDocument = tab.IsUntitled;
        _isDirty = tab.IsDirty;
        _autoSaveTimer.Stop();
        ClearAutoSaveStatus();
        SetFileCorrupted(_corruptedTabs.Contains(tab));
        SetEditorContent(IsImagePreviewFile(_currentFilePath) ? string.Empty : tab.Content);
        // Restores this tab's caret position (clamped to the current document length).
        EditorTextBox.TextArea.Caret.Offset = Math.Clamp(tab.CaretOffset, 0, EditorTextBox.Document.TextLength);
        // Restores scroll position: ScrollToLine first, then a pixel offset at Background priority.
        EditorTextBox.ScrollToLine(tab.TopLineNumber);
        var savedOffsetY = tab.ScrollOffsetY;
        if (savedOffsetY > 0.0)
        {
            // Posts at Background priority so AvaloniaEdit finishes its layout pass before we reposition the viewport.
            Dispatcher.UIThread.Post(() =>
            {
                var sv = EditorTextBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (sv is not null)
                    sv.Offset = new Vector(sv.Offset.X, savedOffsetY);
            }, DispatcherPriority.Background);
        }
        UpdateCurrentDocumentPresentation();

        // Directly set the backing field before NavigateTo so the bail-early
        // check doesn't short-circuit when we're already on the editor page.
        _isHomePageVisible = false;
        NavigateTo(Page.Editor);
        RefreshState(fullRefresh: true);

        if (focusEditor)
            FocusEditor();
    }

    private void CloseTab(EditorTab tab)
    {
        var closingActiveTab = ReferenceEquals(tab, ActiveEditorTab);
        var index = OpenTabs.IndexOf(tab);
        if (index < 0)
            return;

        if (closingActiveTab)
            CloseCompletionWindow();
        _InsightEngine.ForgetFile(tab.Path);
        OpenTabs.RemoveAt(index);
        _corruptedTabs.Remove(tab);

        if (!closingActiveTab)
        {
            RefreshState(fullRefresh: true);
            return;
        }

        if (OpenTabs.Count > 0)
        {
            var nextIndex = Math.Min(index, OpenTabs.Count - 1);
            ActivateTab(OpenTabs[nextIndex], focusEditor: true, preserveCurrentState: false);
            return;
        }

        ActiveEditorTab = null;
        _currentFilePath = null;
        _hasUntitledDocument = false;
        _isDirty = false;
        CurrentLanguageExtension = null;
        CurrentImagePreview = null;
        SetFileCorrupted(false);
        EditorTextBox.SyntaxHighlighting = null;
        ConfigureRainbowBrackets(null);
        SetEditorContent(string.Empty);

        // No tabs and no folder left open - the explorer would otherwise be
        // stuck visible with nothing to show and no folder-gated toggle to
        // close it (that toggle only appears when IsFolderOpen).
        if (_currentFolderPath is null)
            IsFileExplorerVisible = false;

        RefreshState(fullRefresh: true);
    }

    private async Task<bool> RequestCloseTabAsync(EditorTab tab)
    {
        if (tab.IsDirty && IsConfirmBeforeClosingUnsavedTabsEnabled)
        {
            var originalActiveTab = ActiveEditorTab;
            var action = await ShowUnsavedTabDialogAsync(tab);
            switch (action)
            {
                case UnsavedTabAction.Cancel:
                    return false;
                case UnsavedTabAction.Save:
                    if (!ReferenceEquals(tab, ActiveEditorTab))
                    {
                        ActivateTab(tab, focusEditor: false);
                    }

                    await SaveAsync();

                    if (tab.IsDirty)
                    {
                        if (originalActiveTab is not null &&
                            !ReferenceEquals(originalActiveTab, tab) &&
                            OpenTabs.Contains(originalActiveTab))
                        {
                            ActivateTab(originalActiveTab, focusEditor: false);
                        }

                        return false;
                    }

                    if (originalActiveTab is not null &&
                        !ReferenceEquals(originalActiveTab, tab) &&
                        OpenTabs.Contains(originalActiveTab))
                    {
                        ActivateTab(originalActiveTab, focusEditor: false);
                    }
                    break;
            }
        }

        CloseTab(tab);
        return true;
    }

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = false
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        await OpenFileFromPathAsync(path);
    }

    // Invoked (via SingleInstance) when a second Kodo launch was redirected here instead
    // of opening its own window - e.g. double-clicking another file in Explorer while
    // Kodo is already running. Brings this window to front and opens the file as a tab.
    public async void ActivateFromSecondaryInstance(string? filePath)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();

        var trimmedPath = filePath?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(trimmedPath) && File.Exists(trimmedPath))
        {
            await OpenFileFromPathAsync(trimmedPath);
        }
    }

    // Central method used by Open File, Open From Tree, and Open Recent
    private async Task OpenFileFromPathAsync(string path)
    {
        EnsureCurrentDocumentHasTab();

        var existingTab = OpenTabs.FirstOrDefault(tab =>
            !tab.IsUntitled &&
            string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existingTab is not null)
        {
            AddRecentFile(path);
            ActivateTab(existingTab);
            return;
        }

        string content;
        bool isCorrupted;
        try
        {
            if (IsImagePreviewFile(path))
            {
                content = string.Empty;
                isCorrupted = false;
                _currentFileEncoding = System.Text.Encoding.UTF8;
            }
            else
            {
                // Offload both the binary-sniff read and the encoding-BOM read to a
                // thread-pool thread so the UI stays responsive on large or slow files.
                var (encoding, corrupted) = await Task.Run(() =>
                {
                    if (IsBinaryContent(path))
                        return (System.Text.Encoding.UTF8, true);
                    return (DetectFileEncoding(path), false);
                });

                isCorrupted = corrupted;
                _currentFileEncoding = encoding;
                content = isCorrupted ? string.Empty : await File.ReadAllTextAsync(path, encoding);
            }
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Open file", ex);
            return;
        }

        // Navigates away from Home before adding the tab (mirrors NewFile()).
        NavigateTo(Page.Editor);

        var tab = new EditorTab(path, Path.GetFileName(path), content);
        if (isCorrupted)
            _corruptedTabs.Add(tab);
        OpenTabs.Add(tab);
        AddRecentFile(path);
        ActivateTab(tab);
    }

    private void EnsureCurrentDocumentHasTab()
    {
        if (ActiveEditorTab is not null || !HasDocumentOpen)
        {
            return;
        }

        var displayName = _currentFilePath is not null
            ? Path.GetFileName(_currentFilePath)
            : $"untitled-{_nextUntitledTabNumber++}.txt";
        var path = _currentFilePath ?? displayName;
        var content = IsImagePreviewFile(_currentFilePath) ? string.Empty : EditorTextBox?.Document?.Text ?? string.Empty;
        var recoveredTab = new EditorTab(path, displayName, content, isUntitled: _currentFilePath is null)
        {
            IsDirty = _isDirty
        };

        OpenTabs.Add(recoveredTab);
        ActivateTab(recoveredTab, focusEditor: false);
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_OnOpened;

        // Applies the editor theme now that the window and editor exist.
        ApplyThemeToEditor();

        // _suppressSettingsSave stays true while tabs restore, cleared in a finally block.
        try
        {
            if (IsRestoreOpenTabsOnLaunchEnabled && _startupOpenTabPaths.Count > 0)
            {
                // Only restore the folder if one of the tabs being restored actually
                // came from it - otherwise a stale LastOpenedFolderPath from a folder
                // the user has since closed would reopen unexpectedly.
                if (!string.IsNullOrWhiteSpace(_startupFolderPath) &&
                    _startupOpenTabPaths.Any(path => IsPathInsideDirectory(path, _startupFolderPath!)))
                {
                    await OpenFolderFromPathAsync(_startupFolderPath!);
                }

                foreach (var path in _startupOpenTabPaths)
                {
                    await OpenFileFromPathAsync(path);
                }

                if (!string.IsNullOrWhiteSpace(_startupActiveTabPath))
                {
                    var activeTab = OpenTabs.FirstOrDefault(tab =>
                        !tab.IsUntitled &&
                        string.Equals(tab.Path, _startupActiveTabPath, StringComparison.OrdinalIgnoreCase));
                    if (activeTab is not null)
                    {
                        ActivateTab(activeTab);
                    }
                }
            }

            // Open the file passed on the command line (e.g. via "Open with" or double-click)
            if (!string.IsNullOrWhiteSpace(_startupFilePath))
            {
                await OpenFileFromPathAsync(_startupFilePath);
            }
        }
        finally
        {
            // Re-enable saves so that any *real* future change (the user toggling
            // a setting, opening/closing a tab, etc.) persists normally.
            _suppressSettingsSave = false;

            // Only forces a write if a settings file already existed.
            if (!_isFirstLaunch)
            {
                SaveSettings(immediate: true);
            }
        }

        _ = RefreshExtensionsAndAutoUpdateAsync();
        _ = RefreshLatestReleaseAsync();
        _ = FetchAnnouncementsAsync(forceNetwork: false);
        _ = FetchSportingEventMessagesAsync();

        // Exactly one of tutorial (first launch) or What's New splash (subsequent launches) shows per run.
        var isReturningUser = _hasCompletedTutorial;

        if (_isFirstLaunch && !_hasCompletedTutorial)
        {
            await ShowTutorialAsync();
        }
        else if (isReturningUser)
        {
            var showReleaseNotes = !IsDevBuild && IsCurrentNewerThanLastSeen(_lastSeenVersion);
            var showConsentAsk   = !_hasRespondedToDataTrackingPrompt || !_hasAcceptedPrivacyPolicy;

            _openingSplashShowsReleaseNotes = showReleaseNotes;
            if (showReleaseNotes || showConsentAsk)
            {
                if (!_hasAcceptedPrivacyPolicy)
                    ResetPrivacyPolicyScrollState();
                IsUpdateSplashVisible = true;
            }
        }
    }

    private void NewFile()
    {
        // Navigate away from home BEFORE adding the tab, so the CollectionChanged
        // notification evaluates IsEditorTabsVisible with IsHomePageVisible already false.
        NavigateTo(Page.Editor);

        var tab = CreateUntitledTab();
        OpenTabs.Add(tab);
        ActivateTab(tab);
    }

    private async Task OpenFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Folder",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null) return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        await OpenFolderFromPathAsync(path);
    }

    // Shared by the folder picker and by startup tab restoration, so a folder
    // opened either way ends up in the same state (tree populated, watcher armed,
    // added to Recent).
    private async Task OpenFolderFromPathAsync(string path)
    {
        _currentFolderPath = path;
        _searchFileCache = null;
        AddRecentFolder(path);
        await PopulateFileTreeAsync(path);
        SetupProjectFolderWatcher(path);
        IsFileExplorerVisible = true;
        RefreshState(fullRefresh: true);
        RefreshRunBuildState();
    }

    private void CloseFolder()
    {
        DisposeProjectFolderWatcher();
        _currentFolderPath = null;
        _searchFileCache = null;
        FileTreeItems.Clear();
        IsFileExplorerVisible = false;
        RefreshState(fullRefresh: true);
        RefreshRunBuildState();
    }

    private async Task<bool> SaveAsync(bool allowPromptForPath, bool forcePromptForPath)
    {
        _autoSaveTimer.Stop();
        if (_isSaving) return false;
        if (HasImagePreview) return false;

        var shouldPromptForPath = forcePromptForPath || _currentFilePath is null;
        if (shouldPromptForPath)
        {
            if (!allowPromptForPath) return false;

            var suggestedFileName = ActiveEditorTab?.DisplayName;
            if (string.IsNullOrWhiteSpace(suggestedFileName))
                suggestedFileName = HasFileOpen ? Path.GetFileName(_currentFilePath) : "untitled.txt";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = forcePromptForPath ? "Save File As" : "Save File",
                SuggestedFileName = suggestedFileName
            });

            var newPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(newPath)) return false;

            _currentFilePath = newPath;
            _hasUntitledDocument = false;
            if (ActiveEditorTab is not null)
                ActiveEditorTab.Rename(newPath, Path.GetFileName(newPath));
            ClearAutoSaveStatus();
            RefreshCurrentFileSyntaxHighlighting();
        }

        try
        {
            _isSaving = true;
            if (IsAutoSaveEnabled && HasFileOpen)
            {
                _autoSaveStatusMessage = AutoSaveSavingMessage;
                _autoSaveStatusTimer.Stop();
                OnPropertyChanged(nameof(FileSummaryText));
            }

            // Read content directly from the TextEditor document
            await File.WriteAllTextAsync(_currentFilePath!, EditorTextBox.Document.Text);
            _isDirty = false;
            if (ActiveEditorTab is not null)
            {
                ActiveEditorTab.Content = EditorTextBox.Document.Text;
                ActiveEditorTab.IsDirty = false;
                if (ActiveEditorTab.IsUntitled)
                    ActiveEditorTab.IsUntitled = false;
            }
            AddRecentFile(_currentFilePath);
            RefreshCurrentFileSyntaxHighlighting();

            if (IsAutoSaveEnabled && HasFileOpen)
            {
                _autoSaveStatusMessage = AutoSaveSavedMessage;
                _autoSaveStatusTimer.Stop();
                _autoSaveStatusTimer.Start();
            }

            RefreshState(fullRefresh: true);
            return true;
        }
        catch (Exception ex)
        {
            _autoSaveStatusTimer.Stop();
            _autoSaveStatusMessage = BuildAutoSaveFailureMessage(ex);
            OnPropertyChanged(nameof(FileSummaryText));
            OnPropertyChanged(nameof(AutoSaveStatusText));
            await ShowWarningDialogAsync("File save", ex);
            return false;
        }
        finally
        {
            _isSaving = false;
            OnPropertyChanged(nameof(FileSummaryText));
            OnPropertyChanged(nameof(AutoSaveStatusText));
        }
    }

    private async Task SaveAsync(bool allowPromptForPath = true) =>
        await SaveAsync(allowPromptForPath, forcePromptForPath: false);

    private async Task<bool> SaveAsAsync() =>
        await SaveAsync(allowPromptForPath: true, forcePromptForPath: true);

    // File tree

    private async Task PopulateFileTreeAsync(string folderPath)
    {
        var items = await CreateFileTreeItemsAsync(folderPath, depth: 0);
        ReplaceFileTreeItems(items);
    }

    // Starts (or restarts, if the folder changed) watching the open project
    // folder for external changes so the tree can refresh itself automatically.
    private void SetupProjectFolderWatcher(string folderPath)
    {
        DisposeProjectFolderWatcher();

        try
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Created += ProjectFolderWatcher_OnChanged;
            watcher.Deleted += ProjectFolderWatcher_OnChanged;
            watcher.Renamed += ProjectFolderWatcher_OnChanged;
            watcher.Error   += ProjectFolderWatcher_OnError;
            watcher.EnableRaisingEvents = true;

            _projectFolderWatcher = watcher;
        }
        catch
        {
            // Some folders (network shares, permission-restricted directories, huge
            // trees) can't be watched - the explorer just won't auto-refresh for them.
        }
    }

    private void DisposeProjectFolderWatcher()
    {
        _fileTreeRefreshTimer.Stop();

        if (_projectFolderWatcher is null) return;

        _projectFolderWatcher.Created -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Deleted -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Renamed -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Error   -= ProjectFolderWatcher_OnError;
        _projectFolderWatcher.Dispose();
        _projectFolderWatcher = null;
    }

    // Raised on the watcher's background thread - hop to the UI thread before
    // touching the DispatcherTimer or anything else.
    private void ProjectFolderWatcher_OnChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(RestartFileTreeRefreshTimer);

    private void ProjectFolderWatcher_OnError(object sender, ErrorEventArgs e) =>
        // Internal buffer overflow or the watched folder became inaccessible -
        // fall back to a full refresh rather than trying to recover the watcher.
        Dispatcher.UIThread.Post(RestartFileTreeRefreshTimer);

    private void RestartFileTreeRefreshTimer()
    {
        _fileTreeRefreshTimer.Stop();
        _fileTreeRefreshTimer.Start();
    }

    private async void FileTreeRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _fileTreeRefreshTimer.Stop();
        _searchFileCache = null;
        await RefreshFileTreePreservingExpansionAsync();
    }

    // Rebuilds the tree from disk, re-expanding whichever directories were
    // expanded beforehand so an external change doesn't collapse the user's view.
    private async Task RefreshFileTreePreservingExpansionAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath) || !Directory.Exists(_currentFolderPath))
            return;

        var expandedPaths = FileTreeItems
            .Where(i => i.IsDirectory && i.IsExpanded)
            .Select(i => i.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = await BuildFileTreeItemsAsync(_currentFolderPath, depth: 0, expandedPaths);
        ReplaceFileTreeItems(items);
    }

    // Like CreateFileTreeItemsAsync, but recurses into any directory whose path is
    // in expandedPaths so previously-expanded subtrees come back expanded.
    private static async Task<List<FileTreeItem>> BuildFileTreeItemsAsync(
        string dirPath, int depth, HashSet<string> expandedPaths)
    {
        var result = new List<FileTreeItem>();
        foreach (var item in await CreateFileTreeItemsAsync(dirPath, depth))
        {
            result.Add(item);
            if (item.IsDirectory && expandedPaths.Contains(item.FullPath))
            {
                item.IsExpanded = true;
                result.AddRange(await BuildFileTreeItemsAsync(item.FullPath, depth + 1, expandedPaths));
            }
        }
        return result;
    }

    private async Task AppendDirectoryContentsAsync(string dirPath, int depth, int insertAfterIndex = -1)
    {
        var items = await CreateFileTreeItemsAsync(dirPath, depth);
        if (items.Count == 0) return;

        var pos = insertAfterIndex + 1;
        foreach (var item in items)
        {
            if (insertAfterIndex < 0)
                FileTreeItems.Add(item);
            else
            {
                FileTreeItems.Insert(pos, item);
                pos++;
            }
        }
    }

    private void ReplaceFileTreeItems(IReadOnlyList<FileTreeItem> items)
    {
        _suppressExplorerWidthRefresh = true;
        try
        {
            FileTreeItems.Clear();
            foreach (var item in items)
                FileTreeItems.Add(item);
        }
        finally
        {
            _suppressExplorerWidthRefresh = false;
            OnPropertyChanged(nameof(ExplorerPanelWidth));
        }
    }

    private static Task<List<FileTreeItem>> CreateFileTreeItemsAsync(string dirPath, int depth) =>
        Task.Run(() => GetSortedEntries(dirPath)
            .Select(entry => new FileTreeItem
            {
                Name        = Path.GetFileName(entry),
                FullPath    = entry,
                IsDirectory = Directory.Exists(entry),
                Depth       = depth,
            })
            .ToList());

    private static string[] GetSortedEntries(string dirPath)
    {
        try
        {
            var dirs = Directory.GetDirectories(dirPath)
                .Where(d => !IsHiddenOnDisk(d))
                .OrderBy(d => Path.GetFileName(d), NaturalSortComparer.OrdinalIgnoreCase)
                .ToArray();

            var files = Directory.GetFiles(dirPath)
                .Where(f => !IsHiddenOnDisk(f))
                .OrderBy(f => Path.GetFileName(f), NaturalSortComparer.OrdinalIgnoreCase)
                .ToArray();

            return [.. dirs, .. files];
        }
        catch { return []; }
    }

    // Mirrors Explorer's notion of "hidden": the actual Hidden/System file
    // attributes, not a Unix-style leading dot. ".gitignore", ".env", etc. have
    // no such attribute on Windows and so should show up like any other file.
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

    private async Task ToggleDirectoryExpansionAsync(FileTreeItem dirItem)
    {
        var index = FileTreeItems.IndexOf(dirItem);
        if (index < 0) return;

        if (dirItem.IsExpanded)
        {
            dirItem.IsExpanded = false;
            // Collect all descendants first, then remove in reverse index order
            // so each removal doesn't shift the indices of subsequent items.
            var toRemove = FileTreeItems
                .Skip(index + 1)
                .TakeWhile(i => i.Depth > dirItem.Depth)
                .ToList();
            for (var i = toRemove.Count - 1; i >= 0; i--)
                FileTreeItems.Remove(toRemove[i]);
        }
        else
        {
            dirItem.IsExpanded = true;
            await AppendDirectoryContentsAsync(dirItem.FullPath, dirItem.Depth + 1, insertAfterIndex: index);
        }
    }

    // Recent files

    private void LoadRecentFiles(IEnumerable<RecentFileEntry>? recentFiles)
    {
        RecentFiles.Clear();

        // Doesn't filter files under a recent folder's tree - only active-folder files collapse.
        // Doesn't filter by File/Directory.Exists - unreachable paths should still reappear.
        foreach (var entry in (recentFiles ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .OrderByDescending(entry => entry.IsPinned)
            .ThenByDescending(entry => entry.LastOpened))
        {
            RecentFiles.Add(new RecentFileItem(entry.Path, entry.IsFolder, entry.LastOpened, entry.IsPinned));
        }
    }

    private void AddRecentFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            !string.IsNullOrWhiteSpace(_currentFolderPath) &&
            IsPathInsideDirectory(path, _currentFolderPath))
        {
            AddRecentFolder(_currentFolderPath);
            return;
        }

        AddRecentPath(path, isFolder: false);
    }

    private void AddRecentFolder(string? path) =>
        AddRecentPath(path, isFolder: true);

    private void AddRecentPath(string? path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = RecentFiles.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            RecentFiles.Remove(existing);
            RecentFiles.Add(new RecentFileItem(path, isFolder, DateTime.Now, existing.IsPinned));
        }
        else
        {
            RecentFiles.Add(new RecentFileItem(path, isFolder, DateTime.Now));
        }

        ReorderRecentFiles();
        SaveSettings();
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private void TogglePinnedRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;

        existing.IsPinned = !existing.IsPinned;
        ReorderRecentFiles();
        SaveSettings();
    }

    private void RemoveRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        RecentFiles.Remove(existing);
        SaveSettings();
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private void ReorderRecentFiles()
    {
        var ordered = RecentFiles
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastOpened)
            .ToList();

        RecentFiles.Clear();
        foreach (var item in ordered)
            RecentFiles.Add(item);

        while (RecentFiles.Count(item => !item.IsPinned) > MaxRecentFiles)
        {
            var removable = RecentFiles.LastOrDefault(item => !item.IsPinned);
            if (removable is null)
                break;

            RecentFiles.Remove(removable);
        }
    }

    private void ZoomInButton_OnClick(object? sender, RoutedEventArgs e)  => ZoomImageIn();
    private void ZoomOutButton_OnClick(object? sender, RoutedEventArgs e) => ZoomImageOut();
    private void ZoomResetButton_OnClick(object? sender, RoutedEventArgs e) => ZoomImageReset();

    // Image zoom helpers

    private void ZoomImageIn()  => ImageZoomLevel = SnapToNiceZoom(_imageZoomLevel + ImageZoomStep);
    private void ZoomImageOut() => ImageZoomLevel = SnapToNiceZoom(_imageZoomLevel - ImageZoomStep);
    private void ZoomImageReset() => ImageZoomLevel = 1.0;

    // Snaps zoom to a clean percentage (0.25, 0.5, 0.75, 1.0, 1.25 …) to
    // avoid floating-point drift making levels like 0.9999999 appear.
    private static double SnapToNiceZoom(double zoom)
    {
        var snapped = Math.Round(zoom / ImageZoomStep) * ImageZoomStep;
        return Math.Clamp(snapped, ImageZoomMin, ImageZoomMax);
    }

    private void ImageScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!HasImagePreview) return;
        var hasControl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (!hasControl) return;

        // Ctrl+wheel → zoom. Mark handled so the ScrollViewer does NOT also scroll.
        if (e.Delta.Y > 0)
            ZoomImageIn();
        else if (e.Delta.Y < 0)
            ZoomImageOut();

        e.Handled = true;
    }

    // Autosave helpers

    private void RestartAutoSaveTimerIfNeeded()
    {
        if (IsAutoSaveEnabled && HasFileOpen && _isDirty)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    private void ClearAutoSaveStatus()
    {
        if (string.IsNullOrWhiteSpace(_autoSaveStatusMessage)) return;
        _autoSaveStatusTimer.Stop();
        _autoSaveStatusMessage = null;
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(AutoSaveStatusText));
    }

    private static string BuildAutoSaveFailureMessage(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unexpected error." : ex.Message.Trim();
        return $"{AutoSaveFailedMessagePrefix} {message}";
    }

    // Editor helpers

    private string GetDocumentDisplayName() =>
        HasFileOpen ? Path.GetFileName(_currentFilePath!) : "untitled.txt";

    // Builds the OS window title with page/file state, simpler than the Discord RPC logic.
    private string BuildWindowTitle()
    {
        var birthday = IsKodoBirthday ? " 🎂" : string.Empty;

        if (_isSettingsPageVisible)   return "Settings";
        if (_isExtensionsPageVisible) return "Extensions";
        if (_isTutorialPageVisible)   return "Tutorial";
        if (_isHomePageVisible)       return $"Kodo{birthday}";

        if (HasDocumentOpen)
        {
            var dirty = _isDirty ? "● " : string.Empty;
            var file  = GetDocumentDisplayName();
            if (IsFolderOpen)
            {
                var workspace = Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(workspace))
                    return $"{dirty}{file} - {workspace}";
            }
            return $"{dirty}{file}";
        }

        return $"Kodo{birthday}";
    }

    // Writes content into the TextEditor document without triggering dirty tracking
    private void SetEditorContent(string content)
    {
        _suppressDirtyTracking = true;
        EditorTextBox.Document.Text = content;
        // Clears the shared TextArea's selection/caret so they don't carry over from the previous tab.
        EditorTextBox.TextArea.ClearSelection();
        EditorTextBox.TextArea.Caret.Offset = 0;
        // Clear the flag via a posted action so it stays true until after
        // AvaloniaEdit's async TextChanged event has fired and been handled.
        Dispatcher.UIThread.Post(
            () => _suppressDirtyTracking = false,
            DispatcherPriority.Background);
    }

    // Terminal helpers

    private void RefreshAvailableTerminalShells(string? preferredShellId = null)
    {
        AvailableTerminalShells.Clear();

        foreach (var shell in TerminalShellSupport.DetectTerminalShells(_isPSReadLinePredictionEnabled))
            AvailableTerminalShells.Add(shell);

        SelectedTerminalShell = AvailableTerminalShells.FirstOrDefault(shell =>
            string.Equals(shell.Id, preferredShellId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableTerminalShells.FirstOrDefault(shell =>
                string.Equals(shell.Id, "powershell", StringComparison.OrdinalIgnoreCase))
            ?? AvailableTerminalShells.FirstOrDefault();
    }

    /// Derives a tab-title-friendly name from the workspace/working-directory path (e.g. "Kodo" from ".../Kodo"), falling back to the shell name for root paths.
    private static string GetWorkspaceDisplayName(string workingDirectory, string fallback)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return fallback;

        var trimmed = workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private string ResolveTerminalWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ActiveTerminalSession?.WorkingDirectory) && Directory.Exists(ActiveTerminalSession.WorkingDirectory))
            return ActiveTerminalSession.WorkingDirectory;

        // The active file's directory takes priority over _currentFolderPath, unless the
        // active file lives inside the open folder.
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
        {
            var fileDirectory = Path.GetDirectoryName(_currentFilePath);
            if (!string.IsNullOrWhiteSpace(fileDirectory) && Directory.Exists(fileDirectory))
            {
                var folderIsOpen = !string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath);
                if (!folderIsOpen || !IsPathInsideDirectory(_currentFilePath, _currentFolderPath!))
                    return fileDirectory;

                return _currentFolderPath!;
            }
        }

        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            return _currentFolderPath;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private TerminalShellOption? GetSelectedTerminalShellOrFallback() =>
        SelectedTerminalShell ?? AvailableTerminalShells.FirstOrDefault();

    private void ToggleTerminalPanel(bool ensureVisible = false)
    {
        NavigateTo(Page.Editor);

        if (ensureVisible)
            IsTerminalVisible = true;
        else
            IsTerminalVisible = !IsTerminalVisible;

        // Do NOT auto-spawn a shell when the panel opens - the user must click
        // "Create terminal" or use Ctrl+Shift+` to start one explicitly.
        if (IsTerminalVisible && ActiveTerminalSession is not null)
            FocusActiveTerminal();
    }

    private void CreateTerminalSession(TerminalShellOption? shell = null, TerminalSession? replaceExisting = null)
    {
        if (!IsTerminalSupported)
            return;

        shell ??= GetSelectedTerminalShellOrFallback();
        if (shell is null)
            return;

        var workingDirectory = ResolveTerminalWorkingDirectory();
        var workspaceName = GetWorkspaceDisplayName(workingDirectory, shell.DisplayName);

        var session = new TerminalSession(shell.Id, shell.DisplayName, workspaceName, workingDirectory);
        StartTerminalProcess(session, shell);

        if (replaceExisting is not null)
        {
            var index = TerminalSessions.IndexOf(replaceExisting);
            if (index >= 0)
            {
                CloseTerminalSession(replaceExisting, activateReplacement: false);
                TerminalSessions.Insert(Math.Min(index, TerminalSessions.Count), session);
            }
            else
            {
                TerminalSessions.Add(session);
            }
        }
        else
        {
            TerminalSessions.Add(session);
        }

        RetitleTerminalSessions();
        ActiveTerminalSession = session;
        IsTerminalVisible = true;
        FocusActiveTerminal();
    }

    /// Tracks the active session's live working directory (via the shell's OSC 1337 CWD reports,
    /// see <see cref="TerminalShellSupport.DetectTerminalShells"/>) and keeps tab titles in sync
    /// as the user cd's around.
    private void TerminalHostControl_OnWorkingDirectoryChanged(object? sender, string path)
    {
        if (ActiveTerminalSession is not { } session) return;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        if (string.Equals(session.WorkingDirectory, path, StringComparison.OrdinalIgnoreCase)) return;

        session.WorkingDirectory = path;
        RetitleTerminalSessions();
    }

    private void RetitleTerminalSessions()
    {
        var seenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in TerminalSessions)
        {
            if (session.HasCustomTitle)
                continue;

            var shell = AvailableTerminalShells.FirstOrDefault(s =>
                string.Equals(s.Id, session.ShellId, StringComparison.OrdinalIgnoreCase));
            var name = GetWorkspaceDisplayName(session.WorkingDirectory, shell?.DisplayName ?? session.ShellDisplayName);

            seenCounts.TryGetValue(name, out var count);
            count++;
            seenCounts[name] = count;

            session.ApplyAutoTitle(count == 1 ? name : $"{name} {count}");
        }
    }

    private void StartTerminalProcess(TerminalSession session, TerminalShellOption shell)
    {
        // Marks the session as launching; the exit watcher is wired elsewhere to avoid duplicates.
        session.IsRunning = true;
        session.StatusText = "Launching...";
    }



    private void ClearActiveTerminal()
    {
        if (ActiveTerminalSession is null)
            return;

        SendTextToTerminal(ActiveTerminalSession, TerminalShellSupport.GetClearCommandForShell(ActiveTerminalSession.ShellId));
    }

    private void RestartActiveTerminal()
    {
        if (ActiveTerminalSession is null)
        {
            CreateTerminalSession();
            return;
        }

        var shell = AvailableTerminalShells.FirstOrDefault(option =>
            string.Equals(option.Id, ActiveTerminalSession.ShellId, StringComparison.OrdinalIgnoreCase))
            ?? GetSelectedTerminalShellOrFallback();
        CreateTerminalSession(shell, ActiveTerminalSession);
    }

    private void RefreshTerminalWindows()
    {
        // ConsoleTerminal handles its own layout; kept only so call-sites still compile.
    }

    private void FocusActiveTerminal()
    {
        // Posts at Background priority so Focus() wins after Avalonia's layout pass.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsTerminalVisible && ActiveTerminalSession is not null)
            {
                TerminalHostControl.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void SendTextToTerminal(TerminalSession session, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        TerminalHostControl.SendInput(text);
    }

    private void CloseTerminalSession(TerminalSession session, bool activateReplacement = true)
    {
        // Stop the ConPTY process for this session.
        if (ReferenceEquals(session, ActiveTerminalSession))
        {
            if (_activeSessionExitedHandler is not null)
            {
                TerminalHostControl.SessionExited -= _activeSessionExitedHandler;
                _activeSessionExitedHandler = null;
            }
            TerminalHostControl.Stop();
        }

        session.IsRunning = false;
        session.StatusText = "Closed";
        session.Dispose();

        var index = TerminalSessions.IndexOf(session);
        TerminalSessions.Remove(session);

        if (!activateReplacement)
            return;

        if (ReferenceEquals(ActiveTerminalSession, session))
        {
            ActiveTerminalSession = TerminalSessions.Count == 0
                ? null
                : TerminalSessions[Math.Clamp(index - 1, 0, TerminalSessions.Count - 1)];
        }
    }

    private void CloseAllTerminalSessions()
    {
        foreach (var session in TerminalSessions.ToList())
            CloseTerminalSession(session);

        ActiveTerminalSession = null;
    }

    private void FocusEditor()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEditorPageVisible && IsTextEditorVisible)
                EditorTextBox.TextArea.Focus();
        }, DispatcherPriority.Background);
    }

    // Event handlers

    // Switches the visible page in one pass - sets all backing fields before firing
    // any notifications, so the UI only re-renders once instead of once per property set.
    private enum Page { Home, Editor, Settings, Extensions, Tutorial, WhatsNew }

    // Modes for the unified search panel opened by Ctrl+F / Ctrl+Shift+F / the status bar.
    private enum SearchMode { FindInFile, FileByName, ProjectSearch }

    private void NavigateTo(Page page)
    {
        var newHome       = page == Page.Home;
        var newSettings   = page == Page.Settings;
        var newExtensions = page == Page.Extensions;
        var newTutorial   = page == Page.Tutorial;
        var newWhatsNew   = page == Page.WhatsNew;

        // Bail early if nothing actually changed
        if (_isHomePageVisible       == newHome       &&
            _isSettingsPageVisible   == newSettings   &&
            _isExtensionsPageVisible == newExtensions &&
            _isTutorialPageVisible   == newTutorial   &&
            _isWhatsNewPageVisible   == newWhatsNew)
            return;

        _isHomePageVisible       = newHome;
        _isSettingsPageVisible   = newSettings;
        _isExtensionsPageVisible = newExtensions;
        _isTutorialPageVisible   = newTutorial;
        _isWhatsNewPageVisible   = newWhatsNew;

        // Any deliberate navigation dismisses the update splash.
        if (_isUpdateSplashVisible)
            IsUpdateSplashVisible = false;

        OnPropertyChanged(nameof(IsHomePageVisible));
        OnPropertyChanged(nameof(IsSettingsPageVisible));
        OnPropertyChanged(nameof(IsExtensionsPageVisible));
        OnPropertyChanged(nameof(IsTutorialPageVisible));
        OnPropertyChanged(nameof(IsWhatsNewPageVisible));
        OnPropertyChanged(nameof(IsEditorPageVisible));
        OnPropertyChanged(nameof(IsSearchPanelActive));
        OnPropertyChanged(nameof(IsEditorTabsVisible));
        OnPropertyChanged(nameof(IsDocumentViewVisible));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(CanShowSaveActions));
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(FilePathText));
        RefreshState(fullRefresh: true);
    }

    private void EditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo(Page.Editor);
        FocusEditor();
    }

    private void HomeButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(Page.Home);

    private async void OpenFileButton_OnClick(object? sender, RoutedEventArgs e) =>
        await OpenFileAsync();

    private async void SaveButton_OnClick(object? sender, RoutedEventArgs e) =>
        await SaveAsync();

    private async void ToolbarSaveButton_OnClick(object? sender, RoutedEventArgs e) =>
        await SaveAsync();

    private async void ToolbarSaveAsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SaveAsAsync();
        await RefreshExplorerTreeAsync();
    }

    private async void ToolbarSaveAllButton_OnClick(object? sender, RoutedEventArgs e) =>
        await SaveAllAsync();

    private async Task SaveAllAsync()
    {
        if (OpenTabs.Count == 0) return;

        var originalActiveTab = ActiveEditorTab;
        SaveCurrentEditorStateIntoTab();

        try
        {
            foreach (var tab in OpenTabs.ToList())
            {
                if (!tab.IsDirty) continue;

                if (tab.IsUntitled)
                {
                    if (!ReferenceEquals(tab, ActiveEditorTab))
                        ActivateTab(tab, focusEditor: false, preserveCurrentState: false);

                    await SaveAsync(allowPromptForPath: true, forcePromptForPath: true);
                }
                else
                {
                    try
                    {
                        await File.WriteAllTextAsync(tab.Path, tab.Content);
                        tab.IsDirty = false;
                    }
                    catch (Exception ex)
                    {
                        await ShowWarningDialogAsync("File save", ex);
                    }
                }
            }
        }
        finally
        {
            if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab) &&
                !ReferenceEquals(originalActiveTab, ActiveEditorTab))
            {
                ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);
            }

            RefreshState(fullRefresh: true);
        }
    }

    private void NewFileButton_OnClick(object? sender, RoutedEventArgs e) =>
        NewFile();

    private void ToggleTerminalButton_OnClick(object? sender, RoutedEventArgs e) =>
        ToggleTerminalPanel();

    private void NewTerminalButton_OnClick(object? sender, RoutedEventArgs e) =>
        CreateTerminalSession();

    private void ClearTerminalButton_OnClick(object? sender, RoutedEventArgs e) =>
        ClearActiveTerminal();

    private void RestartTerminalButton_OnClick(object? sender, RoutedEventArgs e) =>
        RestartActiveTerminal();

    private void StatusBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }



    private void CloseAllTerminalSessionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseAllTerminalSessions();
        IsTerminalVisible = false;
    }

    private void OpenTerminalSessionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TerminalSession session })
        {
            ActiveTerminalSession = session;
            IsTerminalVisible = true;
            FocusActiveTerminal();
        }
    }

    private void CloseTerminalSessionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TerminalSession session })
        {
            CloseTerminalSession(session);
            // The × button can steal focus; explicitly return it to the terminal.
            FocusActiveTerminal();
        }
    }

    private async void RenameTerminalSessionMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<TerminalSession>(sender) is not { } session) return;
        var newName = await ShowRenameDialogAsync(session.Title);
        if (newName is null || string.Equals(newName, session.Title, StringComparison.Ordinal)) return;
        session.Title = newName;
    }

    private void DuplicateTerminalSessionMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<TerminalSession>(sender) is not { } session) return;
        var shell = AvailableTerminalShells.FirstOrDefault(s =>
            string.Equals(s.Id, session.ShellId, StringComparison.OrdinalIgnoreCase))
            ?? GetSelectedTerminalShellOrFallback();
        CreateTerminalSession(shell);
    }

    private void RestartTerminalSessionMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<TerminalSession>(sender) is not { } session) return;
        var shell = AvailableTerminalShells.FirstOrDefault(s =>
            string.Equals(s.Id, session.ShellId, StringComparison.OrdinalIgnoreCase))
            ?? GetSelectedTerminalShellOrFallback();
        CreateTerminalSession(shell, session);
    }

    private void CloseOtherTerminalSessionsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<TerminalSession>(sender) is not { } pivotSession) return;
        var others = TerminalSessions.Where(s => !ReferenceEquals(s, pivotSession)).ToList();
        foreach (var s in others)
            CloseTerminalSession(s);
    }

    private async void OpenFolderButton_OnClick(object? sender, RoutedEventArgs e) =>
        await OpenFolderAsync();

    private void CloseFolderButton_OnClick(object? sender, RoutedEventArgs e) =>
        CloseFolder();

    private void CollapseExplorerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        // Only toggles panel visibility - previously wiped folder state via CloseFolder().
        IsFileExplorerVisible = !IsFileExplorerVisible;
    }

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(Page.Settings);

    private void ExtensionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenExtensionsPage(showMarketplaceTab: false, forceRefresh: false);
    }

    private async void RefreshExtensionsButton_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshExtensionsDataAsync(force: true);

    private void InstalledTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsInstalledTabSelected = true;

    // Switches to one of the marketplace tabs (Languages/Themes/Plugins) and refreshes the
    // listing (respecting the normal refresh cooldown).
    private void LanguagesTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenMarketplaceTab(ExtensionsTabModes.Languages);

    private void ThemesTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenMarketplaceTab(ExtensionsTabModes.Themes);

    private void PluginsTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenMarketplaceTab(ExtensionsTabModes.Plugins);

    private void OpenMarketplaceTab(string tab)
    {
        SetSelectedExtensionsTab(tab);
        RefreshMarketplaceConnectivityState();
        _ = RefreshExtensionsDataAsync();
    }

    // Used by the "Visit Marketplace" button on the home screen -
    // opens the Extensions page AND switches to the Marketplace tab
    private void OpenMarketplaceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenExtensionsPage(showMarketplaceTab: true, forceRefresh: true);
    }

    private void RefreshNewsButton_OnClick(object? sender, RoutedEventArgs e) =>
        _ = FetchAnnouncementsAsync(forceNetwork: true);

    private void OpenTutorialButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _tutorialOpenedFromSettings = true;
        TutorialStepIndex = 0;
        NavigateTo(Page.Tutorial);
    }

    private void OpenWhatsNewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo(Page.WhatsNew);
        IsWhatsNewExpanded = true;
        _ = RefreshLatestReleaseAsync();
    }

    // Only reachable once any pending consent ask is resolved, so the splash can't be dismissed past an unanswered question.
    private void DismissUpdateSplashButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsUpdateSplashVisible = false;

    // Shared consent card: declining counts as answering, same as accepting. Only auto-dismisses
    // the splash once the Privacy Policy prompt (if also pending) has been resolved too.
    private void AcceptDataTrackingButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsDataTrackingEnabled = true;
        if (IsUpdateSplashVisible && !IsPrivacyPolicyPromptVisible) IsUpdateSplashVisible = false;
    }

    private void DeclineDataTrackingButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsDataTrackingEnabled = false;
        if (IsUpdateSplashVisible && !IsPrivacyPolicyPromptVisible) IsUpdateSplashVisible = false;
    }

    // No decline path - this is acknowledgment of terms, not an opt-in. Only reachable once
    // IsPrivacyPolicyScrolledToBottom is true (button is disabled until then).
    private void AcceptPrivacyPolicyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _hasAcceptedPrivacyPolicy = true;
        OnPropertyChanged(nameof(IsPrivacyPolicyPromptVisible));
        OnPropertyChanged(nameof(AreAllConsentPromptsResolved));
        if (IsUpdateSplashVisible && !IsDataTrackingPromptVisible) IsUpdateSplashVisible = false;
        SaveSettings();
    }

    // Tracks whether the embedded Privacy Policy ScrollViewer has been scrolled to its bottom.
    private void PrivacyPolicyScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;

        const double epsilon = 4.0;
        var scrollableHeight = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        var atBottom = scrollableHeight <= 0 ||
                       scrollViewer.Offset.Y >= scrollableHeight - epsilon;

        if (atBottom) IsPrivacyPolicyScrolledToBottom = true;
    }

    private void BackToEditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo(Page.Editor);
        FocusEditor();
    }

    private async void RefreshLatestReleaseButton_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshLatestReleaseAsync();

    private void ToggleWhatsNewExpandedButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsWhatsNewExpanded = !IsWhatsNewExpanded;

    private void DismissUpdateBanner_OnClick(object? sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = true;
        OnPropertyChanged(nameof(IsAppUpdateAvailable));
    }

    private void DismissExtensionUpdateBanner_OnClick(object? sender, RoutedEventArgs e)
    {
        _extensionUpdateBannerDismissed = true;
        OnPropertyChanged(nameof(IsExtensionUpdateBannerVisible));
    }

    private void OpenMarketplaceFromBannerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _extensionUpdateBannerDismissed = true;
        OnPropertyChanged(nameof(IsExtensionUpdateBannerVisible));
        OpenExtensionsPage(showMarketplaceTab: true, forceRefresh: false);
    }

    private void OpenExtensionsPage(bool showMarketplaceTab, bool forceRefresh)
    {
        NavigateTo(Page.Extensions);
        if (showMarketplaceTab) IsLanguagesTabSelected = true;
        RefreshMarketplaceConnectivityState();
        _ = RefreshExtensionsDataAsync(force: forceRefresh);
    }

    private async void CheckForUpdatesButton_OnClick(object? sender, RoutedEventArgs e) =>
        await CheckForUpdatesManuallyAsync();

    // Explicit Settings-page update check; always reports its result.
    private async Task CheckForUpdatesManuallyAsync()
    {
        if (IsCheckingForUpdatesManually) return;

        IsCheckingForUpdatesManually = true;
        CheckForUpdatesStatusText    = "Checking for updates…";

        try
        {
            var installInBackground = IsAutoUpdateAppInBackgroundEnabled;
            var update = await UpdateService.CheckAndHandleUpdateAsync(installInBackground, found =>
            {
                CheckForUpdatesStatusText = installInBackground
                    ? $"Kodo {found.Version} found - installing in the background…"
                    : $"Kodo {found.Version} is available.";
            });

            if (update is null)
                CheckForUpdatesStatusText = $"You're up to date - Kodo {KodoDiagnostics.AppVersion}.";
        }
        catch (Exception ex)
        {
            // CheckAndHandleUpdateAsync already swallows failures, but guard here too so a manual click can never crash the page.
            CheckForUpdatesStatusText = "Couldn't check for updates. Check your connection and try again.";
            KodoDiagnostics.LogDebug("Manual check-for-updates failed", ex);
        }
        finally
        {
            IsCheckingForUpdatesManually = false;
        }
    }

    private async void OpenReleasesPageButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var originalContent = button?.Content;

        if (button is not null)
        {
            button.IsEnabled = false;
            button.Content   = "Checking…";
        }

        try
        {
            // A button labelled "Open Releases Page" silently installing and exiting Kodo out
            // from under the user is not what they clicked for - always just check and show
            // the dialog here. Actual background installs are KodoUpdater.exe's job.
            var update = await UpdateService.CheckAndHandleUpdateAsync(installInBackground: false);
            if (update is null)
                // No installer asset found (rate-limited, draft release, etc.) - fall back to the releases page.
                OpenUrl(ReleasesPageUrl);
        }
        finally
        {
            if (button is not null)
            {
                button.Content   = originalContent;
                button.IsEnabled = true;
            }
        }
    }

    private void OpenDiscordButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(DiscordServerUrl);

    private void OpenWebsiteButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(WebsiteUrl);

    private void OpenPrivacyPolicyButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(PrivacyPolicyUrl);

    private void ViewShortcutsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        // Non-rebindable reference list - context-specific keys that only fire inside an
        // active terminal search overlay and rely on modifier-free keys (which would collide
        // with typing the query), so they're not exposed in the editable list.
        var fixedShortcuts = new (string Gesture, string Description)[]
        {
            ("Enter / F3",         "Next search match (in terminal)"),
            ("Shift+Enter / Shift+F3", "Previous search match (in terminal)"),
        };

        // Header
        var titleText = new TextBlock
        {
            Text         = "Keyboard Shortcuts",
            FontSize     = 16,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = PrimaryTextBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var hintText = new TextBlock
        {
            Text         = "Click a shortcut below, then press a new key combination. Press Escape to cancel (so Escape itself can only be reassigned via Reset, not capture).",
            FontSize     = 12,
            Foreground   = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var editableHeader = new TextBlock
        {
            Text       = "Editable",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            Margin     = new Thickness(0, 4, 0, 0),
        };

        var otherHeader = new TextBlock
        {
            Text       = "Other",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            Margin     = new Thickness(0, 4, 0, 0),
        };

        // Editable (rebindable) grid - one row per KeybindDefinition
        var editableGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*,Auto,Auto"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", KeybindDefinitions.Length))),
        };

        // Non-rebindable reference list, reusing the old static layout
        var fixedGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,24,*"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", fixedShortcuts.Length))),
        };

        for (var i = 0; i < fixedShortcuts.Length; i++)
        {
            var (gesture, description) = fixedShortcuts[i];

            var gestureBorder = new Border
            {
                Background      = CardBrush,
                BorderBrush     = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(8, 3),
                Margin          = new Thickness(0, 0, 0, 6),
                Child           = new TextBlock
                {
                    Text       = gesture,
                    FontSize   = 12,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                    Foreground = PrimaryTextBrush,
                },
            };

            var descText = new TextBlock
            {
                Text              = description,
                FontSize          = 13,
                Foreground        = MutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap,
                Margin            = new Thickness(0, 0, 0, 6),
            };

            Grid.SetRow(gestureBorder, i);
            Grid.SetColumn(gestureBorder, 0);
            Grid.SetRow(descText, i);
            Grid.SetColumn(descText, 2);
            fixedGrid.Children.Add(gestureBorder);
            fixedGrid.Children.Add(descText);
        }

        // Conflict/status line shown under the editable list while capturing or on error.
        var statusText = new TextBlock
        {
            Text         = string.Empty,
            FontSize     = 12,
            Foreground   = Brush.Parse("#E5484D"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible    = false,
        };

        // Only one row can be "listening" for a new key combo at a time.
        string? capturingId = null;
        var gestureTextBlocks = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        var editButtons        = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var resetButtons        = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

        void RefreshRow(string id)
        {
            if (gestureTextBlocks.TryGetValue(id, out var tb))
                tb.Text = FormatGesture(_keybinds[id]);
            var def = KeybindDefinitions.First(d => d.Id == id);
            var isCustom = _keybinds[id].Key != def.Default.Key || _keybinds[id].KeyModifiers != def.Default.KeyModifiers;
            if (resetButtons.TryGetValue(id, out var rb))
                rb.IsVisible = isCustom;
        }

        void CancelCapture()
        {
            if (capturingId is null) return;
            if (editButtons.TryGetValue(capturingId, out var btn))
                btn.Content = "Edit";
            capturingId = null;
            statusText.IsVisible = false;
        }

        for (var i = 0; i < KeybindDefinitions.Length; i++)
        {
            var def = KeybindDefinitions[i];

            var gestureBorder = new Border
            {
                Background      = CardBrush,
                BorderBrush     = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(8, 3),
                Margin          = new Thickness(0, 0, 0, 6),
            };
            var gestureText = new TextBlock
            {
                Text       = FormatGesture(_keybinds[def.Id]),
                FontSize   = 12,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                Foreground = PrimaryTextBrush,
            };
            gestureBorder.Child = gestureText;
            gestureTextBlocks[def.Id] = gestureText;

            var descText = new TextBlock
            {
                Text              = def.Description,
                FontSize          = 13,
                Foreground        = MutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap,
                Margin            = new Thickness(12, 0, 0, 6),
            };

            var editButton = new Button
            {
                Content         = "Edit",
                FontSize        = 11,
                Padding         = new Thickness(10, 3),
                Margin          = new Thickness(0, 0, 6, 6),
                Background      = ButtonBrush,
                Foreground      = PrimaryTextBrush,
                BorderThickness = new Thickness(0),
                CornerRadius    = new CornerRadius(5),
            };
            editButtons[def.Id] = editButton;

            var resetButton = new Button
            {
                Content         = "Reset",
                FontSize        = 11,
                Padding         = new Thickness(10, 3),
                Margin          = new Thickness(0, 0, 0, 6),
                Background      = ButtonBrush,
                Foreground      = MutedTextBrush,
                BorderThickness = new Thickness(0),
                CornerRadius    = new CornerRadius(5),
                IsVisible       = _keybinds[def.Id].Key != def.Default.Key || _keybinds[def.Id].KeyModifiers != def.Default.KeyModifiers,
            };
            resetButtons[def.Id] = resetButton;

            editButton.Click += (_, _) =>
            {
                if (capturingId == def.Id)
                {
                    CancelCapture();
                    return;
                }
                CancelCapture();
                capturingId = def.Id;
                editButton.Content = "Press keys\u2026";
                statusText.IsVisible = false;
            };

            resetButton.Click += (_, _) =>
            {
                CancelCapture();
                _keybinds[def.Id] = def.Default;
                RefreshRow(def.Id);
                SaveSettings(immediate: true);
            };

            var id = def.Id;
            Grid.SetRow(gestureBorder, i);
            Grid.SetColumn(gestureBorder, 0);
            Grid.SetRow(descText, i);
            Grid.SetColumn(descText, 1);
            Grid.SetRow(editButton, i);
            Grid.SetColumn(editButton, 2);
            Grid.SetRow(resetButton, i);
            Grid.SetColumn(resetButton, 3);
            editableGrid.Children.Add(gestureBorder);
            editableGrid.Children.Add(descText);
            editableGrid.Children.Add(editButton);
            editableGrid.Children.Add(resetButton);
        }

        var scroll = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing  = 6,
                Children = { editableHeader, editableGrid, otherHeader, fixedGrid },
            },
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight                     = 460,
        };

        // Dismiss button
        var dismissButton = new Button
        {
            Content             = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding             = new Thickness(20, 8),
            Background          = AccentBrush,
            Foreground          = AccentForegroundBrush,
            BorderThickness     = new Thickness(0),
            CornerRadius        = new CornerRadius(8),
        };

        var content = new StackPanel
        {
            Spacing  = 16,
            Margin   = new Thickness(20),
            Children = { titleText, hintText, scroll, statusText, dismissButton },
        };

        Window? dialog = null;
        dialog = new Window
        {
            Title                 = "Kodo - Keyboard Shortcuts",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            MinWidth              = 400,
            MaxHeight             = 680,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = CardBrush,
            Content               = content,
        };

        // Captures the next key combo while a row is in "Press keys..." mode. Runs in the
        // tunnel phase (with handledEventsToo) so it sees the key before any control inside
        // the dialog would otherwise consume it.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, keyArgs) =>
        {
            if (capturingId is null) return;

            if (keyArgs.Key is Key.Escape)
            {
                keyArgs.Handled = true;
                CancelCapture();
                return;
            }

            // Ignore bare modifier presses - wait for the actual key.
            if (keyArgs.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                return;
            }

            keyArgs.Handled = true;
            var newGesture = new KeyGesture(keyArgs.Key, keyArgs.KeyModifiers);

            // A gesture with no modifiers at all would normally fight with regular typing,
            // so it's only allowed for keys that never appear in text input (function keys).
            if (!IsValidKeybindGesture(newGesture))
            {
                statusText.Text = "Shortcuts need at least one modifier key (Ctrl, Alt, Shift, or Win), unless it's a function key.";
                statusText.IsVisible = true;
                return;
            }

            var capturingDef = KeybindDefinitions.First(d => d.Id == capturingId);
            // Conflicts are only reported within the same scope: context-local bindings
            // (terminal) may reuse a chord that a global binding already uses, since they
            // can't be active at the same time.
            var conflict = KeybindDefinitions.FirstOrDefault(d =>
                d.Id != capturingId &&
                d.IsContextLocal == capturingDef.IsContextLocal &&
                _keybinds[d.Id].Key == newGesture.Key &&
                _keybinds[d.Id].KeyModifiers == newGesture.KeyModifiers);

            if (conflict is not null)
            {
                statusText.Text = $"{FormatGesture(newGesture)} is already used by \"{conflict.Description}\".";
                statusText.IsVisible = true;
                return;
            }

            var id = capturingId;
            _keybinds[id] = newGesture;
            RefreshRow(id);
            SaveSettings(immediate: true);

            if (editButtons.TryGetValue(id, out var btn))
                btn.Content = "Edit";
            capturingId = null;
            statusText.IsVisible = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        dismissButton.Click += (_, _) => dialog!.Close();
        _ = dialog.ShowDialog(this);
    }

    private async void OpenCrashLogFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = KodoDiagnostics.LogDirectoryPath;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Open crash logs folder", ex);
        }
    }

    private async void OpenSettingsFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetDirectoryName(SettingsFilePath) ?? string.Empty;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Open settings folder", ex);
        }
    }

    private void OpenLatestReleaseButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(LatestReleaseUrl);

    private void OpenReleaseLinkButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch failures quietly; the release info remains visible in-app.
        }
    }

    private async void OpenExtensionsFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(ExtensionsFolderPath))
                Directory.CreateDirectory(ExtensionsFolderPath);

            Process.Start(new ProcessStartInfo
            {
                FileName        = ExtensionsFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Could not open extensions folder: {ex.Message}";
            await ShowWarningDialogAsync("Open extensions folder", ex);
        }
    }

    private async void CopyDiagnosticInfoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(BuildDiagnosticReport()) ?? Task.CompletedTask);
            DeveloperOptionsStatusText = "Diagnostic info copied to clipboard.";
        }
        catch (Exception ex)
        {
            DeveloperOptionsStatusText = $"Could not copy diagnostic info: {ex.Message}";
        }
    }

    private async void ClearLogsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "Clear logs?",
            "This permanently deletes the main log and crash log files from disk. This can't be undone.",
            confirmLabel: "Clear",
            isDestructive: true);

        if (!confirmed) return;

        var clearedAny = false;
        var failures = new List<string>();

        foreach (var path in new[] { KodoDiagnostics.MainLogFilePath, KodoDiagnostics.CrashLogFilePath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                clearedAny = true;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }

        DeveloperOptionsStatusText = failures.Count > 0
            ? $"Couldn't clear: {string.Join(", ", failures)}"
            : clearedAny
                ? "Logs cleared."
                : "No logs to clear.";
    }

    // Builds a bug-report snapshot shared by Copy Diagnostic Info and Export Kodo Data.
    private string BuildDiagnosticReport()
    {
        var sb = new StringBuilder();

        void Section(string title)
        {
            sb.AppendLine();
            sb.AppendLine($"── {title} ──");
        }

        // Environment
        sb.Append("Kodo ").AppendLine(KodoDiagnostics.AppVersion);
        sb.Append("OS: ").AppendLine(KodoDiagnostics.OSDescription);
        sb.Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription);
        sb.Append("Architecture: ").Append(RuntimeInformation.ProcessArchitecture)
          .Append(" / ").AppendLine(Environment.Is64BitProcess ? "64-bit" : "32-bit");

        // Latest release - helps confirm if the bug is already fixed upstream.
        var latestTag = LatestReleaseTag;
        sb.Append("Latest release: ").AppendLine(
            string.IsNullOrWhiteSpace(latestTag)
                ? "(not yet fetched)"
                : IsNewerVersionAvailable ? $"{latestTag}  ← update available" : latestTag);

        // Editor State
        Section("Editor State");
        var unsavedCount = OpenTabs.Count(t => t.IsDirty);
        sb.AppendLine($"Open tabs: {OpenTabs.Count}{(unsavedCount > 0 ? $"  ({unsavedCount} unsaved)" : string.Empty)}");
        var lang = CurrentLanguageExtension;
        sb.AppendLine($"Active language: {(lang is not null ? $"{lang.Name} v{lang.Version}" : "(none / plain text)")}");
        if (HasFileOpen)
            sb.AppendLine($"Active file encoding: {EncodingDisplayText}");
        sb.AppendLine($"Tab size: {TabSize}");
        sb.AppendLine($"Font size: {EditorFontSize}px");
        sb.AppendLine($"Word wrap: {IsWordWrapEnabled}");
        sb.AppendLine($"Insight: {IsInsightEnabled}");
        sb.AppendLine($"Auto-save: {IsAutoSaveEnabled}");

        // Appearance
        Section("Appearance");
        sb.AppendLine($"Theme: {(IsSystemThemeActive ? $"System Default ({CurrentThemeName})" : CurrentThemeName)}");
        sb.AppendLine($"Accent mode: {_accentColorMode}");
        if (_accentColorMode == "custom")
            sb.AppendLine($"Custom accent: {_customAccentHex}");

        // Terminal
        Section("Terminal");
        sb.AppendLine($"Preferred shell: {SelectedTerminalShell?.DisplayName ?? "(none)"}");

        // Discord Rich Presence
        Section("Discord Rich Presence");
        sb.AppendLine($"Enabled: {IsDiscordRichPresenceEnabled}");
        sb.AppendLine($"Improved RPC: {IsDiscordImprovedRpcEnabled}");

        // Extensions
        var extensions = VisibleLoadedExtensions.ToList();
        Section($"Installed Extensions ({extensions.Count})");
        if (extensions.Count == 0)
            sb.AppendLine("(none)");
        else
            foreach (var ext in extensions)
                sb.AppendLine($"{ext.Name} v{ext.Version} ({ext.Type}) by {ext.Author}");

        // File Locations
        Section("File Locations");
        sb.AppendLine($"Settings: {SettingsFilePath}");
        sb.AppendLine($"Main log: {MainLogFilePath}");
        sb.AppendLine($"Crash log: {CrashLogFilePath}");
        sb.AppendLine($"Extensions folder: {ExtensionsFolderPath}");
        sb.AppendLine($"Verbose logging: {IsVerboseLoggingEnabled}");

        return sb.ToString();
    }

    private async void ExportKodoDataButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var export = BuildDiagnosticReport();
            var suggestedFileName = $"Kodo-Data-Export-{KodoDiagnostics.UtcNow():yyyyMMdd-HHmmss}.txt";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Kodo Data",
                SuggestedFileName = suggestedFileName
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                DeveloperOptionsStatusText = "Export cancelled.";
                return;
            }

            await File.WriteAllTextAsync(path, export);
            DeveloperOptionsStatusText = $"Kodo data exported to {path}.";
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("MainWindow.ExportKodoDataButton_OnClick", ex, operation: "Export Kodo data");
            DeveloperOptionsStatusText = $"Could not export Kodo data: {ex.Message}";
        }
    }

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string themeName })
            ApplyTheme(themeName);
    }

    private void ThemeGroupHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ThemeExtensionGroup group })
            group.IsExpanded = !group.IsExpanded;
    }

    // Convenience handlers used by the tutorial setup step's theme buttons.
    private void ThemeDarkButton_OnClick(object? sender, RoutedEventArgs e)  => ApplyTheme("Dark");
    private void ThemeLightButton_OnClick(object? sender, RoutedEventArgs e) => ApplyTheme("Light");

    private async void FileTreeItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileTreeItem item })
        {
            if (item.IsDirectory)
                await ToggleDirectoryExpansionAsync(item);
            else
                await OpenFileFromPathAsync(item.FullPath);
        }
    }

    private async void RecentFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentFileItem item }) return;

        if (item.IsFolder)
        {
            if (!Directory.Exists(item.Path))
            {
                await ShowNotFoundDialogAsync(item.Path, isFolder: true);
                return;
            }
            _currentFolderPath = item.Path;
            _searchFileCache = null;
            AddRecentFolder(item.Path);
            await PopulateFileTreeAsync(item.Path);
            SetupProjectFolderWatcher(item.Path);
            IsFileExplorerVisible = true;
            RefreshState(fullRefresh: true);
        }
        else
        {
            if (!File.Exists(item.Path))
            {
                await ShowNotFoundDialogAsync(item.Path, isFolder: false);
                return;
            }
            await OpenFileFromPathAsync(item.Path);
        }
    }

    private void TogglePinnedRecentFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        TogglePinnedRecentFile(path);
        e.Handled = true;
    }

    private void OpenEditorTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorTab tab })
            ActivateTab(tab);
    }

    private async void CloseEditorTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            await RequestCloseTabAsync(tab);
    }

    private async void CloseOtherTabsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: EditorTab pivotTab }) return;
        var others = OpenTabs.Where(t => !ReferenceEquals(t, pivotTab)).ToList();
        foreach (var tab in others)
        {
            if (!await RequestCloseTabAsync(tab))
                break;
        }
    }

    private async void CloseAllTabsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var all = OpenTabs.ToList();
        foreach (var tab in all)
        {
            if (!await RequestCloseTabAsync(tab))
                break;
        }
    }

    private static T? TryGetTaggedData<T>(object? sender) where T : class =>
        sender switch
        {
            MenuItem { Tag: T taggedItem } => taggedItem,
            Button { Tag: T taggedButton } => taggedButton,
            _ => null
        };

    private string GetRelativePathOrFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath))
            return path;

        try
        {
            return Path.GetRelativePath(_currentFolderPath, path);
        }
        catch
        {
            return path;
        }
    }

    private string GetExplorerTargetDirectory(FileTreeItem item) =>
        item.IsDirectory
            ? item.FullPath
            : Path.GetDirectoryName(item.FullPath) ?? _currentFolderPath ?? item.FullPath;

    private string GetExplorerRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath))
            throw new InvalidOperationException("No folder is currently open in the explorer.");

        return _currentFolderPath;
    }

    private static string CreateUniqueSiblingPath(string path, bool isDirectory)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileName(path);
        string extension;
        string baseName;

        if (isDirectory || (fileName.StartsWith('.') && fileName.Count(ch => ch == '.') == 1))
        {
            extension = string.Empty;
            baseName = fileName;
        }
        else
        {
            extension = Path.GetExtension(fileName);
            baseName = Path.GetFileNameWithoutExtension(fileName);
        }

        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? " - Copy" : $" - Copy ({index})";
            var candidate = Path.Combine(directory, $"{baseName}{suffix}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static string CreateUniqueChildPath(string directory, string baseName, string extension = "")
    {
        for (var index = 1; ; index++)
        {
            var candidateName = index == 1 ? $"{baseName}{extension}" : $"{baseName} ({index}){extension}";
            var candidate = Path.Combine(directory, candidateName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            var destinationChild = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectoryRecursive(directory, destinationChild);
        }
    }

    private async Task OpenPathInSystemExplorer(string path, bool selectItem)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startInfo = selectItem && File.Exists(path)
                    ? new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    }
                    : new ProcessStartInfo
                    {
                        FileName = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path,
                        UseShellExecute = true
                    };

                Process.Start(startInfo);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                // `open -R <path>` reveals and selects the item in Finder.
                // Falls back to opening the parent directory if path doesn't exist.
                var target = selectItem && (File.Exists(path) || Directory.Exists(path)) ? path
                    : Directory.Exists(path) ? path
                    : Path.GetDirectoryName(path) ?? path;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{target}\"",
                    UseShellExecute = false
                });
                return;
            }

            // Linux: try common file managers that support --select / reveal flags,
            // then fall back to opening the parent directory via xdg-open.
            if (OperatingSystem.IsLinux() && selectItem && File.Exists(path))
            {
                var fileManagers = new[]
                {
                    ("nautilus", $"--select \"{path}\""),   // GNOME
                    ("dolphin",  $"--select \"{path}\""),   // KDE
                    ("nemo",     $"\"{path}\""),             // Cinnamon
                    ("thunar",   $"\"{Path.GetDirectoryName(path)}\""), // XFCE (no select)
                };

                foreach (var (binary, args) in fileManagers)
                {
                    // Check the binary exists before trying to launch it.
                    var which = Process.Start(new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = binary,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    });
                    which?.WaitForExit();
                    if (which?.ExitCode != 0) continue;

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = binary,
                        Arguments = args,
                        UseShellExecute = false
                    });
                    return;
                }
            }

            // Universal fallback: open the containing directory.
            var fallbackDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
            Process.Start(new ProcessStartInfo { FileName = fallbackDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Could not open path: {ex.Message}";
            await ShowWarningDialogAsync("Open in system explorer", ex);
        }
    }

    private async Task RefreshExplorerTreeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
        {
            // Snapshot which directories are expanded before wiping the tree,
            // then restore them afterward so the user's expansion state is preserved.
            var expandedPaths = FileTreeItems
                .Where(i => i.IsDirectory && i.IsExpanded)
                .Select(i => i.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await PopulateFileTreeAsync(_currentFolderPath);

            if (expandedPaths.Count > 0)
                await RestoreExpandedPathsAsync(expandedPaths);
        }
    }

    // Re-expands directories that were open before a tree refresh.
    // Works top-down: a parent must be expanded before its children are visible.
    private async Task RestoreExpandedPathsAsync(HashSet<string> expandedPaths)
    {
        // Keep expanding until no more progress can be made (handles nested directories).
        bool anyExpanded;
        do
        {
            anyExpanded = false;
            // Take a snapshot - ToggleDirectoryExpansionAsync mutates FileTreeItems
            var candidates = FileTreeItems
                .Where(i => i.IsDirectory && !i.IsExpanded && expandedPaths.Contains(i.FullPath))
                .ToList();

            foreach (var item in candidates)
            {
                await ToggleDirectoryExpansionAsync(item);
                anyExpanded = true;
            }
        }
        while (anyExpanded);
    }

    private async Task<bool> CloseTabsForPathAsync(string path, bool isDirectory)
    {
        var matchingTabs = OpenTabs
            .Where(tab => !tab.IsUntitled && (
                string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase) ||
                (isDirectory && tab.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        foreach (var tab in matchingTabs)
        {
            if (!await RequestCloseTabAsync(tab))
                return false;
        }

        return true;
    }

    private async Task<bool> EnsureTabsReadyForDeletionAsync(List<EditorTab> tabs)
    {
        var originalActiveTab = ActiveEditorTab;

        foreach (var tab in tabs.Where(t => t.IsDirty))
        {
            var action = await ShowUnsavedTabDialogAsync(tab);
            switch (action)
            {
                case UnsavedTabAction.Cancel:
                    if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab))
                        ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);
                    return false;

                case UnsavedTabAction.Save:
                    if (!ReferenceEquals(tab, ActiveEditorTab))
                        ActivateTab(tab, focusEditor: false);

                    if (!await SaveAsync(allowPromptForPath: true, forcePromptForPath: false))
                    {
                        if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab))
                            ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);
                        return false;
                    }
                    break;
            }
        }

        if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab))
            ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);

        return true;
    }

    private void CloseTabsWithoutPrompt(IEnumerable<EditorTab> tabs)
    {
        foreach (var tab in tabs.ToList())
            CloseTab(tab);
    }


    /// After a rename or move, updates every open tab under <paramref name="oldPath"/> to the new location,
    /// patching <c>_currentFilePath</c> if the active tab is affected.

    private void RetargetTabPaths(string oldPath, string newPath, bool wasDirectory)
    {
        foreach (var tab in OpenTabs.Where(t => !t.IsUntitled))
        {
            string? updated = null;

            if (wasDirectory)
            {
                var prefix = oldPath + Path.DirectorySeparatorChar;
                if (tab.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    updated = newPath + Path.DirectorySeparatorChar + tab.Path[prefix.Length..];
            }
            else if (string.Equals(tab.Path, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                updated = newPath;
            }

            if (updated is null) continue;

            tab.Rename(updated, Path.GetFileName(updated));

            if (ReferenceEquals(tab, ActiveEditorTab))
            {
                _currentFilePath = updated;
                RefreshState(fullRefresh: true);
            }
        }
    }


    /// Shows a small modal asking for a new name; returns the trimmed input, or null if cancelled/empty.
    private async Task<string?> ShowRenameDialogAsync(string currentName)
    {
        string? result = null;
        Window? dialog = null;

        var inputBox = new TextBox
        {
            Text             = currentName,
            Background       = ButtonBrush,
            Foreground       = PrimaryTextBrush,
            BorderBrush      = SurfaceBorderBrush,
            Padding          = new Thickness(8, 6),
            FontSize         = 14,
            CaretBrush       = PrimaryTextBrush,
        };

        var confirmButton = CreateDialogButton("Rename", AccentBrush, AccentBrush, AccentForegroundBrush, () =>
        {
            result = inputBox.Text?.Trim();
            dialog!.Close();
        });

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)  { result = inputBox.Text?.Trim(); dialog!.Close(); }
            if (e.Key == Key.Escape) { dialog!.Close(); }
        };

        dialog = new Window
        {
            Width                   = 380,
            Height                  = 160,
            CanResize               = false,
            ShowInTaskbar           = false,
            WindowStartupLocation   = WindowStartupLocation.CenterOwner,
            Title                   = "Rename",
            Background              = CardBrush,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child   = new StackPanel
                {
                    Spacing  = 14,
                    Children =
                    {
                        new TextBlock { Text = "Enter a new name:", FontSize = 15,
                            FontWeight = FontWeight.SemiBold, Foreground = PrimaryTextBrush },
                        inputBox,
                        new StackPanel
                        {
                            Orientation         = Orientation.Horizontal,
                            Spacing             = 10,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children =
                            {
                                CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush,
                                    () => dialog!.Close()),
                                confirmButton
                            }
                        }
                    }
                }
            }
        };

        dialog.Opened += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        await dialog.ShowDialog(this);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private async Task ActivateEditorTabForMenuActionAsync(EditorTab tab)
    {
        if (!ReferenceEquals(ActiveEditorTab, tab))
            ActivateTab(tab, focusEditor: false);

        await Task.CompletedTask;
    }

    private async void SaveEditorTabMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab) return;
        await ActivateEditorTabForMenuActionAsync(tab);
        await SaveAsync();
    }

    private async void SaveEditorTabAsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab) return;
        await ActivateEditorTabForMenuActionAsync(tab);
        await SaveAsAsync();
        await RefreshExplorerTreeAsync();
    }

    private void CopyEditorTabPathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(tab.Path);
    }

    private void CopyEditorTabRelativePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(GetRelativePathOrFullPath(tab.Path));
    }

    private async void RevealEditorTabInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;
        await OpenPathInSystemExplorer(tab.Path, selectItem: true);
    }

    private async void CollapseAllTreeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _isFileTreeExpanded = !_isFileTreeExpanded;
        if (!_isFileTreeExpanded)
        {
            // Collapse: repopulate from scratch - fastest correct collapse
            if (!string.IsNullOrWhiteSpace(_currentFolderPath))
                await PopulateFileTreeAsync(_currentFolderPath);
        }
        else
        {
            // Expand: toggle all top-level directories
            var rootDirs = FileTreeItems.Where(i => i.IsDirectory && i.Depth == 0 && !i.IsExpanded).ToList();
            foreach (var dir in rootDirs)
                await ToggleDirectoryExpansionAsync(dir);
        }
    }

    private async void NewFileInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var directory = GetExplorerTargetDirectory(item);
            var newFilePath = CreateUniqueChildPath(directory, "new-file", ".txt");
            await File.WriteAllTextAsync(newFilePath, string.Empty);
            await RefreshExplorerTreeAsync();
            await OpenFileFromPathAsync(newFilePath);
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New file failed: {ex.Message}";
            await ShowWarningDialogAsync("New file in explorer", ex);
        }
    }

    private async void ExplorerHeaderNewFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var directory = GetExplorerRootDirectory();
            var newFilePath = CreateUniqueChildPath(directory, "new-file", ".txt");
            await File.WriteAllTextAsync(newFilePath, string.Empty);
            await RefreshExplorerTreeAsync();
            await OpenFileFromPathAsync(newFilePath);
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New file failed: {ex.Message}";
            await ShowWarningDialogAsync("New file in explorer", ex);
        }
    }

    private async void NewFolderInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var directory = GetExplorerTargetDirectory(item);
            var newFolderPath = CreateUniqueChildPath(directory, "New Folder");
            Directory.CreateDirectory(newFolderPath);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New folder failed: {ex.Message}";
            await ShowWarningDialogAsync("New folder in explorer", ex);
        }
    }

    private async void ExplorerHeaderNewFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var directory = GetExplorerRootDirectory();
            var newFolderPath = CreateUniqueChildPath(directory, "New Folder");
            Directory.CreateDirectory(newFolderPath);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New folder failed: {ex.Message}";
            await ShowWarningDialogAsync("New folder in explorer", ex);
        }
    }

    private async void OpenExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        if (item.IsDirectory)
            await ToggleDirectoryExpansionAsync(item);
        else
            await OpenFileFromPathAsync(item.FullPath);
    }

    private async void RevealExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        await OpenPathInSystemExplorer(item.FullPath, selectItem: !item.IsDirectory);
    }

    private void SearchInFolderMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        if (_currentFolderPath is null) return;

        // Compute relative path from project root to the selected item.
        var relativePath = GetRelativePathOrName(_currentFolderPath, item.FullPath);
        if (!string.IsNullOrEmpty(relativePath))
        {
            // If it's a directory, append /** glob; if it's a file, just use the filename.
            SearchIncludeFilter = item.IsDirectory
                ? relativePath.Replace('\\', '/') + "/**"
                : relativePath.Replace('\\', '/');
        }

        OpenSearchPanel(SearchMode.ProjectSearch);
    }

    private void CopyFileNameMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(item.Name);
    }

    private void CopyFilePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is { } item)
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(item.FullPath);
    }

    private void CopyRelativeFilePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(GetRelativePathOrFullPath(item.FullPath));
    }

    private async void DuplicateExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var duplicatePath = CreateUniqueSiblingPath(item.FullPath, item.IsDirectory);
            if (item.IsDirectory)
                CopyDirectoryRecursive(item.FullPath, duplicatePath);
            else
                File.Copy(item.FullPath, duplicatePath, overwrite: false);

            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Duplicate failed: {ex.Message}";
            await ShowWarningDialogAsync("Duplicate file", ex);
        }
    }

    private async void DeleteFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        try
        {
            var kind = item.IsDirectory ? "folder" : "file";
            var confirmed = await ShowConfirmationDialogAsync(
                $"Delete {kind}?",
                item.IsDirectory
                    ? $"This permanently deletes '{item.Name}' and everything inside it from disk. This can't be undone."
                    : $"This permanently deletes '{item.Name}' from disk. This can't be undone.",
                confirmLabel: "Delete",
                isDestructive: true);

            if (!confirmed) return;

            var matchingTabs = OpenTabs
                .Where(tab => !tab.IsUntitled && (
                    string.Equals(tab.Path, item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    (item.IsDirectory && tab.Path.StartsWith(item.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (!await EnsureTabsReadyForDeletionAsync(matchingTabs))
                return;

            if (item.IsDirectory)
                Directory.Delete(item.FullPath, recursive: true);
            else
                File.Delete(item.FullPath);

            CloseTabsWithoutPrompt(matchingTabs);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Delete failed: {ex.Message}";
            await ShowWarningDialogAsync("Delete file", ex);
        }
    }

    private async void RenameFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        var newName = await ShowRenameDialogAsync(item.Name);
        if (newName is null || string.Equals(newName, item.Name, StringComparison.Ordinal)) return;

        var newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)!, newName);

        if ((item.IsDirectory ? Directory.Exists(newPath) : File.Exists(newPath)))
        {
            ExtensionsStatusText = $"Rename failed: '{newName}' already exists.";
            return;
        }

        // If dirty tabs are open under this path, ask to save first
        var affectedTabs = OpenTabs
            .Where(t => !t.IsUntitled && (
                string.Equals(t.Path, item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                (item.IsDirectory && t.Path.StartsWith(item.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        if (!await EnsureTabsReadyForDeletionAsync(affectedTabs)) return;

        try
        {
            if (item.IsDirectory)
                Directory.Move(item.FullPath, newPath);
            else
                File.Move(item.FullPath, newPath);

            RetargetTabPaths(item.FullPath, newPath, item.IsDirectory);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Rename failed: {ex.Message}";
            await ShowWarningDialogAsync("Rename file", ex);
        }
    }

    private void CutFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        _clipboardItemPath        = item.FullPath;
        _clipboardItemIsDirectory = item.IsDirectory;
        _clipboardIsCut           = true;
        ExtensionsStatusText      = $"Cut: {item.Name}";
    }

    private void CopyFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        _clipboardItemPath        = item.FullPath;
        _clipboardItemIsDirectory = item.IsDirectory;
        _clipboardIsCut           = false;
        ExtensionsStatusText      = $"Copied: {item.Name}";
    }

    private async void PasteFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_clipboardItemPath is null) return;
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } target) return;

        // Paste destination is the target's directory (or the target itself if it's a folder)
        var destDir = target.IsDirectory ? target.FullPath : Path.GetDirectoryName(target.FullPath)!;
        var itemName = Path.GetFileName(_clipboardItemPath.TrimEnd(Path.DirectorySeparatorChar));
        var destPath = CreateUniqueSiblingPath(Path.Combine(destDir, itemName), _clipboardItemIsDirectory);

        try
        {
            if (_clipboardIsCut)
            {
                if (_clipboardItemIsDirectory)
                    Directory.Move(_clipboardItemPath, destPath);
                else
                    File.Move(_clipboardItemPath, destPath);

                RetargetTabPaths(_clipboardItemPath, destPath, _clipboardItemIsDirectory);
                _clipboardItemPath = null; // cut is consumed
            }
            else
            {
                if (_clipboardItemIsDirectory)
                    CopyDirectoryRecursive(_clipboardItemPath, destPath);
                else
                    File.Copy(_clipboardItemPath, destPath, overwrite: false);
            }

            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Paste failed: {ex.Message}";
            await ShowWarningDialogAsync("Paste file", ex);
        }
    }

    private void OpenSearchMenu_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.FindInFile);

    private void SearchModeFindInFileButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.FindInFile);

    private void SearchModeFileByNameButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.FileByName);

    private void SearchModeProjectButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.ProjectSearch);

    private void SearchMatchCaseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchMatchCaseEnabled = !IsSearchMatchCaseEnabled;
        if (IsFindInFileSearchMode)
        {
            UpdateFindHighlights();
            FocusSearchInput();
        }
        else if (IsSearchPanelActive)
            RestartSearchDebounce();
    }

    private void SearchWholeWordButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchWholeWordEnabled = !IsSearchWholeWordEnabled;
        if (IsFindInFileSearchMode)
        {
            UpdateFindHighlights();
            FocusSearchInput();
        }
        else if (IsSearchPanelActive)
            RestartSearchDebounce();
    }

    private void SearchRegexButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchRegexEnabled = !IsSearchRegexEnabled;
        if (IsFindInFileSearchMode)
        {
            UpdateFindHighlights();
            FocusSearchInput();
        }
        else if (IsSearchPanelActive)
            RestartSearchDebounce();
    }

    private void SearchFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsFilterRowVisible = !IsFilterRowVisible;
        OnPropertyChanged(nameof(IsFilterRowSupported));
        if (IsFilterRowVisible && IsProjectSearchMode)
            RestartSearchDebounce();
    }

    private void ReplaceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReplaceCurrentMatch();
    }

    private async void ReplaceAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var count = CountCurrentMatches();
        if (count == 0)
        {
            SearchStatusText = "No matches to replace.";
            return;
        }

        var matchWord = count == 1 ? "match" : "matches";
        var confirmed = await ShowConfirmationDialogAsync(
            "Replace All?",
            $"This will replace {count} {matchWord} in the current file. This action can be undone with Ctrl+Z.",
            confirmLabel: "Replace All");

        if (confirmed)
            ReplaceAllMatches();
    }

    private int CountCurrentMatches()
    {
        if (string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return 0;

        var text = EditorTextBox.Document.Text;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        var count = 0;
        var searchIndex = 0;
        while (searchIndex <= text.Length)
        {
            var m = FindNextMatch(text, FindText, searchIndex, forward: true, comparison, IsSearchWholeWordEnabled, regex);
            if (m.Offset < 0) break;
            count++;
            searchIndex = m.Offset + m.Length;
            if (m.Length == 0) break;
        }
        return count;
    }

    private void CloseSearchPanel_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchPanelVisible = false;
        FocusEditor();
    }

    private void SearchResultsListBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        if (e.Key == Key.Enter)
        {
            if (listBox.SelectedItem is SearchDisplayItem { IsGroupHeader: true, Group: { } group })
                ToggleGroup(group);
            else
                OpenSelectedSearchResult();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            IsSearchPanelVisible = false;
            FocusEditor();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            int nextIndex = FindNextResultIndex(listBox.SelectedIndex, e.Key == Key.Down ? 1 : -1);
            if (nextIndex >= 0)
            {
                listBox.SelectedIndex = nextIndex;
                e.Handled = true;
            }
            else
            {
                FocusSearchInput();
                ResetHistoryIndex();
                e.Handled = true;
            }
        }
    }

    private int FindNextResultIndex(int current, int direction)
    {
        var items = SearchDisplayItems;
        var next = current + direction;
        while (next >= 0 && next < items.Count)
        {
            if (!items[next].IsGroupHeader)
                return next;
            next += direction;
        }
        return -1;
    }

    private void SearchResultsListBox_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenSelectedSearchResult();
    }

    private void SearchResultsListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsListBox?.SelectedItem is SearchDisplayItem { IsGroupHeader: false, Result: { } item } displayItem)
        {
            var index = SearchDisplayItems.IndexOf(displayItem);
            if (index >= 0)
                SearchResultsListBox.ScrollIntoView(index);
        }
    }

    private void SearchGroupHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchFileGroup group })
            ToggleGroup(group);
    }

    private void OpenSelectedSearchResult()
    {
        if (SearchResultsListBox?.SelectedItem is not SearchDisplayItem { IsGroupHeader: false, Result: { } result })
            return;

        OpenSearchResult(result);
    }

    private async void OpenSearchResult(SearchResultItem result)
    {
        await OpenFileFromPathAsync(result.Path);

        if (result.LineNumber > 0 && EditorTextBox?.Document is { } doc)
        {
            var lineNumber = Math.Clamp(result.LineNumber, 1, doc.LineCount);
            var line = doc.GetLineByNumber(lineNumber);
            var matchOffset = string.IsNullOrEmpty(FindText)
                ? -1
                : doc.GetText(line.Offset, line.Length).IndexOf(FindText, StringComparison.OrdinalIgnoreCase);

            Dispatcher.UIThread.Post(() =>
            {
                var caretOffset = matchOffset >= 0 ? line.Offset + matchOffset : line.Offset;
                EditorTextBox.TextArea.Caret.Offset = caretOffset;
                if (matchOffset >= 0 && !string.IsNullOrEmpty(FindText))
                {
                    EditorTextBox.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(
                        EditorTextBox.TextArea, caretOffset, caretOffset + FindText.Length);
                }
                else
                {
                    EditorTextBox.TextArea.ClearSelection();
                }
                EditorTextBox.ScrollToLine(lineNumber);
                FocusEditor();
            }, DispatcherPriority.Background);
        }
    }

    private void OpenSearchPanel(SearchMode mode)
    {
        if (!CanShowSearchPanelForMode(mode))
            return;

        _searchMode = mode;
        NotifySearchModeChanged();
        IsSearchPanelVisible = true;
        _searchResults.Clear();
        _searchDisplayItems.Clear();
        SearchResultsListBox!.SelectedIndex = -1;
        SearchStatusText = string.Empty;
        if (mode == SearchMode.FindInFile)
            UpdateFindHighlights();
        else
            _ = RunActiveSearchAsync();
        FocusSearchInput();
    }

    // Ctrl+F and the search button both route through the same menu now.
    // Picking a mode opens the panel directly.
    private void ToggleSearchPanel(SearchMode mode)
    {
        if (IsSearchPanelVisible && _searchMode == mode)
        {
            IsSearchPanelVisible = false;
            FocusEditor();
            return;
        }

        OpenSearchPanel(mode);
    }

    // Clicking a mode tab inside the panel. Unlike ToggleSearchPanel this never
    // closes the panel - it just switches the active mode (and shows a hint if
    // the chosen mode can't run yet, e.g. Project search with no folder open).
    private void SwitchSearchMode(SearchMode mode)
    {
        ResetHistoryIndex();
        _searchMode = mode;
        NotifySearchModeChanged();

        if (!CanShowSearchPanelForMode(mode))
        {
            _searchCancellation?.Cancel();
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            SearchResultsListBox!.SelectedIndex = -1;
            SearchStatusText = GetModeUnavailableMessage(mode);
            return;
        }

        SearchStatusText = string.Empty;
        _searchResults.Clear();
        _searchDisplayItems.Clear();
        SearchResultsListBox!.SelectedIndex = -1;
        if (mode == SearchMode.FindInFile)
            UpdateFindHighlights();
        else
            _ = RunActiveSearchAsync();
        FocusSearchInput();
    }

    private bool CanShowSearchPanelForMode(SearchMode mode) => mode switch
    {
        SearchMode.FindInFile => CanShowFindInFile,
        SearchMode.FileByName => IsFolderOpen,
        SearchMode.ProjectSearch => IsFolderOpen,
        _ => false,
    };

    private static string GetModeUnavailableMessage(SearchMode mode) => mode switch
    {
        SearchMode.FileByName => "Open a folder to search files by name.",
        SearchMode.ProjectSearch => "Open a folder to search the project.",
        _ => string.Empty,
    };

    private void NotifySearchModeChanged()
    {
        OnPropertyChanged(nameof(IsFindInFileSearchMode));
        OnPropertyChanged(nameof(IsFileByNameSearchMode));
        OnPropertyChanged(nameof(IsProjectSearchMode));
        OnPropertyChanged(nameof(IsSearchResultsVisible));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(IsSearchPanelActive));
    }

    private void FocusSearchInput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
            if (searchTextBox is null) return;
            searchTextBox.Focus();
            searchTextBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void SearchTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsSearchPanelVisible = false;
            FocusEditor();
            ResetHistoryIndex();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_searchMode == SearchMode.FindInFile)
            {
                if (!string.IsNullOrEmpty(FindText))
                    AddToHistory(FindText);
                FindInEditor(forward: true);
            }
            else
            {
                OpenSelectedSearchResult();
            }
            ResetHistoryIndex();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            CycleHistory(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (IsSearchResultsVisible && SearchDisplayItems.Count > 0)
            {
                SearchResultsListBox?.Focus();
                ResetHistoryIndex();
            }
            else
            {
                CycleHistory(1);
            }
            e.Handled = true;
        }
        else
        {
            // Any regular keypress resets MRU cycling state.
            ResetHistoryIndex();
        }
    }

    // Unified search: debounced execution for Files/Project modes.

    private void RestartSearchDebounce()
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = RunActiveSearchAsync();
    }

    // MRU query history helpers.

    private List<string> GetCurrentHistory() => _searchMode switch
    {
        SearchMode.FindInFile => _findInFileHistory,
        SearchMode.FileByName => _fileByNameHistory,
        SearchMode.ProjectSearch => _projectSearchHistory,
        _ => _findInFileHistory,
    };

    private void AddToHistory(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var history = GetCurrentHistory();
        history.Remove(query);
        history.Insert(0, query);
        if (history.Count > 10)
            history.RemoveRange(10, history.Count - 10);
    }

    private void ResetHistoryIndex()
    {
        _historyIndex = -1;
        _savedFindText = string.Empty;
    }

    private void CycleHistory(int direction)
    {
        var history = GetCurrentHistory();
        if (history.Count == 0) return;

        if (_historyIndex < 0)
        {
            _savedFindText = FindText ?? string.Empty;
            _historyIndex = direction < 0 ? 0 : history.Count - 1;
        }
        else
        {
            _historyIndex += direction;
        }

        if (_historyIndex < 0)
        {
            FindText = _savedFindText;
            ResetHistoryIndex();
        }
        else if (_historyIndex >= history.Count)
        {
            FindText = _savedFindText;
            ResetHistoryIndex();
        }
        else
        {
            FindText = history[_historyIndex];
        }
    }

    // Search result display grouping.

    private void RebuildDisplayItems()
    {
        _searchDisplayItems.Clear();

        // In FindInFile mode or FileByName mode: no grouping, flat list.
        if (_searchMode != SearchMode.ProjectSearch)
        {
            foreach (var item in _searchResults)
                _searchDisplayItems.Add(new SearchDisplayItem { Result = item });
            UpdateSearchPanelMinWidth();
            return;
        }

        // Project search: group by file path.
        // Preserve each group's expanded/collapsed state across rebuilds - new
        // SearchFileGroup instances are constructed below, so without this the
        // state set by ToggleGroup() would be discarded on every call.
        var previousExpansion = _fileGroups
            .ToDictionary(g => g.FilePath, g => g.IsExpanded, StringComparer.OrdinalIgnoreCase);

        _fileGroups.Clear();
        var grouped = _searchResults
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SearchFileGroup
            {
                FilePath = g.Key,
                FileName = Path.GetFileName(g.Key),
                RelativePath = g.First().RelativePath,
                MatchCount = g.Count(),
                IsExpanded = previousExpansion.TryGetValue(g.Key, out var wasExpanded) && wasExpanded,
            })
            .ToList();

        foreach (var group in grouped)
        {
            _fileGroups.Add(group);
            _searchDisplayItems.Add(new SearchDisplayItem { IsGroupHeader = true, Group = group });
            if (group.IsExpanded)
            {
                foreach (var item in _searchResults.Where(r =>
                    string.Equals(r.Path, group.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    _searchDisplayItems.Add(new SearchDisplayItem { Result = item });
                }
            }
        }

        UpdateSearchPanelMinWidth();
    }

    private void UpdateSearchPanelMinWidth()
    {
        if (_searchResults.Count == 0)
        {
            SearchPanelBorder.MinWidth = 0;
            return;
        }

        double maxWidth = 0;
        const double charWidth12 = 7.2;
        const double charWidth11 = 6.4;
        const double iconWidth = 22;
        const double chevronWidth = 12;
        const double paddingAndMargins = 56;

        // Individual result row: Icon(22) + DisplayName(12px) + gap(8) + RelativePath(11px, star column)
        // The star column fills remaining space so we only need the auto columns.
        // Group header row: Chevron(12) + FileName(12px) + gap(8) + RelativePath(11px) + gap + MatchCount(11px)
        foreach (var item in _searchResults)
        {
            // Individual result width (auto columns only)
            double resultWidth = iconWidth + item.DisplayName.Length * charWidth12;
            if (resultWidth > maxWidth) maxWidth = resultWidth;
        }

        foreach (var group in _fileGroups)
        {
            // Group header width (all auto columns)
            double groupWidth = chevronWidth
                               + group.FileName.Length * charWidth12
                               + 8
                               + group.RelativePath.Length * charWidth11
                               + 8
                               + (group.MatchCount.ToString().Length + 9) * charWidth11; // "N matches"
            if (groupWidth > maxWidth) maxWidth = groupWidth;
        }

        SearchPanelBorder.MinWidth = Math.Min(maxWidth + paddingAndMargins, 560);
    }

    private void ToggleGroup(SearchFileGroup group)
    {
        group.IsExpanded = !group.IsExpanded;
        RebuildDisplayItems();
    }

    private async Task RunActiveSearchAsync()
    {
        if (!IsSearchPanelVisible)
            return;

        if (_searchMode == SearchMode.FindInFile)
        {
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            UpdateFindHighlights();
            return;
        }

        if (string.IsNullOrWhiteSpace(FindText))
        {
            _searchCancellation?.Cancel();
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            SearchStatusText = _searchMode == SearchMode.FileByName
                ? "Type a file name to find files."
                : "Type text to search the project.";
            return;
        }

        if (!CanShowSearchPanelForMode(_searchMode))
        {
            _searchCancellation?.Cancel();
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            SearchStatusText = GetModeUnavailableMessage(_searchMode);
            return;
        }

        var mode = _searchMode;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCancellation = cts;
        var token = cts.Token;

        IsSearchBusy = true;
        SearchStatusText = "Searching...";

        var matchCase = IsSearchMatchCaseEnabled;
        var wholeWord = IsSearchWholeWordEnabled;
        var useRegex = IsSearchRegexEnabled;
        var includeFilter = IsProjectSearchMode ? SearchIncludeFilter : null;
        var excludeFilter = IsProjectSearchMode ? SearchExcludeFilter : null;
        var cache = GetOrBuildSearchCache(_currentFolderPath!, includeFilter, excludeFilter);
        var sw = Stopwatch.StartNew();

        List<SearchResultItem> results;
        bool truncated = false;
        try
        {
            if (mode == SearchMode.FileByName)
            {
                results = await Task.Run(() => SearchFilesByName(FindText, _currentFolderPath!, matchCase, useRegex, cache.Files, token), token);
            }
            else
            {
                var (projectResults, wasTruncated) = await Task.Run(() => SearchProjectForText(FindText, _currentFolderPath!, matchCase, wholeWord, useRegex, cache.Files, token), token);
                results = projectResults;
                truncated = wasTruncated;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one (or the panel closed mid-search).
            IsSearchBusy = false;
            return;
        }

        if (token.IsCancellationRequested || !IsSearchPanelVisible || _searchMode != mode)
        {
            IsSearchBusy = false;
            return;
        }

        sw.Stop();
        _searchResults.Clear();
        foreach (var item in results)
            _searchResults.Add(item);
        RebuildDisplayItems();
        AddToHistory(FindText);
        SearchStatusText = BuildSearchStatusText(results.Count, truncated, sw.Elapsed);
        IsSearchBusy = false;
    }

    private string BuildSearchStatusText(int resultCount, bool truncated = false, TimeSpan elapsed = default)
    {
        var countText = resultCount.ToString("N0");
        var text = _searchMode switch
        {
            SearchMode.FileByName => resultCount == 0
                ? "No files found."
                : resultCount == 1 ? "1 file found." : $"{countText} files found.",
            SearchMode.ProjectSearch => resultCount == 0
                ? "No matches found."
                : resultCount == 1 ? "1 match found." : $"{countText} matches found.",
            _ => string.Empty,
        };
        if (truncated)
            text += " (showing first 2,000)";
        if (elapsed.TotalMilliseconds >= 1)
            text += $" ({elapsed.TotalMilliseconds:F0}ms)";
        return text;
    }

    private (List<string> Files, SearchIgnoreRules Rules) GetOrBuildSearchCache(string root, string? includeFilter = null, string? excludeFilter = null)
    {
        // Invalidate cache if filters changed.
        if (_searchFileCache is { } cached &&
            string.Equals(cached.Rules.IncludeFilterSnapshot, includeFilter ?? "", StringComparison.Ordinal) &&
            string.Equals(cached.Rules.ExcludeFilterSnapshot, excludeFilter ?? "", StringComparison.Ordinal))
        {
            return cached;
        }

        var rules = SearchIgnoreRules.Load(root, includeFilter, excludeFilter);
        rules.IncludeFilterSnapshot = includeFilter ?? "";
        rules.ExcludeFilterSnapshot = excludeFilter ?? "";
        var files = new List<string>();
        EnumerateProjectFiles(root, files, rules);
        _searchFileCache = (files, rules);
        return _searchFileCache.Value;
    }

    // Built-in directory names that are always skipped during project search,
    // regardless of .gitignore contents.
    private static readonly HashSet<string> DefaultIgnoreDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", ".vs",
    };

    /// <summary>
    /// Minimal .gitignore parser. Collects patterns from .gitignore files and
    /// answers whether a given path should be excluded from project search.
    /// </summary>
    private sealed class SearchIgnoreRules
    {
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
            catch { /* unreadable .gitignore — skip */ }
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
            // Pattern without slash — match against a single path component.
            if (!pattern.Contains('/') && !pattern.Contains('\\'))
                return MatchesFileName(pattern, componentName);

            return false;
        }

        private static bool MatchesFilePath(string pattern, string ruleRoot, string fullPath)
        {
            if (!pattern.Contains('/') && !pattern.Contains('\\'))
            {
                // Filename-only pattern — match against just the filename.
                return MatchesFileName(pattern, Path.GetFileName(fullPath));
            }

            // Path pattern — match against relative path from the .gitignore root.
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
    }

    // Walks the open folder's whole tree, skipping ignored directories and
    // guarding against directory cycles (junction points etc.).
    private static void EnumerateProjectFiles(string root, List<string> files, SearchIgnoreRules ignoreRules, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!visited.Add(normalized))
                return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                if (ignoreRules.ShouldSkipDirectory(dir)) continue;
                EnumerateProjectFiles(dir, files, ignoreRules, visited);
            }
            foreach (var file in Directory.GetFiles(root))
            {
                if (ignoreRules.ShouldSkipFile(file)) continue;
                files.Add(file);
            }
        }
        catch
        {
            // Unreadable directory - skip it and keep going elsewhere.
        }
    }

    private static List<SearchResultItem> SearchFilesByName(string query, string root, bool matchCase, bool useRegex, List<string> files, CancellationToken token)
    {
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options | RegexOptions.Compiled);
            }
            catch
            {
                return new List<SearchResultItem>();
            }
        }

        var scoredResults = new List<(SearchResultItem Item, int Score)>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);

            if (regex is not null)
            {
                var match = regex.Match(name);
                if (!match.Success) continue;

                var matchIndices = new List<int>();
                foreach (Group g in match.Groups)
                    foreach (Capture c in g.Captures)
                        for (var i = 0; i < c.Length; i++)
                            matchIndices.Add(c.Index + i);

                scoredResults.Add((new SearchResultItem
                {
                    Path = file,
                    DisplayName = name,
                    RelativePath = GetRelativePathOrName(root, file),
                    Icon = FileTreeItem.GetFileIcon(name),
                    MatchedIndices = matchIndices,
                    Score = match.Index == 0 ? 2000 : 1000,
                }, match.Index == 0 ? 2000 : 1000));
            }
            else
            {
                var (score, indices) = FuzzyMatch.Match(query, name, matchCase);
                if (score < 0) continue;

                scoredResults.Add((new SearchResultItem
                {
                    Path = file,
                    DisplayName = name,
                    RelativePath = GetRelativePathOrName(root, file),
                    Icon = FileTreeItem.GetFileIcon(name),
                    MatchedIndices = indices,
                    Score = score,
                }, score));
            }
        }

        scoredResults.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scoredResults.Select(r => r.Item).ToList();
    }

    private static (List<SearchResultItem> Results, bool Truncated) SearchProjectForText(string query, string root, bool matchCase, bool wholeWord, bool useRegex, List<string> files, CancellationToken token)
    {
        const int maxResults = 2000;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options | RegexOptions.Compiled);
            }
            catch
            {
                return (new List<SearchResultItem>(), false);
            }
        }

        var results = new List<SearchResultItem>();
        var truncated = false;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (IsImagePreviewFile(file) || IsBinaryContent(file)) continue;

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;

                    bool matched;
                    List<int>? matchIndices = null;
                    if (regex is not null)
                    {
                        var m = regex.Match(line);
                        matched = m.Success;
                        if (matched)
                        {
                            matchIndices = new List<int>();
                            foreach (Group g in m.Groups)
                                foreach (Capture c in g.Captures)
                                    for (var i = 0; i < c.Length; i++)
                                        matchIndices.Add(c.Index + i);
                        }
                    }
                    else
                    {
                        matched = line.Contains(query, comparison);
                        if (matched && wholeWord && !LineContainsWholeWord(line, query, comparison))
                            matched = false;
                    }

                    if (!matched) continue;

                    results.Add(new SearchResultItem
                    {
                        Path = file,
                        DisplayName = Path.GetFileName(file),
                        RelativePath = GetRelativePathOrName(root, file),
                        LineNumber = lineNumber,
                        PreviewText = $"{lineNumber}: {TrimSearchPreview(line)}",
                        Icon = FileTreeItem.GetFileIcon(file),
                        MatchedPreviewIndices = matchIndices is not null ? matchIndices.ToArray() : System.Array.Empty<int>(),
                    });
                    if (results.Count >= maxResults)
                    {
                        truncated = true;
                        return (results, truncated);
                    }
                }
            }
            catch
            {
                // Unreadable or locked file - skip it.
            }
        }
        return (results, truncated);
    }

    private static bool LineContainsWholeWord(string line, string needle, StringComparison comparison)
    {
        var idx = 0;
        while (idx <= line.Length - needle.Length)
        {
            idx = line.IndexOf(needle, idx, comparison);
            if (idx < 0) return false;
            if (IsWholeWordMatch(line, idx, needle.Length))
                return true;
            idx += Math.Max(1, needle.Length);
        }
        return false;
    }

    private static string GetRelativePathOrName(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            return string.IsNullOrEmpty(rel) ? Path.GetFileName(path) : rel;
        }
        catch
        {
            return path;
        }
    }

    private static string TrimSearchPreview(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 140 ? trimmed : trimmed[..140];
    }

    // Encoding detection & change


    /// Detects a file's encoding from its BOM, falling back to UTF-8 (no BOM).

    private static System.Text.Encoding DetectFileEncoding(string path)
    {
        try
        {
            Span<byte> bom = stackalloc byte[4];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = fs.Read(bom);

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);   // UTF-8 BOM
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return System.Text.Encoding.Unicode;          // UTF-16 LE
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode; // UTF-16 BE
            if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                return System.Text.Encoding.UTF32;

            // No BOM - default to UTF-8 without BOM
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }


    /// Shows an encoding picker and immediately re-saves the file if a different encoding is chosen.

    private async void EncodingStatusBarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasFileOpen) return;

        // CodePagesEncodingProvider is required for non-Unicode encodings (e.g. 1252) on
        // .NET Core / .NET 5+. Registering it more than once is safe - it's a no-op.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // Build the list defensively: skip any encoding the current runtime can't supply.
        var candidateEncodings = new (string Label, Func<System.Text.Encoding> Factory)[]
        {
            ("UTF-8",          () => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            ("UTF-8 with BOM", () => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
            ("UTF-16 LE",      () => System.Text.Encoding.Unicode),
            ("UTF-16 BE",      () => System.Text.Encoding.BigEndianUnicode),
            ("UTF-32",         () => System.Text.Encoding.UTF32),
            ("Windows-1252",   () => System.Text.Encoding.GetEncoding(1252)),
            ("ASCII",          () => System.Text.Encoding.ASCII),
        };

        var encodings = candidateEncodings
            .Select(c =>
            {
                try   { return ((string Label, System.Text.Encoding Enc)?)(c.Label, c.Factory()); }
                catch { return null; }
            })
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        System.Text.Encoding? chosen = null;
        Window? dialog = null;

        // Resolves the live accent color so the encoding highlight matches the active theme.
        var accentColor = AccentBrush.ToImmutable() is ISolidColorBrush accentSolid
            ? accentSolid.Color
            : Color.Parse("#8C00FF");

        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text = "Save file with encoding:",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var (label, enc) in encodings)
        {
            var isCurrent = enc.CodePage == _currentFileEncoding.CodePage &&
                            enc.GetPreamble().Length == _currentFileEncoding.GetPreamble().Length;
            var btn = new Button
            {
                Content = isCurrent ? $"{label}  ✓" : label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = isCurrent
                    ? new SolidColorBrush(accentColor, 0.18)
                    : new SolidColorBrush(Color.Parse("#252526")),
                Foreground = isCurrent
                    ? new SolidColorBrush(accentColor)
                    : Brushes.White,
                BorderBrush = new SolidColorBrush(DialogPalette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 7),
            };
            var capturedEnc = enc;
            btn.Click += (_, _) =>
            {
                chosen = capturedEnc;
                dialog?.Close();
            };
            panel.Children.Add(btn);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "The file will be re-saved immediately with the chosen encoding.",
            FontSize = 11,
            Foreground = new SolidColorBrush(DialogPalette.TextDim),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        dialog = new Window
        {
            Title = "Change File Encoding",
            Width = 280,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(DialogPalette.Surface),
            Content = panel,
        };

        await dialog.ShowDialog(this);

        if (chosen is null) return;

        // Preamble length distinguishes UTF-8 vs UTF-8 BOM even when CodePage matches.
        if (chosen.CodePage == _currentFileEncoding.CodePage &&
            chosen.GetPreamble().Length == _currentFileEncoding.GetPreamble().Length)
            return;

        try
        {
            await File.WriteAllTextAsync(_currentFilePath!, EditorTextBox.Document.Text, chosen);
            _currentFileEncoding = chosen;
            OnPropertyChanged(nameof(EncodingDisplayText));
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Change encoding", ex);
        }
    }

    private void FindNextButton_OnClick(object? sender, RoutedEventArgs e) =>
        FindInEditor(forward: true);

    private void FindPrevButton_OnClick(object? sender, RoutedEventArgs e) =>
        FindInEditor(forward: false);

    private void ReplaceCurrentMatch()
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var doc = EditorTextBox.Document.Text;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        var match = FindNextMatch(doc, FindText, Math.Max(0, caretOffset - 1), forward: true, comparison, IsSearchWholeWordEnabled, regex);
        if (match.Offset < 0)
            return;

        EditorTextBox.TextArea.Document.Replace(match.Offset, match.Length, ReplaceText ?? string.Empty);
        FindInEditor(forward: true);
    }

    private void ReplaceAllMatches()
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var doc = EditorTextBox.Document;
        var text = doc.Text;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var replacement = ReplaceText ?? string.Empty;
        var regex = BuildFindRegex();

        // Collect all match offsets in a single pass over the original text.
        var matches = new List<(int Offset, int Length)>();
        var searchIndex = 0;
        while (searchIndex <= text.Length)
        {
            var m = FindNextMatch(text, FindText, searchIndex, forward: true, comparison, IsSearchWholeWordEnabled, regex);
            if (m.Offset < 0)
                break;
            matches.Add(m);
            searchIndex = m.Offset + m.Length;
            if (m.Length == 0) break; // prevent infinite loop on zero-length regex matches
        }

        if (matches.Count == 0)
            return;

        // Build the replacement string by copying unchanged segments and inserting replacements.
        var sb = new System.Text.StringBuilder(text.Length + matches.Count * Math.Max(0, replacement.Length - FindText.Length));
        var pos = 0;
        foreach (var match in matches)
        {
            if (match.Offset > pos)
                sb.Append(text, pos, match.Offset - pos);
            sb.Append(replacement);
            pos = match.Offset + match.Length;
        }
        if (pos < text.Length)
            sb.Append(text, pos, text.Length - pos);

        // Single document operation — keeps the undo history clean.
        doc.Replace(0, text.Length, sb.ToString());

        SearchStatusText = $"Replaced {matches.Count} match{(matches.Count == 1 ? string.Empty : "es")}.";
    }

    // Editor context menu (right-click)

    private void EditorContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasSelection = EditorTextBox?.TextArea?.Selection is { IsEmpty: false };
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Name is "EditorCutMenuItem" or "EditorCopyMenuItem" or "EditorChangeAllOccurrencesMenuItem")
                item.IsEnabled = hasSelection;
        }
    }

    // Mirrors VS Code's "Change All Occurrences": grabs the current selection,
    // drops it into Find, and opens the Find/Replace panel so the user can
    // type a replacement and hit Replace All.
    private void EditorChangeAllOccurrencesMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var selectedText = sel.GetText();
        if (string.IsNullOrEmpty(selectedText)) return;

        FindText = selectedText;
        OpenSearchPanel(SearchMode.FindInFile);

        // Send focus to the Replace box instead of the Find box, since the
        // find term is already filled in and the user's next move is typing
        // the replacement.
        Dispatcher.UIThread.Post(() =>
        {
            var replaceTextBox = this.FindControl<TextBox>("ReplaceTextBox");
            if (replaceTextBox is null) return;
            replaceTextBox.Focus();
            replaceTextBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void EditorCutMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        EditorTextBox?.TextArea?.Selection?.ReplaceSelectionWithText(string.Empty);

    private async void EditorCopyMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.TextArea?.Selection is not { } sel) return;
        var text = sel.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private async void EditorPasteMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.TextArea is not { } textArea) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            textArea.Selection.ReplaceSelectionWithText(text);
    }

    private void FindInEditor(bool forward)
    {
        if (string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null) return;
        var doc = EditorTextBox.Document.Text;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        (int Offset, int Length) match;
        if (forward)
        {
            match = FindNextMatch(doc, FindText, caretOffset + 1, forward: true, comparison, IsSearchWholeWordEnabled, regex);
            if (match.Offset < 0)
                match = FindNextMatch(doc, FindText, 0, forward: true, comparison, IsSearchWholeWordEnabled, regex);
        }
        else
        {
            var searchTo = Math.Max(0, caretOffset - 1);
            match = FindNextMatch(doc, FindText, searchTo, forward: false, comparison, IsSearchWholeWordEnabled, regex);
            if (match.Offset < 0)
                match = FindNextMatch(doc, FindText, doc.Length - 1, forward: false, comparison, IsSearchWholeWordEnabled, regex);
        }

        if (match.Offset < 0) return;
        EditorTextBox.TextArea.Caret.Offset = match.Offset;
        EditorTextBox.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(EditorTextBox.TextArea, match.Offset, match.Offset + match.Length);
        EditorTextBox.ScrollToLine(EditorTextBox.Document.GetLineByOffset(match.Offset).LineNumber);

        // Track which match we're on for the "X of Y" status display.
        if (_findMatchOffsets.Count > 0)
        {
            _currentFindMatchIndex = _findMatchOffsets.BinarySearch(match.Offset);
            if (_currentFindMatchIndex < 0)
                _currentFindMatchIndex = ~_currentFindMatchIndex - 1;
            if (_currentFindMatchIndex < 0)
                _currentFindMatchIndex = _findMatchOffsets.Count - 1;
            UpdateFindStatusText();
        }
    }

    private static (int Offset, int Length) FindNextMatch(string text, string needle, int startIndex, bool forward, StringComparison comparison, bool wholeWord, Regex? regex = null)
    {
        if (regex is not null)
        {
            if (forward)
            {
                var m = regex.Match(text, startIndex);
                return m.Success ? (m.Index, m.Length) : (-1, 0);
            }
            else
            {
                // For backward regex search, scan all matches up to startIndex and return the last one before it.
                (int Offset, int Length) last = (-1, 0);
                foreach (Match m in regex.Matches(text))
                {
                    if (m.Index > startIndex) break;
                    last = (m.Index, m.Length);
                }
                return last;
            }
        }

        if (string.IsNullOrEmpty(needle))
            return (-1, 0);

        if (forward)
        {
            var index = Math.Max(0, startIndex);
            while (index <= text.Length - needle.Length)
            {
                index = text.IndexOf(needle, index, comparison);
                if (index < 0) return (-1, 0);
                if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                    return (index, needle.Length);
                index += Math.Max(1, needle.Length);
            }

            return (-1, 0);
        }

        var searchTo = Math.Min(Math.Max(0, startIndex), text.Length - 1);
        while (searchTo >= 0)
        {
            var index = text.LastIndexOf(needle, searchTo, comparison);
            if (index < 0) return (-1, 0);
            if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                return (index, needle.Length);
            searchTo = index - 1;
        }

        return (-1, 0);
    }

    private static bool IsWholeWordMatch(string text, int index, int length)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        var beforeOk = index == 0 || !IsWordChar(text[index - 1]);
        var afterIndex = index + length;
        var afterOk = afterIndex >= text.Length || !IsWordChar(text[afterIndex]);
        return beforeOk && afterOk;
    }

    private void UpdateFindHighlights()
    {
        if (EditorTextBox?.Document is null) return;

        _findHighlightRenderer.Clear();
        _findMatchOffsets.Clear();
        _currentFindMatchIndex = -1;

        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText))
        {
            SearchStatusText = string.Empty;
            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return;
        }

        var text = EditorTextBox.Document.Text;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        var searchIndex = 0;
        while (searchIndex <= text.Length)
        {
            var m = FindNextMatch(text, FindText, searchIndex, forward: true, comparison, IsSearchWholeWordEnabled, regex);
            if (m.Offset < 0) break;
            _findMatchOffsets.Add(m.Offset);
            _findHighlightRenderer.AddMatch(m.Offset, m.Length);
            searchIndex = m.Offset + m.Length;
            if (m.Length == 0) break;
        }

        // Determine which match the caret is on.
        if (_findMatchOffsets.Count > 0)
        {
            var caretOffset = EditorTextBox.TextArea.Caret.Offset;
            _currentFindMatchIndex = _findMatchOffsets.BinarySearch(caretOffset);
            if (_currentFindMatchIndex < 0)
                _currentFindMatchIndex = Math.Max(0, ~_currentFindMatchIndex - 1);
        }

        UpdateFindStatusText();
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private Regex? BuildFindRegex()
    {
        if (!IsSearchRegexEnabled || string.IsNullOrEmpty(FindText)) return null;
        try
        {
            var options = IsSearchMatchCaseEnabled ? RegexOptions.None : RegexOptions.IgnoreCase;
            return new Regex(FindText, options | RegexOptions.Compiled);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateFindStatusText()
    {
        if (!IsFindInFileSearchMode) return;
        var total = _findMatchOffsets.Count;
        if (total == 0)
            SearchStatusText = string.IsNullOrEmpty(FindText) ? string.Empty : "No matches.";
        else
            SearchStatusText = $"{_currentFindMatchIndex + 1} of {total}";
    }

    private void IncreaseFontSizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorFontSize = Math.Min(32, EditorFontSize + 1);

    private void DecreaseFontSizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorFontSize = Math.Max(8, EditorFontSize - 1);

    private async void ClearRecentFilesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasRecentFiles) return;

        // "Clear Recent Files" only removes non-pinned entries.
        var clearable = RecentFiles.Where(f => !f.IsPinned).ToList();
        if (clearable.Count == 0) return;

        var confirmed = await ShowConfirmationDialogAsync(
            "Clear Recent Files?",
            "This removes every non-pinned entry from your Recent Files list. Pinned entries are kept. The files themselves aren't touched - only the list of shortcuts to them. This can't be undone.",
            confirmLabel: "Clear",
            isDestructive: true);

        if (!confirmed) return;

        foreach (var item in clearable)
            RecentFiles.Remove(item);

        SaveSettings();
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private async void InstallMarketplaceExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MarketplaceExtension marketplaceExtension })
            await InstallMarketplaceExtensionAsync(marketplaceExtension);
    }

    private async void UpdateAllMarketplaceExtensionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsUpdatingAllExtensions || _isAutoUpdatingExtensions)
            return;

        var pendingUpdateIds = MarketplaceExtensions
            .Where(extension => extension.IsUpdateAvailable && extension.IsInstallEnabled)
            .Select(extension => extension.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingUpdateIds.Count == 0)
        {
            ExtensionsStatusText = "All extensions are up to date.";
            return;
        }

        IsUpdatingAllExtensions = true;
        var successfulUpdates = 0;
        var failedUpdates = 0;

        try
        {
            for (var index = 0; index < pendingUpdateIds.Count; index++)
            {
                var extensionId = pendingUpdateIds[index];
                var marketplaceExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (marketplaceExtension is null || !marketplaceExtension.IsUpdateAvailable)
                    continue;

                ExtensionsStatusText = $"Updating {index + 1} of {pendingUpdateIds.Count}: {marketplaceExtension.Name}...";
                await InstallMarketplaceExtensionAsync(marketplaceExtension);

                var refreshedExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (refreshedExtension is not null && refreshedExtension.IsInstalled && !refreshedExtension.IsUpdateAvailable)
                    successfulUpdates++;
                else
                    failedUpdates++;
            }

            ExtensionsStatusText = failedUpdates == 0
                ? $"Updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}."
                : $"Updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}. {failedUpdates} couldn't be updated.";
        }
        finally
        {
            IsUpdatingAllExtensions = false;
        }
    }

    // Starts/stops the extension auto-update timer to match IsAutoUpdateExtensionsEnabled; called at startup and on toggle.
    private void UpdateExtensionAutoUpdateLifecycle()
    {
        _extensionAutoUpdateTimer.Stop();
        if (IsAutoUpdateExtensionsEnabled)
            _extensionAutoUpdateTimer.Start();
    }

    // The app auto-updater's timer and tick handler now live in AppUpdateScheduler (Updater.cs).

    // Fires every few hours while Kodo is open and the setting is enabled, so
    // extensions published mid-session aren't only picked up on next launch.
    private async void ExtensionAutoUpdateTimer_OnTick(object? sender, EventArgs e)
    {
        if (!IsAutoUpdateExtensionsEnabled)
            return;

        // suppressWatchdog=true: this is a silent background check, so a stall shouldn't pop a timeout dialog.
        await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
        await AutoUpdateExtensionsIfEnabledAsync();
    }

    // Fires hourly to keep the Marketplace tab current, even with auto-update off.
    private async void MarketplaceRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        // suppressWatchdog=true for the same reason as the extension sweep - a silent hourly refresh shouldn't pop a dialog.
        await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
    }

    // Used on startup: refreshes extensions/marketplace first, then silently installs pending updates if opted in.
    private async Task RefreshExtensionsAndAutoUpdateAsync()
    {
        await RefreshExtensionsDataAsync();
        await AutoUpdateExtensionsIfEnabledAsync();
    }

    // Silently installs pending updates when opted in; guards against overlap.
    private async Task AutoUpdateExtensionsIfEnabledAsync()
    {
        if (!IsAutoUpdateExtensionsEnabled || _isAutoUpdatingExtensions || IsUpdatingAllExtensions)
            return;

        var pendingUpdateIds = MarketplaceExtensions
            .Where(extension => extension.IsUpdateAvailable && extension.IsInstallEnabled)
            .Select(extension => extension.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingUpdateIds.Count == 0)
            return;

        _isAutoUpdatingExtensions = true;
        var successfulUpdates = 0;
        var failedUpdates = 0;

        try
        {
            for (var index = 0; index < pendingUpdateIds.Count; index++)
            {
                var extensionId = pendingUpdateIds[index];
                var marketplaceExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (marketplaceExtension is null || !marketplaceExtension.IsUpdateAvailable || marketplaceExtension.IsInstalling)
                    continue;

                if (!IsAutoUpdateExtensionsInBackgroundEnabled)
                    ExtensionsStatusText = $"Auto-updating {index + 1} of {pendingUpdateIds.Count}: {marketplaceExtension.Name}...";
                await InstallMarketplaceExtensionAsync(marketplaceExtension);

                var refreshedExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (refreshedExtension is not null && refreshedExtension.IsInstalled && !refreshedExtension.IsUpdateAvailable)
                    successfulUpdates++;
                else
                    failedUpdates++;
            }

            if ((successfulUpdates > 0 || failedUpdates > 0) && !IsAutoUpdateExtensionsInBackgroundEnabled)
            {
                ExtensionsStatusText = failedUpdates == 0
                    ? $"Automatically updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}."
                    : $"Automatically updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}. {failedUpdates} couldn't be updated.";
            }
        }
        finally
        {
            _isAutoUpdatingExtensions = false;
        }
    }

    private async void UpdateInstalledExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedExtension extension })
            return;

        var marketplaceExtension = GetMarketplaceExtensionForInstalled(extension);
        if (marketplaceExtension is null)
        {
            ExtensionsStatusText = $"Couldn't find an update source for {extension.Name}.";
            return;
        }

        await InstallMarketplaceExtensionAsync(marketplaceExtension);
    }

    private async void UninstallExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedExtension extension }) return;

        var confirmed = await ShowConfirmationDialogAsync(
            "Uninstall extension?",
            $"This removes '{extension.Name}' from disk and disables it immediately. This can't be undone.",
            confirmLabel: "Uninstall",
            isDestructive: true);

        if (!confirmed) return;

        await UninstallExtensionAsync(extension);
    }

    // Shows a Ctrl+click tooltip and hand cursor over URLs; only fires on state transitions, not every pixel of movement.
    private void EditorTextView_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var textView = EditorTextBox.TextArea.TextView;
        var nowOverLink = IsPointerOverLink(e.GetPosition(textView), textView);

        if (nowOverLink == _isPointerOverEditorLink)
            return; // no state change - leave tooltip and cursor alone

        _isPointerOverEditorLink = nowOverLink;

        if (nowOverLink)
        {
            ToolTip.SetTip(textView, "Ctrl+click to open link");
            ToolTip.SetShowDelay(textView, 400);
            textView.Cursor = new Cursor(StandardCursorType.Hand);
        }
        else
        {
            ToolTip.SetTip(textView, null);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
        }
    }

    private void EditorTextView_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isPointerOverEditorLink) return;
        _isPointerOverEditorLink = false;
        var textView = EditorTextBox.TextArea.TextView;
        ToolTip.SetTip(textView, null);
        textView.Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    // Returns true if the given pointer position (relative to the TextView, not
    // scroll-adjusted) falls within any URL span on the visible line.
    private bool IsPointerOverLink(Point pointerPosition, AvaloniaEdit.Rendering.TextView textView)
    {
        var pos = textView.GetPositionFloor(pointerPosition + textView.ScrollOffset);
        if (pos is null) return false;

        try
        {
            var line = EditorTextBox.Document.GetLineByNumber(pos.Value.Line);
            var lineText = EditorTextBox.Document.GetText(line.Offset, line.Length);
            var colOffset = pos.Value.Column - 1; // Column is 1-based

            if (StrictLinkElementGenerator.TryGetLinkSpan(lineText, colOffset, out _, out _))
                return true;
        }
        catch
        {
            // Document may be null or line out of range during rapid edits - treat as no link
        }

        return false;
    }

    // TextEditor fires EventHandler (not RoutedEventHandler) - signature must match exactly
    private void EditorTextBox_OnTextChanged(object? sender, EventArgs e)
    {
        _rainbowBracketColorizer.InvalidateCache();
        if (_suppressDirtyTracking) return;
        ClearAutoSaveStatus();
        _isDirty = true;
        if (ActiveEditorTab is not null)
        {
            ActiveEditorTab.Content = EditorTextBox.Document.Text;
            ActiveEditorTab.IsDirty = true;
        }
        QueueRefreshState(fullRefresh: true);
        QueueWordCountRefresh();
        RestartAutoSaveTimerIfNeeded();
        QueueInsightRefresh();
        if (IsFindInFileSearchMode && IsSearchPanelVisible)
            UpdateFindHighlights();
    }

	// Fires before the character is written; skips an auto-inserted closing character.
    private void EditorTextArea_OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!IsSmartSyntaxEnabled()) return;
        if (IsMarkdownFile(_currentFilePath)) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch     = e.Text[0];
        var caret  = EditorTextBox.TextArea.Caret;
        var doc    = EditorTextBox.Document;
        var offset = caret.Offset;
        var selection = EditorTextBox.TextArea.Selection;

        if (!selection.IsEmpty && BracketPairs.TryGetValue(ch, out var selectionClosing))
        {
            var segment = selection.SurroundingSegment;
            if (segment is not null)
            {
                var selectedText = selection.GetText();
                doc.Replace(segment, $"{ch}{selectedText}{selectionClosing}");
                caret.Offset = segment.Offset + selectedText.Length + 2;
                e.Handled = true;
                return;
            }
        }

        if (!ClosingChars.Contains(ch)) return;

        if (ch == '}' && TryAlignClosingDelimiterBeforeInsert(doc, caret, '}'))
            offset = caret.Offset;

        if (offset >= doc.TextLength) return;
        if (doc.GetCharAt(offset) != ch) return;

        // Asymmetric pairs are always safe to skip; symmetric pairs only skip mid-pair.
        bool skip = ch is ')' or ']' or '}' or '>';
        if (!skip && (ch == '"' || ch == '\''))
            skip = offset > 0 && doc.GetCharAt(offset - 1) == ch;

        if (skip)
        {
            caret.Offset = offset + 1;
            e.Handled = true;
        }
    }

    // Fires AFTER the character has been written into the document.
    // Used to insert the matching closing character right after the opener.
    private void EditorTextArea_OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!IsSmartSyntaxEnabled()) return;
        if (IsMarkdownFile(_currentFilePath)) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];

        if (!BracketPairs.TryGetValue(ch, out var closing)) return;

        var caret  = EditorTextBox.TextArea.Caret;
        var doc    = EditorTextBox.Document;
        var offset = caret.Offset;

        // For symmetric pairs, don't auto-close when the next char is alphanumeric
        // (avoids nuisance completions mid-word, e.g. typing " in  it's).
        if (ch == '"' || ch == '\'' || ch == '`')
        {
            if (offset < doc.TextLength)
            {
                var next = doc.GetCharAt(offset);
                if (char.IsLetterOrDigit(next) || next == ch) return;
            }
        }

        // Insert the closer and explicitly restore the caret to the space between the pair.
        doc.Insert(offset, closing.ToString());
        caret.Offset = offset;
    }

    // Insight (predictive completion popup)

    // Recomputes and shows/updates/hides the completion popup based on the word at
    // the caret. Called after every real text edit (see EditorTextBox_OnTextChanged).
    private void UpdateInsight()
    {
        if (!IsInsightEnabled)
        {
            CloseCompletionWindow();
            return;
        }

        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null)
        {
            CloseCompletionWindow();
            return;
        }

        // Skips plain-text files and untitled (unsaved) tabs, which have no language to predict against.
        if (ActiveEditorTab is null || ActiveEditorTab.IsUntitled || IsPlainTextFile(_currentFilePath))
        {
            CloseCompletionWindow();
            return;
        }

        if (IsInsightBlacklisted(_currentFilePath))
        {
            CloseCompletionWindow();
            return;
        }

        var doc = EditorTextBox.Document;
        var offset = Math.Clamp(EditorTextBox.TextArea.Caret.Offset, 0, doc.TextLength);
        var text = doc.Text;

        var wordStart = InsightEngine.FindWordStart(text, offset);
        var prefix = text[wordStart..offset];

        // Require at least one word character already typed, so the popup doesn't pop
        // up after whitespace, punctuation, or a fresh newline.
        if (prefix.Length == 0)
        {
            CloseCompletionWindow();
            return;
        }

        var fileKey = ActiveEditorTab?.Path ?? "untitled";
        _InsightEngine.ScanDocument(fileKey, text);

        // Keeps popup rows in sync with the active theme even if it changed
        // while a suggestion list was already open.
        InsightSuggestion.PanelForeground = PrimaryTextBrush;
        InsightSuggestion.MutedForeground = MutedTextBrush;

        var suggestions = _InsightEngine.GetSuggestions(prefix, fileKey, CurrentLanguageExtension, text, offset);
        if (suggestions.Count == 0)
        {
            CloseCompletionWindow();
            return;
        }

        if (_completionWindow is null)
        {
            _completionWindow = CreateCompletionWindow();
            _completionWindow.StartOffset = wordStart;
            foreach (var suggestion in suggestions)
                _completionWindow.CompletionList.CompletionData.Add(suggestion);
            _completionWindow.Show();
        }
        else
        {
            _completionWindow.StartOffset = wordStart;
            _completionWindow.CompletionList.CompletionData.Clear();
            foreach (var suggestion in suggestions)
                _completionWindow.CompletionList.CompletionData.Add(suggestion);
        }
    }

    private void CloseCompletionWindow()
    {
        _completionWindow?.Close();
        _completionWindow = null;
    }

    // Row geometry - kept as named constants so the popup's MaxHeight can be an
    // exact multiple of a row, rather than cutting off partway through one.
    private const double InsightRowHeight = 26d;
    private const double InsightListVerticalPadding = 8d;  // ListBox Padding(0,4) top+bottom
    private const double InsightBorderThickness = 2d;      // 1px top + 1px bottom
    private const int InsightVisibleRows = 8;

    // Builds a CompletionWindow styled to match Kodo's own panels (CardBrush background,
    // SurfaceBorderBrush border, accent-tinted selected row).
    private CompletionWindow CreateCompletionWindow()
    {
        var window = new CompletionWindow(EditorTextBox.TextArea)
        {
            MaxHeight = InsightRowHeight * InsightVisibleRows
                + InsightListVerticalPadding + InsightBorderThickness,
            MaxWidth = 460,
            Width = 460,
        };

        // CompletionWindow itself is just a Popup (positioning only, no chrome) - the
        // actual panel/border/font live on CompletionList, which it hosts as Child.
        var panelBrush = CardBrush;
        window.CompletionList.Background = panelBrush;
        window.CompletionList.BorderBrush = SurfaceBorderBrush;
        window.CompletionList.BorderThickness = new Thickness(1);
        window.CompletionList.FontFamily = EditorTextBox.FontFamily;
        window.CompletionList.HorizontalAlignment = HorizontalAlignment.Stretch;

        // Rounds the popup panel to match every other card/flyout in the app.
        var panelCornerStyle = new Style(x => x.OfType<CompletionList>().Template().OfType<Border>());
        panelCornerStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
        window.Styles.Add(panelCornerStyle);

        if (window.CompletionList.ListBox is { } listBox)
        {
            // Opaque background so editor text can't bleed through the gaps between rows.
            listBox.Background = panelBrush;
            listBox.Padding = new Thickness(0, 4);
            listBox.Margin = new Thickness(0);
            listBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        // Zeroes ListBoxItem's background transition so rows render instantly instead of crossfading.
        var noTransitionStyle = new Style(x => x.OfType<ListBoxItem>());
        noTransitionStyle.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions()));
        window.Styles.Add(noTransitionStyle);

        // Paints ListBoxItem's Background directly so each row's highlight stretches to the popup's full width.
        var baseRowStyle = new Style(x => x.OfType<ListBoxItem>());
        baseRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, panelBrush));
        baseRowStyle.Setters.Add(new Setter(Avalonia.Controls.ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        window.Styles.Add(baseRowStyle);

        // Selected row: Kodo's accent color at low opacity over the panel, same
        // tinting pattern used for editor text selection - not a fixed VS Code blue.
        var accentTint = AccentBrush.ToImmutable() is ISolidColorBrush accentSolid
            ? new SolidColorBrush(accentSolid.Color, 0.35)
            : new SolidColorBrush(Color.Parse("#8C00FF"), 0.35);
        var selectedRowStyle = new Style(x => x.OfType<ListBoxItem>().Class(":selected"));
        selectedRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, accentTint));
        window.Styles.Add(selectedRowStyle);

        // Hover row: reuses the app's own button-hover color instead of a hardcoded gray.
        var hoverRowStyle = new Style(x => x.OfType<ListBoxItem>().Class(":pointerover"));
        hoverRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, ButtonHoverBrush));
        window.Styles.Add(hoverRowStyle);

        var rowPaddingStyle = new Style(x => x.OfType<ListBoxItem>());
        rowPaddingStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(6, 3)));
        // A fixed MinHeight keeps every row the same height so the virtualizing panel positions them consistently.
        rowPaddingStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.MinHeightProperty, InsightRowHeight));
        window.Styles.Add(rowPaddingStyle);

        window.Closed += (_, _) => _completionWindow = null;
        return window;
    }

    private void MainWindow_EditorKeyIntercept_OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Lets the open Insight popup own navigation/accept/dismiss keys before smart-enter/smart-tab.
        if (_completionWindow is not null && e.Key is Key.Tab or Key.Escape
            or Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            return;
        }

        // Doesn't intercept keys destined for the terminal.
        // Uses the TopLevel FocusManager, not e.Source, for the true current focus owner.
        if (IsTerminalVisible && ActiveTerminalSession is not null)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            var isTerminalFocused = focused is not null &&
                (ReferenceEquals(focused, TerminalHostControl) ||
                 focused.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, TerminalHostControl)));

            if (isTerminalFocused)
                return;
        }

        if (!IsEditorKeyEvent(e))
            return;

        if (EditorTextBox?.Document is null)
            return;

        var textArea = EditorTextBox.TextArea;
        var caret = textArea.Caret;
        var doc = EditorTextBox.Document;
        // Swallow editor-local Find/Find-in-project so AvaloniaEdit doesn't open its own find UI,
        // whatever gesture the user has them bound to.
        if (MatchesKeybind(e, "FindInProject"))
        {
            OpenSearchPanel(SearchMode.ProjectSearch);
            e.Handled = true;
            return;
        }
        if (MatchesKeybind(e, "FindInFile"))
        {
            OpenSearchPanel(SearchMode.FindInFile);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when IsSmartSyntaxEnabled() && (e.KeyModifiers & KeyModifiers.Shift) != KeyModifiers.Shift:
                HandleSmartEnter(doc, caret);
                e.Handled = true;
                return;

            case Key.Tab when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.Shift:
                HandleTabKey(() => HandleOutdent(doc, textArea.Selection, caret), doc, caret);
                e.Handled = true;
                return;

            case Key.Tab when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.None:
                HandleTabKey(() => HandleIndent(doc, textArea.Selection, caret), doc, caret);
                e.Handled = true;
                return;

            case Key.Back when IsSmartSyntaxEnabled():
                if (HandleSmartBackspace(doc, caret))
                {
                    e.Handled = true;
                    return;
                }
                break;

            case Key.V when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _ = HandleSmartPasteAsync(doc, textArea, caret);
                return;

        }

        if (IsSmartSyntaxEnabled() && MatchesKeybind(e, "ToggleLineComment"))
        {
            ToggleLineComment(doc, textArea, textArea.Selection, caret);
            e.Handled = true;
        }
    }

    private bool IsEditorKeyEvent(KeyEventArgs e)
    {
        if (EditorTextBox is null || e.Source is not Visual visual)
            return false;

        if (ReferenceEquals(visual, EditorTextBox) || ReferenceEquals(visual, EditorTextBox.TextArea))
            return true;

        return visual.GetSelfAndVisualAncestors().Any(v =>
            ReferenceEquals(v, EditorTextBox) || ReferenceEquals(v, EditorTextBox.TextArea));
    }

    private void HandleTabKey(Action tabAction, AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret)
    {
        try
        {
            tabAction();
        }
        catch
        {
            // Keep Tab editor-local even if AvaloniaEdit reports an invalid selection snapshot.
            var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
            doc.Insert(safeOffset, GetIndentUnit());
            SetCaretOffsetSafely(caret, doc, safeOffset + GetIndentUnit().Length);
        }
    }

    private static void SetCaretOffsetSafely(
        AvaloniaEdit.Editing.Caret caret,
        AvaloniaEdit.Document.TextDocument doc,
        int desiredOffset)
    {
        caret.Offset = Math.Clamp(desiredOffset, 0, doc.TextLength);
    }

    private void HandleSmartEnter(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret)
    {
        var offset = caret.Offset;
        var line = doc.GetLineByOffset(offset);
        var lineText = doc.GetText(line);
        var caretColumnInLine = offset - line.Offset;
        var textBeforeCaret = lineText[..Math.Min(caretColumnInLine, lineText.Length)];
        var textAfterCaret = lineText[Math.Min(caretColumnInLine, lineText.Length)..];
        var indent = GetLeadingWhitespace(textBeforeCaret);
        var trimmedBeforeCaret = textBeforeCaret.TrimEnd();
        var extraIndent = ShouldIncreaseIndentAfter(trimmedBeforeCaret) ? GetIndentUnit() : string.Empty;

        if (ShouldInsertStructuredBlock(trimmedBeforeCaret, textAfterCaret))
        {
            var blockText = Environment.NewLine + indent + extraIndent + Environment.NewLine + indent;
            doc.Insert(offset, blockText);
            caret.Offset = offset + Environment.NewLine.Length + indent.Length + extraIndent.Length;
            return;
        }

        var adjustedIndent = StartsWithClosingDelimiter(textAfterCaret)
            ? RemoveOneIndentUnit(indent)
            : indent;

        var newLineText = Environment.NewLine + adjustedIndent + extraIndent;
        doc.Insert(offset, newLineText);
        caret.Offset = offset + newLineText.Length;
    }

    private bool HandleSmartBackspace(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret)
    {
        var selection = EditorTextBox.TextArea.Selection;
        if (selection is not null && !selection.IsEmpty)
            return false;

        var offset = caret.Offset;
        if (offset <= 0 || offset >= doc.TextLength)
            return false;

        var opening = doc.GetCharAt(offset - 1);
        if (!BracketPairs.TryGetValue(opening, out var closing))
            return false;

        if (doc.GetCharAt(offset) != closing)
            return false;

        doc.Remove(offset - 1, 2);
        SetCaretOffsetSafely(caret, doc, offset - 1);
        return true;
    }

    private void HandleIndent(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        if (selection is null || selection.IsEmpty)
        {
            var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
            doc.Insert(safeOffset, GetIndentUnit());
            SetCaretOffsetSafely(caret, doc, safeOffset + GetIndentUnit().Length);
            return;
        }

        var segment = selection.SurroundingSegment;
        if (segment is null)
        {
            var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
            doc.Insert(safeOffset, GetIndentUnit());
            SetCaretOffsetSafely(caret, doc, safeOffset + GetIndentUnit().Length);
            return;
        }

        var lines = GetSelectedLines(doc, segment.Offset, segment.EndOffset);
        foreach (var line in lines.OrderByDescending(l => l.Offset))
            doc.Insert(line.Offset, GetIndentUnit());

        SetCaretOffsetSafely(caret, doc, segment.EndOffset + (GetIndentUnit().Length * lines.Count));
    }

    private void HandleOutdent(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        if (selection is null || selection.IsEmpty)
        {
            var line = doc.GetLineByOffset(caret.Offset);
            var lineText = doc.GetText(line);
            var caretColumnInLine = caret.Offset - line.Offset;
            var removable = GetOutdentLength(lineText, caretColumnInLine);
            if (removable <= 0)
                return;

            doc.Remove(line.Offset, removable);
            SetCaretOffsetSafely(caret, doc, caret.Offset - removable);
            return;
        }

        var segment = selection.SurroundingSegment;
        if (segment is null)
            return;

        var lines = GetSelectedLines(doc, segment.Offset, segment.EndOffset);
        var removed = 0;
        foreach (var line in lines.OrderByDescending(l => l.Offset))
        {
            var lineText = doc.GetText(line);
            var removable = GetOutdentLength(lineText, lineText.Length);
            if (removable <= 0)
                continue;

            doc.Remove(line.Offset, removable);
            removed += removable;
        }

        SetCaretOffsetSafely(caret, doc, Math.Max(segment.Offset, segment.EndOffset - removed));
    }

    private static string GetLeadingWhitespace(string text)
    {
        var length = 0;
        while (length < text.Length && char.IsWhiteSpace(text[length]) && text[length] != '\r' && text[length] != '\n')
            length++;

        return text[..length];
    }

    private static bool ShouldIncreaseIndentAfter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.EndsWith(":", StringComparison.Ordinal) ||
               text.EndsWith("{", StringComparison.Ordinal) ||
               text.EndsWith("[", StringComparison.Ordinal) ||
               text.EndsWith("(", StringComparison.Ordinal) ||
               text.EndsWith("=>", StringComparison.Ordinal) ||
               text.EndsWith(" then", StringComparison.OrdinalIgnoreCase) ||
               text.EndsWith(" do", StringComparison.OrdinalIgnoreCase);
    }

    private string GetIndentUnit() => "\t";

    private static bool ShouldInsertStructuredBlock(string textBeforeCaret, string textAfterCaret)
    {
        var trimmedAfter = textAfterCaret.TrimStart();
        if (string.IsNullOrEmpty(trimmedAfter))
            return false;

        if (!BracketPairs.TryGetValue(textBeforeCaret.LastOrDefault(), out var closing))
            return false;

        return trimmedAfter.Length > 0 && trimmedAfter[0] == closing && closing is ')' or ']' or '}';
    }

    private static bool StartsWithClosingDelimiter(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("}", StringComparison.Ordinal) ||
               trimmed.StartsWith("]", StringComparison.Ordinal) ||
               trimmed.StartsWith(")", StringComparison.Ordinal);
    }

    private bool TryAlignClosingDelimiterBeforeInsert(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret, char closing)
    {
        var offset = caret.Offset;
        var line = doc.GetLineByOffset(offset);
        var lineText = doc.GetText(line);
        var caretColumnInLine = offset - line.Offset;
        var textBeforeCaret = lineText[..Math.Min(caretColumnInLine, lineText.Length)];

        if (textBeforeCaret.Length == 0 || !string.IsNullOrWhiteSpace(textBeforeCaret))
            return false;

        var removable = GetOutdentLength(textBeforeCaret, textBeforeCaret.Length);
        if (removable <= 0)
            return false;

        doc.Remove(line.Offset, removable);
        SetCaretOffsetSafely(caret, doc, offset - removable);
        return true;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private string ReindentPastedText(string text, AvaloniaEdit.Document.TextDocument doc, int offset)
    {
        var normalized = NormalizeLineEndings(text);
        if (!normalized.Contains('\n'))
            return text;

        var line = doc.GetLineByOffset(Math.Clamp(offset, 0, doc.TextLength));
        var lineText = doc.GetText(line);
        var caretColumnInLine = Math.Clamp(offset - line.Offset, 0, lineText.Length);
        var textBeforeCaret = lineText[..caretColumnInLine];
        var baseIndent = GetLeadingWhitespace(textBeforeCaret);
        var pasteLines = normalized.Split('\n');

        if (pasteLines.Length <= 1)
            return text;

        var firstNonEmptyIndex = Array.FindIndex(pasteLines, static l => !string.IsNullOrWhiteSpace(l));
        if (firstNonEmptyIndex < 0)
            return text;

        var commonIndent = GetLeadingWhitespace(pasteLines[firstNonEmptyIndex]);
        for (var i = firstNonEmptyIndex + 1; i < pasteLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(pasteLines[i]))
                continue;

            commonIndent = GetSharedIndent(commonIndent, GetLeadingWhitespace(pasteLines[i]));
            if (commonIndent.Length == 0)
                break;
        }

        for (var i = 1; i < pasteLines.Length; i++)
        {
            if (pasteLines[i].Length == 0)
                continue;

            var trimmedLine = pasteLines[i];
            if (commonIndent.Length > 0 && trimmedLine.StartsWith(commonIndent, StringComparison.Ordinal))
                trimmedLine = trimmedLine[commonIndent.Length..];

            pasteLines[i] = baseIndent + trimmedLine;
        }

        return string.Join(Environment.NewLine, pasteLines);
    }

    private static string GetSharedIndent(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var length = 0;
        while (length < max && left[length] == right[length])
            length++;

        return left[..length];
    }

    private async Task HandleSmartPasteAsync(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Editing.Caret caret)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        var insertionText = ReindentPastedText(text, doc, caret.Offset);
        var selection = textArea.Selection;
        if (selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null)
        {
            var segment = selection.SurroundingSegment;
            doc.Replace(segment, insertionText);
            SetCaretOffsetSafely(caret, doc, segment.Offset + insertionText.Length);
            return;
        }

        var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
        doc.Insert(safeOffset, insertionText);
        SetCaretOffsetSafely(caret, doc, safeOffset + insertionText.Length);
    }

    private void ToggleLineComment(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        var lineCommentToken = CurrentLanguageExtension?.CommentLine;
        if (string.IsNullOrWhiteSpace(lineCommentToken))
            return;

        var startOffset = selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null
            ? selection.SurroundingSegment.Offset
            : caret.Offset;
        var endOffset = selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null
            ? selection.SurroundingSegment.EndOffset
            : caret.Offset;

        var lines = GetSelectedLines(doc, startOffset, endOffset);
        if (lines.Count == 0)
            return;

        var shouldUncomment = lines
            .Where(line => !string.IsNullOrWhiteSpace(doc.GetText(line)))
            .All(line =>
            {
                var text = doc.GetText(line);
                var indent = GetLeadingWhitespace(text);
                return text[indent.Length..].StartsWith(lineCommentToken, StringComparison.Ordinal);
            });

        var delta = 0;
        foreach (var line in lines.OrderByDescending(l => l.Offset))
        {
            var text = doc.GetText(line);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var indent = GetLeadingWhitespace(text);
            var commentOffset = line.Offset + indent.Length;
            if (shouldUncomment)
            {
                if (text[indent.Length..].StartsWith(lineCommentToken, StringComparison.Ordinal))
                {
                    var removedForLine = lineCommentToken.Length;
                    doc.Remove(commentOffset, lineCommentToken.Length);
                    if (text.Length > indent.Length + lineCommentToken.Length && text[indent.Length + lineCommentToken.Length] == ' ')
                    {
                        doc.Remove(commentOffset, 1);
                        removedForLine++;
                    }

                    delta -= removedForLine;
                }
            }
            else
            {
                doc.Insert(commentOffset, lineCommentToken + " ");
                delta += lineCommentToken.Length + 1;
            }
        }

        if (selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null)
        {
            var segment = selection.SurroundingSegment;
            var newEnd = Math.Max(segment.Offset, segment.EndOffset + delta);
            textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, segment.Offset, newEnd);
        }
        else
        {
            SetCaretOffsetSafely(caret, doc, caret.Offset + delta);
        }
    }

    private string RemoveOneIndentUnit(string indent)
    {
        var indentUnit = GetIndentUnit();
        if (indent.EndsWith(indentUnit, StringComparison.Ordinal))
            return indent[..^indentUnit.Length];

        return indent.Length > 0 ? indent[..^1] : indent;
    }

    private int GetOutdentLength(string lineText, int availableLength)
    {
        if (string.IsNullOrEmpty(lineText) || availableLength <= 0)
            return 0;

        var maxLength = Math.Min(availableLength, lineText.Length);
        var indentUnit = GetIndentUnit();
        if (maxLength >= indentUnit.Length &&
            lineText[..indentUnit.Length].Equals(indentUnit, StringComparison.Ordinal))
        {
            return indentUnit.Length;
        }

        var whitespaceCount = 0;
        while (whitespaceCount < maxLength && (lineText[whitespaceCount] == ' ' || lineText[whitespaceCount] == '\t'))
            whitespaceCount++;

        return whitespaceCount > 0 ? 1 : 0;
    }

    private static List<AvaloniaEdit.Document.DocumentLine> GetSelectedLines(
        AvaloniaEdit.Document.TextDocument doc,
        int startOffset,
        int endOffset)
    {
        if (endOffset > startOffset)
            endOffset--;

        var lines = new List<AvaloniaEdit.Document.DocumentLine>();
        var line = doc.GetLineByOffset(startOffset);
        while (line is not null)
        {
            lines.Add(line);
            if (line.EndOffset >= endOffset || line.NextLine is null)
                break;

            line = line.NextLine;
        }

        return lines;
    }

    private async void AutoSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (!IsAutoSaveEnabled || !HasFileOpen || !_isDirty) return;
        try
        {
            await SaveAsync(allowPromptForPath: false);
        }
        catch (Exception ex)
        {
            _autoSaveStatusMessage = BuildAutoSaveFailureMessage(ex);
            OnPropertyChanged(nameof(FileSummaryText));
            OnPropertyChanged(nameof(AutoSaveStatusText));
            await ShowWarningDialogAsync("Auto-save", ex);
        }
    }

    private void AutoSaveStatusTimer_OnTick(object? sender, EventArgs e)
    {
        _autoSaveStatusTimer.Stop();
        ClearAutoSaveStatus();
    }

    private bool _isConfirmedClose;

    private async void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // If we already confirmed through the dialog loop, let it through.
        if (_isConfirmedClose) return;

        var dirtyTabs = OpenTabs.Where(t => t.IsDirty).ToList();
        if (dirtyTabs.Count == 0 || !IsConfirmBeforeClosingUnsavedTabsEnabled) return;

        // Cancel the close and handle it ourselves asynchronously.
        e.Cancel = true;

        foreach (var tab in dirtyTabs)
        {
            var action = await ShowUnsavedTabDialogAsync(tab);
            switch (action)
            {
                case UnsavedTabAction.Cancel:
                    return; // User aborted - leave the window open.

                case UnsavedTabAction.Save:
                    ActivateTab(tab, focusEditor: false);
                    if (!await SaveAsync(allowPromptForPath: true, forcePromptForPath: false))
                        return; // Save was cancelled - leave the window open.
                    break;

                // UnsavedTabAction.Discard - just continue to the next tab.
            }
        }

        // All dirty tabs resolved - close for real.
        _isConfirmedClose = true;
        Close();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        SaveSettings(immediate: true, synchronous: true);
        _autoSaveTimer.Stop();
        _autoSaveStatusTimer.Stop();
        _discordReconnectTimer.Stop();
        _extensionsRefreshDebounceTimer.Stop();
        _extensionAutoUpdateTimer.Stop();
        _appUpdateScheduler.Stop();
        _marketplaceRefreshTimer.Stop();
        _wordCountRefreshTimer.Stop();
        _settingsSaveDebounceTimer.Stop();
        _windowsAccentPollTimer.Stop();
        _windowsThemePollTimer.Stop();
        NetworkChange.NetworkAvailabilityChanged -= NetworkChange_OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkChange_OnNetworkAddressChanged;
        CloseAllTerminalSessions();
        DisposeExtensionFolderWatchers();
        DisposeProjectFolderWatcher();
        DisposeDiscordPresence();
        CurrentImagePreview = null;
    }

    // Runs on the UI thread every 2 s; re-applies the accent only when the
    // registry value has actually changed, so there's no unnecessary work.
    private void WindowsAccentPollTimer_OnTick(object? sender, EventArgs e)
    {
        var current = GetWindowsAccentColor() ?? string.Empty;
        if (current == _lastSeenWindowsAccentHex) return;
        _lastSeenWindowsAccentHex = current;
        ApplyAccentOverride();
        if (_accentColorMode == "windows")
        {
            ApplyThemeToEditor();
            RefreshExtensionTheme();
        }
    }

    // Runs every 2s; refreshes the System Default preview on a Windows theme change.
    private void WindowsThemePollTimer_OnTick(object? sender, EventArgs e)
    {
        var current = ResolveSystemThemeName();
        if (current == _lastSeenWindowsThemeName) return;
        _lastSeenWindowsThemeName = current;
        RefreshSystemThemePreview();
        if (IsSystemThemeActive)
            ApplyTheme("System");
    }

    private void NetworkChange_OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Dispatcher.UIThread.Post(() => RefreshMarketplaceConnectivityState());

    private void NetworkChange_OnNetworkAddressChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => RefreshMarketplaceConnectivityState());

    private static bool HasActiveWirelessConnection() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(networkInterface =>
            networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
            networkInterface.OperationalStatus == OperationalStatus.Up &&
            networkInterface.GetIPProperties().UnicastAddresses.Any(address => !System.Net.IPAddress.IsLoopback(address.Address)));

    private static bool HasActiveInternetConnection() =>
        NetworkInterface.GetIsNetworkAvailable() &&
        NetworkInterface.GetAllNetworkInterfaces().Any(networkInterface =>
            networkInterface.OperationalStatus == OperationalStatus.Up &&
            networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
            networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Tunnel);

    // Recognizes GitHub's 403/429 responses and swaps in a clearer message.
    private static bool IsGitHubRateLimitException(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests };

    private static string DescribeFetchFailure(Exception exception) =>
        IsGitHubRateLimitException(exception)
            ? "GitHub's API rate limit was hit. Wait a few minutes, then try again."
            : exception.Message;

    private void RefreshMarketplaceConnectivityState(string? operation = null, Exception? exception = null)
    {
        var hasWirelessConnection = HasActiveWirelessConnection();
        var hasInternetConnection = HasActiveInternetConnection();

        string message = string.Empty;

        if (!hasInternetConnection)
        {
            message = hasWirelessConnection
                ? "No internet connection. Marketplace installs and updates won't work until you're back online."
                : "No Wi-Fi or internet detected. Marketplace installs and updates won't work until you're back online.";
        }
        else if (exception is not null)
        {
            // Shows a message for any exception when online, not just connectivity failures.
            message = IsGitHubRateLimitException(exception)
                ? "GitHub's API rate limit was hit. Marketplace refreshes will resume once it resets."
                : hasWirelessConnection
                    ? "Couldn't reach the marketplace. Your connection may be unstable - try again in a moment."
                    : "No Wi-Fi detected. If you're expecting a connection, reconnect first. Marketplace downloads may fail while offline.";
        }

        if (!string.IsNullOrWhiteSpace(operation) && !string.IsNullOrWhiteSpace(message))
            message = $"{message} (Last issue: {operation})";

        MarketplaceConnectivityMessage = message;
        IsMarketplaceConnectivityWarningVisible = !string.IsNullOrWhiteSpace(message);
    }

    private TutorialStep CurrentTutorialStep => TutorialSteps[TutorialStepIndex];

    private void OnTutorialStepChanged()
    {
        OnPropertyChanged(nameof(TutorialStepIndex));
        OnPropertyChanged(nameof(TutorialStepLabel));
        OnPropertyChanged(nameof(TutorialProgressDotsText));
        OnPropertyChanged(nameof(TutorialSectionTitle));
        OnPropertyChanged(nameof(TutorialTitle));
        OnPropertyChanged(nameof(TutorialBody));
        OnPropertyChanged(nameof(TutorialShortcutText));
        OnPropertyChanged(nameof(TutorialSpotlightTitle));
        OnPropertyChanged(nameof(TutorialHighlightOne));
        OnPropertyChanged(nameof(TutorialHighlightTwo));
        OnPropertyChanged(nameof(TutorialHighlightThree));
        OnPropertyChanged(nameof(CanGoToPreviousTutorialStep));
        OnPropertyChanged(nameof(TutorialPrimaryButtonText));
        OnPropertyChanged(nameof(IsTutorialSetupStep));
        OnPropertyChanged(nameof(IsNotTutorialSetupStep));
        OnPropertyChanged(nameof(IsTutorialWelcomeStep));
        OnPropertyChanged(nameof(IsNotTutorialWelcomeStep));
        OnPropertyChanged(nameof(IsTutorialHeaderVisible));
    }

    private void CompleteTutorialAndReturnHome()
    {
        _hasCompletedTutorial = true;
        SaveSettings();
        NavigateTo(Page.Home);
    }

    // Gates every path that can end the tutorial until consent is answered and the Privacy
    // Policy has been accepted.
    private bool TryFinishTutorial()
    {
        if (IsDataTrackingPromptVisible || IsPrivacyPolicyPromptVisible)
        {
            TutorialStepIndex = TutorialSteps.Length - 1;
            return false;
        }

        CompleteTutorialAndReturnHome();
        return true;
    }

    private void PreviousTutorialStepButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TutorialStepIndex > 0)
            TutorialStepIndex--;
    }

    private void NextTutorialStepButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TutorialStepIndex < TutorialSteps.Length - 1)
        {
            TutorialStepIndex++;
            return;
        }

        TryFinishTutorial();
    }

    private void SkipTutorialButton_OnClick(object? sender, RoutedEventArgs e) =>
        TryFinishTutorial();

    private async void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Doesn't swallow keys when the terminal is focused; also stops Escape stealing focus.
        if (IsTerminalVisible && ActiveTerminalSession is not null)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            if (focused is not null &&
                (ReferenceEquals(focused, TerminalHostControl) ||
                 focused.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, TerminalHostControl))))
                return;
        }

        var hasControl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;

        // Escape - dismiss the search panel / Settings / Extensions / Tutorial / WhatsNew and return to editor
        if (MatchesKeybind(e, "CloseOverlay"))
        {
            if (IsSearchPanelVisible)
            {
                IsSearchPanelVisible = false;
                FocusEditor();
                e.Handled = true;
            }
            else if (IsTutorialPageVisible)
            {
                TryFinishTutorial();
                e.Handled = true;
            }
            else if (IsSettingsPageVisible || IsExtensionsPageVisible || IsWhatsNewPageVisible)
            {
                NavigateTo(Page.Editor);
                FocusEditor();
                e.Handled = true;
            }
            return;
        }

        // Rebindable commands (Settings -> Help -> View Shortcuts) are checked against
        // the user's current gesture for each id, rather than a fixed Key/modifier switch,
        // so a remapped shortcut actually takes effect here. Every entry in
        // KeybindDefinitions is covered by a branch below - nothing left hardcoded.
        if (MatchesKeybind(e, "NewFile"))
        {
            NewFile();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "GoEditor"))
        {
            NavigateTo(Page.Editor);
            FocusEditor();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "OpenExtensions") || MatchesKeybind(e, "OpenExtensionsAlt"))
        {
            NavigateTo(Page.Extensions);
            RefreshMarketplaceConnectivityState();
            _ = RefreshExtensionsDataAsync();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "OpenSettings"))
        {
            NavigateTo(Page.Settings);
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "GoHome"))
        {
            NavigateTo(Page.Home);
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "SaveAs"))
        {
            e.Handled = true;
            await SaveAsAsync();
        }
        else if (MatchesKeybind(e, "Save"))
        {
            e.Handled = true;
            await SaveAsync();
        }
        else if (MatchesKeybind(e, "OpenFile"))
        {
            e.Handled = true;
            await OpenFileAsync();
        }
        else if (MatchesKeybind(e, "CloseFolder"))
        {
            e.Handled = true;
            if (IsFolderOpen)
                CloseFolder();
        }
        else if (MatchesKeybind(e, "OpenFolder"))
        {
            e.Handled = true;
            await OpenFolderAsync();
        }
        else if (MatchesKeybind(e, "ToggleFileExplorer"))
        {
            IsFileExplorerVisible = !IsFileExplorerVisible;
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "ToggleTerminal") || MatchesKeybind(e, "ToggleTerminalAlt"))
        {
            ToggleTerminalPanel();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "NewTerminalSession"))
        {
            CreateTerminalSession();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "CloseTab"))
        {
            if (ActiveEditorTab is not null)
                await RequestCloseTabAsync(ActiveEditorTab);
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "FindInProject"))
        {
            OpenSearchPanel(SearchMode.ProjectSearch);
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "FindInFile"))
        {
            OpenSearchPanel(SearchMode.FindInFile);
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "Cut"))
        {
            e.Handled = true;
            await CutEditorSelectionAsync();
        }
        else if (MatchesKeybind(e, "Copy"))
        {
            e.Handled = true;
            await CopyEditorSelectionAsync();
        }
        else if (MatchesKeybind(e, "Paste"))
        {
            e.Handled = true;
            await PasteIntoEditorAsync();
        }
        // Image zoom: the numpad +/-/0 keys always work as a hardware fallback alongside
        // whichever gesture ZoomIn/ZoomOut/ZoomReset are currently bound to.
        else if (MatchesKeybind(e, "ZoomIn") || (hasControl && e.Key == Key.Add))
        {
            if (HasImagePreview)
            {
                ZoomImageIn();
                e.Handled = true;
            }
        }
        else if (MatchesKeybind(e, "ZoomOut") || (hasControl && e.Key == Key.Subtract))
        {
            if (HasImagePreview)
            {
                ZoomImageOut();
                e.Handled = true;
            }
        }
        else if (MatchesKeybind(e, "ZoomReset") || (hasControl && e.Key == Key.NumPad0))
        {
            if (HasImagePreview)
            {
                ZoomImageReset();
                e.Handled = true;
            }
        }
    }

    private async Task CutEditorSelectionAsync()
    {
        if (EditorTextBox?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var text = sel.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
        sel.ReplaceSelectionWithText(string.Empty);
    }

    private async Task CopyEditorSelectionAsync()
    {
        if (EditorTextBox?.TextArea?.Selection is not { } sel) return;
        var text = sel.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private async Task PasteIntoEditorAsync()
    {
        if (EditorTextBox?.TextArea is not { } textArea) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            textArea.Selection.ReplaceSelectionWithText(text);
    }

    // Nested types

    private static int NormalizeTabSize(int value) => value is 2 or 4 or 8 ? value : 4;

    private async Task<UnsavedTabAction> ShowUnsavedTabDialogAsync(EditorTab tab)
    {
        var result = UnsavedTabAction.Cancel;
        Window? dialog = null;
        dialog = new Window
        {
            Width = 420,
            Height = 190,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Unsaved Changes",
            Background = CardBrush,
            Content = BuildUnsavedTabDialogContent(
                tab,
                () => { result = UnsavedTabAction.Save; dialog!.Close(); },
                () => { result = UnsavedTabAction.Discard; dialog!.Close(); },
                () => { result = UnsavedTabAction.Cancel; dialog!.Close(); })
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private Control BuildUnsavedTabDialogContent(
        EditorTab tab,
        Action saveAction,
        Action discardAction,
        Action cancelAction)
    {
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush, cancelAction),
                CreateDialogButton("Discard", ButtonHoverBrush, SurfaceBorderBrush, PrimaryTextBrush, discardAction),
                CreateDialogButton("Save", AccentBrush, AccentBrush, AccentForegroundBrush, saveAction)
            }
        };

        return new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Save changes before closing?",
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = PrimaryTextBrush
                    },
                    new TextBlock
                    {
                        Text = $"{tab.DisplayName} has unsaved changes.",
                        Foreground = MutedTextBrush,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Choose Save to keep them, Discard to close without saving, or Cancel to keep editing.",
                        Foreground = MutedTextBrush,
                        TextWrapping = TextWrapping.Wrap
                    },
                    buttonRow
                }
            }
        };
    }

    private static Button CreateDialogButton(
        string text,
        IBrush background,
        IBrush borderBrush,
        IBrush foreground,
        Action clickAction)
    {
        var button = new Button
        {
            Content = text,
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(8),
            MinWidth = 86,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        button.Click += (_, _) => clickAction();
        return button;
    }


    // Shown once when settings.json doesn't exist; HasCompletedTutorial is then persisted so later launches skip it.

    private Task ShowTutorialAsync()
    {
        try
        {
            _tutorialOpenedFromSettings = false;
            TutorialStepIndex = 0;
            if (!_hasAcceptedPrivacyPolicy)
                ResetPrivacyPolicyScrollState();
            NavigateTo(Page.Tutorial);
        }
        catch
        {
            // Tutorial failure must never crash the app.
        }

        return Task.CompletedTask;
    }

    // Shown when a recent file/folder path is unreachable at open time - not an error, since the entry may simply be offline.
    // Kept in recents so it reappears once available; offers an explicit "Remove from recents" button.
    private async Task ShowNotFoundDialogAsync(string path, bool isFolder)
    {
        try
        {
            var kind = isFolder ? "Folder" : "File";

            var titleText = new TextBlock
            {
                Text         = $"{kind} Not Found",
                FontSize     = 16,
                FontWeight   = FontWeight.SemiBold,
                Foreground   = PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var bodyText = new TextBlock
            {
                Text         = $"This {kind.ToLowerInvariant()} couldn't be opened because it isn't currently accessible. " +
                               $"It may be on a drive that isn't connected, or it may have been moved or deleted.\n\n{path}",
                FontSize     = 13,
                Foreground   = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var removeButton = new Button
            {
                Content             = "Remove from Recents",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(16, 8),
                Background          = ButtonBrush,
                Foreground          = MutedTextBrush,
                BorderBrush         = SurfaceBorderBrush,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(8),
            };

            var dismissButton = new Button
            {
                Content             = "OK",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding             = new Thickness(28, 8),
                Background          = AccentBrush,
                Foreground          = AccentForegroundBrush,
                BorderThickness     = new Thickness(0),
                CornerRadius        = new CornerRadius(8),
            };

            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            buttonRow.Children.Add(removeButton);
            Grid.SetColumn(dismissButton, 1);
            buttonRow.Children.Add(dismissButton);

            var content = new StackPanel
            {
                Spacing  = 12,
                Margin   = new Thickness(20),
                Children = { titleText, bodyText, buttonRow },
            };

            Window? dialog = null;
            dialog = new Window
            {
                Title                 = "Kodo - Not Found",
                Width                 = 480,
                SizeToContent         = SizeToContent.Height,
                MinWidth              = 360,
                MaxHeight             = 400,
                CanResize             = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background            = CardBrush,
                Content               = content,
            };

            removeButton.Click += (_, _) => { RemoveRecentFile(path); dialog!.Close(); };
            dismissButton.Click += (_, _) => dialog!.Close();
            await dialog.ShowDialog(this);
        }
        catch (Exception dialogEx)
        {
            KodoDiagnostics.LogDebug("ShowNotFoundDialogAsync failed to display.", dialogEx);
        }
    }

    // Generic "are you sure?" prompt for destructive-but-recoverable actions.
    private async Task<bool> ShowConfirmationDialogAsync(
        string title,
        string body,
        string confirmLabel = "Confirm",
        string cancelLabel = "Cancel",
        bool isDestructive = false)
    {
        try
        {
            var titleText = new TextBlock
            {
                Text         = title,
                FontSize     = 16,
                FontWeight   = FontWeight.SemiBold,
                Foreground   = PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var bodyText = new TextBlock
            {
                Text         = body,
                FontSize     = 13,
                Foreground   = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0),
            };

            var cancelButton = new Button
            {
                Content             = cancelLabel,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(16, 8),
                Background          = ButtonBrush,
                Foreground          = MutedTextBrush,
                BorderBrush         = SurfaceBorderBrush,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(8),
            };

            var confirmButton = new Button
            {
                Content             = confirmLabel,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding             = new Thickness(20, 8),
                Background          = isDestructive ? new SolidColorBrush(Color.Parse("#C4302B")) : AccentBrush,
                Foreground          = AccentForegroundBrush,
                BorderThickness     = new Thickness(0),
                CornerRadius        = new CornerRadius(8),
            };

            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            buttonRow.Children.Add(cancelButton);
            Grid.SetColumn(confirmButton, 1);
            buttonRow.Children.Add(confirmButton);

            var content = new StackPanel
            {
                Spacing  = 12,
                Margin   = new Thickness(20),
                Children = { titleText, bodyText, buttonRow },
            };

            Window? dialog = null;
            dialog = new Window
            {
                Title                 = "Kodo",
                Width                 = 420,
                SizeToContent         = SizeToContent.Height,
                MinWidth              = 340,
                MaxHeight             = 320,
                CanResize             = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background            = CardBrush,
                Content               = content,
            };

            var result = false;
            cancelButton.Click  += (_, _) => { result = false; dialog!.Close(); };
            confirmButton.Click += (_, _) => { result = true;  dialog!.Close(); };
            await dialog.ShowDialog(this);
            return result;
        }
        catch (Exception dialogEx)
        {
            KodoDiagnostics.LogDebug($"ShowConfirmationDialogAsync failed to display for '{title}'.", dialogEx);
            // If the dialog itself fails to render, fail safe by not
            // performing the (potentially destructive) action it was gating.
            return false;
        }
    }

    // Two-tier warning dialog: Critical shows an amber banner, non-critical is softer.
    private async Task ShowWarningDialogAsync(string context, Exception exception, bool isCritical = false)
    {
        // Classify automatically: file-save and auto-save failures always
        // get the critical tier since unsaved data may be at risk.
        isCritical = isCritical
            || context.StartsWith("File save", StringComparison.OrdinalIgnoreCase)
            || context.StartsWith("Auto-save", StringComparison.OrdinalIgnoreCase);

        var source = isCritical ? "MainWindow.Warning.Critical" : "MainWindow.Warning";
        KodoDiagnostics.LogWarning(source, exception, operation: context);

        if (ShouldSuppressWarningDialog(context, exception))
        {
            KodoDiagnostics.LogDebug($"Suppressed duplicate warning dialog for '{context}'.", exception);
            return;
        }

        try
        {
            var titleLabel   = isCritical ? "Action required" : "Something went wrong";
            var subtitleMessage = isCritical
                ? "Kodo could not complete this file operation. Your in-editor content is still intact - try saving again or use Save As to choose a different location."
                : "Kodo ran into a problem with this operation. No data was lost - you can try again.";
            var windowTitle  = isCritical ? "Kodo - Warning" : "Kodo - Notice";
            var logPath      = KodoDiagnostics.MainLogFilePath;

            // --- Header ---
            var titleText = new TextBlock
            {
                Text         = titleLabel,
                FontSize     = 16,
                FontWeight   = FontWeight.SemiBold,
                Foreground   = PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var subtitleText = new TextBlock
            {
                Text         = subtitleMessage,
                FontSize     = 13,
                Foreground   = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0),
            };

            // Amber banner - only shown for critical tier so the visual weight
            // matches the severity (mirrors the terminating-crash amber banner).
            var criticalBanner = new Border
            {
                IsVisible       = isCritical,
                Background      = new SolidColorBrush(Color.Parse("#2D1F00")),
                BorderBrush     = new SolidColorBrush(Color.Parse("#6B4800")),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 6),
                Child = new TextBlock
                {
                    Text        = "⚠ This operation affects file data. Check the log if the problem persists.",
                    FontSize    = 12,
                    Foreground  = new SolidColorBrush(Color.Parse("#FFA040")),
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            // Context badge (e.g. "File save", "Extension install - MyLang")
            var contextBadge = new Border
            {
                Background      = ButtonBrush,
                BorderBrush     = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text       = context,
                    FontSize   = 12,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                    Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                },
            };

            var metadataText = new SelectableTextBlock
            {
                Text         = KodoDiagnostics.BuildDiagnosticSummary(source, false, context),
                FontSize     = 11,
                FontFamily   = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                Foreground   = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            // Human-readable error message above the raw stack trace.
            var errorMessageText = new TextBlock
            {
                Text         = string.IsNullOrWhiteSpace(exception.Message)
                                   ? "An unexpected error occurred."
                                   : DescribeFetchFailure(exception),
                FontSize     = 13,
                Foreground   = PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            // Scrollable, selectable stack trace.
            var exceptionText = new SelectableTextBlock
            {
                Text         = KodoDiagnostics.BuildDiagnosticPayload(source, exception, false, KodoSeverity.Warning, context, redactPaths: true),
                FontSize     = 12,
                FontFamily   = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                Foreground   = new SolidColorBrush(Color.Parse("#CE9178")),
                TextWrapping = TextWrapping.Wrap,
            };

            var exceptionScroll = new ScrollViewer
            {
                Content  = exceptionText,
                MaxHeight = 200,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };

            var exceptionBorder = new Border
            {
                Background      = CardBrush,
                BorderBrush     = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(12),
                Child           = exceptionScroll,
            };

            var logPathText = new TextBlock
            {
                Text         = "Logged to: %AppData%\\Kodo\\kodo.log",
                FontSize     = 11,
                Foreground   = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            // --- Action buttons ---
            var copyButton = new Button
            {
                Content             = "Copy to Clipboard",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(16, 8),
                Background          = ButtonBrush,
                Foreground          = MutedTextBrush,
                BorderBrush         = SurfaceBorderBrush,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(8),
            };

            var dismissButton = new Button
            {
                Content             = "Dismiss",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding             = new Thickness(20, 8),
                Background          = AccentBrush,
                Foreground          = AccentForegroundBrush,
                BorderThickness     = new Thickness(0),
                CornerRadius        = new CornerRadius(8),
            };

            var reportButton = new Button
            {
                Content             = "Report on GitHub",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(16, 8),
                Background          = ButtonBrush,
                Foreground          = MutedTextBrush,
                BorderBrush         = SurfaceBorderBrush,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(8),
                Margin              = new Thickness(8, 0, 0, 0),
            };

            var leftButtons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children    = { copyButton, reportButton },
            };

            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            buttonRow.Children.Add(leftButtons);
            Grid.SetColumn(dismissButton, 1);
            buttonRow.Children.Add(dismissButton);

            var content = new StackPanel
            {
                Spacing  = 12,
                Margin   = new Thickness(20),
                Children =
                {
                    titleText,
                    subtitleText,
                    criticalBanner,
                    contextBadge,
                    metadataText,
                    errorMessageText,
                    exceptionBorder,
                    logPathText,
                    buttonRow,
                },
            };

            Window? dialog = null;
            dialog = new Window
            {
                Title         = windowTitle,
                Width         = 520,
                SizeToContent = SizeToContent.Height,
                MinWidth      = 380,
                MinHeight     = 180,
                MaxHeight     = 660,
                CanResize     = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background    = CardBrush,
                Content       = content,
            };

            copyButton.Click += async (_, _) =>
            {
                try
                {
                    var clip = TopLevel.GetTopLevel(dialog)?.Clipboard;
                    if (clip is not null)
                    {
                var text = KodoDiagnostics.BuildDiagnosticPayload(source, exception, false, KodoSeverity.Warning, context, redactPaths: true);
                await clip.SetTextAsync(text);
                        copyButton.Content   = "Copied!";
                        copyButton.Foreground = PrimaryTextBrush;
                    }
                }
                catch
                {
                    // Clipboard failures must not crash the error dialog.
                }
            };

            reportButton.Click += (_, _) =>
            {
                try
                {
                    // Pre-fill a GitHub issue with the context as the title, mirroring the crash dialog.
                    var title = Uri.EscapeDataString($"[Warning] {context}: {exception.Message}"
                        .Replace("\r", "").Replace("\n", " ").Trim());
                    var body = Uri.EscapeDataString(KodoDiagnostics.BuildDiagnosticPayload(source, exception, false, KodoSeverity.Warning, context, redactPaths: true));
                    var url = $"https://github.com/Kodo-IDE/Kodo/issues/new?title={title}&body={body}&labels=bug&template=bug_report.md";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    // Opening the browser must not crash the warning dialog.
                }
            };

            dismissButton.Click += (_, _) => dialog!.Close();
            await dialog.ShowDialog(this);
        }
        catch (Exception dialogEx)
        {
            KodoDiagnostics.LogWarning(source, dialogEx, operation: $"Warning dialog failed to display for context '{context}'");
            KodoDiagnostics.LogDebug($"ShowWarningDialogAsync failed to display for context '{context}'.", dialogEx);
        }
    }

    // Runs factory with a timeout, throwing a named TimeoutException on expiry.
    private static async Task<T> RunWithGitHubTimeoutAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> factory)
    {
        using var cts = new CancellationTokenSource(GitHubOperationTimeout);
        try
        {
            return await factory(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Re-raise as TimeoutException so callers can distinguish a
            // deliberate 7-second timeout from a user-initiated cancellation.
            throw new TimeoutException(
                $"GitHub operation '{operationName}' did not complete within " +
                $"{GitHubOperationTimeout.TotalSeconds:0} seconds and was cancelled.");
        }
    }

    // Overload for operations that return no value.
    private static async Task RunWithGitHubTimeoutAsync(
        string operationName,
        Func<CancellationToken, Task> factory)
    {
        await RunWithGitHubTimeoutAsync<bool>(
            operationName,
            async ct => { await factory(ct).ConfigureAwait(false); return true; })
            .ConfigureAwait(false);
    }

    private bool ShouldSuppressWarningDialog(string context, Exception exception)
    {
        var key = $"{context}|{exception.GetType().FullName}|{exception.Message}";
        var now = DateTime.UtcNow;
        if (_warningDialogCooldowns.TryGetValue(key, out var lastShownUtc) &&
            now - lastShownUtc < WarningDialogCooldown)
        {
            return true;
        }

        _warningDialogCooldowns[key] = now;
        return false;
    }

    // AppSettings and RecentFileEntry moved to Models/AppSettings.cs - both are pure
    // data (the persisted-settings schema), with no dependency on MainWindow itself.

    private sealed record ExtensionScanResult(
        List<LoadedExtension> Extensions,
        List<string> LoadErrors);

    private enum UnsavedTabAction
    {
        Save,
        Discard,
        Cancel
    }

    private sealed record TutorialStep(
        string SectionTitle,
        string Title,
        string Body,
        string Shortcut,
        string SpotlightTitle,
        string HighlightOne,
        string HighlightTwo,
        string HighlightThree);
}



public sealed class IndentGuideBackgroundRenderer : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public int TabSize { get; set; } = 4;

    // Disabled for unsaved (untitled) files and plain-text files (.txt / .log / .text)
    // so that smart visual features don't activate where no language context exists.
    public bool IsEnabled { get; set; } = true;

    public IBrush GuideBrush { get; set; } = new SolidColorBrush(Color.Parse("#808080"), 0.4);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!IsEnabled)
            return;

        if (!textView.VisualLinesValid || TabSize <= 0)
            return;

        var spaceWidth = textView.WideSpaceWidth;
        if (spaceWidth <= 0)
            return;

        var document = textView.Document;
        if (document is null || document.LineCount == 0)
            return;

        if (!textView.VisualLines.Any())
            return;

        // Pre-compute indent depth (in tab-stop levels) for every document line.
        var totalLines = document.LineCount;
        var lineDepths = new int[totalLines + 1]; // 1-based index

        for (var i = 1; i <= totalLines; i++)
        {
            var docLine = document.GetLineByNumber(i);
            var text    = document.GetText(docLine);
            lineDepths[i] = string.IsNullOrWhiteSpace(text) ? -1 : GetIndentColumns(text) / TabSize;
        }

        // Fill blank lines from surrounding context so guides are continuous
        for (var i = 1; i <= totalLines; i++)
        {
            if (lineDepths[i] != -1) continue;
            var above = 0;
            for (var a = i - 1; a >= 1; a--)
                if (lineDepths[a] >= 0) { above = lineDepths[a]; break; }
            var below = 0;
            for (var b = i + 1; b <= totalLines; b++)
                if (lineDepths[b] >= 0) { below = lineDepths[b]; break; }
            lineDepths[i] = Math.Min(above, below);
        }

        // Measures true text-start X, subtracting ScrollOffset.
        var scrollX = textView.ScrollOffset.X;
        var scrollY = textView.ScrollOffset.Y;

        var refLine = textView.VisualLines[0].FirstDocumentLine;
        var originX = textView.GetVisualPosition(
            new AvaloniaEdit.TextViewPosition(refLine.LineNumber, 1),
            VisualYPosition.LineTop).X - scrollX;

        // Dashed pen matching VS Code-style indent guides
        var dashStyle = new DashStyle([2, 2], 0);
        var pen = new Pen(GuideBrush, 1, dashStyle);

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            var depth = lineDepths[lineNumber];
            if (depth <= 0) continue;

            // VisualTop is document-absolute; subtract scrollY for screen coords
            var top    = visualLine.VisualTop - scrollY;
            var bottom = top + visualLine.Height;

            for (var level = 1; level <= depth; level++)
            {
                // Each guide sits at the first character of its indent level: column TabSize, 2*TabSize, etc.
                var x = originX + (level * TabSize - 1) * spaceWidth;
                if (x < 0 || x > textView.Bounds.Width) continue;

                drawingContext.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            }
        }
    }

    private int GetIndentColumns(string lineText)
    {
        var columns = 0;
        foreach (var ch in lineText)
        {
            if      (ch == ' ')  columns++;
            else if (ch == '\t') columns += TabSize - (columns % TabSize);
            else break;
        }
        return columns;
    }
}

/// <summary>
/// Highlights all search matches in the editor when Find-in-file is active.
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

// Replaces the default LinkElementGenerator: genuine URLs only, Ctrl+click to open.
public sealed class StrictLinkElementGenerator : LinkElementGenerator
{
    private static readonly char[] TrailingPunctuation = [')', ']', '}', '.', ',', ':', ';', '!', '?', '\'', '"'];
    private const string HttpPrefix = "http";

    private int _cachedLineNumber = -1;
    private string _cachedLineText = string.Empty;
    private List<(int Start, int Length)> _cachedSpans = [];

    public StrictLinkElementGenerator()
    {
        RequireControlModifierForClick = true;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.VisualLine;
        var document = CurrentContext.Document;
        var lineNumber = line.FirstDocumentLine.LineNumber;
        var lineText = document.GetText(line.FirstDocumentLine.Offset, line.FirstDocumentLine.Length);

        if (lineNumber != _cachedLineNumber || !string.Equals(lineText, _cachedLineText, StringComparison.Ordinal))
        {
            _cachedLineNumber = lineNumber;
            _cachedLineText = lineText;
            _cachedSpans = ParseUrlSpans(lineText);
        }

        var relativeOffset = offset - line.FirstDocumentLine.Offset;
        foreach (var span in _cachedSpans)
        {
            if (relativeOffset != span.Start)
                continue;

            var url = lineText.Substring(span.Start, span.Length).TrimEnd(TrailingPunctuation);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var linkText = new VisualLineLinkText(line, url.Length);
            linkText.NavigateUri = uri;
            linkText.RequireControlModifierForClick = RequireControlModifierForClick;
            return linkText;
        }

        return null;
    }

    internal static bool TryGetLinkSpan(string lineText, int columnOffset, out int start, out int length)
    {
        foreach (var span in ParseUrlSpans(lineText))
        {
            if (columnOffset < span.Start || columnOffset >= span.Start + span.Length)
                continue;

            start = span.Start;
            length = span.Length;
            return true;
        }

        start = 0;
        length = 0;
        return false;
    }

    private static List<(int Start, int Length)> ParseUrlSpans(string lineText)
    {
        var spans = new List<(int Start, int Length)>();
        if (string.IsNullOrWhiteSpace(lineText))
            return spans;

        var index = 0;
        while (index < lineText.Length)
        {
            var httpIndex = lineText.IndexOf(HttpPrefix, index, StringComparison.OrdinalIgnoreCase);
            if (httpIndex < 0)
                break;

            if (httpIndex > 0 && IsUrlChar(lineText[httpIndex - 1]))
            {
                index = httpIndex + 4;
                continue;
            }

            var end = httpIndex;
            while (end < lineText.Length && IsUrlChar(lineText[end]))
                end++;

            var url = lineText[httpIndex..end].TrimEnd(TrailingPunctuation);
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                spans.Add((httpIndex, url.Length));

            index = Math.Max(end, httpIndex + 4);
        }

        return spans;
    }

    private static bool IsUrlChar(char ch) =>
        !char.IsWhiteSpace(ch) &&
        ch is not '<' and not '>' and not '"' and not '\'' and not '[' and not ']' and not '(' and not ')' and not '{' and not '}' and not '|' and not '\\' and not '^' and not '`';
}

// Converts bold flag to FontWeight for the release-notes run template.
public sealed class BoolToFontWeightConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? FontWeight.SemiBold : FontWeight.Regular;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MarketplaceTileWidthConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly MarketplaceTileWidthConverter Instance = new();

    private const int    Columns           = 5;
    private const double HorizontalPadding = 48; // ScrollViewer Padding="24" on each side
    private const double ScrollbarGutter   = 20; // room for the vertical scrollbar when visible
    private const double TileMargin        = 12; // Border.extensiontile Margin="6" on each side
    private const double MinTileWidth      = 120; // floor so tiles never collapse to nothing

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not double width) return MinTileWidth;

        var available = width - HorizontalPadding - ScrollbarGutter;
        var perColumn = available / Columns - TileMargin;
        return Math.Max(MinTileWidth, perColumn);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}