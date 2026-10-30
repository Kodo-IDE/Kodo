// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Kodo;
using Kodo.Models;
using Shape = Avalonia.Controls.Shapes.Shape;
using PathShape = Avalonia.Controls.Shapes.Path;

namespace GitSourceControl;

internal sealed class GitSourceControlIntegration
{
    private MainWindow? _window;
    private Grid? _rootGrid;
    private Border? _sourcePanel;
    private readonly List<Button> _remoteActionButtons = new();
    private bool _panelOpen;
    private bool _sidebarViewAvailable;
    private string? _repositoryRoot;
    private string? _branchName;
    private string _upstreamStatus = string.Empty;
    private List<GitChange> _changes = [];
    private readonly List<IDisposable> _dynamicThemeSyncs = [];
    private readonly List<IDisposable> _fixedThemeSyncs = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _lifetime;
    private bool _refreshing;
    private bool _refreshRequested;
    private TaskCompletionSource<bool>? _refreshCompletion;
    private bool _operationRunning;
    private bool _lastOperationSucceeded;
    private bool _hasHead;
    private bool _hasRemote;
    private int _diffRequest;

    // ---- state added in this revision ----
    private const int CommitHistoryCount = 15;
    private const int DefaultRowLimit = 300;
    private GitStatusSnapshot? _snapshot;
    private List<string> _remotes = [];
    private List<GitCommit> _commits = [];
    private int _stashCount;
    private string? _repositoryProblem;
    private string? _lastOid;
    private string? _cachedCandidate;
    private bool _forceFullRefresh = true;
    private DateTime _lastFullRefreshUtc = DateTime.MinValue;
    private string _lastSignature = string.Empty;
    private int _themeVersion;
    private bool _statusFromPoll = true;
    private bool _amend;
    private bool _actionsExpanded;
    private bool _operationIsNetwork;
    private string _lastOperationError = string.Empty;
    private string _commitMessageDraft = string.Empty;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal) { "commits" };
    private readonly Dictionary<string, int> _rowLimits = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operationCts;
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private ScrollViewer? _contentScroll;
    private StackPanel? _actionsPanel;
    private Button? _pullButton;
    private Button? _pushButton;
    private Button? _amendButton;
    private Button? _cancelButton;
    private Button? _stashButton;
    private Button? _popStashButton;
    private Button? _undoCommitButton;
    private Button? _discardAllButton;

    private StackPanel? _groupsPanel;
    private TextBlock? _repositoryLabel;
    private TextBlock? _syncLabel;
    private TextBlock? _statusLabel;
    private TextBox? _commitMessage;
    private Button? _commitButton;
    private Window? _branchWindow;
    private Window? _fallbackWindow;
    private TextEditor? _editorText;
    private GitDiffLineColorizer? _editorDiffColorizer;
    private readonly Dictionary<string, string> _diffPreviewPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _diffPreviewSources = new(StringComparer.OrdinalIgnoreCase);

    public void OnLoad(MainWindow window)
    {
        _window = window;
        _sidebarViewAvailable = window.IsEditorPageVisible;
        _lifetime = new CancellationTokenSource();
        ((INotifyPropertyChanged)window).PropertyChanged += Window_OnPropertyChanged;
        window.OpenTabs.CollectionChanged += OpenTabs_OnCollectionChanged;
        window.AddHandler(InputElement.KeyDownEvent, Window_OnKeyDown, RoutingStrategies.Tunnel);
        _refreshTimer.Tick += RefreshTimer_OnTick;
        _refreshTimer.Start();
        _themeTimer.Tick += ThemeTimer_OnTick;
        CleanupStalePreviewFiles();
    }

    public void OnUnload()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_OnTick;
        _themeTimer.Stop();
        _themeTimer.Tick -= ThemeTimer_OnTick;
        if (_window is not null)
        {
            ((INotifyPropertyChanged)_window).PropertyChanged -= Window_OnPropertyChanged;
            _window.OpenTabs.CollectionChanged -= OpenTabs_OnCollectionChanged;
            _window.RemoveHandler(InputElement.KeyDownEvent, Window_OnKeyDown);
        }
        try { _operationCts?.Cancel(); } catch { }
        try { _lifetime?.Cancel(); } catch { }
        _lifetime?.Dispose();
        _lifetime = null;
        CloseAuxiliaryWindows();
        CloseSourcePanel();
        DisposeThemeSyncs(_dynamicThemeSyncs);
        DisposeThemeSyncs(_fixedThemeSyncs);
        RestoreEditorAfterDiff();
        _window = null;
    }

    /// <summary>Diff previews are temp files that are normally deleted when their tab closes; sweep up any a crash left behind.</summary>
    private static void CleanupStalePreviewFiles()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "Kodo", "GitDiffPreviews");
                if (!Directory.Exists(directory)) return;
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
                }
            }
            catch { }
        });
    }

    private void Window_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindow.ActiveEditorTab) or nameof(MainWindow.WorkspaceFolderPath) or nameof(MainWindow.IsFolderOpen))
        {
            if (e.PropertyName == nameof(MainWindow.ActiveEditorTab))
                Dispatcher.UIThread.Post(UpdateDiffEditorState, DispatcherPriority.Background);
            if (_panelOpen) _ = RefreshRepositoryAsync();
        }
        else if (e.PropertyName is nameof(MainWindow.IsHomePageVisible) or nameof(MainWindow.IsEditorPageVisible)
                 or nameof(MainWindow.IsSettingsPageVisible) or nameof(MainWindow.IsExtensionsPageVisible)
                 or nameof(MainWindow.IsTutorialPageVisible) or nameof(MainWindow.IsWhatsNewPageVisible))
        {
            SyncWithHostPage();
        }
        else if (e.PropertyName == "IsFileExplorerVisible" && _panelOpen && _fallbackWindow is null)
        {
            // Returning to Explorer through Kodo's activity bar also closes
            // this view and restores the original sidebar surface.
            CloseSourcePanel();
        }
        else if (e.PropertyName == "ExplorerPanelWidth" && _panelOpen)
        {
            UpdateSidebarWidth();
        }
        else if (e.PropertyName is null || e.PropertyName.EndsWith("Brush", StringComparison.Ordinal))
        {
            if (_panelOpen)
            {
                // Kodo raises one PropertyChanged per brush on a theme switch; repaint once, not once per brush.
                _themeTimer.Stop();
                _themeTimer.Start();
            }
        }
    }

    private void OpenTabs_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<EditorTab>())
            {
                var key = _diffPreviewPaths.FirstOrDefault(pair => string.Equals(pair.Value, item.Path, StringComparison.OrdinalIgnoreCase)).Key;
                if (key is null) continue;
                _diffPreviewPaths.Remove(key);
                _diffPreviewSources.Remove(item.Path);
                try { File.Delete(item.Path); } catch { }
            }
        }
        if (_panelOpen) _ = RefreshRepositoryAsync();
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _window is null ||
            e.Key != Key.G || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift))
            return;

        var focused = TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox || focused?.GetType().Name.Contains("TextArea", StringComparison.Ordinal) == true)
            return;
        if (focused is Visual focusedVisual && FindNamedControl(_window, "TerminalHostControl") is { } terminal &&
            (ReferenceEquals(focusedVisual, terminal) || focusedVisual.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, terminal))))
            return;

        ToggleSourcePanel();
        e.Handled = true;
    }

    private void RefreshTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_panelOpen || !_sidebarViewAvailable) return;
        // Don't spawn git processes while Kodo is in the background; polling resumes on the next tick after it is focused.
        if (!HostIsActive()) return;
        _ = RefreshRepositoryAsync(fromPoll: true);
    }

    private void ThemeTimer_OnTick(object? sender, EventArgs e)
    {
        _themeTimer.Stop();
        if (!_panelOpen) return;
        _themeVersion++;
        _lastSignature = string.Empty;
        RefreshPanelContent();
    }

    private bool HostIsActive()
    {
        // Isolated + guarded: Window.IsActive is a host member; if it were ever unavailable we simply keep polling.
        try { return HostIsActiveCore(); }
        catch { return true; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool HostIsActiveCore() => _window?.IsActive ?? true;

    // ---------------------------------------------------------------------
    // Activity bar and sidebar
    // ---------------------------------------------------------------------

    internal void ToggleSourcePanelFromHost() => ToggleSourcePanel();

    private void ToggleSourcePanel()
    {
        if (_fallbackWindow is not null)
        {
            _fallbackWindow.Close();
            return;
        }
        if (_panelOpen)
        {
            CloseSourcePanel();
            return;
        }
        OpenSourcePanel();
    }

    private void OpenSourcePanel()
    {
        if (_window is null || _panelOpen) return;
        if (!_sidebarViewAvailable)
        {
            _window.ShowEditorPage();
            SyncWithHostPage();
        }
        if (!_sidebarViewAvailable) return;
        if (_window.Content is not Grid root || root.ColumnDefinitions.Count < 3)
        {
            ShowFallbackWindow();
            return;
        }

        _rootGrid = root;
        _sourcePanel = CreateSourcePanel();
        Grid.SetColumn(_sourcePanel, 1);
        Grid.SetRow(_sourcePanel, 0);
        Grid.SetRowSpan(_sourcePanel, Math.Max(1, root.RowDefinitions.Count));
        _sourcePanel.ZIndex = 100;
        root.Children.Add(_sourcePanel);
        UpdateSidebarWidth();
        _panelOpen = true;
        SetActivitySelected(true);
        SetOperationBusy(_operationRunning);
        _lastSignature = string.Empty;
        RefreshPanelContent();
        _ = RefreshRepositoryAsync();
    }

    private void CloseSourcePanel()
    {
        if (_sourcePanel is not null)
        {
            try { _rootGrid?.Children.Remove(_sourcePanel); } catch { }
        }
        _sourcePanel = null;
        _rootGrid = null;
        ForgetPanelControls();
        _panelOpen = false;
        SetActivitySelected(false);
        DisposeThemeSyncs(_dynamicThemeSyncs);
        DisposeThemeSyncs(_fixedThemeSyncs);
    }

    /// <summary>
    /// Drops every reference to the current panel's controls. The text typed into the commit box is kept first:
    /// closing the panel (or switching to Home and back) used to throw an unsent message away.
    /// </summary>
    private void ForgetPanelControls()
    {
        if (_commitMessage is not null) _commitMessageDraft = _commitMessage.Text ?? string.Empty;
        _groupsPanel = null;
        _contentScroll = null;
        _repositoryLabel = null;
        _syncLabel = null;
        _statusLabel = null;
        _commitMessage = null;
        _commitButton = null;
        _branchButton = null;
        _pullButton = null;
        _pushButton = null;
        _amendButton = null;
        _cancelButton = null;
        _actionsPanel = null;
        _stashButton = null;
        _popStashButton = null;
        _undoCommitButton = null;
        _discardAllButton = null;
        _remoteActionButtons.Clear();
    }

    private void UpdateSidebarWidth()
    {
        if (_window is null || _sourcePanel is null) return;
        _sourcePanel.Width = _window.ExplorerPanelWidth;
        _sourcePanel.MinWidth = 180;
        _sourcePanel.MaxWidth = 600;
    }

    private void SetActivitySelected(bool selected) => _window?.SetGitSourceControlPanelOpen(selected);

    private void SyncWithHostPage()
    {
        if (_window is null) return;
        _sidebarViewAvailable = _window.IsEditorPageVisible;
        if (!_sidebarViewAvailable && _panelOpen)
            CloseSourcePanel();
        if (!_sidebarViewAvailable && _fallbackWindow is not null)
            _fallbackWindow.Close();
    }

    private Border CreateSourcePanel()
    {
        _remoteActionButtons.Clear();
        var dock = new DockPanel { Background = GetWindowBrush("SidebarBrush") };
        TrackTheme(dock, Panel.BackgroundProperty, "SidebarBrush", _fixedThemeSyncs);

        var header = new Border
        {
            Padding = new Thickness(12, 8),
            Background = GetWindowBrush("TopBarBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        TrackTheme(header, Border.BackgroundProperty, "TopBarBrush", _fixedThemeSyncs);
        TrackTheme(header, Border.BorderBrushProperty, "SurfaceBorderBrush", _fixedThemeSyncs);
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 6
        };
        var accentMark = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = GetWindowBrush("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        TrackTheme(accentMark, Border.BackgroundProperty, "AccentBrush", _fixedThemeSyncs);
        headerGrid.Children.Add(accentMark);
        var title = new TextBlock
        {
            Text = "Source Control",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetColumn(title, 1);
        TrackTheme(title, TextBlock.ForegroundProperty, "MutedTextBrush", _fixedThemeSyncs);
        headerGrid.Children.Add(title);
        var more = CreateSmallButton("⋯", "More actions: stash, undo last commit, discard all", ToggleActionsAsync, _fixedThemeSyncs);
        more.Width = 28;
        more.Height = 28;
        more.Classes.Add("activity");
        Grid.SetColumn(more, 2);
        headerGrid.Children.Add(more);
        var refresh = CreateSmallButton("↻", "Refresh Git status", async () =>
        {
            _forceFullRefresh = true;
            await RefreshRepositoryAsync();
        }, _fixedThemeSyncs);
        refresh.Width = 28;
        refresh.Height = 28;
        refresh.Classes.Add("activity");
        Grid.SetColumn(refresh, 3);
        headerGrid.Children.Add(refresh);
        header.Child = headerGrid;
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var repositoryCard = new Border
        {
            Margin = new Thickness(12, 12, 12, 6),
            Padding = new Thickness(10),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Background = GetWindowBrush("CardBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(1)
        };
        TrackTheme(repositoryCard, Border.BackgroundProperty, "CardBrush", _fixedThemeSyncs);
        TrackTheme(repositoryCard, Border.BorderBrushProperty, "SurfaceBorderBrush", _fixedThemeSyncs);
        var repositoryHeader = new StackPanel { Spacing = 7 };
        _repositoryLabel = new TextBlock
        {
            Text = "No repository",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush("PrimaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        TrackTheme(_repositoryLabel, TextBlock.ForegroundProperty, "PrimaryTextBrush", _fixedThemeSyncs);
        repositoryHeader.Children.Add(_repositoryLabel);

        var branchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        var branch = new Button
        {
            Content = "⑂  Select branch",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(9, 7),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        branch.Click += BranchButton_OnClick;
        TrackButton(branch, _fixedThemeSyncs);
        _branchButton = branch;
        ToolTip.SetTip(branch, "Switch or create a branch");
        branchRow.Children.Add(branch);
        repositoryHeader.Children.Add(branchRow);

        var remoteActions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 5
        };
        var fetch = CreateTextButton("↓  Fetch", "Fetch remote refs", async () => await RunGitOperationAsync(["fetch", "--prune"], "Fetched from remote."), _fixedThemeSyncs);
        _remoteActionButtons.Add(fetch);
        fetch.Padding = new Thickness(5, 6);
        fetch.FontSize = 11;
        Grid.SetColumn(fetch, 0);
        remoteActions.Children.Add(fetch);
        var pull = CreateTextButton("⇣  Pull", "Pull with fast-forward only", async () => await RunGitOperationAsync(["pull", "--ff-only"], "Pulled changes."), _fixedThemeSyncs);
        _pullButton = pull;
        pull.Padding = new Thickness(5, 6);
        pull.FontSize = 11;
        Grid.SetColumn(pull, 1);
        remoteActions.Children.Add(pull);
        var push = CreateTextButton("⇡  Push", "Push to remote", PushAsync, _fixedThemeSyncs);
        _pushButton = push;
        _remoteActionButtons.Add(push);
        push.Padding = new Thickness(5, 6);
        push.FontSize = 11;
        Grid.SetColumn(push, 2);
        remoteActions.Children.Add(push);
        repositoryHeader.Children.Add(remoteActions);

        _syncLabel = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Foreground = GetWindowBrush("MutedTextBrush"),
            Margin = new Thickness(2, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        TrackTheme(_syncLabel, TextBlock.ForegroundProperty, "MutedTextBrush", _fixedThemeSyncs);
        repositoryHeader.Children.Add(_syncLabel);
        repositoryCard.Child = repositoryHeader;
        DockPanel.SetDock(repositoryCard, Dock.Top);
        dock.Children.Add(repositoryCard);

        // Inline "more actions" area (toggled by the header button). Plain buttons rather than a popup menu so
        // it only relies on controls the rest of this panel already uses.
        var actionsCard = new Border
        {
            Margin = new Thickness(12, 0, 12, 6),
            Padding = new Thickness(8),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Background = GetWindowBrush("CardBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(1),
            IsVisible = false
        };
        TrackTheme(actionsCard, Border.BackgroundProperty, "CardBrush", _fixedThemeSyncs);
        TrackTheme(actionsCard, Border.BorderBrushProperty, "SurfaceBorderBrush", _fixedThemeSyncs);
        // Two single-row grids in a StackPanel: only members this panel already uses (no Grid row definitions/spacing).
        var actionsRows = new StackPanel { Spacing = 5 };
        Grid NewActionRow()
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 5 };
            actionsRows.Children.Add(row);
            return row;
        }
        var actionsRowOne = NewActionRow();
        var actionsRowTwo = NewActionRow();
        _stashButton = CreateTextButton("⤓  Stash", "Stash all changes, including untracked files", StashAsync, _fixedThemeSyncs);
        _popStashButton = CreateTextButton("⤒  Pop Stash", "Apply and remove the most recent stash", PopStashAsync, _fixedThemeSyncs);
        _undoCommitButton = CreateTextButton("↶  Undo Last Commit", "Undo the last commit but keep its changes staged", UndoLastCommitAsync, _fixedThemeSyncs);
        _discardAllButton = CreateTextButton("✕  Discard All…", "Discard all unstaged changes and delete untracked files", DiscardAllAsync, _fixedThemeSyncs, "DangerBrush", "DangerForegroundBrush");
        void PlaceAction(Grid row, Button button, int column)
        {
            button.Padding = new Thickness(5, 6);
            button.FontSize = 11;
            Grid.SetColumn(button, column);
            row.Children.Add(button);
        }
        PlaceAction(actionsRowOne, _stashButton, 0);
        PlaceAction(actionsRowOne, _popStashButton, 1);
        PlaceAction(actionsRowTwo, _undoCommitButton, 0);
        PlaceAction(actionsRowTwo, _discardAllButton, 1);
        actionsCard.Child = actionsRows;
        DockPanel.SetDock(actionsCard, Dock.Top);
        _actionsPanel = new StackPanel { Children = { actionsCard } };
        DockPanel.SetDock(_actionsPanel, Dock.Top);
        dock.Children.Add(_actionsPanel);

        var commitPanel = new StackPanel { Spacing = 7, Margin = new Thickness(12, 6, 12, 10) };
        var commitHeading = new TextBlock
        {
            Text = "Commit",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush("PrimaryTextBrush")
        };
        TrackTheme(commitHeading, TextBlock.ForegroundProperty, "PrimaryTextBrush", _fixedThemeSyncs);
        commitPanel.Children.Add(commitHeading);
        _commitMessage = new TextBox
        {
            PlaceholderText = "Message (Ctrl+Enter to commit)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            MaxHeight = 120,
            Padding = new Thickness(10),
            CornerRadius = KodoDesignTokens.ControlRadius,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = _commitMessageDraft
        };
        _commitMessage.KeyDown += CommitMessage_OnKeyDown;
        TrackTheme(_commitMessage, TextBox.BackgroundProperty, "EditorBackgroundBrush", _fixedThemeSyncs);
        TrackTheme(_commitMessage, TextBox.ForegroundProperty, "PrimaryTextBrush", _fixedThemeSyncs);
        TrackTheme(_commitMessage, TextBox.BorderBrushProperty, "SurfaceBorderBrush", _fixedThemeSyncs);
        commitPanel.Children.Add(_commitMessage);
        _amendButton = new Button
        {
            Content = "☐  Amend last commit",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 3),
            FontSize = 11,
            CornerRadius = KodoDesignTokens.CompactRadius,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _amendButton.Click += async (_, _) => await ToggleAmendAsync();
        ToolTip.SetTip(_amendButton, "Replace the previous commit instead of creating a new one");
        TrackQuietButton(_amendButton, _fixedThemeSyncs, "MutedTextBrush");
        commitPanel.Children.Add(_amendButton);
        _commitButton = new Button
        {
            Content = "✓  Commit Changes",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(9, 8),
            CornerRadius = KodoDesignTokens.ControlRadius,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _commitButton.Click += CommitButton_OnClick;
        TrackButton(_commitButton, _fixedThemeSyncs, "AccentBrush", "AccentForegroundBrush");
        commitPanel.Children.Add(_commitButton);
        DockPanel.SetDock(commitPanel, Dock.Top);
        dock.Children.Add(commitPanel);

        var statusBorder = new Border
        {
            Padding = new Thickness(10, 6),
            Background = GetWindowBrush("TopBarBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            [DockPanel.DockProperty] = Dock.Bottom
        };
        TrackTheme(statusBorder, Border.BackgroundProperty, "TopBarBrush", _fixedThemeSyncs);
        TrackTheme(statusBorder, Border.BorderBrushProperty, "SurfaceBorderBrush", _fixedThemeSyncs);
        _statusLabel = new TextBlock
        {
            Text = "Ready",
            FontSize = 11,
            Foreground = GetWindowBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        TrackTheme(_statusLabel, TextBlock.ForegroundProperty, "MutedTextBrush", _fixedThemeSyncs);
        var statusGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        statusGrid.Children.Add(_statusLabel);
        _cancelButton = CreateTextButton("Cancel", "Cancel the running Git operation", CancelOperationAsync, _fixedThemeSyncs);
        _cancelButton.Padding = new Thickness(8, 2);
        _cancelButton.FontSize = 11;
        _cancelButton.IsVisible = false;
        Grid.SetColumn(_cancelButton, 1);
        statusGrid.Children.Add(_cancelButton);
        statusBorder.Child = statusGrid;
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        dock.Children.Add(statusBorder);

        _contentScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        DockPanel.SetDock(_contentScroll, Dock.Top);
        _groupsPanel = new StackPanel { Spacing = 1, Margin = new Thickness(0, 3, 0, 12) };
        _contentScroll.Content = _groupsPanel;
        // The scrollable groups must be the last child so DockPanel gives them
        // the remaining space. Keeping the footer before the fill child avoids
        // squeezing the status text into a narrow docked column.
        dock.Children.Add(_contentScroll);

        var sourcePanel = new Border
        {
            Background = GetWindowBrush("SidebarBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = dock
        };
        TrackTheme(sourcePanel, Border.BackgroundProperty, "SidebarBrush", _fixedThemeSyncs);
        TrackTheme(sourcePanel, Border.BorderBrushProperty, "SurfaceBorderBrush", _fixedThemeSyncs);
        return sourcePanel;
    }

    private void SetOperationBusy(bool busy)
    {
        if (_sourcePanel is not null) _sourcePanel.IsHitTestVisible = !busy;
        if (_fallbackWindow?.Content is Control fallbackPanel)
            fallbackPanel.IsHitTestVisible = !busy;
    }

    private Button? _branchButton;

    /// <summary>
    /// Redraws the panel only when something it shows has actually changed. The 4-second poll used to rebuild
    /// every row each time, which reset hover state, lost scroll position and could swallow a click that landed
    /// mid-rebuild.
    /// </summary>
    private void UpdatePanel()
    {
        var signature = BuildSignature();
        if (_groupsPanel is not null && string.Equals(signature, _lastSignature, StringComparison.Ordinal)) return;
        _lastSignature = signature;
        RefreshPanelContent();
    }

    private string BuildSignature()
    {
        var sb = new StringBuilder(256 + _changes.Count * 48);
        void Part(string? value) => sb.Append(value).Append('\u0001');
        Part(_repositoryRoot);
        Part(_repositoryProblem);
        Part(_repositoryRoot is null ? GetWorkspaceCandidate() : null);
        Part(_snapshot?.BranchDisplay);
        Part(_snapshot?.Oid);
        Part(_upstreamStatus);
        Part(_snapshot?.HasUpstream == true ? "u" : "-");
        Part(_hasRemote ? "r" : "-");
        Part(_operationRunning ? "busy" : "idle");
        Part(_operationIsNetwork ? "net" : "-");
        Part(_amend ? "amend" : "-");
        Part(_actionsExpanded ? "actions" : "-");
        Part(_stashCount.ToString());
        Part(_themeVersion.ToString());
        Part(string.Join(",", _collapsedGroups.OrderBy(g => g, StringComparer.Ordinal)));
        Part(string.Join(",", _rowLimits.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Value}")));
        foreach (var change in _changes)
        {
            sb.Append(change.Path).Append('|').Append(change.IndexStatus).Append(change.WorktreeStatus).Append('|').Append(change.OriginalPath).Append('\u0002');
        }
        sb.Append('\u0001');
        foreach (var commit in _commits) sb.Append(commit.Hash).Append(',');
        return sb.ToString();
    }

    /// <summary>Enabled/visible state of every control that depends on repository or operation state.</summary>
    private void ApplyControlStates()
    {
        var repositoryAvailable = _repositoryRoot is not null && !_operationRunning;
        var hasConflicts = _changes.Any(c => c.IsConflicted);
        var hasStaged = _changes.Any(c => c.IsStaged);
        var hasUpstream = _snapshot?.HasUpstream == true;
        var canPublish = _hasRemote && _snapshot is { HasUpstream: false, IsDetached: false } && !string.IsNullOrEmpty(_snapshot.Branch);

        if (_branchButton is not null) _branchButton.IsEnabled = repositoryAvailable;
        foreach (var action in _remoteActionButtons) action.IsEnabled = repositoryAvailable && _hasRemote;
        if (_pullButton is not null)
        {
            _pullButton.IsEnabled = repositoryAvailable && _hasRemote && hasUpstream;
            ToolTip.SetTip(_pullButton, _hasRemote && !hasUpstream ? "This branch has no upstream to pull from - publish it first" : "Pull with fast-forward only");
        }
        if (_pushButton is not null)
        {
            _pushButton.Content = canPublish ? "⇡  Publish" : "⇡  Push";
            ToolTip.SetTip(_pushButton, canPublish ? "Publish this branch to the remote and set its upstream" : "Push to remote");
        }
        // Typing a message stays possible during a long fetch/push; only a local operation (which rewrites the index) locks it.
        if (_commitMessage is not null) _commitMessage.IsEnabled = _repositoryRoot is not null && !(_operationRunning && !_operationIsNetwork);
        if (_amendButton is not null)
        {
            _amendButton.Content = _amend ? "☑  Amend last commit" : "☐  Amend last commit";
            _amendButton.IsEnabled = repositoryAvailable && _hasHead;
        }
        if (_commitButton is not null)
        {
            _commitButton.Content = _amend ? "✓  Amend Commit" : "✓  Commit Changes";
            _commitButton.IsEnabled = repositoryAvailable && !hasConflicts && (hasStaged || (_amend && _hasHead));
            ToolTip.SetTip(_commitButton, hasConflicts ? "Resolve merge conflicts and stage the resolved files before committing" : null);
        }
        if (_cancelButton is not null) _cancelButton.IsVisible = _operationRunning && _operationIsNetwork;
        if (_stashButton is not null) _stashButton.IsEnabled = repositoryAvailable && _hasHead && _changes.Count > 0;
        if (_popStashButton is not null)
        {
            _popStashButton.IsEnabled = repositoryAvailable && _stashCount > 0;
            _popStashButton.Content = _stashCount > 0 ? $"⤒  Pop Stash ({_stashCount})" : "⤒  Pop Stash";
        }
        if (_undoCommitButton is not null) _undoCommitButton.IsEnabled = repositoryAvailable && _hasHead;
        if (_discardAllButton is not null) _discardAllButton.IsEnabled = repositoryAvailable && _changes.Any(c => c.IsUnstaged);
        if (_actionsPanel is not null) _actionsPanel.IsVisible = _actionsExpanded && _repositoryRoot is not null;
    }

    private void RefreshPanelContent()
    {
        if (!_panelOpen || _window is null || _groupsPanel is null) return;
        // Whatever path led here, the drawn state is the current state: the next poll must not redraw it again.
        _lastSignature = BuildSignature();
        var previousOffset = _contentScroll?.Offset ?? default;
        DisposeThemeSyncs(_dynamicThemeSyncs);
        _groupsPanel.Children.Clear();
        if (_repositoryLabel is not null)
        {
            _repositoryLabel.Text = _repositoryRoot is null ? "No repository" : Path.GetFileName(_repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            ToolTip.SetTip(_repositoryLabel, _repositoryRoot ?? "No Git repository detected");
        }
        if (_branchButton is not null)
        {
            var text = _snapshot?.BranchDisplay ?? "Select branch";
            _branchButton.Content = new TextBlock
            {
                Text = $"⑂  {text}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = GetWindowBrush("PrimaryTextBrush"),
                FontSize = 12
            };
            TrackTheme(_branchButton.Content as AvaloniaObject, TextBlock.ForegroundProperty, "PrimaryTextBrush", _dynamicThemeSyncs);
        }
        if (_syncLabel is not null)
        {
            var syncSummary = string.IsNullOrWhiteSpace(_upstreamStatus)
                ? _repositoryRoot is null ? string.Empty : _hasRemote ? "No upstream branch configured" : "No remote configured"
                : _upstreamStatus;
            _syncLabel.Text = syncSummary;
            ToolTip.SetTip(_syncLabel, syncSummary);
        }
        ApplyControlStates();

        if (_repositoryRoot is null)
        {
            if (!string.IsNullOrWhiteSpace(_repositoryProblem))
            {
                // Something other than "not a repository" went wrong (e.g. git missing, "dubious ownership").
                // Offering `git init` here would be wrong - show git's own explanation instead.
                AddEmptyState(_repositoryProblem!, showInitialize: false);
                return;
            }
            AddEmptyState(GetWorkspaceCandidate() is null
                ? "Open a folder or a file inside a Git repository."
                : "This folder is not a Git repository.", showInitialize: GetWorkspaceCandidate() is not null);
            return;
        }

        static int ByPath(GitChange a, GitChange b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
        var conflicts = _changes.Where(c => c.IsConflicted).ToList();
        var staged = _changes.Where(c => c.IsStaged).ToList();
        var working = _changes.Where(c => c.IsUnstaged).ToList();
        conflicts.Sort(ByPath);
        staged.Sort(ByPath);
        working.Sort(ByPath);
        AddChangeGroup("Merge Conflicts", "conflicts", conflicts, ChangeGroupKind.Conflicts);
        AddChangeGroup("Staged Changes", "staged", staged, ChangeGroupKind.Staged);
        AddChangeGroup("Changes", "working", working, ChangeGroupKind.Unstaged);
        if (conflicts.Count == 0 && staged.Count == 0 && working.Count == 0)
            AddEmptyState("There are no changes to commit.", showInitialize: false);
        AddCommitsSection();

        // Keep the user's place in a long list across rebuilds.
        var scroll = _contentScroll;
        if (scroll is not null && previousOffset.Y > 0)
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(scroll, _contentScroll)) scroll.Offset = previousOffset;
            }, DispatcherPriority.Background);
    }

    private void AddEmptyState(string text, bool showInitialize)
    {
        if (_groupsPanel is null || _window is null) return;
        var content = new StackPanel
        {
            Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new TextBlock
        {
            Text = showInitialize ? "⑂" : "✓",
            FontSize = 26,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        TrackTheme(icon, TextBlock.ForegroundProperty, "AccentBrush", _dynamicThemeSyncs);
        content.Children.Add(icon);
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = GetWindowBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 240
        };
        TrackTheme(label, TextBlock.ForegroundProperty, "MutedTextBrush", _dynamicThemeSyncs);
        content.Children.Add(label);
        if (showInitialize)
        {
            var initialize = CreateTextButton("Initialize Repository", "Create a Git repository in the current folder", async () => await InitializeRepositoryAsync(), _dynamicThemeSyncs, "AccentBrush", "AccentForegroundBrush");
            initialize.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(initialize);
        }
        var panel = new Border
        {
            Margin = new Thickness(12, 22, 12, 12),
            Padding = new Thickness(18, 20),
            CornerRadius = KodoDesignTokens.CardRadius,
            Background = GetWindowBrush("CardBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content
        };
        TrackTheme(panel, Border.BackgroundProperty, "CardBrush", _dynamicThemeSyncs);
        TrackTheme(panel, Border.BorderBrushProperty, "SurfaceBorderBrush", _dynamicThemeSyncs);
        _groupsPanel.Children.Add(panel);
    }

    private enum ChangeGroupKind { Conflicts, Staged, Unstaged }

    private Button CreateGroupToggle(string title, string key)
    {
        var collapsed = _collapsedGroups.Contains(key);
        var toggle = new Button
        {
            Content = $"{(collapsed ? "▸" : "▾")}  {title.ToUpperInvariant()}",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = KodoDesignTokens.CompactRadius,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(toggle, collapsed ? "Expand" : "Collapse");
        toggle.Click += (_, _) => ToggleGroup(key);
        TrackQuietButton(toggle, _dynamicThemeSyncs, "MutedTextBrush");
        return toggle;
    }

    private void ToggleGroup(string key)
    {
        if (!_collapsedGroups.Remove(key)) _collapsedGroups.Add(key);
        _lastSignature = string.Empty;
        RefreshPanelContent();
    }

    private void AddChangeGroup(string title, string key, List<GitChange> changes, ChangeGroupKind kind)
    {
        if (_groupsPanel is null || _window is null || changes.Count == 0) return;
        var collapsed = _collapsedGroups.Contains(key);

        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(6, 10, 10, 4)
        };
        heading.Children.Add(CreateGroupToggle(title, key));
        var count = new TextBlock
        {
            Text = changes.Count.ToString(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush(kind == ChangeGroupKind.Conflicts ? "WarningBrush" : "MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        TrackTheme(count, TextBlock.ForegroundProperty, kind == ChangeGroupKind.Conflicts ? "WarningBrush" : "MutedTextBrush", _dynamicThemeSyncs);
        Grid.SetColumn(count, 1);
        heading.Children.Add(count);

        if (kind == ChangeGroupKind.Staged)
        {
            var unstageAll = CreateSmallButton("−", "Unstage all changes", async () =>
                await RunGitOperationAsync(GitCommands.UnstageAll(_hasHead), "All changes unstaged."));
            Grid.SetColumn(unstageAll, 3);
            heading.Children.Add(unstageAll);
        }
        else if (kind == ChangeGroupKind.Unstaged)
        {
            var discardAll = CreateSmallButton("↶", "Discard all changes…", DiscardAllAsync);
            Grid.SetColumn(discardAll, 2);
            heading.Children.Add(discardAll);
            var stageAll = CreateSmallButton("+", "Stage all changes", async () =>
                await RunGitOperationAsync(GitCommands.StageAll(), "All changes staged."));
            Grid.SetColumn(stageAll, 3);
            heading.Children.Add(stageAll);
        }
        _groupsPanel.Children.Add(heading);
        if (collapsed) return;

        // Thousands of rows would freeze the UI thread; show a page and let the user ask for more.
        var limit = _rowLimits.TryGetValue(key, out var stored) ? stored : DefaultRowLimit;
        foreach (var change in changes.Take(limit))
            _groupsPanel.Children.Add(CreateChangeRow(change, kind));
        if (changes.Count > limit)
        {
            var hidden = changes.Count - limit;
            var more = new Button
            {
                Content = $"Show {Math.Min(500, hidden)} more…  ({hidden} hidden)",
                FontSize = 11,
                Margin = new Thickness(6, 2, 6, 2),
                Padding = new Thickness(8, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = KodoDesignTokens.CompactRadius,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            more.Click += (_, _) =>
            {
                _rowLimits[key] = limit + 500;
                _lastSignature = string.Empty;
                RefreshPanelContent();
            };
            TrackQuietButton(more, _dynamicThemeSyncs, "MutedTextBrush");
            _groupsPanel.Children.Add(more);
        }
    }

    private Control CreateChangeRow(GitChange change, ChangeGroupKind kind)
    {
        var staged = kind == ChangeGroupKind.Staged;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(6, 1, 6, 1),
            ColumnSpacing = 2
        };

        var open = new Button
        {
            Content = CreateFileLabel(change, staged),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5),
            CornerRadius = KodoDesignTokens.CompactRadius,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var rename = change.IsRenameOrCopy && !string.IsNullOrEmpty(change.OriginalPath) ? $"\n{change.OriginalPath}  →  {change.Path}" : string.Empty;
        ToolTip.SetTip(open, kind == ChangeGroupKind.Conflicts
            ? $"{change.Path}\nMerge conflict - click to open the file and resolve it"
            : $"{change.Path}{rename}\nClick to view the diff");
        open.Click += async (_, _) =>
        {
            if (kind == ChangeGroupKind.Conflicts) await OpenWorkingFileAsync(change);
            else await ShowDiffAsync(change, staged);
        };
        TrackQuietButton(open, _dynamicThemeSyncs);
        row.Children.Add(open);

        if (kind != ChangeGroupKind.Conflicts && !change.IsDeletedInWorktree)
        {
            var openFile = CreateSmallButton("↗", "Open file", async () => await OpenWorkingFileAsync(change));
            Grid.SetColumn(openFile, 1);
            row.Children.Add(openFile);
        }

        Button primary;
        if (staged)
        {
            primary = CreateSmallButton("−", "Unstage", async () =>
                await RunGitOperationAsync(GitCommands.Unstage(change, _hasHead), "Change unstaged."));
        }
        else
        {
            var tip = kind == ChangeGroupKind.Conflicts ? "Mark as resolved (stage)" : "Stage";
            primary = CreateSmallButton("+", tip, async () =>
                await RunGitOperationAsync(GitCommands.Stage(change), kind == ChangeGroupKind.Conflicts ? "Marked as resolved." : "Change staged."));
        }
        Grid.SetColumn(primary, 2);
        row.Children.Add(primary);

        if (kind == ChangeGroupKind.Unstaged)
        {
            var discard = change.IsUntracked
                ? CreateSmallButton("✕", "Delete untracked file…", async () => await DeleteUntrackedAsync(change))
                : CreateSmallButton("↶", "Discard changes…", async () => await DiscardChangeAsync(change));
            Grid.SetColumn(discard, 3);
            row.Children.Add(discard);
        }
        return row;
    }

    private Control CreateFileLabel(GitChange change, bool staged)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 7
        };
        var status = change.DisplayStatus(staged);
        var statusBrushName = GetStatusBrushName(status);
        var statusText = new TextBlock
        {
            Text = status.ToString(),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Width = 12,
            Foreground = GetWindowBrush(statusBrushName),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        TrackTheme(statusText, TextBlock.ForegroundProperty, statusBrushName, _dynamicThemeSyncs);
        grid.Children.Add(statusText);

        // File name first, then the folder in a quieter tone. One trimmed TextBlock means a narrow sidebar clips the
        // folder (the less useful part) before the name - the old "full path, trimmed at the end" cut off the name.
        var (fileName, directory) = GitCommands.SplitPath(change.Path);
        var name = new TextBlock
        {
            FontSize = 12,
            Foreground = GetWindowBrush("PrimaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        name.Inlines!.Add(new Run(fileName));
        var suffix = change.IsRenameOrCopy && !string.IsNullOrEmpty(change.OriginalPath)
            ? $"  ← {change.OriginalPath.Replace('\\', '/')}"
            : directory.Length > 0 ? $"  {directory}" : string.Empty;
        if (suffix.Length > 0)
            name.Inlines.Add(new Run(suffix) { Foreground = GetWindowBrush("MutedTextBrush"), FontSize = 11 });
        TrackTheme(name, TextBlock.ForegroundProperty, "PrimaryTextBrush", _dynamicThemeSyncs);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        return grid;
    }

    private void AddCommitsSection()
    {
        if (_groupsPanel is null || _window is null || _commits.Count == 0) return;
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(6, 14, 10, 4)
        };
        heading.Children.Add(CreateGroupToggle("Recent Commits", "commits"));
        _groupsPanel.Children.Add(heading);
        if (_collapsedGroups.Contains("commits")) return;

        foreach (var commit in _commits)
        {
            var captured = commit;
            var label = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            var hash = new TextBlock
            {
                Text = commit.ShortHash,
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,Monospace"),
                Foreground = GetWindowBrush("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            TrackTheme(hash, TextBlock.ForegroundProperty, "AccentBrush", _dynamicThemeSyncs);
            label.Children.Add(hash);
            var subject = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            subject.Inlines!.Add(new Run(commit.Subject) { Foreground = GetWindowBrush("PrimaryTextBrush") });
            subject.Inlines.Add(new Run($"  {commit.RelativeDate}") { Foreground = GetWindowBrush("MutedTextBrush"), FontSize = 11 });
            Grid.SetColumn(subject, 1);
            label.Children.Add(subject);

            var button = new Button
            {
                Content = label,
                Margin = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 5),
                CornerRadius = KodoDesignTokens.CompactRadius,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(button, $"{commit.Subject}\n{commit.Author} · {commit.RelativeDate}\n{commit.Hash}\nClick to view this commit");
            button.Click += async (_, _) => await ShowCommitAsync(captured);
            TrackQuietButton(button, _dynamicThemeSyncs);
            _groupsPanel.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------------
    // Repository state and operations
    // ---------------------------------------------------------------------

    private async Task RefreshRepositoryAsync(bool fromPoll = false)
    {
        if (_window is null || !_panelOpen) return;
        if (_refreshing)
        {
            _refreshRequested = true;
            if (_refreshCompletion is not null)
                await _refreshCompletion.Task;
            return;
        }
        _refreshing = true;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCompletion = completion;
        try
        {
            do
            {
                _refreshRequested = false;
                await RefreshRepositoryCoreAsync(fromPoll);
            }
            while (_refreshRequested && _panelOpen && _lifetime?.IsCancellationRequested != true);
        }
        finally
        {
            _refreshing = false;
            _refreshCompletion = null;
            completion.TrySetResult(true);
        }
    }

    private void ClearRepositoryState(string? problem = null)
    {
        _repositoryRoot = null;
        _snapshot = null;
        _hasRemote = false;
        _branchName = null;
        _upstreamStatus = string.Empty;
        _changes = [];
        _remotes = [];
        _commits = [];
        _stashCount = 0;
        _hasHead = false;
        _lastOid = null;
        _repositoryProblem = problem;
    }

    private async Task RefreshRepositoryCoreAsync(bool fromPoll)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;
        try
        {
            var candidate = GetWorkspaceCandidate();
            if (candidate is null)
            {
                ClearRepositoryState();
                UpdatePanel();
                SetStatus("Open a folder or file to start.", fromPoll);
                return;
            }

            // The repository root only changes when the open folder/file does, so resolve it once rather than
            // spawning `git rev-parse` on every poll.
            string repository;
            if (_repositoryRoot is not null
                && string.Equals(_cachedCandidate, candidate, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(_repositoryRoot))
            {
                repository = _repositoryRoot;
            }
            else
            {
                var rootResult = await GitProcess.RunAsync(candidate, ["rev-parse", "--show-toplevel"], TimeSpan.FromSeconds(10), token);
                if (!rootResult.Success)
                {
                    // "Not a repository" is normal (offer `git init`); anything else (git missing, "dubious ownership",
                    // permissions) is a real problem the user needs to read, not a prompt to initialise.
                    var notRepository = rootResult.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
                    var message = notRepository ? "No Git repository found in this folder or its parents." : FormatGitError(rootResult);
                    ClearRepositoryState(notRepository ? null : message);
                    UpdatePanel();
                    SetStatus(message, fromPoll);
                    return;
                }
                repository = rootResult.Output.Trim();
                if (!Directory.Exists(repository))
                {
                    ClearRepositoryState("The Git repository path is no longer available.");
                    UpdatePanel();
                    SetStatus("The Git repository path is no longer available.", fromPoll);
                    return;
                }
                _cachedCandidate = candidate;
                _forceFullRefresh = true;
            }

            // One process per poll: porcelain v2 reports branch, HEAD commit, upstream, ahead/behind, renames and
            // conflicts together.
            var status = await GitProcess.RunAsync(repository, GitCommands.StatusArgs, TimeSpan.FromSeconds(30), token);
            if (!status.Success || status.Output.Contains("[Output truncated]", StringComparison.Ordinal))
            {
                _repositoryRoot = repository;
                _repositoryProblem = null;
                _snapshot = null;
                _branchName = null;
                _upstreamStatus = string.Empty;
                _changes = [];
                UpdatePanel();
                SetStatus(status.Success ? "Too many changes to display." : FormatGitError(status), fromPoll);
                return;
            }

            var snapshot = GitStatusParser.Parse(status.Output);

            // Slow-changing data (remotes, history, stash) is refreshed when HEAD moves, after any operation,
            // on a manual refresh and at least once a minute - not on every poll.
            var full = _forceFullRefresh
                       || !string.Equals(_repositoryRoot, repository, StringComparison.Ordinal)
                       || !string.Equals(snapshot.Oid, _lastOid, StringComparison.Ordinal)
                       || DateTime.UtcNow - _lastFullRefreshUtc > TimeSpan.FromSeconds(60);
            if (full)
            {
                var remotesTask = GitProcess.RunAsync(repository, GitCommands.Remotes, TimeSpan.FromSeconds(10), token);
                var stashTask = GitProcess.RunAsync(repository, GitCommands.StashList, TimeSpan.FromSeconds(10), token);
                var logTask = snapshot.HasCommits
                    ? GitProcess.RunAsync(repository, GitCommands.Log(CommitHistoryCount), TimeSpan.FromSeconds(15), token)
                    : Task.FromResult(new GitResult(0, string.Empty, string.Empty));
                await Task.WhenAll(new Task[] { remotesTask, stashTask, logTask });
                var remotes = await remotesTask;
                var stash = await stashTask;
                var log = await logTask;
                _remotes = remotes.Success ? GitCommands.ParseRemotes(remotes.Output) : [];
                _stashCount = stash.Success ? GitCommands.CountLines(stash.Output) : 0;
                _commits = log.Success ? GitCommands.ParseLog(log.Output) : [];
                _lastOid = snapshot.Oid;
                _lastFullRefreshUtc = DateTime.UtcNow;
                _forceFullRefresh = false;
            }

            _repositoryRoot = repository;
            _repositoryProblem = null;
            _snapshot = snapshot;
            _hasRemote = _remotes.Count > 0;
            _branchName = snapshot.Branch;
            _upstreamStatus = snapshot.UpstreamSummary;
            _changes = snapshot.Changes.ToList();
            _hasHead = snapshot.HasCommits;
            UpdatePanel();

            if (!_operationRunning && (!fromPoll || _statusFromPoll))
            {
                var conflicts = _changes.Count(c => c.IsConflicted);
                var summary = _changes.Count == 0
                    ? "Working tree clean."
                    : conflicts > 0
                        ? $"{_changes.Count} changed file(s), {conflicts} with merge conflicts."
                        : $"{_changes.Count} changed file(s).";
                SetStatus(summary, fromPoll: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus($"Git status failed: {ex.Message}", fromPoll);
        }
    }

    private async Task InitializeRepositoryAsync()
    {
        var candidate = GetWorkspaceCandidate();
        if (candidate is null) return;
        await RunGitOperationAsync(["init"], "Initialized a Git repository.", candidate);
    }

    private static bool IsNetworkCommand(IReadOnlyList<string> arguments)
        => arguments.Count > 0 && arguments[0] is "fetch" or "pull" or "push" or "clone";

    private Task RunGitOperationAsync(IReadOnlyList<string> arguments, string successMessage, string? workingDirectory = null)
        => RunGitSequenceAsync([arguments], successMessage, workingDirectory);

    /// <summary>Runs one or more git commands in order, stopping at the first failure.</summary>
    private async Task RunGitSequenceAsync(IReadOnlyList<IReadOnlyList<string>> commands, string successMessage, string? workingDirectory = null)
    {
        if (_operationRunning)
        {
            SetStatus("Another Git operation is still running…");
            return;
        }
        var directory = workingDirectory ?? _repositoryRoot;
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetStatus("No Git repository is open.");
            return;
        }

        var network = commands.Any(IsNetworkCommand);
        _operationRunning = true;
        _operationIsNetwork = network;
        _lastOperationSucceeded = false;
        _lastOperationError = string.Empty;
        // Short local operations lock the panel; network operations can take minutes, so the panel stays usable
        // (and shows a Cancel button) while they run.
        if (!network) SetOperationBusy(true);
        ApplyControlStates();
        SetStatus(network ? "Working…  (Cancel to abort)" : "Working…");
        var operationStatus = string.Empty;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
        _operationCts = cts;
        try
        {
            var failed = false;
            foreach (var command in commands)
            {
                // Pushing/fetching a large repository can legitimately take far longer than a local command.
                var timeout = IsNetworkCommand(command) ? TimeSpan.FromMinutes(10.0) : TimeSpan.FromSeconds(90);
                var result = await GitProcess.RunAsync(directory, command, timeout, cts.Token);
                if (result.Success) continue;
                operationStatus = FormatGitError(result);
                _lastOperationError = operationStatus;
                failed = true;
                break;
            }
            if (!failed)
            {
                _lastOperationSucceeded = true;
                operationStatus = successMessage;
            }
        }
        catch (OperationCanceledException)
        {
            operationStatus = _lifetime?.IsCancellationRequested == true ? string.Empty : "Cancelled.";
        }
        catch (Exception ex) { operationStatus = $"Git operation failed: {ex.Message}"; }
        finally
        {
            _operationRunning = false;
            _operationIsNetwork = false;
            if (ReferenceEquals(_operationCts, cts)) _operationCts = null;
            cts.Dispose();
            _forceFullRefresh = true;     // an operation can change branches, remotes, stash and history
            _lastSignature = string.Empty;
            await RefreshRepositoryAsync();
            SetOperationBusy(false);
            ApplyControlStates();
            if (!string.IsNullOrWhiteSpace(operationStatus)) SetStatus(operationStatus);
        }
    }

    private Task CancelOperationAsync()
    {
        try { _operationCts?.Cancel(); } catch { }
        return Task.CompletedTask;
    }

    private Task ToggleActionsAsync()
    {
        _actionsExpanded = !_actionsExpanded;
        _lastSignature = string.Empty;
        RefreshPanelContent();
        return Task.CompletedTask;
    }

    private async Task PushAsync()
    {
        // A branch without an upstream cannot be pushed with a plain `git push`; publish it and set the upstream.
        var arguments = GitCommands.Push(_snapshot, _remotes);
        var publishing = arguments.Count > 1;
        await RunGitOperationAsync(arguments, publishing ? "Branch published." : "Pushed changes.");
    }

    private async Task StashAsync()
    {
        if (_changes.Count == 0)
        {
            SetStatus("There are no changes to stash.");
            return;
        }
        await RunGitOperationAsync(GitCommands.StashPush, "Changes stashed.");
    }

    private Task PopStashAsync() => RunGitOperationAsync(GitCommands.StashPop, "Stash applied.");

    private Task UndoLastCommitAsync() => RunGitOperationAsync(GitCommands.UndoLastCommit, "Last commit undone. Its changes are staged.");

    private async Task ToggleAmendAsync()
    {
        if (_operationRunning || _repositoryRoot is null) return;
        if (!_amend && !_hasHead)
        {
            SetStatus("There is no commit to amend yet.");
            return;
        }
        _amend = !_amend;
        if (_amend && string.IsNullOrWhiteSpace(_commitMessage?.Text))
        {
            // Start from the existing message, as editors usually do.
            try
            {
                var token = _lifetime?.Token ?? CancellationToken.None;
                var last = await GitProcess.RunAsync(_repositoryRoot, GitCommands.LastCommitMessage, TimeSpan.FromSeconds(10), token);
                if (last.Success && _commitMessage is not null && string.IsNullOrWhiteSpace(_commitMessage.Text))
                    _commitMessage.Text = last.Output.Trim();
            }
            catch (OperationCanceledException) { }
        }
        _lastSignature = string.Empty;
        ApplyControlStates();
    }

    private async Task CommitAsync()
    {
        if (_operationRunning) return;
        _lastOperationSucceeded = false;
        if (_changes.Any(c => c.IsConflicted))
        {
            SetStatus("Resolve the merge conflicts (and stage the resolved files) before committing.");
            return;
        }
        var message = _commitMessage?.Text?.Trim() ?? string.Empty;
        var amend = _amend;
        if (!amend && message.Length == 0)
        {
            SetStatus("Enter a commit message first.");
            _commitMessage?.Focus();
            return;
        }
        if (!amend && !_changes.Any(c => c.IsStaged))
        {
            SetStatus("Stage some changes first.");
            return;
        }
        await RunGitOperationAsync(GitCommands.Commit(message, amend), amend ? "Commit amended." : "Commit created.");
        if (_lastOperationSucceeded)
        {
            // The panel may have been closed while git ran; never assume the controls still exist.
            _commitMessageDraft = string.Empty;
            if (_commitMessage is not null) _commitMessage.Text = string.Empty;
            if (amend)
            {
                _amend = false;
                _lastSignature = string.Empty;
                ApplyControlStates();
            }
        }
    }

    private async Task DiscardChangeAsync(GitChange change)
    {
        if (_window is null || change.IsUntracked) return;
        var confirmed = await ShowConfirmationAsync(
            "Discard Changes",
            $"Discard the unstaged changes to '{change.Path}'? This cannot be undone.",
            "Discard");
        if (confirmed)
            await RunGitOperationAsync(GitCommands.DiscardTracked(change), "Unstaged changes discarded.");
    }

    private async Task DeleteUntrackedAsync(GitChange change)
    {
        if (_window is null || !change.IsUntracked) return;
        var confirmed = await ShowConfirmationAsync(
            "Delete Untracked File",
            $"Permanently delete '{change.Path}'? It is not tracked by Git, so this cannot be undone.",
            "Delete");
        if (confirmed)
            await RunGitOperationAsync(GitCommands.DeleteUntracked([change.Path]), "Untracked file deleted.");
    }

    private async Task DiscardAllAsync()
    {
        if (_window is null || _operationRunning) return;
        var unstaged = _changes.Where(c => c.IsUnstaged && !c.IsConflicted).ToList();
        if (unstaged.Count == 0)
        {
            SetStatus("There are no unstaged changes to discard.");
            return;
        }
        var tracked = unstaged.Where(c => !c.IsUntracked).ToList();
        var untracked = unstaged.Where(c => c.IsUntracked).Select(c => c.Path).ToList();
        var parts = new List<string>();
        if (tracked.Count > 0) parts.Add($"restore {tracked.Count} modified or deleted file(s)");
        if (untracked.Count > 0) parts.Add($"permanently delete {untracked.Count} untracked file(s)");
        var confirmed = await ShowConfirmationAsync(
            "Discard All Changes",
            $"This will {string.Join(" and ", parts)}. Staged changes are kept. This cannot be undone.",
            "Discard All");
        if (!confirmed) return;

        // Name the files explicitly (in batches, to stay under command-line length limits) instead of "." so only
        // what the list shows is touched.
        var commands = new List<IReadOnlyList<string>>();
        foreach (var batch in GitCommands.Chunk(tracked.SelectMany(c => c.Pathspecs).Distinct().ToList(), 100))
            commands.Add(["restore", "--worktree", "--", .. batch]);
        foreach (var batch in GitCommands.Chunk(untracked, 100))
            commands.Add(GitCommands.DeleteUntracked(batch));
        await RunGitSequenceAsync(commands, "Discarded all unstaged changes.");
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message, string confirmText)
    {
        if (_window is null) return false;
        var syncs = new List<IDisposable>();
        var dialog = CreateDialogWindow(title, 420, 210);
        TrackTheme(dialog, Window.BackgroundProperty, "CardBrush", syncs);
        var content = new DockPanel();
        var header = new StackPanel { Spacing = 3, Margin = new Thickness(20, 18, 20, 12) };
        var titleLabel = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush("PrimaryTextBrush")
        };
        TrackTheme(titleLabel, TextBlock.ForegroundProperty, "PrimaryTextBrush", syncs);
        header.Children.Add(titleLabel);
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        var footer = new Border
        {
            Padding = new Thickness(20, 12),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        TrackTheme(footer, Border.BorderBrushProperty, "SurfaceBorderBrush", syncs);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateTextButton("Cancel", "Close without changing anything", async () => { dialog.Close(false); await Task.CompletedTask; }, syncs);
        var confirm = CreateTextButton(confirmText, confirmText, async () => { dialog.Close(true); await Task.CompletedTask; }, syncs, "DangerBrush", "DangerForegroundBrush");
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        var messageLabel = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = GetWindowBrush("PrimaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        TrackTheme(messageLabel, TextBlock.ForegroundProperty, "PrimaryTextBrush", syncs);
        content.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 18), Child = messageLabel });
        dialog.Content = CreateDialogSurface(content, syncs);
        try
        {
            return await dialog.ShowDialog<bool>(_window);
        }
        finally
        {
            DisposeThemeSyncs(syncs);
        }
    }

    private void CommitMessage_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CommitAsync();
            e.Handled = true;
        }
    }

    private void CommitButton_OnClick(object? sender, RoutedEventArgs e) => _ = CommitAsync();

    private void BranchButton_OnClick(object? sender, RoutedEventArgs e) => _ = ShowBranchPickerAsync();

    private async Task ShowBranchPickerAsync()
    {
        if (_window is null || _repositoryRoot is null || _operationRunning) return;
        if (_branchWindow is not null)
        {
            _branchWindow.Activate();
            return;
        }

        var syncs = new List<IDisposable>();
        var repository = _repositoryRoot;
        var picker = CreateDialogWindow("Branches", 440, 560);
        TrackTheme(picker, Window.BackgroundProperty, "CardBrush", syncs);
        _branchWindow = picker;
        picker.Closed += (_, _) =>
        {
            DisposeThemeSyncs(syncs);
            if (ReferenceEquals(_branchWindow, picker)) _branchWindow = null;
        };
        picker.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                picker.Close();
                e.Handled = true;
            }
        };

        var branches = new List<GitBranch>();
        var filterText = string.Empty;
        var list = new StackPanel { Spacing = 2 };
        var createError = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetWindowBrush("DangerBrush"),
            IsVisible = false,
            Margin = new Thickness(2, 0, 0, 0)
        };
        TrackTheme(createError, TextBlock.ForegroundProperty, "DangerBrush", syncs);

        Control Heading(string text)
        {
            var heading = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = GetWindowBrush("MutedTextBrush"),
                Margin = new Thickness(6, 10, 6, 3)
            };
            TrackTheme(heading, TextBlock.ForegroundProperty, "MutedTextBrush", syncs);
            return heading;
        }

        void Populate()
        {
            list.Children.Clear();
            var needle = filterText.Trim();
            bool Matches(GitBranch b) => needle.Length == 0 || b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
            var locals = branches.Where(b => !b.IsRemote && Matches(b))
                .OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var localNames = new HashSet<string>(branches.Where(b => !b.IsRemote).Select(b => b.Name), StringComparer.Ordinal);
            // A remote branch that already has a local twin would just fail `switch --track` - hide it.
            var remotes = branches.Where(b => b.IsRemote && Matches(b))
                .Where(b => !localNames.Contains(b.Name[(b.Name.IndexOf('/') + 1)..]))
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

            if (locals.Count > 0) list.Children.Add(Heading("LOCAL BRANCHES"));
            foreach (var branch in locals)
            {
                var captured = branch;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 2 };
                var label = new TextBlock
                {
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.Inlines!.Add(new Run((branch.IsCurrent ? "✓  " : "⑂  ") + branch.Name) { Foreground = GetWindowBrush(branch.IsCurrent ? "AccentBrush" : "PrimaryTextBrush") });
                if (branch.Upstream is not null)
                    label.Inlines.Add(new Run($"   {branch.Upstream}") { Foreground = GetWindowBrush("MutedTextBrush"), FontSize = 11 });
                var button = new Button
                {
                    Content = label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 8),
                    CornerRadius = KodoDesignTokens.CompactRadius,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    IsEnabled = !branch.IsCurrent
                };
                ToolTip.SetTip(button, branch.IsCurrent ? "Current branch" : $"Switch to {branch.Name}");
                button.Click += async (_, _) =>
                {
                    picker.Close();
                    await RunGitOperationAsync(GitCommands.SwitchTo(captured), $"Switched to {captured.Name}.");
                };
                TrackQuietButton(button, syncs, "PrimaryTextBrush");
                row.Children.Add(button);
                if (!branch.IsCurrent)
                {
                    var delete = CreateSmallButton("✕", $"Delete branch {branch.Name}…", async () =>
                    {
                        await DeleteBranchAsync(captured.Name);
                        await LoadAsync();
                    }, syncs);
                    Grid.SetColumn(delete, 1);
                    row.Children.Add(delete);
                }
                list.Children.Add(row);
            }

            if (remotes.Count > 0) list.Children.Add(Heading("REMOTE BRANCHES"));
            foreach (var branch in remotes)
            {
                var captured = branch;
                var button = new Button
                {
                    Content = new TextBlock
                    {
                        Text = $"☁  {branch.Name}",
                        FontSize = 13,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = GetWindowBrush("PrimaryTextBrush")
                    },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 8),
                    CornerRadius = KodoDesignTokens.CompactRadius,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                ToolTip.SetTip(button, $"Check out {branch.Name} as a new local tracking branch");
                button.Click += async (_, _) =>
                {
                    picker.Close();
                    await RunGitOperationAsync(GitCommands.SwitchTo(captured), $"Checked out {captured.Name}.");
                };
                TrackQuietButton(button, syncs, "PrimaryTextBrush");
                list.Children.Add(button);
            }

            if (locals.Count == 0 && remotes.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = branches.Count == 0 ? "Loading branches…" : "No matching branches.",
                    FontSize = 12,
                    Foreground = GetWindowBrush("MutedTextBrush"),
                    Margin = new Thickness(10, 14)
                };
                TrackTheme(empty, TextBlock.ForegroundProperty, "MutedTextBrush", syncs);
                list.Children.Add(empty);
            }
        }

        async Task LoadAsync()
        {
            var result = await GitProcess.RunAsync(repository, GitCommands.BranchList, TimeSpan.FromSeconds(15), _lifetime?.Token ?? CancellationToken.None);
            if (_branchWindow is null) return;
            if (result.Success) branches = GitCommands.ParseBranches(result.Output);
            else SetStatus(FormatGitError(result));
            Populate();
        }

        async Task CreateAsync(TextBox box)
        {
            var name = box.Text?.Trim() ?? string.Empty;
            var problem = GitCommands.ValidateBranchName(name);
            if (problem is null && branches.Any(b => !b.IsRemote && string.Equals(b.Name, name, StringComparison.Ordinal)))
                problem = $"A branch named '{name}' already exists.";
            if (problem is not null)
            {
                createError.Text = problem;
                createError.IsVisible = true;
                return;
            }
            createError.IsVisible = false;
            picker.Close();
            await RunGitOperationAsync(["switch", "-c", name], $"Created and switched to {name}.");
        }

        var content = new DockPanel();
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(18, 16, 18, 8) };
        var title = new TextBlock
        {
            Text = "Branches",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetWindowBrush("PrimaryTextBrush")
        };
        TrackTheme(title, TextBlock.ForegroundProperty, "PrimaryTextBrush", syncs);
        header.Children.Add(title);
        var search = new TextBox { PlaceholderText = "Filter branches", Padding = new Thickness(9), CornerRadius = KodoDesignTokens.ControlRadius, FontSize = 12 };
        TrackTheme(search, TextBox.BackgroundProperty, "EditorBackgroundBrush", syncs);
        TrackTheme(search, TextBox.ForegroundProperty, "PrimaryTextBrush", syncs);
        TrackTheme(search, TextBox.BorderBrushProperty, "SurfaceBorderBrush", syncs);
        search.TextChanged += (_, _) =>
        {
            filterText = search.Text ?? string.Empty;
            Populate();
        };
        header.Children.Add(search);
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);

        var create = new StackPanel { Spacing = 6, Margin = new Thickness(18, 10, 18, 14) };
        var newName = new TextBox { PlaceholderText = "New branch name (Enter to create)", Padding = new Thickness(9), CornerRadius = KodoDesignTokens.ControlRadius, FontSize = 12 };
        TrackTheme(newName, TextBox.BackgroundProperty, "EditorBackgroundBrush", syncs);
        TrackTheme(newName, TextBox.ForegroundProperty, "PrimaryTextBrush", syncs);
        TrackTheme(newName, TextBox.BorderBrushProperty, "SurfaceBorderBrush", syncs);
        newName.TextChanged += (_, _) => createError.IsVisible = false;
        newName.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await CreateAsync(newName);
        };
        var createButton = CreateTextButton("⑂  Create and Switch", "Create a branch from the current commit and switch to it", async () => await CreateAsync(newName), syncs, "AccentBrush", "AccentForegroundBrush");
        createButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        create.Children.Add(newName);
        create.Children.Add(createError);
        create.Children.Add(createButton);
        var footer = new Border
        {
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = create
        };
        TrackTheme(footer, Border.BorderBrushProperty, "SurfaceBorderBrush", syncs);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);

        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 0),
            Content = list
        });

        picker.Content = CreateDialogSurface(content, syncs);
        Populate();
        picker.Show(_window);
        search.Focus();
        await LoadAsync();
    }

    private async Task DeleteBranchAsync(string name)
    {
        if (!await ShowConfirmationAsync("Delete Branch", $"Delete the local branch '{name}'?", "Delete")) return;
        await RunGitOperationAsync(GitCommands.DeleteBranch(name, force: false), $"Deleted branch {name}.");
        if (!_lastOperationSucceeded && _lastOperationError.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            if (await ShowConfirmationAsync("Delete Unmerged Branch",
                    $"'{name}' has commits that are not merged anywhere else. Deleting it will lose them. Delete anyway?",
                    "Force Delete"))
                await RunGitOperationAsync(GitCommands.DeleteBranch(name, force: true), $"Deleted branch {name}.");
        }
    }

    // ---------------------------------------------------------------------
    // Diff preview
    // ---------------------------------------------------------------------

    private sealed class GitDiffLineColorizer : DocumentColorizingTransformer
    {
        private static readonly MethodInfo? SetTextRunPropertiesMethod =
            typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);
        // Neighbouring lines are needed to tell a file header from a content line; read via reflection so a
        // different AvaloniaEdit build can only ever cost us that refinement, never break the diff view.
        private static readonly PropertyInfo? NextLineProperty = typeof(DocumentLine).GetProperty("NextLine");
        private static readonly PropertyInfo? PreviousLineProperty = typeof(DocumentLine).GetProperty("PreviousLine");
        private static readonly IBrush AddedLineBrush = new SolidColorBrush(Color.Parse("#2EA043"), 0.18);
        private static readonly IBrush RemovedLineBrush = new SolidColorBrush(Color.Parse("#F85149"), 0.18);
        private static readonly IBrush HunkBrush = new SolidColorBrush(Color.Parse("#58A6FF"));
        private static readonly IBrush MetaBrush = new SolidColorBrush(Color.Parse("#8B949E"));

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            try
            {
                var document = CurrentContext.Document;
                var text = document.GetText(line.Offset, line.Length);
                var kind = Classify(document, line, text);
                IBrush? background = kind switch
                {
                    DiffLineKind.Added => AddedLineBrush,
                    DiffLineKind.Removed => RemovedLineBrush,
                    _ => null
                };
                IBrush? foreground = kind switch
                {
                    DiffLineKind.Hunk => HunkBrush,
                    DiffLineKind.FileHeader or DiffLineKind.Meta => MetaBrush,
                    _ => null
                };
                if (background is null && foreground is null) return;

                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    var properties = element.TextRunProperties.Clone();
                    if (background is not null) properties.SetBackgroundBrush(background);
                    if (foreground is not null)
                    {
                        // Guarded at the call site: this lambda runs inside AvaloniaEdit's render loop, outside ColorizeLine's try.
                        try { TrySetForeground(properties, foreground); } catch { }
                    }
                    SetTextRunPropertiesMethod?.Invoke(element, [properties]);
                });
            }
            catch
            {
                // A rendering-pipeline callback must never throw: an uncoloured line is the worst outcome.
            }
        }

        private static DiffLineKind Classify(AvaloniaEdit.Document.TextDocument document, DocumentLine line, string text)
        {
            // Only "--- " / "+++ " lines are ambiguous (a removed line starting "-- " is also "--- "); look at the
            // neighbours for those and nowhere else.
            if (!text.StartsWith("--- ", StringComparison.Ordinal) && !text.StartsWith("+++ ", StringComparison.Ordinal))
                return GitDiffClassifier.Classify(text, null, null);
            if (NextLineProperty is null || PreviousLineProperty is null)
                return GitDiffClassifier.ClassifyWithoutNeighbours(text);
            try
            {
                return GitDiffClassifier.Classify(text, LineText(document, PreviousLineProperty.GetValue(line)), LineText(document, NextLineProperty.GetValue(line)));
            }
            catch
            {
                return GitDiffClassifier.ClassifyWithoutNeighbours(text);
            }
        }

        private static string? LineText(AvaloniaEdit.Document.TextDocument document, object? neighbour)
            => neighbour is DocumentLine other ? document.GetText(other.Offset, other.Length) : null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TrySetForeground(object properties, IBrush brush)
        {
            // Reflection: only the background setter is used elsewhere, and a missing foreground setter should just
            // mean "no tint", not an exception inside AvaloniaEdit's render loop.
            try { properties.GetType().GetMethod("SetForegroundBrush", [typeof(IBrush)])?.Invoke(properties, [brush]); }
            catch { }
        }
    }

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private async Task ShowDiffAsync(GitChange change, bool staged)
    {
        if (_window is null || _repositoryRoot is null) return;
        if (change.IsConflicted)
        {
            // A conflicted file has no meaningful two-sided diff; open it so the markers can be resolved.
            await OpenWorkingFileAsync(change);
            return;
        }
        var request = ++_diffRequest;
        var result = await GitProcess.RunAsync(_repositoryRoot, GitCommands.DiffArgs(change, staged, NullDevice), TimeSpan.FromSeconds(20), _lifetime?.Token ?? CancellationToken.None);
        if (request != _diffRequest || _window is null) return;
        var content = result.ExitCode is 0 or 1
            ? string.IsNullOrWhiteSpace(result.Output) ? "No textual diff is available for this file." : result.Output
            : FormatGitError(result);
        await OpenPreviewAsync(GetDiffPreviewPath(change.Path, staged), content);
    }

    private async Task ShowCommitAsync(GitCommit commit)
    {
        if (_window is null || _repositoryRoot is null) return;
        var request = ++_diffRequest;
        var result = await GitProcess.RunAsync(_repositoryRoot, GitCommands.ShowCommit(commit.Hash), TimeSpan.FromSeconds(30), _lifetime?.Token ?? CancellationToken.None);
        if (request != _diffRequest || _window is null) return;
        var content = result.Success
            ? string.IsNullOrWhiteSpace(result.Output) ? "This commit has no changes to show." : result.Output
            : FormatGitError(result);
        await OpenPreviewAsync(GetCommitPreviewPath(commit), content);
    }

    private async Task OpenPreviewAsync(string previewPath, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
            await File.WriteAllTextAsync(previewPath, content, new UTF8Encoding(false), _lifetime?.Token ?? CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(async () => await OpenDiffPreviewInEditorAsync(previewPath));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open preview: {ex.Message}");
        }
    }

    private async Task OpenWorkingFileAsync(GitChange change)
    {
        if (_window is null || _repositoryRoot is null) return;
        var path = Path.GetFullPath(Path.Combine(_repositoryRoot, change.Path));
        if (!File.Exists(path))
        {
            SetStatus($"'{change.Path}' no longer exists on disk.");
            return;
        }
        try
        {
            var open = typeof(MainWindow).GetMethod("OpenFileFromPathAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (open?.Invoke(_window, [path]) is Task openTask) await openTask;
            _window.ShowEditorPage();
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open file: {ex.Message}");
        }
    }

    private string GetDiffPreviewPath(string sourcePath, bool staged)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".diff";
        var key = $"{sourcePath}|{(staged ? "staged" : "working")}";
        if (_diffPreviewPaths.TryGetValue(key, out var existing)) return existing;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var path = Path.Combine(Path.GetTempPath(), "Kodo", "GitDiffPreviews", $"{Path.GetFileNameWithoutExtension(sourcePath)}-{(staged ? "staged" : "working")}-{id}{extension}");
        _diffPreviewPaths[key] = path;
        // Stored as an absolute path: GetWorkspaceCandidate() later uses it to find the repository, and a path
        // relative to the repository root resolves against the wrong directory.
        _diffPreviewSources[path] = _repositoryRoot is null ? sourcePath : Path.GetFullPath(Path.Combine(_repositoryRoot, sourcePath));
        return path;
    }

    private string GetCommitPreviewPath(GitCommit commit)
    {
        var key = $"commit|{commit.Hash}";
        if (_diffPreviewPaths.TryGetValue(key, out var existing)) return existing;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var path = Path.Combine(Path.GetTempPath(), "Kodo", "GitDiffPreviews", $"commit-{commit.ShortHash}-{id}.diff");
        _diffPreviewPaths[key] = path;
        _diffPreviewSources[path] = _repositoryRoot ?? string.Empty;
        return path;
    }

    private async Task OpenDiffPreviewInEditorAsync(string path)
    {
        if (_window is null) return;
        var existingTab = _window.OpenTabs.FirstOrDefault(tab => string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existingTab is not null)
        {
            existingTab.Content = await File.ReadAllTextAsync(path);
            if (ReferenceEquals(_window.ActiveEditorTab, existingTab))
            {
                typeof(MainWindow).GetMethod("SetEditorContent", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_window, [existingTab.Content]);
                typeof(MainWindow).GetMethod("UpdateCurrentDocumentPresentation", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_window, null);
            }
            else
            {
                var activate = typeof(MainWindow).GetMethod("ActivateTab", BindingFlags.Instance | BindingFlags.NonPublic);
                activate?.Invoke(_window, [existingTab, true, true]);
            }
        }
        else
        {
            var open = typeof(MainWindow).GetMethod("OpenFileFromPathAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (open?.Invoke(_window, [path]) is Task openTask) await openTask;
        }
        _editorText ??= FindNamedControl(_window, "EditorTextBox") as TextEditor;
        if (_editorText is null) return;
        _editorTextBeforeDiffReadOnly ??= _editorText.IsReadOnly;
        _editorText.IsReadOnly = true;
        AttachDiffColorizer();
        _editorText.SyntaxHighlighting = _window.GetSyntaxHighlightingForPath(path);
        _window.ShowEditorPage();
    }

    private bool? _editorTextBeforeDiffReadOnly;

    private void UpdateDiffEditorState()
    {
        if (_window is null) return;
        _editorText ??= FindNamedControl(_window, "EditorTextBox") as TextEditor;
        if (_editorText is null) return;
        var activePath = _window.ActiveEditorTab?.Path;
        var isDiff = activePath is not null && _diffPreviewPaths.Values.Contains(activePath, StringComparer.OrdinalIgnoreCase);
        if (isDiff)
        {
            _editorTextBeforeDiffReadOnly ??= _editorText.IsReadOnly;
            _editorText.IsReadOnly = true;
            AttachDiffColorizer();
            _editorText.SyntaxHighlighting = _window.GetSyntaxHighlightingForPath(activePath!);
        }
        else
        {
            DetachDiffColorizer();
            if (_editorTextBeforeDiffReadOnly is { } wasReadOnly)
            {
                _editorText.IsReadOnly = wasReadOnly;
                _editorTextBeforeDiffReadOnly = null;
            }
        }
    }

    private void AttachDiffColorizer()
    {
        if (_editorText is null || _editorDiffColorizer is not null) return;
        _editorDiffColorizer = new GitDiffLineColorizer();
        _editorText.TextArea.TextView.LineTransformers.Add(_editorDiffColorizer);
    }

    private void DetachDiffColorizer()
    {
        if (_editorText is null || _editorDiffColorizer is null) return;
        _editorText.TextArea.TextView.LineTransformers.Remove(_editorDiffColorizer);
        _editorDiffColorizer = null;
    }

    private void RestoreEditorAfterDiff()
    {
        if (_editorText is null) return;
        DetachDiffColorizer();
        if (_editorTextBeforeDiffReadOnly is { } wasReadOnly)
        {
            _editorText.IsReadOnly = wasReadOnly;
            _editorTextBeforeDiffReadOnly = null;
        }
    }

    // ---------------------------------------------------------------------
    // Small UI helpers
    // ---------------------------------------------------------------------

    private Button CreateIconButton(string text, string tip, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontSize = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = KodoDesignTokens.CompactRadius,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await action();
        TrackButton(button, _fixedThemeSyncs);
        return button;
    }

    private Button CreateSmallButton(string text, string tip, Func<Task> action, List<IDisposable>? themeSyncs = null)
    {
        var button = new Button
        {
            Content = text,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            FontSize = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = KodoDesignTokens.CompactRadius,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await action();
        TrackQuietButton(button, themeSyncs ?? _dynamicThemeSyncs, "MutedTextBrush");
        return button;
    }

    private Button CreateTextButton(
        string text,
        string tip,
        Func<Task> action,
        List<IDisposable>? themeSyncs = null,
        string backgroundBrush = "ButtonBrush",
        string foregroundBrush = "PrimaryTextBrush")
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 6),
            FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = KodoDesignTokens.CompactRadius,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await action();
        TrackButton(button, themeSyncs ?? _dynamicThemeSyncs, backgroundBrush, foregroundBrush);
        return button;
    }

    private void TrackButton(Button button, List<IDisposable> collection, string backgroundBrush = "ButtonBrush", string foregroundBrush = "PrimaryTextBrush")
    {
        if (_window is null) return;
        button.BorderThickness = new Thickness(1);
        TrackTheme(button, Button.BackgroundProperty, backgroundBrush, collection);
        TrackTheme(button, Button.BorderBrushProperty, "SurfaceBorderBrush", collection);
        TrackTheme(button, Button.ForegroundProperty, foregroundBrush, collection);
        button.PointerEntered += (_, _) => button.Background = backgroundBrush == "ButtonBrush"
            ? GetWindowBrush("ButtonHoverBrush")
            : GetWindowBrush(backgroundBrush);
        button.PointerExited += (_, _) => button.Background = GetWindowBrush(backgroundBrush);
    }

    private void TrackQuietButton(Button button, List<IDisposable> collection, string foregroundBrush = "PrimaryTextBrush", bool selected = false)
    {
        if (_window is null) return;
        button.BorderThickness = new Thickness(0);
        button.BorderBrush = Brushes.Transparent;
        button.Background = selected ? GetWindowBrush("ButtonBrush") : Brushes.Transparent;
        TrackTheme(button, Button.ForegroundProperty, foregroundBrush, collection);
        if (selected)
            TrackTheme(button, Button.BackgroundProperty, "ButtonBrush", collection);
        button.PointerEntered += (_, _) => button.Background = GetWindowBrush("ButtonHoverBrush");
        button.PointerExited += (_, _) => button.Background = selected ? GetWindowBrush("ButtonBrush") : Brushes.Transparent;
    }

    private void TrackTheme(AvaloniaObject? target, AvaloniaProperty property, string brushName, List<IDisposable> collection)
    {
        if (target is not null && _window is not null)
            collection.Add(ThemeBrushSync.Track(target, property, _window, brushName));
    }

    private static void DisposeThemeSyncs(List<IDisposable> collection)
    {
        foreach (var subscription in collection)
        {
            try { subscription.Dispose(); } catch { }
        }
        collection.Clear();
    }

    private IBrush? GetWindowBrush(string name)
    {
        try { return _window?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_window) as IBrush; }
        catch { return null; }
    }

    private static string GetStatusBrushName(char status) => status switch
    {
        'A' or '?' or 'R' or 'C' => "AccentBrush",
        'D' => "DangerBrush",
        'U' => "WarningBrush",
        _ => "MutedTextBrush"
    };

    private void SetStatus(string text, bool fromPoll = false)
    {
        // Poll results ("3 changed files") never replace an operation result such as a push error: those stay
        // until the user does something else. (Previously every 4s poll overwrote them.)
        _statusFromPoll = fromPoll;
        if (_statusLabel is null) return;
        _statusLabel.Text = text;
        ToolTip.SetTip(_statusLabel, text);
    }

    private static string FormatGitError(GitResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        message = message.Trim();
        return string.IsNullOrWhiteSpace(message) ? $"Git exited with code {result.ExitCode}." : message;
    }

    private string? GetWorkspaceCandidate()
    {
        if (_window is null) return null;
        var folder = _window.WorkspaceFolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) return Path.GetFullPath(folder);
        var file = _window.ActiveEditorTab?.Path;
        if (file is not null && _diffPreviewSources.TryGetValue(file, out var sourcePath)) file = sourcePath;
        if (string.IsNullOrWhiteSpace(file) || _window.ActiveEditorTab?.IsUntitled == true) return null;
        try
        {
            if (Directory.Exists(file)) return Path.GetFullPath(file);
            if (File.Exists(file)) return Path.GetDirectoryName(Path.GetFullPath(file));
            var parent = Path.GetDirectoryName(Path.GetFullPath(file));
            return Directory.Exists(parent) ? parent : null;
        }
        catch { return null; }
    }

    private static Control? FindNamedControl(Visual root, string name)
    {
        try
        {
            return root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        }
        catch { return null; }
    }

    private Window CreateDialogWindow(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = Math.Min(width, 320),
        MinHeight = Math.Min(height, 220),
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Background = GetWindowBrush("CardBrush")
    };

    private Border CreateDialogSurface(Control content, List<IDisposable> themeSyncs)
    {
        var surface = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = KodoDesignTokens.CardRadius,
            Background = GetWindowBrush("CardBrush"),
            BorderBrush = GetWindowBrush("SurfaceBorderBrush"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = content
        };
        TrackTheme(surface, Border.BackgroundProperty, "CardBrush", themeSyncs);
        TrackTheme(surface, Border.BorderBrushProperty, "SurfaceBorderBrush", themeSyncs);
        return surface;
    }

    private void ShowFallbackWindow()
    {
        if (_window is null) return;
        if (_fallbackWindow is not null)
        {
            _fallbackWindow.Activate();
            return;
        }
        var fallback = CreateDialogWindow("Source Control", 360, 640);
        fallback.Content = CreateSourcePanel();
        _panelOpen = true;
        _fallbackWindow = fallback;
        SetOperationBusy(_operationRunning);
        SetActivitySelected(true);
        fallback.Closed += (_, _) =>
        {
            if (ReferenceEquals(_fallbackWindow, fallback)) _fallbackWindow = null;
            _panelOpen = false;
            ForgetPanelControls();
            SetActivitySelected(false);
            DisposeThemeSyncs(_fixedThemeSyncs);
            DisposeThemeSyncs(_dynamicThemeSyncs);
        };
        _lastSignature = string.Empty;
        fallback.Show(_window);
        _ = RefreshRepositoryAsync();
    }

    private void CloseAuxiliaryWindows()
    {
        try { _branchWindow?.Close(); } catch { }
        try { _fallbackWindow?.Close(); } catch { }
        _branchWindow = null;
        _fallbackWindow = null;
    }
}
