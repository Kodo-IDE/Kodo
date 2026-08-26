// Licensed under GPL-v3.0
using System;
using System.Collections.Concurrent;
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

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MaxRecentFiles = 6;
    private const string DefaultDiscordClientId = "1495509170756255744";
    private const string DefaultDiscordLargeImageKey = "kodo_logo";
    private const string DefaultDiscordLargeImageText = "Kodo";
    private const string SettingsFileName = "kodosettings.json";
    private const string DiscordClientIdEnvironmentVariable = "KODO_DISCORD_CLIENT_ID";
    private const string AutoSaveSavedMessage = "Saved.";
    private const string AutoSaveSavingMessage = "Saving...";
    private const string AutoSaveFailedMessagePrefix = "Save failed:";
    private static readonly string CurrentAppVersion = KodoDiagnostics.AppVersion;
    public string CopyrightText => $"© {DateTime.Now.Year} Kodo, built by KerbalMissile and SS-YYC. Licensed under the GNU GPL-v3.0.";
    private static readonly string[] MarketplaceIndexUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/ExtensionsIndex.json",
    ];
    private static readonly string[] CompilerIndexUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/CompilerIndex.json",
    ];
    private static readonly string[] PluginsIndexUrls =
    [
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/PluginsIndex.json",
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
    private static readonly string AnnouncementsUrl = "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Announcements/ANNOUNCEMENTS.md";
    private static readonly string NewsCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kodo",
        "news-cache.json");
    private static readonly string LatestReleaseCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kodo",
        "whats-new-cache.json");

    private bool _isNewsLoading = true;
    private bool _isNewsError;
    private bool _isPerformanceModeEnabled;
    private bool _newsDisabled;
    private bool _whatsNewDisabled;
    // Performance: debounce search filtering until the user stops typing.
    private bool _isDebouncedSearchEnabled;
    private bool _extensionSearchPending;
    private bool _settingsSearchPending;

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
    private readonly DispatcherTimer _InsightRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _diagnosticPopupHideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _settingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly object _settingsWriteLock = new();
    private AppSettings? _pendingSettingsSnapshot;
    private bool _isPersistingSettings;
    private readonly DispatcherTimer _windowsAccentPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastSeenWindowsAccentHex = string.Empty;
    private readonly DispatcherTimer _windowsThemePollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastSeenWindowsThemeName = string.Empty;
    private readonly RainbowBracketColorizer _rainbowBracketColorizer = new();
    private readonly InterpolatedStringColorizer _interpolatedStringColorizer = new();
    private readonly HtmlEmbeddedColorizer _htmlEmbeddedColorizer = new();
    private readonly MarkdownColorizer _markdownColorizer = new();
    private readonly EmojiTypefaceColorizer _emojiTypefaceColorizer = new();
    private readonly InsightEngine _InsightEngine = new();
    private CompletionWindow? _completionWindow;
    private readonly DeadCodeHighlightRenderer _deadCodeHighlightRenderer = new();
    private readonly DeadCodeTextBrightener _deadCodeTextBrightener = new();
    private readonly ErrorLineHighlightRenderer _errorHighlightRenderer = new();
    private readonly ErrorTextDarkener _errorTextDarkener = new();
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
    private bool _isAutoUpdatingExtensions;
    private bool _isAutoUpdateExtensionsEnabled;
    private bool _isAutoUpdateExtensionsInBackgroundEnabled;
    private bool _isAutoUpdateAppEnabled = true;
    private bool _isAutoUpdateAppInBackgroundEnabled;
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
    private bool _openingSplashShowsReleaseNotes;
    private string _lastSeenVersion = string.Empty;
    private bool _isHomePageVisible;
    private bool _isFileExplorerVisible;
    private bool _isFileTreeExpanded;
    private bool _isStatusBarFilePathVisible = true;
    private bool _isWordWrapEnabled;
    // Defaults to true - predictive completion is on unless the user turns it off.
    private bool _isInsightEnabled = true;
    private bool _isInsightCodeSuggestionsEnabled = true;
    private bool _isInsightDeadCodeEnabled = true;
    private bool _isInsightErrorDetectionEnabled = true;
    private string _insightBlacklistExtensions = ".txt,.md";
    private HashSet<string> _insightBlacklistSet = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };
    private HashSet<string> _dismissedDiagnostics = new(StringComparer.Ordinal);
    private bool _suppressExplorerWidthRefresh;
    private bool _isConfirmBeforeClosingUnsavedTabsEnabled = true;
    private bool _isRestoreOpenTabsOnLaunchEnabled;
    private string _selectedExtensionsTab = ExtensionsTabModes.Installed;
    private bool _suppressDirtyTracking;
    private bool _suppressSettingsSave;
    private bool _isDeveloperOptionsVisible;
    private bool _isVerboseLoggingEnabled;
    private int _tabSize = 4;
    private int _editorFontSize = 14;
    private string _accentColorMode = "kodo";   // "kodo" | "windows" | "custom"
    private string _customAccentHex = "#8C00FF";
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
    private bool _isFirstLaunch;
    private bool _hasCompletedTutorial;
    private bool _isDataTrackingEnabled;
    private bool _hasRespondedToDataTrackingPrompt;
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

    private FileSystemWatcher? _projectFolderWatcher;
    private readonly DispatcherTimer _fileTreeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _extensionsRefreshDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _extensionAutoUpdateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly AppUpdateScheduler _appUpdateScheduler;
    private readonly DispatcherTimer _marketplaceRefreshTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly IndentGuideBackgroundRenderer _indentGuideRenderer = new();
    private readonly List<string> _startupOpenTabPaths = [];
    private string? _startupFolderPath;
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _marketplaceIconBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _marketplaceSvgCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _warningDialogCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconFetchSemaphore = new(4, 4);
    private static readonly TimeSpan ExtensionsRefreshCooldown = TimeSpan.FromSeconds(8);
    private string MarketplaceIndexCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "marketplace-index.json");
    private string MarketplaceIndexETagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "marketplace-index.json.etag");
    private string? _marketplaceIndexETag;
    private string CompilerIndexCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "compiler-index.json");
    private string CompilerIndexETagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "compiler-index.json.etag");
    private string? _compilerIndexETag;
    private string PluginsIndexCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "plugins-index.json");
    private string PluginsIndexETagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "plugins-index.json.etag");
    private string? _pluginsIndexETag;
    private List<CompilerIndexEntry> _compilerIndexEntries = [];
    private List<MarketplaceExtension> _pluginsIndexEntries = [];
    private string CompilerInstallRegistryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "installed-compilers.json");
    private string CompilersFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "Compilers");
    private string ManualCompilersRegistryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "manual-compilers.json");
    private Dictionary<string, ManualCompilerRecord> _manualCompilers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _autoDetectDismissedIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasRunCompilerAutoDetect;
    private Dictionary<string, InstalledCompilerRecord> _installedCompilers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan WarningDialogCooldown   = TimeSpan.FromSeconds(3);

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
    private readonly DispatcherTimer _searchFilterDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
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
    private string _searchIncludeFilter = string.Empty;
    private string _searchExcludeFilter = string.Empty;
    private bool _isFilterRowVisible;
    private bool _isFileCorrupted;
    private bool _isPointerOverEditorLink;
    private string? _hoveredDeadCodeReason;
    private string? _hoveredErrorReason;
    private readonly HashSet<EditorTab> _corruptedTabs = new(ReferenceEqualityComparer.Instance);
    private TerminalSession? _activeTerminalSession;
    private EventHandler<IntPtr>? _activeSessionExitedHandler;
    private TerminalShellOption? _selectedTerminalShell;
    private bool _isTerminalVisible;
    private bool _isTerminalSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private bool _isPSReadLinePredictionEnabled;
    private double _terminalPanelHeight = AppSettings.DefaultTerminalPanelHeight;
    private bool _isResizingTerminalPanel;
    private double _terminalPanelDragStartPointerY;
    private double _terminalPanelDragStartHeight;

    private double _explorerPanelWidth = AppSettings.DefaultExplorerPanelWidth;
    private bool _isResizingExplorerPanel;
    private double _explorerPanelDragStartPointerX;
    private double _explorerPanelDragStartWidth;

    private readonly Dictionary<LoadedExtension, KodoHighlightingDefinition> _highlightingCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LoadedExtension, CompiledSyntaxProfile> _compiledSyntaxProfileCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, LoadedExtension?> _contentSniffCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ColorSwatchElementGenerator _colorSwatchGenerator = new();

    private string _findText = string.Empty;
    private string _replaceText = string.Empty;
    private bool _isSearchMatchCaseEnabled;
    private bool _isSearchWholeWordEnabled;
    private bool _isSearchRegexEnabled;
    private int _tutorialStepIndex;

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

    public ObservableCollection<FileTreeItem> FileTreeItems { get; } = new();
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();
    public ObservableCollection<EditorTab> OpenTabs { get; } = new();
    public ObservableCollection<TerminalSession> TerminalSessions { get; } = new();
    public ObservableCollection<LoadedExtension> LoadedExtensions { get; } = new();
    public ObservableCollection<MarketplaceExtension> MarketplaceExtensions { get; } = new();
    public ObservableCollection<MarketplaceExtension> CompilerExtensions { get; } = new();
    public ObservableCollection<MarketplaceExtension> ManualCompilerExtensions { get; } = new();
    public ObservableCollection<string> ExtensionLoadErrors { get; } = new();
    public ObservableCollection<TerminalShellOption> AvailableTerminalShells { get; } = new();
    public ObservableCollection<NewsItem> NewsItems { get; } = new();

    public bool IsNewsLoading
    {
        get => _isNewsLoading;
        private set { if (_isNewsLoading == value) return; _isNewsLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNewsContentVisible)); OnPropertyChanged(nameof(IsNewsEmpty)); OnPropertyChanged(nameof(IsNewsRefreshEnabled)); }
    }

    public bool IsNewsError
    {
        get => _isNewsError;
        private set { if (_isNewsError == value) return; _isNewsError = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNewsContentVisible)); OnPropertyChanged(nameof(IsNewsEmpty)); }
    }

    public bool IsNewsDisabled => IsPerformanceModeEnabled && _newsDisabled;
    public bool IsNewsContentVisible => !IsNewsDisabled && !IsNewsLoading && !IsNewsError && NewsItems.Count > 0;
    public bool IsNewsEmpty => !IsNewsDisabled && !IsNewsLoading && !IsNewsError && NewsItems.Count == 0;
    public bool IsNewsRefreshEnabled => !IsNewsDisabled && !IsNewsLoading;

    public bool IsWhatsNewDisabled => IsPerformanceModeEnabled && _whatsNewDisabled;
    public bool IsWhatsNewRefreshEnabled => !IsWhatsNewDisabled && !IsRefreshingLatestRelease;
    public bool IsLatestReleaseStatusVisible => !IsWhatsNewDisabled && !HasLatestRelease;

    public bool NewsDisabled
    {
        get => _newsDisabled;
        set
        {
            if (_newsDisabled == value) return;
            _newsDisabled = value;
            OnPropertyChanged();
            RaiseMany(nameof(IsNewsDisabled), nameof(IsNewsContentVisible), nameof(IsNewsEmpty), nameof(IsNewsRefreshEnabled));
            SaveSettings();
            if (IsPerformanceModeEnabled)
            {
                if (value)
                {
                    NewsItems.Clear();
                    IsNewsLoading = false;
                    IsNewsError = false;
                }
                else
                {
                    _ = FetchAnnouncementsAsync(forceNetwork: false);
                }
            }
        }
    }

    public bool WhatsNewDisabled
    {
        get => _whatsNewDisabled;
        set
        {
            if (_whatsNewDisabled == value) return;
            _whatsNewDisabled = value;
            OnPropertyChanged();
            RaiseMany(nameof(IsWhatsNewDisabled), nameof(IsWhatsNewRefreshEnabled), nameof(IsLatestReleaseStatusVisible));
            SaveSettings();
            if (IsPerformanceModeEnabled)
            {
                if (value)
                {
                    LatestRelease = null;
                    LatestReleaseStatusText = "Disabled by Performance mode.";
                }
                else
                {
                    _ = RefreshLatestReleaseAsync(forceNetwork: false);
                }
            }
        }
    }

    public bool IsPerformanceModeEnabled
    {
        get => _isPerformanceModeEnabled;
        set
        {
            if (_isPerformanceModeEnabled == value) return;
            _isPerformanceModeEnabled = value;
            OnPropertyChanged();
            RaiseMany(nameof(IsNewsDisabled), nameof(IsNewsContentVisible), nameof(IsNewsEmpty), nameof(IsNewsRefreshEnabled), nameof(IsWhatsNewDisabled), nameof(IsWhatsNewRefreshEnabled), nameof(IsLatestReleaseStatusVisible), nameof(IsDebouncedSearchActive));
            SaveSettings();
            if (value)
            {
                if (_newsDisabled)
                {
                    NewsItems.Clear();
                    IsNewsLoading = false;
                    IsNewsError = false;
                }
                if (_whatsNewDisabled)
                {
                    LatestRelease = null;
                    LatestReleaseStatusText = "Disabled by Performance mode.";
                }
            }
            else
            {
                if (_newsDisabled)
                    _ = FetchAnnouncementsAsync(forceNetwork: false);
                if (_whatsNewDisabled)
                    _ = RefreshLatestReleaseAsync(forceNetwork: false);
                FlushPendingSearchFilters();
            }
        }
    }

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
            RaiseMany(nameof(HasImagePreview), nameof(IsImagePreviewVisible), nameof(IsTextEditorVisible), nameof(ImageZoomedWidth), nameof(ImageZoomedHeight), nameof(ImageZoomPercent));
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
            RaiseMany(nameof(ImageZoomPercent), nameof(ImageZoomedWidth), nameof(ImageZoomedHeight));
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
        EditorTextBox.TextArea.TextView.VisualLinesChanged += (_, _) =>
        {
            var textView = EditorTextBox.TextArea.TextView;
            dottedLineMargin.Height = Math.Min(textView.DocumentHeight, textView.Bounds.Height);
        };
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_indentGuideRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_deadCodeHighlightRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_errorHighlightRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_findHighlightRenderer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_rainbowBracketColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_interpolatedStringColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_htmlEmbeddedColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_markdownColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_emojiTypefaceColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_deadCodeTextBrightener);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_errorTextDarkener);
        EditorTextBox.TextArea.TextView.LinkTextForegroundBrush = Brush.Parse("#5BA3D9");
        EditorTextBox.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;
        var defaultLinkGen = EditorTextBox.TextArea.TextView.ElementGenerators.OfType<LinkElementGenerator>().FirstOrDefault();
        if (defaultLinkGen is not null)
            EditorTextBox.TextArea.TextView.ElementGenerators.Remove(defaultLinkGen);
        EditorTextBox.TextArea.TextView.ElementGenerators.Add(new StrictLinkElementGenerator());
        EditorTextBox.TextArea.TextView.ElementGenerators.Add(_colorSwatchGenerator);
        EditorTextBox.TextArea.TextView.PointerMoved += EditorTextView_OnPointerMoved;
        EditorTextBox.TextArea.TextView.PointerEntered += (_, _) => _diagnosticPopupHideTimer.Stop();
        EditorTextBox.TextArea.TextView.PointerExited += EditorTextView_OnPointerExited;
        EditorTextBox.TextArea.TextView.PointerPressed += (_, _) => HideDiagnosticPopup();
        EditorTextBox.TextArea.TextView.PointerWheelChanged += (_, _) => HideDiagnosticPopup();
        EditorTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => HideDiagnosticPopup(), RoutingStrategies.Bubble);
        OpenTabs.CollectionChanged += OpenTabs_CollectionChanged;
        TerminalSessions.CollectionChanged += TerminalSessions_CollectionChanged;
        TerminalHostControl.WorkingDirectoryChanged += TerminalHostControl_OnWorkingDirectoryChanged;
        FileTreeItems.CollectionChanged += FileTreeItems_CollectionChanged;
        _fileTreeRefreshTimer.Tick += FileTreeRefreshTimer_OnTick;
        _searchDebounceTimer.Tick += SearchDebounceTimer_OnTick;
        _searchFilterDebounceTimer.Tick += SearchFilterDebounceTimer_OnTick;
        // TextEditor uses EventHandler (not RoutedEventHandler), so hook up in code-behind
        EditorTextBox.TextChanged += EditorTextBox_OnTextChanged;
        EditorTextBox.TextArea.Caret.PositionChanged += (_, _) => { HideDiagnosticPopup(); QueueRefreshState(); };
        EditorTextBox.TextArea.GotFocus += (_, _) => QueueInsightRefresh();
        Activated += (_, _) => QueueInsightRefresh();
        EditorTextBox.TextArea.TextEntering += EditorTextArea_OnTextEntering;
        EditorTextBox.TextArea.TextEntered  += EditorTextArea_OnTextEntered;
        AddHandler(InputElement.KeyDownEvent, MainWindow_EditorKeyIntercept_OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
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
        _isInsightCodeSuggestionsEnabled = settings.InsightCodeSuggestionsEnabled;
        _isInsightDeadCodeEnabled = settings.InsightDeadCodeEnabled;
        _isInsightErrorDetectionEnabled = settings.InsightErrorDetectionEnabled;
        _insightBlacklistExtensions = string.IsNullOrWhiteSpace(settings.InsightBlacklistExtensions) ? ".txt,.md" : settings.InsightBlacklistExtensions;
        RebuildInsightBlacklist();
        _dismissedDiagnostics = new HashSet<string>(settings.DismissedDiagnostics ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        _isConfirmBeforeClosingUnsavedTabsEnabled = settings.ConfirmBeforeClosingUnsavedTabsEnabled;
        _isRestoreOpenTabsOnLaunchEnabled = settings.RestoreOpenTabsOnLaunchEnabled;
        _isAutoUpdateExtensionsEnabled = settings.AutoUpdateExtensionsEnabled;
        _isAutoUpdateExtensionsInBackgroundEnabled = settings.AutoUpdateExtensionsInBackgroundEnabled;
        _isAutoUpdateAppEnabled = settings.AutoUpdateAppEnabled;
        _isAutoUpdateAppInBackgroundEnabled = settings.AutoUpdateAppInBackgroundEnabled;
        _isPerformanceModeEnabled = settings.PerformanceModeEnabled;
        if (_isPerformanceModeEnabled)
            _latestReleaseStatusText = "Disabled by Performance mode.";
        _newsDisabled = settings.NewsDisabled;
        _whatsNewDisabled = settings.WhatsNewDisabled;
        _isDebouncedSearchEnabled = settings.DebouncedSearchEnabled;
        _hasCompletedTutorial = settings.HasCompletedTutorial;
        _isDataTrackingEnabled = settings.AllowDataTracking;
        _hasRespondedToDataTrackingPrompt = settings.HasRespondedToDataTrackingPrompt;
        _hasAcceptedPrivacyPolicy = settings.HasAcceptedPrivacyPolicy;
        AptabaseClient.SetEnabled(_isDataTrackingEnabled);
        _accentColorMode = settings.AccentColorMode is "kodo" or "windows" or "custom" or "theme"
            ? settings.AccentColorMode : "kodo";
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
        foreach (var pair in settings.CustomBuildScripts)
            _customBuildScripts[pair.Key] = pair.Value;
        RefreshAvailableTerminalShells(settings.PreferredTerminalShellId);
        _autoSaveTimer.Tick += AutoSaveTimer_OnTick;
        _autoSaveStatusTimer.Tick += AutoSaveStatusTimer_OnTick;
        _discordReconnectTimer.Tick += DiscordReconnectTimer_OnTick;
        _editorStateRefreshTimer.Tick += EditorStateRefreshTimer_OnTick;
        _wordCountRefreshTimer.Tick += WordCountRefreshTimer_OnTick;
        _InsightRefreshTimer.Tick += InsightRefreshTimer_OnTick;
        _diagnosticPopupHideTimer.Tick += DiagnosticPopupHideTimer_OnTick;
        _settingsSaveDebounceTimer.Tick += SettingsSaveDebounceTimer_OnTick;
        _extensionsRefreshDebounceTimer.Tick += ExtensionsRefreshDebounceTimer_OnTick;
        _extensionAutoUpdateTimer.Tick += ExtensionAutoUpdateTimer_OnTick;
        _appUpdateScheduler = new AppUpdateScheduler(
            isEnabled: () => IsAutoUpdateAppEnabled,
            isManualCheckInProgress: () => IsCheckingForUpdatesManually,
            installInBackground: () => IsAutoUpdateAppInBackgroundEnabled);
        _marketplaceRefreshTimer.Tick += MarketplaceRefreshTimer_OnTick;

        EnsureExtensionsFolder();
        SetupExtensionFolderWatchers();
        LoadExtensions();
        ApplyThemeBrushes(_requestedThemeName);

        // Migration: legacy "kodo" mode with an active theme is upgraded to "theme" mode.
        if (_accentColorMode == "kodo" && _hasThemeAccent)
            _accentColorMode = "theme";

        DataContext = this;
        IsHomePageVisible = true;

        UpdateDiscordRichPresenceLifecycle();
        UpdateExtensionAutoUpdateLifecycle();
        _appUpdateScheduler.UpdateLifecycle();
        _marketplaceRefreshTimer.Start();
        _ = RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
        ApplyEditorSettings();
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkChange_OnNetworkAddressChanged;
        RefreshMarketplaceConnectivityState();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _lastSeenWindowsAccentHex = GetWindowsAccentColor() ?? string.Empty;
            _windowsAccentPollTimer.Tick += WindowsAccentPollTimer_OnTick;
            _windowsAccentPollTimer.Start();

            _lastSeenWindowsThemeName = ResolveSystemThemeName();
            _windowsThemePollTimer.Tick += WindowsThemePollTimer_OnTick;
            _windowsThemePollTimer.Start();
        }
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        RefreshState(fullRefresh: true);
    }













































    private async Task RefreshLatestReleaseAsync(bool forceNetwork = false)
    {
        if (_isRefreshingLatestRelease || IsWhatsNewDisabled)
            return;

        _isRefreshingLatestRelease = true;
        RaiseMany(nameof(IsRefreshingLatestRelease), nameof(IsWhatsNewRefreshEnabled), nameof(RefreshLatestReleaseButtonText));
        LatestReleaseStatusText = "Loading latest release...";

        try
        {
            if (!forceNetwork && LoadCachedLatestRelease())
            {
                LatestReleaseStatusText = HasLatestRelease
                    ? $"Latest release: {LatestReleaseDisplayName}"
                    : "No releases found.";
                return;
            }

            LatestRelease = await FetchLatestReleaseInfoAsync();

            LatestReleaseStatusText = HasLatestRelease
                ? $"Latest release: {LatestReleaseDisplayName}"
                : "No releases found.";

            if (HasLatestRelease)
                SaveLatestReleaseCache();
        }
        catch (Exception ex)
        {
            LatestRelease = null;
            LatestReleaseStatusText = $"Could not load release info: {DescribeFetchFailure(ex)}";

            KodoDiagnostics.LogDebug("Failed to fetch latest release info", ex);
            await ShowWarningDialogAsync("Latest release info fetch", ex);
        }
        finally
        {
            _isRefreshingLatestRelease = false;
            RaiseMany(nameof(IsRefreshingLatestRelease), nameof(IsWhatsNewRefreshEnabled), nameof(RefreshLatestReleaseButtonText));
        }
    }

    private async Task FetchAnnouncementsAsync(bool forceNetwork)
    {
        if (IsNewsDisabled)
        {
            NewsItems.Clear();
            IsNewsLoading = false;
            IsNewsError = false;
            return;
        }

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
        }
    }

    private bool LoadCachedLatestRelease()
    {
        try
        {
            if (!File.Exists(LatestReleaseCachePath))
                return false;

            var json = File.ReadAllText(LatestReleaseCachePath);
            var cached = JsonSerializer.Deserialize<ReleaseInfo>(json);
            if (cached is null ||
                (string.IsNullOrWhiteSpace(cached.Name) &&
                 string.IsNullOrWhiteSpace(cached.Tag) &&
                 string.IsNullOrWhiteSpace(cached.Notes)))
                return false;

            LatestRelease = cached;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveLatestReleaseCache()
    {
        try
        {
            if (LatestRelease is null)
                return;

            var dir = Path.GetDirectoryName(LatestReleaseCachePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(LatestRelease);
            File.WriteAllText(LatestReleaseCachePath, json);
        }
        catch
        {
        }
    }

    private static List<NewsItem> ParseAnnouncementsMd(string md)
    {
        var items = new List<NewsItem>();
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


























    // Like SyncObservableCollection, but carries over the already-fetched icon bitmap.























    private static string BuildGitHubContentsUrl(string owner, string repo, string path) =>
        $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";



    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase);
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







    private static string NormalizeGitHubBlobViewerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

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


    private const string KodoExtensionsOwner = "Kodo-IDE";
    private const string KodoExtensionsRepo = "Kodo-Extensions";







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







    private static string ExtractVersionFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

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



    // Shallow-clones a LoadedExtension so each theme entry gets its own object


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











    private static bool IsPlainTextFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".text", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNoFileExtension(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return true;

        return Path.GetExtension(filePath).Length == 0;
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




    private static Color PushTowardExtreme(Color color, bool towardWhite, double amount)
    {
        if (towardWhite)
        {
            byte Boost(byte channel) => (byte)Math.Min(255, channel + (255 - channel) * amount);
            return Color.FromArgb(color.A, Boost(color.R), Boost(color.G), Boost(color.B));
        }

        byte Darken(byte channel) => (byte)Math.Max(0, channel - channel * amount);
        return Color.FromArgb(color.A, Darken(color.R), Darken(color.G), Darken(color.B));
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
        _markdownColorizer.TabSize = TabSize;
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

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



    private CompiledSyntaxProfile? ResolveFenceLanguageSyntaxProfile(string fenceLanguage)
    {
        var extension = ResolveFenceLanguageExtension(fenceLanguage);
        return extension is null ? null : ResolveCompiledSyntaxProfile(extension);
    }







    private void SetFileCorrupted(bool corrupted)
    {
        if (_isFileCorrupted == corrupted) return;
        _isFileCorrupted = corrupted;
        RaiseMany(nameof(IsCorruptedFileViewVisible), nameof(IsTextEditorVisible), nameof(CanShowFindInFile), nameof(CanShowSearchPanel), nameof(IsSearchPanelActive), nameof(CanShowSaveActions));
    }



    private void ConfigureInterpolatedStrings(CompiledSyntaxProfile? syntaxProfile)
    {
        _interpolatedStringColorizer.UpdateSyntax(syntaxProfile);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }


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
            RaiseMany(nameof(IsEmptyStateVisible), nameof(IsDocumentViewVisible), nameof(FileSummaryText), nameof(FilePathText), nameof(LanguageDisplayText), nameof(CanShowSaveActions));
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

    public bool IsUpdateSplashVisible
    {
        get => _isUpdateSplashVisible;
        private set
        {
            if (_isUpdateSplashVisible == value) return;
            _isUpdateSplashVisible = value;
            OnPropertyChanged();
            RaiseMany(nameof(IsReleaseNotesSectionVisible), nameof(OpeningSplashTitleText), nameof(OpeningSplashSubtitleText));
        }
    }

    public bool IsReleaseNotesSectionVisible => _isUpdateSplashVisible && _openingSplashShowsReleaseNotes;

    public string OpeningSplashTitleText => _openingSplashShowsReleaseNotes
        ? "What's New in Kodo"
        : "Help Improve Kodo";

    public string OpeningSplashSubtitleText => _openingSplashShowsReleaseNotes
        ? LatestReleaseDisplayName
        : "One quick question before you get back to it.";

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

    public bool IsPrivacyPolicyPromptVisible => !_hasAcceptedPrivacyPolicy;

    public bool AreAllConsentPromptsResolved => !IsDataTrackingPromptVisible && !IsPrivacyPolicyPromptVisible;

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

    private void ResetPrivacyPolicyScrollState() => IsPrivacyPolicyScrolledToBottom = false;

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











    internal const double FileTreeRowFixedOverhead = 16 + 2 + 32 + 6 + 8 + 10;





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

    public bool IsNoExtensionsVisible =>
        _selectedInstalledContentFilter != InstalledContentFilters.Compilers &&
        !VisibleLoadedExtensions.Any() && string.IsNullOrWhiteSpace(_extensionSearchText);

    public int InstalledExtensionsCount => VisibleLoadedExtensions.Count();
    private IEnumerable<MarketplaceExtension> GetDeduplicatedInstalledCompilerSourceNoFilter()
    {
        var managedIds = new HashSet<string>(_installedCompilers.Keys, StringComparer.OrdinalIgnoreCase);
        var installedCanonical = CompilerExtensions.Where(e => e.IsInstalled);
        var dedupedCanonical = installedCanonical.Where(c =>
        {
            if (managedIds.Contains(c.Id)) return true;
            var hasManualProxy = _manualCompilers.Values.Any(r =>
                string.Equals(r.CanonicalCompilerId, c.Id, StringComparison.OrdinalIgnoreCase));
            return !hasManualProxy;
        });
        var dedupedManuals = ManualCompilerExtensions.Where(m =>
        {
            if (!_manualCompilers.TryGetValue(m.Id, out var rec)) return true;
            var canonical = rec.CanonicalCompilerId;
            if (string.IsNullOrWhiteSpace(canonical)) return true;
            return !managedIds.Contains(canonical);
        });
        return dedupedCanonical.Concat(dedupedManuals);
    }

    public int InstalledCompilersCount => GetDeduplicatedInstalledCompilerSourceNoFilter().Count();
    public bool HasVisibleInstalledExtensions => FilteredInstalledExtensions.Any();
    public bool HasVisibleMarketplaceExtensions => FilteredMarketplaceExtensions.Any();
    public bool HasVisibleCompilerExtensions => FilteredCompilerExtensions.Any();
    public bool HasVisibleInstalledExtensionsOrCompilers =>
        HasVisibleInstalledExtensions || HasVisibleInstalledCompilerExtensions;
    public bool HasVisibleInstalledExtensionsAndCompilers =>
        HasVisibleInstalledExtensions && HasVisibleInstalledCompilerExtensions;

    public IEnumerable<LoadedExtension> ThemeExtensions =>
        LoadedExtensions.Where(e => e.Type.Equals("theme", StringComparison.OrdinalIgnoreCase) && e.ThemeDefinition is not null);

    public bool HasThemeExtensions => ThemeExtensions.Any();



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
            RaiseMany(nameof(RefreshExtensionsButtonText), nameof(CanUpdateAllExtensions), nameof(IsMarketplaceUnavailableVisible), nameof(IsMarketplacePartialErrorVisible), nameof(IsMarketplaceEmptyVisible));
        }
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

    public bool IsCompilersTabSelected
    {
        get => _selectedExtensionsTab == ExtensionsTabModes.Compilers;
        private set { if (value) SetSelectedExtensionsTab(ExtensionsTabModes.Compilers); }
    }

    public bool IsMarketplaceSectionTabSelected =>
        _selectedExtensionsTab is ExtensionsTabModes.Languages or ExtensionsTabModes.Themes or ExtensionsTabModes.Plugins;

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
            RaiseMany(nameof(HasLatestRelease), nameof(LatestReleaseDisplayName), nameof(LatestReleaseTag), nameof(LatestReleaseNotes), nameof(LatestReleaseFormatted), nameof(LatestReleasePreview), nameof(LatestReleaseUrl), nameof(LatestReleaseLinks), nameof(HasLatestReleaseLinks), nameof(IsNewerVersionAvailable), nameof(IsAppUpdateAvailable), nameof(IsLatestReleaseStatusVisible));
        }
    }

    public bool HasLatestRelease => LatestRelease is not null;

    public string CurrentAppVersionDisplay => CurrentAppVersion;

    private bool _updateBannerDismissed;
    private bool _extensionUpdateBannerDismissed;

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

        return VersionPriority(CurrentAppVersion) > VersionPriority(lastSeen);
    }

    public bool IsNewerVersionAvailable
    {
        get
        {
            if (IsDevBuild) return false;
            if (!HasLatestRelease || string.IsNullOrWhiteSpace(LatestReleaseTag)) return false;

            if (!Version.TryParse(StripPreRelease(CurrentAppVersion), out var current)) return false;
            if (!Version.TryParse(StripPreRelease(LatestReleaseTag),  out var latest))  return false;

            if (latest != current) return latest > current;

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

    private static readonly Regex MdInlineBoldRegex = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled | RegexOptions.Singleline);

    // Parses raw GitHub markdown into bold/normal runs for AXAML rendering.
    private static IReadOnlyList<FormattedParagraph> ParseMarkdownParagraphs(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var raw = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        raw = MdCodeFenceRegex.Replace(raw, string.Empty);
        raw = raw.Replace("```", string.Empty);

        var paragraphs = new List<FormattedParagraph>();

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (MdHrRegex.IsMatch(line)) continue;

            if (string.IsNullOrWhiteSpace(line)) continue;

            var isBullet   = false;
            var isOrdered  = false;
            string? orderedPrefix = null;

            var bulletMatch = MdBulletRegex.Match(line);
            if (bulletMatch.Success)
            {
                isBullet = true;
                line = line[bulletMatch.Length..];
            }
            else
            {
                var orderedMatch = MdOrderedListRegex.Match(line);
                if (orderedMatch.Success)
                {
                    isOrdered     = true;
                    orderedPrefix = orderedMatch.Groups[1].Value + ".";
                    line          = line[orderedMatch.Length..];
                }
                else
                {
                    var headingMatch = MdHeadingRegex.Match(line);
                    if (headingMatch.Success)
                        line = line[headingMatch.Length..].Trim();
                }
            }

            line = MdBlockquoteRegex.Replace(line, string.Empty);

            line = MdImageRegex.Replace(line, "$1");
            line = MdLinkRegex.Replace(line, "$1");

            line = MdInlineCodeRegex.Replace(line, "$1");

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

            var markerColumnWidth = isBullet ? 18.0
                : isOrdered ? 28.0
                : 0.0;

            var pos = 0;
            foreach (Match m in MdInlineBoldRegex.Matches(line))
            {
                if (m.Index > pos)
                    runs.Add(new FormattedRun { Text = line[pos..m.Index], IsBold = false });

                var boldText = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(boldText))
                    runs.Add(new FormattedRun { Text = boldText, IsBold = true });

                pos = m.Index + m.Length;
            }

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

    public bool IsInstalledSearchEmptyVisible =>
        _selectedInstalledContentFilter != InstalledContentFilters.Compilers &&
        !string.IsNullOrWhiteSpace(_extensionSearchText) &&
        VisibleLoadedExtensions.Any() &&
        !FilteredInstalledExtensions.Any();

    public bool IsMarketplaceSearchEmptyVisible =>
        !string.IsNullOrWhiteSpace(_extensionSearchText) &&
        MarketplaceExtensions.Any() &&
        !FilteredMarketplaceExtensions.Any();

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
            RaiseMany(nameof(IsMarketplaceUnavailableVisible), nameof(IsMarketplacePartialErrorVisible), nameof(IsMarketplaceEmptyVisible));
        }
    }

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

    public string TutorialHighlightTwo => (!IsAmericanEnglish && TutorialStepIndex == 4)
        ? CurrentTutorialStep.HighlightTwo.Replace("tab behavior", "tab behaviour")
        : CurrentTutorialStep.HighlightTwo;

    // Step 5 ("Set up") HighlightThree contains "accent colour" - swap for US users.
    public string TutorialHighlightThree => IsAmericanEnglish
        ? CurrentTutorialStep.HighlightThree.Replace("accent colour", "accent color")
        : CurrentTutorialStep.HighlightThree;

    public bool CanGoToPreviousTutorialStep => TutorialStepIndex > 0;

    public bool IsTutorialSetupStep => TutorialStepIndex == TutorialSteps.Length - 1;
    public bool IsNotTutorialSetupStep => !IsTutorialSetupStep;

    // True only on the very first "Welcome to Kodo!" splash step.
    public bool IsTutorialWelcomeStep => TutorialStepIndex == 0;
    public bool IsNotTutorialWelcomeStep => !IsTutorialWelcomeStep;

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

    public string MainLogFilePath => KodoDiagnostics.MainLogFilePath;

    public string CrashLogFilePath => KodoDiagnostics.CrashLogFilePath;

    public string CrashLogFolderPath => KodoDiagnostics.LogDirectoryPath;

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
            RaiseMany(nameof(FilePathText), nameof(LanguageDisplayText), nameof(StatusBarFilePathVisibilityText), nameof(ActiveTerminalWorkingDirectory), nameof(ActiveTerminalFooterText));
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
            {
                CloseCompletionWindow();
                ClearDeadCodeHighlighting();
                ClearErrorHighlighting();
            }
            SaveSettings();
        }
    }

    public bool IsInsightCodeSuggestionsEnabled
    {
        get => _isInsightCodeSuggestionsEnabled;
        set
        {
            if (_isInsightCodeSuggestionsEnabled == value) return;
            _isInsightCodeSuggestionsEnabled = value;
            OnPropertyChanged();
            if (!_isInsightCodeSuggestionsEnabled)
                CloseCompletionWindow();
            SaveSettings();
        }
    }

    public bool IsInsightDeadCodeEnabled
    {
        get => _isInsightDeadCodeEnabled;
        set
        {
            if (_isInsightDeadCodeEnabled == value) return;
            _isInsightDeadCodeEnabled = value;
            OnPropertyChanged();
            if (!_isInsightDeadCodeEnabled)
            {
                ClearDeadCodeHighlighting();
            }
            else
            {
                QueueInsightRefresh();
            }
            SaveSettings();
        }
    }

    public bool IsInsightErrorDetectionEnabled
    {
        get => _isInsightErrorDetectionEnabled;
        set
        {
            if (_isInsightErrorDetectionEnabled == value) return;
            _isInsightErrorDetectionEnabled = value;
            OnPropertyChanged();
            if (!_isInsightErrorDetectionEnabled)
            {
                ClearErrorHighlighting();
            }
            else
            {
                QueueInsightRefresh();
            }
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

    // Build Insight ignore list
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

    private string GetDiagnosticSignature(string? filePath, string lineText, string message) =>
        $"{filePath ?? string.Empty}|{lineText.Trim()}|{message}";

    public bool IsDiagnosticDismissed(string? filePath, string lineText, string message) =>
        _dismissedDiagnostics.Contains(GetDiagnosticSignature(filePath, lineText, message));

    public void DismissDiagnostic(string? filePath, string lineText, string message)
    {
        var sig = GetDiagnosticSignature(filePath, lineText, message);
        if (_dismissedDiagnostics.Add(sig))
        {
            SaveSettings();
            QueueInsightRefresh();
        }
    }

    public void ClearDismissedDiagnostics()
    {
        if (_dismissedDiagnostics.Count == 0) return;
        _dismissedDiagnostics.Clear();
        SaveSettings();
        QueueInsightRefresh();
    }

    public bool IsPSReadLinePredictionEnabled
    {
        get => _isPSReadLinePredictionEnabled;
        set
        {
            if (_isPSReadLinePredictionEnabled == value) return;
            _isPSReadLinePredictionEnabled = value;
            OnPropertyChanged();
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
            if (IsDebouncedSearchActive)
            {
                _extensionSearchPending = true;
                RestartSearchFilterDebounce();
            }
            else
            {
                NotifyExtensionFiltersChanged();
            }
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
            if (IsDebouncedSearchActive)
            {
                _settingsSearchPending = true;
                RestartSearchFilterDebounce();
            }
            else
            {
                NotifySettingsSearchChanged();
            }
        }
    }

    public bool IsDebouncedSearchActive => IsPerformanceModeEnabled && _isDebouncedSearchEnabled;

    public bool IsDebouncedSearchEnabled
    {
        get => _isDebouncedSearchEnabled;
        set
        {
            if (_isDebouncedSearchEnabled == value) return;
            _isDebouncedSearchEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDebouncedSearchActive));
            SaveSettings();
            if (!value)
                FlushPendingSearchFilters();
        }
    }









    private void SettingsPage_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var box = this.FindControl<TextBox>("SettingsSearchTextBox");
        if (box is null) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (!ReferenceEquals(focused, box)) return;
        // Don't steal focus if the click was inside the TextBox itself.
        if (e.Source is Visual v && (v == box || v.GetVisualAncestors().Contains(box)))
            return;
        var grid = this.FindControl<Grid>("SettingsPageGrid");
        if (grid is not null)
        {
            grid.Focusable = true;
            grid.Focus();
        }
        if (_searchFilterDebounceTimer.IsEnabled)
            _searchFilterDebounceTimer.Stop();
        if (_settingsSearchPending)
            FlushPendingSearchFilters();
    }







    private bool _isSettingsSearchEmpty;
    public bool IsSettingsSearchEmptyVisible => _isSettingsSearchEmpty;


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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (value) Task.Run(UpdateService.EnsureAutostartRegistered);
                else Task.Run(UpdateService.RemoveAutostartRegistration);
            }
        }
    }

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

            IEnumerable<LoadedExtension> source = VisibleLoadedExtensions;

            if (_selectedInstalledContentFilter == InstalledContentFilters.Languages)
                source = source.Where(e => string.Equals(e.Type, "language", StringComparison.OrdinalIgnoreCase));
            else if (_selectedInstalledContentFilter == InstalledContentFilters.Plugins)
                source = source.Where(e => string.Equals(e.Type, "plugin", StringComparison.OrdinalIgnoreCase));
            else if (_selectedInstalledContentFilter == InstalledContentFilters.Themes)
                source = source.Where(e => string.Equals(e.Type, "theme", StringComparison.OrdinalIgnoreCase));
            else if (_selectedInstalledContentFilter == InstalledContentFilters.Extensions)
                source = source.Where(e => !string.Equals(e.Type, "compiler", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(_extensionSearchText))
            {
                source = source.Where(e =>
                    e.Name.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.Description.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase));
            }

            return SortInstalledExtensions(source);
        }
    }

    public IEnumerable<LoadedExtension> FilteredInstalledLanguageExtensions => FilterInstalledExtensionsByType("language");
    public IEnumerable<LoadedExtension> FilteredInstalledPluginExtensions => FilterInstalledExtensionsByType("plugin");
    public IEnumerable<LoadedExtension> FilteredInstalledThemeExtensions => FilterInstalledExtensionsByType("theme");

    private IEnumerable<LoadedExtension> FilterInstalledExtensionsByType(string type)
    {
        if (_selectedInstalledContentFilter == InstalledContentFilters.Compilers)
            return [];

        // If a specific extension type filter is active, hide other types
        if (_selectedInstalledContentFilter == InstalledContentFilters.Languages && !string.Equals(type, "language", StringComparison.OrdinalIgnoreCase))
            return [];
        if (_selectedInstalledContentFilter == InstalledContentFilters.Plugins && !string.Equals(type, "plugin", StringComparison.OrdinalIgnoreCase))
            return [];
        if (_selectedInstalledContentFilter == InstalledContentFilters.Themes && !string.Equals(type, "theme", StringComparison.OrdinalIgnoreCase))
            return [];

        var source = VisibleLoadedExtensions.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_extensionSearchText))
        {
            source = source.Where(e =>
                e.Name.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(_extensionSearchText, StringComparison.OrdinalIgnoreCase));
        }

        return SortInstalledExtensions(source);
    }

    public bool HasVisibleInstalledLanguageExtensions => FilteredInstalledLanguageExtensions.Any();
    public bool HasVisibleInstalledPluginExtensions => FilteredInstalledPluginExtensions.Any();
    public bool HasVisibleInstalledThemeExtensions => FilteredInstalledThemeExtensions.Any();

    public bool IsInstalledLanguageDividerVisible => HasVisibleInstalledLanguageExtensions && (HasVisibleInstalledPluginExtensions || HasVisibleInstalledThemeExtensions || HasVisibleInstalledCompilerExtensions);
    public bool IsInstalledPluginDividerVisible => HasVisibleInstalledPluginExtensions && (HasVisibleInstalledThemeExtensions || HasVisibleInstalledCompilerExtensions);
    public bool IsInstalledThemeDividerVisible => HasVisibleInstalledThemeExtensions && HasVisibleInstalledCompilerExtensions;

    public IEnumerable<MarketplaceExtension> FilteredMarketplaceExtensions
    {
        get
        {
            IEnumerable<MarketplaceExtension> source = MarketplaceExtensions;

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

    public IReadOnlyList<string> InstalledContentFilterOptions { get; } =
    [
        InstalledContentFilters.All,
        InstalledContentFilters.Languages,
        InstalledContentFilters.Plugins,
        InstalledContentFilters.Themes,
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

    public IEnumerable<MarketplaceExtension> FilteredInstalledCompilerExtensions
    {
        get
        {
            // Only show compilers when filter is All or Compilers
            if (_selectedInstalledContentFilter == InstalledContentFilters.Languages ||
                _selectedInstalledContentFilter == InstalledContentFilters.Plugins ||
                _selectedInstalledContentFilter == InstalledContentFilters.Themes ||
                _selectedInstalledContentFilter == InstalledContentFilters.Extensions)
                return [];

            IEnumerable<MarketplaceExtension> source = GetDeduplicatedInstalledCompilerSourceNoFilter();

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
        !HasVisibleInstalledCompilerExtensions && (_selectedInstalledContentFilter == InstalledContentFilters.All || _selectedInstalledContentFilter == InstalledContentFilters.Compilers);

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

        var deltaY = _terminalPanelDragStartPointerY - e.GetPosition(this).Y;
        TerminalPanelHeight = _terminalPanelDragStartHeight + deltaY;
        e.Handled = true;
    }

    private void TerminalPanelSplitter_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingTerminalPanel) return;

        _isResizingTerminalPanel = false;
        e.Pointer.Capture(null);
        SaveSettings(immediate: true);
        e.Handled = true;
    }

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
            OnPropertyChanged(nameof(ConfirmBeforeClosingStatusText));
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
            OnPropertyChanged(nameof(ConfirmBeforeClosingStatusText));
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
            RaiseMany(nameof(ThemeStatusText), nameof(IsDarkThemeActive), nameof(IsLightThemeActive), nameof(IsSystemThemeActive));
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

    private const double ExplorerHeaderFixedChrome = 12 + 12 + 3 + 6 + 4 * 28;

    private static readonly Typeface ExplorerHeaderTypeface = new("Segoe UI", weight: FontWeight.SemiBold);

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
            RaiseMany(nameof(IsAmericanEnglish), nameof(LabelAccentColour), nameof(TooltipAccentTheme), nameof(TooltipAccentWindows), nameof(TooltipAccentCustom), nameof(LabelPersonalization), nameof(LabelPersonalizationDescription), nameof(TutorialSpotlightTitle), nameof(TutorialBody), nameof(TutorialHighlightOne), nameof(TutorialHighlightThree));
            SaveSettings();
        }
    }



    public bool IsAmericanEnglish => _userCountry == "US";

    public string LabelAccentColour        => IsAmericanEnglish ? "Accent Color"      : "Accent Colour";
    public string TooltipAccentTheme       => IsAmericanEnglish ? "Use the accent color preset by the active theme" : "Use the accent colour preset by the active theme";
    public string TooltipAccentWindows     => IsAmericanEnglish ? "Use your Windows system accent color" : "Use your Windows system accent colour";
    public string TooltipAccentCustom      => IsAmericanEnglish ? "Choose a custom accent color" : "Choose a custom accent colour";
    public string LabelPersonalization     => IsAmericanEnglish ? "Personalization"   : "Personalisation";
    public string LabelPersonalizationDescription => IsAmericanEnglish
        ? "These settings personalize the welcome message on the Home screen. Your name is used in greetings when set. Country is auto-detected from your system if left blank. Hemisphere and time zone are also auto-detected when possible."
        : "These settings personalise the welcome message on the Home screen. Your name is used in greetings when set. Country is auto-detected from your system if left blank. Hemisphere and time zone are also auto-detected when possible.";



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




    private static string DetectCountryCode()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion;
            return region.TwoLetterISORegionName.ToUpperInvariant();
        }
        catch { return string.Empty; }
    }


    private string[]? _welcomeMessagesCache;

    private string? _selectedWelcomeMessage;

    private readonly bool _isTaglineGreeting = Random.Shared.Next(10_000) == 0;
    public bool IsTaglineGreeting => _isTaglineGreeting;

    private readonly bool _isRareWordmarkVariant = Random.Shared.Next(1_000_000) == 0;
    public string KodoWordmarkText => _isRareWordmarkVariant ? "KODE-O" : "KODO";


    private static readonly DateTime _kodoBirthDate = new(2026, 4, 18);



    public bool IsKodoBirthday
    {
        get
        {
            var today = DateTime.Now;
            return today.Month == _kodoBirthDate.Month && today.Day == _kodoBirthDate.Day;
        }
    }

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
                KodoBirthdayAge);
            _selectedWelcomeMessage ??= _welcomeMessagesCache[Random.Shared.Next(_welcomeMessagesCache.Length)];
            return _selectedWelcomeMessage;
        }
    }

    public bool IsDarkThemeActive  => !IsSystemThemeActive && string.Equals(CurrentThemeName, "Dark",  StringComparison.OrdinalIgnoreCase);
    public bool IsLightThemeActive => !IsSystemThemeActive && string.Equals(CurrentThemeName, "Light", StringComparison.OrdinalIgnoreCase);

    // True when the user picked "follow Windows"; tracked off _requestedThemeName.
    public bool IsSystemThemeActive => string.Equals(_requestedThemeName, "System", StringComparison.OrdinalIgnoreCase);

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
            ? "Discord Rich Presence with detailed status is on when the Discord desktop app is running."
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

    public string ConfirmBeforeClosingStatusText => IsConfirmBeforeClosingUnsavedTabsEnabled
        ? "Shows Save, Discard and Cancel when you try to close a tab with unsaved changes."
        : "Unsaved tabs close immediately without asking - any unsaved changes will be lost.";

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

    public IBrush AccentForegroundBrush { get; private set; } = Brushes.White;







    private static Color DarkenColor(Color c, double amount)
    {
        byte Adjust(byte ch) => (byte)Math.Clamp(ch * (1 - amount), 0, 255);
        return Color.FromArgb(c.A, Adjust(c.R), Adjust(c.G), Adjust(c.B));
    }

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
            RaiseMany(nameof(IsAccentKodo), nameof(IsAccentWindows), nameof(IsAccentCustom), nameof(IsAccentTheme));
        }
    }
    public bool IsAccentKodo    => _accentColorMode == "kodo";
    public bool IsAccentWindows => _accentColorMode == "windows";
    public bool IsAccentCustom  => _accentColorMode == "custom";
    public bool HasThemeAccent  => _hasThemeAccent;
    public bool IsAccentTheme   => _accentColorMode == "theme";
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
        RaiseMany(nameof(HasOpenEditors), nameof(HasMultipleOpenEditors), nameof(IsEditorTabsVisible));
        SaveSettings();
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

        RaiseMany(nameof(HasActiveTerminal), nameof(HasTerminalSessions), nameof(TerminalSessionCount), nameof(TerminalStatusBarText), nameof(ActiveTerminalFooterText));
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
        RaiseMany(nameof(ActiveTerminalWorkingDirectory), nameof(ActiveTerminalStatusText), nameof(ActiveTerminalFooterText), nameof(TerminalStatusBarText));
    }


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
        RaiseMany(nameof(HasDocumentOpen), nameof(IsDocumentViewVisible), nameof(HasImagePreview), nameof(IsImagePreviewVisible), nameof(IsTextEditorVisible), nameof(CanShowFindInFile), nameof(CanShowSearchPanel), nameof(IsSearchPanelActive), nameof(CanShowSaveActions), nameof(IsWordCountVisible), nameof(HasFileOpen), nameof(IsFolderOpen), nameof(IsEmptyStateVisible), nameof(HasRecentFiles), nameof(FileSummaryText), nameof(FilePathText), nameof(ExplorerHeaderText), nameof(ExplorerHeaderTooltipText), nameof(ExplorerPanelMinWidth), nameof(DiscordRichPresenceStatusText), nameof(AutoSaveStatusText), nameof(LanguageDisplayText), nameof(EncodingDisplayText), nameof(ActiveTerminalWorkingDirectory), nameof(ActiveTerminalFooterText), nameof(TerminalStatusBarText));
        UpdateDiscordPresence();
    }

    private void QueueRefreshState(bool fullRefresh = false)
    {
        _pendingFullStateRefresh |= fullRefresh;
        _editorStateRefreshTimer.Stop();
        _editorStateRefreshTimer.Start();
    }



    private void QueueWordCountRefresh()
    {
        _wordCountRefreshTimer.Stop();
        _wordCountRefreshTimer.Start();
    }









    private string GetDocumentStatusSuffix()
    {
        var text = GetDocumentStatusText();
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $" • {text}";
    }




    private void LoadWindowIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Kodo/Assets/kodo-logo.png"));
        Icon = new WindowIcon(iconStream);
    }


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

        TerminalHostControl.Keybinds = _keybinds;
    }

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

            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

            if (json.Contains("\"CodePredictEnabled\"", StringComparison.Ordinal))
                json = json.Replace("\"CodePredictEnabled\"", "\"InsightEnabled\"", StringComparison.Ordinal);

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
            InsightCodeSuggestionsEnabled         = IsInsightCodeSuggestionsEnabled,
            InsightDeadCodeEnabled                = IsInsightDeadCodeEnabled,
            InsightErrorDetectionEnabled          = IsInsightErrorDetectionEnabled,
            InsightBlacklistExtensions           = InsightBlacklistExtensions,
            DismissedDiagnostics                 = new HashSet<string>(_dismissedDiagnostics, StringComparer.Ordinal),
            TabSize                                = TabSize,
            EditorFontSize                         = EditorFontSize,
            ConfirmBeforeClosingUnsavedTabsEnabled  = IsConfirmBeforeClosingUnsavedTabsEnabled,
            RestoreOpenTabsOnLaunchEnabled          = IsRestoreOpenTabsOnLaunchEnabled,
            AutoUpdateExtensionsEnabled             = IsAutoUpdateExtensionsEnabled,
            AutoUpdateExtensionsInBackgroundEnabled = IsAutoUpdateExtensionsInBackgroundEnabled,
            AutoUpdateAppEnabled                    = IsAutoUpdateAppEnabled,
            AutoUpdateAppInBackgroundEnabled         = IsAutoUpdateAppInBackgroundEnabled,
            PerformanceModeEnabled                   = IsPerformanceModeEnabled,
            NewsDisabled                             = NewsDisabled,
            WhatsNewDisabled                         = WhatsNewDisabled,
            DebouncedSearchEnabled                   = IsDebouncedSearchEnabled,
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
            CustomBuildScripts = new Dictionary<string, string>(_customBuildScripts, StringComparer.OrdinalIgnoreCase),
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


















    // Keeps the System Default preview live regardless of the active theme mode.




























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
            _suppressSettingsSave = false;

            // Only forces a write if a settings file already existed.
            if (!_isFirstLaunch)
            {
                SaveSettings(immediate: true);
            }
        }

        _ = RefreshExtensionsAndAutoUpdateAsync();
        _ = RefreshLatestReleaseAsync(forceNetwork: false);
        _ = FetchAnnouncementsAsync(forceNetwork: false);

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
        NavigateTo(AppPage.Editor);

        var tab = CreateUntitledTab();
        OpenTabs.Add(tab);
        ActivateTab(tab);
    }



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










    private void ProjectFolderWatcher_OnError(object sender, ErrorEventArgs e) =>
        Dispatcher.UIThread.Post(RestartFileTreeRefreshTimer);















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


    private string GetDocumentDisplayName() =>
        HasFileOpen ? Path.GetFileName(_currentFilePath!) : "untitled.txt";

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
        EditorTextBox.TextArea.ClearSelection();
        EditorTextBox.TextArea.Caret.Offset = 0;
        Dispatcher.UIThread.Post(
            () => _suppressDirtyTracking = false,
            DispatcherPriority.Background);
        QueueInsightRefresh();
    }


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
        NavigateTo(AppPage.Editor);

        if (ensureVisible)
            IsTerminalVisible = true;
        else
            IsTerminalVisible = !IsTerminalVisible;

        if (IsTerminalVisible && ActiveTerminalSession is not null)
            FocusActiveTerminal();
    }

    private void CreateTerminalSession(TerminalShellOption? shell = null, TerminalSession? replaceExisting = null, string? workingDirectoryOverride = null)
    {
        if (!IsTerminalSupported)
            return;

        shell ??= GetSelectedTerminalShellOrFallback();
        if (shell is null)
            return;

        var workingDirectory = workingDirectoryOverride is not null && Directory.Exists(workingDirectoryOverride)
            ? workingDirectoryOverride
            : ResolveTerminalWorkingDirectory();
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


    private void NavigateTo(AppPage page)
    {
        var newHome       = page == AppPage.Home;
        var newSettings   = page == AppPage.Settings;
        var newExtensions = page == AppPage.Extensions;
        var newTutorial   = page == AppPage.Tutorial;
        var newWhatsNew   = page == AppPage.WhatsNew;

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

        if (_isUpdateSplashVisible)
            IsUpdateSplashVisible = false;

        RaiseMany(nameof(IsHomePageVisible), nameof(IsSettingsPageVisible), nameof(IsExtensionsPageVisible), nameof(IsTutorialPageVisible), nameof(IsWhatsNewPageVisible), nameof(IsEditorPageVisible), nameof(IsSearchPanelActive), nameof(IsEditorTabsVisible), nameof(IsDocumentViewVisible), nameof(IsEmptyStateVisible), nameof(CanShowSaveActions), nameof(FileSummaryText), nameof(FilePathText));
        RefreshState(fullRefresh: true);
    }

    private void EditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Editor);
        FocusEditor();
    }

    private void HomeButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Home);

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

    private void CopyTerminalSessionWorkingDirectoryMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<TerminalSession>(sender) is not { } session) return;
        if (string.IsNullOrWhiteSpace(session.WorkingDirectory)) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(session.WorkingDirectory);
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



    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Settings);





    private void InstalledTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsInstalledTabSelected = true;

    private void LanguagesTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenMarketplaceTab(ExtensionsTabModes.Languages);



    private void PluginsTabButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenMarketplaceTab(ExtensionsTabModes.Plugins);





    private void RefreshNewsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsNewsDisabled) return;
        _ = FetchAnnouncementsAsync(forceNetwork: true);
    }

    private void OpenTutorialButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _tutorialOpenedFromSettings = true;
        TutorialStepIndex = 0;
        NavigateTo(AppPage.Tutorial);
    }

    private void OpenWhatsNewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.WhatsNew);
        IsWhatsNewExpanded = true;
        _ = RefreshLatestReleaseAsync(forceNetwork: false);
    }

    private void DismissUpdateSplashButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsUpdateSplashVisible = false;

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

    private void AcceptPrivacyPolicyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _hasAcceptedPrivacyPolicy = true;
        OnPropertyChanged(nameof(IsPrivacyPolicyPromptVisible));
        OnPropertyChanged(nameof(AreAllConsentPromptsResolved));
        if (IsUpdateSplashVisible && !IsDataTrackingPromptVisible) IsUpdateSplashVisible = false;
        SaveSettings();
    }

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
        NavigateTo(AppPage.Editor);
        FocusEditor();
    }

    private async void RefreshLatestReleaseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsWhatsNewDisabled) return;
        await RefreshLatestReleaseAsync(forceNetwork: true);
    }

    private void ToggleWhatsNewExpandedButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsWhatsNewExpanded = !IsWhatsNewExpanded;

    private void DismissUpdateBanner_OnClick(object? sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = true;
        OnPropertyChanged(nameof(IsAppUpdateAvailable));
    }







    private async void CheckForUpdatesButton_OnClick(object? sender, RoutedEventArgs e) =>
        await CheckForUpdatesManuallyAsync();

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
            var update = await UpdateService.CheckAndHandleUpdateAsync(installInBackground: false);
            if (update is null)
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
        var fixedShortcuts = new (string Gesture, string Description)[]
        {
            ("Enter / F3",         "Next search match (in terminal)"),
            ("Shift+Enter / Shift+F3", "Previous search match (in terminal)"),
        };

        // Polished header - accent badge + title, hint underneath, divider
        var iconBadge = new Border
        {
            Background        = AccentBrush,
            CornerRadius      = new CornerRadius(8),
            Width             = 36,
            Height            = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text                = "⌨",
                FontSize            = 16,
                Foreground          = AccentForegroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, -1, 0, 0),
            },
        };

        var titleText = new TextBlock
        {
            Text              = "Keyboard Shortcuts",
            FontSize          = 17,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = PrimaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children          = { iconBadge, titleText },
        };

        var hintText = new TextBlock
        {
            Text         = "Click a shortcut below, then press a new key combination. Press Escape to cancel (so Escape itself can only be reassigned via Reset, not capture).",
            FontSize     = 12,
            Foreground   = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Opacity      = 0.92,
        };

        var headerDivider = new Border
        {
            Height     = 1,
            Background = SurfaceBorderBrush,
            Opacity    = 0.9,
            Margin     = new Thickness(0, 2),
        };

        var editableHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 8,
            Margin      = new Thickness(0, 0, 0, 2),
            Children =
            {
                new Border
                {
                    Width             = 3,
                    Height            = 14,
                    Background        = AccentBrush,
                    CornerRadius      = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text              = "Editable",
                    FontSize          = 11,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = MutedTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    LetterSpacing     = 0.3,
                },
            },
        };

        var otherHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 8,
            Margin      = new Thickness(0, 8, 0, 2),
            Children =
            {
                new Border
                {
                    Width             = 3,
                    Height            = 14,
                    Background        = AccentBrush,
                    CornerRadius      = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = 0.55,
                },
                new TextBlock
                {
                    Text              = "Other",
                    FontSize          = 11,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = MutedTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    LetterSpacing     = 0.3,
                },
            },
        };

        var editableGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*,Auto,Auto"),
            RowDefinitions    = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", KeybindDefinitions.Length))),
        };

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
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(8, 4),
                Margin          = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center,
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

        var statusBorder = new Border
        {
            Background      = new SolidColorBrush(Color.Parse("#E5484D"), 0.08),
            BorderBrush     = new SolidColorBrush(Color.Parse("#E5484D"), 0.22),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(10, 7),
            IsVisible       = false,
            Child           = statusText,
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

        void UpdateStatus(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                statusText.Text      = string.Empty;
                statusText.IsVisible = false;
                statusBorder.IsVisible = false;
            }
            else
            {
                statusText.Text      = message;
                statusText.IsVisible = true;
                statusBorder.IsVisible = true;
            }
        }

        void CancelCapture()
        {
            if (capturingId is null) return;
            if (editButtons.TryGetValue(capturingId, out var btn))
                btn.Content = "Edit";
            capturingId = null;
            UpdateStatus(null);
        }

        for (var i = 0; i < KeybindDefinitions.Length; i++)
        {
            var def = KeybindDefinitions[i];

            var gestureBorder = new Border
            {
                Background      = CardBrush,
                BorderBrush     = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(8, 4),
                Margin          = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center,
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
                editButton.Content = "Press keys…";
                UpdateStatus(null);
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

        var sectionDivider = new Border
        {
            Height     = 1,
            Background = SurfaceBorderBrush,
            Opacity    = 0.85,
            Margin     = new Thickness(0, 4),
        };

        var innerStack = new StackPanel
        {
            Spacing  = 2,
            Children = { editableHeader, editableGrid, sectionDivider, otherHeader, fixedGrid },
        };

        var scroll = new ScrollViewer
        {
            Content = innerStack,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight                     = 380,
        };

        var scrollBorder = new Border
        {
            Background      = WindowBackgroundBrush,
            BorderBrush     = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(12, 10),
            Child           = scroll,
        };

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

        var footerHint = new TextBlock
        {
            Text              = "Changes save automatically",
            FontSize          = 11,
            Foreground        = MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity           = 0.75,
        };

        var footerDivider = new Border
        {
            Height     = 1,
            Background = SurfaceBorderBrush,
            Opacity    = 0.9,
            Margin     = new Thickness(0, 4, 0, 0),
        };

        var footerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children          = { footerHint, dismissButton },
        };
        Grid.SetColumn(dismissButton, 1);

        var content = new StackPanel
        {
            Spacing  = 12,
            Children = { headerRow, hintText, headerDivider, scrollBorder, statusBorder, footerDivider, footerRow },
        };

        Window? dialog = null;
        dialog = new Window
        {
            Title                 = "Kodo - Keyboard Shortcuts",
            Width                 = 560,
            SizeToContent         = SizeToContent.Height,
            MinWidth              = 440,
            MaxHeight             = 720,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = WindowBackgroundBrush,
            Content = new Border
            {
                Background      = CardBrush,
                BorderBrush     = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(12),
                Padding         = new Thickness(20),
                Margin          = new Thickness(16),
                Child           = content,
            },
        };

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

            if (!IsValidKeybindGesture(newGesture))
            {
                UpdateStatus("Shortcuts need at least one modifier key (Ctrl, Alt, Shift, or Win), unless it's a function key.");
                return;
            }

            var capturingDef = KeybindDefinitions.First(d => d.Id == capturingId);
            var conflict = KeybindDefinitions.FirstOrDefault(d =>
                d.Id != capturingId &&
                d.IsContextLocal == capturingDef.IsContextLocal &&
                _keybinds[d.Id].Key == newGesture.Key &&
                _keybinds[d.Id].KeyModifiers == newGesture.KeyModifiers);

            if (conflict is not null)
            {
                UpdateStatus($"{FormatGesture(newGesture)} is already used by \"{conflict.Description}\".");
                return;
            }

            var id = capturingId;
            _keybinds[id] = newGesture;
            RefreshRow(id);
            SaveSettings(immediate: true);

            if (editButtons.TryGetValue(id, out var btn))
                btn.Content = "Edit";
            capturingId = null;
            UpdateStatus(null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        dismissButton.Click += (_, _) => dialog!.Close();
        _ = dialog.ShowDialog(this);
    }

    private int _homeVersionTapCount;
    private DateTime _homeVersionLastTap;

    private void HomeVersionText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if (!e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed) return;

        var now = DateTime.UtcNow;
        if ((now - _homeVersionLastTap).TotalSeconds > 1.2)
            _homeVersionTapCount = 0;
        _homeVersionLastTap = now;
        _homeVersionTapCount++;

        if (_homeVersionTapCount < 7)
            return;

        _homeVersionTapCount = 0;
        var originalText = tb.Text;
        tb.Text = "Hehe, that tickles!";
        var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        revertTimer.Tick += (_, _) =>
        {
            revertTimer.Stop();
            tb.Text = originalText;
        };
        revertTimer.Start();
    }

    private int _aboutVersionTapCount;
    private DateTime _aboutVersionLastTap;

    private void AboutVersionText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if (!e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed) return;

        var now = DateTime.UtcNow;
        if ((now - _aboutVersionLastTap).TotalSeconds > 1.2)
            _aboutVersionTapCount = 0;
        _aboutVersionLastTap = now;
        _aboutVersionTapCount++;

        if (_aboutVersionTapCount < 7)
            return;

        _aboutVersionTapCount = 0;
        var originalText = tb.Text;
        tb.Text = "That tickles!";
        var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        revertTimer.Tick += (_, _) =>
        {
            revertTimer.Stop();
            tb.Text = originalText;
        };
        revertTimer.Start();
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

    private async void ClearCacheButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "Clear cache?",
            "This permanently deletes cache files from disk. This can't be undone.",
            confirmLabel: "Clear",
            isDestructive: true);

        if (!confirmed) return;

        var clearedAny = false;
        var failures = new List<string>();

        var kodoDir = KodoDiagnostics.LogDirectoryPath;
        if (Directory.Exists(kodoDir))
        {
            foreach (var file in Directory.GetFiles(kodoDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                try
                {
                    // Preserve log files and settings - those are handled separately
                    if (fileName == "kodo.log" || fileName == "crash.log" || fileName == "kodosettings.json") continue;
                    File.Delete(file);
                    clearedAny = true;
                }
                catch (Exception ex)
                {
                    failures.Add($"{fileName} ({ex.Message})");
                }
            }
        }

        DeveloperOptionsStatusText = failures.Count > 0
            ? $"Couldn't clear: {string.Join(", ", failures)}"
            : clearedAny
                ? "Cache cleared."
                : "No cache to clear.";
    }

    private async void ResetKodoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "Reset Kodo?",
            "This permanently deletes ALL Kodo data (logs, cache, settings) and relaunches the application. This can't be undone.",
            confirmLabel: "Reset",
            isDestructive: true);

        if (!confirmed) return;

        // Prevent closing prompts and settings writes while handing reset to a clean process.
        _isConfirmedClose = true;
        _suppressSettingsSave = true;
        _settingsSaveDebounceTimer.Stop();

        // The next process removes data before constructing MainWindow or loading settings.
        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--reset-kodo",
                    UseShellExecute = true,
                });
                Environment.Exit(0);
            }
            catch { /* best effort */ }
        }
    }

    private string BuildDiagnosticReport()
    {
        var sb = new StringBuilder();

        void Section(string title)
        {
            sb.AppendLine();
            sb.AppendLine($"── {title} ──");
        }

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

        Section("Appearance");
        sb.AppendLine($"Theme: {(IsSystemThemeActive ? $"System Default ({CurrentThemeName})" : CurrentThemeName)}");
        sb.AppendLine($"Accent mode: {_accentColorMode}");
        if (_accentColorMode == "custom")
            sb.AppendLine($"Custom accent: {_customAccentHex}");

        Section("Terminal");
        sb.AppendLine($"Preferred shell: {SelectedTerminalShell?.DisplayName ?? "(none)"}");

        Section("Discord Rich Presence");
        sb.AppendLine($"Enabled: {IsDiscordRichPresenceEnabled}");
        sb.AppendLine($"Improved RPC: {IsDiscordImprovedRpcEnabled}");

        var extensions = VisibleLoadedExtensions.ToList();
        Section($"Installed Extensions ({extensions.Count})");
        if (extensions.Count == 0)
            sb.AppendLine("(none)");
        else
            foreach (var ext in extensions)
                sb.AppendLine($"{ext.Name} v{ext.Version} ({ext.Type}) by {ext.Author}");

        Section("File Locations");
        sb.AppendLine($"Settings: {SettingsFilePath}");
        sb.AppendLine($"Main log: {MainLogFilePath}");
        sb.AppendLine($"Crash log: {CrashLogFilePath}");
        sb.AppendLine($"Extensions folder: {ExtensionsFolderPath}");
        sb.AppendLine($"Debug logging: {IsVerboseLoggingEnabled}");

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





    // Convenience handlers used by the tutorial setup step's theme buttons.













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







    private async Task RestoreExpandedPathsAsync(HashSet<string> expandedPaths)
    {
        // Keep expanding until no more progress can be made (handles nested directories).
        bool anyExpanded;
        do
        {
            anyExpanded = false;
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

    private void CopyEditorTabNameMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(Path.GetFileName(tab.Path));
    }





    private async void CollapseAllTreeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _isFileTreeExpanded = !_isFileTreeExpanded;
        if (!_isFileTreeExpanded)
        {
            if (!string.IsNullOrWhiteSpace(_currentFolderPath))
                await PopulateFileTreeAsync(_currentFolderPath);
        }
        else
        {
            var rootDirs = FileTreeItems.Where(i => i.IsDirectory && i.Depth == 0 && !i.IsExpanded).ToList();
            foreach (var dir in rootDirs)
                await ToggleDirectoryExpansionAsync(dir);
        }
    }





















    private void CopyRelativeFilePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(GetRelativePathOrFullPath(item.FullPath));
    }





    private void RenameFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        BeginInlineRename(item);
    }

    private void BeginInlineRename(FileTreeItem item)
    {
        item.RenameText = item.Name;
        item.IsRenaming = true;
        Dispatcher.UIThread.Post(() =>
        {
            var textBox = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, item));
            textBox?.Focus();
            textBox?.SelectAll();
        });
    }

    private async void InlineRenameTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox textBox && textBox.DataContext is FileTreeItem item)
        {
            item.IsRenaming = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && sender is TextBox enterTextBox && enterTextBox.DataContext is FileTreeItem enterItem)
        {
            e.Handled = true;
            await CompleteInlineRenameAsync(enterItem);
        }
    }

    private async void InlineRenameTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is FileTreeItem item && item.IsRenaming)
            await CompleteInlineRenameAsync(item);
    }

    private async Task CompleteInlineRenameAsync(FileTreeItem item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;

        var newName = item.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, item.Name, StringComparison.Ordinal)) return;

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





































































    // Unified search: debounced execution for Files/Project modes.





































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










    private async void EncodingStatusBarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasFileOpen) return;

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

        var accentColor = AccentBrush.ToImmutable() is ISolidColorBrush accentSolid
            ? accentSolid.Color
            : Color.Parse("#8C00FF");

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border
                {
                    Width = 3,
                    Height = 16,
                    Background = AccentBrush,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "Save file with encoding:",
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var headerDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 6)
        };

        var footerDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 6)
        };

        var listPanel = new StackPanel { Spacing = 6 };

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
                    : ButtonBrush,
                Foreground = isCurrent
                    ? new SolidColorBrush(accentColor)
                    : PrimaryTextBrush,
                BorderBrush = SurfaceBorderBrush,
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
            listPanel.Children.Add(btn);
        }

        var listBorder = new Border
        {
            Background = WindowBackgroundBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = listPanel
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                headerRow,
                new TextBlock { Text = "Choose an encoding for this file:", FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap, Opacity = 0.92 },
                headerDivider,
                listBorder,
                new TextBlock
                {
                    Text = "The file will be re-saved immediately with the chosen encoding.",
                    FontSize = 11,
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                },
                footerDivider
            }
        };

        var outer = new Border
        {
            Background = CardBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(16),
            Child = panel
        };

        dialog = new Window
        {
            Title = "Change File Encoding",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            MinWidth = 300,
            MaxHeight = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBackgroundBrush,
            Content = outer,
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










    private void EditorContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasSelection = EditorTextBox?.TextArea?.Selection is { IsEmpty: false };
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Name is "EditorCutMenuItem" or "EditorCopyMenuItem" or "EditorFindAllOccurrencesMenuItem" or "EditorChangeAllOccurrencesMenuItem")
                item.IsEnabled = hasSelection;
            if (item.Name == "EditorDismissDiagnosticMenuItem")
            {
                var diag = GetDiagnosticAtCaret();
                var hasDiagnostic = diag is not null;
                item.IsEnabled = hasDiagnostic;
                item.Header = hasDiagnostic ? $"Dismiss: {diag?.message ?? "Diagnostic"}" : "Dismiss Diagnostic";
            }
        }
    }

    private (string? filePath, string lineText, string message, int offset, int length)? GetDiagnosticAtCaret()
    {
        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null) return null;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var docText = EditorTextBox.Document.Text;
        var lines = docText.Split('\n');
        foreach (var err in _errorHighlightRenderer.Spans)
        {
            if (caretOffset >= err.StartOffset && caretOffset < err.StartOffset + err.Length)
            {
                var lineIdx = docText.Substring(0, err.StartOffset).Count(c => c == '\n');
                var lineText = lineIdx < lines.Length ? lines[lineIdx] : string.Empty;
                return (_currentFilePath, lineText, err.Message, err.StartOffset, err.Length);
            }
        }
        foreach (var dead in _deadCodeHighlightRenderer.Spans)
        {
            if (caretOffset >= dead.StartOffset && caretOffset < dead.StartOffset + dead.Length)
            {
                var lineIdx = docText.Substring(0, dead.StartOffset).Count(c => c == '\n');
                var lineText = lineIdx < lines.Length ? lines[lineIdx] : string.Empty;
                return (_currentFilePath, lineText, dead.Reason, dead.StartOffset, dead.Length);
            }
        }
        var caretLine = EditorTextBox.Document.GetLineByOffset(caretOffset);
        var caretLineText = EditorTextBox.Document.GetText(caretLine.Offset, caretLine.Length);
        foreach (var err in _errorHighlightRenderer.Spans)
        {
            var errLine = docText.Substring(0, err.StartOffset).Count(c => c == '\n');
            if (errLine == docText.Substring(0, caretOffset).Count(c => c == '\n'))
            {
                return (_currentFilePath, caretLineText, err.Message, err.StartOffset, err.Length);
            }
        }
        foreach (var dead in _deadCodeHighlightRenderer.Spans)
        {
            var deadLine = docText.Substring(0, dead.StartOffset).Count(c => c == '\n');
            if (deadLine == docText.Substring(0, caretOffset).Count(c => c == '\n'))
            {
                return (_currentFilePath, caretLineText, dead.Reason, dead.StartOffset, dead.Length);
            }
        }
        return null;
    }

    private void EditorDismissDiagnosticMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        var diag = GetDiagnosticAtCaret();
        if (diag is null) return;
        var (filePath, lineText, message, _, _) = diag.Value;
        DismissDiagnostic(filePath, lineText, message);
    }



    private void EditorChangeAllOccurrencesMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var selectedText = sel.GetText();
        if (string.IsNullOrEmpty(selectedText)) return;

        FindText = selectedText;
        OpenSearchPanel(SearchMode.FindInFile);

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













    private void IncreaseFontSizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorFontSize = Math.Min(32, EditorFontSize + 1);

    private void DecreaseFontSizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorFontSize = Math.Max(8, EditorFontSize - 1);












    // Fires hourly to keep the Marketplace tab current, even with auto-update off.




    // Silently installs pending updates when opted in; guards against overlap.








    private void EditorTextView_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isPointerOverEditorLink && _hoveredDeadCodeReason is null && _hoveredErrorReason is null) return;
        // Lenient: 650ms + 14px transparent hit border to comfortably reach tooltip
        _diagnosticPopupHideTimer.Stop();
        _diagnosticPopupHideTimer.Start();
    }

    private void DiagnosticPopup_OnPointerEntered(object? sender, PointerEventArgs e) => _diagnosticPopupHideTimer.Stop();

    private void DiagnosticPopup_OnPointerExited(object? sender, PointerEventArgs e)
    {
        _diagnosticPopupHideTimer.Stop();
        _diagnosticPopupHideTimer.Start();
    }

    private void DiagnosticPopupHideTimer_OnTick(object? sender, EventArgs e)
    {
        _diagnosticPopupHideTimer.Stop();
        var textView = EditorTextBox.TextArea.TextView;
        if (textView.IsPointerOver || DiagnosticPopup.IsPointerOver || DiagnosticPopupBorder.IsPointerOver || DiagnosticPopupHitBorder.IsPointerOver) return;
        if (!_isPointerOverEditorLink && _hoveredDeadCodeReason is null && _hoveredErrorReason is null) return;
        _isPointerOverEditorLink = false;
        _hoveredDeadCodeReason = null;
        _hoveredErrorReason = null;
        _hoveredDiagnosticLineText = null;
        _hoveredDiagnosticMessage = null;
        DiagnosticPopup.IsOpen = false;
        ToolTip.SetTip(textView, null);
        textView.Cursor = new Cursor(StandardCursorType.Ibeam);
    }





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



	// Fires before the character is written; skips an auto-inserted closing character.












    private void ClearDeadCodeHighlighting()
    {
        _deadCodeHighlightRenderer.SetSpans(Array.Empty<InsightEngine.DeadCodeSpan>());
        _deadCodeTextBrightener.SetSpans(Array.Empty<InsightEngine.DeadCodeSpan>());
        var emptyDead = Array.Empty<InsightEngine.DeadCodeSpan>();
        _errorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _errorTextDarkener.SetSpans(_errorHighlightRenderer.Spans, emptyDead);
        EditorTextBox?.TextArea.TextView.Redraw();
    }

    private void UpdateErrorHighlighting()
    {
        if (!IsInsightEnabled || !IsInsightErrorDetectionEnabled ||
            EditorTextBox?.Document is null ||
            ActiveEditorTab is null || ActiveEditorTab.IsUntitled ||
            IsPlainTextFile(_currentFilePath) ||
            HasNoFileExtension(_currentFilePath) ||
            IsInsightBlacklisted(_currentFilePath))
        {
            ClearErrorHighlighting();
            HideDiagnosticPopup();
            return;
        }

        var rawSpans = _InsightEngine.FindErrors(EditorTextBox.Document.Text, CurrentLanguageExtension);
        var spans = FilterDismissedErrorSpans(rawSpans, EditorTextBox.Document, _currentFilePath);
        _errorHighlightRenderer.SetSpans(spans);
        _errorHighlightRenderer.SetDeadCodeSpans(_deadCodeHighlightRenderer.Spans);
        _errorTextDarkener.SetSpans(spans, _deadCodeHighlightRenderer.Spans);
        if (spans.Count != rawSpans.Count)
            HideDiagnosticPopup();
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        EditorTextBox.TextArea.TextView.Redraw();
    }

    private List<InsightEngine.ErrorSpan> FilterDismissedErrorSpans(List<InsightEngine.ErrorSpan> spans, AvaloniaEdit.Document.TextDocument doc, string? filePath)
    {
        if (_dismissedDiagnostics.Count == 0 || spans.Count == 0) return spans;
        var filtered = new List<InsightEngine.ErrorSpan>(spans.Count);
        foreach (var span in spans)
        {
            try
            {
                var line = doc.GetLineByOffset(Math.Clamp(span.StartOffset, 0, doc.TextLength));
                var lineText = doc.GetText(line.Offset, line.Length);
                if (IsDiagnosticDismissed(filePath, lineText, span.Message))
                    continue;
            }
            catch { }
            filtered.Add(span);
        }
        return filtered;
    }

    private void ClearErrorHighlighting()
    {
        var emptyErr = Array.Empty<InsightEngine.ErrorSpan>();
        var emptyDead = Array.Empty<InsightEngine.DeadCodeSpan>();
        _errorHighlightRenderer.SetSpans(emptyErr);
        _errorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _errorTextDarkener.SetSpans(emptyErr, emptyDead);
        EditorTextBox?.TextArea.TextView.Redraw();
    }

    private const double InsightRowHeight = 32d;
    private const double InsightListVerticalPadding = 12d; // ListBox Padding(6,4) + margin breathing
    private const double InsightBorderThickness = 2d;       // 1px top + 1px bottom
    private const int InsightVisibleRows = 7;









    private static void SetCaretOffsetSafely(
        AvaloniaEdit.Editing.Caret caret,
        AvaloniaEdit.Document.TextDocument doc,
        int desiredOffset)
    {
        caret.Offset = Math.Clamp(desiredOffset, 0, doc.TextLength);
    }













    private string GetIndentUnit() => "\t";



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



    private static string GetSharedIndent(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var length = 0;
        while (length < max && left[length] == right[length])
            length++;

        return left[..length];
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

        // Ensure settings are saved before closing, regardless of debounce timer state.
        SaveSettings(immediate: true);

        var dirtyTabs = OpenTabs.Where(t => t.IsDirty).ToList();
        if (dirtyTabs.Count == 0 || !IsConfirmBeforeClosingUnsavedTabsEnabled) return;

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
        _searchFilterDebounceTimer.Stop();
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



    // Runs every 2s; refreshes the System Default preview on a Windows theme change.




    private void NetworkChange_OnNetworkAddressChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => RefreshMarketplaceConnectivityState());





    // Recognizes GitHub's 403/429 responses and swaps in a clearer message.






    private TutorialStep CurrentTutorialStep => TutorialSteps[TutorialStepIndex];

    private void OnTutorialStepChanged()
    {
        RaiseMany(nameof(TutorialStepIndex), nameof(TutorialStepLabel), nameof(TutorialProgressDotsText), nameof(TutorialSectionTitle), nameof(TutorialTitle), nameof(TutorialBody), nameof(TutorialShortcutText), nameof(TutorialSpotlightTitle), nameof(TutorialHighlightOne), nameof(TutorialHighlightTwo), nameof(TutorialHighlightThree), nameof(CanGoToPreviousTutorialStep), nameof(TutorialPrimaryButtonText), nameof(IsTutorialSetupStep), nameof(IsNotTutorialSetupStep), nameof(IsTutorialWelcomeStep), nameof(IsNotTutorialWelcomeStep), nameof(IsTutorialHeaderVisible));
    }

    private void CompleteTutorialAndReturnHome()
    {
        _hasCompletedTutorial = true;
        SaveSettings();
        NavigateTo(AppPage.Home);
    }

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
        if (IsTerminalVisible && ActiveTerminalSession is not null)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            if (focused is not null &&
                (ReferenceEquals(focused, TerminalHostControl) ||
                 focused.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, TerminalHostControl))))
                return;
        }

        var hasControl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;

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
                NavigateTo(AppPage.Editor);
                FocusEditor();
                e.Handled = true;
            }
            return;
        }

        if (MatchesKeybind(e, "NewFile"))
        {
            NewFile();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "GoEditor"))
        {
            NavigateTo(AppPage.Editor);
            FocusEditor();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "OpenExtensions") || MatchesKeybind(e, "OpenExtensionsAlt"))
        {
            NavigateTo(AppPage.Extensions);
            RefreshMarketplaceConnectivityState();
            _ = RefreshExtensionsDataAsync();
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "OpenSettings"))
        {
            NavigateTo(AppPage.Settings);
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "GoHome"))
        {
            NavigateTo(AppPage.Home);
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


    private static int NormalizeTabSize(int value) => value is 2 or 4 or 8 ? value : 4;





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
}
