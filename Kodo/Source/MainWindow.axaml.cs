// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Kodo.Models;
using Kodo.Editor;
using DiscordAssetsModel = DiscordRPC.Assets;
using DiscordRichPresenceModel = DiscordRPC.RichPresence;
using DiscordRpcClient = DiscordRPC.DiscordRpcClient;

namespace Kodo;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private EditorDocumentSession _editorSession = null!;
    private EditorDocumentSession EditorSession => _editorSession;
    private const int MaxRecentFiles = 6;
    private const string DefaultDiscordClientId = "1495509170756255744";
    private const string DefaultDiscordLargeImageKey = "kodo_logo";
    private const string DefaultDiscordLargeImageText = "Kodo";
    private const string DiscordClientIdEnvironmentVariable = "KODO_DISCORD_CLIENT_ID";
    private const string AutoSaveSavedMessage = "Saved.";
    private const string AutoSaveSavingMessage = "Saving...";
    private const string AutoSaveFailedMessagePrefix = "Save failed:";
    private static readonly string CurrentAppVersion = KodoDiagnostics.AppVersion;
    public string CopyrightText => $"© {DateTime.Now.Year} Kodo-IDE, built by KerbalMissile and SS-YYC. Licensed under the GNU GPL-v3.0 license.";
    public string AboutTitle => $"Kodo for {AboutPlatformName}";

    private static string AboutPlatformName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" : "your platform";
    private static readonly string[] MarketplaceIndexUrls =
    [
        "https://raw.githubusercontent.com/Kodo-IDE/Kodo-Extensions/main/Indexs/ExtensionsIndex.json",
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/ExtensionsIndex.json",
    ];
    private static readonly string[] CompilerIndexUrls =
    [
        "https://raw.githubusercontent.com/Kodo-IDE/Kodo-Extensions/main/Indexs/CompilerIndex.json",
        "https://api.github.com/repos/Kodo-IDE/Kodo-Extensions/contents/Indexs/CompilerIndex.json",
    ];
    private static readonly string[] PluginsIndexUrls =
    [
        "https://raw.githubusercontent.com/Kodo-IDE/Kodo-Extensions/main/Indexs/PluginsIndex.json",
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
    private const string WebsiteUrl = "https://kodo-ide.github.io/";
    private const string DownloadKodoUrl = "https://github.com/Kodo-IDE/Kodo/releases/latest";
    private const string InstagramUrl = "https://www.instagram.com/kodo.ide/";
    private const string GitHubRepoUrl = "https://github.com/Kodo-IDE";
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
    private bool _isDebouncedSearchEnabled;
    private bool _extensionSearchPending;
    private bool _settingsSearchPending;

    private string? _currentFilePath;
    private System.Text.Encoding _currentFileEncoding = Kodo.Models.EditorTab.DefaultEncoding;
    private Kodo.Models.LineEnding _currentLineEnding = Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;
    private string? _currentFolderPath;
    private DiscordRpcClient? _discordRpcClient;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _autoSaveStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _discordReconnectTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _editorStateRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _wordCountRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _InsightRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _lspRefreshDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _findHighlightDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _diagnosticPopupHideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private AvaloniaEdit.Folding.FoldingManager? _primaryLspFoldingManager;
    private AvaloniaEdit.Folding.FoldingManager? _secondaryLspFoldingManager;
    private AvaloniaEdit.Folding.FoldingManager? _lspFoldingManager
    {
        get => ReferenceEquals(_activeEditorPane, _secondaryEditorPane)
            ? _secondaryLspFoldingManager
            : _primaryLspFoldingManager;
        set
        {
            if (ReferenceEquals(_activeEditorPane, _secondaryEditorPane))
                _secondaryLspFoldingManager = value;
            else
                _primaryLspFoldingManager = value;
        }
    }
    private readonly DispatcherTimer _diagnosticPopupShowTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly UiStallWatchdog _stallWatchdog = new();
    private bool _layoutProbeArmed;
    private string? _layoutProbeStage;
    private long _layoutProbeStartTimestamp;
    private int _layoutProbeThresholdMs = 500;

    private void ArmLayoutProbe(string stage, int thresholdMs = 500)
    {
        var editor = EditorTextBox;
        if (editor is null || _layoutProbeArmed) return;
        _layoutProbeArmed = true;
        _layoutProbeStage = stage;
        _layoutProbeThresholdMs = thresholdMs;
        _layoutProbeStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        editor.LayoutUpdated += LayoutProbe_OnLayoutUpdated;
    }

    private void LayoutProbe_OnLayoutUpdated(object? sender, EventArgs e)
    {
        var editor = EditorTextBox;
        if (editor is not null) editor.LayoutUpdated -= LayoutProbe_OnLayoutUpdated;
        var stage = _layoutProbeStage;
        var threshold = _layoutProbeThresholdMs;
        _layoutProbeArmed = false;
        _layoutProbeStage = null;
        if (stage is null) return;
        var elapsed = (long)(System.Diagnostics.Stopwatch.GetTimestamp() - _layoutProbeStartTimestamp) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        KodoDiagnostics.ReportSlowStage(stage, elapsed, threshold, $"len={editor?.Document?.TextLength ?? 0}");
    }
    private readonly DispatcherTimer _settingsSaveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _editorMinimapRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _editorPaneSizeRefreshPending;
    private readonly object _settingsWriteLock = new();
    private AppSettings? _pendingSettingsSnapshot;
    private bool _isPersistingSettings;
    private readonly DispatcherTimer _windowsAccentPollTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _lastSeenWindowsAccentHex = string.Empty;
    private readonly DispatcherTimer _windowsThemePollTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _lastSeenWindowsThemeName = string.Empty;
    private string _systemAccentHex = "#8C00FF";
    private bool _hasSystemAccent;
    private bool _lspEnabled = true;
    private bool _lspCompletionEnabled = true;
    private bool _lspHoverEnabled = true;
    private bool _lspSignatureHelpEnabled = true;
    private bool _lspInlayHintsEnabled = true;
    private bool _lspFoldingEnabled = true;
    private bool _lspDocumentHighlightsEnabled = true;
    private bool _lspDiagnosticsEnabled = true;
    private bool _lspSemanticHighlightingEnabled;
    private long _lspCompletionSettingsRevision;
    private bool _lspAutoInstall;
    private bool _lspPreferManaged = true;
    private bool _lspPreferSystem = true;
    private string? _lspInstallDir;
    private readonly Dictionary<string, string> _lspExecutableOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _lspDisabledLanguages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lspDismissedInstallPrompts = new(StringComparer.OrdinalIgnoreCase);

    private readonly RainbowBracketColorizer _primaryRainbowBracketColorizer = new();
    private readonly RainbowBracketColorizer _secondaryRainbowBracketColorizer = new();
    private RainbowBracketColorizer _rainbowBracketColorizer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryRainbowBracketColorizer : _primaryRainbowBracketColorizer;
    private readonly InterpolatedStringColorizer _primaryInterpolatedStringColorizer = new();
    private readonly InterpolatedStringColorizer _secondaryInterpolatedStringColorizer = new();
    private InterpolatedStringColorizer _interpolatedStringColorizer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryInterpolatedStringColorizer : _primaryInterpolatedStringColorizer;
    private readonly HtmlEmbeddedColorizer _primaryHtmlEmbeddedColorizer = new();
    private readonly HtmlEmbeddedColorizer _secondaryHtmlEmbeddedColorizer = new();
    private HtmlEmbeddedColorizer _htmlEmbeddedColorizer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryHtmlEmbeddedColorizer : _primaryHtmlEmbeddedColorizer;
    private readonly MarkdownColorizer _primaryMarkdownColorizer = new();
    private readonly MarkdownColorizer _secondaryMarkdownColorizer = new();
    private MarkdownColorizer _markdownColorizer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryMarkdownColorizer : _primaryMarkdownColorizer;
    private readonly EmojiTypefaceColorizer _primaryEmojiTypefaceColorizer = new();
    private readonly EmojiTypefaceColorizer _secondaryEmojiTypefaceColorizer = new();
    private EmojiTypefaceColorizer _emojiTypefaceColorizer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryEmojiTypefaceColorizer : _primaryEmojiTypefaceColorizer;
    private readonly InsightEngine _InsightEngine = new();
    private CompletionWindow? _completionWindow;
    private TextEditor? _completionEditor;
    private string? _lastSuggestionsText;
    private long _insightDocVersion => EditorSession.Revision;
    private CancellationTokenSource _insightAnalysisCancellation = new();
    private CancellationTokenSource _deadCodeAnalysisCancellation = new();
    private CancellationTokenSource _errorAnalysisCancellation = new();
    private readonly object _insightAnalysisCacheLock = new();
    private long _cachedInsightAnalysisVersion = -1;
    private string? _cachedInsightAnalysisPath;
    private string? _cachedInsightAnalysisText;
    private string? _cachedInsightAnalysisExtension;
    private List<ErrorSpan>? _cachedInsightAnalysisSpans;
    private readonly DeadCodeHighlightRenderer _primaryDeadCodeHighlightRenderer = new();
    private readonly DeadCodeHighlightRenderer _secondaryDeadCodeHighlightRenderer = new();
    private DeadCodeHighlightRenderer _deadCodeHighlightRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryDeadCodeHighlightRenderer : _primaryDeadCodeHighlightRenderer;
    private readonly DeadCodeTextBrightener _primaryDeadCodeTextBrightener = new();
    private readonly DeadCodeTextBrightener _secondaryDeadCodeTextBrightener = new();
    private DeadCodeTextBrightener _deadCodeTextBrightener => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryDeadCodeTextBrightener : _primaryDeadCodeTextBrightener;
    private readonly ErrorLineHighlightRenderer _primaryErrorHighlightRenderer = new();
    private readonly ErrorLineHighlightRenderer _secondaryErrorHighlightRenderer = new();
    private ErrorLineHighlightRenderer _errorHighlightRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryErrorHighlightRenderer : _primaryErrorHighlightRenderer;
    private readonly ErrorTextDarkener _primaryErrorTextDarkener = new();
    private readonly ErrorTextDarkener _secondaryErrorTextDarkener = new();
    private ErrorTextDarkener _errorTextDarkener => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryErrorTextDarkener : _primaryErrorTextDarkener;
    private readonly EditorPaneState _editorPane = new();
    private readonly EditorPaneState _secondaryEditorPane = new();
    private EditorPaneState _activeEditorPane = null!;
    private bool _isSplitScreenVisible;
    private int _nextUntitledTabNumber = 1;
    private string? _autoSaveStatusMessage;
    private bool _isAutoSaveEnabled;
    private bool _isDirty;
    private bool _isSaving;
    private bool _isDiscordRichPresenceEnabled;
    private bool _isGitSourceControlEnabled;
    private bool _hasUntitledDocument;
    private bool _isRefreshingExtensions;
    private Task? _initialExtensionScanTask;
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
    private bool _isUpdateSplashVisible;
    private bool _openingSplashShowsReleaseNotes;
    private string _lastSeenVersion = string.Empty;
    private bool _isHomePageVisible;
    private bool _isFileExplorerVisible;
    private bool _isFileTreeExpanded;
    private bool _isStatusBarFilePathVisible = true;
    private bool _isWordWrapEnabled;
    private bool _isMinimapVisible = true;
    private bool _isInsightEnabled = true;
    private bool _isInsightCodeSuggestionsEnabled = true;
    private bool _isInsightDeadCodeEnabled = true;
    private bool _isInsightErrorDetectionEnabled = true;
    private string _insightBlacklistExtensions = ".txt,.md";
    private HashSet<string> _insightBlacklistSet = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };
    private string _insightErrorDeadCodeBlacklistExtensions = string.Empty;
    private HashSet<string> _insightErrorDeadCodeBlacklistSet = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _dismissedDiagnostics = new(StringComparer.Ordinal);
    private static int _lspDiagnosticsRevision;
    private int _dismissedDiagnosticsRevision;
    private long _errorHighlightAppliedSignature = -1;
    private long _deadCodeAppliedSignature = -1;
    private bool _suppressExplorerWidthRefresh;
    private bool _isConfirmBeforeClosingUnsavedTabsEnabled = true;
    private bool _isRestoreOpenTabsOnLaunchEnabled;
    private string _selectedExtensionsTab = ExtensionsTabModes.Installed;
    private bool _suppressDirtyTracking;
    private bool _suppressSettingsSave;
    private bool _isDeveloperOptionsVisible;
    private bool _isVerboseLoggingEnabled;
    private int _tabSize = 4;
    private bool _insertSpaces = true;
    private int _editorFontSize = 14;
    private string _editorFontFamily = "";
    private int _terminalScrollbackLines = 5000;
    private string _accentColorMode = "kodo";
    private string _customAccentHex = "#8C00FF";
    private string _themeAccentHex = "#8C00FF";
    private bool _hasThemeAccent = false;
    private string _windowBackgroundHex = "#1E1E1E";
    private bool _hasWindowBackground = false;
    private string _currentThemeName = "Dark";
    private string _requestedThemeName = "Dark";
    private string _editorStatsText = "0 lines";
    private string _wordCountText = string.Empty;
    private string _statusBarCaretText = string.Empty;
    private string _statusBarSelectionText = string.Empty;
    private string _statusBarDocumentText = string.Empty;
    private string _statusBarDiagnosticsText = "No problems";
    private string _statusBarDiagnosticsTooltip = "No problems detected";
    private bool _pendingFullStateRefresh = true;
    private (string?, string?, int, bool, bool, bool, bool, bool) _lastDiscordPresenceKey;
    private bool _hasDiscordPresenceKey;
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
    private readonly IndentGuideBackgroundRenderer _primaryIndentGuideRenderer = new();
    private readonly IndentGuideBackgroundRenderer _secondaryIndentGuideRenderer = new();
    private IndentGuideBackgroundRenderer _indentGuideRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryIndentGuideRenderer : _primaryIndentGuideRenderer;
    private readonly List<string> _startupOpenTabPaths = [];
    private string? _startupFolderPath;
    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private ExtensionScanResult? _cachedThemeScan;
    private DateTime _cachedThemeScanUtc = DateTime.MinValue;
    private readonly TimeSpan ThemeScanCacheValidity = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, byte[]> _marketplaceIconBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _marketplaceSvgCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _warningDialogCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconFetchSemaphore = new(3, 3);
    private readonly ConcurrentDictionary<string, Task<IconResult>> _iconFetchInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _rateLimitLock = new();
    private static int _gitHubRateLimitRemaining = int.MaxValue;
    private static DateTime _gitHubRateLimitResetUtc = DateTime.MinValue;
    private static DateTime _gitHubRateLimitBackoffUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan IconDiskCacheTtl = TimeSpan.FromDays(7);
    private static string IconDiskCacheDir => KodoPaths.CacheDir("IconCache");
    private static readonly TimeSpan ExtensionsRefreshCooldown = TimeSpan.FromSeconds(8);
    private string MarketplaceIndexCachePath =>
        KodoPaths.CacheFile("marketplace-index.json");
    private string MarketplaceIndexETagPath =>
        KodoPaths.CacheFile("marketplace-index.json.etag");
    private string? _marketplaceIndexETag;
    private string CompilerIndexCachePath =>
        KodoPaths.CacheFile("compiler-index.json");
    private string CompilerIndexETagPath =>
        KodoPaths.CacheFile("compiler-index.json.etag");
    private string? _compilerIndexETag;
    private string PluginsIndexCachePath =>
        KodoPaths.CacheFile("plugins-index.json");
    private string PluginsIndexETagPath =>
        KodoPaths.CacheFile("plugins-index.json.etag");
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
    private static readonly TimeSpan WarningDialogCooldown = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan GitHubOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ImagePreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif",
        ".webp", ".ico", ".cur", ".tif", ".tiff"
    };
    private DateTime _lastExtensionsRefreshUtc = DateTime.MinValue;
    private int _extensionFilterBatchDepth;
    private bool _pendingExtensionFilterNotify;
    private readonly List<Action> _deferredExtensionUiActions = new();
    private readonly object _deferredExtensionUiActionsLock = new();
    private void BeginExtensionFilterBatch() => _extensionFilterBatchDepth++;
    private void EndExtensionFilterBatch()
    {
        if (--_extensionFilterBatchDepth > 0) return;
        List<Action> actions;
        lock (_deferredExtensionUiActionsLock)
        {
            if (_deferredExtensionUiActions.Count == 0)
            {
                actions = new List<Action>();
            }
            else
            {
                actions = new List<Action>(_deferredExtensionUiActions);
                _deferredExtensionUiActions.Clear();
            }
        }
        if (actions.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var a in actions) a();
                if (_pendingExtensionFilterNotify)
                {
                    _pendingExtensionFilterNotify = false;
                    NotifyExtensionFiltersChangedCore();
                }
            }, DispatcherPriority.Background);
        }
        else if (_pendingExtensionFilterNotify)
        {
            _pendingExtensionFilterNotify = false;
            NotifyExtensionFiltersChangedCore();
        }
    }
    private void NotifyExtensionFiltersChangedCore()
    {
        RaiseMany(nameof(FilteredInstalledExtensions), nameof(FilteredInstalledLanguageExtensions), nameof(FilteredInstalledPluginExtensions), nameof(FilteredInstalledThemeExtensions), nameof(HasVisibleInstalledLanguageExtensions), nameof(HasVisibleInstalledPluginExtensions), nameof(HasVisibleInstalledThemeExtensions), nameof(IsInstalledLanguageDividerVisible), nameof(IsInstalledPluginDividerVisible), nameof(IsInstalledThemeDividerVisible), nameof(FilteredMarketplaceExtensions), nameof(FilteredCompilerExtensions), nameof(FilteredInstalledCompilerExtensions), nameof(IsNoExtensionsVisible), nameof(IsInstalledSearchEmptyVisible), nameof(IsMarketplaceSearchEmptyVisible), nameof(IsMarketplaceEmptyVisible), nameof(InstalledExtensionsCount), nameof(InstalledCompilersCount), nameof(MarketplaceEmptyStateText), nameof(HasVisibleInstalledExtensions), nameof(HasVisibleMarketplaceExtensions), nameof(HasVisibleCompilerExtensions), nameof(HasVisibleInstalledCompilerExtensions), nameof(HasVisibleInstalledExtensionsOrCompilers), nameof(HasVisibleInstalledExtensionsAndCompilers), nameof(IsInstalledCompilersEmptyStateVisible));
    }
    private void DeferExtensionUiAction(Action action)
    {
        lock (_deferredExtensionUiActionsLock)
        {
            _deferredExtensionUiActions.Add(action);
        }
    }

    private List<Action> TakeDeferredExtensionUiActions()
    {
        lock (_deferredExtensionUiActionsLock)
        {
            if (_deferredExtensionUiActions.Count == 0)
                return new List<Action>();
            var snapshot = new List<Action>(_deferredExtensionUiActions);
            _deferredExtensionUiActions.Clear();
            return snapshot;
        }
    }

    private void ClearDeferredExtensionUiActions()
    {
        lock (_deferredExtensionUiActionsLock)
        {
            _deferredExtensionUiActions.Clear();
        }
    }

    private async Task InvokeExtensionUiAsync(Action action)
    {
        if (_extensionFilterBatchDepth > 0)
        {
            lock (_deferredExtensionUiActionsLock)
            {
                _deferredExtensionUiActions.Add(action);
            }
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
    }
    private string? _startupActiveTabPath;
    private string? _startupPrimaryPaneTabPath;
    private string? _startupSecondaryPaneTabPath;
    private readonly List<string> _startupPrimaryPaneTabPaths = [];
    private readonly List<string> _startupSecondaryPaneTabPaths = [];
    private bool _startupSplitEditorVisible;
    private bool _startupActivePaneIsSecondary;
    private string? _startupFilePath;
    private string _extensionSearchText = string.Empty;
    private string _settingsSearchText = string.Empty;
    private string _selectedInstalledExtensionSort = ExtensionSortModes.Alphabetical;
    private string _selectedMarketplaceExtensionSort = ExtensionSortModes.Alphabetical;
    private string _userCountry = string.Empty;
    private int _userHemisphere = 0;
    private string _userTimezoneOffset = string.Empty;
    private string _userName = string.Empty;
    private bool _isSearchPanelVisible;
    private SearchMode _searchMode = SearchMode.FindInFile;
    private readonly ObservableCollection<SearchResultItem> _searchResults = new();
    private ObservableCollection<SearchDisplayItem> _searchDisplayItems = new();
    private readonly List<SearchFileGroup> _fileGroups = new();
    private string _searchStatusText = string.Empty;
    private bool _isSearchBusy;
    private CancellationTokenSource? _searchCancellation;
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _searchFilterDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private (List<string> Files, SearchIgnoreRules Rules)? _searchFileCache;
    private readonly FindHighlightRenderer _primaryFindHighlightRenderer = new();
    private readonly FindHighlightRenderer _secondaryFindHighlightRenderer = new();
    private FindHighlightRenderer _findHighlightRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryFindHighlightRenderer : _primaryFindHighlightRenderer;
    private readonly FindHighlightRenderer _primaryLspHighlightRenderer = new();
    private readonly FindHighlightRenderer _secondaryLspHighlightRenderer = new();
    private FindHighlightRenderer _lspHighlightRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryLspHighlightRenderer : _primaryLspHighlightRenderer;
    private readonly LspInlayHintRenderer _primaryLspInlayHintRenderer = new();
    private readonly LspInlayHintRenderer _secondaryLspInlayHintRenderer = new();
    private LspInlayHintRenderer _lspInlayHintRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryLspInlayHintRenderer : _primaryLspInlayHintRenderer;
    private readonly LspSemanticTokenRenderer _primaryLspSemanticTokenRenderer = new();
    private readonly LspSemanticTokenRenderer _secondaryLspSemanticTokenRenderer = new();
    private LspSemanticTokenRenderer _lspSemanticTokenRenderer => ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _secondaryLspSemanticTokenRenderer : _primaryLspSemanticTokenRenderer;
    private List<int> _findMatchOffsets = new();
    private int _currentFindMatchIndex = -1;
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
    private EventHandler<TerminalProcessHandle>? _activeSessionExitedHandler;
    private TerminalShellOption? _selectedTerminalShell;
    private bool _isTerminalVisible;
    private bool _isTerminalSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    private bool _isPSReadLinePredictionEnabled;
    private double _terminalPanelHeight = AppSettings.DefaultTerminalPanelHeight;
    private bool _isResizingTerminalPanel;
    private double _terminalPanelDragStartPointerY;
    private double _terminalPanelDragStartHeight;

    private double _explorerPanelWidth = AppSettings.DefaultExplorerPanelWidth;
    private bool _isResizingExplorerPanel;
    private double _explorerPanelDragStartPointerX;
    private double _explorerPanelDragStartWidth;
    private string? _deferredPreferredShellId;
    private static readonly IBrush SharedLinkTextBrush = Brush.Parse("#5BA3D9");

    private readonly Dictionary<LoadedExtension, KodoHighlightingDefinition> _highlightingCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<LoadedExtension, Lazy<CompiledSyntaxProfile>> _compiledSyntaxProfileCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, LoadedExtension?> _contentSniffCache =
        new(FileSystemPaths.Comparer);
    private string? _lastAppliedExtensionFingerprint;

    private LoadedExtension? _primaryAppliedSyntaxExtension;
    private LoadedExtension? _secondaryAppliedSyntaxExtension;
    private LoadedExtension? _appliedSyntaxExtension
    {
        get => ReferenceEquals(_activeEditorPane, _secondaryEditorPane)
            ? _secondaryAppliedSyntaxExtension
            : _primaryAppliedSyntaxExtension;
        set
        {
            if (ReferenceEquals(_activeEditorPane, _secondaryEditorPane))
                _secondaryAppliedSyntaxExtension = value;
            else
                _primaryAppliedSyntaxExtension = value;
        }
    }
    private readonly ColorSwatchElementGenerator _colorSwatchGenerator = new();
    private ColorSwatchElementGenerator? _secondaryColorSwatchGenerator;

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
            "Take a quick tour of the essentials, then make Kodo yours. It is five short steps, and you can revisit this tutorial any time from Settings.",
            "",
            "",
            "",
            "",
            ""
        ),
        new(
            "Welcome",
            "Start from Home",
            "Open a recent project, create a file, or choose a folder. Once a project is open, the explorer and editor keep your files close at hand.",
            "Ctrl+H",
            "Pick up where you left off",
            "Open a recent file or project from Home.",
            "Use New File for a quick scratch document.",
            "Open a folder to work with a project and its file tree."
        ),
        new(
            "Editing",
            "Make the editor yours",
            "Open a file or create a scratch document. Work across tabs, use the file explorer to move through a project, and tune the editor to suit your workflow.",
            "Ctrl+N / Ctrl+O / Ctrl+K",
            "A focused workspace",
            "Create a scratch file with Ctrl+N.",
            "Open a file with Ctrl+O or a project folder with Ctrl+K.",
            "Keep related files open in tabs as you work."
        ),
        new(
            "Marketplace",
            "Add the tools you need",
            "Find language support, themes, plugins, and compiler tools in the Marketplace. Install what you need for your stack and keep it updated from inside Kodo.",
            "Ctrl+E",
            "Extend Kodo",
            "Browse language support and themes.",
            "Install plugins to add commands and capabilities.",
            "Manage available updates from the Marketplace."
        ),
        new(
            "Settings",
            "Shape your workflow",
            "Set your theme, editor font, indentation, autosave, and other preferences. Settings are grouped so you can find and adjust them quickly.",
            "Ctrl+,",
            "Make Kodo feel right",
            "Choose a theme and accent colour.",
            "Adjust font size, indentation, and editor options.",
            "Set autosave and launch preferences."
        ),
        new(
            "Set up",
            "A few final choices",
            "Choose a theme and accent colour. Profile details are optional and can be changed later in Settings. To finish, read the Privacy Policy and choose whether to share anonymous usage data.",
            "Ctrl+,  ·  Settings",
            "Finish your setup",
            "Theme and accent colour apply throughout Kodo.",
            "Profile details help personalize messages on Home.",
            "Your profile is optional; the Privacy Policy and data choice must be resolved to continue."
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
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Kodo/{KodoDiagnostics.AppVersion} (https://github.com/Kodo-IDE/Kodo)");
        return client;
    }

    private string ExtensionsFolderPath => KodoPaths.ExtensionsDir;
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
            OnPropertyChanged();
            RaiseMany(nameof(HasImagePreview), nameof(IsImagePreviewVisible), nameof(IsTextEditorVisible), nameof(CanToggleSplitEditor), nameof(IsLineEndingVisible), nameof(LineEndingDisplayText), nameof(IsIndentationVisible), nameof(IndentationDisplayText), nameof(ImageZoomedWidth), nameof(ImageZoomedHeight), nameof(ImageZoomPercent));
            BitmapLifetime.DisposeAfterLayout(previousPreview);
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



            if (_activeEditorPane.ActiveTab is { } activeImageTab &&
                ReferenceEquals(_activeEditorPane.ImagePreviewTab, activeImageTab) &&
                _activeEditorPane.ImagePreview is not null)
                activeImageTab.GetViewState(_activeEditorPane.Id).ImageZoomLevel = clamped;
            OnPropertyChanged();
            RaiseMany(nameof(ImageZoomPercent), nameof(ImageZoomedWidth), nameof(ImageZoomedHeight),
                nameof(PrimaryPaneImageZoomedWidth), nameof(PrimaryPaneImageZoomedHeight), nameof(PrimaryPaneImageZoomPercent),
                nameof(SecondaryPaneImageZoomedWidth), nameof(SecondaryPaneImageZoomedHeight), nameof(SecondaryPaneImageZoomPercent));
        }
    }

    public string ImageZoomPercent => $"{(int)Math.Round(_imageZoomLevel * 100)}%";

    public double ImageZoomedWidth =>
        CurrentImagePreview is not null ? CurrentImagePreview.PixelSize.Width * _imageZoomLevel : 0;

    public double ImageZoomedHeight =>
        CurrentImagePreview is not null ? CurrentImagePreview.PixelSize.Height * _imageZoomLevel : 0;

    public EditorTab? ActiveEditorTab
    {
        get => _activeEditorPane.ActiveTab;
        private set
        {
            if (ReferenceEquals(_activeEditorPane.ActiveTab, value))
                return;

            ClearLspSignatureHelpTooltip();
            var previousTab = _activeEditorPane.ActiveTab;
            var otherPane = ReferenceEquals(_activeEditorPane, _editorPane)
                ? _secondaryEditorPane
                : _editorPane;
            if (previousTab is not null && !ReferenceEquals(previousTab, otherPane.ActiveTab))
                previousTab.IsSelected = false;

            _activeEditorPane.ActiveTab = value;
            RefreshPanePreviewState(_activeEditorPane);
            RestorePaneImageScrollOffset(_activeEditorPane, value);
            ImageZoomLevel = value?.GetViewState(_activeEditorPane.Id).ImageZoomLevel ?? 1.0;

            if (_activeEditorPane.ActiveTab is not null)
                _activeEditorPane.ActiveTab.IsSelected = true;

            RefreshPaneTabSelection();
            ClearDeadCodeHighlighting();
            ClearErrorHighlighting();
            ClearLspPresentationForEditor(EditorTextBox);
            _findHighlightRenderer.Clear();

            OnPropertyChanged();
            OnPropertyChanged(nameof(SecondaryPaneSelectedTab));
            OnPropertyChanged(nameof(PrimaryPaneBreadcrumbText));
            OnPropertyChanged(nameof(SecondaryPaneBreadcrumbText));
            NotifyPanePreviewStateChanged(_editorPane);
            NotifyPanePreviewStateChanged(_secondaryEditorPane);
            OnPropertyChanged(nameof(HasOpenEditors));
            OnPropertyChanged(nameof(IsEditorTabsVisible));
            OnPropertyChanged(nameof(IsSharedEditorTabsVisible));
            OnPropertyChanged(nameof(CanToggleSplitEditor));
            SaveSettings();
        }
    }

    public MainWindow() : this(null) { }

    private readonly System.Diagnostics.Stopwatch _startupStopwatch = System.Diagnostics.Stopwatch.StartNew();
    public MainWindow(string? startupFilePath)
    {
        _suppressSettingsSave = true;

        var trimmedStartupPath = startupFilePath?.Trim().Trim('"');
        _startupFilePath = !string.IsNullOrWhiteSpace(trimmedStartupPath)
            && (File.Exists(trimmedStartupPath) || Directory.Exists(trimmedStartupPath))
            ? trimmedStartupPath
            : null;
        _activeEditorPane = _editorPane;
        InitializeComponent();
        PrimaryEditorPane.AddHandler(
            InputElement.PointerPressedEvent,
            PrimaryEditorPane_OnPointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        SecondaryEditorPane.AddHandler(
            InputElement.PointerPressedEvent,
            SecondaryEditorPane_OnPointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _editorSession = new EditorDocumentSession(EditorTextBox.Document);
        PrimaryEditorTextBox.DocumentChanged += (_, e) => HandleEditorPaneDocumentChanged(_editorPane, PrimaryEditorTextBox, e.NewDocument);
        SecondaryEditorTextBox.DocumentChanged += (_, e) => HandleEditorPaneDocumentChanged(_secondaryEditorPane, SecondaryEditorTextBox, e.NewDocument);
        PrimaryEditorTextBox.TextChanged += EditorTextBox_OnPaneTextChanged;
        CenterQuickAccessSearchFrame();
        InsightWorkerGateway.Default.SetRegistryProvider(() =>
        {
            lock (LoadedExtensionsLock)
            {
                var snapshot = new List<LoadedExtension>(LoadedExtensions.Count);
                foreach (var ext in LoadedExtensions) snapshot.Add(ext);
                return (IReadOnlyList<LoadedExtension>)snapshot;
            }
        });
        AddHandler(PointerPressedEvent, OnWindowPointerPressedToDismissFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        _lspFoldingManager = AvaloniaEdit.Folding.FoldingManager.Install(EditorTextBox.TextArea);
        _secondaryLspFoldingManager = AvaloniaEdit.Folding.FoldingManager.Install(SecondaryEditorTextBox.TextArea);
        NotifySettingsSearchChanged();
        LoadWindowIcon();
        EditorTextBox.LineNumbersMargin = new Thickness(8, 0, 8, 0);
        EditorTextBox.FontFamily = KodoFonts.MonoFamily;
        EditorTextBox.TextArea.TextView.Options.AllowScrollBelowDocument = false;
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_indentGuideRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_deadCodeHighlightRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_errorHighlightRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_findHighlightRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_lspHighlightRenderer);
        EditorTextBox.TextArea.TextView.ElementGenerators.Add(_lspInlayHintRenderer);
        EditorTextBox.TextArea.TextView.BackgroundRenderers.Add(_lspSemanticTokenRenderer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_rainbowBracketColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_interpolatedStringColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_htmlEmbeddedColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_markdownColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_emojiTypefaceColorizer);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_deadCodeTextBrightener);
        EditorTextBox.TextArea.TextView.LineTransformers.Add(_errorTextDarkener);
        EditorTextBox.TextArea.TextView.LinkTextForegroundBrush = SharedLinkTextBrush;
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
        EditorTextBox.TextArea.TextView.PointerWheelChanged += EditorTextView_OnPointerWheelChanged;
        EditorTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) =>
        {
            HideDiagnosticPopup();


            UpdateEditorMinimapViewport(PrimaryEditorTextBox, EditorMinimapView);
            QueueLspViewportRefresh();
        }, RoutingStrategies.Bubble);
        OpenTabs.CollectionChanged += OpenTabs_CollectionChanged;
        TerminalSessions.CollectionChanged += TerminalSessions_CollectionChanged;
        TerminalHostControl.WorkingDirectoryChanged += TerminalHostControl_OnWorkingDirectoryChanged;
        TerminalHostControl.DetachedSessionExited += TerminalHostControl_OnDetachedSessionExited;
        FileTreeItems.CollectionChanged += FileTreeItems_CollectionChanged;
        _fileTreeRefreshTimer.Tick += FileTreeRefreshTimer_OnTick;
        _searchDebounceTimer.Tick += SearchDebounceTimer_OnTick;
        _searchFilterDebounceTimer.Tick += SearchFilterDebounceTimer_OnTick;
        EditorMinimapView.ScrollRequested += fraction => EditorMinimapView_OnScrollRequested(PrimaryEditorTextBox, fraction);
        SecondaryEditorMinimapView.ScrollRequested += fraction => EditorMinimapView_OnScrollRequested(SecondaryEditorTextBox, fraction);
        _editorMinimapRefreshTimer.Tick += EditorMinimapRefreshTimer_OnTick;
        ScheduleEditorMinimapRefresh();
        PrimaryEditorTextBox.TextArea.Caret.PositionChanged += (_, _) => EditorCaretPositionChanged(PrimaryEditorTextBox);
        ConfigureSecondaryEditorView();
        PrimaryEditorTextBox.GotFocus += (_, _) => ActivateEditorPane(_editorPane, focusEditor: false);
        SecondaryEditorTextBox.GotFocus += (_, _) => ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        PrimaryEditorTextBox.SizeChanged += EditorPaneSizeChanged;
        SecondaryEditorTextBox.SizeChanged += EditorPaneSizeChanged;
        SecondaryEditorTextBox.TextArea.Caret.PositionChanged += (_, _) => EditorCaretPositionChanged(SecondaryEditorTextBox);
        try
        {
            var taType = EditorTextBox.TextArea.GetType();
            var ev = taType.GetEvent("SelectionChanged");
            if (ev != null) ev.AddEventHandler(EditorTextBox.TextArea, new EventHandler((s, e) => QueueRefreshState()));
            if (ev != null) ev.AddEventHandler(SecondaryEditorTextBox.TextArea, new EventHandler((s, e) => QueueRefreshState()));
            var edType = EditorTextBox.GetType();
            var ev2 = edType.GetEvent("SelectionChanged");
            if (ev2 != null) ev2.AddEventHandler(EditorTextBox, new EventHandler((s, e) => QueueRefreshState()));
            if (ev2 != null) ev2.AddEventHandler(SecondaryEditorTextBox, new EventHandler((s, e) => QueueRefreshState()));
        }
        catch { }
        EditorTextBox.TextArea.GotFocus += (_, _) => QueueInsightRefresh();
        SecondaryEditorTextBox.TextArea.GotFocus += (_, _) => QueueInsightRefresh();
        Activated += (_, _) => QueueInsightRefresh();
        EditorTextBox.TextArea.TextEntering += EditorTextArea_OnTextEntering;
        EditorTextBox.TextArea.TextEntered += EditorTextArea_OnTextEntered;
        SecondaryEditorTextBox.TextArea.TextEntering += EditorTextArea_OnTextEntering;
        SecondaryEditorTextBox.TextArea.TextEntered += EditorTextArea_OnTextEntered;
        AddHandler(InputElement.KeyDownEvent, MainWindow_EditorKeyIntercept_OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        ImageScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            ImageScrollViewer_OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        ImageScrollViewer.ScrollChanged += ImageScrollViewer_OnScrollChanged;
        SecondaryImageScrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            ImageScrollViewer_OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        SecondaryImageScrollViewer.ScrollChanged += ImageScrollViewer_OnScrollChanged;
        _isFirstLaunch = !File.Exists(SettingsFilePath);
        var settings = LoadSettings();
        InitializeKeybinds(settings);
        _requestedThemeName = string.IsNullOrWhiteSpace(settings.ThemeName) ? "Dark" : settings.ThemeName;
        _isAutoSaveEnabled = settings.AutoSaveEnabled;
        _isDiscordRichPresenceEnabled = settings.DiscordRichPresenceEnabled;
        _isGitSourceControlEnabled = settings.GitSourceControlEnabled;
        _isDeveloperOptionsVisible = settings.DeveloperOptionsVisible;
        _isVerboseLoggingEnabled = settings.VerboseLoggingEnabled;
        KodoDiagnostics.VerboseLoggingEnabled = _isVerboseLoggingEnabled;
        _isStatusBarFilePathVisible = settings.StatusBarFilePathVisible;
        _isWordWrapEnabled = settings.WordWrapEnabled;
        _isMinimapVisible = settings.MinimapVisible;
        _isInsightEnabled = settings.InsightEnabled;
        _isInsightCodeSuggestionsEnabled = settings.InsightCodeSuggestionsEnabled;
        _isInsightDeadCodeEnabled = settings.InsightDeadCodeEnabled;
        _isInsightErrorDetectionEnabled = settings.InsightErrorDetectionEnabled;
        _insightBlacklistExtensions = string.IsNullOrWhiteSpace(settings.InsightBlacklistExtensions) ? ".txt,.md" : settings.InsightBlacklistExtensions;
        RebuildInsightBlacklist();
        _insightErrorDeadCodeBlacklistExtensions = string.IsNullOrWhiteSpace(settings.InsightErrorDeadCodeBlacklistExtensions) ? string.Empty : settings.InsightErrorDeadCodeBlacklistExtensions;
        RebuildErrorDeadCodeBlacklist();
        _dismissedDiagnostics = new HashSet<string>(settings.DismissedDiagnostics ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        _dismissedDiagnosticsRevision++;
        _isConfirmBeforeClosingUnsavedTabsEnabled = settings.ConfirmBeforeClosingUnsavedTabsEnabled;
        _isRestoreOpenTabsOnLaunchEnabled = settings.RestoreOpenTabsOnLaunchEnabled;
        _isAutoUpdateExtensionsEnabled = settings.AutoUpdateExtensionsEnabled;
        _isAutoUpdateExtensionsInBackgroundEnabled = settings.AutoUpdateExtensionsInBackgroundEnabled;
        _isAutoUpdateAppEnabled = settings.AutoUpdateAppEnabled;
        IsAutoUpdateAppInBackgroundEnabled1 = settings.AutoUpdateAppInBackgroundEnabled;
        _lspEnabled = settings.LspEnabled;
        _lspCompletionEnabled = settings.LspCompletionEnabled;
        _lspHoverEnabled = settings.LspHoverEnabled;
        _lspSignatureHelpEnabled = settings.LspSignatureHelpEnabled;
        _lspInlayHintsEnabled = settings.LspInlayHintsEnabled;
        _lspFoldingEnabled = settings.LspFoldingEnabled;
        _lspDocumentHighlightsEnabled = settings.LspDocumentHighlightsEnabled;
        _lspDiagnosticsEnabled = settings.LspDiagnosticsEnabled;
        _lspSemanticHighlightingEnabled = settings.LspSemanticHighlightingEnabled;
        _lspAutoInstall = settings.LspAutoInstall;
        _lspPreferManaged = settings.LspPreferManaged;
        _lspPreferSystem = settings.LspPreferSystem;
        _lspInstallDir = settings.LspInstallDir;
        foreach (var kv in settings.LspExecutableOverrides ?? new Dictionary<string, string>()) _lspExecutableOverrides[kv.Key] = kv.Value;
        foreach (var kv in settings.LspDisabledLanguages ?? new Dictionary<string, bool>()) _lspDisabledLanguages[kv.Key] = kv.Value;
        foreach (var id in settings.LspDismissedInstallPrompts ?? new HashSet<string>()) _lspDismissedInstallPrompts.Add(id);
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
        _insertSpaces = settings.InsertSpaces;
        _editorFontSize = settings.EditorFontSize is >= 8 and <= 32 ? settings.EditorFontSize : 14;
        _editorFontFamily = string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? "Default" : settings.EditorFontFamily;
        _terminalScrollbackLines = Math.Clamp(settings.TerminalScrollbackLines, 200, 200000);
        TerminalHostControl.MaxScrollbackLines = _terminalScrollbackLines;
        _terminalPanelHeight = TerminalShellSupport.NormalizeTerminalPanelHeight(settings.TerminalPanelHeight);
        _explorerPanelWidth = NormalizeExplorerPanelWidth(settings.ExplorerPanelWidth);
        _userCountry = string.IsNullOrWhiteSpace(settings.UserCountry)
            ? DetectCountryCode()
            : settings.UserCountry.ToUpperInvariant();
        UpdateLocalizedSpelling();
        _userHemisphere = settings.UserHemisphere is >= 0 and <= 2 ? settings.UserHemisphere : 0;
        _userTimezoneOffset = settings.UserTimezoneOffset ?? string.Empty;
        _userName = settings.UserName ?? string.Empty;
        _lastSeenVersion = settings.LastSeenVersion ?? string.Empty;
        _isTerminalVisible = settings.TerminalVisible && IsTerminalSupported;
        _startupOpenTabPaths.AddRange(settings.OpenTabPaths
            .Where(path => File.Exists(path))
            .Distinct(FileSystemPaths.Comparer));
        _startupActiveTabPath = settings.ActiveTabPath;
        _startupSplitEditorVisible = settings.SplitEditorVisible;
        _startupPrimaryPaneTabPath = settings.PrimaryPaneTabPath;
        _startupSecondaryPaneTabPath = settings.SecondaryPaneTabPath;
        _startupPrimaryPaneTabPaths.AddRange((settings.PrimaryPaneTabPaths ?? [])
            .Where(File.Exists)
            .Distinct(FileSystemPaths.Comparer));
        _startupSecondaryPaneTabPaths.AddRange((settings.SecondaryPaneTabPaths ?? [])
            .Where(File.Exists)
            .Distinct(FileSystemPaths.Comparer));
        _startupActivePaneIsSecondary = settings.ActivePaneIsSecondary;
        _startupFolderPath = !string.IsNullOrWhiteSpace(settings.LastOpenedFolderPath) &&
                              Directory.Exists(settings.LastOpenedFolderPath)
            ? settings.LastOpenedFolderPath
            : null;
        LoadRecentFiles(settings.RecentFiles);
        _isPSReadLinePredictionEnabled = settings.PSReadLinePredictionEnabled;
        foreach (var pair in settings.CustomRunCommands ?? new Dictionary<string, string>())
            _customRunCommands[pair.Key] = pair.Value;
        foreach (var pair in settings.CustomBuildCommands ?? new Dictionary<string, string>())
            _customBuildCommands[pair.Key] = pair.Value;
        foreach (var pair in settings.CompilerOverrides ?? new Dictionary<string, string>())
            _compilerOverrides[pair.Key] = pair.Value;
        foreach (var pair in settings.CustomBuildScripts ?? new Dictionary<string, string>())
            _customBuildScripts[pair.Key] = pair.Value;
        _deferredPreferredShellId = settings.PreferredTerminalShellId;
        _autoSaveTimer.Tick += AutoSaveTimer_OnTick;
        _autoSaveStatusTimer.Tick += AutoSaveStatusTimer_OnTick;
        _discordReconnectTimer.Tick += DiscordReconnectTimer_OnTick;
        _editorStateRefreshTimer.Tick += EditorStateRefreshTimer_OnTick;
        _wordCountRefreshTimer.Tick += WordCountRefreshTimer_OnTick;
        _InsightRefreshTimer.Tick += InsightRefreshTimer_OnTick;
        _diagnosticPopupHideTimer.Tick += DiagnosticPopupHideTimer_OnTick;
        _diagnosticPopupShowTimer.Tick += DiagnosticPopupShowTimer_OnTick;
        _lspRefreshDebounceTimer.Tick += LspRefreshDebounceTimer_OnTick;
        _findHighlightDebounceTimer.Tick += FindHighlightDebounceTimer_OnTick;
        _settingsSaveDebounceTimer.Tick += SettingsSaveDebounceTimer_OnTick;
        _extensionsRefreshDebounceTimer.Tick += ExtensionsRefreshDebounceTimer_OnTick;
        _extensionAutoUpdateTimer.Tick += ExtensionAutoUpdateTimer_OnTick;
        InitLspDocumentSync();
        _appUpdateScheduler = new AppUpdateScheduler(
            isEnabled: () => IsAutoUpdateAppEnabled,
            isManualCheckInProgress: () => IsCheckingForUpdatesManually,
            installInBackground: () => IsAutoUpdateAppInBackgroundEnabled);
        _marketplaceRefreshTimer.Tick += MarketplaceRefreshTimer_OnTick;

        EnsureExtensionsFolder();
        try
        {
            var themeScan = _cachedThemeScan is not null && DateTime.UtcNow - _cachedThemeScanUtc < ThemeScanCacheValidity
                ? _cachedThemeScan
                : ScanInstalledThemeExtensions();
            if (themeScan.Extensions.Count > 0)
            {
                ApplyLoadedExtensionsResult(themeScan);
                _cachedThemeScan = themeScan;
                _cachedThemeScanUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Theme preload failed", ex); }
        ApplyThemeBrushes(_requestedThemeName);

        var extensionStartupLoading = BeginAppLoading("Loading extensions");
        _initialExtensionScanTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50);
                var scan = await Task.Run(() => ScanInstalledExtensions()).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyLoadedExtensionsResult(scan);
                    RefreshCurrentFileSyntaxHighlighting();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex) { KodoDiagnostics.LogDebug("Deferred LoadExtensions failed", ex); }
            finally { extensionStartupLoading.Dispose(); }
        });

        Closed += (_, _) =>
        {
            _lspRefreshDebounceTimer.Stop();
            _findHighlightDebounceTimer.Stop();
        };

        if (_accentColorMode == "kodo" && _hasThemeAccent)
            _accentColorMode = "theme";

        DataContext = this;
        IsHomePageVisible = true;

        ApplyEditorSettings();
        Opened += MainWindow_OnOpened;
        Activated += (_, _) => _stallWatchdog.Reset();
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        RefreshState(fullRefresh: true);
        KodoDiagnostics.LogDebug($"Startup ctor fast-path: {_startupStopwatch.ElapsedMilliseconds}ms (extensions deferred)");
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
            if (!forceNetwork && await LoadCachedLatestReleaseAsync())
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
            if (!forceNetwork && await LoadCachedAnnouncementsAsync())
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

    private async Task<bool> LoadCachedAnnouncementsAsync()
    {
        try
        {
            var items = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(NewsCachePath))
                        return null;
                    var json = File.ReadAllText(NewsCachePath);
                    return JsonSerializer.Deserialize<List<NewsItem>>(json);
                }
                catch { return null; }
            }).ConfigureAwait(false);

            if (items is null || items.Count == 0)
                return false;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in items)
                    NewsItems.Add(item);
            }, DispatcherPriority.Background);
            return true;
        }
        catch { return false; }
    }

    private void SaveNewsCache()
    {
        var snapshot = NewsItems.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(NewsCachePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(NewsCachePath, json);
            }
            catch { }
        });
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

    private async Task<bool> LoadCachedLatestReleaseAsync()
    {
        try
        {
            var cached = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(LatestReleaseCachePath))
                        return null;
                    var json = File.ReadAllText(LatestReleaseCachePath);
                    return JsonSerializer.Deserialize<ReleaseInfo>(json);
                }
                catch { return null; }
            }).ConfigureAwait(false);

            if (cached is null ||
                (string.IsNullOrWhiteSpace(cached.Name) &&
                 string.IsNullOrWhiteSpace(cached.Tag) &&
                 string.IsNullOrWhiteSpace(cached.Notes)))
                return false;

            await Dispatcher.UIThread.InvokeAsync(() => LatestRelease = cached, DispatcherPriority.Background);
            return true;
        }
        catch { return false; }
    }

    private void SaveLatestReleaseCache()
    {
        var snapshot = LatestRelease;
        if (snapshot is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(LatestReleaseCachePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(LatestReleaseCachePath, json);
            }
            catch { }
        });
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
                Title = title ?? string.Empty,
                Body = string.Join("\n", bodyLines).Trim(),
                UpdatedAt = updatedAt ?? string.Empty,
            });
        }

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

    private static string BuildGitHubContentsUrl(string owner, string repo, string path) =>
        $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return FileSystemPaths.IsPrefixOf(normalizedPath, normalizedDirectory);
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
            var repo = segments[1];
            var branch = segments[3];
            var path = string.Join("/", segments, 4, segments.Length - 4);
            return $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}";
        }

        return url;
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

    private ScrollViewer? _editorHorizontalScrollViewer;
    private bool _editorHorizontalScrollViewerResolved;
    private bool _caretVisibilityRefreshPending;
    private TextEditor? _caretVisibilityRefreshEditor;

    private ScrollViewer? EditorHorizontalScrollViewer()
    {
        if (_editorHorizontalScrollViewerResolved && _editorHorizontalScrollViewer is not null)
            return _editorHorizontalScrollViewer;
        if (EditorTextBox is null) return null;
        try
        {
            _editorHorizontalScrollViewer = EditorTextBox
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
        }
        catch { _editorHorizontalScrollViewer = null; }
        _editorHorizontalScrollViewerResolved = true;
        return _editorHorizontalScrollViewer;
    }

    private const double EditorHorizontalScrollStep = 120;
    private const double EditorHorizontalScrollPageStep = 480;
    private const double EditorHorizontalWheelPixelsPerDelta = 360;
    private const double EditorHorizontalWheelMaxDelta = 4;

    private bool ScrollEditorHorizontally(double pixels)
    {
        var viewer = EditorHorizontalScrollViewer();
        if (viewer is null) return false;

        var max = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        if (max <= 0) return false;

        var current = viewer.Offset.X;
        var target = Math.Clamp(current + pixels, 0, max);
        if (Math.Abs(target - current) < 0.5) return false;

        viewer.Offset = new Vector(target, viewer.Offset.Y);
        return true;
    }

    private bool ScrollEditorHorizontallyByPage(double pages) =>
        ScrollEditorHorizontally(pages * EditorHorizontalScrollPageStep);

    private bool HandleEditorHorizontalScrollGesture(PointerWheelEventArgs e)
    {
        double scaled;
        if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            scaled = Math.Clamp(e.Delta.X, -EditorHorizontalWheelMaxDelta, EditorHorizontalWheelMaxDelta);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            scaled = Math.Clamp(-e.Delta.Y, -EditorHorizontalWheelMaxDelta, EditorHorizontalWheelMaxDelta);
        else
            return false;
        if (scaled == 0) return false;
        return ScrollEditorHorizontally(scaled * EditorHorizontalWheelPixelsPerDelta);
    }

    private void ApplyEditorSettings()
    {
        ApplyEditorSettings(PrimaryEditorTextBox);
        ApplyEditorSettings(SecondaryEditorTextBox);
        _primaryIndentGuideRenderer.TabSize = TabSize;
        _secondaryIndentGuideRenderer.TabSize = TabSize;
        _primaryMarkdownColorizer.TabSize = TabSize;
        _secondaryMarkdownColorizer.TabSize = TabSize;
        PrimaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
        SecondaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);

    }

    private void ApplyEditorSettings(TextEditor? editor)
    {
        if (editor is null) return;
        editor.WordWrap = IsWordWrapEnabled;
        editor.Options.IndentationSize = TabSize;
        editor.Options.ConvertTabsToSpaces = InsertSpaces;
        editor.FontSize = EditorFontSize;
        editor.FontFamily = KodoFonts.ResolveEditorFamily(EditorFontFamily);
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
        var presentationWatch = System.Diagnostics.Stopwatch.StartNew();
        var previewWatch = System.Diagnostics.Stopwatch.StartNew();
        var imagePreview = TryLoadImagePreview(_currentFilePath);
        previewWatch.Stop();
        KodoDiagnostics.ReportSlowStage("presentation/image preview", previewWatch.ElapsedMilliseconds, 500, $"path={_currentFilePath}");
        CurrentImagePreview = imagePreview;

        if (imagePreview is not null)
        {
            SetFileCorrupted(false);
            CurrentLanguageExtension = null;
            ClearEditorSyntaxState();
            return;
        }

        var stageWatch = System.Diagnostics.Stopwatch.StartNew();
        RefreshCurrentFileSyntaxHighlighting(stageWatch);
        presentationWatch.Stop();
        KodoDiagnostics.ReportSlowStage("document presentation", presentationWatch.ElapsedMilliseconds, 1000, $"path={_currentFilePath}");
    }

    private void RefreshCurrentFileSyntaxHighlighting(System.Diagnostics.Stopwatch? stageWatch = null)
    {
        if (EditorTextBox is null)
            return;

        stageWatch ??= System.Diagnostics.Stopwatch.StartNew();
        stageWatch.Restart();
        RefreshRunBuildState();
        stageWatch.Stop();
        KodoDiagnostics.ReportSlowStage("presentation/run build state", stageWatch.ElapsedMilliseconds, 500, $"path={_currentFilePath}");

        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            CurrentLanguageExtension = null;
            ClearEditorSyntaxState();
            _indentGuideRenderer.IsEnabled = false;
            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return;
        }

        stageWatch.Restart();
        var langExt = GetLanguageExtension(_currentFilePath);
        stageWatch.Stop();
        KodoDiagnostics.ReportSlowStage("presentation/language extension", stageWatch.ElapsedMilliseconds, 500, $"path={_currentFilePath}");

        CurrentLanguageExtension = langExt;

        _indentGuideRenderer.IsEnabled = langExt is not null;
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        if (langExt is null)
        {
            ClearEditorSyntaxState();
        }
        else
        {
            stageWatch.Restart();
            ApplySyntaxHighlighting(langExt);
            stageWatch.Stop();
            KodoDiagnostics.ReportSlowStage("presentation/apply syntax", stageWatch.ElapsedMilliseconds, 500, $"path={_currentFilePath}");
        }
    }

    private void ClearEditorSyntaxState()
    {
        if (EditorTextBox is null)
            return;

        _appliedSyntaxExtension = null;
        EditorTextBox.SyntaxHighlighting = null;
        ConfigureRainbowBrackets(null);
        ConfigureInterpolatedStrings(null);
        ConfigureHtmlEmbeddedHighlighting(null);
        ConfigureMarkdownHighlighting(null);
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
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
        RaiseMany(nameof(IsCorruptedFileViewVisible), nameof(IsTextEditorVisible), nameof(CanToggleSplitEditor), nameof(CanShowFindInFile), nameof(CanShowSearchPanel), nameof(IsSearchPanelActive), nameof(CanShowSaveActions), nameof(IsLineEndingVisible), nameof(LineEndingDisplayText), nameof(IsIndentationVisible), nameof(IndentationDisplayText));
    }

    private void ConfigureInterpolatedStrings(CompiledSyntaxProfile? syntaxProfile)
    {
        _interpolatedStringColorizer.UpdateSyntax(syntaxProfile);
    }

    private static readonly string[] EditorPageVisibilityDependents =
    [
        nameof(IsEditorPageVisible), nameof(IsHomeOrEditorPageVisible), nameof(IsSearchPanelActive),
        nameof(IsEditorTabsVisible), nameof(IsSharedEditorTabsVisible), nameof(IsDocumentViewVisible), nameof(IsEmptyStateVisible),
        nameof(IsTextEditorVisible), nameof(CanToggleSplitEditor),
        nameof(IsPrimaryPaneImagePreviewVisible), nameof(IsSecondaryPaneImagePreviewVisible),
        nameof(IsPrimaryPaneCorruptedFileViewVisible), nameof(IsSecondaryPaneCorruptedFileViewVisible),
        nameof(IsPrimaryPaneTextEditorVisible), nameof(IsSecondaryPaneTextEditorVisible),
        nameof(IsPrimaryPaneMinimapVisible), nameof(IsSecondaryPaneMinimapVisible)
    ];

    public bool IsSettingsPageVisible
    {
        get => _isSettingsPageVisible;
        set
        {
            if (_isSettingsPageVisible == value) return;
            _isSettingsPageVisible = value;
            OnPropertyChanged();
            RaiseMany(EditorPageVisibilityDependents);
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
            RaiseMany(EditorPageVisibilityDependents);
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
            OnPropertyChanged(nameof(AreSidebarButtonsEnabled));
            RaiseMany(EditorPageVisibilityDependents);
        }
    }

    public bool AreSidebarButtonsEnabled =>
        !(_isFirstLaunch && !_hasCompletedTutorial && IsTutorialPageVisible);

    private bool IsFirstRunTutorialActive =>
        _isFirstLaunch && !_hasCompletedTutorial && IsTutorialPageVisible;

    public bool IsWhatsNewPageVisible
    {
        get => _isWhatsNewPageVisible;
        set
        {
            if (_isWhatsNewPageVisible == value) return;
            _isWhatsNewPageVisible = value;
            OnPropertyChanged();
            RaiseMany(EditorPageVisibilityDependents);
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
            RaiseMany(nameof(IsEditorPageVisible), nameof(IsHomeOrEditorPageVisible), nameof(IsSearchPanelActive), nameof(IsEditorTabsVisible), nameof(IsSharedEditorTabsVisible), nameof(IsEmptyStateVisible), nameof(IsDocumentViewVisible), nameof(FileSummaryText), nameof(FilePathText), nameof(LanguageDisplayText), nameof(CanShowSaveActions));
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
                OnPropertyChanged(nameof(CanFinishTutorial));
                OnPropertyChanged(nameof(CanAdvanceTutorial));
                OnPropertyChanged(nameof(CanSkipTutorial));
                OnPropertyChanged(nameof(TutorialSetupStatusText));
            }

            SaveSettings();
        }
    }

    public bool IsDataTrackingPromptVisible => !_hasRespondedToDataTrackingPrompt;

    public bool IsPrivacyPolicyPromptVisible => !_hasAcceptedPrivacyPolicy;

    public bool AreAllConsentPromptsResolved => !IsDataTrackingPromptVisible && !IsPrivacyPolicyPromptVisible;

    public bool CanFinishTutorial => AreAllConsentPromptsResolved;

    public bool CanAdvanceTutorial => !IsTutorialSetupStep || CanFinishTutorial;

    public bool CanSkipTutorial => !IsTutorialSetupStep || CanFinishTutorial;

    public string TutorialSetupStatusText => CanFinishTutorial
        ? "Setup complete. You’re ready to start coding."
        : "Finish is locked until you reach the end of the Privacy Policy, accept it, and choose a data-sharing option.";

    public string PrivacyPolicyScrollStatusText => IsPrivacyPolicyScrolledToBottom
        ? "End reached. Accept the policy below to continue."
        : "Keep scrolling to the bottom. Accept unlocks when you reach the end.";

    public bool IsPrivacyPolicyScrolledToBottom
    {
        get => _isPrivacyPolicyScrolledToBottom;
        set
        {
            if (_isPrivacyPolicyScrolledToBottom == value) return;
            _isPrivacyPolicyScrolledToBottom = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrivacyPolicyScrollStatusText));
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
            CenterQuickAccessSearchFrame();
            SaveSettings();
        }
    }

    private const double MinExplorerPanelWidth = 180;
    private const double MaxExplorerPanelWidth = 600;

    internal const double FileTreeRowFixedOverhead = 16 + 2 + 32 + 6 + 8 + 10 + 36;

    public bool IsFileExplorerVisible
    {
        get => _isFileExplorerVisible;
        private set
        {
            if (_isFileExplorerVisible == value) return;
            _isFileExplorerVisible = value;
            OnPropertyChanged();
            CenterQuickAccessSearchFrame();
        }
    }

    private void CenterQuickAccessSearchFrame()
    {
        if (this.FindControl<Border>("QuickAccessSearchFrame") is not { } frame)
            return;

        var leftSidebarWidth = 60d + (IsFileExplorerVisible ? ExplorerPanelWidth + 4d : 0d);
        frame.RenderTransform = new TranslateTransform(-leftSidebarWidth / 2, 0);
    }

    public bool IsEditorPageVisible => !IsHomePageVisible && !IsSettingsPageVisible && !IsExtensionsPageVisible && !IsTutorialPageVisible && !IsWhatsNewPageVisible;

    public bool IsHomeOrEditorPageVisible => IsHomePageVisible || IsEditorPageVisible;

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
    public bool IsSharedEditorTabsVisible => IsEditorTabsVisible && !IsSplitScreenVisible;

    public bool CanShowSaveActions => IsTextEditorVisible;

    public string WhatsNewToggleText => IsWhatsNewExpanded ? "Hide release notes" : "Show release notes";

    public string WhatsNewToggleGlyph => IsWhatsNewExpanded ? "▾" : "▸";

    public bool HasFileOpen => _currentFilePath is not null;

    public bool IsFolderOpen => _currentFolderPath is not null;

    public string? WorkspaceFolderPath => _currentFolderPath;

    public bool IsEmptyStateVisible => IsHomePageVisible || (IsEditorPageVisible && !HasDocumentOpen);

    public bool HasRecentFiles => RecentFiles.Count > 0;

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
            .Select(g => new ThemeExtensionGroup(g.Key, g.ToList()))
            .OrderBy(g => g.IsMultiTheme)
            .ThenBy(g => g.GroupName)
            .ToList();

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

    public string CurrentAppVersionDisplay => UpdateService.InstalledVersionDisplay();

    private bool _updateBannerDismissed;
    private bool _extensionUpdateBannerDismissed;

    private static bool IsDevBuild => UpdateService.IsDevelopmentBuild;

    private static int VersionPriority(string tag)
    {
        var dash = tag.IndexOf('-');
        if (dash < 0) return 5;

        var suffix = tag[dash..];
        if (suffix.Contains("dev", StringComparison.OrdinalIgnoreCase)) return 0;
        if (suffix.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return 1;
        if (suffix.Contains("beta", StringComparison.OrdinalIgnoreCase)) return 2;
        if (suffix.Contains("rc", StringComparison.OrdinalIgnoreCase)) return 4;

        return 3;
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

        if (!Version.TryParse(StripPreRelease(lastSeen), out var seen)) return false;
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
            if (!Version.TryParse(StripPreRelease(LatestReleaseTag), out var latest)) return false;

            if (latest != current) return latest > current;

            return VersionPriority(LatestReleaseTag) > VersionPriority(CurrentAppVersion);
        }
    }

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

    private object? _releaseNotesCacheKey;
    private string _releaseNotesDisplayText = string.Empty;
    private string _releaseNotesPreview = string.Empty;
    private IReadOnlyList<FormattedParagraph> _releaseNotesParagraphs = [];
    private IReadOnlyList<ReleaseLinkItem> _releaseNotesLinks = [];

    private void EnsureReleaseNotesCache()
    {
        var release = LatestRelease;
        var key = (object?)release ?? string.Empty;
        if (ReferenceEquals(_releaseNotesCacheKey, key)) return;

        _releaseNotesCacheKey = key;
        var notes = release?.Notes ?? string.Empty;

        if (string.IsNullOrWhiteSpace(notes))
        {
            _releaseNotesDisplayText = "No release notes available.";
            _releaseNotesPreview = _releaseNotesDisplayText;
            _releaseNotesParagraphs = [new FormattedParagraph { Runs = [new FormattedRun { Text = _releaseNotesDisplayText }] }];
            _releaseNotesLinks = [];
            return;
        }

        _releaseNotesDisplayText = ConvertMarkdownToDisplayText(notes);
        _releaseNotesParagraphs = ParseMarkdownParagraphs(notes);
        _releaseNotesLinks = ExtractReleaseLinks(notes);

        var flattened = _releaseNotesDisplayText.Replace("\r\n", "\n").Replace('\r', '\n');
        _releaseNotesPreview = flattened.Length > 220 ? flattened[..220].TrimEnd() + "..." : flattened;
    }

    public string LatestReleaseNotes
    {
        get { EnsureReleaseNotesCache(); return _releaseNotesDisplayText; }
    }

    public IReadOnlyList<FormattedParagraph> LatestReleaseFormatted
    {
        get { EnsureReleaseNotesCache(); return _releaseNotesParagraphs; }
    }

    public string LatestReleasePreview
    {
        get { EnsureReleaseNotesCache(); return _releaseNotesPreview; }
    }

    public string LatestReleaseUrl => LatestRelease?.Url ?? ReleasesPageUrl;

    public IReadOnlyList<ReleaseLinkItem> LatestReleaseLinks
    {
        get { EnsureReleaseNotesCache(); return _releaseNotesLinks; }
    }

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

    private static readonly Regex MdCodeFenceRegex = new(@"```(?:[\w#+.-]+)?\n?", RegexOptions.Compiled);
    private static readonly Regex MdInlineCodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex MdImageRegex = new(@"!\[([^\]]*)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex MdLinkRegex = new(@"\[(.*?)\]\((.*?)\)", RegexOptions.Compiled);
    private static readonly Regex MdHeadingRegex = new(@"(?m)^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex MdBlockquoteRegex = new(@"(?m)^\s{0,3}>\s?", RegexOptions.Compiled);
    private static readonly Regex MdHrRegex = new(@"(?m)^\s*[-*_]{3,}\s*$", RegexOptions.Compiled);
    private static readonly Regex MdBulletRegex = new(@"(?m)^\s*[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex MdOrderedListRegex = new(@"(?m)^\s*(\d+)\.\s+", RegexOptions.Compiled);
    private static readonly Regex MdBoldRegex = new(@"(?<!\*)\*\*(?!\*)(.*?)\*\*(?<!\*)", RegexOptions.Compiled);
    private static readonly Regex MdItalicRegex = new(@"(?<!\*)\*(?!\*)(.*?)\*(?<!\*)", RegexOptions.Compiled);
    private static readonly Regex MdBoldUnderscoreRegex = new(@"__(.*?)__", RegexOptions.Compiled);
    private static readonly Regex MdItalicUnderscoreRegex = new(@"(?<!_)_(?!_)(.*?)(?<!_)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex MdStrikethroughRegex = new(@"~~(.*?)~~", RegexOptions.Compiled);
    private static readonly Regex MdTableLeadingPipeRegex = new(@"(?m)^\s*\|", RegexOptions.Compiled);
    private static readonly Regex MdTableTrailingPipeRegex = new(@"(?m)\|\s*$", RegexOptions.Compiled);
    private static readonly Regex MdTablePipeRegex = new(@"\|", RegexOptions.Compiled);
    private static readonly Regex MdExcessNewlinesRegex = new(@"\n{3,}", RegexOptions.Compiled);

    private static readonly Regex MdInlineBoldRegex = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled | RegexOptions.Singleline);

    private const double MdListIndentWidth = 20.0;

    private static int IndentLevelFromMarkerMatch(string markerMatchValue)
    {
        var leadingSpaces = markerMatchValue.Length - markerMatchValue.TrimStart(' ').Length;
        return Math.Min(leadingSpaces / 2, 4);
    }

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

            line = line.Replace("\t", "    ");

            var isBullet = false;
            var isOrdered = false;
            string? orderedPrefix = null;
            var indentLevel = 0;

            var bulletMatch = MdBulletRegex.Match(line);
            if (bulletMatch.Success)
            {
                isBullet = true;
                indentLevel = IndentLevelFromMarkerMatch(bulletMatch.Value);
                line = line[bulletMatch.Length..];
            }
            else
            {
                var orderedMatch = MdOrderedListRegex.Match(line);
                if (orderedMatch.Success)
                {
                    isOrdered = true;
                    orderedPrefix = orderedMatch.Groups[1].Value + ".";
                    indentLevel = IndentLevelFromMarkerMatch(orderedMatch.Value);
                    line = line[orderedMatch.Length..];
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

            line = MdItalicRegex.Replace(line, "$1");
            line = MdItalicUnderscoreRegex.Replace(line, "$1");

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

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

            var leftMargin = (isBullet || isOrdered) ? indentLevel * MdListIndentWidth : 0.0;

            paragraphs.Add(new FormattedParagraph
            {
                Runs = runs,
                TopMargin = isBullet || isOrdered ? new Thickness(leftMargin, 2, 0, 0) : new Thickness(0, 6, 0, 0),
                Marker = marker,
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

    public bool IsMarketplaceUnavailableVisible => MarketplaceExtensions.Count == 0 && IsMarketplaceConnectivityWarningVisible;
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
        "theme" => "No themes in the Marketplace right now.",
        "plugin" => "No plugins in the Marketplace right now.",
        _ => "The Marketplace has no extensions listed right now."
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

    public string TutorialStepLabel => TutorialStepIndex == 0
        ? "WELCOME"
        : $"STEP {TutorialStepIndex} OF {TutorialSteps.Length - 1}";

    public string TutorialProgressDotsText =>
        string.Join(" ", Enumerable.Range(0, TutorialSteps.Length).Select(index => index <= TutorialStepIndex ? "●" : "○"));

    public double TutorialProgressPercent => TutorialSteps.Length <= 1
        ? 100
        : TutorialStepIndex * 100.0 / (TutorialSteps.Length - 1);

    public string TutorialSectionTitle => CurrentTutorialStep.SectionTitle;

    public string TutorialTitle => CurrentTutorialStep.Title;

    public string TutorialBody => IsAmericanEnglish
        ? CurrentTutorialStep.Body.Replace("accent colour", "accent color")
        : CurrentTutorialStep.Body;

    public string TutorialShortcutText => CurrentTutorialStep.Shortcut;

    public string TutorialSpotlightTitle => CurrentTutorialStep.SpotlightTitle;

    public string TutorialHighlightOne => CurrentTutorialStep.HighlightOne;

    public string TutorialHighlightTwo => (!IsAmericanEnglish && TutorialStepIndex == 4)
        ? CurrentTutorialStep.HighlightTwo.Replace("tab behavior", "tab behaviour")
        : CurrentTutorialStep.HighlightTwo;

    public string TutorialHighlightThree => IsAmericanEnglish
        ? CurrentTutorialStep.HighlightThree.Replace("accent colour", "accent color")
        : CurrentTutorialStep.HighlightThree;

    public bool CanGoToPreviousTutorialStep => TutorialStepIndex > 0;

    public bool IsTutorialSetupStep => TutorialStepIndex == TutorialSteps.Length - 1;
    public bool IsNotTutorialSetupStep => !IsTutorialSetupStep;

    public bool IsTutorialWelcomeStep => TutorialStepIndex == 0;
    public bool IsNotTutorialWelcomeStep => !IsTutorialWelcomeStep;

    public bool IsTutorialHeaderVisible => _tutorialOpenedFromSettings;

    public string TutorialPrimaryButtonText => IsTutorialSetupStep ? "Finish setup" : "Next step";

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
        }
    }

    public bool IsGitSourceControlEnabled
    {
        get => _isGitSourceControlEnabled;
        set
        {
            if (_isGitSourceControlEnabled == value) return;
            _isGitSourceControlEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            UpdateGitSourceControlLifecycle();
        }
    }

    public bool IsGitSourceControlPanelOpen { get; private set; }

    internal void SetGitSourceControlPanelOpen(bool isOpen)
    {
        if (IsGitSourceControlPanelOpen == isOpen) return;
        IsGitSourceControlPanelOpen = isOpen;
        OnPropertyChanged(nameof(IsGitSourceControlPanelOpen));
    }

    private void GitSourceControlActivityButton_OnClick(object? sender, RoutedEventArgs e) =>
        _gitSourceControlIntegration?.ToggleSourcePanelFromHost();

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
            RaiseMany(nameof(FilePathText), nameof(LanguageDisplayText), nameof(StatusBarFilePathVisibilityText), nameof(ActiveTerminalWorkingDirectory), nameof(ActiveTerminalFooterText), nameof(PrimaryPaneBreadcrumbText), nameof(SecondaryPaneBreadcrumbText));
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

    public bool IsMinimapVisible
    {
        get => _isMinimapVisible;
        set
        {
            if (_isMinimapVisible == value) return;
            _isMinimapVisible = value;
            OnPropertyChanged();
            RaiseMany(nameof(IsPrimaryPaneMinimapVisible), nameof(IsSecondaryPaneMinimapVisible));
            SaveSettings();
            if (value) ScheduleEditorMinimapRefresh();
            else
            {
                _editorMinimapRefreshTimer.Stop();
                EditorMinimapView?.SetDocument(null);
                SecondaryEditorMinimapView?.SetDocument(null);
            }
        }
    }

    private void ScheduleEditorMinimapRefresh()
    {
        _editorMinimapRefreshTimer.Stop();
        if (IsMinimapVisible)
            _editorMinimapRefreshTimer.Start();
    }

    private void EditorMinimapRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _editorMinimapRefreshTimer.Stop();
        if (!IsMinimapVisible)
        {
            EditorMinimapView?.SetDocument(null);
            SecondaryEditorMinimapView?.SetDocument(null);
            return;
        }

        EditorMinimapView?.SetDocument(PrimaryEditorTextBox?.Document);
        SecondaryEditorMinimapView?.SetDocument(IsSplitScreenVisible ? SecondaryEditorTextBox?.Document : null);
        UpdateEditorMinimapViewport(PrimaryEditorTextBox, EditorMinimapView);
        if (IsSplitScreenVisible)
            UpdateEditorMinimapViewport(SecondaryEditorTextBox, SecondaryEditorMinimapView);
    }

    private void UpdateEditorMinimapViewport()
    {
        UpdateEditorMinimapViewport(PrimaryEditorTextBox, EditorMinimapView);
        if (IsSplitScreenVisible)
            UpdateEditorMinimapViewport(SecondaryEditorTextBox, SecondaryEditorMinimapView);
    }

    private void EditorPaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not TextEditor || _editorPaneSizeRefreshPending)
            return;

        _editorPaneSizeRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _editorPaneSizeRefreshPending = false;
            UpdateEditorMinimapViewport();
            if (IsSplitScreenVisible)
                QueueLspViewportRefresh();
        }, DispatcherPriority.Background);
    }

    private void UpdateEditorMinimapViewport(TextEditor? editor, Kodo.Renderers.EditorMinimapControl? minimap)
    {
        if (ReferenceEquals(editor, SecondaryEditorTextBox) && !IsSplitScreenVisible)
        {
            minimap?.SetDocument(null);
            return;
        }
        if (!IsMinimapVisible || editor?.Document is not { } document || editor.TextArea?.TextView is not { } textView || minimap is null)
            return;

        if (textView.VisualLinesValid && textView.VisualLines.Count > 0)
        {
            var top = textView.VisualLines[0].FirstDocumentLine.LineNumber;
            var bottom = textView.VisualLines[^1].LastDocumentLine.LineNumber;
            minimap?.SetViewport(top, Math.Max(top, bottom));
            return;
        }

        var topPosition = textView.GetPositionFloor(new Point(textView.ScrollOffset.X, textView.ScrollOffset.Y));
        var topLine = Math.Clamp(topPosition?.Line ?? 1, 1, document.LineCount);
        var visibleLines = Math.Max(1, (int)Math.Ceiling(textView.Bounds.Height / Math.Max(1, textView.DefaultLineHeight)));
        minimap?.SetViewport(topLine, Math.Min(document.LineCount, topLine + visibleLines));
    }

    private void EditorMinimapView_OnScrollRequested(TextEditor editor, double fraction)
    {
        if (editor?.Document is not { } document) return;
        if (IsSplitScreenVisible)
        {
            if (ReferenceEquals(editor, SecondaryEditorTextBox))
                ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
            else if (ReferenceEquals(editor, PrimaryEditorTextBox))
                ActivateEditorPane(_editorPane, focusEditor: false);
        }
        var scrollViewer = editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null)
        {
            var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            if (maximumOffset > 0)
            {
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(fraction, 0, 1) * maximumOffset);
                return;
            }
        }

        var line = 1 + (int)Math.Round(Math.Clamp(fraction, 0, 1) * Math.Max(0, document.LineCount - 1));
        editor.ScrollToLine(Math.Clamp(line, 1, document.LineCount));
    }

    public bool IsInsightEnabled
    {
        get => _isInsightEnabled;
        set
        {
            if (_isInsightEnabled == value) return;
            _isInsightEnabled = value;
            CancelEditorAnalysisRequests();
            CancelInactiveTabDiagnosticScans();
            OnPropertyChanged();
            lock (_insightAnalysisCacheLock)
            {
                _cachedInsightAnalysisVersion = -1;
                _cachedInsightAnalysisText = null;
            }
            if (!_isInsightEnabled)
            {
                CloseCompletionWindow();
                ClearAllPaneDiagnosticHighlights();
            }
            QueueInsightRefresh();
            RefreshInactiveSplitPaneDiagnostics();
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
            CancelEditorAnalysisRequests();
            CancelInactiveTabDiagnosticScans();
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
            CancelEditorAnalysisRequests();
            CancelInactiveTabDiagnosticScans();
            OnPropertyChanged();
            if (!_isInsightDeadCodeEnabled)
            {
                ClearAllPaneDeadCodeHighlights();
            }
            else
            {
                QueueInsightRefresh();
            }
            RefreshInactiveSplitPaneDiagnostics();
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
            CancelEditorAnalysisRequests();
            CancelInactiveTabDiagnosticScans();
            OnPropertyChanged();
            lock (_insightAnalysisCacheLock)
            {
                _cachedInsightAnalysisVersion = -1;
                _cachedInsightAnalysisText = null;
            }
            if (!_isInsightErrorDetectionEnabled)
            {
                ClearAllPaneErrorHighlights();
                QueueInsightRefresh();
            }
            else
            {
                QueueInsightRefresh();
            }
            RefreshInactiveSplitPaneDiagnostics();
            SaveSettings();
        }
    }

    public bool LspEnabled
    {
        get => _lspEnabled;
        set
        {
            if (_lspEnabled == value) return;
            _lspEnabled = value;
            _lspCompletionSettingsRevision++;
            OnPropertyChanged();
            if (value)
            {
                var path = _currentFilePath;
                if (!string.IsNullOrWhiteSpace(path) && EditorTextBox?.Document is not null && ResolveLspExtensionForFile(path) is not null)
                    QueueLspDidChange(path);
                InvalidateAllLspPanePresentations();
                QueueLspPresentationRefresh();
            }
            else
            {
                _lspDidChangeTimer.Stop();
                _lspRefreshDebounceTimer.Stop();
                CancelLspHoverRequest();
                _ = LspShutdownAllAsync();
                lock (_lspDiagnosticsLock)
                {
                    _lspDiagnostics.Clear();
                    _lspDiagnosticVersions.Clear();
                    _lspDiagnosticRefreshPending.Clear();
                    BumpLspDiagnosticsRevision();
                }
                ClearLspHoverTooltip();
                ClearLspPresentationForEditor(PrimaryEditorTextBox);
                ClearLspPresentationForEditor(SecondaryEditorTextBox);
                _primaryLspFoldingManager?.UpdateFoldings(Array.Empty<AvaloniaEdit.Folding.NewFolding>(), 0);
                _secondaryLspFoldingManager?.UpdateFoldings(Array.Empty<AvaloniaEdit.Folding.NewFolding>(), 0);
                ClearLspSignatureHelpTooltip();
                _ = UpdateErrorHighlightingSafelyAsync();
                foreach (var tab in OpenTabs.Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path)))
                    UpdateInactiveTabDiagnosticsForFile(tab.Path);
            }
            QueueInsightRefresh();
            SaveSettings();
        }
    }
    public bool LspCompletionEnabled
    {
        get => _lspCompletionEnabled;
        set { if (_lspCompletionEnabled == value) return; _lspCompletionEnabled = value; _lspCompletionSettingsRevision++; OnPropertyChanged(); QueueInsightRefresh(); SaveSettings(); }
    }
    public bool LspHoverEnabled
    {
        get => _lspHoverEnabled;
        set
        {
            if (_lspHoverEnabled == value) return;
            _lspHoverEnabled = value;
            OnPropertyChanged();
            if (!value) CancelLspHoverRequest();
            if (!value) ClearLspHoverTooltip();
            SaveSettings();
        }
    }
    public bool LspSignatureHelpEnabled
    {
        get => _lspSignatureHelpEnabled;
        set
        {
            if (_lspSignatureHelpEnabled == value) return;
            _lspSignatureHelpEnabled = value;
            OnPropertyChanged();
            if (value)
            {
                InvalidateAllLspPanePresentations();
                QueueLspPresentationRefresh();
            }
            else ClearLspSignatureHelpTooltip();
            SaveSettings();
        }
    }
    public bool LspInlayHintsEnabled
    {
        get => _lspInlayHintsEnabled;
        set
        {
            if (_lspInlayHintsEnabled == value) return;
            _lspInlayHintsEnabled = value;
            OnPropertyChanged();
            if (value)
            {
                InvalidateAllLspPanePresentations();
                QueueLspPresentationRefresh();
            }
            else
            {
                ClearLspInlayHintsForEditor(PrimaryEditorTextBox);
                ClearLspInlayHintsForEditor(SecondaryEditorTextBox);
            }
            SaveSettings();
        }
    }
    public bool LspFoldingEnabled
    {
        get => _lspFoldingEnabled;
        set
        {
            if (_lspFoldingEnabled == value) return;
            _lspFoldingEnabled = value;
            OnPropertyChanged();
            if (value)
            {
                InvalidateAllLspPanePresentations();
                QueueLspPresentationRefresh();
            }
            else
            {
                _primaryLspFoldingManager?.UpdateFoldings(Array.Empty<AvaloniaEdit.Folding.NewFolding>(), 0);
                _secondaryLspFoldingManager?.UpdateFoldings(Array.Empty<AvaloniaEdit.Folding.NewFolding>(), 0);
            }
            SaveSettings();
        }
    }
    public bool LspDocumentHighlightsEnabled
    {
        get => _lspDocumentHighlightsEnabled;
        set
        {
            if (_lspDocumentHighlightsEnabled == value) return;
            _lspDocumentHighlightsEnabled = value;
            OnPropertyChanged();
            if (value)
            {
                InvalidateAllLspPanePresentations();
                QueueLspPresentationRefresh();
            }
            else
            {
                _primaryLspHighlightRenderer.Clear();
                _secondaryLspHighlightRenderer.Clear();
                PrimaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
                SecondaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
            }
            SaveSettings();
        }
    }
    public bool LspDiagnosticsEnabled
    {
        get => _lspDiagnosticsEnabled;
        set
        {
            if (_lspDiagnosticsEnabled == value) return;
            _lspDiagnosticsEnabled = value;
            OnPropertyChanged();
            if (!value)
            {
                lock (_lspDiagnosticsLock)
                {
                    _lspDiagnostics.Clear();
                    _lspDiagnosticVersions.Clear();
                    _lspDiagnosticRefreshPending.Clear();
                    BumpLspDiagnosticsRevision();
                }
                _ = UpdateErrorHighlightingSafelyAsync();
                foreach (var tab in OpenTabs.Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path)))
                    UpdateInactiveTabDiagnosticsForFile(tab.Path);
            }
            else if (!string.IsNullOrWhiteSpace(_currentFilePath) && EditorTextBox?.Document is not null)
            {
                QueueLspDidChange(_currentFilePath);
            }
            SaveSettings();
        }
    }
    public bool LspSemanticHighlightingEnabled
    {
        get => _lspSemanticHighlightingEnabled;
        set
        {
            if (_lspSemanticHighlightingEnabled == value) return;
            _lspSemanticHighlightingEnabled = value;
            OnPropertyChanged();
            if (value)
            {
                InvalidateAllLspPanePresentations();
                QueueLspPresentationRefresh();
            }
            else
            {
                _primaryLspSemanticTokenRenderer.SetTokens(Array.Empty<(int, int, IBrush)>());
                _secondaryLspSemanticTokenRenderer.SetTokens(Array.Empty<(int, int, IBrush)>());
                PrimaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
                SecondaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
            }
            SaveSettings();
        }
    }
    public bool LspAutoInstall
    {
        get => _lspAutoInstall;
        set { if (_lspAutoInstall == value) return; _lspAutoInstall = value; OnPropertyChanged(); SaveSettings(); }
    }
    public bool LspPreferManaged
    {
        get => _lspPreferManaged;
        set { if (_lspPreferManaged == value) return; _lspPreferManaged = value; OnPropertyChanged(); SaveSettings(); }
    }
    public bool LspPreferSystem
    {
        get => _lspPreferSystem;
        set { if (_lspPreferSystem == value) return; _lspPreferSystem = value; OnPropertyChanged(); SaveSettings(); }
    }
    public string? LspInstallDir
    {
        get => _lspInstallDir ?? LspInstallationManager.GetManagedRoot(BuildLspResolverSettings());
        set { if (_lspInstallDir == value) return; _lspInstallDir = value; OnPropertyChanged(); SaveSettings(); }
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

    public string InsightErrorDeadCodeBlacklistExtensions
    {
        get => _insightErrorDeadCodeBlacklistExtensions;
        set
        {
            if (_insightErrorDeadCodeBlacklistExtensions == value) return;
            _insightErrorDeadCodeBlacklistExtensions = value;
            RebuildErrorDeadCodeBlacklist();
            OnPropertyChanged();
            SaveSettings();
            QueueInsightRefresh();
        }
    }

    private void RebuildErrorDeadCodeBlacklist()
    {
        _insightErrorDeadCodeBlacklistSet.Clear();
        foreach (var part in _insightErrorDeadCodeBlacklistExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            _insightErrorDeadCodeBlacklistSet.Add(ext);
        }
    }

    private bool IsErrorDeadCodeBlacklisted(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _insightErrorDeadCodeBlacklistSet.Contains(ext);
    }

    private static string GetDiagnosticSignature(string? filePath, string lineText, string message) =>
        $"{filePath ?? string.Empty}|{lineText.Trim()}|{message}";

    private static string GetDiagnosticLineText(string text, int[] lineStarts, int offset, Dictionary<int, string> lineCache)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var index = Array.BinarySearch(lineStarts, offset);
        if (index < 0) index = Math.Max(0, ~index - 1);
        var lineStart = lineStarts[index];
        if (lineCache.TryGetValue(lineStart, out var cached)) return cached;

        var lineEnd = index + 1 < lineStarts.Length ? lineStarts[index + 1] : text.Length;
        while (lineEnd > lineStart && text[lineEnd - 1] is '\r' or '\n') lineEnd--;
        var lineText = text.Substring(lineStart, lineEnd - lineStart);
        lineCache[lineStart] = lineText;
        return lineText;
    }

    public bool IsDiagnosticDismissed(string? filePath, string lineText, string message) =>
        _dismissedDiagnostics.Contains(GetDiagnosticSignature(filePath, lineText, message));

    public void DismissDiagnostic(string? filePath, string lineText, string message)
    {
        var sig = GetDiagnosticSignature(filePath, lineText, message);
        if (_dismissedDiagnostics.Add(sig))
        {
            _dismissedDiagnosticsRevision++;
            SaveSettings();
            QueueInsightRefresh();
        }
    }

    public void ClearDismissedDiagnostics()
    {
        if (_dismissedDiagnostics.Count == 0) return;
        _dismissedDiagnostics.Clear();
        _dismissedDiagnosticsRevision++;
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
            OnPropertyChanged(nameof(IndentationDisplayText));
            ApplyEditorSettings();
            SaveSettings();
        }
    }

    public bool InsertSpaces
    {
        get => _insertSpaces;
        set
        {
            if (_insertSpaces == value) return;
            _insertSpaces = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IndentationDisplayText));
            OnPropertyChanged(nameof(EditorBehaviorStatusText));
            ApplyEditorSettings();
            SaveSettings();
        }
    }

    public string IndentationDisplayText => InsertSpaces ? $"Spaces: {TabSize}" : $"Tab Size: {TabSize}";

    public bool IsIndentationVisible => IsTextEditorVisible;

    public int TabSizeIndex
    {
        get
        {
            if (!InsertSpaces) return 3;
            return TabSize switch
            {
                2 => 0,
                8 => 2,
                _ => 1
            };
        }
        set
        {
            switch (value)
            {
                case 0: InsertSpaces = true; TabSize = 2; break;
                case 2: InsertSpaces = true; TabSize = 8; break;
                case 3: InsertSpaces = false; break;
                default: InsertSpaces = true; TabSize = 4; break;
            }
        }
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

    public string EditorFontFamily
    {
        get => _editorFontFamily;
        set
        {
            var next = value ?? "";
            if (_editorFontFamily == next) return;
            _editorFontFamily = next;
            OnPropertyChanged();
            ApplyEditorSettings();
            SaveSettings();
        }
    }

    public IReadOnlyList<string> EditorFontChoices => KodoFonts.EditorFontChoices;

    public int TerminalScrollbackLines
    {
        get => _terminalScrollbackLines;
        set
        {
            var clamped = Math.Clamp(value, 200, 200000);
            if (_terminalScrollbackLines == clamped) return;
            _terminalScrollbackLines = clamped;
            OnPropertyChanged();
            TerminalHostControl.MaxScrollbackLines = clamped;
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

    private void OnWindowPointerPressedToDismissFocus(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        for (Visual? v = source; v is not null && v != this; v = v.GetVisualParent())
        {
            if (v is Control { Focusable: true })
                return;
        }
        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void SettingsPage_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var box = this.FindControl<TextBox>("SettingsSearchTextBox");
        if (box is null) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (!ReferenceEquals(focused, box)) return;
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
        }
    }

    public bool IsAutoUpdateAppInBackgroundEnabled
    {
        get => IsAutoUpdateAppInBackgroundEnabled1;
        set
        {
            if (IsAutoUpdateAppInBackgroundEnabled1 == value) return;
            IsAutoUpdateAppInBackgroundEnabled1 = value;
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
            OnPropertyChanged(nameof(IsTerminalPanelVisible));
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
                var detached = TerminalHostControl.Detach();
                if (detached is not null)
                    _activeTerminalSession.LiveHandle = detached;
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
                        if (_activeSessionExitedHandler is not null)
                        {
                            TerminalHostControl.SessionExited -= _activeSessionExitedHandler;
                            _activeSessionExitedHandler = null;
                        }

                        var watchedSession = _activeTerminalSession;
                        void OnExited(object? s, TerminalProcessHandle exitedHandle)
                        {
                            if (!ReferenceEquals(exitedHandle, watchedSession.LiveHandle) &&
                                !ReferenceEquals(exitedHandle, TerminalHostControl.ActiveHandle))
                                return;

                            TerminalHostControl.SessionExited -= OnExited;
                            _activeSessionExitedHandler = null;
                            watchedSession.IsRunning = false;

                            CloseTerminalSession(watchedSession);
                            RefreshTerminalStatusBindings();
                        }
                        _activeSessionExitedHandler = OnExited;
                        TerminalHostControl.SessionExited += OnExited;

                        var liveHandle = watchedSession.LiveHandle;
                        if (liveHandle is not null && !liveHandle.Exited && ConsoleTerminal.IsHandleAlive(liveHandle))
                        {
                            TerminalHostControl.Attach(liveHandle);
                            if (watchedSession.Snapshot is not null)
                                TerminalHostControl.RestoreSnapshot(watchedSession.Snapshot);
                            watchedSession.IsRunning = true;
                            watchedSession.StatusText = "Ready";
                        }
                        else
                        {
                            if (liveHandle is not null)
                            {
                                TerminalHostControl.DestroyHandle(liveHandle);
                                watchedSession.LiveHandle = null;
                            }
                            var hasSnapshot = watchedSession.Snapshot is not null;
                            TerminalHostControl.Start(shell.FileName, shell.Arguments,
                                watchedSession.WorkingDirectory,
                                suppressOutputUntilRestored: hasSnapshot);
                            watchedSession.LiveHandle = TerminalHostControl.ActiveHandle;
                            watchedSession.IsRunning = true;
                            watchedSession.StatusText = "Ready";
                            if (watchedSession.Snapshot is not null)
                                TerminalHostControl.RestoreSnapshot(watchedSession.Snapshot);
                        }
                    }
                    catch (Exception ex)
                    {
                        KodoDiagnostics.LogWarning("MainWindow.ActiveTerminalSession",
                            ex, operation: $"Failed to start shell for '{_activeTerminalSession.Title}'");
                        _activeTerminalSession.IsRunning = false;
                        _activeTerminalSession.StatusText = $"Failed to start: {ex.Message}";
                    }
                }
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

    public string FileSummaryText => IsTutorialPageVisible
        ? "Tutorial"
        : IsHomePageVisible
        ? "Home"
        : HasDocumentOpen
        ? $"{GetDocumentDisplayName()}{GetDocumentStatusSuffix()}"
        : "Home";
    public bool IsTerminalSupported => _isTerminalSupported;

    public bool IsTerminalPanelVisible => IsTerminalVisible && IsTerminalSupported;

    public bool IsPSReadLineRelevant => OperatingSystem.IsWindows();
    public bool HasActiveTerminal => ActiveTerminalSession is not null;
    public bool HasTerminalSessions => TerminalSessions.Count > 0;
    public int TerminalSessionCount => TerminalSessions.Count;
    public string ActiveTerminalStatusText => ActiveTerminalSession?.StatusText ?? (IsTerminalSupported ? "No active terminal" : "Windows and Linux only");
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
        : IsTerminalSupported ? "Choose a shell and open a terminal session." : "Embedded terminal is currently supported on Windows and Linux.";
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

    private static readonly Typeface ExplorerHeaderTypeface = new("Inter,Segoe UI,sans-serif", weight: FontWeight.SemiBold);

    private string? _explorerPanelMinWidthHeader;
    private double _explorerPanelMinWidthValue;

    public double ExplorerPanelMinWidth
    {
        get
        {
            var header = ExplorerHeaderText;
            if (_explorerPanelMinWidthHeader == header) return _explorerPanelMinWidthValue;

            var formatted = new FormattedText(
                header,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                ExplorerHeaderTypeface,
                11,
                Brushes.Black);

            var width = Math.Min(MaxExplorerPanelWidth,
                Math.Max(MinExplorerPanelWidth, formatted.Width + ExplorerHeaderFixedChrome));

            _explorerPanelMinWidthHeader = header;
            _explorerPanelMinWidthValue = width;
            return width;
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
            RaiseWelcomeMessageChanged();
            UpdateLocalizedSpelling();
            RaiseMany(nameof(IsAmericanEnglish), nameof(LabelAccentColour), nameof(LabelAccentSystem), nameof(TooltipSystemTheme), nameof(RevealInFileManagerText), nameof(TerminalShellDescriptionText), nameof(RecheckLanguageServersLabel), nameof(TooltipAccentTheme), nameof(TooltipAccentWindows), nameof(TooltipAccentCustom), nameof(LabelPersonalization), nameof(LabelPersonalizationHeader), nameof(LabelPersonalizationDescription), nameof(PersonalizationExportTooltip), nameof(TutorialSpotlightTitle), nameof(TutorialBody), nameof(TutorialHighlightOne), nameof(TutorialHighlightThree), nameof(DeadCodeTooltip), nameof(CancelledSpelling));
            SaveSettings();
        }
    }

    private void UpdateLocalizedSpelling()
    {
        _colorSwatchGenerator.PickerTitle = IsAmericanEnglish ? "Color Picker" : "Colour Picker";
        _colorSwatchGenerator.EditColorTooltip = IsAmericanEnglish ? "Edit Color" : "Edit Colour";
        if (_secondaryColorSwatchGenerator is not null)
        {
            _secondaryColorSwatchGenerator.PickerTitle = IsAmericanEnglish ? "Color Picker" : "Colour Picker";
            _secondaryColorSwatchGenerator.EditColorTooltip = IsAmericanEnglish ? "Edit Color" : "Edit Colour";
        }
    }

    public bool IsAmericanEnglish => _userCountry == "US";
    public string CancelledSpelling => IsAmericanEnglish ? "canceled" : "cancelled";
    public string DeadCodeTooltip => IsAmericanEnglish
        ? "Gray out unused variables, unused functions, and unreachable code. Experimental; please double-check whether it is correct."
        : "Grey out unused variables, unused functions, and unreachable code. Experimental; please double-check whether it is correct.";

    public string LabelAccentColour => IsAmericanEnglish ? "Accent Color" : "Accent Colour";
    public string LabelAccentSystem => SystemThemeHelper.AccentModeDisplayName(IsAmericanEnglish);
    public string TooltipSystemTheme => SystemThemeHelper.SystemThemeTooltip(IsAmericanEnglish);
    public string RevealInFileManagerText => SystemThemeHelper.RevealInFileManagerText(IsAmericanEnglish);

    private bool _isRecheckingLanguageServers;

    public bool IsRecheckingLanguageServers
    {
        get => _isRecheckingLanguageServers;
        private set
        {
            if (_isRecheckingLanguageServers == value) return;
            _isRecheckingLanguageServers = value;
            OnPropertyChanged();
        }
    }

    private string _lspRecheckStatusText = string.Empty;
    public string LspRecheckStatusText
    {
        get => _lspRecheckStatusText;
        private set
        {
            if (_lspRecheckStatusText == value) return;
            _lspRecheckStatusText = value;
            OnPropertyChanged();
        }
    }

    public string RecheckLanguageServersLabel => "Re-check language servers";

    public string TerminalShellDescriptionText =>
        OperatingSystem.IsWindows()
            ? "New terminal sessions use the selected shell. PowerShell is preferred when it is available."
            : "New terminal sessions use the selected shell, starting in the folder you have open.";

    public string TooltipAccentTheme => IsAmericanEnglish ? "Use the accent color preset by the active theme" : "Use the accent colour preset by the active theme";
    public string TooltipAccentWindows
    {
        get
        {
            var baseText = SystemThemeHelper.AccentModeTooltip(IsAmericanEnglish);
            return _hasSystemAccent
                ? baseText
                : baseText + " \u2013 no system accent detected, using the theme accent instead.";
        }
    }
    public string TooltipAccentCustom => IsAmericanEnglish ? "Choose a custom accent color" : "Choose a custom accent colour";
    public string LabelPersonalization => IsAmericanEnglish ? "Personalization" : "Personalisation";
    public string LabelPersonalizationHeader => IsAmericanEnglish ? "PERSONALIZATION" : "PERSONALISATION";
    public string PersonalizationExportTooltip => IsAmericanEnglish
        ? "Save a formatted .txt file with all of Kodo's settings, personalization, open/recent files, and installed extensions"
        : "Save a formatted .txt file with all of Kodo's settings, personalisation, open/recent files, and installed extensions";
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
            RaiseWelcomeMessageChanged();
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
            RaiseWelcomeMessageChanged();
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
            RaiseWelcomeMessageChanged();
            SaveSettings();
        }
    }

    private void RaiseWelcomeMessageChanged()
    {
        OnPropertyChanged(nameof(WelcomeMessage));
        OnPropertyChanged(nameof(IsTaglineGreeting));
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

    public bool IsTaglineGreeting => string.Equals(WelcomeMessage, WelcomeMessageBuilder.TaglineGreeting, StringComparison.Ordinal);

    public string TaglinePrefixText => WelcomeMessageBuilder.TaglinePrefix;
    public string TaglineAccentText => WelcomeMessageBuilder.TaglineAccentSuffix;

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
            var age = today.Year - _kodoBirthDate.Year;
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

    public bool IsDarkThemeActive => !IsSystemThemeActive && string.Equals(CurrentThemeName, "Dark", StringComparison.OrdinalIgnoreCase);
    public bool IsLightThemeActive => !IsSystemThemeActive && string.Equals(CurrentThemeName, "Light", StringComparison.OrdinalIgnoreCase);

    public bool IsSystemThemeActive => string.Equals(_requestedThemeName, "System", StringComparison.OrdinalIgnoreCase);

    public IBrush SystemThemePreviewBackground { get; private set; } = Brush.Parse("#1E1E1E");
    public IBrush SystemThemePreviewBorder { get; private set; } = Brush.Parse("#2B2B2B");

    private (bool home, bool imagePreview, bool documentOpen, bool untitled, string? filePath)
        _languageDisplayCacheKey;
    private string _cachedLanguageDisplayText = string.Empty;

    public string LanguageDisplayText
    {
        get
        {
            var key = (_isHomePageVisible, HasImagePreview, HasDocumentOpen, _hasUntitledDocument, _currentFilePath);
            if (key != _languageDisplayCacheKey)
            {
                _languageDisplayCacheKey = key;
                _cachedLanguageDisplayText = ComputeLanguageDisplayText();
            }

            return _cachedLanguageDisplayText;
        }
    }

    private string ComputeLanguageDisplayText()
    {
        if (IsHomePageVisible) return string.Empty;
        if (HasImagePreview) return "Image Preview";
        if (!HasDocumentOpen) return string.Empty;
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
        {
            var name = Path.GetFileName(_currentFilePath);
            var dot = name.IndexOf('.');
            if (dot >= 0 && dot < name.Length - 1)
                return $"{name[dot..].ToLowerInvariant()} file";
            var ext = Path.GetExtension(_currentFilePath);
            if (!string.IsNullOrWhiteSpace(ext))
                return $"{ext.ToLowerInvariant()} file";
            return string.IsNullOrWhiteSpace(name) ? "Plain Text" : $"{name} file";
        }
        return "Plain Text";
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
                1200 => "UTF-16 LE",
                1201 => "UTF-16 BE",
                12000 => "UTF-32",
                20127 => "ASCII",
                _ => _currentFileEncoding.WebName.ToUpperInvariant(),
            };
        }
    }

    public string LineEndingDisplayText
    {
        get
        {
            if (!IsLineEndingVisible) return string.Empty;
            return _currentLineEnding switch
            {
                Kodo.Models.LineEnding.CRLF => "CRLF",
                Kodo.Models.LineEnding.CR => "CR",
                _ => "LF"
            };
        }
    }

    public bool IsLineEndingVisible => HasFileOpen && IsTextEditorVisible;

    public string DiscordRichPresenceStatusText => !IsDiscordRichPresenceEnabled
        ? "Discord Rich Presence is turned off."
        : "Discord Rich Presence shows your current activity when the Discord desktop app is running.";

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

    public string AutoUpdateAppStatusText
    {
        get
        {
            if (!IsAutoUpdateAppSupported)
                return "Kodo does not ship automatic updates on this platform, so download a new version yourself.";
            if (!IsAutoUpdateAppEnabled)
                return "Kodo only updates when you download a new installer yourself.";
            return IsAutoUpdateAppInBackgroundEnabled
                ? "Checking for new Kodo versions on launch and every few hours, and installing them automatically in the background once Kodo is closed."
                : "Checking for new Kodo versions on launch and every few hours, and prompting to install them.";
        }
    }

    public bool IsAutoUpdateAppSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

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

    public bool IsDocumentDirty => _isDirty;

    public string StatusBarFileIconText
    {
        get
        {
            if (!HasDocumentOpen || string.IsNullOrWhiteSpace(_currentFilePath))
                return "..";
            return Kodo.Models.FileTreeItem.GetFileIcon(Path.GetFileName(_currentFilePath) ?? string.Empty);
        }
    }

    public string StatusBarFilePathTooltip => _currentFilePath ?? "No file open";

    public string StatusBarCaretText
    {
        get => _statusBarCaretText;
        private set { if (_statusBarCaretText == value) return; _statusBarCaretText = value; OnPropertyChanged(); }
    }

    public string StatusBarSelectionText
    {
        get => _statusBarSelectionText;
        private set { if (_statusBarSelectionText == value) return; _statusBarSelectionText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection)); }
    }

    public string StatusBarDocumentText
    {
        get => _statusBarDocumentText;
        private set { if (_statusBarDocumentText == value) return; _statusBarDocumentText = value; OnPropertyChanged(); }
    }

    public bool HasSelection => !string.IsNullOrWhiteSpace(_statusBarSelectionText);

    public string StatusBarDiagnosticsText
    {
        get => _statusBarDiagnosticsText;
        private set { if (_statusBarDiagnosticsText == value) return; _statusBarDiagnosticsText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDiagnostics)); }
    }

    public string StatusBarDiagnosticsTooltip
    {
        get => _statusBarDiagnosticsTooltip;
        private set { if (_statusBarDiagnosticsTooltip == value) return; _statusBarDiagnosticsTooltip = value; OnPropertyChanged(); }
    }

    public bool HasDiagnostics => StatusBarDiagnosticsText != "No problems" && !string.IsNullOrWhiteSpace(StatusBarDiagnosticsText);

    public IBrush WindowBackgroundBrush { get; private set; } = Brush.Parse("#1E1E1E");
    public IBrush TopBarBrush { get; private set; } = Brush.Parse("#181818");
    public IBrush SidebarBrush { get; private set; } = Brush.Parse("#181818");
    public IBrush ButtonBrush { get; private set; } = Brush.Parse("#252526");
    public IBrush ButtonHoverBrush { get; private set; } = Brush.Parse("#313437");
    public IBrush EditorBackgroundBrush { get; private set; } = Brush.Parse("#1E1E1E");
    public IBrush CardBrush { get; private set; } = Brush.Parse("#252526");
    public IBrush PrimaryTextBrush { get; private set; } = Brush.Parse("#F4F4F4");
    public IBrush MutedTextBrush { get; private set; } = Brush.Parse("#A0A0A0");
    public IBrush SurfaceBorderBrush { get; private set; } = Brush.Parse("#2B2B2B");
    public IBrush AccentBrush { get; private set; } = Brush.Parse("#8C00FF");
    public IBrush DangerBrush { get; } = new SolidColorBrush(KodoDesignTokens.DangerColor);
    public IBrush DangerForegroundBrush { get; } = Brushes.White;
    public Color DangerColor => KodoDesignTokens.DangerColor;
    public IBrush WarningBrush { get; } = new SolidColorBrush(KodoDesignTokens.WarningColor);
    public IBrush WarningSurfaceBrush { get; } = new SolidColorBrush(KodoDesignTokens.WarningSurfaceColor);
    public IBrush WarningBorderBrush { get; } = new SolidColorBrush(KodoDesignTokens.WarningBorderColor);

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

    private static double GetContrastRatio(Color a, Color b)
    {
        var l1 = GetRelativeLuminance(a);
        var l2 = GetRelativeLuminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    public IBrush WindowsAccentPreviewBrush { get; private set; } =
        Brush.Parse("#8C00FF");

    public bool HasSystemAccent => _hasSystemAccent;

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
    public bool IsAccentKodo => _accentColorMode == "kodo";
    public bool IsAccentWindows => _accentColorMode == "windows";
    public bool IsAccentCustom => _accentColorMode == "custom";
    public bool HasThemeAccent => _hasThemeAccent;
    public bool IsAccentTheme => _accentColorMode == "theme";
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
        add => ViewModelPropertyChanged += value;
        remove => ViewModelPropertyChanged -= value;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        ViewModelPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OpenTabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshPaneTabSelection();
        RaiseMany(nameof(HasOpenEditors), nameof(HasMultipleOpenEditors), nameof(IsEditorTabsVisible), nameof(IsSharedEditorTabsVisible), nameof(IsEmptyStateVisible), nameof(IsDocumentViewVisible), nameof(IsHomeOrEditorPageVisible), nameof(HasDocumentOpen), nameof(IsTextEditorVisible), nameof(CanToggleSplitEditor));
        Dispatcher.UIThread.Post(() => SaveSettings(), DispatcherPriority.Background);
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

        var watch = System.Diagnostics.Stopwatch.StartNew();
        _pendingFullStateRefresh = false;
        RefreshWordCount();
        RefreshNonCaretState();
        watch.Stop();
        KodoDiagnostics.ReportSlowStage("editor state refresh", watch.ElapsedMilliseconds, 250);
    }

    private void RefreshCaretAndDocumentStats()
    {
        var document = EditorTextBox?.Document;
        if (HasDocumentOpen && !IsHomePageVisible)
        {
            if (CurrentImagePreview is not null)
            {
                EditorStatsText = $"{CurrentImagePreview.PixelSize.Width} x {CurrentImagePreview.PixelSize.Height}px";
                StatusBarCaretText = $"{CurrentImagePreview.PixelSize.Width}×{CurrentImagePreview.PixelSize.Height}";
                StatusBarSelectionText = string.Empty;
                StatusBarDocumentText = "Image";
            }
            else
            {
                var lines = document?.LineCount ?? 1;
                var characters = document?.TextLength ?? 0;

                var selection = EditorTextBox?.TextArea?.Selection;
                if (selection is not null && !selection.IsEmpty && document is not null)
                {
                    var seg = selection.SurroundingSegment;
                    var selChars = seg?.Length ?? 0;
                    var selLines = 1;
                    try
                    {
                        if (seg is not null && seg.Length > 0)
                        {
                            var startLine = document.GetLineByOffset(seg.Offset).LineNumber;
                            var endLine = document.GetLineByOffset(Math.Max(seg.Offset, seg.EndOffset - 1)).LineNumber;
                            var lineCountFromOffsets = Math.Max(1, endLine - startLine + 1);
                            selLines = lineCountFromOffsets;
                        }
                    }
                    catch { }

                    string selText;
                    if (selLines > 1)
                    {
                        var charWord = selChars == 1 ? "char" : "chars";
                        selText = $"{selLines} lines, {selChars} {charWord} selected";
                    }
                    else
                    {
                        var charWord = selChars == 1 ? "char" : "chars";
                        selText = $"{selChars} {charWord} selected";
                    }
                    EditorStatsText = $"{selText}  |  {lines} lines  |  {characters} characters";
                    var caret = EditorTextBox?.TextArea?.Caret;
                    StatusBarCaretText = $"Ln {caret?.Line ?? 1}, Col {caret?.Column ?? 1}";
                    StatusBarSelectionText = selText;
                    StatusBarDocumentText = $"{lines} lines • {characters} chars";
                }
                else
                {
                    var caret = EditorTextBox?.TextArea?.Caret;
                    var ln = caret?.Line ?? 1;
                    var col = caret?.Column ?? 1;
                    EditorStatsText = $"Ln {ln}, Col {col}  |  {lines} lines  |  {characters} characters";
                    StatusBarCaretText = $"Ln {ln}, Col {col}";
                    StatusBarSelectionText = string.Empty;
                    StatusBarDocumentText = $"{lines} lines • {characters} chars";
                }
            }
        }
        else
        {
            EditorStatsText = string.Empty;
            StatusBarCaretText = string.Empty;
            StatusBarSelectionText = string.Empty;
            StatusBarDocumentText = string.Empty;
        }
    }

    private static void BumpLspDiagnosticsRevision() => Interlocked.Increment(ref _lspDiagnosticsRevision);

    private static long MixSignatureText(long hash, string? value)
    {
        unchecked
        {
            hash ^= (long)(value?.Length ?? 0) << 24;
            if (value is null) return hash;
            foreach (var ch in value) hash = (hash * 16777619) ^ ch;
            return hash;
        }
    }

    private static long MixSignatureInt(long hash, long value) => unchecked(hash * 16777619 ^ value);

    private long ComputeErrorHighlightSignature(long scanVersion, string? scanPath, string scanExtension)
    {
        var lspRevision = Interlocked.CompareExchange(ref _lspDiagnosticsRevision, 0, 0);
        var hash = MixSignatureInt(scanVersion, lspRevision);
        hash = MixSignatureText(hash, scanPath);
        hash = MixSignatureText(hash, scanExtension);
        return MixSignatureInt(hash, _dismissedDiagnosticsRevision);
    }

    private long ComputeDeadCodeSignature(long scanVersion, string? scanPath, string scanExtension, string? folderPath)
    {
        var hash = MixSignatureInt(scanVersion, _dismissedDiagnosticsRevision);
        hash = MixSignatureText(hash, scanPath);
        hash = MixSignatureText(hash, scanExtension);
        return MixSignatureText(hash, folderPath);
    }

    private void RefreshStatusBarDiagnostics()
    {
        var errors = _errorHighlightRenderer.Spans.Count(s => string.Equals(s.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warnings = _errorHighlightRenderer.Spans.Count(s => string.Equals(s.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        var infos = _errorHighlightRenderer.Spans.Count - errors - warnings;
        var dead = _deadCodeHighlightRenderer.Spans.Count;

        if (errors == 0 && warnings == 0 && infos == 0 && dead == 0)
        {
            StatusBarDiagnosticsText = "No problems";
            StatusBarDiagnosticsTooltip = "No problems detected by Insight";
        }
        else
        {
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            if (infos > 0) parts.Add($"{infos} info");
            if (dead > 0) parts.Add($"{dead} unused");

            StatusBarDiagnosticsText = string.Join(" • ", parts);
            StatusBarDiagnosticsTooltip = string.Join(Environment.NewLine, parts.Select(p => $"• {p}"));
        }
        UpdateActiveTabDiagnostics();
    }

    private void UpdateTabDiagnostics(Kodo.Models.EditorTab? tab, IReadOnlyList<ErrorSpan> errorSpans, IReadOnlyList<DeadCodeSpan> deadSpans)
    {
        if (tab is null) return;
        var errors = errorSpans.Count(s => string.Equals(s.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warnings = errorSpans.Count(s => string.Equals(s.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        var infos = errorSpans.Count - errors - warnings;
        var dead = deadSpans.Count;
        tab.UpdateDiagnostics(errors, warnings, infos, dead);
        UpdateFileTreeErrorCount(tab.Path, errors);
    }

    private void UpdateActiveTabDiagnostics()
    {
        UpdateTabDiagnostics(ActiveEditorTab, _errorHighlightRenderer.Spans, _deadCodeHighlightRenderer.Spans);
    }

    private void RefreshInactiveSplitPaneDiagnostics()
    {
        if (!IsSplitScreenVisible)
            return;

        var inactivePane = ReferenceEquals(_activeEditorPane, _editorPane)
            ? _secondaryEditorPane
            : _editorPane;
        var tab = inactivePane.ActiveTab;
        if (tab is null)
            return;

        if (ResolveLspExtensionForFile(tab.Path) is { HasLsp: true } && LspEnabled && LspDiagnosticsEnabled)
        {
            UpdateInactiveTabDiagnosticsForFile(tab.Path);
            return;
        }

        if (IsInsightEnabled && (IsInsightErrorDetectionEnabled || IsInsightDeadCodeEnabled))
        {
            _ = RefreshInactiveTabInsightDiagnosticsAsync(tab);
            return;
        }

        var errorRenderer = ReferenceEquals(inactivePane, _secondaryEditorPane)
            ? _secondaryErrorHighlightRenderer
            : _primaryErrorHighlightRenderer;
        var deadRenderer = ReferenceEquals(inactivePane, _secondaryEditorPane)
            ? _secondaryDeadCodeHighlightRenderer
            : _primaryDeadCodeHighlightRenderer;
        UpdateTabDiagnostics(tab, errorRenderer.Spans, deadRenderer.Spans);
    }

    private void UpdateInactiveTabDiagnosticsForFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var matchingTabs = OpenTabs.Where(t => !t.IsUntitled && IsSameDocument(t.Path, filePath)).ToList();
        if (matchingTabs.Count == 0) return;

        List<LspRawDiagnostic>? raw;
        lock (_lspDiagnosticsLock)
        {
            var normPath = NormalizeFilePath(filePath);
            if (!_lspDiagnostics.TryGetValue(normPath, out raw) && !_lspDiagnostics.TryGetValue(filePath, out raw))
            {
                var uri = FilePathToUri(filePath);
                var normUri = NormalizeFilePath(uri);
                if (!_lspDiagnostics.TryGetValue(uri, out raw) && !_lspDiagnostics.TryGetValue(normUri, out raw))
                {
                    foreach (var kv in _lspDiagnostics)
                    {
                        if (IsSameDocument(kv.Key, filePath)) { raw = kv.Value; break; }
                    }
                }
            }
            raw = raw is null ? new List<LspRawDiagnostic>() : new List<LspRawDiagnostic>(raw);
        }

        var splitPaneErrors = UpdateInactiveSplitPaneLspHighlightsForFile(filePath);
        var errors = raw.Count(d => string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warnings = raw.Count(d => string.Equals(d.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        var infos = raw.Count - errors - warnings;
        int? visibleTabErrorCount = null;

        foreach (var tab in matchingTabs)
        {
            var shownInInactivePane = IsSplitScreenVisible &&
                ((!ReferenceEquals(_activeEditorPane, _editorPane) && ReferenceEquals(_editorPane.ActiveTab, tab)) ||
                 (!ReferenceEquals(_activeEditorPane, _secondaryEditorPane) && ReferenceEquals(_secondaryEditorPane.ActiveTab, tab)));
            var unused = tab.UnusedCount;
            var tabErrors = errors;
            var tabWarnings = warnings;
            var tabInfos = infos;
            if (shownInInactivePane && splitPaneErrors is { } visibleErrors)
            {
                tabErrors = visibleErrors.Count(span => string.Equals(span.Severity, "error", StringComparison.OrdinalIgnoreCase));
                tabWarnings = visibleErrors.Count(span => string.Equals(span.Severity, "warning", StringComparison.OrdinalIgnoreCase));
                tabInfos = visibleErrors.Count - tabErrors - tabWarnings;
                visibleTabErrorCount = tabErrors;
            }

            tab.UpdateDiagnostics(tabErrors, tabWarnings, tabInfos, unused);
            if (raw.Count == 0 && (tab.HasDiagnostics || shownInInactivePane) &&
                tabErrors == 0 && tabWarnings == 0 && tabInfos == 0)
            {
                _ = RefreshInactiveTabInsightDiagnosticsAsync(tab);
            }
        }
        UpdateFileTreeErrorCount(filePath, visibleTabErrorCount ?? errors);
    }

    private List<ErrorSpan>? UpdateInactiveSplitPaneLspHighlightsForFile(string filePath)
    {
        if (!IsSplitScreenVisible)
            return null;

        var client = GetLspClientForFile(filePath);
        var encoding = LspPath.NormalizePositionEncoding(client?.PositionEncoding);
        List<ErrorSpan>? displayedErrors = null;
        foreach (var pane in new[] { _editorPane, _secondaryEditorPane })
        {
            if (ReferenceEquals(pane, _activeEditorPane) ||
                !FileSystemPaths.Equals(pane.ActiveTab?.Path, filePath))
                continue;

            var editor = GetEditorForPane(pane);
            if (editor.Document is not { } document)
                continue;

            var errors = GetLspDiagnosticsForFile(filePath, document, encoding);
            if (_dismissedDiagnostics.Count > 0)
                errors = FilterDismissedErrorSpans(errors, document, filePath,
                    new HashSet<string>(_dismissedDiagnostics, StringComparer.Ordinal));

            var deadRenderer = ReferenceEquals(editor, SecondaryEditorTextBox)
                ? _secondaryDeadCodeHighlightRenderer
                : _primaryDeadCodeHighlightRenderer;
            var errorRenderer = ReferenceEquals(editor, SecondaryEditorTextBox)
                ? _secondaryErrorHighlightRenderer
                : _primaryErrorHighlightRenderer;
            var errorDarkener = ReferenceEquals(editor, SecondaryEditorTextBox)
                ? _secondaryErrorTextDarkener
                : _primaryErrorTextDarkener;
            var preserveLocalDiagnostics = !IsLspPrimaryForFile(filePath);
            var combinedErrors = errorRenderer.Spans
                .Where(span => preserveLocalDiagnostics &&
                    !string.Equals(span.Source, "lsp", StringComparison.OrdinalIgnoreCase))
                .Concat(errors)
                .GroupBy(span => (span.StartOffset, span.Length,
                    Severity: span.Severity.Trim().ToLowerInvariant(), span.Message, span.Code))
                .Select(group => group.OrderByDescending(span =>
                    string.Equals(span.Source, "lsp", StringComparison.OrdinalIgnoreCase) ? 2 :
                    span.Source.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ? 1 : 0).First())
                .ToList();
            displayedErrors = combinedErrors;
            var errorIndex = combinedErrors.Count == 0
                ? SpanOverlapIndex.Empty
                : SpanOverlapIndex.Build(combinedErrors, span => span.StartOffset,
                    span => (long)span.StartOffset + Math.Max(1, span.Length));

            errorRenderer.SetSpans(combinedErrors, errorIndex);
            errorRenderer.SetDeadCodeSpans(deadRenderer.Spans, deadRenderer.SpanIndex);
            errorDarkener.SetSpans(combinedErrors, errorIndex, deadRenderer.Spans, deadRenderer.SpanIndex);
            RedrawVisibleEditorLines(editor);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        return displayedErrors;
    }

    private async Task RefreshInactiveTabInsightDiagnosticsAsync(Kodo.Models.EditorTab tab)
    {
        if (tab.IsUntitled || IsPlainTextFile(tab.Path) || HasNoFileExtension(tab.Path) || IsInsightBlacklisted(tab.Path) || IsErrorDeadCodeBlacklisted(tab.Path))
            return;
        if (!IsInsightEnabled || (!IsInsightErrorDetectionEnabled && !IsInsightDeadCodeEnabled)) return;
        if (ReferenceEquals(tab, ActiveEditorTab) || IsLspPrimaryForFile(tab.Path)) return;

        var cancellation = BeginInactiveTabDiagnosticScan(tab);
        var cancellationToken = cancellation.Token;
        try
        {
            await Task.Delay(180, cancellationToken);
            var filePath = tab.Path;
            var folderPath = _currentFolderPath;
            var document = tab.Document;
            if (document is { TextLength: > 80_000 }) return;
            var documentVersion = document?.Version;
            var text = await GetEditorTabContentAsync(tab, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length > 80_000 || IsLspPrimaryForFile(filePath)) return;
            var ext = GetLanguageExtension(filePath);
            var spans = IsInsightErrorDetectionEnabled
                ? await InsightWorkerGateway.Default.FindErrorsAsync(_InsightEngine, text, ext, cancellationToken)
                : new List<ErrorSpan>();
            var deadSpans = IsInsightDeadCodeEnabled
                ? await InsightWorkerGateway.Default.FindDeadCodeAsync(_InsightEngine, text, ext, folderPath, filePath, cancellationToken)
                : new List<DeadCodeSpan>();
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentInactiveTabDiagnosticScan(tab, cancellation) ||
                    !OpenTabs.Contains(tab) || ReferenceEquals(tab, ActiveEditorTab) ||
                    !FileSystemPaths.Equals(tab.Path, filePath) ||
                    !FileSystemPaths.Equals(_currentFolderPath, folderPath) || !IsInsightEnabled ||
                    (document is not null && (!ReferenceEquals(tab.Document, document) ||
                     !ReferenceEquals(document.Version, documentVersion))) ||
                    (document is null && !string.Equals(tab.Content ?? string.Empty, text, StringComparison.Ordinal)))
                    return;

                if (!IsInsightErrorDetectionEnabled)
                    spans = new List<ErrorSpan>();
                if (!IsInsightDeadCodeEnabled)
                    deadSpans = new List<DeadCodeSpan>();
                var dismissed = new HashSet<string>(_dismissedDiagnostics, StringComparer.Ordinal);
                var filteredSpans = FilterDismissedErrorSpans(spans, document ?? new TextDocument(text), filePath, dismissed);
                var filteredDeadSpans = FilterDismissedDeadCodeSpans(deadSpans, text, filePath, dismissed);
                UpdateTabDiagnostics(tab, filteredSpans, filteredDeadSpans);

                if (!IsSplitScreenVisible || document is null)
                    return;

                foreach (var pane in new[] { _editorPane, _secondaryEditorPane })
                {
                    if (ReferenceEquals(pane, _activeEditorPane) || !ReferenceEquals(pane.ActiveTab, tab))
                        continue;

                    var editor = GetEditorForPane(pane);
                    if (!ReferenceEquals(editor.Document, document))
                        continue;

                    var isSecondary = ReferenceEquals(editor, SecondaryEditorTextBox);
                    var deadRenderer = isSecondary ? _secondaryDeadCodeHighlightRenderer : _primaryDeadCodeHighlightRenderer;
                    var deadBrightener = isSecondary ? _secondaryDeadCodeTextBrightener : _primaryDeadCodeTextBrightener;
                    var errorRenderer = isSecondary ? _secondaryErrorHighlightRenderer : _primaryErrorHighlightRenderer;
                    var errorDarkener = isSecondary ? _secondaryErrorTextDarkener : _primaryErrorTextDarkener;
                    var deadIndex = filteredDeadSpans.Count == 0
                        ? SpanOverlapIndex.Empty
                        : SpanOverlapIndex.Build(filteredDeadSpans, span => span.StartOffset,
                            span => (long)span.StartOffset + Math.Max(1, span.Length));
                    var errorIndex = filteredSpans.Count == 0
                        ? SpanOverlapIndex.Empty
                        : SpanOverlapIndex.Build(filteredSpans, span => span.StartOffset,
                            span => (long)span.StartOffset + Math.Max(1, span.Length));

                    deadRenderer.SetSpans(filteredDeadSpans, deadIndex);
                    deadBrightener.SetSpans(filteredDeadSpans);
                    errorRenderer.SetSpans(filteredSpans, errorIndex);
                    errorRenderer.SetDeadCodeSpans(filteredDeadSpans, deadIndex);
                    errorDarkener.SetSpans(filteredSpans, errorIndex, filteredDeadSpans, deadIndex);
                    RedrawVisibleEditorLines(editor);
                    editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"Inactive-tab diagnostics failed for '{tab.Path}'", ex); }
        finally { FinishInactiveTabDiagnosticScan(tab, cancellation); }
    }

    private static readonly string[] EditorStateRefreshProperties =
    [
        nameof(HasDocumentOpen), nameof(IsDocumentViewVisible), nameof(HasImagePreview), nameof(IsImagePreviewVisible),
        nameof(IsTextEditorVisible), nameof(CanToggleSplitEditor), nameof(CanShowFindInFile), nameof(CanShowSearchPanel), nameof(IsSearchPanelActive),
        nameof(IsPrimaryPaneImagePreviewVisible), nameof(IsSecondaryPaneImagePreviewVisible),
        nameof(IsPrimaryPaneCorruptedFileViewVisible), nameof(IsSecondaryPaneCorruptedFileViewVisible),
        nameof(IsPrimaryPaneTextEditorVisible), nameof(IsSecondaryPaneTextEditorVisible),
        nameof(CanShowSaveActions), nameof(IsWordCountVisible), nameof(HasFileOpen), nameof(IsFolderOpen),
        nameof(HomeQuickSearchPlaceholderText), nameof(WorkspaceFolderPath), nameof(IsEmptyStateVisible), nameof(HasRecentFiles), nameof(FileSummaryText),
        nameof(FilePathText), nameof(StatusBarFileIconText), nameof(StatusBarFilePathTooltip), nameof(IsDocumentDirty),
        nameof(StatusBarCaretText), nameof(StatusBarSelectionText), nameof(StatusBarDocumentText), nameof(HasSelection),
        nameof(StatusBarDiagnosticsText), nameof(StatusBarDiagnosticsTooltip), nameof(HasDiagnostics), nameof(ExplorerHeaderText),
        nameof(ExplorerHeaderTooltipText), nameof(ExplorerPanelMinWidth), nameof(DiscordRichPresenceStatusText), nameof(AutoSaveStatusText),
        nameof(LanguageDisplayText), nameof(EncodingDisplayText), nameof(LineEndingDisplayText), nameof(IsLineEndingVisible),
        nameof(IndentationDisplayText), nameof(IsIndentationVisible), nameof(ActiveTerminalWorkingDirectory), nameof(ActiveTerminalFooterText),
        nameof(TerminalStatusBarText)
    ];

    private void RefreshNonCaretState()
    {
        Title = BuildWindowTitle();
        RaiseMany(EditorStateRefreshProperties);
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
        CancelWordCountScan();
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
                _lastDiscordPresenceKey = default;
                _hasDiscordPresenceKey = false;
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

        var currentKey = GetDiscordPresenceKey();
        if (_hasDiscordPresenceKey && _lastDiscordPresenceKey == currentKey) return;

        try
        {
            var details = GetDiscordPresenceDetails();
            var state = GetDiscordPresenceState();

            _discordRpcClient.SetPresence(new DiscordRichPresenceModel
            {
                Details = details,
                State = state,
                Assets = new DiscordAssetsModel
                {
                    LargeImageKey = DefaultDiscordLargeImageKey,
                    LargeImageText = DefaultDiscordLargeImageText
                },
                Buttons = new[]
                {
                    new DiscordRPC.Button
                    {
                        Label = "Download Kodo",
                        Url = DownloadKodoUrl
                    }
                },
                Timestamps = new DiscordRPC.Timestamps(_sessionStart)
            });
            _lastDiscordPresenceKey = currentKey;
            _hasDiscordPresenceKey = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discord] Failed to update Rich Presence: {ex.Message}");
            ResetDiscordPresenceForReconnect();
            _discordReconnectTimer.Start();
        }
    }

    private (string? filePath, string? folderPath, int tabCount,
             bool untitled, bool imagePreview, bool settings, bool extensions, bool home) GetDiscordPresenceKey() =>
        (_currentFilePath, _currentFolderPath, OpenTabs.Count,
         _hasUntitledDocument, HasImagePreview, _isSettingsPageVisible, _isExtensionsPageVisible,
         _isHomePageVisible);

    private string GetDiscordPresenceDetails()
    {
        var version = KodoDiagnostics.AppVersion;
        if (version.EndsWith("-DEV", StringComparison.OrdinalIgnoreCase))
        {
            var stableVersion = version[..^4];
            var displayVersion = stableVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? stableVersion : $"v{stableVersion}";
            return $"Developing Kodo {displayVersion}";
        }

        if (_isSettingsPageVisible) return "Tweaking settings";
        if (_isExtensionsPageVisible) return "Browsing extensions";
        if (_isHomePageVisible) return "On the home screen";
        if (_isTutorialPageVisible) return "Following the tutorial";
        if (_isWhatsNewPageVisible) return "Reading what's new";

        if (HasDocumentOpen)
            return $"Editing {GetDocumentDisplayName()}";

        return IsFolderOpen ? "Browsing project files" : "Idle in Kodo";
    }

    private string GetDiscordPresenceState() =>
        HasFileOpen ? GetDiscordWorkspaceLabel()
        : _hasUntitledDocument ? GetDiscordWorkspaceLabel("Editing an unsaved file")
        : IsFolderOpen ? GetDiscordWorkspaceLabel()
        : "Waiting for a file";

    private string GetDiscordWorkspaceLabel(string fallback = "Working in editor")
    {
        if (!IsFolderOpen) return fallback;
        var folderName = Path.GetFileName(_currentFolderPath!.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) return fallback;
        var tabCount = OpenTabs.Count;
        return tabCount > 1
            ? $"Workspace: {folderName}  ({tabCount} files open)"
            : $"Workspace: {folderName}";
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
        catch { }
        finally
        {
            _discordRpcClient = null;
            _lastDiscordPresenceKey = default;
            _hasDiscordPresenceKey = false;
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
        catch { }
        finally
        {
            _discordRpcClient = null;
        }
    }

    private void DiscordReconnectTimer_OnTick(object? sender, EventArgs e)
    {
        _discordReconnectTimer.Stop();
        if (!IsDiscordRichPresenceEnabled) return;
        if (!IsActive) { _discordReconnectTimer.Interval = TimeSpan.FromSeconds(30); _discordReconnectTimer.Start(); return; }
        _discordReconnectTimer.Interval = TimeSpan.FromSeconds(10);
        UpdateDiscordRichPresenceLifecycle();
    }

    private static readonly KeybindDefinition[] KeybindDefinitions =
    {
        new("GoHome",             "Go to Home",             new KeyGesture(Key.H, KeyModifiers.Control),                        "Navigation"),
        new("QuickAccess",        "Quick Access: Go to File", new KeyGesture(Key.P, KeyModifiers.Control),                     "Navigation"),
        new("GoEditor",           "Go to Editor",            new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift),  "Navigation"),
        new("OpenSettings",       "Open Settings",           new KeyGesture(Key.OemComma, KeyModifiers.Control),                 "Navigation"),
        new("CommandPalette",     "Show All Commands",       new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift),  "Navigation"),
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
        new("ToggleSplitEditor",  "Split editor / close split", new KeyGesture(Key.OemPipe, KeyModifiers.Control),               "Editor"),
        new("FocusFirstEditorGroup",  "Focus first editor group",  new KeyGesture(Key.D1, KeyModifiers.Control),                 "Editor"),
        new("FocusSecondEditorGroup", "Focus second editor group", new KeyGesture(Key.D2, KeyModifiers.Control),                 "Editor"),
        new("GoToDefinition",    "Go to Definition",        new KeyGesture(Key.F12, KeyModifiers.None),                         "Editor"),
        new("EditorGoBack",      "Go Back in Editor Navigation", new KeyGesture(Key.Left, KeyModifiers.Control | KeyModifiers.Alt), "Navigation"),
        new("EditorGoForward",   "Go Forward in Editor Navigation", new KeyGesture(Key.Right, KeyModifiers.Control | KeyModifiers.Alt), "Navigation"),
        new("NextProblem",       "Go to Next Problem",      new KeyGesture(Key.F8, KeyModifiers.None),                           "Editor"),
        new("PreviousProblem",   "Go to Previous Problem",  new KeyGesture(Key.F8, KeyModifiers.Shift),                          "Editor"),
        new("ToggleLineComment",  "Toggle line comment",     new KeyGesture(Key.Oem2, KeyModifiers.Control),                     "Editor"),
        new("MoveLineUp",         "Move line up",            new KeyGesture(Key.Up, KeyModifiers.Alt),                             "Editor"),
        new("MoveLineDown",       "Move line down",          new KeyGesture(Key.Down, KeyModifiers.Alt),                           "Editor"),
        new("DuplicateLineUp",    "Duplicate line up",       new KeyGesture(Key.Up, KeyModifiers.Alt | KeyModifiers.Shift),         "Editor"),
        new("DuplicateLineDown",  "Duplicate line down",     new KeyGesture(Key.Down, KeyModifiers.Alt | KeyModifiers.Shift),       "Editor"),
        new("DeleteLine",         "Delete line",             new KeyGesture(Key.K, KeyModifiers.Control | KeyModifiers.Shift),      "Editor"),
        new("InsertLineAbove",    "Insert line above",       new KeyGesture(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift), "Editor"),
        new("InsertLineBelow",    "Insert line below",       new KeyGesture(Key.Enter, KeyModifiers.Control),                      "Editor"),
        new("ChangeAllOccurrences","Change all occurrences", new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift),      "Editor"),
        new("GoToLine",           "Go to line",              new KeyGesture(Key.G, KeyModifiers.Control),                         "Editor"),
        new("SelectCurrentLine",  "Select current line",      new KeyGesture(Key.L, KeyModifiers.Control),                         "Editor"),
        new("ToggleHighlightCurrentLine", "Toggle current line highlight", new KeyGesture(Key.K, KeyModifiers.Control | KeyModifiers.Alt), "Editor"),
        new("ScrollEditorLeft",     "Scroll editor left",         new KeyGesture(Key.Left, KeyModifiers.Alt),                      "Editor"),
        new("ScrollEditorRight",    "Scroll editor right",        new KeyGesture(Key.Right, KeyModifiers.Alt),                     "Editor"),
        new("ScrollEditorPageLeft", "Scroll editor left (page)",  new KeyGesture(Key.PageUp, KeyModifiers.Alt),                     "Editor"),
        new("ScrollEditorPageRight","Scroll editor right (page)", new KeyGesture(Key.PageDown, KeyModifiers.Alt),                   "Editor"),
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

    private HashSet<string> _disabledKeybinds = new(StringComparer.OrdinalIgnoreCase);

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
        _disabledKeybinds = settings.DisabledKeybinds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(settings.DisabledKeybinds, StringComparer.OrdinalIgnoreCase);
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

    private HashSet<string> BuildDisabledKeybindsSnapshot() =>
        new(_disabledKeybinds, StringComparer.OrdinalIgnoreCase);

    private bool IsKeybindEnabled(string id) => !_disabledKeybinds.Contains(id);

    private bool MatchesKeybind(KeyEventArgs e, string id) =>
        !_disabledKeybinds.Contains(id) &&
        _keybinds.TryGetValue(id, out var gesture) &&
        e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;

    private string SettingsFilePath => SettingsStore.FilePath;

    private AppSettings LoadSettings()
    {
        var settings = SettingsStore.Current;

        settings.ThemeName = string.IsNullOrWhiteSpace(settings.ThemeName) ? "Dark" : settings.ThemeName;
        settings.RecentFiles = settings.RecentFiles?
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .GroupBy(e => e.Path, FileSystemPaths.Comparer)
            .Select(g => g.OrderByDescending(e => e.IsPinned).ThenByDescending(e => e.LastOpened).First())
            .ToList() ?? [];
        settings.OpenTabPaths = settings.OpenTabPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(FileSystemPaths.Comparer)
            .ToList() ?? [];
        settings.TabSize = NormalizeTabSize(settings.TabSize);
        settings.TerminalPanelHeight = TerminalShellSupport.NormalizeTerminalPanelHeight(settings.TerminalPanelHeight);
        settings.ExplorerPanelWidth = NormalizeExplorerPanelWidth(settings.ExplorerPanelWidth);
        return settings;
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
            ThemeName = _requestedThemeName,
            AutoSaveEnabled = IsAutoSaveEnabled,
            DiscordRichPresenceEnabled = IsDiscordRichPresenceEnabled,
            GitSourceControlEnabled = IsGitSourceControlEnabled,
            DeveloperOptionsVisible = IsDeveloperOptionsVisible,
            VerboseLoggingEnabled = IsVerboseLoggingEnabled,
            StatusBarFilePathVisible = IsStatusBarFilePathVisible,
            WordWrapEnabled = IsWordWrapEnabled,
            MinimapVisible = IsMinimapVisible,
            InsightEnabled = IsInsightEnabled,
            InsightCodeSuggestionsEnabled = IsInsightCodeSuggestionsEnabled,
            InsightDeadCodeEnabled = IsInsightDeadCodeEnabled,
            InsightErrorDetectionEnabled = IsInsightErrorDetectionEnabled,
            InsightBlacklistExtensions = InsightBlacklistExtensions,
            InsightErrorDeadCodeBlacklistExtensions = InsightErrorDeadCodeBlacklistExtensions,
            DismissedDiagnostics = new HashSet<string>(_dismissedDiagnostics, StringComparer.Ordinal),
            TabSize = TabSize,
            InsertSpaces = InsertSpaces,
            EditorFontSize = EditorFontSize,
            EditorFontFamily = EditorFontFamily,
            TerminalScrollbackLines = TerminalScrollbackLines,
            ConfirmBeforeClosingUnsavedTabsEnabled = IsConfirmBeforeClosingUnsavedTabsEnabled,
            RestoreOpenTabsOnLaunchEnabled = IsRestoreOpenTabsOnLaunchEnabled,
            AutoUpdateExtensionsEnabled = IsAutoUpdateExtensionsEnabled,
            AutoUpdateExtensionsInBackgroundEnabled = IsAutoUpdateExtensionsInBackgroundEnabled,
            AutoUpdateAppEnabled = IsAutoUpdateAppEnabled,
            AutoUpdateAppInBackgroundEnabled = IsAutoUpdateAppInBackgroundEnabled,
            PerformanceModeEnabled = IsPerformanceModeEnabled,
            NewsDisabled = NewsDisabled,
            WhatsNewDisabled = WhatsNewDisabled,
            DebouncedSearchEnabled = IsDebouncedSearchEnabled,
            PreferredTerminalShellId = SelectedTerminalShell?.Id,
            PSReadLinePredictionEnabled = IsPSReadLinePredictionEnabled,
            TerminalVisible = IsTerminalVisible,
            TerminalPanelHeight = TerminalPanelHeight,
            ExplorerPanelWidth = ExplorerPanelWidth,
            HasCompletedTutorial = _hasCompletedTutorial,
            AccentColorMode = _accentColorMode,
            CustomAccentHex = _customAccentHex,
            CachedThemeAccentHex = _hasThemeAccent ? _themeAccentHex : null,
            CachedThemeWindowBackgroundHex = _hasWindowBackground ? _windowBackgroundHex : null,
            UserCountry = _userCountry,
            UserHemisphere = _userHemisphere,
            UserTimezoneOffset = _userTimezoneOffset,
            UserName = _userName,
            LastSeenVersion = CurrentAppVersion,
            AllowDataTracking = _isDataTrackingEnabled,
            HasRespondedToDataTrackingPrompt = _hasRespondedToDataTrackingPrompt,
            HasAcceptedPrivacyPolicy = _hasAcceptedPrivacyPolicy,
            OpenTabPaths = OpenTabs
                .Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path))
                .Select(tab => tab.Path)
                .Distinct(FileSystemPaths.Comparer)
                .ToList(),
            ActiveTabPath = ActiveEditorTab is { IsUntitled: false } activeTab
                ? activeTab.Path
                : null,
            SplitEditorVisible = IsSplitScreenVisible,
            PrimaryPaneTabPath = IsSplitScreenVisible && _editorPane.ActiveTab is { IsUntitled: false } primaryPaneTab
                ? primaryPaneTab.Path
                : null,
            SecondaryPaneTabPath = IsSplitScreenVisible && _secondaryEditorPane.ActiveTab is { IsUntitled: false } secondaryPaneTab
                ? secondaryPaneTab.Path
                : null,
            PrimaryPaneTabPaths = IsSplitScreenVisible
                ? _editorPane.Tabs.Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path))
                    .Select(tab => tab.Path!).Distinct(FileSystemPaths.Comparer).ToList()
                : [],
            SecondaryPaneTabPaths = IsSplitScreenVisible
                ? _secondaryEditorPane.Tabs.Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path))
                    .Select(tab => tab.Path!).Distinct(FileSystemPaths.Comparer).ToList()
                : [],
            ActivePaneIsSecondary = IsSplitScreenVisible && ReferenceEquals(_activeEditorPane, _secondaryEditorPane),
            LastOpenedFolderPath = _currentFolderPath,
            RecentFiles = RecentFiles
                .Select(f => new RecentFileEntry { Path = f.Path, IsFolder = f.IsFolder, LastOpened = f.LastOpened, IsPinned = f.IsPinned })
                .ToList(),
            CustomRunCommands = new Dictionary<string, string>(_customRunCommands, StringComparer.OrdinalIgnoreCase),
            CustomBuildCommands = new Dictionary<string, string>(_customBuildCommands, StringComparer.OrdinalIgnoreCase),
            CompilerOverrides = new Dictionary<string, string>(_compilerOverrides, StringComparer.OrdinalIgnoreCase),
            CustomBuildScripts = new Dictionary<string, string>(_customBuildScripts, StringComparer.OrdinalIgnoreCase),
            CustomKeybinds = BuildCustomKeybindsSnapshot(),
            DisabledKeybinds = BuildDisabledKeybindsSnapshot(),
            LspEnabled = _lspEnabled,
            LspCompletionEnabled = _lspCompletionEnabled,
            LspHoverEnabled = _lspHoverEnabled,
            LspSignatureHelpEnabled = _lspSignatureHelpEnabled,
            LspInlayHintsEnabled = _lspInlayHintsEnabled,
            LspFoldingEnabled = _lspFoldingEnabled,
            LspDocumentHighlightsEnabled = _lspDocumentHighlightsEnabled,
            LspDiagnosticsEnabled = _lspDiagnosticsEnabled,
            LspSemanticHighlightingEnabled = _lspSemanticHighlightingEnabled,
            LspAutoInstall = _lspAutoInstall,
            LspPreferManaged = _lspPreferManaged,
            LspPreferSystem = _lspPreferSystem,
            LspInstallDir = _lspInstallDir,
            LspExecutableOverrides = new Dictionary<string, string>(LspExecutableOverrides, StringComparer.OrdinalIgnoreCase),
            LspDisabledLanguages = new Dictionary<string, bool>(_lspDisabledLanguages, StringComparer.OrdinalIgnoreCase),
            LspDismissedInstallPrompts = new HashSet<string>(_lspDismissedInstallPrompts, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void PersistSettingsSnapshot(AppSettings snapshot, bool synchronous = false)
    {
        SettingsStore.Publish(snapshot);

        void WriteToDisk(AppSettings toWrite) => SettingsStore.WriteToDisk(toWrite);

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
            if (_isPersistingSettings) return;
            _isPersistingSettings = true;
        }

        Task.Run(() =>
        {
            while (true)
            {
                lock (_settingsWriteLock)
                {
                    if (_pendingSettingsSnapshot is null)
                    {
                        _isPersistingSettings = false;
                        return;
                    }
                    WriteToDisk(_pendingSettingsSnapshot);
                    _pendingSettingsSnapshot = null;
                }
            }
        });
    }

    public async void ActivateFromSecondaryInstance(string? filePath)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();

        var trimmedPath = filePath?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(trimmedPath))
        {
            if (Directory.Exists(trimmedPath))
                await OpenFolderFromPathAsync(trimmedPath);
            else if (File.Exists(trimmedPath))
                await OpenFileFromPathAsync(trimmedPath);
        }
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
        var document = IsImagePreviewFile(_currentFilePath) ? null : EditorTextBox?.Document;
        var content = string.Empty;
        var recoveredTab = new EditorTab(path, displayName, content, isUntitled: _currentFilePath is null)
        {
            IsDirty = _isDirty,
            Document = document
        };
        CaptureTabDiskState(recoveredTab);

        OpenTabs.Add(recoveredTab);
        ActivateTab(recoveredTab, focusEditor: false);
    }

    internal static TextDocument? CreateStartupRestoredDocument(string content, bool isCorrupted, bool isImagePreview) =>
        isCorrupted || isImagePreview ? null : new TextDocument(content);

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_OnOpened;

        if (_initialExtensionScanTask is not null)
            await _initialExtensionScanTask;

        ApplyThemeToEditor();
        var startupRestoreLoading = IsRestoreOpenTabsOnLaunchEnabled && _startupOpenTabPaths.Count > 0
            ? BeginAppLoading("Restoring startup session")
            : null;

        try
        {
            if (IsRestoreOpenTabsOnLaunchEnabled && _startupOpenTabPaths.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(_startupFolderPath) &&
                    _startupOpenTabPaths.Any(path => IsPathInsideDirectory(path, _startupFolderPath!)))
                {
                    await OpenFolderFromPathAsync(_startupFolderPath!);
                }

                var distinctPaths = _startupOpenTabPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .Distinct(FileSystemPaths.Comparer)
                    .ToList();

                if (distinctPaths.Count > 0)
                {
                    var readTasks = distinctPaths.Select(async path =>
                    {
                        try
                        {
                            if (IsImagePreviewFile(path))
                            {
                                return (path: (string?)path, content: string.Empty, lineEnding: Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF, isCorrupted: false, encoding: System.Text.Encoding.UTF8);
                            }
                            var (encoding, corrupted) = await Task.Run(() =>
                            {
                                if (IsBinaryContent(path))
                                    return (System.Text.Encoding.UTF8, true);
                                return (SearchEngine.DetectFileEncoding(path), false);
                            }).ConfigureAwait(false);

                            string content;
                            if (corrupted)
                            {
                                content = string.Empty;
                            }
                            else if (new FileInfo(path).Length > MaxFileSizeForFullLoad)
                            {
                                content = await ReadLargeFileAsync(path, encoding).ConfigureAwait(false);
                            }
                            else
                            {
                                content = await File.ReadAllTextAsync(path, encoding).ConfigureAwait(false);
                            }

                            var lineEnding = !corrupted && !string.IsNullOrEmpty(content)
                                ? DetectLineEnding(content)
                                : Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;

                            return (path: (string?)path, content, lineEnding, isCorrupted: corrupted, encoding);
                        }
                        catch (Exception ex)
                        {
                            KodoDiagnostics.LogDebug($"Unable to restore startup tab '{path}'", ex);
                            return (path: (string?)null, content: string.Empty,
                                lineEnding: Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF,
                                isCorrupted: false, encoding: System.Text.Encoding.UTF8);
                        }
                    }).ToArray();

                    var results = await Task.WhenAll(readTasks);

                    EditorTab? tabToActivate = null;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var path in _startupOpenTabPaths)
                        {
                            var result = results.FirstOrDefault(r => FileSystemPaths.Equals(r.path, path));
                            if (result.path is null) continue;
                            if (OpenTabs.Any(t => !t.IsUntitled && FileSystemPaths.Equals(t.Path, result.path)))
                                continue;

                            var restoredDocument = CreateStartupRestoredDocument(
                                result.content,
                                result.isCorrupted,
                                IsImagePreviewFile(result.path));
                            var tab = new EditorTab(result.path, Path.GetFileName(result.path), string.Empty, lineEnding: result.lineEnding)
                            {
                                Encoding = result.encoding,
                                Document = restoredDocument
                            };
                            if (result.isCorrupted)
                                _corruptedTabs.Add(tab);
                            CaptureTabDiskState(tab);
                            OpenTabs.Add(tab);
                            AddRecentFile(result.path);
                        }

                        if (!string.IsNullOrWhiteSpace(_startupActiveTabPath))
                        {
                            var activeTab = OpenTabs.FirstOrDefault(tab =>
                                !tab.IsUntitled &&
                                FileSystemPaths.Equals(tab.Path, _startupActiveTabPath));
                            tabToActivate = activeTab ?? (OpenTabs.Count > 0 ? OpenTabs[0] : null);
                        }
                        else if (OpenTabs.Count > 0 && ActiveEditorTab is null)
                        {
                            tabToActivate = OpenTabs[0];
                        }
                    }, DispatcherPriority.MaxValue);
                    if (tabToActivate is not null)
                    {
                        var largeContentLength = tabToActivate.Document?.TextLength ?? tabToActivate.Content.Length;
                        if (largeContentLength > 120_000)
                            await Task.Delay(TimeSpan.FromMilliseconds(60));
                        var activationVersion = -1;
                        EditorPaneState? activationPane = null;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (OpenTabs.Contains(tabToActivate))
                            {
                                if (!ReferenceEquals(_activeEditorPane.ActiveTab, tabToActivate))
                                    ActivateTab(tabToActivate, scheduleContentLoad: false);
                                activationVersion = _activeTabVersion;
                                activationPane = _activeEditorPane;
                            }
                        });
                        if (activationPane is not null)
                        {
                            await SetEditorTabContentSafelyAsync(tabToActivate, activationVersion, activationPane);
                            await EnsurePaneDocumentHasContentAsync(activationPane, tabToActivate);
                        }
                    }
                    RaiseMany(nameof(IsHomePageVisible), nameof(IsEditorPageVisible), nameof(IsHomeOrEditorPageVisible), nameof(IsEditorTabsVisible), nameof(IsSharedEditorTabsVisible), nameof(IsDocumentViewVisible), nameof(HasOpenEditors));
                    RefreshState(fullRefresh: true);
                }
            }

            if (IsRestoreOpenTabsOnLaunchEnabled && _startupSplitEditorVisible && _startupOpenTabPaths.Count > 0)
                await RestoreStartupSplitEditorAsync();

            if (!string.IsNullOrWhiteSpace(_startupFilePath))
            {
                if (Directory.Exists(_startupFilePath))
                    await OpenFolderFromPathAsync(_startupFilePath);
                else
                    await OpenFileFromPathAsync(_startupFilePath);
            }
        }
        finally
        {
            _suppressSettingsSave = false;

            if (!_isFirstLaunch)
            {
                SaveSettings(immediate: true);
            }
            startupRestoreLoading?.Dispose();
        }

        InitializeDeferredStartupWork();
        UpdateGitSourceControlLifecycle();

        var isReturningUser = _hasCompletedTutorial;

        if (_isFirstLaunch && !_hasCompletedTutorial)
        {
            await ShowTutorialAsync();
        }
        else if (isReturningUser)
        {
            var showReleaseNotes = !IsDevBuild && IsCurrentNewerThanLastSeen(_lastSeenVersion);
            var showConsentAsk = !_hasRespondedToDataTrackingPrompt || !_hasAcceptedPrivacyPolicy;

            _openingSplashShowsReleaseNotes = showReleaseNotes;
            if (showReleaseNotes || showConsentAsk)
            {
                if (!_hasAcceptedPrivacyPolicy)
                    ResetPrivacyPolicyScrollState();
                IsUpdateSplashVisible = true;
            }
        }
    }

    private void InitializeDeferredStartupWork()
    {

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                await Dispatcher.UIThread.InvokeAsync(SetupExtensionFolderWatchers);
            }
            catch { }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NetworkChange.NetworkAvailabilityChanged += NetworkChange_OnNetworkAvailabilityChanged;
                    NetworkChange.NetworkAddressChanged += NetworkChange_OnNetworkAddressChanged;
                    RefreshMarketplaceConnectivityState();

                    UpdateExtensionAutoUpdateLifecycle();
                    _appUpdateScheduler.UpdateLifecycle();
                    _marketplaceRefreshTimer.Start();

                    if (SystemThemeHelper.SupportsAccent)
                    {
                        _lastSeenWindowsAccentHex = GetWindowsAccentColor() ?? string.Empty;
                        _windowsAccentPollTimer.Tick += WindowsAccentPollTimer_OnTick;
                        _windowsAccentPollTimer.Start();

                        _lastSeenWindowsThemeName = ResolveSystemThemeName();
                        _windowsThemePollTimer.Tick += WindowsThemePollTimer_OnTick;
                        _windowsThemePollTimer.Start();
                    }
                });

            }
            catch { }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                var shells = await Task.Run(() => TerminalShellSupport.DetectTerminalShells(_isPSReadLinePredictionEnabled).ToList());
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var preferred = _deferredPreferredShellId;
                    AvailableTerminalShells.Clear();
                    foreach (var shell in shells)
                        AvailableTerminalShells.Add(shell);
                    var platformDefaultId = TerminalShellSupport.GetDefaultShellId();
                    SelectedTerminalShell = AvailableTerminalShells.FirstOrDefault(s =>
                            string.Equals(s.Id, preferred, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableTerminalShells.FirstOrDefault(s =>
                            string.Equals(s.Id, platformDefaultId, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableTerminalShells.FirstOrDefault();
                });
            }
            catch { }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                await Dispatcher.UIThread.InvokeAsync(UpdateDiscordRichPresenceLifecycle);
            }
            catch { }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await RunStaggeredStartupLoadingAsync());
            }
            catch { }
        });
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
        using var loading = BeginAppLoading($"Opening {Path.GetFileName(path)}");
        _currentFolderPath = path;
        _searchFileCache = null;
        InvalidateLspResolutionCaches();
        InvalidateDotnetProjectCache();
        AddRecentFolder(path);
        await PopulateFileTreeAsync(path);
        if (!FileSystemPaths.Equals(_currentFolderPath, path))
            return;
        SetupProjectFolderWatcher(path);
        IsFileExplorerVisible = true;
        RefreshState(fullRefresh: true);
        RefreshRunBuildState();
    }

    private void CloseFolder()
    {
        _fileTreeLoadGeneration++;
        DisposeProjectFolderWatcher();
        _currentFolderPath = null;
        _searchFileCache = null;
        InvalidateLspResolutionCaches();
        InvalidateDotnetProjectCache();
        FileTreeItems.Clear();
        IsFileExplorerVisible = false;
        RefreshState(fullRefresh: true);
        RefreshRunBuildState();
        SaveSettings(immediate: true, synchronous: true);
    }

    private async Task<bool> SaveAsync(bool allowPromptForPath, bool forcePromptForPath)
    {
        _autoSaveTimer.Stop();
        if (_isSaving) return false;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var originalFilePath = requestTab is { IsUntitled: false } ? requestTab.Path : null;
        var requestDocument = requestEditor.Document;
        if (HasImagePreview) return false;
        if (ActiveEditorTab is not null && _corruptedTabs.Contains(ActiveEditorTab))
        {
            await ReportSaveRefusedAsync(
                $"\"{ActiveEditorTab.DisplayName}\" is a binary or corrupted file, so it cannot be saved as text. The original bytes were left untouched.");
            return false;
        }

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

            if (!ReferenceEquals(_activeEditorPane, requestPane) || !ReferenceEquals(EditorTextBox, requestEditor) ||
                !ReferenceEquals(requestPane.ActiveTab, requestTab) || !ReferenceEquals(requestEditor.Document, requestDocument))
                return false;

            var newPath = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(newPath)) return false;

            var duplicateTab = OpenTabs.FirstOrDefault(t => !t.IsUntitled && t.Path is not null && FileSystemPaths.Equals(t.Path, newPath) && t != ActiveEditorTab);
            if (duplicateTab is not null)
            {
                ExtensionsStatusText = $"Save failed: \"{Path.GetFileName(newPath)}\" is already open in another tab.";
                await ShowWarningDialogAsync("Save File", new InvalidOperationException($"\"{newPath}\" is already open. Close the other tab or save with a different name."));
                return false;
            }

            _currentFilePath = newPath;
            EditorSession.SetFilePath(newPath);
            _hasUntitledDocument = false;
            if (ActiveEditorTab is not null)
                ActiveEditorTab.Rename(newPath, Path.GetFileName(newPath));
            if (ActiveEditorTab is { } renamedTab)
            {
                RefreshSyntaxHighlightingForTabInPanes(renamedTab);
                foreach (var pane in new[] { _editorPane, _secondaryEditorPane })
                {
                    if (!ReferenceEquals(pane.ActiveTab, renamedTab))
                        continue;

                    ResetPaneLspPresentation(pane);
                }
            }
            OnPropertyChanged(nameof(PrimaryPaneBreadcrumbText));
            OnPropertyChanged(nameof(SecondaryPaneBreadcrumbText));
            ClearAutoSaveStatus();
            RefreshCurrentFileSyntaxHighlighting();
            QueueInsightRefresh();
        }

        var savingTab = ActiveEditorTab;
        var savingPath = _currentFilePath;
        var savingDocument = EditorTextBox.Document;
        var savingDocumentVersion = savingDocument.Version;
        var savingLineEnding = _currentLineEnding;
        var savingSnapshot = EditorTextSnapshotStore.GetSnapshot(savingDocument);
        var expectedDiskWriteTimeUtc = !shouldPromptForPath && savingTab is { LastKnownLength: >= 0 }
            ? savingTab.LastKnownWriteTimeUtc
            : (DateTime?)null;
        var expectedDiskLength = !shouldPromptForPath && savingTab is { LastKnownLength: >= 0 }
            ? savingTab.LastKnownLength
            : (long?)null;

        if (!shouldPromptForPath && savingTab is not null && IsTabDiskStateChanged(savingTab))
        {
            await ReportSaveRefusedAsync(
                $"\"{Path.GetFileName(savingPath!)}\" changed on disk since it was opened, so it was not overwritten. Use Save As to keep your changes under a new name, or reload the file to pick up the newer version.");
            return false;
        }

        if (savingTab is { IsUntitled: false, IsDirty: false, LastKnownLength: > 0 } &&
            savingSnapshot.Source.TextLength == 0 && !IsTabDiskStateChanged(savingTab))
        {
            await ReportSaveRefusedAsync(
                $"\"{Path.GetFileName(savingPath!)}\" is still marked clean, but its editor buffer is empty while the file on disk contains {savingTab.LastKnownLength:N0} bytes. The file was not overwritten.");
            return false;
        }

        if (savingTab is { IsUntitled: false, IsDirty: true, LastKnownLength: > 0 } &&
            savingSnapshot.Source.TextLength == 0 && !IsTabDiskStateChanged(savingTab))
        {
            var confirmed = await ShowConfirmationDialogAsync(
                "Save an empty file?",
                $"The editor buffer for \"{Path.GetFileName(savingPath!)}\" is empty, but the existing file contains {savingTab.LastKnownLength:N0} bytes. Replace the file with an empty file?",
                "Replace with Empty File",
                "Keep Existing File",
                isDestructive: true);
            if (!confirmed || !ReferenceEquals(_activeEditorPane, requestPane) ||
                !ReferenceEquals(requestPane.ActiveTab, savingTab) || !ReferenceEquals(EditorTextBox.Document, savingDocument) ||
                !ReferenceEquals(savingDocument.Version, savingDocumentVersion))
                return false;
        }

        var savingEncoding = savingTab?.Encoding ?? _currentFileEncoding;

        try
        {
            _isSaving = true;
            if (IsAutoSaveEnabled && HasFileOpen)
            {
                _autoSaveStatusMessage = AutoSaveSavingMessage;
                _autoSaveStatusTimer.Stop();
                OnPropertyChanged(nameof(FileSummaryText));
            }

            var saveWatch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => WriteTextFileAtomicallyAsync(
                savingPath!, savingSnapshot, savingLineEnding, savingEncoding,
                expectedDiskWriteTimeUtc, expectedDiskLength,
                async () => await Dispatcher.UIThread.InvokeAsync(() => savingTab is null || FileSystemPaths.Equals(savingTab.Path, savingPath))));
            saveWatch.Stop();
            KodoDiagnostics.ReportSlowStage("save convert+write", saveWatch.ElapsedMilliseconds, 1000, $"len={savingSnapshot.Source.TextLength}");

            var targetPathMatchesTab = savingTab is null || FileSystemPaths.Equals(savingTab.Path, savingPath);
            if (targetPathMatchesTab && ReferenceEquals(savingDocument.Version, savingDocumentVersion))
            {
                if (savingTab is not null)
                {
                    savingTab.Content = string.Empty;
                    savingTab.IsDirty = false;
                }
                if (ReferenceEquals(ActiveEditorTab, savingTab))
                {
                    _isDirty = false;
                    OnPropertyChanged(nameof(IsDocumentDirty));
                }
            }
            else
            {
                KodoDiagnostics.LogDebug($"Save raced with edits for '{savingPath}' - leaving tab dirty");
                if (savingTab is not null)
                    savingTab.IsDirty = true;
            }
            if (savingTab is not null && savingTab.IsUntitled)
            {
                savingTab.IsUntitled = false;
                RefreshSyntaxHighlightingForTabInPanes(savingTab);
            }
            if (savingTab is not null && targetPathMatchesTab)
            {
                CaptureTabDiskState(savingTab);
                savingTab.IsModifiedExternally = false;
                if (ReferenceEquals(_editorPane.ActiveTab, savingTab))
                    RefreshPanePreviewState(_editorPane);
                if (ReferenceEquals(_secondaryEditorPane.ActiveTab, savingTab))
                    RefreshPanePreviewState(_secondaryEditorPane);
            }
            if (ReferenceEquals(ActiveEditorTab, savingTab))
            {
                if (shouldPromptForPath)
                    UpdateCurrentDocumentPresentation();
                else
                    RefreshRunBuildState();
            }
            AddRecentFile(savingPath);

            if (IsAutoSaveEnabled && HasFileOpen && ReferenceEquals(ActiveEditorTab, savingTab))
            {
                if (ReferenceEquals(savingDocument.Version, savingDocumentVersion))
                {
                    _autoSaveStatusMessage = AutoSaveSavedMessage;
                    _autoSaveStatusTimer.Stop();
                    _autoSaveStatusTimer.Start();
                }
                else
                {
                    _autoSaveStatusMessage = AutoSaveSavingMessage;
                    _autoSaveStatusTimer.Stop();
                    OnPropertyChanged(nameof(FileSummaryText));
                }
            }

            if (!string.IsNullOrWhiteSpace(savingPath) && ResolveLspExtensionForFile(savingPath) is not null)
                _ = FlushLspDocumentAsync(savingPath, string.Empty);
            if (shouldPromptForPath && savingTab is not null && !string.IsNullOrWhiteSpace(savingPath))
            {
                if (!string.IsNullOrWhiteSpace(originalFilePath) &&
                    !FileSystemPaths.Equals(originalFilePath, savingPath) &&
                    IsLspDocumentOpenOrOpening(originalFilePath))
                    _ = RetargetLspDocumentAsync(originalFilePath, savingPath, savingTab);
                else
                    _ = LspNotifyActiveDocumentDidOpenAsync(savingPath);
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

    private async Task ReportSaveRefusedAsync(string reason)
    {
        _autoSaveStatusTimer.Stop();
        _autoSaveStatusMessage = reason;
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(AutoSaveStatusText));
        await ShowWarningDialogAsync("File save", new IOException(reason));
    }

    private static Task WriteTextFileAtomicallyAsync(
        string path,
        string text,
        System.Text.Encoding encoding,
        DateTime? expectedWriteTimeUtc = null,
        long? expectedLength = null,
        Func<Task<bool>>? validateBeforeCommit = null) =>
        WriteTextFileAtomicallyCoreAsync(
            path, encoding, writer => writer.WriteAsync(text), expectedWriteTimeUtc, expectedLength, validateBeforeCommit);

    private static Task WriteTextFileAtomicallyAsync(
        string path,
        EditorTextSnapshot snapshot,
        Kodo.Models.LineEnding lineEnding,
        System.Text.Encoding encoding,
        DateTime? expectedWriteTimeUtc = null,
        long? expectedLength = null,
        Func<Task<bool>>? validateBeforeCommit = null) =>
        WriteTextFileAtomicallyCoreAsync(
            path, encoding, writer => WriteEditorSnapshotAsync(writer, snapshot, lineEnding),
            expectedWriteTimeUtc, expectedLength, validateBeforeCommit);

    private static async Task WriteTextFileAtomicallyCoreAsync(
        string path,
        System.Text.Encoding encoding,
        Func<StreamWriter, Task> writeContent,
        DateTime? expectedWriteTimeUtc = null,
        long? expectedLength = null,
        Func<Task<bool>>? validateBeforeCommit = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("Save path has no parent directory.");
        Directory.CreateDirectory(directory);

        var unixMode = OperatingSystem.IsWindows() ? null : ReadUnixMode(fullPath);
        if (unixMode is not null && (unixMode.Value & UnixFileMode.UserWrite) == 0)
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");

        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (unixMode.HasValue && !OperatingSystem.IsWindows())
                options.UnixCreateMode = unixMode.Value;

            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream, encoding, 1024, leaveOpen: true))
            {
                await writeContent(writer);
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (unixMode.HasValue && !OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tempPath, unixMode.Value); } catch { }
            }

            if (expectedWriteTimeUtc is { } expectedTime && expectedLength is { } expectedFileLength)
            {
                var currentInfo = new FileInfo(fullPath);
                if (!currentInfo.Exists || currentInfo.LastWriteTimeUtc != expectedTime || currentInfo.Length != expectedFileLength)
                    throw new IOException($"\"{fullPath}\" changed on disk while Kodo was saving it. The existing file was left untouched.");
            }

            if (validateBeforeCommit is not null && !await validateBeforeCommit())
                throw new IOException($"The editor tab changed paths while Kodo was saving \"{fullPath}\". The file was left untouched.");

            await CommitTempFileAsync(tempPath, fullPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static async Task WriteEditorSnapshotAsync(
        StreamWriter writer,
        EditorTextSnapshot snapshot,
        Kodo.Models.LineEnding lineEnding)
    {
        var newline = lineEnding switch
        {
            Kodo.Models.LineEnding.CRLF => "\r\n",
            Kodo.Models.LineEnding.CR => "\r",
            Kodo.Models.LineEnding.LF => "\n",
            _ => Environment.NewLine
        };
        var input = new char[32 * 1024];
        var output = new char[32 * 1024 + 2];
        var outputLength = 0;
        var pendingCarriageReturn = false;
        using var reader = snapshot.Source.CreateReader();

        void AppendNewline()
        {
            newline.AsSpan().CopyTo(output.AsSpan(outputLength));
            outputLength += newline.Length;
        }

        async Task FlushOutputAsync()
        {
            if (outputLength == 0) return;
            await writer.WriteAsync(output.AsMemory(0, outputLength));
            outputLength = 0;
        }

        int read;
        while ((read = await reader.ReadAsync(input.AsMemory())) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var current = input[i];
                if (pendingCarriageReturn)
                {
                    AppendNewline();
                    pendingCarriageReturn = false;
                    if (current == '\n')
                    {
                        if (outputLength >= 32 * 1024)
                            await FlushOutputAsync();
                        continue;
                    }
                }

                if (current == '\r')
                    pendingCarriageReturn = true;
                else if (current == '\n')
                    AppendNewline();
                else
                    output[outputLength++] = current;

                if (outputLength >= 32 * 1024)
                    await FlushOutputAsync();
            }
        }

        if (pendingCarriageReturn)
            AppendNewline();
        await FlushOutputAsync();
    }

    private static UnixFileMode? ReadUnixMode(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path)) return null;
            return File.GetUnixFileMode(path);
        }
        catch
        {
            return null;
        }
    }

    private static async Task CommitTempFileAsync(string tempPath, string targetPath)
    {
        try
        {
            File.Move(tempPath, targetPath, overwrite: true);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && File.Exists(targetPath) && (ex is IOException or UnauthorizedAccessException))
        {
            KodoDiagnostics.LogDebug($"Atomic replace of '{targetPath}' was blocked by an open handle - falling back to an in-place write.");
        }

        await using (var source = new FileStream(
                         tempPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         targetPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, 128 * 1024);
            await destination.FlushAsync();
            destination.Flush(flushToDisk: true);
        }
        File.Delete(tempPath);
    }

    private void ProjectFolderWatcher_OnError(object sender, ErrorEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RecheckDirtyTabsForExternalChanges();
            RestartFileTreeRefreshTimer();
        });

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

        if (_isSettingsPageVisible) return "Settings";
        if (_isExtensionsPageVisible) return "Extensions";
        if (_isTutorialPageVisible) return "Tutorial";
        if (_isHomePageVisible) return $"Kodo{birthday}";

        if (HasDocumentOpen)
        {
            var dirty = _isDirty ? "● " : string.Empty;
            var file = GetDocumentDisplayName();
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

    private void SetEditorContent(string content)
    {
        SetEditorContentCore(null, content, content.Length);
    }

    private async Task SetEditorContentAsync(
        string content,
        int expectedActivationVersion,
        EditorPaneState? expectedPane = null,
        EditorTab? expectedTab = null)
    {
        var dispatchQueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_activeTabVersion != expectedActivationVersion ||
                (expectedPane is not null && !ReferenceEquals(_activeEditorPane, expectedPane)) ||
                (expectedPane is not null && expectedTab is not null &&
                    !ReferenceEquals(expectedPane.ActiveTab, expectedTab))) return;
            SetEditorContent(new TextDocument(content));
        }, DispatcherPriority.Normal);
        KodoDiagnostics.ReportSlowStage(
            "editor document install dispatch",
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(dispatchQueuedAt).TotalMilliseconds,
            50,
            $"len={content.Length}");
    }

    private void SetEditorContent(TextDocument document)
    {
        Dispatcher.UIThread.VerifyAccess();
        SetEditorContentCore(document, null, document.TextLength, preserveUndoHistory: false);
    }

    private void SetEditorContent(TextDocument document, bool preserveUndoHistory)
    {
        Dispatcher.UIThread.VerifyAccess();
        SetEditorContentCore(document, null, document.TextLength, preserveUndoHistory);
    }

    private void SetEditorContentCore(TextDocument? replacementDocument, string? content, int contentLength, bool preserveUndoHistory = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        var contentWatch = System.Diagnostics.Stopwatch.StartNew();
        var documentReferenceChanges = replacementDocument is not null && !ReferenceEquals(EditorTextBox.Document, replacementDocument);
        _suppressDirtyTracking = true;
        _editorHorizontalScrollViewerResolved = false;
        _editorHorizontalScrollViewer = null;
        try
        {
            if (replacementDocument is not null)
            {
                EditorTextBox.Document = replacementDocument;
                var otherPane = ReferenceEquals(_activeEditorPane, _editorPane)
                    ? _secondaryEditorPane
                    : _editorPane;
                var otherEditor = ReferenceEquals(otherPane, _secondaryEditorPane)
                    ? SecondaryEditorTextBox
                    : PrimaryEditorTextBox;
                if (ActiveEditorTab is { } activeTab && ReferenceEquals(otherPane.ActiveTab, activeTab) &&
                    !ReferenceEquals(otherEditor.Document, replacementDocument))
                    otherEditor.Document = replacementDocument;
            }
            else if (EditorTextBox.Document.TextLength != contentLength || contentLength > 0)
                EditorTextBox.Document.Text = content ?? string.Empty;
            if (!preserveUndoHistory)
                EditorTextBox.Document.UndoStack.ClearAll();
            if (ActiveEditorTab is not null)
            {
                ActiveEditorTab.Document = EditorTextBox.Document;
                ActiveEditorTab.Content = string.Empty;
            }
            EditorTextBox.TextArea.ClearSelection();
            EditorTextBox.TextArea.Caret.Offset = 0;
            RefreshCurrentFileSyntaxHighlighting();
            if (!documentReferenceChanges)
            {
                EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
                EditorTextBox.TextArea.TextView.Redraw();
            }
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
        contentWatch.Stop();
        KodoDiagnostics.ReportSlowStage("editor content set", contentWatch.ElapsedMilliseconds, 1000, $"len={contentLength}");
        ArmLayoutProbe("layout/after content set");
        QueueInsightRefresh();
    }

    private void RefreshAvailableTerminalShells(string? preferredShellId = null)
    {
        AvailableTerminalShells.Clear();

        foreach (var shell in TerminalShellSupport.DetectTerminalShells(_isPSReadLinePredictionEnabled))
            AvailableTerminalShells.Add(shell);

        var platformDefaultId = TerminalShellSupport.GetDefaultShellId();
        SelectedTerminalShell = AvailableTerminalShells.FirstOrDefault(shell =>
            string.Equals(shell.Id, preferredShellId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableTerminalShells.FirstOrDefault(shell =>
                string.Equals(shell.Id, platformDefaultId, StringComparison.OrdinalIgnoreCase))
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
        if (!IsTerminalSupported) return;

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

        try
        {
            CreateTerminalSessionCore(shell, replaceExisting, workingDirectoryOverride);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogCritical("MainWindow.CreateTerminalSession", ex, isTerminating: false,
                operation: "Create terminal session");
            ExtensionsStatusText = $"Failed to create terminal: {ex.Message}";
            IsTerminalVisible = true;
        }
    }

    private void CreateTerminalSessionCore(TerminalShellOption? shell, TerminalSession? replaceExisting, string? workingDirectoryOverride)
    {
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
    }

    private void FocusActiveTerminal()
    {
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

    private void TerminalHostControl_OnDetachedSessionExited(object? sender, TerminalProcessHandle handle)
    {
        var session = TerminalSessions.FirstOrDefault(t => ReferenceEquals(t.LiveHandle, handle));
        if (session is null)
        {
            TerminalHostControl.DestroyHandle(handle);
            return;
        }
        if (ReferenceEquals(session, ActiveTerminalSession))
        {
            session.IsRunning = false;
            CloseTerminalSession(session);
            RefreshTerminalStatusBindings();
            return;
        }
        session.IsRunning = false;
        session.StatusText = "Exited";
        RefreshTerminalStatusBindings();
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
            session.LiveHandle = null;
        }
        else if (session.LiveHandle is { } detached)
        {
            TerminalHostControl.DestroyHandle(detached);
            session.LiveHandle = null;
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
            CloseTerminalSession(session, activateReplacement: false);

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
        var newHome = page == AppPage.Home;
        var newSettings = page == AppPage.Settings;
        var newExtensions = page == AppPage.Extensions;
        var newTutorial = page == AppPage.Tutorial;
        var newWhatsNew = page == AppPage.WhatsNew;

        if (_isHomePageVisible == newHome &&
            _isSettingsPageVisible == newSettings &&
            _isExtensionsPageVisible == newExtensions &&
            _isTutorialPageVisible == newTutorial &&
            _isWhatsNewPageVisible == newWhatsNew)
            return;

        _isHomePageVisible = newHome;
        _isSettingsPageVisible = newSettings;
        _isExtensionsPageVisible = newExtensions;
        _isTutorialPageVisible = newTutorial;
        _isWhatsNewPageVisible = newWhatsNew;

        if (_isUpdateSplashVisible)
            IsUpdateSplashVisible = false;

        CloseCompletionWindow();
        HideDiagnosticPopup();
        IsSearchPanelVisible = false;

        RaiseMany(nameof(IsHomePageVisible), nameof(IsSettingsPageVisible), nameof(IsExtensionsPageVisible), nameof(IsTutorialPageVisible), nameof(AreSidebarButtonsEnabled), nameof(IsWhatsNewPageVisible), nameof(IsEditorPageVisible), nameof(IsHomeOrEditorPageVisible), nameof(IsSearchPanelActive), nameof(IsEditorTabsVisible), nameof(IsSharedEditorTabsVisible), nameof(IsDocumentViewVisible), nameof(IsEmptyStateVisible), nameof(CanShowSaveActions), nameof(FileSummaryText), nameof(FilePathText));
        RefreshState(fullRefresh: true);
    }

    private void EditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        ShowEditorPage();
    }

    public void ShowEditorPage()
    {
        NavigateTo(AppPage.Editor);
        FocusEditor();
    }

    private void HomeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        NavigateTo(AppPage.Home);
    }

    private async void OpenFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        await OpenFileAsync();
    }

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

        var originalActivePane = _activeEditorPane;
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
                    {
                        var tabPane = ReferenceEquals(_editorPane.ActiveTab, tab)
                            ? _editorPane
                            : ReferenceEquals(_secondaryEditorPane.ActiveTab, tab)
                                ? _secondaryEditorPane
                                : _activeEditorPane;
                        ActivateEditorPane(ResolveEditorPaneForOpen(tabPane), focusEditor: false);
                        ActivateTab(tab, focusEditor: false, preserveCurrentState: false);
                    }

                    await SaveAsync(allowPromptForPath: true, forcePromptForPath: true);
                }
                else
                {
                    if (_corruptedTabs.Contains(tab))
                        continue;

                    if (IsTabDiskStateChanged(tab))
                    {
                        ExtensionsStatusText = $"Save failed: \"{Path.GetFileName(tab.Path)}\" changed on disk and was left untouched.";
                        await ShowWarningDialogAsync("File save", new IOException($"\"{tab.Path}\" changed on disk since it was opened. Kodo did not overwrite it."));
                        continue;
                    }

                    try
                    {
                        var tabSource = await Dispatcher.UIThread.InvokeAsync(() =>
                            (Document: tab.Document, Version: tab.Document?.Version, Content: tab.Content ?? string.Empty,
                                Path: tab.Path, LineEnding: tab.LineEnding, Encoding: tab.Encoding ?? _currentFileEncoding,
                                LastWriteTimeUtc: tab.LastKnownWriteTimeUtc, LastLength: tab.LastKnownLength));
                        var encToUse = tabSource.Encoding;
                        if (encToUse == System.Text.Encoding.UTF8 && File.Exists(tabSource.Path))
                        {
                            try { encToUse = await Task.Run(() => SearchEngine.DetectFileEncoding(tabSource.Path)); } catch { }
                        }
                        if (tabSource.Document is { } tabDocument)
                        {
                            var tabSnapshot = EditorTextSnapshotStore.GetSnapshot(tabDocument);
                            await Task.Run(() => WriteTextFileAtomicallyAsync(
                                tabSource.Path, tabSnapshot, tabSource.LineEnding, encToUse,
                                tabSource.LastLength >= 0 ? tabSource.LastWriteTimeUtc : null,
                                tabSource.LastLength >= 0 ? tabSource.LastLength : null,
                                async () => await Dispatcher.UIThread.InvokeAsync(() => FileSystemPaths.Equals(tab.Path, tabSource.Path))));
                        }
                        else
                        {
                            var textToSave = await Task.Run(() => ConvertToLineEnding(tabSource.Content, tabSource.LineEnding));
                            await WriteTextFileAtomicallyAsync(
                                tabSource.Path,
                                textToSave,
                                encToUse,
                                tabSource.LastLength >= 0 ? tabSource.LastWriteTimeUtc : null,
                                tabSource.LastLength >= 0 ? tabSource.LastLength : null,
                                async () => await Dispatcher.UIThread.InvokeAsync(() => FileSystemPaths.Equals(tab.Path, tabSource.Path)));
                        }
                        var unchanged = await Dispatcher.UIThread.InvokeAsync(() => tabSource.Document is not null
                            ? FileSystemPaths.Equals(tab.Path, tabSource.Path) && ReferenceEquals(tab.Document, tabSource.Document) && ReferenceEquals(tabSource.Document.Version, tabSource.Version)
                            : FileSystemPaths.Equals(tab.Path, tabSource.Path) && tab.Document is null && string.Equals(tab.Content ?? string.Empty, tabSource.Content, StringComparison.Ordinal));
                        if (unchanged)
                        {
                            tab.Content = string.Empty;
                            tab.IsDirty = false;
                        }
                        else
                        {
                            tab.IsDirty = true;
                            KodoDiagnostics.LogDebug($"Save All raced with edits for '{tab.Path}' - leaving tab dirty");
                        }
                        if (FileSystemPaths.Equals(tab.Path, tabSource.Path))
                        {
                            tab.Encoding = encToUse;
                            tab.IsModifiedExternally = false;
                            CaptureTabDiskState(tab);
                        }
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
            if (!ReferenceEquals(_activeEditorPane, originalActivePane))
                ActivateEditorPane(originalActivePane, focusEditor: false);

            if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab) &&
                !ReferenceEquals(originalActiveTab, ActiveEditorTab))
            {
                ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);
            }

            RefreshState(fullRefresh: true);
        }
    }

    private void NewFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        NewFile();
    }

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

    private async void StatusBarFilePathButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath)) return;
        try
        {
            await TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(_currentFilePath)!;
        }
        catch { }
    }

    private void StatusBarDiagnosticsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateEditorDiagnostic(1);
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

    private async void OpenFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        await OpenFolderAsync();
    }

    private void CloseFolderButton_OnClick(object? sender, RoutedEventArgs e) =>
        CloseFolder();

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        NavigateTo(AppPage.Settings);
    }

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
        OnPropertyChanged(nameof(CanFinishTutorial));
        OnPropertyChanged(nameof(CanAdvanceTutorial));
        OnPropertyChanged(nameof(CanSkipTutorial));
        OnPropertyChanged(nameof(TutorialSetupStatusText));
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
        if (IsDevBuild)
        {
            CheckForUpdatesStatusText = "App updates are disabled for -DEV builds.";
            return;
        }
        if (IsCheckingForUpdatesManually) return;

        IsCheckingForUpdatesManually = true;
        CheckForUpdatesStatusText = "Checking for updates…";

        try
        {
            var installInBackground = IsAutoUpdateAppInBackgroundEnabled;
            var update = await UpdateService.CheckAndHandleUpdateAsync(installInBackground, found =>
            {
                CheckForUpdatesStatusText = UpdateService.IsBetaVersionTag(found.Version)
                    ? $"Optional unstable beta {found.Version} is available."
                    : installInBackground
                    ? $"Kodo {found.Version} found - installing in the background…"
                    : $"Kodo {found.Version} is available.";
            }, includeBeta: true);

            if (update is null)
            {
                var hotfix = await UpdateService.CheckForHotfixAsync(includeBeta: true);
                if (hotfix is not null)
                {
                    var installed = UpdateService.ResolveInstalledHotfix();
                    CheckForUpdatesStatusText = $"Kodo {HotfixVersion.Format(installed.BaseVersion, hotfix.HotfixLevel)} hotfix available.";
                    UpdateDialog.ShowForHotfix(hotfix);
                    return;
                }
                CheckForUpdatesStatusText = UpdateService.LastIncompatibleReason
                    ?? $"You're up to date - Kodo {KodoDiagnostics.AppVersion}.";
            }
        }
        catch (Exception ex)
        {
            CheckForUpdatesStatusText = "Couldn't check for updates. Check your connection and try again.";
            KodoDiagnostics.LogDebug("Manual check-for-updates failed", ex);
        }
        finally
        {
            IsCheckingForUpdatesManually = false;
            OnPropertyChanged(nameof(CurrentAppVersionDisplay));
        }
    }

    private void OpenReleasesPageButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(ReleasesPageUrl);

    private async void DownloadUpdateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsDevBuild) return;
        var button = sender as Button;
        var originalContent = button?.Content;

        if (button is not null)
        {
            button.IsEnabled = false;
            button.Content = "Checking…";
        }

        try
        {
            var update = await UpdateService.CheckAndHandleUpdateAsync(installInBackground: false, includeBeta: true);
            if (update is null)
            {
                if (HasLatestRelease && IsNewerVersionAvailable && !string.IsNullOrWhiteSpace(LatestReleaseUrl))
                    OpenUrl(LatestReleaseUrl);
                else
                    OpenUrl(ReleasesPageUrl);
            }
        }
        finally
        {
            if (button is not null)
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }
    }

    private void OpenDiscordButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(DiscordServerUrl);

    private void OpenInstagramButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(InstagramUrl);

    private void OpenWebsiteButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(WebsiteUrl);

    private void OpenGitHubButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(GitHubRepoUrl);

    private void OpenPrivacyPolicyButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenUrl(PrivacyPolicyUrl);

    private void ViewShortcutsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var fixedShortcuts = new (string Gesture, string Description)[]
        {
            ("Enter / F3",         "Next search match (in terminal)"),
            ("Shift+Enter / Shift+F3", "Previous search match (in terminal)"),
            ("↑ / ↓; PgUp / PgDn", "Move through command palette results"),
            ("Enter / Esc",        "Run the selected command / close the command palette"),
            ("Enter / Esc",        "Apply the selected quick fix / dismiss quick fixes"),
            ("Enter / ↑ / ↓ / Esc", "Open search results, move through them, or close search"),
            ("Ctrl+Numpad + / - / 0", "Zoom in, out, or reset in the image viewer (numpad keys)"),
        };

        var iconBadge = new Border
        {
            Background = AccentBrush,
            CornerRadius = KodoDesignTokens.ControlRadius,
            Width = 36,
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "⌨",
                FontSize = 16,
                Foreground = AccentForegroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };

        var titleText = new TextBlock
        {
            Text = "Keyboard Shortcuts",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = PrimaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconBadge, titleText },
        };

        var hintText = new TextBlock
        {
            Text = "Click a shortcut below, then press a new key combination. Press Escape to cancel (so Escape itself can only be reassigned via Reset, not capture).",
            FontSize = 12,
            Foreground = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.92,
        };

        var headerDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 2),
        };

        var editableHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 2),
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
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 2),
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

        const string shortcutColumns = "164,*,72,72,88";
        var editableGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(shortcutColumns),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", KeybindDefinitions.Length))),
        };

        var fixedGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,24,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", fixedShortcuts.Length))),
        };

        for (var i = 0; i < fixedShortcuts.Length; i++)
        {
            var (gesture, description) = fixedShortcuts[i];

            var gestureBorder = new Border
            {
                Background = ButtonBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = KodoDesignTokens.ControlRadius,
                Padding = new Thickness(9, 5),
                Margin = new Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = gesture,
                    FontSize = 12,
                    FontFamily = KodoFonts.MonoFamily,
                    Foreground = PrimaryTextBrush,
                },
            };

            var descText = new TextBlock
            {
                Text = description,
                FontSize = 13,
                Foreground = MutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };

            Grid.SetRow(gestureBorder, i);
            Grid.SetColumn(gestureBorder, 0);
            Grid.SetRow(descText, i);
            Grid.SetColumn(descText, 2);
            fixedGrid.Children.Add(gestureBorder);
            fixedGrid.Children.Add(descText);
        }

        var statusText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            Foreground = DangerBrush,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var statusBorder = new Border
        {
            Background = new SolidColorBrush(DangerColor, 0.08),
            BorderBrush = new SolidColorBrush(DangerColor, 0.22),
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.CompactRadius,
            Padding = new Thickness(10, 7),
            IsVisible = false,
            Child = statusText,
        };

        string? capturingId = null;
        var gestureTextBlocks = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        var editButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var resetButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var toggleButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

        Button MakeRowButton(string content, string tooltip)
        {
            var button = new Button
            {
                Content = content,
                FontSize = 11,
                Padding = new Thickness(8, 3),
                Margin = new Thickness(3, 0, 3, 4),
                Background = ButtonBrush,
                Foreground = PrimaryTextBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = KodoDesignTokens.ControlRadius,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            ToolTip.SetTip(button, tooltip);
            return button;
        }

        void RefreshRow(string id)
        {
            if (gestureTextBlocks.TryGetValue(id, out var tb))
                tb.Text = FormatGesture(_keybinds[id]);
            var def = KeybindDefinitions.First(d => d.Id == id);
            var isCustom = _keybinds[id].Key != def.Default.Key || _keybinds[id].KeyModifiers != def.Default.KeyModifiers;
            if (resetButtons.TryGetValue(id, out var rb))
            {
                rb.IsVisible = isCustom || !IsKeybindEnabled(id);
                rb.Foreground = MutedTextBrush;
            }
            if (toggleButtons.TryGetValue(id, out var tb2))
            {
                var enabled = IsKeybindEnabled(id);
                tb2.Content = enabled ? "Enabled" : "Disabled";
                tb2.Foreground = enabled ? PrimaryTextBrush : AccentForegroundBrush;
                tb2.Background = enabled ? ButtonBrush : new SolidColorBrush(DangerColor, 0.16);
                ToolTip.SetTip(tb2, enabled ? "Disable this shortcut" : "Enable this shortcut");
            }
        }

        void UpdateStatus(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                statusText.Text = string.Empty;
                statusText.IsVisible = false;
                statusBorder.IsVisible = false;
            }
            else
            {
                statusText.Text = message;
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
                Background = ButtonBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = KodoDesignTokens.ControlRadius,
                Padding = new Thickness(9, 5),
                Margin = new Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var gestureText = new TextBlock
            {
                Text = FormatGesture(_keybinds[def.Id]),
                FontSize = 12,
                FontFamily = KodoFonts.MonoFamily,
                Foreground = PrimaryTextBrush,
            };
            gestureBorder.Child = gestureText;
            gestureTextBlocks[def.Id] = gestureText;

            var descText = new TextBlock
            {
                Text = def.Description,
                FontSize = 13,
                Foreground = MutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 0, 8, 4),
            };

            var editButton = MakeRowButton("Edit", "Change this shortcut");
            editButtons[def.Id] = editButton;

            var resetButton = MakeRowButton("Reset", "Restore the default shortcut and re-enable it");
            resetButton.IsVisible = _keybinds[def.Id].Key != def.Default.Key || _keybinds[def.Id].KeyModifiers != def.Default.KeyModifiers;
            resetButtons[def.Id] = resetButton;

            var toggleButton = MakeRowButton(
                IsKeybindEnabled(def.Id) ? "Enabled" : "Disabled",
                IsKeybindEnabled(def.Id) ? "Disable this shortcut" : "Enable this shortcut");
            toggleButtons[def.Id] = toggleButton;

            toggleButton.Click += (_, _) =>
            {
                SetKeybindEnabled(def.Id, !IsKeybindEnabled(def.Id));
                RefreshRow(def.Id);
            };

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
                _disabledKeybinds.Remove(def.Id);
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
            Grid.SetRow(toggleButton, i);
            Grid.SetColumn(toggleButton, 4);
            editableGrid.Children.Add(gestureBorder);
            editableGrid.Children.Add(descText);
            editableGrid.Children.Add(editButton);
            editableGrid.Children.Add(resetButton);
            editableGrid.Children.Add(toggleButton);
        }

        var sectionDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.85,
            Margin = new Thickness(0, 4),
        };

        var columnHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(shortcutColumns),
            Margin = new Thickness(0, 2, 0, 5),
        };
        var columnHeaderLabels = new[] { "Shortcut", "Action", "Edit", "Reset", "State" };
        for (var column = 0; column < columnHeaderLabels.Length; column++)
        {
            var header = new TextBlock
            {
                Text = columnHeaderLabels[column],
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedTextBrush,
                LetterSpacing = 0.3,
                Opacity = 0.8,
                Margin = new Thickness(column == 1 ? 8 : 0, 0, 0, 0),
            };
            if (column >= 2) header.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(header, column);
            columnHeader.Children.Add(header);
        }

        var innerStack = new StackPanel
        {
            Spacing = 2,
            Children = { editableHeader, columnHeader, editableGrid, sectionDivider, otherHeader, fixedGrid },
        };

        var scroll = new ScrollViewer
        {
            Content = innerStack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight = 380,
        };

        var scrollBorder = CreateDialogSurface(scroll, new Thickness(14, 12), new Thickness(0));

        var dismissButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 8),
            Background = AccentBrush,
            Foreground = AccentForegroundBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = KodoDesignTokens.ControlRadius,
        };

        var footerHint = new TextBlock
        {
            Text = "Changes save automatically",
            FontSize = 11,
            Foreground = MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        };

        var footerDivider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var footerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { footerHint, dismissButton },
        };
        Grid.SetColumn(dismissButton, 1);

        var content = new StackPanel
        {
            Spacing = 12,
            Children = { headerRow, hintText, headerDivider, scrollBorder, statusBorder, footerDivider, footerRow },
        };

        Window? dialog = null;
        dialog = new Window
        {
            Title = "Kodo - Keyboard Shortcuts",
            Width = 800,
            SizeToContent = SizeToContent.Height,
            MinWidth = 720,
            MaxHeight = 720,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBackgroundBrush,
            Content = CreateDialogSurface(content),
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
            RaiseMany(nameof(NewFileTooltip), nameof(OpenFileTooltip), nameof(OpenFolderTooltip), nameof(CloseFolderTooltip),
                nameof(ToggleExplorerTooltip), nameof(OpenExtensionsTooltip), nameof(SaveTooltip), nameof(SaveAsTooltip),
                nameof(ToggleTerminalTooltip), nameof(NewTerminalTooltip), nameof(FindInFileTooltip), nameof(FindInProjectTooltip),
                nameof(SettingsTooltip), nameof(HomeTooltip), nameof(EditorTooltip), nameof(ZoomOutTooltip),
                nameof(ZoomResetTooltip), nameof(ZoomInTooltip), nameof(QuickAccessShortcutText));
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

    private async Task ShowKeyboardShortcutsAsync()
    {
        ViewShortcutsButton_OnClick(null, null!);
        await Task.CompletedTask;
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
                FileName = path,
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
                FileName = path,
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

        void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    clearedAny = true;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }

        void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    clearedAny = true;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }

        TryDeleteFile(NewsCachePath);
        TryDeleteFile(LatestReleaseCachePath);
        TryDeleteFile(MarketplaceIndexCachePath);
        TryDeleteFile(MarketplaceIndexETagPath);
        TryDeleteFile(CompilerIndexCachePath);
        TryDeleteFile(CompilerIndexETagPath);
        TryDeleteFile(PluginsIndexCachePath);
        TryDeleteFile(PluginsIndexETagPath);

        TryDeleteDirectory(IconDiskCacheDir);

        _marketplaceIndexETag = null;
        _compilerIndexETag = null;
        _pluginsIndexETag = null;

        _marketplaceIconBytesCache.Clear();
        _marketplaceSvgCache.Clear();

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

        _isConfirmedClose = true;
        _suppressSettingsSave = true;
        _settingsSaveDebounceTimer.Stop();

        var exeFileName = OperatingSystem.IsWindows() ? "Kodo.exe" : "Kodo";
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            exePath = Path.Combine(AppContext.BaseDirectory, exeFileName);
        if (File.Exists(exePath))
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
            catch { }
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

        sb.Append("Kodo ").AppendLine(UpdateService.InstalledVersionDisplay());
        sb.Append("OS: ").AppendLine(KodoDiagnostics.OSDescription);
        sb.Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription);
        sb.Append("Architecture: ").Append(RuntimeInformation.ProcessArchitecture)
          .Append(" / ").AppendLine(Environment.Is64BitProcess ? "64-bit" : "32-bit");

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
        {
            sb.AppendLine($"Active file encoding: {EncodingDisplayText}");
            if (IsLineEndingVisible)
                sb.AppendLine($"Active file line ending: {LineEndingDisplayText}");
        }
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
                DeveloperOptionsStatusText = $"Export {CancelledSpelling}.";
                return;
            }

            await WriteTextFileAtomicallyAsync(path, export, new System.Text.UTF8Encoding(false));
            DeveloperOptionsStatusText = $"Kodo data exported to {path}.";
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("MainWindow.ExportKodoDataButton_OnClick", ex, operation: "Export Kodo data");
            DeveloperOptionsStatusText = $"Could not export Kodo data: {ex.Message}";
        }
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

    private async Task RestoreExpandedPathsAsync(HashSet<string> expandedPaths, int expectedGeneration)
    {
        if (_fileTreeLoadGeneration != expectedGeneration) return;
        bool anyExpanded;
        do
        {
            if (_fileTreeLoadGeneration != expectedGeneration) return;
            anyExpanded = false;
            var candidates = FileTreeItems
                .Where(i => i.IsDirectory && !i.IsExpanded && expandedPaths.Contains(i.FullPath))
                .ToList();

            foreach (var item in candidates)
            {
                if (_fileTreeLoadGeneration != expectedGeneration) return;
                await ToggleDirectoryExpansionAsync(item);
                if (_fileTreeLoadGeneration != expectedGeneration) return;
                anyExpanded = true;
            }
        }
        while (anyExpanded);
    }

    private async Task<bool> EnsureTabsReadyForDeletionAsync(List<EditorTab> tabs)
    {
        var originalActiveTab = ActiveEditorTab;





        var ownershipSnapshot = tabs.Where(t => t.IsDirty)
            .Select(t => (tab: t, inPrimary: _editorPane.Tabs.Contains(t), inSecondary: _secondaryEditorPane.Tabs.Contains(t)))
            .ToList();

        foreach (var tab in tabs.Where(t => t.IsDirty))
        {
            var action = await ShowUnsavedTabDialogAsync(tab);
            var declined = false;
            switch (action)
            {
                case UnsavedTabAction.Cancel:
                    declined = true;
                    break;

                case UnsavedTabAction.Save:


                    if (!ReferenceEquals(tab, ActiveEditorTab))
                        ActivateTab(tab, focusEditor: false, preserveOtherPaneMembership: true);

                    if (!await SaveAsync(allowPromptForPath: true, forcePromptForPath: false))
                        declined = true;
                    break;
            }

            if (declined)
            {
                RestoreDeletionPromptTabOwnership(ownershipSnapshot);
                if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab))
                    ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);
                return false;
            }
        }

        if (originalActiveTab is not null && OpenTabs.Contains(originalActiveTab))
            ActivateTab(originalActiveTab, focusEditor: false, preserveCurrentState: false);

        return true;
    }

    private void RestoreDeletionPromptTabOwnership(
        List<(EditorTab tab, bool inPrimary, bool inSecondary)> snapshot)
    {
        foreach (var entry in snapshot)
        {
            RestoreDeletionPromptTabOwnershipInPane(_editorPane, entry.tab, entry.inPrimary);
            RestoreDeletionPromptTabOwnershipInPane(_secondaryEditorPane, entry.tab, entry.inSecondary);
        }
    }

    private void RestoreDeletionPromptTabOwnershipInPane(EditorPaneState pane, EditorTab tab, bool wasMember)
    {
        if (wasMember || !pane.Tabs.Contains(tab))
            return;

        var index = pane.Tabs.IndexOf(tab);
        pane.Tabs.Remove(tab);
        if (ReferenceEquals(pane.ActiveTab, tab))
        {
            var replacementIndex = Math.Min(index, pane.Tabs.Count - 1);
            var replacementTab = pane.Tabs.Count > 0 ? pane.Tabs[replacementIndex] : null;
            SetOtherPaneTab(pane, replacementTab);
        }
    }

    private void RetargetTabPaths(string oldPath, string newPath, bool wasDirectory)
    {
        foreach (var tab in OpenTabs.Where(t => !t.IsUntitled))
        {
            string? updated = null;

            if (wasDirectory)
            {
                var prefix = oldPath + Path.DirectorySeparatorChar;
                if (FileSystemPaths.StartsWith(tab.Path, prefix))
                    updated = newPath + Path.DirectorySeparatorChar + tab.Path[prefix.Length..];
            }
            else if (FileSystemPaths.Equals(tab.Path, oldPath))
            {
                updated = newPath;
            }

            if (updated is null) continue;

            var previousPath = tab.Path;
            tab.Rename(updated, Path.GetFileName(updated));
            if (ReferenceEquals(_editorPane.ActiveTab, tab))
                RefreshPanePreviewState(_editorPane);
            if (ReferenceEquals(_secondaryEditorPane.ActiveTab, tab))
                RefreshPanePreviewState(_secondaryEditorPane);
            RefreshSyntaxHighlightingForTabInPanes(tab);
            foreach (var pane in new[] { _editorPane, _secondaryEditorPane })
            {
                if (ReferenceEquals(pane.ActiveTab, tab))
                    ResetPaneLspPresentation(pane);
            }
            OnPropertyChanged(nameof(PrimaryPaneBreadcrumbText));
            OnPropertyChanged(nameof(SecondaryPaneBreadcrumbText));
            _ = RetargetLspDocumentAsync(previousPath, updated, tab);
            QueueLspPresentationRefresh();

            if (ReferenceEquals(tab, ActiveEditorTab))
            {
                _currentFilePath = updated;
                EditorSession.SetFilePath(updated);
                UpdateCurrentDocumentPresentation();
                QueueInsightRefresh();
                RefreshState(fullRefresh: true);
            }
        }
    }

    private (EditorPaneState Pane, EditorTab? Tab, EditorPaneState TargetPane) ActivateEditorTabForMenuAction(EditorTab tab)
    {
        var previousPane = _activeEditorPane;
        var previousTab = previousPane.ActiveTab;
        var targetPane = ReferenceEquals(_editorPane.ActiveTab, tab)
            ? _editorPane
            : ReferenceEquals(_secondaryEditorPane.ActiveTab, tab)
                ? _secondaryEditorPane
                : previousPane;
        targetPane = ResolveEditorPaneForOpen(targetPane);
        ActivateEditorPane(targetPane, focusEditor: false);
        if (!ReferenceEquals(targetPane.ActiveTab, tab))
            ActivateTab(tab, focusEditor: false);
        return (previousPane, previousTab, targetPane);
    }

    private void RestoreEditorTabMenuContext((EditorPaneState Pane, EditorTab? Tab, EditorPaneState TargetPane) context, EditorTab targetTab)
    {
        if (!ReferenceEquals(_activeEditorPane, context.TargetPane) || !ReferenceEquals(context.TargetPane.ActiveTab, targetTab))
            return;

        if (context.Tab is null || !OpenTabs.Contains(context.Tab))
            return;

        ActivateEditorPane(context.Pane, focusEditor: false);
        if (!ReferenceEquals(context.Pane.ActiveTab, context.Tab))
            ActivateTab(context.Tab, focusEditor: false);
    }

    private async void SaveEditorTabMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab) return;
        var context = ActivateEditorTabForMenuAction(tab);
        try { await SaveAsync(); }
        finally { RestoreEditorTabMenuContext(context, tab); }
    }

    private async void SaveEditorTabAsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab) return;
        var context = ActivateEditorTabForMenuAction(tab);
        try
        {
            await SaveAsAsync();
            await RefreshExplorerTreeAsync();
        }
        finally { RestoreEditorTabMenuContext(context, tab); }
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

    private void RenameEditorTabMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;

        BeginInlineTabRename(tab);
    }

    private EditorTab? _inlineTabRenameFocusPending;

    private void BeginInlineTabRename(EditorTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Path)) return;

        if (_inlineTabRenameFocusPending is { } pending && !ReferenceEquals(pending, tab))
            pending.IsRenaming = false;

        tab.RenameText = tab.DisplayName;
        tab.IsRenaming = true;
        tab.IsRenamingInSecondaryPane = IsSplitScreenVisible && ReferenceEquals(_activeEditorPane, _secondaryEditorPane);
        tab.IsRenamingInPrimaryPane = !tab.IsRenamingInSecondaryPane;
        _inlineTabRenameFocusPending = tab;

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_inlineTabRenameFocusPending, tab)) return;

            var textBox = FindInlineTabRenameTextBox(tab);
            if (textBox is null) return;

            textBox.Focus();
            textBox.SelectAll();
            _inlineTabRenameFocusPending = null;
        });
    }

    private TextBox? FindInlineTabRenameTextBox(EditorTab tab)
    {
        var candidates = this.GetVisualDescendants()
            .OfType<TextBox>()
            .Where(control => ReferenceEquals(control.DataContext, tab));
        if (IsSplitScreenVisible)
        {
            var pane = ReferenceEquals(_activeEditorPane, _secondaryEditorPane)
                ? SecondaryEditorPane
                : PrimaryEditorPane;
            return candidates.FirstOrDefault(control =>
                control.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, pane)));
        }

        return candidates.FirstOrDefault();
    }

    private async void InlineTabRenameTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not EditorTab tab) return;

        switch (e.Key)
        {
            case Key.Escape:
                tab.IsRenaming = false;
                tab.RenameText = tab.DisplayName;
                _inlineTabRenameFocusPending = null;
                FocusEditor();
                e.Handled = true;
                break;

            case Key.Enter:
                e.Handled = true;
                await CompleteInlineTabRenameAsync(tab);
                break;
        }
    }

    private async void InlineTabRenameTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is EditorTab tab && tab.IsRenaming)
            await CompleteInlineTabRenameAsync(tab);
    }

    private async Task CompleteInlineTabRenameAsync(EditorTab tab)
    {
        if (!tab.IsRenaming) return;
        tab.IsRenaming = false;
        _inlineTabRenameFocusPending = null;

        var newName = tab.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(newName)) { tab.RenameText = tab.DisplayName; FocusEditor(); return; }

        var currentName = tab.DisplayName;
        if (string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            tab.RenameText = currentName;
            FocusEditor();
            return;
        }

        void Abort(string message)
        {
            tab.RenameText = tab.DisplayName;
            ExtensionsStatusText = message;
            FocusEditor();
        }

        var oldPath = tab.Path;
        if (string.IsNullOrWhiteSpace(oldPath) || !File.Exists(oldPath))
        {
            Abort("Rename failed: the file no longer exists on disk.");
            return;
        }

        var invalidReason = ValidateFileName(newName);
        if (invalidReason is not null)
        {
            Abort($"Rename failed: {invalidReason}");
            return;
        }

        var parentDir = Path.GetDirectoryName(oldPath);
        if (string.IsNullOrWhiteSpace(parentDir))
        {
            Abort("Rename failed: the file has no parent folder.");
            return;
        }

        var newPath = Path.Combine(parentDir, newName);
        var isCaseOnlyRename = FileSystemPaths.Comparer.Equals(oldPath, newPath)
                               && !string.Equals(oldPath, newPath, StringComparison.Ordinal);
        if (!isCaseOnlyRename && File.Exists(newPath))
        {
            Abort($"Rename failed: '{newName}' already exists.");
            return;
        }

        try
        {
            MoveFileCaseSafe(oldPath, newPath, isCaseOnlyRename);

            RetargetTabPaths(oldPath, newPath, wasDirectory: false);
            tab.RenameText = newName;

            if (tab.IsDirty)
            {
                var context = ActivateEditorTabForMenuAction(tab);
                try { await SaveAsync(); }
                finally { RestoreEditorTabMenuContext(context, tab); }
            }

            await RefreshExplorerTreeAsync();
            ExtensionsStatusText = $"Renamed to '{newName}'.";
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Rename failed: {ex.Message}";
            await ShowWarningDialogAsync("Rename file", ex);
        }
        finally
        {
            FocusEditor();
        }
    }

    private FileTreeItem? _inlineRenameFocusPending;
    private FileTreeItem? _newFileInlineRenameItem;

    private void BeginInlineRename(FileTreeItem item)
    {
        item.RenameText = item.Name;
        item.IsRenaming = true;
        _inlineRenameFocusPending = item;
        Dispatcher.UIThread.Post(() =>
        {
            var textBox = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, item));
            if (textBox is not null)
            {
                textBox.Focus();
                textBox.SelectAll();
                _inlineRenameFocusPending = null;
            }
        });
    }

    private async void InlineRenameTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox textBox && textBox.DataContext is FileTreeItem item)
        {
            item.IsRenaming = false;
            if (ReferenceEquals(_newFileInlineRenameItem, item))
                _newFileInlineRenameItem = null;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && sender is TextBox enterTextBox && enterTextBox.DataContext is FileTreeItem enterItem)
        {
            e.Handled = true;
            await CompleteInlineRenameAsync(enterItem);
        }
    }

    private void InlineRenameTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
    }

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static string? ValidateFileName(string newName)
    {
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) != -1 || newName.EndsWith(".") || newName.EndsWith(" "))
            return $"'{newName}' contains invalid characters.";

        var nameWithoutExt = newName.Contains('.') ? newName[..newName.IndexOf('.')] : newName;
        if (ReservedFileNames.Contains(nameWithoutExt) || ReservedFileNames.Contains(newName))
            return $"'{newName}' is a reserved name.";

        return null;
    }

    private static void MoveFileCaseSafe(string currentPath, string newPath, bool isCaseOnlyRename)
    {
        if (!isCaseOnlyRename)
        {
            File.Move(currentPath, newPath);
            return;
        }

        var tempPath = newPath + ".tmp_rename_" + Guid.NewGuid().ToString("N");
        File.Move(currentPath, tempPath);
        try
        {
            File.Move(tempPath, newPath);
        }
        catch (Exception moveException)
        {
            if (File.Exists(tempPath) && !File.Exists(currentPath))
            {
                try { File.Move(tempPath, currentPath); }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        $"Rename failed and the original file remains at '{tempPath}'.",
                        new AggregateException(moveException, rollbackException));
                }
            }

            throw;
        }
    }

    private static void MoveDirectoryCaseSafe(string currentPath, string newPath)
    {
        var tempPath = newPath + ".tmp_rename_" + Guid.NewGuid().ToString("N");
        Directory.Move(currentPath, tempPath);
        try
        {
            Directory.Move(tempPath, newPath);
        }
        catch (Exception moveException)
        {
            if (Directory.Exists(tempPath) && !Directory.Exists(currentPath))
            {
                try { Directory.Move(tempPath, currentPath); }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        $"Rename failed and the original folder remains at '{tempPath}'.",
                        new AggregateException(moveException, rollbackException));
                }
            }

            throw;
        }
    }

    private async Task CompleteInlineRenameAsync(FileTreeItem item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        if (ReferenceEquals(_newFileInlineRenameItem, item))
            _newFileInlineRenameItem = null;

        var newName = item.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, item.Name, StringComparison.Ordinal)) return;

        var invalidReason = ValidateFileName(newName);
        if (invalidReason is not null)
        {
            ExtensionsStatusText = $"Rename failed: {invalidReason}";
            return;
        }

        var parentDir = Path.GetDirectoryName(item.FullPath) ?? _currentFolderPath ?? string.Empty;
        var newPath = Path.Combine(parentDir, newName);

        var isCaseOnlyRename = FileSystemPaths.Comparer.Equals(item.FullPath, newPath) && !string.Equals(item.FullPath, newPath, StringComparison.Ordinal);
        if (!isCaseOnlyRename && (item.IsDirectory ? Directory.Exists(newPath) : File.Exists(newPath)))
        {
            ExtensionsStatusText = $"Rename failed: '{newName}' already exists.";
            return;
        }

        var affectedTabs = OpenTabs
            .Where(t => !t.IsUntitled && (
                FileSystemPaths.Equals(t.Path, item.FullPath) ||
                (item.IsDirectory && FileSystemPaths.IsPrefixOf(t.Path, item.FullPath))))
            .ToList();

        if (!await EnsureTabsReadyForDeletionAsync(affectedTabs)) return;

        try
        {
            if (isCaseOnlyRename)
            {
                if (item.IsDirectory)
                    MoveDirectoryCaseSafe(item.FullPath, newPath);
                else
                    MoveFileCaseSafe(item.FullPath, newPath, isCaseOnlyRename: true);
            }
            else if (item.IsDirectory)
                Directory.Move(item.FullPath, newPath);
            else
                MoveFileCaseSafe(item.FullPath, newPath, isCaseOnlyRename);

            RetargetTabPaths(item.FullPath, newPath, item.IsDirectory);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Rename failed: {ex.Message}";
            await ShowWarningDialogAsync("Rename file", ex);
        }
    }

    private async void EncodingStatusBarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasFileOpen || !IsTextEditorVisible) return;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var requestDocument = requestEditor.Document;
        var requestPath = _currentFilePath;
        var requestLineEnding = _currentLineEnding;
        var requestEncoding = _currentFileEncoding;

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

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
                try { return ((string Label, System.Text.Encoding Enc)?)(c.Label, c.Factory()); }
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
            var isCurrent = enc.CodePage == requestEncoding.CodePage &&
                            enc.GetPreamble().Length == requestEncoding.GetPreamble().Length;
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
                CornerRadius = KodoDesignTokens.CompactRadius,
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
            CornerRadius = KodoDesignTokens.ControlRadius,
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

        var outer = CreateDialogSurface(panel, new Thickness(16));

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

        using (PerformanceBudget.TrackModal())
        {
            await dialog.ShowDialog(this);
        }

        if (chosen is null) return;
        if (!ReferenceEquals(_activeEditorPane, requestPane) || !ReferenceEquals(EditorTextBox, requestEditor) ||
            !ReferenceEquals(requestPane.ActiveTab, requestTab) || !ReferenceEquals(requestEditor.Document, requestDocument) ||
            !FileSystemPaths.Equals(_currentFilePath, requestPath) || string.IsNullOrWhiteSpace(requestPath))
            return;

        if (chosen.CodePage == requestEncoding.CodePage &&
            chosen.GetPreamble().Length == requestEncoding.GetPreamble().Length)
            return;

        try
        {
            if (requestTab is not null && IsTabDiskStateChanged(requestTab))
                throw new IOException($"\"{Path.GetFileName(requestPath)}\" changed on disk and was left untouched.");
            var documentVersion = requestDocument.Version;
            var snapshot = EditorTextSnapshotStore.GetSnapshot(requestDocument);
            var expectedWriteTimeUtc = requestTab is { LastKnownLength: >= 0 } ? requestTab.LastKnownWriteTimeUtc : (DateTime?)null;
            var expectedLength = requestTab is { LastKnownLength: >= 0 } ? requestTab.LastKnownLength : (long?)null;
            await Task.Run(() => WriteTextFileAtomicallyAsync(
                requestPath, snapshot, requestLineEnding, chosen, expectedWriteTimeUtc, expectedLength,
                async () => await Dispatcher.UIThread.InvokeAsync(() => requestTab is not null && FileSystemPaths.Equals(requestTab.Path, requestPath))));

            if (requestTab is not null && OpenTabs.Contains(requestTab) && FileSystemPaths.Equals(requestTab.Path, requestPath))
            {
                var documentUnchanged = ReferenceEquals(requestDocument.Version, documentVersion);
                requestTab.Content = string.Empty;
                requestTab.Encoding = chosen;
                requestTab.IsDirty = !documentUnchanged;
                requestTab.IsModifiedExternally = false;
                CaptureTabDiskState(requestTab);
                if (ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(requestPane.ActiveTab, requestTab))
                {
                    _currentFileEncoding = chosen;
                    _isDirty = !documentUnchanged;
                    OnPropertyChanged(nameof(EncodingDisplayText));
                    OnPropertyChanged(nameof(IsDocumentDirty));
                }
            }
            RefreshState(fullRefresh: true);
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Change encoding", ex);
        }
    }

    private async void LineEndingStatusBarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasFileOpen || !IsTextEditorVisible) return;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var requestDocument = requestEditor.Document;
        var requestPath = _currentFilePath;
        var requestLineEnding = _currentLineEnding;
        var requestEncoding = _currentFileEncoding;

        var lineEndings = new (string Label, Kodo.Models.LineEnding Ending)[]
        {
            ("LF (\\n) - Unix / macOS", Kodo.Models.LineEnding.LF),
            ("CRLF (\\r\\n) - Windows", Kodo.Models.LineEnding.CRLF),
            ("CR (\\r) - Classic Mac OS", Kodo.Models.LineEnding.CR),
        };

        Kodo.Models.LineEnding? chosen = null;
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
                    Text = "Save file with line ending:",
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

        foreach (var (label, ending) in lineEndings)
        {
            var isCurrent = ending == requestLineEnding;
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
                CornerRadius = KodoDesignTokens.CompactRadius,
                Padding = new Thickness(12, 7),
            };
            var captured = ending;
            btn.Click += (_, _) =>
            {
                chosen = captured;
                dialog?.Close();
            };
            listPanel.Children.Add(btn);
        }

        var listBorder = new Border
        {
            Background = WindowBackgroundBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Padding = new Thickness(10),
            Child = listPanel
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                headerRow,
                new TextBlock { Text = "Choose line ending for this file:", FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap, Opacity = 0.92 },
                headerDivider,
                listBorder,
                new TextBlock
                {
                    Text = "The file will be re-saved immediately with the chosen line ending.",
                    FontSize = 11,
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                },
                footerDivider
            }
        };

        var outer = CreateDialogSurface(panel, new Thickness(16));

        dialog = new Window
        {
            Title = "Change Line Ending",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            MinWidth = 300,
            MaxHeight = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBackgroundBrush,
            Content = outer,
        };

        using (PerformanceBudget.TrackModal())
        {
            await dialog.ShowDialog(this);
        }

        if (chosen is null) return;
        if (!ReferenceEquals(_activeEditorPane, requestPane) || !ReferenceEquals(EditorTextBox, requestEditor) ||
            !ReferenceEquals(requestPane.ActiveTab, requestTab) || !ReferenceEquals(requestEditor.Document, requestDocument) ||
            !FileSystemPaths.Equals(_currentFilePath, requestPath) || string.IsNullOrWhiteSpace(requestPath))
            return;
        if (chosen.Value == requestLineEnding) return;

        try
        {
            if (requestTab is not null && IsTabDiskStateChanged(requestTab))
                throw new IOException($"\"{Path.GetFileName(requestPath)}\" changed on disk and was left untouched.");
            var documentVersion = requestDocument.Version;
            var snapshot = EditorTextSnapshotStore.GetSnapshot(requestDocument);
            var expectedWriteTimeUtc = requestTab is { LastKnownLength: >= 0 } ? requestTab.LastKnownWriteTimeUtc : (DateTime?)null;
            var expectedLength = requestTab is { LastKnownLength: >= 0 } ? requestTab.LastKnownLength : (long?)null;
            await Task.Run(() => WriteTextFileAtomicallyAsync(
                requestPath, snapshot, chosen.Value, requestEncoding, expectedWriteTimeUtc, expectedLength,
                async () => await Dispatcher.UIThread.InvokeAsync(() => requestTab is not null && FileSystemPaths.Equals(requestTab.Path, requestPath))));

            if (requestTab is not null && OpenTabs.Contains(requestTab) && FileSystemPaths.Equals(requestTab.Path, requestPath))
            {
                var documentUnchanged = ReferenceEquals(requestDocument.Version, documentVersion);
                requestTab.LineEnding = chosen.Value;
                requestTab.Content = string.Empty;
                requestTab.IsDirty = !documentUnchanged;
                requestTab.IsModifiedExternally = false;
                CaptureTabDiskState(requestTab);
                if (ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(requestPane.ActiveTab, requestTab))
                {
                    _currentLineEnding = chosen.Value;
                    _isDirty = !documentUnchanged;
                    OnPropertyChanged(nameof(LineEndingDisplayText));
                    OnPropertyChanged(nameof(IsLineEndingVisible));
                    OnPropertyChanged(nameof(IsDocumentDirty));
                }
            }
            RefreshState(fullRefresh: true);
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Change line ending", ex);
        }
    }

    private async void IndentationStatusBarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasFileOpen || !IsTextEditorVisible) return;

        var options = new (string Label, bool UseSpaces, int Size)[]
        {
            ("Spaces: 2", true, 2),
            ("Spaces: 4", true, 4),
            ("Spaces: 8", true, 8),
            ("Tab Size: 2", false, 2),
            ("Tab Size: 4", false, 4),
            ("Tab Size: 8", false, 8),
        };

        (bool UseSpaces, int Size)? chosen = null;
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
                    Text = "Change indentation:",
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

        foreach (var (label, useSpaces, size) in options)
        {
            var isCurrent = useSpaces == InsertSpaces && size == TabSize;
            var btn = new Button
            {
                Content = isCurrent ? $"{label}  \u2713" : label,
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
                CornerRadius = KodoDesignTokens.CompactRadius,
                Padding = new Thickness(12, 7),
            };
            var capturedSpaces = useSpaces;
            var capturedSize = size;
            btn.Click += (_, _) =>
            {
                chosen = (capturedSpaces, capturedSize);
                dialog?.Close();
            };
            listPanel.Children.Add(btn);
        }

        var listBorder = new Border
        {
            Background = WindowBackgroundBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Padding = new Thickness(10),
            Child = listPanel
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                headerRow,
                new TextBlock { Text = "Choose indentation for the editor:", FontSize = 12, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap, Opacity = 0.92 },
                headerDivider,
                listBorder,
                new TextBlock
                {
                    Text = "Changes apply to new indentations and editor guides.",
                    FontSize = 11,
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                },
                footerDivider
            }
        };

        var outer = CreateDialogSurface(panel, new Thickness(16));

        dialog = new Window
        {
            Title = "Change Indentation",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            MinWidth = 300,
            MaxHeight = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBackgroundBrush,
            Content = outer,
        };

        using (PerformanceBudget.TrackModal())
        {
            await dialog.ShowDialog(this);
        }

        if (chosen is null) return;
        var (chosenUseSpaces, chosenSize) = chosen.Value;
        if (chosenUseSpaces == InsertSpaces && chosenSize == TabSize) return;

        InsertSpaces = chosenUseSpaces;
        TabSize = chosenSize;
        OnPropertyChanged(nameof(IndentationDisplayText));
        OnPropertyChanged(nameof(IsIndentationVisible));
    }

    private void EditorContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var targetEditor = menu.PlacementTarget as TextEditor ??
            (menu.PlacementTarget as Control)?.GetVisualAncestors().OfType<TextEditor>().FirstOrDefault();
        if (ReferenceEquals(targetEditor, SecondaryEditorTextBox))
            ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        else if (ReferenceEquals(targetEditor, PrimaryEditorTextBox))
            ActivateEditorPane(_editorPane, focusEditor: false);

        var hasSelection = EditorTextBox?.TextArea?.Selection is { IsEmpty: false };
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var name = item.Name ?? string.Empty;
            if (name.EndsWith("CutMenuItem", StringComparison.Ordinal) ||
                name.EndsWith("CopyMenuItem", StringComparison.Ordinal) ||
                name.EndsWith("FindAllOccurrencesMenuItem", StringComparison.Ordinal) ||
                name.EndsWith("ChangeAllOccurrencesMenuItem", StringComparison.Ordinal))
                item.IsEnabled = hasSelection;

            if (name.EndsWith("CutMenuItem", StringComparison.Ordinal))
                item.InputGesture = _keybinds.TryGetValue("Cut", out var cutGesture) ? cutGesture : null;
            if (name.EndsWith("CopyMenuItem", StringComparison.Ordinal))
                item.InputGesture = _keybinds.TryGetValue("Copy", out var copyGesture) ? copyGesture : null;
            if (name.EndsWith("PasteMenuItem", StringComparison.Ordinal))
                item.InputGesture = _keybinds.TryGetValue("Paste", out var pasteGesture) ? pasteGesture : null;

            if (name.EndsWith("DismissDiagnosticMenuItem", StringComparison.Ordinal))
            {
                var diag = GetDiagnosticAtCaret();
                var hasDiagnostic = diag is not null;
                item.IsEnabled = hasDiagnostic;
                item.Header = hasDiagnostic ? $"Dismiss: {diag?.message ?? "Diagnostic"}" : "Dismiss Diagnostic";
            }
            if (name.EndsWith("GoToDefinitionMenuItem", StringComparison.Ordinal))
            {
                var word = GetLanguageWordAtCaret();
                var isVariable = word is not null && CurrentLanguageExtension?.LangRules is { HasDefinitionProvider: true };
                item.IsEnabled = isVariable;
                item.IsVisible = isVariable;
                item.InputGesture = _keybinds.TryGetValue("GoToDefinition", out var definitionGesture) ? definitionGesture : null;
            }
            if (name.EndsWith("FindReferencesMenuItem", StringComparison.Ordinal))
                item.IsEnabled = CurrentLanguageExtension?.LangRules?.HasReferenceProvider == true && GetLanguageWordAtCaret() is not null;
            if (name.EndsWith("RenameSymbolMenuItem", StringComparison.Ordinal))
                item.IsEnabled = ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true } && GetLanguageWordAtCaret() is not null;
            if (name.EndsWith("TypeDefinitionMenuItem", StringComparison.Ordinal) || name.EndsWith("ImplementationMenuItem", StringComparison.Ordinal))
                item.IsEnabled = ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true } && GetLanguageWordAtCaret() is not null;
            if (name.EndsWith("DocumentSymbolsMenuItem", StringComparison.Ordinal))
                item.IsEnabled = ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true };
            if (name.EndsWith("SignatureHelpMenuItem", StringComparison.Ordinal))
                item.IsEnabled = ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true };
            if (name.EndsWith("FormatDocumentMenuItem", StringComparison.Ordinal))
                item.IsEnabled = CurrentLanguageExtension?.LangRules?.HasFormatter == true ||
                                 (ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true });
            if (name.EndsWith("QuickFixMenuItem", StringComparison.Ordinal))
            {
                var hasDiagnostic = GetDiagnosticAtCaret() is not null;
                var hasProvider = CurrentLanguageExtension?.LangRules?.HasCodeActionProvider == true ||
                                  ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true };
                item.IsEnabled = hasDiagnostic || hasProvider;
                item.Header = hasDiagnostic ? "Quick Fix for Diagnostic…" : "Quick Fix…";
            }
        }
    }

    private (string? filePath, string lineText, string message, int offset, int length)? GetDiagnosticAtCaret()
    {
        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null) return null;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var doc = EditorTextBox.Document;
        foreach (var err in _errorHighlightRenderer.Spans)
        {
            if (caretOffset >= err.StartOffset && caretOffset < err.StartOffset + err.Length)
            {
                var line = doc.GetLineByOffset(Math.Clamp(err.StartOffset, 0, doc.TextLength));
                var lineText = doc.GetText(line.Offset, line.Length);
                return (_currentFilePath, lineText, err.Message, err.StartOffset, err.Length);
            }
        }
        foreach (var dead in _deadCodeHighlightRenderer.Spans)
        {
            if (caretOffset >= dead.StartOffset && caretOffset < dead.StartOffset + dead.Length)
            {
                var line = doc.GetLineByOffset(Math.Clamp(dead.StartOffset, 0, doc.TextLength));
                var lineText = doc.GetText(line.Offset, line.Length);
                return (_currentFilePath, lineText, dead.Reason, dead.StartOffset, dead.Length);
            }
        }
        var caretLine = EditorTextBox.Document.GetLineByOffset(caretOffset);
        var caretLineText = EditorTextBox.Document.GetText(caretLine.Offset, caretLine.Length);
        var caretLineNumber = caretLine.LineNumber;
        foreach (var err in _errorHighlightRenderer.Spans)
        {
            var errLine = doc.GetLineByOffset(Math.Clamp(err.StartOffset, 0, doc.TextLength)).LineNumber;
            if (errLine == caretLineNumber)
            {
                return (_currentFilePath, caretLineText, err.Message, err.StartOffset, err.Length);
            }
        }
        foreach (var dead in _deadCodeHighlightRenderer.Spans)
        {
            var deadLine = doc.GetLineByOffset(Math.Clamp(dead.StartOffset, 0, doc.TextLength)).LineNumber;
            if (deadLine == caretLineNumber)
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

    private async void EditorCutMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        var targetPane = _activeEditorPane;
        var targetEditor = EditorTextBox;
        var targetTab = targetPane.ActiveTab;
        var targetDocument = targetEditor?.Document;
        if (targetTab is null || targetDocument is null || targetEditor?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var text = sel.GetText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
        if (!ReferenceEquals(_activeEditorPane, targetPane) || !ReferenceEquals(targetPane.ActiveTab, targetTab) ||
            !ReferenceEquals(targetEditor.Document, targetDocument) || !ReferenceEquals(targetEditor.TextArea.Selection, sel))
            return;
        sel.ReplaceSelectionWithText(string.Empty);
    }

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
        var targetPane = _activeEditorPane;
        var targetEditor = EditorTextBox;
        var targetTab = targetPane.ActiveTab;
        var targetDocument = targetEditor?.Document;
        var textArea = targetEditor?.TextArea;
        var selection = textArea?.Selection;
        if (targetTab is null || targetDocument is null || textArea is null || selection is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text) || !ReferenceEquals(_activeEditorPane, targetPane) ||
            !ReferenceEquals(targetPane.ActiveTab, targetTab) || !ReferenceEquals(targetEditor?.Document, targetDocument) ||
            !ReferenceEquals(textArea?.Selection, selection))
            return;
        selection.ReplaceSelectionWithText(text);
    }

    private void IncreaseFontSizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorFontSize = Math.Min(32, EditorFontSize + 1);

    private void DecreaseFontSizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorFontSize = Math.Max(8, EditorFontSize - 1);

    private void EditorTextView_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(sender, _hoveredEditorTextView)) return;
        var hasTooltip = sender is AvaloniaEdit.Rendering.TextView textView && ToolTip.GetTip(textView) is not null;
        CancelLanguageHoverRequest();
        CancelLspHoverRequest();
        if (!_isPointerOverEditorLink && _hoveredDeadCodeReason is null && _hoveredErrorReason is null &&
            _hoveredLanguageInfo is null && _lspHoverTooltip is null && !hasTooltip) return;
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
        var textView = _hoveredEditorTextView ?? EditorTextBox.TextArea.TextView;
        if (textView.IsPointerOver || DiagnosticPopup.IsPointerOver || DiagnosticPopupBorder.IsPointerOver || DiagnosticPopupHitBorder.IsPointerOver) return;
        if (!_isPointerOverEditorLink && _hoveredDeadCodeReason is null && _hoveredErrorReason is null &&
            _hoveredLanguageInfo is null && _lspHoverTooltip is null) return;
        _isPointerOverEditorLink = false;
        _hoveredDeadCodeReason = null;
        _hoveredErrorReason = null;
        _hoveredDiagnosticLineText = null;
        _hoveredDiagnosticMessage = null;
        _hoveredLanguageInfo = null;
        _hoveredDiagnosticFilePath = null;
        _hoveredEditorTextView = null;
        _hoveredEditorPane = null;
        _lspHoverTooltip = null;
        DiagnosticPopup.IsOpen = false;
        ToolTip.SetTip(textView, null);
        textView.Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    private bool IsPointerOverLink(Point pointerPosition, AvaloniaEdit.Rendering.TextView textView, TextEditor editor)
    {
        var document = editor.Document;
        if (document is null) return false;
        var pos = textView.GetPositionFloor(pointerPosition + textView.ScrollOffset);
        if (pos is null) return false;

        try
        {
            var line = document.GetLineByNumber(pos.Value.Line);
            var lineText = document.GetText(line.Offset, line.Length);
            var colOffset = pos.Value.Column - 1;

            if (StrictLinkElementGenerator.TryGetLinkSpan(lineText, colOffset, out _, out _))
                return true;
        }
        catch
        {
        }

        return false;
    }

    private void ClearDeadCodeHighlighting()
    {
        var alreadyEmpty = _deadCodeHighlightRenderer.Spans.Count == 0;
        _deadCodeAppliedSignature = -1;
        _deadCodeHighlightRenderer.SetSpans(Array.Empty<DeadCodeSpan>());
        _deadCodeTextBrightener.SetSpans(Array.Empty<DeadCodeSpan>());
        var emptyDead = Array.Empty<DeadCodeSpan>();
        _errorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _errorTextDarkener.SetSpans(_errorHighlightRenderer.Spans, _errorHighlightRenderer.SpanIndex, emptyDead, SpanOverlapIndex.Empty);
        if (!alreadyEmpty)
        {
            RedrawVisibleEditorLines();
            EditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
        }
        if (!alreadyEmpty)
            RefreshStatusBarDiagnostics();
    }

    private void RedrawVisibleEditorLines()
    {
        RedrawVisibleEditorLines(EditorTextBox);
    }

    private static void RedrawVisibleEditorLines(TextEditor? editor)
    {
        var textView = editor?.TextArea?.TextView;
        if (textView is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;

        var firstLine = textView.VisualLines[0].FirstDocumentLine;
        var lastLine = textView.VisualLines[^1].LastDocumentLine;
        var start = firstLine.Offset;
        var end = lastLine.Offset + lastLine.TotalLength;
        textView.Redraw(start, Math.Max(1, end - start));
    }

    private async Task UpdateErrorHighlightingSafelyAsync()
    {
        try
        {
            await UpdateErrorHighlightingAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Error highlighting refresh failed.", ex);
        }
    }

    private async Task UpdateErrorHighlightingAsync(string? textSnapshot = null)
    {
        var scanToken = RenewAnalysisCancellation(ref _errorAnalysisCancellation).Token;
        var hasLspForFile = LspEnabled && LspDiagnosticsEnabled &&
            ResolveLspExtensionForFile(_currentFilePath) is not null;
        if (EditorTextBox?.Document is null ||
            ActiveEditorTab is null || ActiveEditorTab.IsUntitled ||
            IsPlainTextFile(_currentFilePath) ||
            HasNoFileExtension(_currentFilePath) ||
            IsInsightBlacklisted(_currentFilePath) ||
            IsErrorDeadCodeBlacklisted(_currentFilePath))
        {
            ClearErrorHighlighting();
            HideDiagnosticPopup();
            return;
        }
        if ((!IsInsightEnabled || !IsInsightErrorDetectionEnabled) && !hasLspForFile)
        {
            ClearErrorHighlighting();
            HideDiagnosticPopup();
            return;
        }

        var document = EditorTextBox.Document;
        var documentLength = document.TextLength;
        if (PerformanceBudget.IsLargeFile(document, 120_000) && !hasLspForFile)
        {
            ClearErrorHighlighting();
            HideDiagnosticPopup();
            return;
        }

        var languageExtension = CurrentLanguageExtension;
        var scanVersion = _insightDocVersion;
        var scanPath = _currentFilePath;
        var scanExtension = languageExtension is null ? string.Empty : $"{languageExtension.Id}\u001F{languageExtension.Version}";
        var signature = ComputeErrorHighlightSignature(scanVersion, scanPath, scanExtension);
        if (signature == _errorHighlightAppliedSignature)
            return;
        var lspForFile = ResolveLspExtensionForFile(_currentFilePath);
        var isLspPrimary = IsLspPrimaryForFile(_currentFilePath);
        var skipLocalText = isLspPrimary || PerformanceBudget.IsLargeFile(document, 120_000);
        var text = textSnapshot ?? string.Empty;
        try
        {
            if (textSnapshot is null && !skipLocalText)
                text = await EditorTextSnapshotStore.GetSnapshot(document)
                    .GetTextAsync(EditorSession.RevisionCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (scanVersion != _insightDocVersion) return;
        List<ErrorSpan>? rawSpans = null;
        lock (_insightAnalysisCacheLock)
        {
            if (_cachedInsightAnalysisVersion == scanVersion &&
                FileSystemPaths.Equals(_cachedInsightAnalysisPath, scanPath) &&
                string.Equals(_cachedInsightAnalysisExtension, scanExtension, StringComparison.Ordinal))
            {
                rawSpans = _cachedInsightAnalysisSpans is null ? null : new List<ErrorSpan>(_cachedInsightAnalysisSpans);
            }
        }

        var hasConfiguredLsp = lspForFile?.HasLsp == true;
        var hasRunningLsp = HasRunningLspForFile(_currentFilePath);

        if (rawSpans is null)
        {
            try
            {
                var insightDisabled = !IsInsightEnabled || !IsInsightErrorDetectionEnabled || IsInsightBlacklisted(_currentFilePath) || IsErrorDeadCodeBlacklisted(_currentFilePath);
                var isLargeFile = PerformanceBudget.IsLargeFile(document);
                var isHugeFile = PerformanceBudget.IsHugeFile(document, 120_000);
                var isLargeByLineCount = document.LineCount >= PerformanceBudget.LargeFileLineCountThreshold;
                var shouldSkipInsightForLsp = hasRunningLsp && (documentLength > 50_000 || isLargeByLineCount);
                if (isLspPrimary || insightDisabled || isHugeFile || isLargeByLineCount || shouldSkipInsightForLsp)
                {
                    rawSpans = new List<ErrorSpan>();
                    var reason = isLspPrimary ? $"LSP primary for {lspForFile?.Id}" : insightDisabled ? "Insight disabled by setting/blacklist" : $"large file len={documentLength} lines={document.LineCount}";
                    KodoDiagnostics.LogDebug($"Insight diagnostics skipped ({reason}, initialized={isLspPrimary})");
                }
                else
                {
                    rawSpans = await InsightWorkerGateway.Default.FindErrorsAsync(_InsightEngine, text, languageExtension, scanToken);
                    KodoDiagnostics.LogDebug($"Insight diagnostics: extension={languageExtension?.Id ?? "<none>"}, hasLangRules={languageExtension?.LangRules?.HasDiagnostics == true}, count={rawSpans.Count} hasConfiguredLsp={hasConfiguredLsp}");
                }
                IReadOnlyList<ExternalToolDiagnostic> externalDiagnostics;
                if (isLargeFile || isLspPrimary || insightDisabled)
                {
                    externalDiagnostics = Array.Empty<ExternalToolDiagnostic>();
                    var skipReason = isLargeFile ? $"large file len={documentLength} lines={document.LineCount}" : isLspPrimary ? $"LSP primary for {lspForFile?.Id}" : "Insight disabled";
                    KodoDiagnostics.LogDebug($"External diagnostics skipped ({skipReason})");
                }
                else
                {
                    externalDiagnostics = await Task.Run(
                        () => ExternalLanguageToolRunner.AnalyzeAsync(
                            languageExtension,
                            _currentFilePath,
                            text,
                            scanToken),
                        scanToken);
                    KodoDiagnostics.LogDebug($"External diagnostics: extension={languageExtension?.Id ?? "<none>"}, tools={languageExtension?.ExternalTools.Count ?? 0}, results={externalDiagnostics.Count}");
                }
                foreach (var diagnostic in externalDiagnostics)
                {
                    if (diagnostic.Start < 0 || diagnostic.Start >= text.Length || string.IsNullOrWhiteSpace(diagnostic.Message))
                        continue;
                    rawSpans.Add(new ErrorSpan(
                        diagnostic.Start,
                        Math.Clamp(diagnostic.Length, 1, text.Length - diagnostic.Start),
                        diagnostic.Message,
                        diagnostic.Severity,
                        diagnostic.Code,
                        diagnostic.Source));
                }
                if (rawSpans.Count > 0)
                    rawSpans = rawSpans
                        .GroupBy(span => (span.StartOffset, span.Length, Severity: span.Severity.Trim().ToLowerInvariant(), Message: span.Message.Trim().ToLowerInvariant(), span.Code))
                        .Select(group => group.OrderByDescending(span => span.Source.Contains("Recovery", StringComparison.OrdinalIgnoreCase)).First())
                        .ToList();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_insightAnalysisCacheLock)
            {
                if (scanVersion == _insightDocVersion && FileSystemPaths.Equals(scanPath, _currentFilePath))
                {
                    _cachedInsightAnalysisVersion = scanVersion;
                    _cachedInsightAnalysisPath = scanPath;
                    _cachedInsightAnalysisExtension = scanExtension;
                    _cachedInsightAnalysisText = text;
                    _cachedInsightAnalysisSpans = new List<ErrorSpan>(rawSpans);
                }
            }
        }

        var mergeWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var diagnosticEncoding = string.IsNullOrWhiteSpace(scanPath) ? "utf-16" : GetLspClientForFile(scanPath)?.PositionEncoding ?? "utf-16";
            var lspSpans = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                scanToken.ThrowIfCancellationRequested();
                return GetLspDiagnosticsForFile(scanPath, document, diagnosticEncoding);
            });
            if (lspSpans.Count > 0)
            {
                KodoDiagnostics.LogDebug($"LSP diagnostics merged: file={_currentFilePath}, count={lspSpans.Count} rawBefore={rawSpans.Count}");
                if (rawSpans.Count == 0)
                {
                    rawSpans = lspSpans;
                }
                else
                {
                    rawSpans = await Task.Run(() =>
                    {
                        var combined = new List<ErrorSpan>(rawSpans.Count + lspSpans.Count);
                        combined.AddRange(rawSpans);
                        combined.AddRange(lspSpans);
                        return combined
                            .GroupBy(span => (span.StartOffset, span.Length, Severity: span.Severity.Trim().ToLowerInvariant(), span.Message, span.Code))
                            .Select(group => group.OrderByDescending(span => span.Source.Equals("lsp", StringComparison.OrdinalIgnoreCase) ? 2 : span.Source.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ? 1 : 0).First())
                            .ToList();
                    }, scanToken);
                    KodoDiagnostics.LogDebug($"LSP diagnostics after dedupe: rawAfter={rawSpans.Count}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP diagnostics merge failed", ex); }

        if (scanVersion != _insightDocVersion) return;
        if (EditorTextBox?.Document is null) return;

        var applyWatch = System.Diagnostics.Stopwatch.StartNew();
        var dismissedDiagnostics = new HashSet<string>(_dismissedDiagnostics, StringComparer.Ordinal);
        var filteredSpans = dismissedDiagnostics.Count == 0
            ? rawSpans
            : await Dispatcher.UIThread.InvokeAsync(() => FilterDismissedErrorSpans(rawSpans, document, scanPath, dismissedDiagnostics));
        var filtered = await Task.Run(() =>
        {
            var index = filteredSpans.Count == 0
                ? SpanOverlapIndex.Empty
                : SpanOverlapIndex.Build(filteredSpans, span => span.StartOffset, span => (long)span.StartOffset + Math.Max(1, span.Length));
            return (Spans: filteredSpans, Index: index);
        }, scanToken);
        if (scanVersion != _insightDocVersion || signature != ComputeErrorHighlightSignature(scanVersion, scanPath, scanExtension) ||
            !ReferenceEquals(EditorTextBox?.Document, document)) return;
        var spans = filtered.Spans;
        var errorIndex = filtered.Index;
        var deadSpans = _deadCodeHighlightRenderer.Spans;
        var deadIndex = _deadCodeHighlightRenderer.SpanIndex;
        _errorHighlightRenderer.SetSpans(spans, errorIndex);
        _errorHighlightRenderer.SetDeadCodeSpans(deadSpans, deadIndex);
        _errorTextDarkener.SetSpans(spans, errorIndex, deadSpans, deadIndex);
        var otherEditor = ReferenceEquals(EditorTextBox, SecondaryEditorTextBox)
            ? PrimaryEditorTextBox
            : SecondaryEditorTextBox;
        if (otherEditor is not null && ReferenceEquals(otherEditor.Document, document))
        {
            var otherDeadRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryDeadCodeHighlightRenderer
                : _primaryDeadCodeHighlightRenderer;
            var otherErrorRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryErrorHighlightRenderer
                : _primaryErrorHighlightRenderer;
            var otherErrorDarkener = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryErrorTextDarkener
                : _primaryErrorTextDarkener;
            otherErrorRenderer.SetSpans(spans, errorIndex);
            otherErrorRenderer.SetDeadCodeSpans(otherDeadRenderer.Spans, otherDeadRenderer.SpanIndex);
            otherErrorDarkener.SetSpans(otherErrorRenderer.Spans, otherErrorRenderer.SpanIndex,
                otherDeadRenderer.Spans, otherDeadRenderer.SpanIndex);
            RedrawVisibleEditorLines(otherEditor);
            otherEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        if (spans.Count != rawSpans.Count)
            HideDiagnosticPopup();
        try
        {
            if (EditorTextBox.TextArea.TextView.VisualLinesValid)
            {
                EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                if (_errorTextDarkener.IsLightTheme)
                    RedrawVisibleEditorLines();
                ArmLayoutProbe("layout/after diagnostics redraw", 400);
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (EditorTextBox?.TextArea?.TextView?.VisualLinesValid == true)
                        {
                            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                            if (_errorTextDarkener.IsLightTheme)
                                RedrawVisibleEditorLines();
                        }
                    }
                    catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
                    {
                        KodoDiagnostics.LogDebug("UpdateErrorHighlighting: Deferred redraw race suppressed", ex);
                    }
                }, DispatcherPriority.Background);
            }
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("UpdateErrorHighlighting: Redraw race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    EditorTextBox?.TextArea?.TextView?.Redraw();
                }
                catch { }
            }, DispatcherPriority.Background);
        }
        KodoDiagnostics.ReportSlowStage("diagnostics merge", mergeWatch.ElapsedMilliseconds, 500, () => $"raw={rawSpans.Count} textLen={documentLength}");
        KodoDiagnostics.ReportSlowStage("diagnostics apply+redraw", applyWatch.ElapsedMilliseconds, 500, () => $"spans={spans.Count} textLen={document.TextLength}");
        RefreshStatusBarDiagnostics();
        _errorHighlightAppliedSignature = signature;
    }

    private static List<ErrorSpan> FilterDismissedErrorSpans(
        List<ErrorSpan> spans,
        AvaloniaEdit.Document.TextDocument document,
        string? filePath,
        HashSet<string> dismissedDiagnostics)
    {
        if (dismissedDiagnostics.Count == 0 || spans.Count == 0) return spans;
        var lineCache = new Dictionary<int, string>();
        var filtered = new List<ErrorSpan>(spans.Count);
        foreach (var span in spans)
        {
            var offset = Math.Clamp(span.StartOffset, 0, document.TextLength);
            var line = document.GetLineByOffset(offset);
            if (!lineCache.TryGetValue(line.LineNumber, out var lineText))
            {
                lineText = document.GetText(line.Offset, line.Length);
                lineCache[line.LineNumber] = lineText;
            }
            if (dismissedDiagnostics.Contains(GetDiagnosticSignature(filePath, lineText, span.Message))) continue;
            filtered.Add(span);
        }
        return filtered;
    }

    private void ClearErrorHighlighting()
    {
        var alreadyEmpty = _errorHighlightRenderer.Spans.Count == 0 && _deadCodeHighlightRenderer.Spans.Count == 0;
        _errorHighlightAppliedSignature = -1;
        var emptyErr = Array.Empty<ErrorSpan>();
        var emptyDead = Array.Empty<DeadCodeSpan>();
        _errorHighlightRenderer.SetSpans(emptyErr);
        _errorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _errorTextDarkener.SetSpans(emptyErr, emptyDead);
        if (!alreadyEmpty)
        {
            if (_errorTextDarkener.IsLightTheme)
                RedrawVisibleEditorLines();
            EditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
        }
        if (!alreadyEmpty)
            RefreshStatusBarDiagnostics();
    }

    private const double InsightRowHeight = 32d;
    private const double InsightListVerticalPadding = 12d;
    private const double InsightBorderThickness = 2d;
    private const int InsightVisibleRows = 7;

    private static void SetCaretOffsetSafely(
        AvaloniaEdit.Editing.Caret caret,
        AvaloniaEdit.Document.TextDocument doc,
        int desiredOffset)
    {
        caret.Offset = Math.Clamp(desiredOffset, 0, doc.TextLength);
        try { caret.BringCaretToView(); } catch { }
    }

    private string GetIndentUnit() => InsertSpaces ? new string(' ', TabSize) : "\t";

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

    private static Kodo.Models.LineEnding DetectLineEnding(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;

        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
            return Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;

        if (crlf > lf && crlf > cr) return Kodo.Models.LineEnding.CRLF;
        if (lf > crlf && lf > cr) return Kodo.Models.LineEnding.LF;
        if (cr > crlf && cr > lf) return Kodo.Models.LineEnding.CR;
        return Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;
    }

    private static string ConvertToLineEnding(string text, Kodo.Models.LineEnding ending)
    {
        if (text.Length == 0)
            return text;

        var target = ending switch
        {
            Kodo.Models.LineEnding.CRLF => "\r\n",
            Kodo.Models.LineEnding.CR => "\r",
            Kodo.Models.LineEnding.LF => "\n",
            _ => Environment.NewLine
        };

        var hasMismatchedEnding = false;
        var newlineCount = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n')
                continue;

            var sourceLength = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            newlineCount++;
            if (sourceLength != target.Length ||
                !text.AsSpan(i, sourceLength).SequenceEqual(target.AsSpan()))
                hasMismatchedEnding = true;

            i += sourceLength - 1;
        }

        if (!hasMismatchedEnding)
            return text;

        var capacity = text.Length;
        if (target.Length > 1)
            capacity = (int)Math.Min(int.MaxValue, (long)text.Length + (long)newlineCount * (target.Length - 1));

        var builder = new System.Text.StringBuilder(capacity);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                builder.Append(target);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

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

    private int GetOutdentLength(ReadOnlySpan<char> lineText, int availableLength, string? indentUnitOverride = null)
    {
        if (lineText.IsEmpty || availableLength <= 0)
            return 0;

        var maxLength = Math.Min(availableLength, lineText.Length);
        var indentUnit = indentUnitOverride ?? GetIndentUnit();
        if (maxLength >= indentUnit.Length &&
            lineText[..indentUnit.Length].SequenceEqual(indentUnit.AsSpan()))
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
        if (_isConfirmedClose) return;

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
                    return;

                case UnsavedTabAction.Save:
                    ActivateTab(tab, focusEditor: false);
                    if (!await SaveAsync(allowPromptForPath: true, forcePromptForPath: false))
                        return;
                    break;

            }
        }

        _isConfirmedClose = true;
        Close();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        StopGitSourceControl();
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
        CancelWordCountScan();
        CancelEditorAnalysisRequests();
        CancelInactiveTabDiagnosticScans();
        _settingsSaveDebounceTimer.Stop();
        _windowsAccentPollTimer.Stop();
        _windowsThemePollTimer.Stop();
        NetworkChange.NetworkAvailabilityChanged -= NetworkChange_OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkChange_OnNetworkAddressChanged;
        CloseAllTerminalSessions();
        try { _lspManager.Dispose(); } catch { }
        DisposeExtensionFolderWatchers();
        DisposeProjectFolderWatcher();
        DisposeDiscordPresence();
        CurrentImagePreview = null;
    }

    private void NetworkChange_OnNetworkAddressChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => RefreshMarketplaceConnectivityState());

    private TutorialStep CurrentTutorialStep => TutorialSteps[TutorialStepIndex];

    public Dictionary<string, string> LspExecutableOverrides => _lspExecutableOverrides;

    public Dictionary<string, string> LspExecutableOverrides1 => _lspExecutableOverrides;

    public bool IsAutoUpdateAppInBackgroundEnabled1 { get => _isAutoUpdateAppInBackgroundEnabled; set => _isAutoUpdateAppInBackgroundEnabled = value; }

    public InsightEngine InsightEngine => _InsightEngine;

    public InsightEngine InsightEngine1 => _InsightEngine;

    private void OnTutorialStepChanged()
    {
        RaiseMany(nameof(TutorialStepIndex), nameof(TutorialStepLabel), nameof(TutorialProgressDotsText), nameof(TutorialProgressPercent), nameof(TutorialSectionTitle), nameof(TutorialTitle), nameof(TutorialBody), nameof(TutorialShortcutText), nameof(TutorialSpotlightTitle), nameof(TutorialHighlightOne), nameof(TutorialHighlightTwo), nameof(TutorialHighlightThree), nameof(CanGoToPreviousTutorialStep), nameof(TutorialPrimaryButtonText), nameof(IsTutorialSetupStep), nameof(IsNotTutorialSetupStep), nameof(IsTutorialWelcomeStep), nameof(IsNotTutorialWelcomeStep), nameof(IsTutorialHeaderVisible), nameof(CanAdvanceTutorial), nameof(CanSkipTutorial), nameof(TutorialSetupStatusText));
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
        if (_commandPaletteOverlay is { IsVisible: true })
        {
            if (e.Key == Key.Escape)
            {
                DismissCommandPalette();
                e.Handled = true;
            }
            else if (HandlePaletteHotkey(e))
                e.Handled = true;
            return;
        }

        if (MatchesKeybind(e, "CommandPalette"))
        {
            e.Handled = true;
            ShowCommandPalette(commandsOnly: true);
            return;
        }
        else if (MatchesKeybind(e, "QuickAccess"))
        {
            e.Handled = true;
            ShowCommandPalette();
            return;
        }

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
        else if (MatchesKeybind(e, "ToggleSplitEditor"))
        {
            if (CanToggleSplitEditor)
                ToggleSplitEditorButton_OnClick(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (MatchesKeybind(e, "FocusFirstEditorGroup"))
        {
            FocusEditorPane(_editorPane);
            e.Handled = IsSplitScreenVisible;
        }
        else if (MatchesKeybind(e, "FocusSecondEditorGroup"))
        {
            FocusEditorPane(_secondaryEditorPane);
            e.Handled = IsSplitScreenVisible;
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
        else if (MatchesKeybind(e, "GoToDefinition"))
        {
            GoToDefinitionAtCaret();
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
        var document = textArea.Document;
        var tab = ActiveEditorTab;
        var activationVersion = _activeTabVersion;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text) || !ReferenceEquals(textArea.Document, document) ||
            activationVersion != _activeTabVersion || !ReferenceEquals(tab, ActiveEditorTab)) return;

        try
        {
            textArea.Selection.ReplaceSelectionWithText(text);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("Invalid document", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("document boundary", StringComparison.OrdinalIgnoreCase))
        {
            if (!ReferenceEquals(textArea.Document, document) || activationVersion != _activeTabVersion ||
                !ReferenceEquals(tab, ActiveEditorTab)) return;

            _lspFoldingManager?.UpdateFoldings(Array.Empty<AvaloniaEdit.Folding.NewFolding>(), 0);
            try
            {
                textArea.TextView.Redraw();
                textArea.Selection.ReplaceSelectionWithText(text);
            }
            catch (Exception retryException)
            {
                KodoDiagnostics.LogDebug("Paste aborted after repairing stale editor folding state.", retryException);
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Paste into editor failed.", ex);
        }
    }

    private static int NormalizeTabSize(int value) => value is 2 or 4 or 8 ? value : 4;

    private (bool insertSpaces, int tabSize)? DetectIndentation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var remaining = text.AsSpan();
        int tabLines = 0;
        int spaceLines = 0;
        var orderedSpaceIndents = new List<int>(Math.Min(text.Length, 2000));
        int sampled = 0;
        var scannedLines = 0;

        while (!remaining.IsEmpty && scannedLines < 20_000)
        {
            scannedLines++;
            var newlineIndex = remaining.IndexOf('\n');
            var raw = newlineIndex >= 0 ? remaining[..newlineIndex] : remaining;
            remaining = newlineIndex >= 0 ? remaining[(newlineIndex + 1)..] : ReadOnlySpan<char>.Empty;
            if (raw.IsWhiteSpace())
                continue;

            int i = 0;
            bool hasTab = false;
            bool hasSpace = false;
            while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t'))
            {
                if (raw[i] == '\t') hasTab = true;
                else hasSpace = true;
                i++;
            }

            if (i == 0)
                continue;

            if (hasTab && hasSpace)
                continue;

            if (hasTab)
                tabLines++;
            else
            {
                spaceLines++;
                orderedSpaceIndents.Add(i);
            }

            sampled++;
            if (sampled >= 2000)
                break;
        }

        if (tabLines == 0 && spaceLines == 0)
            return null;

        if (tabLines + spaceLines < 2)
            return null;

        if (tabLines > spaceLines)
        {
            return (false, TabSize);
        }

        var deltas = new Dictionary<int, int>();
        for (var idx = 0; idx < orderedSpaceIndents.Count - 1; idx++)
        {
            var a = orderedSpaceIndents[idx];
            var b = orderedSpaceIndents[idx + 1];
            if (b > a)
            {
                var d = b - a;
                if (d is >= 1 and <= 8)
                {
                    deltas.TryGetValue(d, out var cnt);
                    deltas[d] = cnt + 1;
                }
            }
        }

        int bestSize = 4;
        int bestCount = -1;
        foreach (var kv in deltas)
        {
            if (kv.Value > bestCount)
            {
                bestCount = kv.Value;
                bestSize = kv.Key;
            }
        }

        if (deltas.Count == 0 && orderedSpaceIndents.Count > 0)
        {
            var min = orderedSpaceIndents.Where(v => v > 0).DefaultIfEmpty(4).Min();
            if (min == 2) bestSize = 2;
            else if (min == 4) bestSize = 4;
            else if (min == 8) bestSize = 8;
            else if (min % 4 == 0) bestSize = 4;
            else if (min % 2 == 0) bestSize = 2;
            else bestSize = 4;
        }

        bestSize = NormalizeTabSize(bestSize);
        return (true, bestSize);
    }

    private void ApplyIndentationForContent((bool insertSpaces, int tabSize)? detected, bool saveSettings = false)
    {
        if (detected is null)
            return;

        var (useSpaces, size) = detected.Value;
        var normalizedSize = NormalizeTabSize(size);
        if (useSpaces == _insertSpaces && normalizedSize == _tabSize)
            return;

        _insertSpaces = useSpaces;
        _tabSize = normalizedSize;
        OnPropertyChanged(nameof(InsertSpaces));
        OnPropertyChanged(nameof(TabSize));
        OnPropertyChanged(nameof(TabSizeIndex));
        OnPropertyChanged(nameof(IndentationDisplayText));
        OnPropertyChanged(nameof(EditorBehaviorStatusText));
        ApplyEditorSettings();
        if (saveSettings)
            SaveSettings();
    }

    private Border CreateDialogSurface(Control content, Thickness? padding = null, Thickness? margin = null)
    {
        return new Border
        {
            Background = CardBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.CardRadius,
            Padding = padding ?? new Thickness(20),
            Margin = margin ?? new Thickness(16),
            Child = content,
        };
    }

    private Button CreateDialogButton(
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
            CornerRadius = KodoDesignTokens.ControlRadius,
            MinWidth = 86,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        button.Click += (_, _) => clickAction();
        return button;
    }
}
