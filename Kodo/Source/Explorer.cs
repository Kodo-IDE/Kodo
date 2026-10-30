// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kodo.Models;
using Kodo.Editor;
using Shared = Kodo.HotfixShared.HotfixShared;

namespace Kodo;

public partial class MainWindow
{
    private const int MaximumQueuedProjectFolderChanges = 4096;
    private readonly object _projectFolderChangeLock = new();
    private readonly HashSet<string> _pendingProjectFolderChangedPaths = new(FileSystemPaths.Comparer);
    private bool _projectFolderChangeDispatchPending;
    private bool _projectFolderChangesNeedFullRecheck;
    private int _fileTreeLoadGeneration;

    private static double NormalizeExplorerPanelWidth(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinExplorerPanelWidth, MaxExplorerPanelWidth)
            : AppSettings.DefaultExplorerPanelWidth;

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
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private void ExplorerPanelSplitter_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        _isResizingExplorerPanel = false;
        SaveSettings(immediate: true);
    }

    private void ExplorerPanelSplitter_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ExplorerPanelWidth = ComputeAutoFitExplorerPanelWidth();
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private double ComputeAutoFitExplorerPanelWidth()
    {
        if (FileTreeItems.Count == 0) return AppSettings.DefaultExplorerPanelWidth;

        var typeface = new Typeface("JetBrains Mono,DejaVu Sans Mono,Ubuntu Mono,Noto Sans Mono,Cascadia Code,Consolas,Menlo,Noto Color Emoji,Segoe UI Emoji,Apple Color Emoji,Monospace");
        var widest = 0.0;
        var sampled = 0;
        const int maxSample = 400;
        foreach (var item in FileTreeItems)
        {
            if (sampled++ >= maxSample) break;
            var formatted = new FormattedText(
                item.Name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                Brushes.Black);

            var total = item.IndentWidth + formatted.Width;
            if (total > widest) widest = total;
        }

        return NormalizeExplorerPanelWidth(widest + FileTreeRowFixedOverhead);
    }

    private void FileTreeItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressExplorerWidthRefresh)
            OnPropertyChanged(nameof(ExplorerPanelWidth));
    }

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

        var tab = ActiveEditorTab;
        var document = EditorTextBox.Document;




        if (OpenTabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Document, document)) is { } ownerTab &&
            !ReferenceEquals(ownerTab, tab))
            return;
        var viewState = tab.GetViewState(_activeEditorPane.Id);
        tab.Document = document;
        tab.Content = string.Empty;
        ActiveEditorTab.IsDirty = _isDirty;
        ActiveEditorTab.LineEnding = _currentLineEnding;
        var scrollOffset = EditorTextBox.TextArea.TextView.ScrollOffset;
        viewState.TopLineNumber = EditorTextBox.TextArea.TextView.GetDocumentLineByVisualTop(
            scrollOffset.Y)?.LineNumber ?? 1;
        viewState.ScrollOffsetX = scrollOffset.X;
        viewState.ScrollOffsetY = scrollOffset.Y;
        viewState.CaretOffset = EditorTextBox.TextArea.Caret.Offset;
        viewState.HasTextViewState = true;
        if (!ActiveEditorTab.IsUntitled && !string.IsNullOrWhiteSpace(_currentFilePath))
            ActiveEditorTab.Path = _currentFilePath;
    }

    private EditorTab CreateUntitledTab()
    {
        var displayName = $"untitled-{_nextUntitledTabNumber++}.txt";
        var defaultEnding = Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;
        return new EditorTab(displayName, displayName, string.Empty, isUntitled: true, lineEnding: defaultEnding)
        {
            Encoding = EditorTab.DefaultEncoding
        };
    }

    private void ActivateTab(EditorTab tab, bool focusEditor = true, bool preserveCurrentState = true, bool scheduleContentLoad = true, bool preserveOtherPaneMembership = false)
    {
        CancelInactiveTabDiagnosticScan(tab);
        if (IsSplitScreenVisible && !CanDisplayInEditorPane(tab))
            CloseSplitEditor();
        MoveTabToActivePane(tab, preserveOtherPaneMembership);
        AddTabToPane(_activeEditorPane, tab);
        var activateWatch = System.Diagnostics.Stopwatch.StartNew();

        if (_inlineTabRenameFocusPending is { } renaming && !ReferenceEquals(renaming, tab))
        {
            renaming.IsRenaming = false;
            renaming.RenameText = renaming.DisplayName;
            _inlineTabRenameFocusPending = null;
        }

        if (ReferenceEquals(ActiveEditorTab, tab))
        {
            if (scheduleContentLoad && !IsImagePreviewFile(tab.Path) &&
                (tab.Document is null || !ReferenceEquals(EditorTextBox.Document, tab.Document) ||
                 tab.Document.TextLength == 0))
            {
                _ = SetEditorTabContentSafelyAsync(tab, _activeTabVersion, _activeEditorPane);
            }
            _isHomePageVisible = false;
            _isSettingsPageVisible = false;
            _isExtensionsPageVisible = false;
            _isTutorialPageVisible = false;
            _isWhatsNewPageVisible = false;
            RaiseMany(nameof(IsHomePageVisible), nameof(IsSettingsPageVisible), nameof(IsExtensionsPageVisible), nameof(IsTutorialPageVisible), nameof(IsWhatsNewPageVisible), nameof(IsEditorPageVisible), nameof(IsHomeOrEditorPageVisible), nameof(IsEditorTabsVisible), nameof(IsDocumentViewVisible), nameof(IsEmptyStateVisible));
            NavigateTo(AppPage.Editor);
            RefreshState(fullRefresh: true);
            Dispatcher.UIThread.Post(() =>
            {
                var sv2 = this.FindControl<ScrollViewer>("EditorTabScrollViewer");
                if (sv2 is null) return;
                var active2 = sv2.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("active") && b.Classes.Contains("tabwrap"));
                active2?.BringIntoView();
            }, DispatcherPriority.Background);
            if (focusEditor)
                FocusEditor();
            return;
        }

        CloseCompletionWindow();
        ClosePendingAutoCloseUndoGroup();
        if (preserveCurrentState)
            SaveCurrentEditorStateIntoTab();
        _isHomePageVisible = false;
        OnPropertyChanged(nameof(IsHomePageVisible));
        OnPropertyChanged(nameof(IsEditorPageVisible));
        OnPropertyChanged(nameof(IsHomeOrEditorPageVisible));
        OnPropertyChanged(nameof(IsEditorTabsVisible));
        ActiveEditorTab = tab;
        ImageZoomLevel = tab.GetViewState(_activeEditorPane.Id).ImageZoomLevel;
        _activeTabVersion++;
        _currentFilePath = tab.IsUntitled ? null : tab.Path;
        EditorSession.SetFilePath(_currentFilePath);
        ResetLspFoldingState();
        _hasUntitledDocument = tab.IsUntitled;
        _isDirty = tab.IsDirty;
        OnPropertyChanged(nameof(IsDocumentDirty));
        _currentLineEnding = tab.LineEnding;
        _currentFileEncoding = tab.Encoding;
        _autoSaveTimer.Stop();
        ClearAutoSaveStatus();
        SetFileCorrupted(_corruptedTabs.Contains(tab));
        var activationVersion = _activeTabVersion;
        var activationPane = _activeEditorPane;
        var activationQueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (scheduleContentLoad)
        {
            Dispatcher.UIThread.Post(() =>
            {
                KodoDiagnostics.ReportSlowStage(
                    "tab content activation dispatch",
                    (long)System.Diagnostics.Stopwatch.GetElapsedTime(activationQueuedAt).TotalMilliseconds,
                    50,
                    $"path={tab.Path}");
                _ = SetEditorTabContentSafelyAsync(tab, activationVersion, activationPane);
            }, DispatcherPriority.Normal);
        }
        var savedViewState = tab.GetViewState(_activeEditorPane.Id);
        var targetTabForScroll = tab;
        var targetPaneForScroll = _activeEditorPane;
        var targetEditorForScroll = EditorTextBox;
        var savedOffsetVersion = _activeTabVersion;
        if (savedViewState.ScrollOffsetX > 0.0 || savedViewState.ScrollOffsetY > 0.0)
            RestorePaneScrollOffset(targetPaneForScroll, targetTabForScroll, targetEditorForScroll, savedOffsetVersion);
        UpdateCurrentDocumentPresentation();

        NavigateTo(AppPage.Editor);
        RefreshState(fullRefresh: true);

        Dispatcher.UIThread.Post(() =>
        {
            var sv = this.FindControl<ScrollViewer>("EditorTabScrollViewer");
            if (sv is null) return;
            var active = sv.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("active") && b.Classes.Contains("tabwrap"));
            active?.BringIntoView();
        }, DispatcherPriority.Background);

        if (focusEditor)
            FocusEditor();
        activateWatch.Stop();
        KodoDiagnostics.ReportSlowStage("tab activation", activateWatch.ElapsedMilliseconds, 1000, $"path={tab.Path} len={tab.Document?.TextLength ?? tab.Content.Length}");
    }

    private async Task SetEditorTabContentSafelyAsync(EditorTab tab, int activationVersion, EditorPaneState activationPane)
    {
        var activationStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await SetEditorTabContentAsync(tab, activationVersion, activationPane);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Tab content activation failed for '{tab.Path}'", ex);
            Dispatcher.UIThread.Post(() =>
            {
                if (_activeTabVersion == activationVersion && ReferenceEquals(_activeEditorPane, activationPane) &&
                    ReferenceEquals(activationPane.ActiveTab, tab))
                    ExtensionsStatusText = $"Unable to finish opening {Path.GetFileName(tab.Path)}: {ex.Message}";
            });
        }
        finally
        {
            KodoDiagnostics.ReportSlowStage(
                "tab content activation total",
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(activationStartedAt).TotalMilliseconds,
                100,
                $"path={tab.Path} len={tab.Document?.TextLength ?? tab.Content.Length}");
        }
    }

    private async Task SetEditorTabContentAsync(EditorTab tab, int activationVersion, EditorPaneState activationPane)
    {
        if (!IsTabActivationCurrent(tab, activationVersion, activationPane)) return;
        await ClearLspFoldingsAsync();
        if (!IsTabActivationCurrent(tab, activationVersion, activationPane)) return;

        var tabDocument = IsImagePreviewFile(_currentFilePath) ? null : tab.Document;
        if (tabDocument is not null)
            SetEditorContent(tabDocument, preserveUndoHistory: true);
        else
            await SetEditorContentAsync(IsImagePreviewFile(_currentFilePath) ? string.Empty : tab.Content, activationVersion, activationPane, tab);
        if (!IsTabActivationCurrent(tab, activationVersion, activationPane)) return;
        if (!tab.IsUntitled && !IsImagePreviewFile(tab.Path))
            await EnsurePaneDocumentHasContentAsync(activationPane, tab);
        if (!IsTabActivationCurrent(tab, activationVersion, activationPane)) return;
        var activeDocument = EditorTextBox.Document;
        tab.Document = activeDocument;
        tab.Content = string.Empty;
        UpdateFindHighlights();
        var documentVersion = activeDocument.Version;
        string? contentSnapshot = null;
        if (!ReferenceEquals(tab.IndentationDetectionVersion, documentVersion))
        {
            var snapshot = EditorTextSnapshotStore.GetSnapshot(activeDocument);
            var indentationSample = await snapshot.GetTextPrefixAsync(1_048_576, EditorSession.RevisionCancellationToken);
            if (!IsTabActivationCurrent(tab, activationVersion, activationPane) ||
                !ReferenceEquals(EditorTextBox.Document, activeDocument) || !ReferenceEquals(activeDocument.Version, documentVersion)) return;
            tab.DetectedIndentation = await Task.Run(() => DetectIndentation(indentationSample));
            if (!IsTabActivationCurrent(tab, activationVersion, activationPane) ||
                !ReferenceEquals(EditorTextBox.Document, activeDocument) || !ReferenceEquals(activeDocument.Version, documentVersion)) return;
            tab.IndentationDetectionVersion = documentVersion;
        }
        if (!tab.IsUntitled && !IsImagePreviewFile(_currentFilePath))
            ApplyIndentationForContent(tab.DetectedIndentation);
        var viewState = tab.GetViewState(_activeEditorPane.Id);
        EditorTextBox.TextArea.Caret.Offset = Math.Clamp(viewState.CaretOffset, 0, EditorTextBox.Document.TextLength);
        EditorTextBox.ScrollToLine(viewState.TopLineNumber);
        RestorePaneScrollOffset(activationPane, tab, EditorTextBox, activationVersion);
        QueueLspPresentationRefresh();
        if (!tab.IsUntitled && !IsImagePreviewFile(tab.Path) && !IsLspDocumentOpenOrOpening(tab.Path))
        {
            contentSnapshot ??= await EditorTextSnapshotStore.GetSnapshot(activeDocument).GetTextAsync();
            if (IsTabActivationCurrent(tab, activationVersion, activationPane) &&
                ReferenceEquals(EditorTextBox.Document, activeDocument))
                _ = LspNotifyDidOpenAsync(tab.Path, contentSnapshot);
        }
    }

    private bool IsTabActivationCurrent(EditorTab tab, int activationVersion, EditorPaneState activationPane) =>
        _activeTabVersion == activationVersion &&
        ReferenceEquals(_activeEditorPane, activationPane) &&
        ReferenceEquals(activationPane.ActiveTab, tab);

    private async Task<string> GetEditorTabContentAsync(EditorTab tab, CancellationToken cancellationToken = default)
    {
        var document = await Dispatcher.UIThread.InvokeAsync(() => tab.Document);
        if (document is null) return tab.Content ?? string.Empty;
        return await EditorTextSnapshotStore.GetSnapshot(document).GetTextAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CloseTab(EditorTab tab)
    {
        CloseTabCore(tab);
        CollapseEmptyEditorPanes();
    }

    private void CloseTabCore(EditorTab tab)
    {
        CancelInactiveTabDiagnosticScan(tab);
        var tabIsInHiddenSecondaryPane = !IsSplitScreenVisible &&
            !ReferenceEquals(ActiveEditorTab, tab) &&
            ReferenceEquals(_secondaryEditorPane.ActiveTab, tab);
        if (!ReferenceEquals(ActiveEditorTab, tab) && !tabIsInHiddenSecondaryPane)
        {
            if (ReferenceEquals(_secondaryEditorPane.ActiveTab, tab))
                ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
            else if (ReferenceEquals(_editorPane.ActiveTab, tab))
                ActivateEditorPane(_editorPane, focusEditor: false);
        }

        var closingActiveTab = ReferenceEquals(tab, ActiveEditorTab);
        var index = OpenTabs.IndexOf(tab);
        if (index < 0)
            return;

        var otherPane = ReferenceEquals(_activeEditorPane, _editorPane)
            ? _secondaryEditorPane
            : _editorPane;
        var activePane = _activeEditorPane;
        var otherPaneAlsoUsesTab = ReferenceEquals(otherPane.ActiveTab, tab);
        var activePaneTabIndex = activePane.Tabs.IndexOf(tab);
        var otherPaneTabIndex = otherPane.Tabs.IndexOf(tab);

        if (closingActiveTab)
            CloseCompletionWindow();
        if (!tab.IsUntitled) _ = LspNotifyDidCloseAsync(tab.Path);
        _ = InsightWorkerGateway.Default.ForgetAsync(_InsightEngine, tab.Path);
        OpenTabs.RemoveAt(index);
        RemoveTabFromPanes(tab);
        _corruptedTabs.Remove(tab);

        if (!closingActiveTab)
        {
            if (tabIsInHiddenSecondaryPane)
            {
                var nextIndex = Math.Min(Math.Max(0, otherPaneTabIndex), _secondaryEditorPane.Tabs.Count - 1);
                var replacementTab = _secondaryEditorPane.Tabs.Count > 0 ? _secondaryEditorPane.Tabs[nextIndex] : null;
                SetOtherPaneTab(_secondaryEditorPane, replacementTab);
            }
            RefreshState(fullRefresh: true);
            return;
        }

        if (otherPaneAlsoUsesTab)
        {
            var nextIndex = Math.Min(Math.Max(0, otherPaneTabIndex), otherPane.Tabs.Count - 1);
            var replacementTab = otherPane.Tabs.Count > 0 ? otherPane.Tabs[nextIndex] : null;
            SetOtherPaneTab(otherPane, replacementTab);
        }

        if (activePane.Tabs.Count > 0)
        {
            var nextIndex = Math.Min(Math.Max(0, activePaneTabIndex), activePane.Tabs.Count - 1);
            var replacementTab = activePane.Tabs[nextIndex];
            ActivateTab(replacementTab, focusEditor: true, preserveCurrentState: false);
            return;
        }

        if (OpenTabs.Count > 0)
        {
            ClearActivePaneAfterLastTabClosed();
            return;
        }

        ClearActivePaneAfterLastTabClosed();
    }

    private void ClearActivePaneAfterLastTabClosed()
    {
        ActiveEditorTab = null;
        _currentFilePath = null;
        EditorSession.SetFilePath(null);
        _hasUntitledDocument = false;
        _isDirty = false;
        OnPropertyChanged(nameof(IsDocumentDirty));
        CurrentLanguageExtension = null;
        CurrentImagePreview = null;
        SetFileCorrupted(false);
        ClearEditorSyntaxState();
        ConfigureRainbowBrackets(null);
        SetEditorContent(string.Empty);
        OnPropertyChanged(nameof(LineEndingDisplayText));
        OnPropertyChanged(nameof(IsLineEndingVisible));
        OnPropertyChanged(nameof(IndentationDisplayText));
        OnPropertyChanged(nameof(IsIndentationVisible));
        OnPropertyChanged(nameof(EncodingDisplayText));

        if (_currentFolderPath is null)
            IsFileExplorerVisible = false;

        RefreshState(fullRefresh: true);
    }

    private void SetOtherPaneTab(EditorPaneState pane, EditorTab? tab)
    {
        var editor = ReferenceEquals(pane, _secondaryEditorPane)
            ? SecondaryEditorTextBox
            : PrimaryEditorTextBox;




        if (pane.ActiveTab is { } currentActive && ReferenceEquals(editor.Document, currentActive.Document))
            SavePaneViewState(pane, editor);
        var otherPane = ReferenceEquals(pane, _editorPane) ? _secondaryEditorPane : _editorPane;
        if (pane.ActiveTab is { } previous && !ReferenceEquals(previous, otherPane.ActiveTab))
            previous.IsSelected = false;
        pane.ActiveTab = tab;
        if (tab is not null)
            AddTabToPane(pane, tab);
        RefreshPanePreviewState(pane);
        RestorePaneImageScrollOffset(pane, tab);
        RefreshPaneTabSelection();
        if (tab is not null)
        {
            if (IsSplitScreenVisible || !ReferenceEquals(pane, _secondaryEditorPane))
                tab.IsSelected = true;
            if ((IsSplitScreenVisible || !ReferenceEquals(pane, _secondaryEditorPane)) && tab.Document is null)
            {
                tab.Document = new AvaloniaEdit.Document.TextDocument(tab.Content);
                tab.Content = string.Empty;
            }
        }

        AssignPaneDocumentWithoutDirtyTracking(editor, IsSplitScreenVisible || !ReferenceEquals(pane, _secondaryEditorPane)
            ? tab?.Document ?? new AvaloniaEdit.Document.TextDocument(string.Empty)
            : new AvaloniaEdit.Document.TextDocument(string.Empty));
        ApplyPaneSyntaxHighlighting(pane, editor, tab);
        if (tab is not null)
        {
            var viewState = tab.GetViewState(pane.Id);
            editor.TextArea.Caret.Offset = Math.Clamp(viewState.CaretOffset, 0, editor.Document.TextLength);
            editor.ScrollToLine(Math.Clamp(viewState.TopLineNumber, 1, editor.Document.LineCount));
        RestorePaneScrollOffset(pane, tab, editor);
        }
        OnPropertyChanged(nameof(PrimaryPaneBreadcrumbText));
        OnPropertyChanged(nameof(SecondaryPaneBreadcrumbText));
        OnPropertyChanged(nameof(SecondaryPaneSelectedTab));
        QueueLspPresentationRefresh();
        if (tab is { IsUntitled: false } && !string.IsNullOrWhiteSpace(tab.Path) && !ReferenceEquals(tab, ActiveEditorTab))
            UpdateInactiveTabDiagnosticsForFile(tab.Path);
    }

    private async Task<bool> RequestCloseTabAsync(EditorTab tab)
    {
        if (tab.IsDirty && IsConfirmBeforeClosingUnsavedTabsEnabled)
        {
            var originalPane = _activeEditorPane;
            var originalActiveTab = ActiveEditorTab;
            var action = await ShowUnsavedTabDialogAsync(tab);
            switch (action)
            {
                case UnsavedTabAction.Cancel:
                    return false;
                case UnsavedTabAction.Save:
                    var savePane = ReferenceEquals(_editorPane.ActiveTab, tab)
                        ? _editorPane
                        : ReferenceEquals(_secondaryEditorPane.ActiveTab, tab)
                            ? _secondaryEditorPane
                            : _activeEditorPane;
                    if (!ReferenceEquals(_activeEditorPane, savePane))
                        ActivateEditorPane(savePane, focusEditor: false);
                    if (!ReferenceEquals(savePane.ActiveTab, tab))
                        ActivateTab(tab, focusEditor: false, scheduleContentLoad: false);

                    var saveActivationVersion = _activeTabVersion;
                    if (!IsImagePreviewFile(tab.Path) &&
                        (!ReferenceEquals(tab.Document, EditorTextBox.Document) || tab.Document is null))
                    {
                        await SetEditorTabContentSafelyAsync(tab, saveActivationVersion, savePane);
                        if (!ReferenceEquals(EditorTextBox.Document, tab.Document))
                        {
                            RestoreAfterClosePrompt(savePane, originalPane, originalActiveTab, tab);
                            return false;
                        }
                    }

                    await SaveAsync();
                    if (tab.IsDirty)
                    {
                        RestoreAfterClosePrompt(savePane, originalPane, originalActiveTab, tab);
                        return false;
                    }

                    RestoreAfterClosePrompt(savePane, originalPane, originalActiveTab, tab);
                    break;
            }
        }

        CloseTab(tab);
        return true;
    }

    private void RestoreAfterClosePrompt(EditorPaneState savePane, EditorPaneState originalPane, EditorTab? originalTab, EditorTab closingTab)
    {
        if (!ReferenceEquals(_activeEditorPane, savePane) || !ReferenceEquals(savePane.ActiveTab, closingTab))
            return;

        if (originalTab is null || !OpenTabs.Contains(originalTab))
            return;

        ActivateEditorPane(originalPane, focusEditor: false);
        if (!ReferenceEquals(originalPane.ActiveTab, originalTab))
            ActivateTab(originalTab, focusEditor: false);
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

    private const long MaxFileSizeForFullLoad = 5_000_000;

    private async Task OpenFileFromPathAsync(string path, EditorPaneState? requestedPane = null, bool preserveExistingPaneMembership = false)
    {
        var destinationPane = ResolveEditorPaneForOpen(requestedPane ?? _activeEditorPane);
        EnsureCurrentDocumentHasTab();

        var existingTab = OpenTabs.FirstOrDefault(tab =>
            !tab.IsUntitled &&
            FileSystemPaths.Equals(tab.Path, path));
        if (existingTab is not null)
        {
            AddRecentFile(path);
            destinationPane = ResolveEditorPaneForOpen(destinationPane);
            if (!ReferenceEquals(_activeEditorPane, destinationPane))
                ActivateEditorPane(destinationPane, focusEditor: false);
            if (!ReferenceEquals(destinationPane.ActiveTab, existingTab))
                ActivateTab(existingTab, scheduleContentLoad: false, preserveOtherPaneMembership: preserveExistingPaneMembership);
            var activationVersion = _activeTabVersion;
            var targetEditor = GetEditorForPane(destinationPane);
            if (!IsImagePreviewFile(path) &&
                (existingTab.Document is null || !ReferenceEquals(targetEditor.Document, existingTab.Document)))
                await SetEditorTabContentSafelyAsync(existingTab, activationVersion, destinationPane);
            return;
        }

        using var loading = BeginAppLoading($"Opening {Path.GetFileName(path)}");

        string content;
        bool isCorrupted;
        var fileEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (IsImagePreviewFile(path))
            {
                content = string.Empty;
                isCorrupted = false;
            }
            else
            {
                var (encoding, corrupted) = await Task.Run(() =>
                {
                    if (IsBinaryContent(path))
                        return (System.Text.Encoding.UTF8, true);
                    return (SearchEngine.DetectFileEncoding(path), false);
                });

                isCorrupted = corrupted;
                fileEncoding = encoding;

                if (File.Exists(path) && new FileInfo(path).Length > MaxFileSizeForFullLoad)
                {
                    content = await ReadLargeFileAsync(path, encoding);
                }
                else
                {
                    content = isCorrupted ? string.Empty : await File.ReadAllTextAsync(path, encoding);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Open file", ex);
            return;
        }

        var lineEndingForTab = !IsImagePreviewFile(path) && !isCorrupted && !string.IsNullOrEmpty(content)
            ? await Task.Run(() => DetectLineEnding(content))
            : Environment.NewLine == "\r\n" ? Kodo.Models.LineEnding.CRLF : Kodo.Models.LineEnding.LF;

        var loadedExistingTab = OpenTabs.FirstOrDefault(tab =>
            !tab.IsUntitled && FileSystemPaths.Equals(tab.Path, path));
        if (loadedExistingTab is not null)
        {
            AddRecentFile(path);
            destinationPane = ResolveEditorPaneForOpen(destinationPane);
            if (!ReferenceEquals(_activeEditorPane, destinationPane))
                ActivateEditorPane(destinationPane, focusEditor: false);
            ActivateTab(loadedExistingTab, preserveOtherPaneMembership: preserveExistingPaneMembership);
            return;
        }

        NavigateTo(AppPage.Editor);

        var tab = new EditorTab(path, Path.GetFileName(path), content, lineEnding: lineEndingForTab) { Encoding = fileEncoding };
        if (isCorrupted)
            _corruptedTabs.Add(tab);
        CaptureTabDiskState(tab);
        OpenTabs.Add(tab);
        AddRecentFile(path);
        destinationPane = ResolveEditorPaneForOpen(destinationPane);
        if (!ReferenceEquals(_activeEditorPane, destinationPane))
            ActivateEditorPane(destinationPane, focusEditor: false);
        ActivateTab(tab);
        if (!isCorrupted && !IsImagePreviewFile(path)) _ = LspNotifyDidOpenAsync(path, content);
    }

    private static async Task<string> ReadLargeFileAsync(string path, System.Text.Encoding encoding)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: false);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
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

    private async Task PopulateFileTreeAsync(string folderPath)
    {
        var generation = ++_fileTreeLoadGeneration;
        var items = await CreateFileTreeItemsAsync(folderPath, depth: 0);
        if (generation != _fileTreeLoadGeneration || !FileSystemPaths.Equals(_currentFolderPath, folderPath))
            return;
        ReplaceFileTreeItems(items);
    }

    private void SetupProjectFolderWatcher(string folderPath)
    {
        DisposeProjectFolderWatcher();

        try
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += ProjectFolderWatcher_OnChanged;
            watcher.Deleted += ProjectFolderWatcher_OnChanged;
            watcher.Renamed += ProjectFolderWatcher_OnChanged;
            watcher.Changed += ProjectFolderWatcher_OnChanged;
            watcher.Error += ProjectFolderWatcher_OnError;
            watcher.EnableRaisingEvents = true;

            _projectFolderWatcher = watcher;
        }
        catch
        {
        }
    }

    private void DisposeProjectFolderWatcher()
    {
        _fileTreeRefreshTimer.Stop();

        if (_projectFolderWatcher is null) return;

        _projectFolderWatcher.Created -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Deleted -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Renamed -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Changed -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Error -= ProjectFolderWatcher_OnError;
        _projectFolderWatcher.Dispose();
        _projectFolderWatcher = null;
    }

    private void ProjectFolderWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (_projectFolderChangeLock)
        {
            QueueProjectFolderChangePath(e.FullPath);
            if (e is RenamedEventArgs renamed)
                QueueProjectFolderChangePath(renamed.OldFullPath);

            if (_projectFolderChangeDispatchPending)
                return;

            _projectFolderChangeDispatchPending = true;
        }

        Dispatcher.UIThread.Post(ProcessProjectFolderChanges);
    }

    private void QueueProjectFolderChangePath(string path)
    {
        if (_projectFolderChangesNeedFullRecheck)
            return;

        _pendingProjectFolderChangedPaths.Add(path);
        if (_pendingProjectFolderChangedPaths.Count <= MaximumQueuedProjectFolderChanges)
            return;

        _pendingProjectFolderChangedPaths.Clear();
        _projectFolderChangesNeedFullRecheck = true;
    }

    private void ProcessProjectFolderChanges()
    {
        string[] changedPaths;
        bool needsFullRecheck;
        lock (_projectFolderChangeLock)
        {
            changedPaths = _pendingProjectFolderChangedPaths.ToArray();
            _pendingProjectFolderChangedPaths.Clear();
            needsFullRecheck = _projectFolderChangesNeedFullRecheck;
            _projectFolderChangesNeedFullRecheck = false;
            _projectFolderChangeDispatchPending = false;
        }

        if (needsFullRecheck)
            RecheckDirtyTabsForExternalChanges();
        else
            foreach (var path in changedPaths)
                MarkExternallyChangedTab(path);

        RestartFileTreeRefreshTimer();
    }

    private void MarkExternallyChangedTab(string path)
    {
        if (_isSaving) return;

        foreach (var tab in OpenTabs)
        {
            if (tab.IsUntitled || !tab.IsDirty || tab.IsModifiedExternally || tab.Path is null) continue;
            if (!FileSystemPaths.Equals(tab.Path, path)) continue;
            if (!IsTabDiskStateChanged(tab)) continue;
            tab.IsModifiedExternally = true;
            if (ReferenceEquals(tab, ActiveEditorTab))
            {
                _autoSaveStatusTimer.Stop();
                _autoSaveStatusMessage = $"{Path.GetFileName(tab.Path)} changed on disk.";
                OnPropertyChanged(nameof(FileSummaryText));
            }
        }
    }

    private void RecheckDirtyTabsForExternalChanges()
    {
        if (_isSaving) return;

        foreach (var tab in OpenTabs)
        {
            if (tab.IsUntitled || !tab.IsDirty || tab.IsModifiedExternally || tab.Path is null)
                continue;
            if (!IsTabDiskStateChanged(tab))
                continue;

            tab.IsModifiedExternally = true;
            if (ReferenceEquals(tab, ActiveEditorTab))
            {
                _autoSaveStatusTimer.Stop();
                _autoSaveStatusMessage = $"{Path.GetFileName(tab.Path)} changed on disk.";
                OnPropertyChanged(nameof(FileSummaryText));
            }
        }
    }

    private static void CaptureTabDiskState(EditorTab tab)
    {
        if (tab.IsUntitled || string.IsNullOrWhiteSpace(tab.Path))
        {
            tab.LastKnownWriteTimeUtc = DateTime.MinValue;
            tab.LastKnownLength = -1;
            return;
        }

        try
        {
            var info = new FileInfo(tab.Path);
            tab.LastKnownWriteTimeUtc = info.LastWriteTimeUtc;
            tab.LastKnownLength = info.Exists ? info.Length : -1;
        }
        catch
        {
            tab.LastKnownWriteTimeUtc = DateTime.MinValue;
            tab.LastKnownLength = -1;
        }
    }

    private static bool IsTabDiskStateChanged(EditorTab tab)
    {
        if (tab.IsUntitled || string.IsNullOrWhiteSpace(tab.Path)) return false;
        if (tab.LastKnownLength < 0) return false;

        try
        {
            var info = new FileInfo(tab.Path);
            if (!info.Exists) return true;
            return info.LastWriteTimeUtc != tab.LastKnownWriteTimeUtc || info.Length != tab.LastKnownLength;
        }
        catch
        {
            return false;
        }
    }

    private void RestartFileTreeRefreshTimer()
    {
        _fileTreeRefreshTimer.Stop();
        _fileTreeRefreshTimer.Start();
    }

    private async void FileTreeRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _fileTreeRefreshTimer.Stop();
        if (!IsActive) { _fileTreeRefreshTimer.Start(); return; }
        _searchFileCache = null;
        if (_newFileInlineRenameItem?.IsRenaming == true)
        {
            RestartFileTreeRefreshTimer();
            return;
        }
        await RefreshFileTreePreservingExpansionAsync();
    }

    private async Task RefreshFileTreePreservingExpansionAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath) || !Directory.Exists(_currentFolderPath))
            return;

        var folderPath = _currentFolderPath;
        var generation = ++_fileTreeLoadGeneration;
        var expandedPaths = FileTreeItems
            .Where(i => i.IsDirectory && i.IsExpanded)
            .Select(i => i.FullPath)
            .ToHashSet(FileSystemPaths.Comparer);

        var items = await BuildFileTreeItemsAsync(folderPath, depth: 0, expandedPaths);
        if (generation != _fileTreeLoadGeneration || !FileSystemPaths.Equals(_currentFolderPath, folderPath))
            return;
        ReplaceFileTreeItems(items);
    }

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

        _suppressExplorerWidthRefresh = true;
        FileTreeItems.CollectionChanged -= FileTreeItems_CollectionChanged;
        try
        {
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
        finally
        {
            FileTreeItems.CollectionChanged += FileTreeItems_CollectionChanged;
            _suppressExplorerWidthRefresh = false;
            FileTreeItems_CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(nameof(FileTreeItems));
            OnPropertyChanged(nameof(ExplorerPanelWidth));
        }
        UpdateFileTreeErrorCountsForOpenTabs();
    }

    private void ReplaceFileTreeItems(IReadOnlyList<FileTreeItem> items)
    {
        _suppressExplorerWidthRefresh = true;
        try
        {
            FileTreeItems.CollectionChanged -= FileTreeItems_CollectionChanged;
            try
            {
                FileTreeItems.Clear();
                foreach (var item in items)
                    FileTreeItems.Add(item);
            }
            finally
            {
                FileTreeItems.CollectionChanged += FileTreeItems_CollectionChanged;
            }
            FileTreeItems_CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(nameof(FileTreeItems));
        }
        finally
        {
            _suppressExplorerWidthRefresh = false;
            OnPropertyChanged(nameof(ExplorerPanelWidth));
        }
        UpdateFileTreeErrorCountsForOpenTabs();
    }

    private void UpdateFileTreeErrorCount(string? filePath, int errors)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        foreach (var item in FileTreeItems)
        {
            if (!item.IsDirectory && FileSystemPaths.Equals(item.FullPath, filePath))
                item.UpdateErrorCount(errors);
        }
    }

    private void UpdateFileTreeErrorCountsForOpenTabs()
    {
        foreach (var tab in OpenTabs)
        {
            if (!tab.IsUntitled)
                UpdateFileTreeErrorCount(tab.Path, tab.ErrorCount);
        }
    }

    private static Task<List<FileTreeItem>> CreateFileTreeItemsAsync(string dirPath, int depth) =>
        Task.Run(() =>
        {
            var items = new List<FileTreeItem>();
            foreach (var entry in GetSortedEntries(dirPath))
            {
                try
                {
                    items.Add(new FileTreeItem
                    {
                        Name = Path.GetFileName(entry),
                        FullPath = entry,
                        IsDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0,
                        Depth = depth,
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return items;
        });

    private async Task ToggleDirectoryExpansionAsync(FileTreeItem dirItem)
    {
        var index = FileTreeItems.IndexOf(dirItem);
        if (index < 0) return;

        if (dirItem.IsExpanded)
        {
            dirItem.ExpansionRequestVersion++;
            MutateFileTreeItems(() =>
            {
                dirItem.IsExpanded = false;
                var endIndex = index + 1;
                while (endIndex < FileTreeItems.Count && FileTreeItems[endIndex].Depth > dirItem.Depth)
                    endIndex++;
                for (var i = endIndex - 1; i > index; i--)
                    FileTreeItems.RemoveAt(i);
            });
            return;
        }

        var generation = _fileTreeLoadGeneration;
        var expansionRequestVersion = ++dirItem.ExpansionRequestVersion;
        dirItem.IsExpanded = true;
        var items = await CreateFileTreeItemsAsync(dirItem.FullPath, dirItem.Depth + 1);
        if (generation != _fileTreeLoadGeneration ||
            dirItem.ExpansionRequestVersion != expansionRequestVersion || !dirItem.IsExpanded ||
            (index = FileTreeItems.IndexOf(dirItem)) < 0)
            return;

        MutateFileTreeItems(() =>
        {
            for (var i = 0; i < items.Count; i++)
                FileTreeItems.Insert(index + 1 + i, items[i]);
        });
    }

    private void MutateFileTreeItems(Action mutation)
    {
        _suppressExplorerWidthRefresh = true;
        FileTreeItems.CollectionChanged -= FileTreeItems_CollectionChanged;
        try
        {
            mutation();
        }
        finally
        {
            FileTreeItems.CollectionChanged += FileTreeItems_CollectionChanged;
            _suppressExplorerWidthRefresh = false;
            FileTreeItems_CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(nameof(FileTreeItems));
            OnPropertyChanged(nameof(ExplorerPanelWidth));
        }
    }

    public RecentFileItem? MostRecentFileItem => RecentFiles.OrderByDescending(item => item.LastOpened).FirstOrDefault();
    public bool HasMostRecentFileItem => MostRecentFileItem is not null;

    public IEnumerable<RecentFileItem> PinnedRecentFiles => RecentFiles.Where(item => item.IsPinned);
    public bool HasPinnedRecentFiles => RecentFiles.Any(item => item.IsPinned);

    private void NotifyRecentFilesChanged() => RaiseMany(
        nameof(HasRecentFiles), nameof(MostRecentFileItem), nameof(HasMostRecentFileItem),
        nameof(PinnedRecentFiles), nameof(HasPinnedRecentFiles));

    private void LoadRecentFiles(IEnumerable<RecentFileEntry>? recentFiles)
    {
        RecentFiles.Clear();

        foreach (var entry in (recentFiles ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .OrderByDescending(entry => entry.IsPinned)
            .ThenByDescending(entry => entry.LastOpened))
        {
            RecentFiles.Add(new RecentFileItem(entry.Path, entry.IsFolder, entry.LastOpened, entry.IsPinned));
        }

        NotifyRecentFilesChanged();
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
            FileSystemPaths.Equals(item.Path, path));

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
        NotifyRecentFilesChanged();
    }

    private void TogglePinnedRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(item =>
            FileSystemPaths.Equals(item.Path, path));
        if (existing is null) return;

        existing.IsPinned = !existing.IsPinned;
        ReorderRecentFiles();
        SaveSettings();
        NotifyRecentFilesChanged();
    }

    private void RemoveRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(f => FileSystemPaths.Equals(f.Path, path));
        if (existing is null) return;
        RecentFiles.Remove(existing);
        SaveSettings();
        NotifyRecentFilesChanged();
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

    private void ZoomInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ActivateImagePaneForControl(sender);
        ZoomImageIn();
    }

    private void ZoomOutButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ActivateImagePaneForControl(sender);
        ZoomImageOut();
    }

    private void ZoomResetButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ActivateImagePaneForControl(sender);
        ZoomImageReset();
    }

    private void ActivateImagePaneForControl(object? sender)
    {
        if (sender is not Visual visual) return;
        var controls = visual.GetSelfAndVisualAncestors().ToArray();
        if (controls.Any(control => ReferenceEquals(control, SecondaryEditorPane)))
            ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        else if (controls.Any(control => ReferenceEquals(control, PrimaryEditorPane)))
            ActivateEditorPane(_editorPane, focusEditor: false);
    }

    private void ZoomImageIn()
    {
        if (ActiveEditorTab is { } tab)
            tab.GetViewState(_activeEditorPane.Id).HasImageViewState = true;
        ImageZoomLevel = SnapToNiceZoom(_imageZoomLevel + ImageZoomStep);
    }

    private void ZoomImageOut()
    {
        if (ActiveEditorTab is { } tab)
            tab.GetViewState(_activeEditorPane.Id).HasImageViewState = true;
        ImageZoomLevel = SnapToNiceZoom(_imageZoomLevel - ImageZoomStep);
    }

    private void ZoomImageReset()
    {
        if (ActiveEditorTab is { } tab)
            tab.GetViewState(_activeEditorPane.Id).HasImageViewState = true;
        ImageZoomLevel = 1.0;
    }

    private static double SnapToNiceZoom(double zoom)
    {
        var snapped = Math.Round(zoom / ImageZoomStep) * ImageZoomStep;
        return Math.Clamp(snapped, ImageZoomMin, ImageZoomMax);
    }

    private void ImageScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ActivateImagePaneForControl(sender);
        if (!HasImagePreview) return;
        var hasControl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (!hasControl) return;

        if (e.Delta.Y > 0)
            ZoomImageIn();
        else if (e.Delta.Y < 0)
            ZoomImageOut();

        e.Handled = true;
    }

    private void CollapseExplorerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFirstRunTutorialActive) return;
        IsFileExplorerVisible = !IsFileExplorerVisible;
    }

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
            InvalidateLspResolutionCaches();
            InvalidateDotnetProjectCache();
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
        if (_tabDragSuppressClick) return;
        if (sender is Button { Tag: EditorTab tab })
            ActivateTab(tab);
    }

    private async void CloseEditorTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            await RequestCloseTabAsync(tab);
    }

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

    private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
    {
        var visited = new HashSet<string>(FileSystemPaths.Comparer);
        visited.Add(NormalizeDirectoryIdentity(sourceDirectory));
        CopyDirectoryRecursiveCore(sourceDirectory, destinationDirectory, visited, 0);
    }

    private static void CopyDirectoryRecursiveCore(string sourceDirectory, string destinationDirectory, HashSet<string> visited, int depth)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            if (!TryEnterCopiedDirectory(directory, sourceDirectory, visited, depth)) continue;
            var destinationChild = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectoryRecursiveCore(directory, destinationChild, visited, depth + 1);
        }
    }

    private static async Task CopyDirectoryRecursiveAsync(string sourceDirectory, string destinationDirectory, CancellationToken ct = default)
    {
        var visited = new HashSet<string>(FileSystemPaths.Comparer);
        visited.Add(NormalizeDirectoryIdentity(sourceDirectory));
        await CopyDirectoryRecursiveAsyncCore(sourceDirectory, destinationDirectory, ct, visited, 0).ConfigureAwait(false);
    }

    private static async Task CopyDirectoryRecursiveAsyncCore(string sourceDirectory, string destinationDirectory, CancellationToken ct, HashSet<string> visited, int depth)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            await using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var dst = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(file)); }
                catch (Exception ex) { KodoDiagnostics.LogDebug($"Could not preserve permissions on {destinationFile}", ex); }
            }
        }
        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryEnterCopiedDirectory(directory, sourceDirectory, visited, depth)) continue;
            var destinationChild = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            await CopyDirectoryRecursiveAsyncCore(directory, destinationChild, ct, visited, depth + 1).ConfigureAwait(false);
        }
    }

    private const int MaxDirectoryCopyDepth = 64;

    private static bool TryEnterCopiedDirectory(string directory, string parentDirectory, HashSet<string> visited, int depth)
    {
        if (depth >= MaxDirectoryCopyDepth)
        {
            KodoDiagnostics.LogDebug($"Stopped copying '{directory}' - the directory tree is deeper than {MaxDirectoryCopyDepth} levels.");
            return false;
        }

        if (IsDirectoryLink(directory))
        {
            KodoDiagnostics.LogDebug($"Skipped linked directory '{directory}' while copying '{parentDirectory}'.");
            return false;
        }

        string identity;
        try
        {
            identity = NormalizeDirectoryIdentity(directory);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Skipped '{directory}' while copying '{parentDirectory}' - the path could not be resolved.", ex);
            return false;
        }

        if (visited.Add(identity))
            return true;

        KodoDiagnostics.LogDebug($"Skipped '{directory}' while copying '{parentDirectory}' - it was already copied.");
        return false;
    }

    private static bool IsDirectoryLink(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeDirectoryIdentity(string directory)
    {
        var resolved = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory;
        return Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

            if (OperatingSystem.IsLinux() && selectItem && File.Exists(path))
            {
                var fileManagers = new[]
                {
                    ("nautilus", $"--select \"{path}\""),
                    ("dolphin",  $"--select \"{path}\""),
                    ("nemo",     $"\"{path}\""),
                    ("thunar",   $"\"{Path.GetDirectoryName(path)}\""),
                };

                foreach (var (binary, args) in fileManagers)
                {
                    if (!IsExecutableOnPath(binary)) continue;

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = binary,
                        Arguments = args,
                        UseShellExecute = false
                    });
                    return;
                }
            }

            var fallbackDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = $"\"{fallbackDir}\"", UseShellExecute = false });
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = fallbackDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Could not open path: {ex.Message}";
            await ShowWarningDialogAsync("Open in system explorer", ex);
        }
    }

    private static bool IsExecutableOnPath(string name)
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return false;
            foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var dir = raw.Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return true;
                }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    private async Task RefreshExplorerTreeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
        {
            var folderPath = _currentFolderPath;
            var expandedPaths = FileTreeItems
                .Where(i => i.IsDirectory && i.IsExpanded)
                .Select(i => i.FullPath)
                .ToHashSet(FileSystemPaths.Comparer);

            var populateTask = PopulateFileTreeAsync(folderPath);
            var generation = _fileTreeLoadGeneration;
            await populateTask;
            if (generation != _fileTreeLoadGeneration || !FileSystemPaths.Equals(_currentFolderPath, folderPath))
                return;

            if (expandedPaths.Count > 0)
                await RestoreExpandedPathsAsync(expandedPaths, generation);
        }
    }

    private async Task<bool> CloseTabsForPathAsync(string path, bool isDirectory)
    {
        var matchingTabs = OpenTabs
            .Where(tab => !tab.IsUntitled && (
                FileSystemPaths.Equals(tab.Path, path) ||
                (isDirectory && FileSystemPaths.IsPrefixOf(tab.Path, path))))
            .ToList();

        foreach (var tab in matchingTabs)
        {
            if (!await RequestCloseTabAsync(tab))
                return false;
        }

        return true;
    }

    private void CloseTabsWithoutPrompt(IEnumerable<EditorTab> tabs)
    {
        foreach (var tab in tabs.ToList())
            CloseTab(tab);
    }

    private async Task<string?> ShowRenameDialogAsync(
        string currentName,
        string title = "Rename",
        string action = "Rename",
        string prompt = "Enter a new name:")
    {
        string? result = null;
        Window? dialog = null;

        var inputBox = new TextBox
        {
            Text = currentName,
            Background = ButtonBrush,
            Foreground = PrimaryTextBrush,
            BorderBrush = SurfaceBorderBrush,
            Padding = new Thickness(8, 6),
            FontSize = 14,
            CaretBrush = PrimaryTextBrush,
        };

        var confirmButton = CreateDialogButton(action, AccentBrush, AccentBrush, AccentForegroundBrush, () =>
        {
            result = inputBox.Text?.Trim();
            dialog!.Close();
        });

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = inputBox.Text?.Trim(); dialog!.Close(); }
            if (e.Key == Key.Escape) { dialog!.Close(); }
        };

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
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var divider = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        var inner = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                headerRow,
                new TextBlock { Text = prompt, FontSize = 13, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap },
                inputBox,
                divider,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush,
                            () => dialog!.Close()),
                        confirmButton
                    }
                }
            }
        };

        dialog = new Window
        {
            Width = 400,
            SizeToContent = SizeToContent.Height,
            MinWidth = 360,
            MaxHeight = 340,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
            Background = WindowBackgroundBrush,
            Content = CreateDialogSurface(inner)
        };

        dialog.Opened += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        using (PerformanceBudget.TrackModal())
        {
            await dialog.ShowDialog(this);
        }
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private async void CloseTabsToTheRightMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } pivotTab) return;
        var pivotIndex = OpenTabs.IndexOf(pivotTab);
        if (pivotIndex < 0) return;

        var toClose = OpenTabs.Skip(pivotIndex + 1).ToList();
        foreach (var tab in toClose)
        {
            if (!await RequestCloseTabAsync(tab))
                break;
        }
    }

    private async void RevealEditorTabInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;
        await OpenPathInSystemExplorer(tab.Path, selectItem: true);
    }

    private void EditorTabContextMenu_OnOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu contextMenu ||
            contextMenu.DataContext is not EditorTab pivotTab)
            return;

        var placementTarget = contextMenu.PlacementTarget as Control;
        var contextControls = placementTarget is null
            ? Enumerable.Empty<Control>()
            : placementTarget.GetVisualAncestors().OfType<Control>().Prepend(placementTarget);
        var isSecondaryPaneContext = contextControls.Any(control => ReferenceEquals(control, SecondaryEditorPane));
        var isPrimaryPaneContext = contextControls.Any(control => ReferenceEquals(control, PrimaryEditorPane));
        if (isSecondaryPaneContext)
            ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        else if (isPrimaryPaneContext)
            ActivateEditorPane(_editorPane, focusEditor: false);

        var paneContext = IsSplitScreenVisible && (isPrimaryPaneContext || isSecondaryPaneContext);
        var contextTabs = paneContext
            ? (isSecondaryPaneContext ? _secondaryEditorPane.Tabs : _editorPane.Tabs)
            : OpenTabs;
        var pivotIndex = contextTabs.IndexOf(pivotTab);
        var hasTabsToRight = pivotIndex >= 0 && pivotIndex < contextTabs.Count - 1;
        var hasOtherTabs = contextTabs.Count > 1;

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
        {
            switch (item.Header?.ToString())
            {
                case "Close Other Files":
                case "Close Others":
                    item.IsVisible = hasOtherTabs;
                    break;
                case "Close Files Below":
                case "Close Tabs to the Right":
                    item.IsVisible = hasTabsToRight;
                    break;
                case "Rename...":
                    item.IsVisible = !pivotTab.IsUntitled;
                    break;
            }
        }
    }

    private void OpenEditorTabInTerminalMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;

        var directory = Path.GetDirectoryName(tab.Path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        CreateTerminalSession(workingDirectoryOverride: directory);
    }

    private async Task CreateExplorerEntryAsync(string directory, string baseName, string? ext, bool isFile)
    {
        try
        {
            var path = CreateUniqueChildPath(directory, baseName, ext ?? string.Empty);

            if (isFile) await File.WriteAllTextAsync(path, string.Empty); else Directory.CreateDirectory(path);
            await RefreshExplorerTreeAsync();
            if (isFile)
            {
                var newItem = FileTreeItems.FirstOrDefault(item =>
                    FileSystemPaths.Equals(item.FullPath, path));
                if (newItem is not null)
                {
                    _newFileInlineRenameItem = newItem;
                    BeginInlineRename(newItem);
                }
            }
        }
        catch (Exception ex)
        {
            var op = isFile ? "file" : "folder";
            ExtensionsStatusText = $"New {op} failed: {ex.Message}";
            await ShowWarningDialogAsync($"New {op} in explorer", ex);
        }
    }
    private async void NewFileInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        await CreateExplorerEntryAsync(GetExplorerTargetDirectory(item), "new-file", ".txt", true);
    }
    private async void ExplorerHeaderNewFileButton_OnClick(object? sender, RoutedEventArgs e) => await CreateExplorerEntryAsync(GetExplorerRootDirectory(), "new-file", ".txt", true);
    private async void NewFolderInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        await CreateExplorerEntryAsync(GetExplorerTargetDirectory(item), "New Folder", null, false);
    }
    private async void ExplorerHeaderNewFolderButton_OnClick(object? sender, RoutedEventArgs e) => await CreateExplorerEntryAsync(GetExplorerRootDirectory(), "New Folder", null, false);

    private async void OpenExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        if (item.IsDirectory)
            await ToggleDirectoryExpansionAsync(item);
        else
            await OpenFileFromPathAsync(item.FullPath);
    }

    private async void OpenExplorerItemToSideMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { IsDirectory: false } item)
            return;

        var sourcePane = _activeEditorPane;
        if (!IsSplitScreenVisible)
            ShowSplitEditor();

        if (!IsSplitScreenVisible)
            return;

        var destinationPane = ReferenceEquals(sourcePane, _editorPane)
            ? _secondaryEditorPane
            : _editorPane;
        var existingTab = OpenTabs.FirstOrDefault(tab => !tab.IsUntitled && FileSystemPaths.Equals(tab.Path, item.FullPath));
        if (existingTab is not null && !ReferenceEquals(sourcePane, destinationPane) && sourcePane.Tabs.Contains(existingTab))
        {
            OpenEditorTabToSide(existingTab, sourcePane, moveFromSourcePane: true);
            return;
        }
        await OpenFileFromPathAsync(item.FullPath, destinationPane);
    }

    private async void RevealExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        await OpenPathInSystemExplorer(item.FullPath, selectItem: !item.IsDirectory);
    }

    private void OpenExplorerItemInTerminalMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        var directory = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        CreateTerminalSession(workingDirectoryOverride: directory);
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

    private async void DuplicateExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var sourcePath = item.FullPath;
            var duplicatePath = CreateUniqueSiblingPath(sourcePath, item.IsDirectory);
            if (item.IsDirectory)
                await Task.Run(() => CopyDirectoryRecursiveAsync(sourcePath, duplicatePath));
            else
                await Task.Run(() => File.Copy(sourcePath, duplicatePath, overwrite: false));

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
                    FileSystemPaths.Equals(tab.Path, item.FullPath) ||
                    (item.IsDirectory && FileSystemPaths.IsPrefixOf(tab.Path, item.FullPath))))
                .ToList();

            if (!await EnsureTabsReadyForDeletionAsync(matchingTabs))
                return;

            if (item.IsDirectory)
                await Task.Run(() => Directory.Delete(item.FullPath, recursive: true));
            else
                await Task.Run(() => File.Delete(item.FullPath));

            CloseTabsWithoutPrompt(matchingTabs);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Delete failed: {ex.Message}";
            await ShowWarningDialogAsync("Delete file", ex);
        }
    }

    private void SetClipboard(FileTreeItem item, bool isCut) { _clipboardItemPath = item.FullPath; _clipboardItemIsDirectory = item.IsDirectory; _clipboardIsCut = isCut; ExtensionsStatusText = $"{(isCut ? "Cut" : "Copied")}: {item.Name}"; }
    private void CutFileMenuItem_OnClick(object? sender, RoutedEventArgs e) { if (TryGetTaggedData<FileTreeItem>(sender) is { } item) SetClipboard(item, true); }
    private void CopyFileMenuItem_OnClick(object? sender, RoutedEventArgs e) { if (TryGetTaggedData<FileTreeItem>(sender) is { } item) SetClipboard(item, false); }

    private async void PasteFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_clipboardItemPath is null) return;
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } target) return;

        var destDir = target.IsDirectory ? target.FullPath : Path.GetDirectoryName(target.FullPath)!;
        var itemName = Path.GetFileName(_clipboardItemPath.TrimEnd(Path.DirectorySeparatorChar));
        var destPath = CreateUniqueSiblingPath(Path.Combine(destDir, itemName), _clipboardItemIsDirectory);

        if (_clipboardItemIsDirectory)
        {
            var normalizedSrc = Path.GetFullPath(_clipboardItemPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var normalizedDest = Path.GetFullPath(destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (FileSystemPaths.Equals(normalizedDest, normalizedSrc) ||
                FileSystemPaths.IsPrefixOf(normalizedDest, normalizedSrc))
            {
                ExtensionsStatusText = "Paste failed: cannot paste a folder inside itself.";
                await ShowWarningDialogAsync("Paste file", new InvalidOperationException("Cannot paste a folder into itself or its descendant."));
                return;
            }
        }

        try
        {
            if (_clipboardIsCut)
            {
                var cutSource = _clipboardItemPath;
                await Task.Run(() =>
                {
                    if (_clipboardItemIsDirectory)
                    {
                        Shared.MoveOrCopyDirectory(cutSource, destPath);
                    }
                    else
                    {
                        try { File.Move(cutSource, destPath); }
                        catch (IOException)
                        {
                            File.Copy(cutSource, destPath, overwrite: false);
                            File.Delete(cutSource);
                        }
                    }
                });

                RetargetTabPaths(_clipboardItemPath, destPath, _clipboardItemIsDirectory);
                _clipboardItemPath = null;
            }
            else
            {
                var copySource = _clipboardItemPath;
                if (_clipboardItemIsDirectory)
                    await Task.Run(() => CopyDirectoryRecursiveAsync(copySource, destPath));
                else
                    await Task.Run(() => File.Copy(copySource, destPath, overwrite: false));
            }

            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Paste failed: {ex.Message}";
            await ShowWarningDialogAsync("Paste file", ex);
        }
    }

    private async void ClearRecentFilesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasRecentFiles) return;

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
        NotifyRecentFilesChanged();
    }

    public string HomeQuickSearchPlaceholderText => IsFolderOpen ? "Jump to file..." : "Open a folder to search files...";

    private async void HomeQuickSearchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsFolderOpen)
            OpenSearchPanel(SearchMode.FileByName);
        else
            await OpenFolderAsync();
    }

    private bool _isHomeNewsTabActive = true;
    private int _activeTabVersion;

    public bool IsHomeUpdatesTabActive => !_isHomeNewsTabActive;
    public bool IsHomeNewsTabActive => _isHomeNewsTabActive;

    private void HomeUpdatesTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _isHomeNewsTabActive = false;
        RaiseMany(nameof(IsHomeUpdatesTabActive), nameof(IsHomeNewsTabActive));
    }

    private void HomeNewsTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _isHomeNewsTabActive = true;
        RaiseMany(nameof(IsHomeUpdatesTabActive), nameof(IsHomeNewsTabActive));
    }

}
