// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using Kodo.Models;
using Kodo.Editor;

namespace Kodo;

public partial class MainWindow
{
    private ITextSourceVersion? _lastHandledEditorTextVersion;
    private TextDocument? _lastHandledEditorTextDocument;

    private TextEditor EditorTextBox => ReferenceEquals(_activeEditorPane, _secondaryEditorPane)
        ? SecondaryEditorTextBox
        : PrimaryEditorTextBox;

    public bool IsSplitScreenVisible
    {
        get => _isSplitScreenVisible;
        private set
        {
            if (_isSplitScreenVisible == value) return;
            _isSplitScreenVisible = value;
            EditorWorkspaceGrid.ColumnDefinitions = value
                ? new ColumnDefinitions("*,5,*")
                : new ColumnDefinitions("*,0,0");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSharedEditorTabsVisible));
            OnPropertyChanged(nameof(SplitEditorButtonText));
            OnPropertyChanged(nameof(SecondaryPaneSelectedTab));
        }
    }

    public string SplitEditorButtonText => IsSplitScreenVisible ? "Close Split" : "Split Editor";

    public ObservableCollection<EditorTab> PrimaryPaneTabs => _editorPane.Tabs;

    public ObservableCollection<EditorTab> SecondaryPaneTabs => _secondaryEditorPane.Tabs;

    public string PrimaryPaneBreadcrumbText => IsStatusBarFilePathVisible ? GetPaneBreadcrumb(_editorPane.ActiveTab) : string.Empty;
    public string SecondaryPaneBreadcrumbText => IsStatusBarFilePathVisible ? GetPaneBreadcrumb(_secondaryEditorPane.ActiveTab) : string.Empty;

    public Bitmap? PrimaryPaneImagePreview => _editorPane.ImagePreview;
    public Bitmap? SecondaryPaneImagePreview => _secondaryEditorPane.ImagePreview;
    public bool IsPrimaryPaneImagePreviewVisible => IsDocumentViewVisible && PrimaryPaneImagePreview is not null;
    public bool IsSecondaryPaneImagePreviewVisible => IsDocumentViewVisible && SecondaryPaneImagePreview is not null;
    public bool IsPrimaryPaneCorruptedFileViewVisible => IsDocumentViewVisible && PrimaryPaneImagePreview is null &&
        _editorPane.ActiveTab is { } primaryTab && _corruptedTabs.Contains(primaryTab);
    public bool IsSecondaryPaneCorruptedFileViewVisible => IsDocumentViewVisible && SecondaryPaneImagePreview is null &&
        _secondaryEditorPane.ActiveTab is { } secondaryTab && _corruptedTabs.Contains(secondaryTab);
    public bool IsPrimaryPaneTextEditorVisible => IsDocumentViewVisible && PrimaryPaneImagePreview is null &&
        (_editorPane.ActiveTab is null || !_corruptedTabs.Contains(_editorPane.ActiveTab));
    public bool IsSecondaryPaneTextEditorVisible => IsDocumentViewVisible && SecondaryPaneImagePreview is null &&
        (_secondaryEditorPane.ActiveTab is null || !_corruptedTabs.Contains(_secondaryEditorPane.ActiveTab));
    public bool IsPrimaryPaneMinimapVisible => IsMinimapVisible && IsPrimaryPaneTextEditorVisible;
    public bool IsSecondaryPaneMinimapVisible => IsMinimapVisible && IsSecondaryPaneTextEditorVisible;
    public double PrimaryPaneImageZoomedWidth => PrimaryPaneImagePreview is { } primaryImage
        ? primaryImage.PixelSize.Width * (_editorPane.ActiveTab?.GetViewState(_editorPane.Id).ImageZoomLevel ?? 1.0)
        : 0;
    public double PrimaryPaneImageZoomedHeight => PrimaryPaneImagePreview is { } primaryImage
        ? primaryImage.PixelSize.Height * (_editorPane.ActiveTab?.GetViewState(_editorPane.Id).ImageZoomLevel ?? 1.0)
        : 0;
    public double SecondaryPaneImageZoomedWidth => SecondaryPaneImagePreview is { } secondaryImage
        ? secondaryImage.PixelSize.Width * (_secondaryEditorPane.ActiveTab?.GetViewState(_secondaryEditorPane.Id).ImageZoomLevel ?? 1.0)
        : 0;
    public double SecondaryPaneImageZoomedHeight => SecondaryPaneImagePreview is { } secondaryImage
        ? secondaryImage.PixelSize.Height * (_secondaryEditorPane.ActiveTab?.GetViewState(_secondaryEditorPane.Id).ImageZoomLevel ?? 1.0)
        : 0;
    public string PrimaryPaneImageZoomPercent => $"{(int)Math.Round((_editorPane.ActiveTab?.GetViewState(_editorPane.Id).ImageZoomLevel ?? 1.0) * 100)}%";
    public string SecondaryPaneImageZoomPercent => $"{(int)Math.Round((_secondaryEditorPane.ActiveTab?.GetViewState(_secondaryEditorPane.Id).ImageZoomLevel ?? 1.0) * 100)}%";

    private string GetPaneBreadcrumb(EditorTab? tab)
    {
        if (tab is null) return string.Empty;
        if (tab.IsUntitled || string.IsNullOrWhiteSpace(tab.Path)) return tab.DisplayName;
        var path = Path.GetFullPath(tab.Path);
        var pathRoot = Path.GetPathRoot(path) ?? string.Empty;
        var root = pathRoot.Length > 1
            ? pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : pathRoot;
        var relative = path[pathRoot.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return string.Join("  ›  ", new[] { root }.Concat(segments));
    }

    public bool CanToggleSplitEditor => IsSplitScreenVisible ||
        (IsDocumentViewVisible && ActiveEditorTab is { } tab && CanDisplayInEditorPane(tab));

    private EditorPaneState ResolveEditorPaneForOpen(EditorPaneState pane) =>
        ReferenceEquals(pane, _secondaryEditorPane) && !IsSplitScreenVisible ? _editorPane : pane;

    private TextEditor GetEditorForPane(EditorPaneState pane) =>
        ReferenceEquals(pane, _secondaryEditorPane) ? SecondaryEditorTextBox : PrimaryEditorTextBox;

    private ScrollViewer GetImageScrollViewerForPane(EditorPaneState pane) =>
        ReferenceEquals(pane, _secondaryEditorPane) ? SecondaryImageScrollViewer : ImageScrollViewer;

    private void RestorePaneImageScrollOffset(EditorPaneState pane, EditorTab? tab)
    {
        if (tab is null || !ReferenceEquals(pane.ImagePreviewTab, tab) || pane.ImagePreview is null)
            return;

        pane.PendingImageScrollRestoreTab = tab;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(pane.ActiveTab, tab))
            {


                if (ReferenceEquals(pane.PendingImageScrollRestoreTab, tab))
                    pane.PendingImageScrollRestoreTab = null;
                return;
            }
            if (!ReferenceEquals(pane.PendingImageScrollRestoreTab, tab))
                return;

            var state = tab.GetViewState(pane.Id);
            GetImageScrollViewerForPane(pane).Offset = new Vector(state.ImageScrollOffsetX, state.ImageScrollOffsetY);
            pane.PendingImageScrollRestoreTab = null;
        }, DispatcherPriority.Background);
    }

    private void ImageScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var pane = ReferenceEquals(sender, SecondaryImageScrollViewer) ? _secondaryEditorPane :
            ReferenceEquals(sender, ImageScrollViewer) ? _editorPane : null;
        if (pane?.ActiveTab is not { } tab || !ReferenceEquals(pane.ImagePreviewTab, tab) || pane.ImagePreview is null ||
            ReferenceEquals(pane.PendingImageScrollRestoreTab, tab))
            return;

        var state = tab.GetViewState(pane.Id);
        var offset = GetImageScrollViewerForPane(pane).Offset;
        if (Math.Abs(state.ImageScrollOffsetX - offset.X) > 0.5 || Math.Abs(state.ImageScrollOffsetY - offset.Y) > 0.5)
            state.HasImageViewState = true;
        state.ImageScrollOffsetX = offset.X;
        state.ImageScrollOffsetY = offset.Y;
    }

    private void RestorePaneScrollOffset(EditorPaneState pane, EditorTab tab, TextEditor editor, int? expectedActivationVersion = null)
    {
        var state = tab.GetViewState(pane.Id);
        Dispatcher.UIThread.Post(() =>
        {
            if ((expectedActivationVersion is { } version && _activeTabVersion != version) ||
                !ReferenceEquals(pane.ActiveTab, tab) || tab.Document is not { } document ||
                !ReferenceEquals(editor.Document, document))
                return;

            var scrollViewer = editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer is not null)
                scrollViewer.Offset = new Vector(state.ScrollOffsetX, state.ScrollOffsetY);
        }, DispatcherPriority.Background);
    }

    private bool CanDisplayInEditorPane(EditorTab tab) =>
        true;

    private void RefreshPanePreviewState(EditorPaneState pane)
    {
        var tab = pane.ActiveTab;
        var path = tab is null || tab.IsUntitled ? null : tab.Path;
        if (pane.ImagePreview is not null && ReferenceEquals(pane.ImagePreviewTab, tab) && FileSystemPaths.Equals(pane.ImagePreviewPath, path))
        {
            NotifyPanePreviewStateChanged(pane);
            return;
        }

        var previousPreview = pane.ImagePreview;
        pane.ImagePreviewTab = tab;
        pane.ImagePreviewPath = path;
        pane.ImagePreview = TryLoadImagePreview(path);
        BitmapLifetime.DisposeAfterLayout(previousPreview);
        NotifyPanePreviewStateChanged(pane);
    }

    private void ReleasePanePreviewState(EditorPaneState pane)
    {
        var previousPreview = pane.ImagePreview;
        pane.ImagePreview = null;
        pane.ImagePreviewTab = null;
        pane.ImagePreviewPath = null;
        BitmapLifetime.DisposeAfterLayout(previousPreview);
        NotifyPanePreviewStateChanged(pane);
    }

    private void NotifyPanePreviewStateChanged(EditorPaneState pane)
    {
        if (ReferenceEquals(pane, _secondaryEditorPane))
        {
            RaiseMany(nameof(SecondaryPaneImagePreview), nameof(IsSecondaryPaneImagePreviewVisible),
                nameof(IsSecondaryPaneCorruptedFileViewVisible), nameof(IsSecondaryPaneTextEditorVisible),
                nameof(IsSecondaryPaneMinimapVisible),
                nameof(SecondaryPaneImageZoomedWidth), nameof(SecondaryPaneImageZoomedHeight), nameof(SecondaryPaneImageZoomPercent));
        }
        else
        {
            RaiseMany(nameof(PrimaryPaneImagePreview), nameof(IsPrimaryPaneImagePreviewVisible),
                nameof(IsPrimaryPaneCorruptedFileViewVisible), nameof(IsPrimaryPaneTextEditorVisible),
                nameof(IsPrimaryPaneMinimapVisible),
                nameof(PrimaryPaneImageZoomedWidth), nameof(PrimaryPaneImageZoomedHeight), nameof(PrimaryPaneImageZoomPercent));
        }
    }

    public EditorTab? SecondaryPaneSelectedTab
    {
        get => _secondaryEditorPane.ActiveTab;
        set
        {
            if (value is null || ReferenceEquals(_secondaryEditorPane.ActiveTab, value) || !OpenTabs.Contains(value) || !CanDisplayInEditorPane(value))
                return;

            if (!IsSplitScreenVisible)
                ShowSplitEditor();
            ActivateEditorPane(_secondaryEditorPane);
            ActivateTab(value, focusEditor: true);
        }
    }

    private void ConfigureSecondaryEditorView()
    {
        var editor = SecondaryEditorTextBox;
        editor.LineNumbersMargin = new Thickness(8, 0, 8, 0);
        editor.FontFamily = KodoFonts.MonoFamily;
        editor.TextArea.TextView.Options.AllowScrollBelowDocument = false;
        AddEditorRenderers(editor.TextArea.TextView, secondaryPane: true);
        editor.TextArea.TextView.LinkTextForegroundBrush = SharedLinkTextBrush;
        editor.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;
        var defaultLinkGenerator = editor.TextArea.TextView.ElementGenerators
            .OfType<LinkElementGenerator>()
            .FirstOrDefault();
        if (defaultLinkGenerator is not null)
            editor.TextArea.TextView.ElementGenerators.Remove(defaultLinkGenerator);
        editor.TextArea.TextView.ElementGenerators.Add(new StrictLinkElementGenerator());
        _secondaryColorSwatchGenerator = new ColorSwatchElementGenerator
        {
            PickerTitle = IsAmericanEnglish ? "Color Picker" : "Colour Picker",
            EditColorTooltip = IsAmericanEnglish ? "Edit Color" : "Edit Colour"
        };
        editor.TextArea.TextView.ElementGenerators.Add(_secondaryColorSwatchGenerator);
        editor.TextArea.TextView.PointerMoved += EditorTextView_OnPointerMoved;
        editor.TextArea.TextView.PointerEntered += (_, _) => _diagnosticPopupHideTimer.Stop();
        editor.TextArea.TextView.PointerExited += EditorTextView_OnPointerExited;
        editor.TextArea.TextView.PointerPressed += (_, _) => HideDiagnosticPopup();
        editor.TextArea.TextView.PointerWheelChanged += EditorTextView_OnPointerWheelChanged;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) =>
        {
            HideDiagnosticPopup();
            UpdateEditorMinimapViewport(SecondaryEditorTextBox, SecondaryEditorMinimapView);
            QueueLspViewportRefresh();
        }, RoutingStrategies.Bubble);
        editor.TextChanged += EditorTextBox_OnPaneTextChanged;
        InitializeTabDragDrop();
    }

    private void EditorTextBox_OnPaneTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextEditor eventEditor ||
            (!ReferenceEquals(eventEditor, PrimaryEditorTextBox) && !ReferenceEquals(eventEditor, SecondaryEditorTextBox)))
            return;

        var editor = eventEditor;
        var otherEditor = ReferenceEquals(editor, PrimaryEditorTextBox)
            ? SecondaryEditorTextBox
            : PrimaryEditorTextBox;
        if (ReferenceEquals(otherEditor.Document, editor.Document) &&
            ReferenceEquals(EditorTextBox.Document, editor.Document))
            editor = EditorTextBox;

        var version = editor.Document.Version;
        if (ReferenceEquals(_lastHandledEditorTextDocument, editor.Document) &&
            ReferenceEquals(_lastHandledEditorTextVersion, version))
            return;
        _lastHandledEditorTextDocument = editor.Document;
        _lastHandledEditorTextVersion = version;
        if (ReferenceEquals(otherEditor.Document, editor.Document))
        {
            SavePaneViewState(_editorPane, PrimaryEditorTextBox);
            SavePaneViewState(_secondaryEditorPane, SecondaryEditorTextBox);
        }
        if (ReferenceEquals(EditorSession.Document, editor.Document))
            EditorSession.NotifyDocumentChanged();
        EditorTextBox_OnTextChanged(editor, e);
    }

    private void EditorCaretPositionChanged(TextEditor editor)
    {
        if (!ReferenceEquals(editor, EditorTextBox)) return;
        HideDiagnosticPopup();
        _lspPresentationCancellation?.Cancel();
        RestartLspPresentationDebounceTimer();
        if (!_pendingFullStateRefresh || !_editorStateRefreshTimer.IsEnabled)
            QueueRefreshState();
        if (_caretVisibilityRefreshPending && ReferenceEquals(_caretVisibilityRefreshEditor, editor))
            return;

        _caretVisibilityRefreshPending = true;
        _caretVisibilityRefreshEditor = editor;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_caretVisibilityRefreshEditor, editor))
            {
                _caretVisibilityRefreshPending = false;
                _caretVisibilityRefreshEditor = null;
            }
            if (!ReferenceEquals(editor, EditorTextBox)) return;
            var caret = editor.TextArea.Caret;
            try { caret.BringCaretToView(); } catch { }
            try
            {
                if (caret.Column > 8) return;
                var scrollViewer = editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scrollViewer is not null && scrollViewer.Offset.X > 10)
                    scrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
                else if (editor.TextArea.TextView.ScrollOffset.X > 10)
                    caret.BringCaretToView();
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    private void HandleEditorPaneDocumentChanged(EditorPaneState pane, TextEditor editor, TextDocument document)
    {
        if (ReferenceEquals(_hoveredEditorPane, pane))
            HideDiagnosticPopup();
        CancelLanguageHoverRequest();
        CancelLspHoverRequest();
        ClearPaneDiagnosticHighlights(editor, redraw: false);
        ClearLspPresentationForEditor(editor, redraw: false);
        (ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryFindHighlightRenderer
            : _primaryFindHighlightRenderer).Clear();
        editor.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
        editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        var oldManager = ReferenceEquals(pane, _secondaryEditorPane)
            ? _secondaryLspFoldingManager
            : _primaryLspFoldingManager;
        if (oldManager is not null)
            FoldingManager.Uninstall(oldManager);
        var newManager = FoldingManager.Install(editor.TextArea);
        if (ReferenceEquals(pane, _secondaryEditorPane))
            _secondaryLspFoldingManager = newManager;
        else
            _primaryLspFoldingManager = newManager;
        ApplyFoldingMarginTheme();
        var paneInlayHintRenderer = ReferenceEquals(pane, _secondaryEditorPane)
            ? _secondaryLspInlayHintRenderer
            : _primaryLspInlayHintRenderer;
        paneInlayHintRenderer.SetDocument(document);
        InvalidateLspPanePresentation(pane);

        ScheduleEditorMinimapRefresh();
        if (!ReferenceEquals(_activeEditorPane, pane) || ReferenceEquals(EditorSession.Document, document))
            return;

        var filePath = EditorSession.FilePath;
        EditorSession.Dispose();
        _editorSession = new EditorDocumentSession(document);
        EditorSession.SetFilePath(filePath);
        TrackLspDocument(document);
        _foldingMargin = null;
        ResetLspFoldingState();
    }

    private void ActivateEditorPane(EditorPaneState pane, bool focusEditor = true)
    {
        if (ReferenceEquals(_activeEditorPane, pane))
        {
            if (focusEditor) EditorTextBox.Focus();
            return;
        }

        _lspCompletionCts?.Cancel();
        _lspPresentationCancellation?.Cancel();
        CloseCompletionWindow();
        ClearLspSignatureHelpTooltip();
        HideDiagnosticPopup();
        CancelLanguageHoverRequest();
        CancelLspHoverRequest();
        ClosePendingAutoCloseUndoGroup();
        SaveCurrentEditorStateIntoTab();
        _activeEditorPane = pane;
        _editorHorizontalScrollViewerResolved = false;
        _editorHorizontalScrollViewer = null;
        var nextTab = pane.ActiveTab;
        if (nextTab is not null)
            CancelInactiveTabDiagnosticScan(nextTab);
        RefreshPanePreviewState(pane);
        RestorePaneImageScrollOffset(pane, pane.ActiveTab);


        if (nextTab is not null)
            nextTab.IsSelected = true;

        if (nextTab is not null)
        {
            var paneEditor = EditorTextBox;
            _currentFilePath = nextTab.IsUntitled ? null : nextTab.Path;
            _hasUntitledDocument = nextTab.IsUntitled;
            _isDirty = nextTab.IsDirty;
            _currentLineEnding = nextTab.LineEnding;
            _currentFileEncoding = nextTab.Encoding;



            SetFileCorrupted(_corruptedTabs.Contains(nextTab));
            if (!nextTab.IsUntitled && !IsImagePreviewFile(nextTab.Path))
                ApplyIndentationForContent(nextTab.DetectedIndentation);
            SetEditorSessionDocument(EditorTextBox.Document, _currentFilePath);
            var viewState = nextTab.GetViewState(pane.Id);
            ImageZoomLevel = viewState.ImageZoomLevel;
            EditorTextBox.TextArea.Caret.Offset = Math.Clamp(viewState.CaretOffset, 0, EditorTextBox.Document.TextLength);
            EditorTextBox.ScrollToLine(Math.Clamp(viewState.TopLineNumber, 1, EditorTextBox.Document.LineCount));
            RestorePaneScrollOffset(pane, nextTab, paneEditor);
        }
        else
        {
            _currentFilePath = null;
            _hasUntitledDocument = false;
            _isDirty = false;
            ImageZoomLevel = 1.0;
            SetEditorSessionDocument(EditorTextBox.Document, null);
            CurrentImagePreview = null;
            SetFileCorrupted(false);
            CurrentLanguageExtension = null;
        }

        _lspFoldingManager ??= FoldingManager.Install(EditorTextBox.TextArea);
        UpdateFindHighlights();
        RefreshStatusBarDiagnostics();
        UpdateEditorMinimapViewport(PrimaryEditorTextBox, EditorMinimapView);
        UpdateEditorMinimapViewport(SecondaryEditorTextBox, SecondaryEditorMinimapView);
        _foldingMargin = null;
        OnPropertyChanged(nameof(ActiveEditorTab));
        OnPropertyChanged(nameof(SecondaryPaneSelectedTab));
        OnPropertyChanged(nameof(PrimaryPaneBreadcrumbText));
        OnPropertyChanged(nameof(SecondaryPaneBreadcrumbText));
        OnPropertyChanged(nameof(HasOpenEditors));
        OnPropertyChanged(nameof(IsDocumentDirty));
        ResetLspFoldingState();
        UpdateCurrentDocumentPresentation();
        RaiseMany(nameof(FileSummaryText), nameof(FilePathText), nameof(LanguageDisplayText), nameof(IsLineEndingVisible), nameof(LineEndingDisplayText), nameof(IndentationDisplayText), nameof(IsIndentationVisible), nameof(CanShowSaveActions));
        RefreshState(fullRefresh: true);
        QueueLspPresentationRefresh();
        QueueInsightRefresh();
        QueueLspViewportRefresh();
        if (focusEditor) EditorTextBox.Focus();
    }

    private void SetEditorSessionDocument(TextDocument document, string? path)
    {
        if (!ReferenceEquals(EditorSession.Document, document))
        {
            EditorSession.Dispose();
            _editorSession = new EditorDocumentSession(document);
        }
        EditorSession.SetFilePath(path);
        TrackLspDocument(document);
        _lspInlayHintRenderer.SetDocument(document);
    }

    private void AddEditorRenderers(TextView textView, bool secondaryPane = false)
    {
        var indentGuide = secondaryPane ? _secondaryIndentGuideRenderer : _primaryIndentGuideRenderer;
        var deadCode = secondaryPane ? _secondaryDeadCodeHighlightRenderer : _primaryDeadCodeHighlightRenderer;
        var errors = secondaryPane ? _secondaryErrorHighlightRenderer : _primaryErrorHighlightRenderer;
        var find = secondaryPane ? _secondaryFindHighlightRenderer : _primaryFindHighlightRenderer;
        var lspHighlight = secondaryPane ? _secondaryLspHighlightRenderer : _primaryLspHighlightRenderer;
        var semanticTokens = secondaryPane ? _secondaryLspSemanticTokenRenderer : _primaryLspSemanticTokenRenderer;
        var inlayHints = secondaryPane ? _secondaryLspInlayHintRenderer : _primaryLspInlayHintRenderer;
        var rainbow = secondaryPane ? _secondaryRainbowBracketColorizer : _primaryRainbowBracketColorizer;
        var interpolated = secondaryPane ? _secondaryInterpolatedStringColorizer : _primaryInterpolatedStringColorizer;
        var html = secondaryPane ? _secondaryHtmlEmbeddedColorizer : _primaryHtmlEmbeddedColorizer;
        var markdown = secondaryPane ? _secondaryMarkdownColorizer : _primaryMarkdownColorizer;
        var emoji = secondaryPane ? _secondaryEmojiTypefaceColorizer : _primaryEmojiTypefaceColorizer;
        var deadCodeBrightener = secondaryPane ? _secondaryDeadCodeTextBrightener : _primaryDeadCodeTextBrightener;
        var errorDarkener = secondaryPane ? _secondaryErrorTextDarkener : _primaryErrorTextDarkener;

        if (!textView.BackgroundRenderers.Contains(indentGuide)) textView.BackgroundRenderers.Add(indentGuide);
        if (!textView.BackgroundRenderers.Contains(deadCode)) textView.BackgroundRenderers.Add(deadCode);
        if (!textView.BackgroundRenderers.Contains(errors)) textView.BackgroundRenderers.Add(errors);
        if (!textView.BackgroundRenderers.Contains(find)) textView.BackgroundRenderers.Add(find);
        if (!textView.BackgroundRenderers.Contains(lspHighlight)) textView.BackgroundRenderers.Add(lspHighlight);
        if (!textView.BackgroundRenderers.Contains(semanticTokens)) textView.BackgroundRenderers.Add(semanticTokens);
        if (!textView.ElementGenerators.Contains(inlayHints)) textView.ElementGenerators.Add(inlayHints);
        if (!textView.LineTransformers.Contains(rainbow)) textView.LineTransformers.Add(rainbow);
        if (!textView.LineTransformers.Contains(interpolated)) textView.LineTransformers.Add(interpolated);
        if (!textView.LineTransformers.Contains(html)) textView.LineTransformers.Add(html);
        if (!textView.LineTransformers.Contains(markdown)) textView.LineTransformers.Add(markdown);
        if (!textView.LineTransformers.Contains(emoji)) textView.LineTransformers.Add(emoji);
        if (!textView.LineTransformers.Contains(deadCodeBrightener)) textView.LineTransformers.Add(deadCodeBrightener);
        if (!textView.LineTransformers.Contains(errorDarkener)) textView.LineTransformers.Add(errorDarkener);
    }

    private void RefreshSyntaxHighlightingForTabInPanes(EditorTab tab)
    {
        if (ReferenceEquals(_editorPane.ActiveTab, tab))
            ApplyPaneSyntaxHighlighting(_editorPane, PrimaryEditorTextBox, tab);
        if (ReferenceEquals(_secondaryEditorPane.ActiveTab, tab))
            ApplyPaneSyntaxHighlighting(_secondaryEditorPane, SecondaryEditorTextBox, tab);
    }

    private void RefreshPaneTabSelection()
    {
        foreach (var tab in OpenTabs)
        {
            tab.IsPrimaryPaneSelected = ReferenceEquals(tab, _editorPane.ActiveTab);
            tab.IsSecondaryPaneSelected = ReferenceEquals(tab, _secondaryEditorPane.ActiveTab);
        }
    }

    private void AddTabToPane(EditorPaneState pane, EditorTab tab)
    {
        if (pane.AddTab(tab) && IsSplitScreenVisible)
            SaveSettings();
    }

    private void MoveTabToActivePane(EditorTab tab, bool preserveOtherPaneMembership)
    {
        if (!IsSplitScreenVisible || preserveOtherPaneMembership)
            return;

        var pane = _activeEditorPane;
        var otherPane = ReferenceEquals(pane, _editorPane) ? _secondaryEditorPane : _editorPane;
        if (pane.Tabs.Contains(tab))
            return;

        var otherIndex = otherPane.Tabs.IndexOf(tab);
        if (otherIndex < 0)
            return;

        if (!EditorPaneState.MoveTabMembership(otherPane, pane, tab))
            return;
        if (ReferenceEquals(otherPane.ActiveTab, tab))
        {
            var replacementIndex = Math.Min(otherIndex, otherPane.Tabs.Count - 1);
            var replacementTab = otherPane.Tabs.Count > 0 ? otherPane.Tabs[replacementIndex] : null;
            SetOtherPaneTab(otherPane, replacementTab);
        }

        SaveSettings();
    }

    private void AssignUnownedOpenTabsToPane(EditorPaneState pane)
    {
        foreach (var tab in OpenTabs)
        {
            if (!_editorPane.Tabs.Contains(tab) && !_secondaryEditorPane.Tabs.Contains(tab))
                AddTabToPane(pane, tab);
        }
    }

    private void RemoveTabFromPanes(EditorTab tab)
    {
        var removed = _editorPane.Tabs.Remove(tab);
        removed |= _secondaryEditorPane.Tabs.Remove(tab);
        if (removed && IsSplitScreenVisible)
            SaveSettings();
    }

    private async Task<bool> RequestClosePaneTabAsync(EditorPaneState pane, EditorTab tab)
    {
        if (!pane.Tabs.Contains(tab))
            return false;

        var otherPane = ReferenceEquals(pane, _editorPane) ? _secondaryEditorPane : _editorPane;
        if (!IsSplitScreenVisible || !otherPane.Tabs.Contains(tab))
            return await RequestCloseTabAsync(tab);

        var tabIndex = pane.Tabs.IndexOf(tab);
        pane.Tabs.Remove(tab);
        SaveSettings();
        if (!ReferenceEquals(pane.ActiveTab, tab))
            return true;

        var replacementIndex = Math.Min(Math.Max(0, tabIndex), pane.Tabs.Count - 1);
        var replacementTab = pane.Tabs.Count > 0 ? pane.Tabs[replacementIndex] : null;
        if (ReferenceEquals(pane, _activeEditorPane))
        {
            if (replacementTab is not null)
                ActivateTab(replacementTab, focusEditor: false);
            else
                ClearActivePaneAfterLastTabClosed();
        }
        else
        {
            SetOtherPaneTab(pane, replacementTab);
        }
        CollapseEmptyEditorPanes();
        return true;
    }

    private async void ClosePaneTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab || sender is not Control control)
            return;
        var pane = control.GetVisualAncestors().OfType<Control>().Any(ancestor => ReferenceEquals(ancestor, SecondaryEditorPane))
            ? _secondaryEditorPane
            : _editorPane;
        await RequestClosePaneTabAsync(pane, tab);
    }

    private async void ClosePaneTabMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            await RequestClosePaneTabAsync(_activeEditorPane, tab);
    }

    private async void CloseOtherPaneTabsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: EditorTab pivotTab }) return;
        var pane = _activeEditorPane;
        foreach (var tab in pane.Tabs.Where(tab => !ReferenceEquals(tab, pivotTab)).ToArray())
        {
            if (!await RequestClosePaneTabAsync(pane, tab))
                break;
        }
    }

    private async void ClosePaneTabsToRightMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: EditorTab pivotTab }) return;
        var pane = _activeEditorPane;
        var pivotIndex = pane.Tabs.IndexOf(pivotTab);
        if (pivotIndex < 0) return;
        foreach (var tab in pane.Tabs.Skip(pivotIndex + 1).ToArray())
        {
            if (!await RequestClosePaneTabAsync(pane, tab))
                break;
        }
    }

    private async void CloseAllPaneTabsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        var pane = _activeEditorPane;
        foreach (var tab in pane.Tabs.ToArray())
        {
            if (!await RequestClosePaneTabAsync(pane, tab))
                break;
        }
    }

    private void ClearAllPaneDiagnosticHighlights()
    {
        _deadCodeAppliedSignature = -1;
        _errorHighlightAppliedSignature = -1;
        var emptyDead = Array.Empty<DeadCodeSpan>();
        var emptyErrors = Array.Empty<ErrorSpan>();
        _primaryDeadCodeHighlightRenderer.SetSpans(emptyDead);
        _secondaryDeadCodeHighlightRenderer.SetSpans(emptyDead);
        _primaryDeadCodeTextBrightener.SetSpans(emptyDead);
        _secondaryDeadCodeTextBrightener.SetSpans(emptyDead);
        _primaryErrorHighlightRenderer.SetSpans(emptyErrors);
        _secondaryErrorHighlightRenderer.SetSpans(emptyErrors);
        _primaryErrorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _secondaryErrorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _primaryErrorTextDarkener.SetSpans(emptyErrors, emptyDead);
        _secondaryErrorTextDarkener.SetSpans(emptyErrors, emptyDead);
        RedrawAllEditorPaneHighlights();
        RefreshStatusBarDiagnostics();
    }

    private void ClearAllPaneDeadCodeHighlights()
    {
        _deadCodeAppliedSignature = -1;
        var emptyDead = Array.Empty<DeadCodeSpan>();
        _primaryDeadCodeHighlightRenderer.SetSpans(emptyDead);
        _secondaryDeadCodeHighlightRenderer.SetSpans(emptyDead);
        _primaryDeadCodeTextBrightener.SetSpans(emptyDead);
        _secondaryDeadCodeTextBrightener.SetSpans(emptyDead);
        var emptyErrors = Array.Empty<ErrorSpan>();
        _primaryErrorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _secondaryErrorHighlightRenderer.SetDeadCodeSpans(emptyDead);
        _primaryErrorTextDarkener.SetSpans(_primaryErrorHighlightRenderer.Spans, _primaryErrorHighlightRenderer.SpanIndex, emptyDead, SpanOverlapIndex.Empty);
        _secondaryErrorTextDarkener.SetSpans(_secondaryErrorHighlightRenderer.Spans, _secondaryErrorHighlightRenderer.SpanIndex, emptyDead, SpanOverlapIndex.Empty);
        RedrawAllEditorPaneHighlights();
        RefreshStatusBarDiagnostics();
    }

    private void ClearAllPaneErrorHighlights()
    {
        _errorHighlightAppliedSignature = -1;
        var emptyErrors = Array.Empty<ErrorSpan>();
        _primaryErrorHighlightRenderer.SetSpans(emptyErrors);
        _secondaryErrorHighlightRenderer.SetSpans(emptyErrors);
        var primaryDead = _primaryDeadCodeHighlightRenderer.Spans;
        var secondaryDead = _secondaryDeadCodeHighlightRenderer.Spans;
        _primaryErrorHighlightRenderer.SetDeadCodeSpans(primaryDead, _primaryDeadCodeHighlightRenderer.SpanIndex);
        _secondaryErrorHighlightRenderer.SetDeadCodeSpans(secondaryDead, _secondaryDeadCodeHighlightRenderer.SpanIndex);
        _primaryErrorTextDarkener.SetSpans(emptyErrors, SpanOverlapIndex.Empty, primaryDead, _primaryDeadCodeHighlightRenderer.SpanIndex);
        _secondaryErrorTextDarkener.SetSpans(emptyErrors, SpanOverlapIndex.Empty, secondaryDead, _secondaryDeadCodeHighlightRenderer.SpanIndex);
        RedrawAllEditorPaneHighlights();
        RefreshStatusBarDiagnostics();
    }

    private void RedrawAllEditorPaneHighlights()
    {
        PrimaryEditorTextBox?.TextArea?.TextView?.Redraw();
        SecondaryEditorTextBox?.TextArea?.TextView?.Redraw();
        PrimaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
        SecondaryEditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
    }

    private void ClearPaneDiagnosticHighlights(TextEditor editor, bool redraw = true)
    {
        var secondary = ReferenceEquals(editor, SecondaryEditorTextBox);
        var deadRenderer = secondary ? _secondaryDeadCodeHighlightRenderer : _primaryDeadCodeHighlightRenderer;
        var deadTransformer = secondary ? _secondaryDeadCodeTextBrightener : _primaryDeadCodeTextBrightener;
        var errorRenderer = secondary ? _secondaryErrorHighlightRenderer : _primaryErrorHighlightRenderer;
        var errorTransformer = secondary ? _secondaryErrorTextDarkener : _primaryErrorTextDarkener;
        var emptyDead = Array.Empty<DeadCodeSpan>();
        var emptyErrors = Array.Empty<ErrorSpan>();
        deadRenderer.SetSpans(emptyDead);
        deadTransformer.SetSpans(emptyDead);
        errorRenderer.SetSpans(emptyErrors);
        errorRenderer.SetDeadCodeSpans(emptyDead);
        errorTransformer.SetSpans(emptyErrors, emptyDead);
        _deadCodeAppliedSignature = -1;
        _errorHighlightAppliedSignature = -1;
        if (redraw)
        {
            editor?.TextArea?.TextView?.Redraw();
            editor?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
        }
        if (ReferenceEquals(editor, EditorTextBox))
            RefreshStatusBarDiagnostics();
    }

    private void ClearLspInlayHintsForEditor(TextEditor editor)
    {
        var renderer = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryLspInlayHintRenderer
            : _primaryLspInlayHintRenderer;
        renderer.ClearHints();
        editor?.TextArea?.TextView?.Redraw();
    }

    private void ClearLspPresentationForEditor(TextEditor editor, bool redraw = true)
    {
        var secondary = ReferenceEquals(editor, SecondaryEditorTextBox);
        (secondary ? _secondaryLspInlayHintRenderer : _primaryLspInlayHintRenderer)
            .ClearHints();
        (secondary ? _secondaryLspSemanticTokenRenderer : _primaryLspSemanticTokenRenderer)
            .SetTokens(Array.Empty<(int, int, IBrush)>());
        (secondary ? _secondaryLspHighlightRenderer : _primaryLspHighlightRenderer).Clear();
        if (redraw)
        {
            editor?.TextArea?.TextView?.Redraw();
            editor?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
        }
    }

    private void ResetPaneLspPresentation(EditorPaneState pane)
    {
        ClearLspPresentationForEditor(GetEditorForPane(pane));
        GetLspFoldingManagerForPane(pane)?.UpdateFoldings(Array.Empty<NewFolding>(), 0);
        InvalidateLspPanePresentation(pane);
    }

    private void ShowSplitEditor(EditorTab? secondaryTabOverride = null, bool useSecondaryTabOverride = false)
    {
        if (IsSplitScreenVisible || EditorTextBox.Document is null)
            return;

        if (ActiveEditorTab is not null)
            SaveCurrentEditorStateIntoTab();
        var primaryTab = ActiveEditorTab;
        AssignUnownedOpenTabsToPane(_editorPane);
        if (primaryTab is not null)
            AddTabToPane(_editorPane, primaryTab);
        var secondaryTab = useSecondaryTabOverride
            ? secondaryTabOverride
            : _secondaryEditorPane.ActiveTab is { } previousSideTab &&
            OpenTabs.Contains(previousSideTab) && CanDisplayInEditorPane(previousSideTab)
            ? previousSideTab
            : primaryTab;
        if (useSecondaryTabOverride && secondaryTab is not null && !ReferenceEquals(secondaryTab, primaryTab))
            _editorPane.Tabs.Remove(secondaryTab);
        _secondaryEditorPane.ActiveTab = secondaryTab;
        if (secondaryTab is not null)
            AddTabToPane(_secondaryEditorPane, secondaryTab);
        RefreshPanePreviewState(_secondaryEditorPane);
        if (secondaryTab is not null)
            secondaryTab.IsSelected = true;
        RefreshPaneTabSelection();
        if (secondaryTab is null)
        {
            AssignPaneDocumentWithoutDirtyTracking(SecondaryEditorTextBox, new TextDocument(string.Empty));
            ApplyPaneSyntaxHighlighting(_secondaryEditorPane, SecondaryEditorTextBox, null);
        }
        else if (secondaryTab.Document is null)
        {
            secondaryTab.Document = new TextDocument(secondaryTab.Content);
            secondaryTab.Content = string.Empty;
        }
        if (secondaryTab is not null)
        {
            var secondaryPaneDocument = secondaryTab.Document ?? new TextDocument(secondaryTab.Content ?? string.Empty);
            AssignPaneDocumentWithoutDirtyTracking(SecondaryEditorTextBox, secondaryPaneDocument);
            ApplyPaneSyntaxHighlighting(_secondaryEditorPane, SecondaryEditorTextBox, secondaryTab);
        }
        if (secondaryTab is not null && ReferenceEquals(PrimaryEditorTextBox.Document, SecondaryEditorTextBox.Document))
            CopyDiagnosticHighlights(PrimaryEditorTextBox, SecondaryEditorTextBox);
        IsSplitScreenVisible = true;
        SaveSettings();
        RestorePaneImageScrollOffset(_secondaryEditorPane, secondaryTab);
        if (IsMinimapVisible)
            SecondaryEditorMinimapView?.SetDocument(SecondaryEditorTextBox.Document);
        ScheduleEditorMinimapRefresh();
        if (secondaryTab is not null)
        {
            var state = secondaryTab.GetViewState(_secondaryEditorPane.Id);
            var secondaryDocument = SecondaryEditorTextBox.Document;
            if (secondaryDocument is not null && SecondaryEditorTextBox.TextArea?.Caret is { } caret)
            {
                caret.Offset = Math.Clamp(state.CaretOffset, 0, secondaryDocument.TextLength);
                SecondaryEditorTextBox.ScrollToLine(Math.Clamp(state.TopLineNumber, 1, secondaryDocument.LineCount));
                RestorePaneScrollOffset(_secondaryEditorPane, secondaryTab, SecondaryEditorTextBox);
            }
        }
        UpdateEditorMinimapViewport(SecondaryEditorTextBox, SecondaryEditorMinimapView);
        OnPropertyChanged(nameof(SecondaryPaneSelectedTab));
        OnPropertyChanged(nameof(PrimaryPaneBreadcrumbText));
        OnPropertyChanged(nameof(SecondaryPaneBreadcrumbText));
        OnPropertyChanged(nameof(IsSplitScreenVisible));
        QueueLspPresentationRefresh();
        if (secondaryTab is { IsUntitled: false } && !string.IsNullOrWhiteSpace(secondaryTab.Path) &&
            !ReferenceEquals(secondaryTab, ActiveEditorTab))
            UpdateInactiveTabDiagnosticsForFile(secondaryTab.Path);
    }

    private async Task RestoreStartupSplitEditorAsync()
    {
        if (!IsRestoreOpenTabsOnLaunchEnabled || !_startupSplitEditorVisible || OpenTabs.Count == 0)
            return;

        RestorePaneTabMembership(_editorPane, OpenTabs, _startupPrimaryPaneTabPaths);
        RestorePaneTabMembership(_secondaryEditorPane, OpenTabs, _startupSecondaryPaneTabPaths);


        foreach (var tab in _secondaryEditorPane.Tabs.Where(_editorPane.Tabs.Contains).ToArray())
            _secondaryEditorPane.Tabs.Remove(tab);

        EditorTab? FindTab(string? path) => string.IsNullOrWhiteSpace(path)
            ? null
            : OpenTabs.FirstOrDefault(tab => !tab.IsUntitled && FileSystemPaths.Equals(tab.Path, path));

        var activeTab = FindTab(_startupActiveTabPath);
        var primaryTab = FindTab(_startupPrimaryPaneTabPath) ??
            (!_startupActivePaneIsSecondary ? activeTab : null) ??
            OpenTabs.FirstOrDefault();
        var secondaryTab = FindTab(_startupSecondaryPaneTabPath) ??
            (_startupActivePaneIsSecondary ? activeTab : null) ??
            OpenTabs.FirstOrDefault(tab => !ReferenceEquals(tab, primaryTab));

        if (primaryTab is not null)
        {
            ActivateEditorPane(_editorPane, focusEditor: false);
            if (!ReferenceEquals(_editorPane.ActiveTab, primaryTab))
                ActivateTab(primaryTab, focusEditor: false, scheduleContentLoad: false);
            var activationVersion = _activeTabVersion;
            var primaryNeedsContentRestore = !IsImagePreviewFile(primaryTab.Path) &&
                (primaryTab.Document is null || !ReferenceEquals(PrimaryEditorTextBox.Document, primaryTab.Document));
            if (primaryNeedsContentRestore)
                await SetEditorTabContentSafelyAsync(primaryTab, activationVersion, _editorPane);
            await EnsurePaneDocumentHasContentAsync(_editorPane, primaryTab);
        }



        ShowSplitEditor(secondaryTab, useSecondaryTabOverride: true);
        if (secondaryTab is not null && !ReferenceEquals(_secondaryEditorPane.ActiveTab, secondaryTab))
            SetOtherPaneTab(_secondaryEditorPane, secondaryTab);
        if (secondaryTab is not null)
            await EnsurePaneDocumentHasContentAsync(_secondaryEditorPane, secondaryTab);

        if (_startupActivePaneIsSecondary && secondaryTab is not null)
            ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        else
            ActivateEditorPane(_editorPane, focusEditor: false);
    }

    internal static void RestorePaneTabMembership(EditorPaneState pane, IEnumerable<EditorTab> openTabs, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var tab = openTabs.FirstOrDefault(candidate => !candidate.IsUntitled &&
                !string.IsNullOrWhiteSpace(candidate.Path) && FileSystemPaths.Equals(candidate.Path, path));
            if (tab is not null)
                pane.AddTab(tab);
        }
    }

    private async Task EnsurePaneDocumentHasContentAsync(EditorPaneState pane, EditorTab tab)
    {
        var editor = GetEditorForPane(pane);
        var captured = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(pane.ActiveTab, tab) || !IsEffectivelyEmptyRestoredDocument(editor.Document))
                return (isEmptyActiveDocument: false, document: editor.Document, documentVersion: editor.Document.Version, bufferedContent: string.Empty);
            return (isEmptyActiveDocument: true, document: editor.Document, documentVersion: editor.Document.Version, bufferedContent: tab.Content ?? string.Empty);
        });
        if (!captured.isEmptyActiveDocument || tab.IsUntitled ||
            _corruptedTabs.Contains(tab) || IsImagePreviewFile(tab.Path))
            return;

        var content = captured.bufferedContent;
        if (content.Length == 0)
        {
            if (tab.IsDirty) return;
            try
            {
                var fileLength = await Task.Run(() => File.Exists(tab.Path) ? new FileInfo(tab.Path).Length : 0L).ConfigureAwait(false);
                if (fileLength == 0) return;
                content = fileLength > MaxFileSizeForFullLoad
                    ? await ReadLargeFileAsync(tab.Path, tab.Encoding).ConfigureAwait(false)
                    : await File.ReadAllTextAsync(tab.Path, tab.Encoding).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"Unable to recover restored editor content for '{tab.Path}'", ex);
                return;
            }
        }

        if (content.Length == 0) return;
        var restored = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(pane.ActiveTab, tab) ||
                !ReferenceEquals(editor.Document, captured.document) ||
                !ReferenceEquals(editor.Document.Version, captured.documentVersion) ||
                !IsEffectivelyEmptyRestoredDocument(editor.Document))
                return false;

            var document = new TextDocument(content);
            tab.Document = document;
            tab.Content = string.Empty;
            AssignPaneDocumentWithoutDirtyTracking(editor, document);
            if (IsSplitScreenVisible)
            {
                var mirrorPane = ReferenceEquals(pane, _editorPane) ? _secondaryEditorPane : _editorPane;
                var mirrorEditor = GetEditorForPane(mirrorPane);
                if (ReferenceEquals(mirrorPane.ActiveTab, tab) &&
                    ReferenceEquals(mirrorEditor.Document, captured.document))
                {
                    AssignPaneDocumentWithoutDirtyTracking(mirrorEditor, document);
                    var mirrorState = tab.GetViewState(mirrorPane.Id);
                    mirrorEditor.TextArea.Caret.Offset = Math.Clamp(mirrorState.CaretOffset, 0, document.TextLength);
                    mirrorEditor.ScrollToLine(Math.Clamp(mirrorState.TopLineNumber, 1, document.LineCount));
                }
            }
            var viewState = tab.GetViewState(pane.Id);
            editor.TextArea.Caret.Offset = Math.Clamp(viewState.CaretOffset, 0, document.TextLength);
            editor.ScrollToLine(Math.Clamp(viewState.TopLineNumber, 1, document.LineCount));
            KodoDiagnostics.LogDebug($"Recovered an empty restored editor document from '{tab.Path}'");
            return true;
        });
        if (!restored) return;

        try
        {
            if (IsLspDocumentOpenOrOpening(tab.Path))
                await LspNotifyDidCloseAsync(tab.Path).ConfigureAwait(false);
            await LspNotifyDidOpenAsync(tab.Path, content).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(QueueLspPresentationRefresh);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Unable to refresh language services after restoring '{tab.Path}'", ex);
        }
    }

    internal static bool IsEffectivelyEmptyRestoredDocument(TextDocument document)
    {
        if (document.TextLength == 0)
            return true;
        if (document.TextLength > 4096)
            return false;

        var text = document.GetTextAsMemory(0, document.TextLength).Span;
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
                return false;
        }
        return true;
    }

    private void AssignPaneDocumentWithoutDirtyTracking(TextEditor editor, TextDocument document)
    {
        var wasSuppressingDirtyTracking = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try
        {
            editor.Document = document;
        }
        finally
        {
            _suppressDirtyTracking = wasSuppressingDirtyTracking;
        }
    }

    private void CopyDiagnosticHighlights(TextEditor source, TextEditor target)
    {
        var sourceIsSecondary = ReferenceEquals(source, SecondaryEditorTextBox);
        var sourceDead = sourceIsSecondary ? _secondaryDeadCodeHighlightRenderer : _primaryDeadCodeHighlightRenderer;
        var sourceErrors = sourceIsSecondary ? _secondaryErrorHighlightRenderer : _primaryErrorHighlightRenderer;
        var targetIsSecondary = ReferenceEquals(target, SecondaryEditorTextBox);
        var targetDead = targetIsSecondary ? _secondaryDeadCodeHighlightRenderer : _primaryDeadCodeHighlightRenderer;
        var targetBrightener = targetIsSecondary ? _secondaryDeadCodeTextBrightener : _primaryDeadCodeTextBrightener;
        var targetErrors = targetIsSecondary ? _secondaryErrorHighlightRenderer : _primaryErrorHighlightRenderer;
        var targetDarkener = targetIsSecondary ? _secondaryErrorTextDarkener : _primaryErrorTextDarkener;

        targetDead.SetSpans(sourceDead.Spans, sourceDead.SpanIndex);
        targetBrightener.SetSpans(sourceDead.Spans);
        targetErrors.SetSpans(sourceErrors.Spans, sourceErrors.SpanIndex);
        targetErrors.SetDeadCodeSpans(sourceDead.Spans, sourceDead.SpanIndex);
        targetDarkener.SetSpans(sourceErrors.Spans, sourceErrors.SpanIndex, sourceDead.Spans, sourceDead.SpanIndex);
        RedrawVisibleEditorLines(target);
        target.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void CloseSplitEditor()
    {
        if (!IsSplitScreenVisible) return;
        HideDiagnosticPopup();
        CancelLanguageHoverRequest();
        CancelLspHoverRequest();
        _diagnosticPopupHideTimer.Stop();
        _hoveredEditorTextView = null;
        _hoveredEditorPane = null;
        _hoveredLanguageInfo = null;
        _hoveredErrorReason = null;
        _hoveredDeadCodeReason = null;
        _hoveredDiagnosticLineText = null;
        _hoveredDiagnosticMessage = null;
        _hoveredDiagnosticFilePath = null;
        _isPointerOverEditorLink = false;
        _lspHoverTooltip = null;
        SavePaneViewState(_secondaryEditorPane, SecondaryEditorTextBox);
        SaveCurrentEditorStateIntoTab();
        var wasSecondaryActive = ReferenceEquals(_activeEditorPane, _secondaryEditorPane);
        var primaryWasEmpty = _editorPane.ActiveTab is null;
        var secondaryActiveTab = _secondaryEditorPane.ActiveTab;
        if (wasSecondaryActive && secondaryActiveTab is not null)
        {
            CopyPaneViewState(secondaryActiveTab, _secondaryEditorPane, _editorPane);
            SetOtherPaneTab(_editorPane, secondaryActiveTab);
        }
        else if (_editorPane.ActiveTab is null && secondaryActiveTab is not null)
        {
            CopyPaneViewState(secondaryActiveTab, _secondaryEditorPane, _editorPane);
            SetOtherPaneTab(_editorPane, secondaryActiveTab);
        }

        foreach (var tab in _secondaryEditorPane.Tabs)
            _editorPane.AddTab(tab);

        if (wasSecondaryActive)
            ActivateEditorPane(_editorPane);
        else if (primaryWasEmpty && secondaryActiveTab is not null)
            ActivateTab(secondaryActiveTab, focusEditor: false);

        _secondaryEditorPane.ActiveTab = null;
        _secondaryEditorPane.Tabs.Clear();
        RefreshPaneTabSelection();
        IsSplitScreenVisible = false;
        SaveSettings();
        AssignPaneDocumentWithoutDirtyTracking(SecondaryEditorTextBox, new TextDocument(string.Empty));
        ApplyPaneSyntaxHighlighting(_secondaryEditorPane, SecondaryEditorTextBox, null);
        ReleasePanePreviewState(_secondaryEditorPane);
        SecondaryEditorMinimapView?.SetDocument(null);
        ScheduleEditorMinimapRefresh();
        OnPropertyChanged(nameof(SecondaryPaneSelectedTab));
    }

    private void SavePaneViewState(EditorPaneState pane, TextEditor editor)
    {
        if (pane.ActiveTab is not { } tab || editor.Document is null)
            return;
        var state = tab.GetViewState(pane.Id);
        var scrollOffset = editor.TextArea.TextView.ScrollOffset;
        if (editor.TextArea.TextView.GetDocumentLineByVisualTop(scrollOffset.Y) is { } topLine)
            state.TopLineNumber = topLine.LineNumber;
        state.ScrollOffsetX = scrollOffset.X;
        state.ScrollOffsetY = scrollOffset.Y;
        state.CaretOffset = editor.TextArea.Caret.Offset;
        state.HasTextViewState = true;
    }

    private static void CopyMissingPaneViewState(EditorTab tab, EditorPaneState sourcePane, EditorPaneState targetPane)
    {
        var source = tab.GetViewState(sourcePane.Id);
        var target = tab.GetViewState(targetPane.Id);
        if (!target.HasTextViewState)
        {
            target.CaretOffset = source.CaretOffset;
            target.TopLineNumber = source.TopLineNumber;
            target.ScrollOffsetX = source.ScrollOffsetX;
            target.ScrollOffsetY = source.ScrollOffsetY;
            target.HasTextViewState = true;
        }
        if (!target.HasImageViewState)
        {
            target.ImageZoomLevel = source.ImageZoomLevel;
            target.ImageScrollOffsetX = source.ImageScrollOffsetX;
            target.ImageScrollOffsetY = source.ImageScrollOffsetY;
            target.HasImageViewState = true;
        }
    }

    private static void CopyPaneViewState(EditorTab tab, EditorPaneState sourcePane, EditorPaneState targetPane)
    {
        var source = tab.GetViewState(sourcePane.Id);
        var target = tab.GetViewState(targetPane.Id);
        target.CaretOffset = source.CaretOffset;
        target.TopLineNumber = source.TopLineNumber;
        target.ScrollOffsetX = source.ScrollOffsetX;
        target.ScrollOffsetY = source.ScrollOffsetY;
        target.HasTextViewState = source.HasTextViewState;
        target.ImageZoomLevel = source.ImageZoomLevel;
        target.ImageScrollOffsetX = source.ImageScrollOffsetX;
        target.ImageScrollOffsetY = source.ImageScrollOffsetY;
        target.HasImageViewState = source.HasImageViewState;
    }

    private void OpenEditorTabToSide(EditorTab tab, EditorPaneState? sourcePaneOverride = null, bool moveFromSourcePane = false)
    {
        if (!OpenTabs.Contains(tab) || !CanDisplayInEditorPane(tab)) return;
        var splitWasVisible = IsSplitScreenVisible;
        var sourcePane = sourcePaneOverride ?? (splitWasVisible ? _activeEditorPane : _editorPane);
        if (sourcePaneOverride is null && !ReferenceEquals(sourcePane.ActiveTab, tab))
        {
            if (ReferenceEquals(_editorPane.ActiveTab, tab))
                sourcePane = _editorPane;
            else if (ReferenceEquals(_secondaryEditorPane.ActiveTab, tab))
                sourcePane = _secondaryEditorPane;
        }
        if (!IsSplitScreenVisible)
            ShowSplitEditor();
        if (!IsSplitScreenVisible) return;

        var targetPane = ReferenceEquals(sourcePane, _secondaryEditorPane)
            ? _editorPane
            : _secondaryEditorPane;


        if (!splitWasVisible)
            targetPane.Tabs.Clear();

        if (moveFromSourcePane && splitWasVisible && sourcePane.Tabs.Count == 1 && sourcePane.Tabs.Contains(tab))
        {
            var replacement = OpenTabs.FirstOrDefault(candidate => !ReferenceEquals(candidate, tab));
            if (replacement is not null)
                SetOtherPaneTab(sourcePane, replacement);
        }

        ActivateEditorPane(targetPane, focusEditor: false);
        if (!ReferenceEquals(ActiveEditorTab, tab))
            ActivateTab(tab, focusEditor: true, preserveOtherPaneMembership: true);
        else
            GetEditorForPane(targetPane).Focus();

        if (moveFromSourcePane)
            RemoveTabFromSourcePane(sourcePane, tab);
    }

    private void RemoveTabFromSourcePane(EditorPaneState sourcePane, EditorTab tab)
    {
        var index = sourcePane.Tabs.IndexOf(tab);
        if (index < 0 || !sourcePane.Tabs.Remove(tab))
            return;
        if (ReferenceEquals(sourcePane.ActiveTab, tab))
        {
            var replacement = sourcePane.Tabs.Count > 0
                ? sourcePane.Tabs[Math.Min(index, sourcePane.Tabs.Count - 1)]
                : null;
            SetOtherPaneTab(sourcePane, replacement);
        }
        SaveSettings();
    }

    private void ToggleSplitEditorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsSplitScreenVisible)
            CloseSplitEditor();
        else
            ShowSplitEditor();
    }

    private void CloseSplitEditorButton_OnClick(object? sender, RoutedEventArgs e) => CloseSplitEditor();

    private void CollapseEmptyEditorPanes()
    {
        if (IsSplitScreenVisible && (_editorPane.Tabs.Count == 0 || _secondaryEditorPane.Tabs.Count == 0))
            CloseSplitEditor();
    }

    private void FocusEditorPane(EditorPaneState pane)
    {
        if (IsSplitScreenVisible && pane.ActiveTab is not null)
            ActivateEditorPane(pane);
    }

    private void FocusOtherEditorPaneButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var otherPane = ReferenceEquals(_activeEditorPane, _editorPane)
            ? _secondaryEditorPane
            : _editorPane;
        if (otherPane.ActiveTab is null && ActiveEditorTab is { } activeTab)
        {
            SaveCurrentEditorStateIntoTab();
            CopyMissingPaneViewState(activeTab, _activeEditorPane, otherPane);
            SetOtherPaneTab(otherPane, activeTab);
        }
        ActivateEditorPane(otherPane);
    }

    private void PrimaryEditorPane_OnPointerPressed(object? sender, RoutedEventArgs e)
    {
        if (IsSplitScreenVisible)
            ActivateEditorPane(_editorPane, focusEditor: false);
    }

    private void SecondaryEditorPane_OnPointerPressed(object? sender, RoutedEventArgs e)
    {



        if (IsSplitScreenVisible)
            ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
    }

    private void OpenEditorTabToSideMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            OpenEditorTabToSide(tab, moveFromSourcePane: true);
    }

    private void OpenPrimaryEditorTabToSideMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            OpenEditorTabToSide(tab, _editorPane, moveFromSourcePane: true);
    }

    private void OpenSecondaryEditorTabToSideMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            OpenEditorTabToSide(tab, _secondaryEditorPane, moveFromSourcePane: true);
    }

    private void OpenPrimaryPaneTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_tabDragSuppressClick) return;
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab) return;
        ActivateEditorPane(_editorPane, focusEditor: false);
        ActivateTab(tab, focusEditor: true);
    }

    private void OpenSecondaryPaneTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_tabDragSuppressClick) return;
        if (TryGetTaggedData<EditorTab>(sender) is not { } tab) return;
        ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        ActivateTab(tab, focusEditor: true);
    }

    private const double TabDragThreshold = 8;
    private EditorTab? _tabDragTab;
    private EditorPaneState? _tabDragSourcePane;
    private Point _tabDragOrigin;
    private bool _tabDragActive;
    private bool _tabDragSuppressClick;
    private Cursor? _tabDragPreviousCursor;

    private void InitializeTabDragDrop()
    {
        AddHandler(InputElement.PointerPressedEvent, TabDrag_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, TabDrag_OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, TabDrag_OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _tabDragTab is not null)
                CancelTabDrag();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void TabDrag_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelTabDrag();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;

        var chain = source.GetSelfAndVisualAncestors().ToList();
        if (chain.OfType<Button>().Any(button => button.Classes.Contains("tabclose")) || chain.OfType<TextBox>().Any())
            return;
        var wrap = chain.OfType<Border>().FirstOrDefault(border => border.Classes.Contains("tabwrap") && border.DataContext is EditorTab);
        if (wrap?.DataContext is not EditorTab tab)
            return;

        _tabDragTab = tab;
        _tabDragSourcePane = chain.Any(visual => ReferenceEquals(visual, SecondaryEditorPane)) ? _secondaryEditorPane
            : chain.Any(visual => ReferenceEquals(visual, PrimaryEditorPane)) && IsSplitScreenVisible ? _editorPane
            : null;
        _tabDragOrigin = point.Position;
        _tabDragActive = false;
    }

    private void TabDrag_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragTab is null)
            return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelTabDrag();
            return;
        }
        if (!_tabDragActive)
        {
            if (Math.Abs(point.Position.X - _tabDragOrigin.X) < TabDragThreshold &&
                Math.Abs(point.Position.Y - _tabDragOrigin.Y) < TabDragThreshold)
                return;
            _tabDragActive = true;
            _tabDragPreviousCursor = Cursor;
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
        UpdateTabDropIndicator(e);
    }

    private void TabDrag_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_tabDragTab is not { } tab)
            return;
        var wasActive = _tabDragActive;
        var sourcePane = _tabDragSourcePane;
        var (targetPane, splitRight) = ResolveTabDropTarget(e);
        var insertIndex = targetPane is null ? 0 : GetTabInsertIndex(targetPane, e);
        CancelTabDrag();
        if (!wasActive)
            return;

        _tabDragSuppressClick = true;
        Dispatcher.UIThread.Post(() => _tabDragSuppressClick = false, DispatcherPriority.Background);

        if (!OpenTabs.Contains(tab))
            return;
        if (IsSplitScreenVisible)
        {
            if (targetPane is null)
                return;
            if (ReferenceEquals(sourcePane, targetPane))
                ReorderTabInPane(targetPane, tab, insertIndex);
            else
                MoveTabToPane(tab, sourcePane, targetPane, insertIndex);
        }
        else if (splitRight && CanToggleSplitEditor)
        {
            OpenEditorTabToSide(tab, moveFromSourcePane: true);
        }
    }

    private void CancelTabDrag()
    {
        if (_tabDragActive)
            Cursor = _tabDragPreviousCursor;
        _tabDragTab = null;
        _tabDragSourcePane = null;
        _tabDragActive = false;
        _tabDragPreviousCursor = null;
        TabDropIndicator.IsVisible = false;
    }

    private (EditorPaneState? pane, bool splitRight) ResolveTabDropTarget(PointerEventArgs e)
    {
        if (!EditorWorkspaceGrid.IsVisible)
            return (null, false);
        var position = e.GetPosition(EditorWorkspaceGrid);
        var size = EditorWorkspaceGrid.Bounds.Size;
        if (!new Rect(size).Contains(position))
            return (null, false);
        if (IsSplitScreenVisible)
            return (position.X <= PrimaryEditorPane.Bounds.Right + 2.5 ? _editorPane : _secondaryEditorPane, false);
        return position.X >= size.Width / 2 ? (null, true) : (_editorPane, false);
    }

    private void UpdateTabDropIndicator(PointerEventArgs e)
    {
        var (targetPane, splitRight) = ResolveTabDropTarget(e);
        var indicator = TabDropIndicator;
        if (IsSplitScreenVisible && targetPane is not null && !ReferenceEquals(targetPane, _tabDragSourcePane))
        {
            var pane = ReferenceEquals(targetPane, _secondaryEditorPane) ? (Control)SecondaryEditorPane : PrimaryEditorPane;
            indicator.HorizontalAlignment = ReferenceEquals(targetPane, _secondaryEditorPane)
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left;
            indicator.Width = pane.Bounds.Width;
            indicator.IsVisible = true;
        }
        else if (!IsSplitScreenVisible && splitRight && CanToggleSplitEditor)
        {
            indicator.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            indicator.Width = EditorWorkspaceGrid.Bounds.Width / 2;
            indicator.IsVisible = true;
        }
        else
        {
            indicator.IsVisible = false;
        }
    }

    private int GetTabInsertIndex(EditorPaneState pane, PointerEventArgs e)
    {
        Control root = ReferenceEquals(pane, _secondaryEditorPane) ? SecondaryEditorPane : PrimaryEditorPane;
        var wraps = root.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tabwrap") && border.DataContext is EditorTab)
            .ToList();
        if (wraps.Count == 0 || wraps[0].TranslatePoint(new Point(0, 0), this) is not { } firstOrigin)
            return pane.Tabs.Count;

        var position = e.GetPosition(this);
        if (position.Y > firstOrigin.Y + wraps[0].Bounds.Height)
            return pane.Tabs.Count;

        var index = 0;
        foreach (var wrap in wraps)
        {
            if (wrap.TranslatePoint(new Point(0, 0), this) is not { } origin || wrap.DataContext is not EditorTab wrapTab)
                continue;
            if (position.X > origin.X + wrap.Bounds.Width / 2)
                index = pane.Tabs.IndexOf(wrapTab) + 1;
        }
        return index;
    }

    private void ReorderTabInPane(EditorPaneState pane, EditorTab tab, int insertIndex)
    {
        var from = pane.Tabs.IndexOf(tab);
        if (from < 0)
            return;
        if (insertIndex > from)
            insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, pane.Tabs.Count - 1);
        if (insertIndex == from)
            return;
        pane.Tabs.Move(from, insertIndex);
        SaveSettings();
    }

    private void MoveTabToPane(EditorTab tab, EditorPaneState? sourcePane, EditorPaneState targetPane, int insertIndex)
    {
        if (sourcePane is null)
        {
            ActivateEditorPane(targetPane, focusEditor: false);
            ActivateTab(tab, focusEditor: true);
            return;
        }

        var sourceIndex = sourcePane.Tabs.IndexOf(tab);
        if (sourceIndex < 0)
            return;
        var sourceWasActive = ReferenceEquals(sourcePane.ActiveTab, tab);
        sourcePane.Tabs.Remove(tab);
        if (!targetPane.Tabs.Contains(tab))
            targetPane.Tabs.Insert(Math.Clamp(insertIndex, 0, targetPane.Tabs.Count), tab);

        ActivateEditorPane(targetPane, focusEditor: false);
        if (!ReferenceEquals(ActiveEditorTab, tab))
            ActivateTab(tab, focusEditor: true, preserveOtherPaneMembership: true);
        else
            GetEditorForPane(targetPane).Focus();

        if (sourceWasActive)
        {
            var replacement = sourcePane.Tabs.Count > 0
                ? sourcePane.Tabs[Math.Min(sourceIndex, sourcePane.Tabs.Count - 1)]
                : null;
            SetOtherPaneTab(sourcePane, replacement);
        }
        SaveSettings();
        CollapseEmptyEditorPanes();
    }
}
