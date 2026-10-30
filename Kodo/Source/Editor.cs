// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Animation;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;
using Avalonia.Threading;
using Kodo.Editor;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{
    private CancellationTokenSource? _wordCountScanCancellation;

    private CancellationTokenSource? _lspCompletionCts;
    private CancellationTokenSource? _lspPresentationCancellation;
    private bool _editorInsightsRefreshRunning;
    private bool _editorInsightsRefreshPending;
    private readonly List<EditorNavigationLocation> _editorBackHistory = new();
    private readonly List<EditorNavigationLocation> _editorForwardHistory = new();
    private const int EditorNavigationHistoryLimit = 100;
    private const int LspMaxFoldingsApplied = 4_000;
    private const int LocalInsightSkipThreshold = 80_000;

    private static readonly Color[] SemanticTokenPalette =
    {
        Color.Parse("#569CD6"), Color.Parse("#4EC9B0"), Color.Parse("#DCDCAA"), Color.Parse("#C586C0"),
        Color.Parse("#CE9178"), Color.Parse("#9CDCFE"), Color.Parse("#B5CEA8"), Color.Parse("#D7BA7D")
    };
    private static readonly IBrush[] SemanticTokenBrushes = SemanticTokenPalette
        .Select(color => (IBrush)new SolidColorBrush(color, 0.18))
        .ToArray();

    private void EditorStateRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _editorStateRefreshTimer.Stop();
        if (!IsActive) { _pendingFullStateRefresh = true; return; }
        RefreshState(fullRefresh: _pendingFullStateRefresh);
    }

    private void QueueInsightRefresh()
    {
        var document = EditorTextBox?.Document;
        var hasLsp = !string.IsNullOrWhiteSpace(_currentFilePath) && ResolveLspExtensionForFile(_currentFilePath) is not null;
        if (hasLsp)
        {
            var delay = document is { } doc && (doc.LineCount >= 50_000 || doc.TextLength >= 1_000_000)
                ? 900
                : document is { } mediumDocument && (mediumDocument.LineCount >= 5_000 || mediumDocument.TextLength >= 250_000)
                    ? 450
                    : 120;
            _InsightRefreshTimer.Interval = TimeSpan.FromMilliseconds(delay);
        }
        else
        {
            _InsightRefreshTimer.Interval = TimeSpan.FromMilliseconds(750);
        }
        _InsightRefreshTimer.Stop();
        _InsightRefreshTimer.Start();
    }

    private static CancellationTokenSource RenewAnalysisCancellation(ref CancellationTokenSource current)
    {
        var previous = current;
        var replacement = new CancellationTokenSource();
        current = replacement;
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
        return replacement;
    }

    private static void CancelAnalysisRequest(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void CancelEditorAnalysisRequests()
    {
        CancelAnalysisRequest(_insightAnalysisCancellation);
        CancelAnalysisRequest(_deadCodeAnalysisCancellation);
        CancelAnalysisRequest(_errorAnalysisCancellation);
    }

    private void InsightRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _InsightRefreshTimer.Stop();
        if (!IsActive) return;
        if (_editorInsightsRefreshRunning)
        {
            _editorInsightsRefreshPending = true;
            return;
        }
        _editorInsightsRefreshRunning = true;
        _ = RefreshEditorInsightsSafelyAsync();
    }

    private sealed record EditorNavigationLocation(string FilePath, int Offset);

    private EditorNavigationLocation? CaptureEditorNavigationLocation()
    {
        if (EditorTextBox?.Document is null || string.IsNullOrWhiteSpace(_currentFilePath)) return null;
        return new EditorNavigationLocation(_currentFilePath, Math.Clamp(EditorTextBox.TextArea.Caret.Offset, 0, EditorTextBox.Document.TextLength));
    }

    private void RecordEditorNavigation(EditorNavigationLocation? location)
    {
        if (location is null) return;
        if (_editorBackHistory.Count > 0 && _editorBackHistory[^1] == location) return;
        _editorBackHistory.Add(location);
        if (_editorBackHistory.Count > EditorNavigationHistoryLimit)
            _editorBackHistory.RemoveAt(0);
        _editorForwardHistory.Clear();
    }

    private async Task NavigateEditorHistoryAsync(bool backwards)
    {
        var source = backwards ? _editorBackHistory : _editorForwardHistory;
        var destination = backwards ? _editorForwardHistory : _editorBackHistory;
        if (source.Count == 0) return;
        var target = source[^1];
        var targetPane = _activeEditorPane;
        var current = CaptureEditorNavigationLocation();
        if (current is null) return;

        try
        {
            if (!FileSystemPaths.Equals(_currentFilePath, target.FilePath))
                await OpenFileFromPathAsync(target.FilePath, targetPane);

            var restored = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var destinationPane = ResolveEditorPaneForOpen(targetPane);
                var destinationEditor = GetEditorForPane(destinationPane);
                if (!FileSystemPaths.Equals(destinationPane.ActiveTab?.Path, target.FilePath) || destinationEditor.Document is null)
                    return false;
                NavigateTo(AppPage.Editor);
                destinationEditor.TextArea.Caret.Offset = Math.Clamp(target.Offset, 0, destinationEditor.Document.TextLength);
                destinationEditor.TextArea.Caret.BringCaretToView();
                destinationEditor.Focus();
                return true;
            });
            if (!restored) return;

            if (source.Count == 0 || source[^1] != target) return;
            source.RemoveAt(source.Count - 1);
            if (destination.Count == 0 || destination[^1] != current)
                destination.Add(current);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Editor navigation history restore failed", ex);
        }
    }

    private void NavigateEditorDiagnostic(int direction)
    {
        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null) return;
        var problems = _errorHighlightRenderer.Spans
            .Select(span => (Offset: span.StartOffset, Message: span.Message))
            .Concat(_deadCodeHighlightRenderer.Spans.Select(span => (Offset: span.StartOffset, Message: span.Reason)))
            .Where(problem => problem.Offset >= 0 && problem.Offset <= EditorTextBox.Document.TextLength)
            .Distinct()
            .OrderBy(problem => problem.Offset)
            .ToList();
        if (problems.Count == 0) return;

        var caret = EditorTextBox.TextArea.Caret.Offset;
        var index = direction >= 0
            ? problems.FindIndex(problem => problem.Offset > caret)
            : problems.FindLastIndex(problem => problem.Offset < caret);
        if (index < 0) index = direction >= 0 ? 0 : problems.Count - 1;

        EditorTextBox.TextArea.Caret.Offset = problems[index].Offset;
        EditorTextBox.TextArea.Caret.BringCaretToView();
        EditorTextBox.Focus();
    }

    private async Task RefreshEditorInsightsSafelyAsync()
    {
        try
        {
            await RefreshEditorInsightsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Editor insight refresh failed.", ex);
        }
        finally
        {
            _editorInsightsRefreshRunning = false;
            if (_editorInsightsRefreshPending && IsActive)
            {
                _editorInsightsRefreshPending = false;
                QueueInsightRefresh();
            }
            else
            {
                _editorInsightsRefreshPending = false;
            }
        }
    }

    private async Task RefreshEditorInsightsAsync()
    {
        var document = EditorTextBox?.Document;
        var len = document?.TextLength ?? 0;
        var refreshWatch = System.Diagnostics.Stopwatch.StartNew();
        var languageExtension = CurrentLanguageExtension;
        var scanExtension = languageExtension is null ? string.Empty : $"{languageExtension.Id}\u001F{languageExtension.Version}";
        var errorSignature = ComputeErrorHighlightSignature(_insightDocVersion, _currentFilePath, scanExtension);
        var deadSignature = ComputeDeadCodeSignature(_insightDocVersion, _currentFilePath, scanExtension, _currentFolderPath);
        var deferLocalAnalysis = PerformanceBudget.IsLargeFile(document, LocalInsightSkipThreshold) &&
            LspEnabled && ResolveLspExtensionForFile(_currentFilePath) is not null;
        if (deferLocalAnalysis)
        {
            await UpdateInsightAsync();
            refreshWatch.Stop();
            KodoDiagnostics.ReportSlowStage("editor insight refresh", refreshWatch.ElapsedMilliseconds, 250, $"len={len} LSP completion only=true");
            return;
        }
        if (errorSignature == _errorHighlightAppliedSignature && deadSignature == _deadCodeAppliedSignature)
        {
            await UpdateInsightAsync();
            refreshWatch.Stop();
            KodoDiagnostics.ReportSlowStage("editor insight refresh", refreshWatch.ElapsedMilliseconds, 250, $"len={len} suggestionsOnly=true");
            return;
        }
        await Task.WhenAll(UpdateInsightAsync(), UpdateDeadCodeHighlightingAsync(), UpdateErrorHighlightingAsync());
        refreshWatch.Stop();
        KodoDiagnostics.ReportSlowStage("editor insight refresh", refreshWatch.ElapsedMilliseconds, 250, $"len={len} suggestionsAndDiagnostics=true");
    }

    private async void WordCountRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _wordCountRefreshTimer.Stop();
        if (!IsActive)
        {
            CancelWordCountScan();
            return;
        }
        if (!HasDocumentOpen || !IsPlainTextFile(_currentFilePath) || EditorTextBox?.Document is null || !IsWordCountVisible)
        {
            CancelWordCountScan();
            WordCountText = string.Empty;
            OnPropertyChanged(nameof(IsWordCountVisible));
            return;
        }
        var document = EditorTextBox.Document;
        var capturedVersion = _insightDocVersion;
        var capturedPath = _currentFilePath;
        var capturedDocument = document;
        var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
        var cancellation = new CancellationTokenSource();
        CancelWordCountScan();
        _wordCountScanCancellation = cancellation;
        int wordCount;
        try
        {
            wordCount = await EditorBackgroundWork.RunAsync(
                token => CountWords(snapshot.Source, token),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Plain-text word count refresh failed.", ex);
            return;
        }
        finally
        {
            if (ReferenceEquals(_wordCountScanCancellation, cancellation))
                _wordCountScanCancellation = null;
            cancellation.Dispose();
        }

        if (capturedVersion != _insightDocVersion || !ReferenceEquals(EditorTextBox?.Document, capturedDocument)) return;
        if (!FileSystemPaths.Equals(capturedPath, _currentFilePath)) return;
        if (!HasDocumentOpen || !IsPlainTextFile(_currentFilePath) || EditorTextBox?.Document is null) return;
        WordCountText = $"{wordCount} words";
        OnPropertyChanged(nameof(IsWordCountVisible));
    }

    private static int CountWords(ITextSource source, CancellationToken cancellationToken)
    {
        var buffer = new char[32 * 1024];
        var wordCount = 0;
        var inWord = false;
        using var reader = source.CreateReader();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                if (char.IsWhiteSpace(buffer[i]))
                    inWord = false;
                else if (!inWord)
                {
                    inWord = true;
                    wordCount++;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return wordCount;
    }

    private void CancelWordCountScan()
    {
        try { _wordCountScanCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void RefreshWordCount()
    {
        if (!HasDocumentOpen || !IsPlainTextFile(_currentFilePath) || EditorTextBox?.Document is null || !IsWordCountVisible)
        {
            CancelWordCountScan();
            WordCountText = string.Empty;
            return;
        }
        QueueWordCountRefresh();
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

    private string? _hoveredDiagnosticLineText;
    private string? _hoveredDiagnosticMessage;
    private string? _hoveredDiagnosticFilePath;
    private string? _hoveredLanguageInfo;
    private AvaloniaEdit.Rendering.TextView? _hoveredEditorTextView;
    private EditorPaneState? _hoveredEditorPane;
    private string? _pendingDiagnosticMessage;
    private double _pendingDiagnosticOffsetX;
    private double _pendingDiagnosticOffsetY;
    private string? _lspSignatureHelpTooltip;
    private string? _lspHoverTooltip;
    private string? _lspHoverRequestKey;
    private CancellationTokenSource? _languageHoverCts;
    private string? _languageHoverKey;
    private string? _languageHoverResultKey;
    private string? _languageHoverResult;

    private void QueueLspPresentationRefresh()
    {
        _lspDocumentRefreshPending = true;
        _lspDocumentRefreshFailures = 0;
        if (IsActive)
            RestartLspPresentationDebounceTimer();
    }

    private void QueueOtherPaneLspPresentationRefresh()
    {
        if (!LspEnabled || !IsActive || !IsSplitScreenVisible)
            return;
        if (_lspRefreshDebounceTimer.IsEnabled)
            return;

        _lspRefreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(120);
        _lspRefreshDebounceTimer.Start();
    }

    private void RestartLspPresentationDebounceTimer()
    {
        if (!LspEnabled)
        {
            _lspRefreshDebounceTimer.Stop();
            return;
        }
        var document = EditorTextBox?.Document;
        var delay = document is { } doc && (doc.LineCount >= 50_000 || doc.TextLength >= 1_000_000)
            ? 1_000
            : document is { TextLength: >= 500_000 } ? 600 : 350;
        _lspRefreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _lspRefreshDebounceTimer.Stop();
        _lspRefreshDebounceTimer.Start();
    }

    private void ClearLspSignatureHelpTooltip()
    {
        var textView = EditorTextBox?.TextArea?.TextView;
        if (textView is not null && _lspSignatureHelpTooltip is not null &&
            Equals(ToolTip.GetTip(textView), _lspSignatureHelpTooltip))
            ToolTip.SetTip(textView, null);
        _lspSignatureHelpTooltip = null;
    }

    private void ClearLspHoverTooltip()
    {
        var textView = _hoveredEditorTextView ?? EditorTextBox?.TextArea?.TextView;
        if (textView is not null && _lspHoverTooltip is not null &&
            Equals(ToolTip.GetTip(textView), _lspHoverTooltip))
            ToolTip.SetTip(textView, null);
        _lspHoverTooltip = null;
    }

    private void HideDiagnosticPopup()
    {
        CancelLanguageHoverRequest();
        CancelLspHoverRequest();
        var textView = _hoveredEditorTextView ?? EditorTextBox?.TextArea?.TextView;
        if (!_diagnosticPopupHideTimer.IsEnabled && !_diagnosticPopupShowTimer.IsEnabled &&
            _pendingDiagnosticMessage is null && !DiagnosticPopup.IsOpen &&
            (textView is null || ToolTip.GetTip(textView) is null))
            return;

        _diagnosticPopupHideTimer.Stop();
        _diagnosticPopupShowTimer.Stop();
        _pendingDiagnosticMessage = null;
        if (DiagnosticPopup.IsOpen)
            DiagnosticPopup.IsOpen = false;
        if (textView is not null && ToolTip.GetTip(textView) is not null)
            ToolTip.SetTip(textView, null);
        _lspHoverTooltip = null;
    }

    private void EditorTextView_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _diagnosticPopupHideTimer.Stop();
        var isSecondaryView = ReferenceEquals(sender, SecondaryEditorTextBox.TextArea.TextView);
        var isPrimaryView = ReferenceEquals(sender, PrimaryEditorTextBox.TextArea.TextView);
        var pane = isSecondaryView ? _secondaryEditorPane : _editorPane;
        var editor = isSecondaryView ? SecondaryEditorTextBox : isPrimaryView ? PrimaryEditorTextBox : EditorTextBox;
        var tab = pane.ActiveTab;
        var filePath = tab is { IsUntitled: false } ? tab.Path : null;
        var textView = sender as TextView ?? editor.TextArea.TextView;
        if (tab is null || editor.Document is null)
        {
            CancelLanguageHoverRequest();
            HideDiagnosticPopup();
            ToolTip.SetTip(textView, null);
            return;
        }
        var hoverTargetChanged = !ReferenceEquals(_hoveredEditorTextView, textView);
        if (hoverTargetChanged && _hoveredEditorTextView is { } previousTextView)
            ToolTip.SetTip(previousTextView, null);
        if (hoverTargetChanged)
            _lspHoverTooltip = null;
        _hoveredEditorTextView = textView;
        _hoveredEditorPane = pane;
        var position = e.GetPosition(textView);
        var nowOverLink = IsPointerOverLink(position, textView, editor);
        var errorRenderer = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryErrorHighlightRenderer
            : _primaryErrorHighlightRenderer;
        var deadCodeRenderer = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryDeadCodeHighlightRenderer
            : _primaryDeadCodeHighlightRenderer;
        var errorReason = nowOverLink ? null : GetErrorReasonAt(position, textView, editor, filePath, errorRenderer);
        var deadCodeReason = nowOverLink ? null : GetDeadCodeReasonAt(position, textView, editor, filePath, deadCodeRenderer);
        string? languageHover;
        if (nowOverLink || errorReason is not null || deadCodeReason is not null)
        {
            CancelLanguageHoverRequest();
            CancelLspHoverRequest();
            languageHover = null;
        }
        else
        {
            languageHover = GetLanguageHoverAt(position, textView, editor, pane, tab, filePath);
        }

        var lspHoverEligible = LspHoverEnabled && !nowOverLink && errorReason is null && deadCodeReason is null &&
            languageHover is null && !string.IsNullOrWhiteSpace(filePath) &&
            ResolveLspExtensionForFile(filePath) is not null;
        string? lspHoverRequestKey = null;
        if (lspHoverEligible && editor.Document is { } documentForHoverKey)
        {
            var hoverPosition = textView.GetPositionFloor(position + textView.ScrollOffset);
            if (hoverPosition is { } resolvedPosition)
                lspHoverRequestKey = $"{pane.Id}\u001f{filePath}\u001f{RuntimeHelpers.GetHashCode(documentForHoverKey.Version)}\u001f{resolvedPosition.Line}:{resolvedPosition.Column}";
        }

        string? diagnosticMessage = BuildDiagnosticMessage(errorReason, deadCodeReason);
        string? primaryReason = errorReason ?? deadCodeReason;

        if (nowOverLink == _isPointerOverEditorLink &&
            (deadCodeReason == _hoveredDeadCodeReason) && (errorReason == _hoveredErrorReason) &&
            languageHover == _hoveredLanguageInfo &&
            !hoverTargetChanged &&
            (!lspHoverEligible || string.Equals(lspHoverRequestKey, _lspHoverRequestKey, StringComparison.Ordinal)) &&
            (diagnosticMessage is null || DiagnosticPopup.IsOpen || _pendingDiagnosticMessage == diagnosticMessage))
            return;

        _isPointerOverEditorLink = nowOverLink;
        _hoveredDeadCodeReason = deadCodeReason;
        _hoveredErrorReason = errorReason;
        _hoveredLanguageInfo = languageHover;

        _hoveredDiagnosticLineText = nowOverLink ? null : GetLineTextAt(position, editor);
        _hoveredDiagnosticFilePath = filePath;
        _hoveredDiagnosticMessage = diagnosticMessage;

        if (nowOverLink)
        {
            _diagnosticPopupShowTimer.Stop();
            _pendingDiagnosticMessage = null;
            DiagnosticPopup.IsOpen = false;
            ToolTip.SetTip(textView, "Ctrl+click to open link");
            ToolTip.SetShowDelay(textView, 400);
            textView.Cursor = new Cursor(StandardCursorType.Hand);
        }
        else if (diagnosticMessage is not null)
        {
            ToolTip.SetTip(textView, null);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
            DiagnosticPopup.PlacementTarget = textView;

            double offsetX, offsetY;
            try
            {
                var floor = textView.GetPositionFloor(position + textView.ScrollOffset);
                if (floor is not null)
                {
                    var y = textView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(floor.Value.Line, 1), AvaloniaEdit.Rendering.VisualYPosition.LineTop).Y - textView.ScrollOffset.Y;
                    if (double.IsNaN(y) || double.IsInfinity(y)) y = position.Y;
                    offsetX = Math.Clamp(position.X, 8, Math.Max(8, textView.Bounds.Width - 430));
                    offsetY = Math.Clamp(y, 4, Math.Max(4, textView.Bounds.Height - 100));
                }
                else
                {
                    offsetX = Math.Clamp(position.X, 8, 300);
                    offsetY = Math.Clamp(position.Y, 4, 300);
                }
            }
            catch
            {
                offsetX = Math.Clamp(position.X, 8, 300);
                offsetY = Math.Clamp(position.Y, 4, 300);
            }

            _pendingDiagnosticMessage = diagnosticMessage;
            _hoveredEditorTextView = textView;
            _pendingDiagnosticOffsetX = offsetX;
            _pendingDiagnosticOffsetY = offsetY;
            _diagnosticPopupShowTimer.Stop();
            _diagnosticPopupShowTimer.Start();
        }
        else if (languageHover is not null)
        {
            CancelLspHoverRequest();
            _lspHoverTooltip = null;
            _diagnosticPopupShowTimer.Stop();
            _pendingDiagnosticMessage = null;
            DiagnosticPopup.IsOpen = false;
            ToolTip.SetTip(textView, languageHover);
            ToolTip.SetShowDelay(textView, 450);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
        }
        else if (lspHoverEligible && lspHoverRequestKey is not null)
        {
            if (string.Equals(lspHoverRequestKey, _lspHoverRequestKey, StringComparison.Ordinal))
                return;
            CancelLspHoverRequest();
            _lspHoverRequestKey = lspHoverRequestKey;
            _diagnosticPopupShowTimer.Stop();
            _pendingDiagnosticMessage = null;
            DiagnosticPopup.IsOpen = false;
            _lspHoverTooltip = null;
            ToolTip.SetTip(textView, null);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
            CancellationTokenSource hoverCts;
            lock (_lspHoverLock)
            {
                var previousHoverCts = _lspHoverCts;
                _lspHoverCts = new CancellationTokenSource();
                hoverCts = _lspHoverCts;
                previousHoverCts?.Cancel();
            }
            var hoverPos = position;
            var hoverView = textView;
            var hoverEditor = editor;
            var hoverPane = pane;
            var hoverTab = tab;
            var hoverDocument = editor.Document;
            var hoverDocumentVersion = hoverDocument.Version;
            var hoverPath = filePath;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250, hoverCts.Token);
                    var hoverState = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (hoverCts.IsCancellationRequested) return (-1, -1, -1, -1, -1, ' ', "");
                        try
                        {
                            var f = hoverView.GetPositionFloor(hoverPos + hoverView.ScrollOffset);
                            if (f is null || !ReferenceEquals(hoverEditor.Document, hoverDocument) ||
                                !ReferenceEquals(hoverDocument.Version, hoverDocumentVersion)) return (-1, -1, -1, -1, -1, ' ', "");
                            var doc = hoverDocument;
                            var l = doc.GetLineByNumber(f.Value.Line);
                            var off = Math.Clamp(l.Offset + Math.Max(0, f.Value.Column - 1), 0, doc.TextLength);
                            var ch = off >= 0 && off < doc.TextLength ? doc.GetCharAt(off) : ' ';
                            var verbose = KodoDiagnostics.VerboseLoggingEnabled;
                            var lineText = verbose && l.Length > 0 ? doc.GetText(l.Offset, Math.Min(l.Length, 40)).Replace("\r", "\\r").Replace("\n", "\\n") : "";
                            var snippetFrom = Math.Max(0, off - 10);
                            var snippet = verbose && doc.TextLength > 0 ? doc.GetText(snippetFrom, Math.Min(20, doc.TextLength - snippetFrom)).Replace("\n", "\\n").Replace("\r", "\\r") : "";
                            var lspLine = f.Value.Line - 1;
                            var lspChar = Math.Max(0, off - l.Offset);
                            if (verbose)
                                KodoDiagnostics.LogDebug($"LSP hover mapping: mouse=({hoverPos.X:F1},{hoverPos.Y:F1}) TextView line={f.Value.Line} col={f.Value.Column} docOffset={off} char='{ch}' lineText='{lineText}' snippet='{snippet}'");
                            return (off, lspLine, lspChar, f.Value.Line, f.Value.Column, ch, snippet);
                        }
                        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP hover mapping failed: {ex.Message}"); return (-1, -1, -1, -1, -1, ' ', ""); }
                    });
                    if (hoverCts.IsCancellationRequested) return;
                    var hoverOffset = hoverState.Item1;
                    var lspLine = hoverState.Item2;
                    var lspChar = hoverState.Item3;
                    var hoverLine = hoverState.Item4;
                    var hoverCol = hoverState.Item5;
                    var hoverChar = hoverState.Item6;
                    var hoverSnippet = hoverState.Item7;
                    if (hoverOffset < 0 || lspLine < 0) return;
                    if (KodoDiagnostics.VerboseLoggingEnabled)
                        KodoDiagnostics.LogDebug($"LSP hover request file={hoverPath} offset={hoverOffset} char='{hoverChar}' line={hoverLine} col={hoverCol} -> LSP line={lspLine} char={lspChar} snippet='{hoverSnippet}'");
                    var hoverInfo = await GetLspHoverAsync(hoverPath, hoverOffset, lspLine, lspChar,
                        RuntimeHelpers.GetHashCode(hoverDocumentVersion), hoverCts.Token).ConfigureAwait(false);
                    if (hoverCts.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(hoverInfo))
                    {
                        KodoDiagnostics.LogDebug($"LSP hover response empty for {hoverPath}");
                        return;
                    }
                    KodoDiagnostics.LogDebug($"LSP hover response len={hoverInfo.Length} for {hoverPath}");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (hoverCts.IsCancellationRequested || !LspEnabled || !LspHoverEnabled ||
                            !ReferenceEquals(hoverPane.ActiveTab, hoverTab) ||
                            !ReferenceEquals(hoverEditor.Document, hoverDocument) ||
                            !ReferenceEquals(hoverDocument.Version, hoverDocumentVersion) ||
                            !FileSystemPaths.Equals(hoverTab.Path, hoverPath) ||
                            !ReferenceEquals(_hoveredEditorTextView, hoverView)) return;
                        if (DiagnosticPopup.IsOpen) return;
                        _lspHoverTooltip = hoverInfo;
                        ToolTip.SetTip(hoverView, hoverInfo);
                        ToolTip.SetShowDelay(hoverView, 450);
                        KodoDiagnostics.LogDebug($"LSP hover UI displayed for {hoverPath}");
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { KodoDiagnostics.LogDebug("LSP hover async failed", ex); }
                finally
                {
                    lock (_lspHoverLock)
                    {
                        if (ReferenceEquals(_lspHoverCts, hoverCts))
                            _lspHoverCts = null;
                    }
                    hoverCts.Dispose();
                }
            });
        }
        else
        {
            CancelLspHoverRequest();
            _lspHoverTooltip = null;
            _diagnosticPopupShowTimer.Stop();
            _pendingDiagnosticMessage = null;
            DiagnosticPopup.IsOpen = false;
            ToolTip.SetTip(textView, null);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
        }
    }

    private void EditorTextView_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ReferenceEquals(sender, SecondaryEditorTextBox.TextArea.TextView))
            ActivateEditorPane(_secondaryEditorPane, focusEditor: false);
        else if (ReferenceEquals(sender, PrimaryEditorTextBox.TextArea.TextView))
            ActivateEditorPane(_editorPane, focusEditor: false);
        HideDiagnosticPopup();

        if (HandleEditorHorizontalScrollGesture(e))
            e.Handled = true;
    }

    private void DiagnosticPopupShowTimer_OnTick(object? sender, EventArgs e)
    {
        _diagnosticPopupShowTimer.Stop();
        if (_pendingDiagnosticMessage is null || _hoveredEditorTextView is null) return;
        DiagnosticPopupText.Text = _pendingDiagnosticMessage;
        DiagnosticPopup.HorizontalOffset = _pendingDiagnosticOffsetX;
        DiagnosticPopup.VerticalOffset = _pendingDiagnosticOffsetY;
        DiagnosticPopup.IsOpen = true;
    }

    private string? GetLanguageHoverAt(
        Point position,
        AvaloniaEdit.Rendering.TextView textView,
        TextEditor editor,
        EditorPaneState pane,
        EditorTab tab,
        string? filePath)
    {
        try
        {
            var rules = string.IsNullOrWhiteSpace(filePath)
                ? null
                : GetLanguageExtension(filePath)?.LangRules;
            if (rules is not { HasHoverProvider: true } || editor.Document is null)
            {
                CancelLanguageHoverRequest();
                return null;
            }
            var floor = textView.GetPositionFloor(position + textView.ScrollOffset);
            if (floor is null)
            {
                CancelLanguageHoverRequest();
                return null;
            }
            var document = editor.Document;
            var documentVersion = document.Version;
            var line = document.GetLineByNumber(floor.Value.Line);
            var offset = Math.Clamp(line.Offset + Math.Max(0, floor.Value.Column - 1), 0, document.TextLength);
            var lineStart = line.Offset;
            var lineEnd = line.Offset + line.Length;
            var start = offset;
            var end = offset;
            var scanStart = Math.Min(offset, Math.Max(lineStart, lineEnd - 1));
            if (scanStart < lineEnd && !InsightEngine.IsWordChar(document.GetCharAt(scanStart)) && scanStart > lineStart)
                scanStart--;
            if (scanStart < lineStart || scanStart >= lineEnd || !InsightEngine.IsWordChar(document.GetCharAt(scanStart)))
            {
                CancelLanguageHoverRequest();
                return null;
            }
            start = scanStart;
            end = scanStart + 1;
            while (start > lineStart && offset - start < 256 && InsightEngine.IsWordChar(document.GetCharAt(start - 1))) start--;
            while (end < lineEnd && end - scanStart < 256 && InsightEngine.IsWordChar(document.GetCharAt(end))) end++;
            if ((start > lineStart && InsightEngine.IsWordChar(document.GetCharAt(start - 1))) ||
                (end < lineEnd && InsightEngine.IsWordChar(document.GetCharAt(end))))
            {
                CancelLanguageHoverRequest();
                return null;
            }
            var word = document.GetText(start, end - start);
            var key = $"{pane.Id}\u001f{filePath}\u001f{RuntimeHelpers.GetHashCode(documentVersion)}\u001f{start}";
            if (!string.Equals(_languageHoverKey, key, StringComparison.Ordinal))
            {
                CancelLanguageHoverRequest();
                _languageHoverKey = key;
                _languageHoverResultKey = null;
                _languageHoverResult = null;
                var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
                var cancellation = new CancellationTokenSource();
                _languageHoverCts = cancellation;
                _ = RunLanguageHoverAsync(editor, pane, tab, document, documentVersion, snapshot, rules, word, key, filePath, textView, cancellation);
            }

            return string.Equals(_languageHoverResultKey, key, StringComparison.Ordinal) ? _languageHoverResult : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunLanguageHoverAsync(
        TextEditor editor,
        EditorPaneState pane,
        EditorTab tab,
        AvaloniaEdit.Document.TextDocument document,
        AvaloniaEdit.Document.ITextSourceVersion documentVersion,
        EditorTextSnapshot snapshot,
        LangRulesAdapter rules,
        string word,
        string key,
        string? filePath,
        AvaloniaEdit.Rendering.TextView textView,
        CancellationTokenSource cancellation)
    {
        string? result = null;
        try
        {
            await Task.Delay(180, cancellation.Token).ConfigureAwait(false);
            var text = await snapshot.GetTextAsync(cancellation.Token).ConfigureAwait(false);
            result = rules.GetHoverInfo(text, word)?.Contents;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Editor language hover failed.", ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var isCancelled = cancellation.IsCancellationRequested;
            if (ReferenceEquals(_languageHoverCts, cancellation))
                _languageHoverCts = null;
            cancellation.Dispose();
            if (string.IsNullOrWhiteSpace(result) || isCancelled ||
                !string.Equals(_languageHoverKey, key, StringComparison.Ordinal) ||
                !ReferenceEquals(pane.ActiveTab, tab) ||
                !ReferenceEquals(editor.Document, document) ||
                !ReferenceEquals(document.Version, documentVersion) ||
                !FileSystemPaths.Equals(filePath, tab.IsUntitled ? null : tab.Path) ||
                !ReferenceEquals(_hoveredEditorTextView, textView) ||
                _isPointerOverEditorLink || _hoveredErrorReason is not null || _hoveredDeadCodeReason is not null)
                return;
            CancelLspHoverRequest();
            _lspHoverTooltip = null;
            _languageHoverResultKey = key;
            _languageHoverResult = result;
            _hoveredLanguageInfo = result;
            ToolTip.SetTip(textView, result);
            ToolTip.SetShowDelay(textView, 0);
        });
    }

    private void CancelLanguageHoverRequest()
    {
        _languageHoverCts?.Cancel();
        _languageHoverCts = null;
        _languageHoverKey = null;
        _languageHoverResultKey = null;
        _languageHoverResult = null;
    }

    private void CancelLspHoverRequest()
    {
        CancellationTokenSource? hoverCts;
        lock (_lspHoverLock)
        {
            hoverCts = _lspHoverCts;
            _lspHoverCts = null;
        }
        hoverCts?.Cancel();
        _lspHoverRequestKey = null;
    }

    private static string? BuildDiagnosticMessage(string? errorReason, string? deadCodeReason)
    {
        if (errorReason is null && deadCodeReason is null) return null;

        var parts = new List<string>();
        if (errorReason is not null)
        {
            var alreadyLabeled = errorReason.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || errorReason.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                || errorReason.StartsWith("Info:", StringComparison.OrdinalIgnoreCase)
                || errorReason.StartsWith("Hint:", StringComparison.OrdinalIgnoreCase);
            parts.Add(alreadyLabeled ? errorReason : $"Error: {errorReason}");
        }
        if (deadCodeReason is not null) parts.Add($"Dead Code: {deadCodeReason}");

        return parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]}{Environment.NewLine}--------------------{Environment.NewLine}{parts[1]}",
            _ => null
        };
    }

    private string? GetLineTextAt(Point position, TextEditor editor)
    {
        try
        {
            var textView = editor.TextArea.TextView;
            var pos = textView.GetPositionFloor(position + textView.ScrollOffset);
            if (pos is null) return null;
            var line = editor.Document.GetLineByNumber(pos.Value.Line);
            return editor.Document.GetText(line.Offset, line.Length);
        }
        catch
        {
            return null;
        }
    }

    private static string StripDiagnosticLabel(string message)
    {
        if (message.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase))
            return message.Substring("Error: ".Length);
        if (message.StartsWith("Warning: ", StringComparison.OrdinalIgnoreCase))
            return message.Substring("Warning: ".Length);
        if (message.StartsWith("Info: ", StringComparison.OrdinalIgnoreCase))
            return message.Substring("Info: ".Length);
        if (message.StartsWith("Hint: ", StringComparison.OrdinalIgnoreCase))
            return message.Substring("Hint: ".Length);
        if (message.StartsWith("Dead Code: ", StringComparison.OrdinalIgnoreCase))
            return message.Substring("Dead Code: ".Length);
        return message;
    }

    private async void DiagnosticPopupDismiss_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_hoveredEditorPane is { } pane && !ReferenceEquals(pane, _activeEditorPane))
            ActivateEditorPane(pane, focusEditor: false);
        var lineText = _hoveredDiagnosticLineText;
        var filePath = _hoveredDiagnosticFilePath;
        var didDismiss = false;

        if (lineText is not null)
        {
            if (_hoveredErrorReason is not null)
            {
                DismissDiagnostic(filePath, lineText, StripDiagnosticLabel(_hoveredErrorReason));
                didDismiss = true;
            }
            if (_hoveredDeadCodeReason is not null)
            {
                DismissDiagnostic(filePath, lineText, StripDiagnosticLabel(_hoveredDeadCodeReason));
                didDismiss = true;
            }
        }

        if (!didDismiss && lineText is not null && DiagnosticPopupText.Text is not null)
        {
            var parts = DiagnosticPopupText.Text.Split(new[] { "--------------------" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var msg = p.Trim();
                if (string.IsNullOrWhiteSpace(msg)) continue;
                msg = StripDiagnosticLabel(msg);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    DismissDiagnostic(filePath, lineText, msg);
                }
            }
        }

        DiagnosticPopup.IsOpen = false;
        if (_hoveredEditorTextView is { } textView)
            ToolTip.SetTip(textView, null);

        await UpdateErrorHighlightingSafelyAsync();
        await UpdateDeadCodeHighlightingAsync();

        _hoveredDiagnosticLineText = null;
        _hoveredDiagnosticMessage = null;
        _hoveredDeadCodeReason = null;
        _hoveredErrorReason = null;
        _hoveredDiagnosticFilePath = null;
    }

    private string? GetErrorReasonAt(
        Point pointerPosition,
        AvaloniaEdit.Rendering.TextView textView,
        TextEditor editor,
        string? filePath,
        ErrorLineHighlightRenderer renderer)
    {
        if (IsErrorDeadCodeBlacklisted(filePath))
            return null;

        if (renderer.Spans.Count == 0)
            return null;

        var pos = textView.GetPositionFloor(pointerPosition + textView.ScrollOffset);
        if (pos is null) return null;

        try
        {
            var doc = editor.Document;
            var ln = pos.Value.Line;
            if (ln < 1 || ln > doc.LineCount) return null;
            var line = doc.GetLineByNumber(ln);
            var colOffset = Math.Clamp(pos.Value.Column - 1, 0, line.Length);
            var offset = line.Offset + colOffset;
            var msg = renderer.GetMessageAt(offset);
            if (msg is not null) return msg;
            bool hasEolSpanOnLine = renderer.HasEolSpanOnLine(line.Offset, line.EndOffset);
            if (hasEolSpanOnLine && colOffset >= Math.Max(0, line.Length - 4))
                return renderer.GetMessageForLine(line.Offset, line.EndOffset);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private string? GetDeadCodeReasonAt(
        Point pointerPosition,
        AvaloniaEdit.Rendering.TextView textView,
        TextEditor editor,
        string? filePath,
        DeadCodeHighlightRenderer renderer)
    {
        if (!IsInsightEnabled || !IsInsightDeadCodeEnabled || IsErrorDeadCodeBlacklisted(filePath))
            return null;

        var pos = textView.GetPositionFloor(pointerPosition + textView.ScrollOffset);
        if (pos is null) return null;

        try
        {
            var doc = editor.Document;
            var ln = pos.Value.Line;
            if (ln < 1 || ln > doc.LineCount) return null;
            var line = doc.GetLineByNumber(ln);
            var colOffset = Math.Clamp(pos.Value.Column - 1, 0, line.Length);
            return renderer.GetReasonAt(line.Offset + colOffset);
        }
        catch
        {
            return null;
        }
    }

    private void EditorTextBox_OnTextChanged(object? sender, EventArgs e)
    {
        var textChangeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var changedEditor = sender as TextEditor ?? EditorTextBox;
        if (changedEditor is null) return;
        var changedPane = ReferenceEquals(changedEditor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane;



        var changedTab = OpenTabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Document, changedEditor.Document))
            ?? changedPane.ActiveTab;
        var changedFilePath = changedTab is { IsUntitled: false } ? changedTab.Path : null;
        var isActiveDocument = ReferenceEquals(changedEditor, EditorTextBox) &&
            (ReferenceEquals(EditorSession.Document, changedEditor.Document) || EditorSession.Document is null);
        if (isActiveDocument)
        {
            _lspCompletionCts?.Cancel();
            _lspPresentationCancellation?.Cancel();
            _lspDocumentRefreshPending = true;
            _lspDocumentRefreshFailures = 0;
            CancelEditorAnalysisRequests();
            CancelLspHoverRequest();
            HideDiagnosticPopup();
        }
        else if (ReferenceEquals(_hoveredEditorTextView, changedEditor.TextArea.TextView))
        {
            CancelLspHoverRequest();
            HideDiagnosticPopup();
        }
        var curLen = changedEditor.Document?.TextLength ?? 0;
        ScheduleEditorMinimapRefresh();
        var deferLocalInsights = LspEnabled && LspDiagnosticsEnabled &&
            PerformanceBudget.IsLargeFile(changedEditor.Document, LocalInsightSkipThreshold) &&
            !string.IsNullOrWhiteSpace(changedFilePath) &&
            ResolveLspExtensionForFile(changedFilePath) is not null;
        if (!string.IsNullOrWhiteSpace(changedFilePath) && !HasNoFileExtension(changedFilePath))
            QueueLspDidChange(changedFilePath);
        ClearPaneDiagnosticHighlights(changedEditor, redraw: false);
        ClearLspPresentationForEditor(changedEditor, redraw: false);
        var changedFindRenderer = ReferenceEquals(changedEditor, SecondaryEditorTextBox)
            ? _secondaryFindHighlightRenderer
            : _primaryFindHighlightRenderer;
        changedFindRenderer.Clear();
        if (isActiveDocument)
        {
            _findMatchOffsets.Clear();
            _currentFindMatchIndex = -1;
            UpdateFindStatusText();
        }
        var otherEditor = ReferenceEquals(changedEditor, SecondaryEditorTextBox)
            ? PrimaryEditorTextBox
            : SecondaryEditorTextBox;
        if (otherEditor is not null && ReferenceEquals(otherEditor.Document, changedEditor.Document))
        {
            ClearPaneDiagnosticHighlights(otherEditor, redraw: false);
            ClearLspPresentationForEditor(otherEditor, redraw: false);
            var otherFindRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryFindHighlightRenderer
                : _primaryFindHighlightRenderer;
            otherFindRenderer.Clear();
        }
        if (isActiveDocument)
            RestartLspPresentationDebounceTimer();
        else
            QueueOtherPaneLspPresentationRefresh();

        if (isActiveDocument && deferLocalInsights)
        {
            CloseCompletionWindow();
        }

        if (_suppressDirtyTracking) return;
        if (changedTab is not null)
            changedTab.IsDirty = true;
        if (isActiveDocument)
        {
            ClearAutoSaveStatus();
            var wasDirty = _isDirty;
            _isDirty = true;
            OnPropertyChanged(nameof(IsDocumentDirty));
            QueueRefreshState(fullRefresh: !deferLocalInsights && !wasDirty);
            RestartAutoSaveTimerIfNeeded();
            if (!deferLocalInsights || LspEnabled)
                QueueInsightRefresh();
        }
        else if (changedTab is not null)
        {
            _ = RefreshInactiveTabInsightDiagnosticsAsync(changedTab);
        }
        if (isActiveDocument && IsFindInFileSearchMode && IsSearchPanelVisible)
        {
            _findHighlightDebounceTimer.Stop();
            _findHighlightDebounceTimer.Start();
        }
        var textChangeElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(textChangeStartedAt);
        KodoDiagnostics.ReportSlowStage("editor text change handling", (long)textChangeElapsed.TotalMilliseconds, 16, $"len={curLen}");
    }

    private void FindHighlightDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _findHighlightDebounceTimer.Stop();
        if (!IsActive) return;
        if (IsFindInFileSearchMode && IsSearchPanelVisible)
            UpdateFindHighlights();
    }

    private TextEditor GetEditorForTextInput(object? sender) =>
        sender is AvaloniaEdit.Editing.TextArea textArea && ReferenceEquals(textArea, SecondaryEditorTextBox.TextArea)
            ? SecondaryEditorTextBox
            : sender is AvaloniaEdit.Editing.TextArea primaryTextArea && ReferenceEquals(primaryTextArea, PrimaryEditorTextBox.TextArea)
                ? PrimaryEditorTextBox
                : EditorTextBox;

    private void EditorTextArea_OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!IsSmartSyntaxEnabled()) return;
        var editor = GetEditorForTextInput(sender);
        var pane = ReferenceEquals(editor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane;
        var filePath = pane.ActiveTab is { IsUntitled: false } tab ? tab.Path : null;
        var languageExtension = string.IsNullOrWhiteSpace(filePath) ? null : GetLanguageExtension(filePath);
        if (IsMarkdownFile(filePath)) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];
        var caret = editor.TextArea.Caret;
        var doc = editor.Document;
        var offset = caret.Offset;
        var selection = editor.TextArea.Selection;
        if (languageExtension?.IsFeatureDisabled("bracketAutoClose") == true) return;
        if (!selection.IsEmpty && BracketPairs.TryGetValue(ch, out var selectionClosing))
        {
            var segment = selection.SurroundingSegment;
            if (segment is not null)
            {
                var selectedText = selection.GetText();
                var documentVersion = doc.Version;
                var selectionOffset = segment.Offset;
                var selectionLength = segment.Length;
                try
                {
                    doc.Replace(segment, $"{ch}{selectedText}{selectionClosing}");
                }
                catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
                {
                    KodoDiagnostics.LogDebug("EditorTextArea_OnTextEntering: Visual line race suppressed", ex);
                    e.Handled = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ReferenceEquals(editor.Document, doc) || !ReferenceEquals(doc.Version, documentVersion)) return;
                        var currentSelection = editor.TextArea.Selection;
                        if (currentSelection.IsEmpty ||
                            currentSelection.SurroundingSegment is not { } currentSegment ||
                            currentSegment.Offset != selectionOffset ||
                            currentSegment.Length != selectionLength ||
                            !string.Equals(currentSelection.GetText(), selectedText, StringComparison.Ordinal)) return;

                        try
                        {
                            doc.Replace(currentSegment, $"{ch}{selectedText}{selectionClosing}");
                            caret.Offset = selectionOffset + selectedText.Length + 1;
                        }
                        catch { }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                    return;
                }
                caret.Offset = segment.Offset + selectedText.Length + 1;
                e.Handled = true;
                return;
            }
        }

        var isPairOpener = BracketPairs.ContainsKey(ch);

        if (!ClosingChars.Contains(ch))
        {
            if (isPairOpener) BeginAutoCloseUndoGroup(doc);
            return;
        }

        if (ch == '}' && TryAlignClosingDelimiterBeforeInsert(doc, caret, '}'))
            offset = caret.Offset;

        if (offset >= doc.TextLength || doc.GetCharAt(offset) != ch)
        {
            if (isPairOpener) BeginAutoCloseUndoGroup(doc);
            return;
        }

        bool skip = ch is ')' or ']' or '}' or '>';
        if (!skip && (ch == '"' || ch == '\''))
            skip = offset > 0 && doc.GetCharAt(offset - 1) == ch;

        if (skip)
        {
            caret.Offset = offset + 1;
            e.Handled = true;
        }
        else if (isPairOpener)
        {
            BeginAutoCloseUndoGroup(doc);
        }
    }

    private AvaloniaEdit.Document.TextDocument? _autoCloseUndoGroupDocument;

    private void BeginAutoCloseUndoGroup(AvaloniaEdit.Document.TextDocument doc)
    {
        if (ReferenceEquals(_autoCloseUndoGroupDocument, doc)) return;
        if (_autoCloseUndoGroupDocument is { } previousDocument)
        {
            previousDocument.UndoStack.EndUndoGroup();
            _autoCloseUndoGroupDocument = null;
        }
        doc.UndoStack.StartUndoGroup();
        _autoCloseUndoGroupDocument = doc;
    }

    private void EndAutoCloseUndoGroupIfOpen(AvaloniaEdit.Document.TextDocument doc)
    {
        if (_autoCloseUndoGroupDocument is not { } groupedDocument) return;
        groupedDocument.UndoStack.EndUndoGroup();
        _autoCloseUndoGroupDocument = null;
    }

    private void ClosePendingAutoCloseUndoGroup()
    {
        if (_autoCloseUndoGroupDocument is { } groupedDocument)
            EndAutoCloseUndoGroupIfOpen(groupedDocument);
    }

    private void EditorTextArea_OnTextEntered(object? sender, TextInputEventArgs e)
    {
        var editor = GetEditorForTextInput(sender);
        var pane = ReferenceEquals(editor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane;
        var filePath = pane.ActiveTab is { IsUntitled: false } tab ? tab.Path : null;
        var languageExtension = string.IsNullOrWhiteSpace(filePath) ? null : GetLanguageExtension(filePath);
        var doc = editor.Document;
        try
        {
            if (!IsSmartSyntaxEnabled()) return;
            if (IsMarkdownFile(filePath)) return;
            if (string.IsNullOrEmpty(e.Text)) return;
            var ch = e.Text[0];

            if (languageExtension?.IsFeatureDisabled("bracketAutoClose") == true)
                return;

            if (!BracketPairs.TryGetValue(ch, out var closing)) return;

            var caret = editor.TextArea.Caret;
            var offset = caret.Offset;

            if (ch == '"' || ch == '\'' || ch == '`')
            {
                if (offset < doc.TextLength)
                {
                    var next = doc.GetCharAt(offset);
                    if (char.IsLetterOrDigit(next) || next == ch) return;
                }
            }

            doc.Insert(offset, closing.ToString());
            caret.Offset = offset;
        }
        finally
        {
            EndAutoCloseUndoGroupIfOpen(doc);
        }
    }

    private string? GetLanguageWordAtCaret()
    {
        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null) return null;
        var document = EditorTextBox.Document;
        var offset = Math.Clamp(EditorTextBox.TextArea.Caret.Offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        var lineStart = line.Offset;
        var lineEnd = line.Offset + line.Length;
        var start = offset;
        var end = offset;
        while (start > lineStart && InsightEngine.IsWordChar(document.GetCharAt(start - 1))) start--;
        while (end < lineEnd && InsightEngine.IsWordChar(document.GetCharAt(end))) end++;
        return end > start ? document.GetText(start, end - start) : null;
    }

    private void EditorGoToDefinitionMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        GoToDefinitionAtCaret();
    }

    private async void GoToDefinitionAtCaret()
    {
        if (EditorTextBox?.Document is null) return;
        var origin = CaptureEditorNavigationLocation();
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var document = EditorTextBox.Document;
        var documentVersion = document.Version;
        var revision = EditorSession.Revision;
        var revisionToken = EditorSession.RevisionCancellationToken;
        var path = _currentFilePath;
        var offset = Math.Clamp(EditorTextBox.TextArea.Caret.Offset, 0, document.TextLength);
        var word = GetLanguageWordAtCaret();
        bool IsRequestCurrent() =>
            ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(EditorTextBox, requestEditor) &&
            ReferenceEquals(requestPane.ActiveTab, requestTab) && ReferenceEquals(requestEditor.Document, document) &&
            ReferenceEquals(document.Version, documentVersion) && EditorSession.Revision == revision &&
            FileSystemPaths.Equals(path, _currentFilePath) && requestEditor.TextArea.Caret.Offset == offset;

        try
        {
            if (!string.IsNullOrWhiteSpace(path) && ResolveLspExtensionForFile(path) is not null)
            {
                if (await TryLspGoToDefinitionAsync(path, offset))
                {
                    RecordEditorNavigation(origin);
                    return;
                }
            }
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP go-to-definition failed, falling back to LangRules", ex); }

        if (!IsRequestCurrent()) return;
        var extension = CurrentLanguageExtension;
        if (extension?.LangRules is not { HasDefinitionProvider: true } rules || string.IsNullOrWhiteSpace(word) || EditorTextBox?.Document is null)
            return;
        string text;
        try
        {
            text = await EditorTextSnapshotStore.GetSnapshot(document).GetTextAsync(revisionToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Go-to-definition could not capture the current text.", ex);
            return;
        }
        if (!IsRequestCurrent()) return;
        LangRuleLocation? definition;
        try
        {
            definition = await EditorBackgroundWork.RunAsync(_ => rules.FindDefinition(text, word), revisionToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Local go-to-definition failed.", ex);
            return;
        }
        if (definition is null || !IsRequestCurrent()) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsRequestCurrent()) return;
            var start = Math.Clamp(definition.Start, 0, EditorTextBox.Document.TextLength);
            var length = Math.Clamp(definition.Length, 0, EditorTextBox.Document.TextLength - start);
            EditorTextBox.Select(start, length);
            EditorTextBox.TextArea.Caret.BringCaretToView();
            EditorTextBox.Focus();
            RecordEditorNavigation(origin);
        });
    }

    private async void EditorFindReferencesMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.Document is null) return;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var document = requestEditor.Document;
        var documentVersion = document.Version;
        var revision = EditorSession.Revision;
        var path = _currentFilePath;
        var offset = Math.Clamp(requestEditor.TextArea.Caret.Offset, 0, document.TextLength);
        var word = GetLanguageWordAtCaret();
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && ResolveLspExtensionForFile(path) is not null)
            {
                if (await TryLspFindReferencesAsync(path, offset, word))
                    return;
            }
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP find references failed, falling back", ex); }
        if (!ReferenceEquals(_activeEditorPane, requestPane) || !ReferenceEquals(EditorTextBox, requestEditor) ||
            !ReferenceEquals(requestPane.ActiveTab, requestTab) || !ReferenceEquals(requestEditor.Document, document) ||
            !ReferenceEquals(document.Version, documentVersion) || EditorSession.Revision != revision ||
            !FileSystemPaths.Equals(path, _currentFilePath) || requestEditor.TextArea.Caret.Offset != offset)
            return;
        var extension = CurrentLanguageExtension;
        if (extension?.LangRules is not { HasReferenceProvider: true } || string.IsNullOrWhiteSpace(word)) return;
        FindText = word;
        OpenSearchPanel(IsFolderOpen ? SearchMode.ProjectSearch : SearchMode.FindInFile);
    }

    private async void EditorFormatDocumentMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.Document is null) return;
        var targetEditor = EditorTextBox;
        var requestPane = _activeEditorPane;
        var requestTab = requestPane.ActiveTab;
        var document = targetEditor.Document;
        var documentVersion = document.Version;
        var revision = EditorSession.Revision;
        var revisionToken = EditorSession.RevisionCancellationToken;
        var path = _currentFilePath;
        bool IsRequestCurrent() =>
            ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(EditorTextBox, targetEditor) &&
            ReferenceEquals(requestPane.ActiveTab, requestTab) && ReferenceEquals(targetEditor.Document, document) &&
            ReferenceEquals(document.Version, documentVersion) && EditorSession.Revision == revision &&
            FileSystemPaths.Equals(path, _currentFilePath);
        string text;
        try
        {
            text = await EditorTextSnapshotStore.GetSnapshot(document).GetTextAsync(revisionToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Document formatting could not capture the current text.", ex);
            return;
        }
        if (text is null) return;
        if (!IsRequestCurrent()) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && ResolveLspExtensionForFile(path) is not null)
            {
                if (await TryLspFormatDocumentAsync(path, text, revisionToken))
                    return;
            }
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP formatting failed, falling back", ex); }
        if (!IsRequestCurrent()) return;
        if (CurrentLanguageExtension?.LangRules is not { HasFormatter: true } rules) return;
        string? formatted;
        try
        {
            formatted = await EditorBackgroundWork.RunAsync(_ => rules.FormatDocument(text), revisionToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Local document formatting failed.", ex);
            if (IsRequestCurrent())
                ExtensionsStatusText = "Formatting failed. See the log for details.";
            return;
        }
        if (formatted is null || string.Equals(formatted, text, StringComparison.Ordinal) || !IsRequestCurrent()) return;
        var caret = targetEditor.TextArea.Caret.Offset;
        if (!IsRequestCurrent()) return;
        try
        {
            document.Text = formatted;
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("EditorFormatDocument: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsRequestCurrent()) return;
                try
                {
                    document.Text = formatted;
                    targetEditor.TextArea.Caret.Offset = Math.Min(caret, document.TextLength);
                }
                catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        targetEditor.TextArea.Caret.Offset = Math.Min(caret, document.TextLength);
    }

    private async void EditorApplyCodeActionMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunCodeActionsWithFallbackAsync(CodeActionScope.All).ConfigureAwait(false);
    }

    private async Task RunCodeActionsWithFallbackAsync(CodeActionScope scope)
    {
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var requestDocument = requestEditor?.Document;
        var requestRevision = EditorSession.Revision;
        var requestCaret = requestEditor?.TextArea.Caret.Offset ?? -1;

        var outcome = await RunCodeActionsAsync(scope).ConfigureAwait(false);
        if (!ShouldRunLocalCodeActionFallback(outcome)) return;

        var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(EditorTextBox, requestEditor) &&
            ReferenceEquals(requestPane.ActiveTab, requestTab) &&
            ReferenceEquals(requestEditor?.Document, requestDocument) && EditorSession.Revision == requestRevision &&
            requestEditor?.TextArea.Caret.Offset == requestCaret);
        if (requestIsCurrent)
            await RunLangRuleFallbackAsync().ConfigureAwait(false);
    }


    private async void EditorRenameSymbolMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.Document is null) return;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var document = requestEditor.Document;
        var documentVersion = document.Version;
        var revision = EditorSession.Revision;
        var path = _currentFilePath;
        var caretOffset = requestEditor.TextArea.Caret.Offset;
        var oldName = GetLanguageWordAtCaret();
        if (string.IsNullOrWhiteSpace(oldName)) return;
        var newName = await ShowTextInputDialogAsync("Rename Symbol", $"Rename '{oldName}' to:", oldName);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
        bool IsRequestCurrent() =>
            ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(EditorTextBox, requestEditor) &&
            ReferenceEquals(requestPane.ActiveTab, requestTab) && ReferenceEquals(requestEditor.Document, document) &&
            ReferenceEquals(document.Version, documentVersion) && EditorSession.Revision == revision &&
            requestEditor.TextArea.Caret.Offset == caretOffset && FileSystemPaths.Equals(path, _currentFilePath);
        if (!IsRequestCurrent()) return;
        var text = await EditorTextSnapshotStore.GetSnapshot(document).GetTextAsync();
        if (text is null || !IsRequestCurrent()) return;
        if (!await TryLspRenameAsync(path, caretOffset, text, newName))
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsRequestCurrent()) ExtensionsStatusText = "The language server could not rename this symbol.";
            });
    }

    private async void EditorTypeDefinitionMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.Document is null) return;
        var origin = CaptureEditorNavigationLocation();
        var document = EditorTextBox.Document;
        var revision = EditorSession.Revision;
        var path = _currentFilePath;
        var offset = EditorTextBox.TextArea.Caret.Offset;
        var ok = await TryLspNavigateFeatureAsync(path, offset, "textDocument/typeDefinition");
        if (ok) RecordEditorNavigation(origin);
        if (!ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision || !FileSystemPaths.Equals(path, _currentFilePath)) return;
        if (!ok) await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = "No type definition was found by the language server.");
    }

    private async void EditorImplementationMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.Document is null) return;
        var origin = CaptureEditorNavigationLocation();
        var document = EditorTextBox.Document;
        var revision = EditorSession.Revision;
        var path = _currentFilePath;
        var offset = EditorTextBox.TextArea.Caret.Offset;
        var ok = await TryLspNavigateFeatureAsync(path, offset, "textDocument/implementation");
        if (ok) RecordEditorNavigation(origin);
        if (!ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision || !FileSystemPaths.Equals(path, _currentFilePath)) return;
        if (!ok) await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = "No implementation was found by the language server.");
    }

    private async void EditorDocumentSymbolsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.Document is null) return;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = requestPane.ActiveTab;
        var filePath = _currentFilePath;
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var document = requestEditor.Document;
        var documentVersion = document.Version;
        var documentRevision = EditorSession.Revision;
        bool IsRequestCurrent() =>
            ReferenceEquals(_activeEditorPane, requestPane) && ReferenceEquals(EditorTextBox, requestEditor) &&
            ReferenceEquals(requestPane.ActiveTab, requestTab) && ReferenceEquals(requestEditor.Document, document) &&
            ReferenceEquals(document.Version, documentVersion) && EditorSession.Revision == documentRevision &&
            FileSystemPaths.Equals(_currentFilePath, filePath);
        var encoding = GetLspClientForFile(filePath)?.PositionEncoding ?? "utf-16";
        var result = await GetLspDocumentSymbolsAsync(filePath).ConfigureAwait(false);
        var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(IsRequestCurrent);
        if (!requestIsCurrent) return;
        if (result is null || result.Value.ValueKind != JsonValueKind.Array || result.Value.GetArrayLength() == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsRequestCurrent())
                    ExtensionsStatusText = "The language server returned no document symbols.";
            });
            return;
        }
        var symbols = new List<(string Label, int Line, int Character)>();
        void AddSymbols(JsonElement items, string indent)
        {
            foreach (var symbol in items.EnumerateArray())
            {
                if (!symbol.TryGetProperty("name", out var name)) continue;
                var range = symbol.TryGetProperty("selectionRange", out var selection) ? selection : symbol.TryGetProperty("range", out var fallback) ? fallback : default;
                if (range.ValueKind != JsonValueKind.Object || !range.TryGetProperty("start", out var start)) continue;
                if (!TryReadLspPosition(start, out var symbolLine, out var symbolCharacter)) continue;
                var kind = symbol.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.Number && kindEl.TryGetInt32(out var kindValue) ? kindValue.ToString() : "";
                symbols.Add(($"{indent}{name.GetString()}  ({kind})", symbolLine, symbolCharacter));
                if (symbol.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array) AddSymbols(children, indent + "  ");
            }
        }
        AddSymbols(result.Value, "");
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (!IsRequestCurrent())
                return;

            var list = new ListBox
            {
                ItemsSource = symbols.Select(s => s.Label).ToArray(),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 0),
                Foreground = PrimaryTextBrush,
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
                        Text = "Document Symbols",
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

            var listBorder = new Border
            {
                Background = WindowBackgroundBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = KodoDesignTokens.ControlRadius,
                Padding = new Thickness(10),
                Child = list
            };

            var hintText = new TextBlock
            {
                Text = "Double-click a symbol to jump to it.",
                FontSize = 11,
                Foreground = MutedTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85
            };

            Window? window = null;
            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children =
                {
                    CreateDialogButton("Close", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush, () => window!.Close())
                }
            };

            var panel = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
                Children = { headerRow, headerDivider, listBorder, hintText, footerDivider, buttonRow }
            };
            Grid.SetRow(listBorder, 2);
            Grid.SetRow(hintText, 3);
            Grid.SetRow(footerDivider, 4);
            Grid.SetRow(buttonRow, 5);

            var outer = CreateDialogSurface(panel, new Thickness(16));

            window = new Window
            {
                Title = "Kodo - Document Symbols",
                Width = 560,
                Height = 420,
                MinWidth = 420,
                MinHeight = 300,
                CanResize = true,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = WindowBackgroundBrush,
                Content = outer,
            };

            list.DoubleTapped += async (_, _) =>
            {
                if (list.SelectedIndex < 0 || list.SelectedIndex >= symbols.Count) return;
                var selected = symbols[list.SelectedIndex];
                window.Close();
                if (!IsRequestCurrent()) return;
                requestEditor.TextArea.Caret.Offset = LspPath.OffsetFromLspPosition(document, selected.Line, selected.Character, encoding);
                requestEditor.TextArea.Caret.BringCaretToView();
                requestEditor.Focus();
            };
            window.Opened += (_, _) => list.Focus();
            using (PerformanceBudget.TrackModal())
            {
                await window.ShowDialog(this);
            }
        });
    }

    private async void EditorSignatureHelpMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!LspEnabled || !LspSignatureHelpEnabled) return;
        if (EditorTextBox?.Document is null) return;
        var filePath = _currentFilePath;
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var document = EditorTextBox.Document;
        var documentRevision = EditorSession.Revision;
        var offset = Math.Clamp(EditorTextBox.TextArea.Caret.Offset, 0, document.TextLength);
        var client = GetLspClientForFile(filePath);
        var encoding = client?.PositionEncoding ?? "utf-16";
        (int line, int character) position;
        if (string.Equals(encoding, "utf-16", StringComparison.OrdinalIgnoreCase))
            position = LspPath.OffsetToLspPosition(document, offset, encoding);
        else
        {
            var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
            var text = await snapshot.GetTextAsync(EditorSession.RevisionCancellationToken).ConfigureAwait(false);
            position = await EditorBackgroundWork.RunAsync(_ => LspPath.OffsetToLspPosition(text, offset, encoding), CancellationToken.None).ConfigureAwait(false);
        }
        var result = await GetLspSignatureHelpAsync(filePath, position.line, position.character).ConfigureAwait(false);
        var label = result is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("signatures", out var signatures) && signatures.ValueKind == JsonValueKind.Array && signatures.GetArrayLength() > 0 && signatures[0].TryGetProperty("label", out var labelElement) ? labelElement.GetString() : null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!FileSystemPaths.Equals(_currentFilePath, filePath) || !ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != documentRevision || EditorTextBox.TextArea.Caret.Offset != offset) return;
            ExtensionsStatusText = string.IsNullOrWhiteSpace(label) ? "No signature help was returned by the language server." : $"Signature: {label}";
        });
    }

    private async void EditorQuickFixMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunCodeActionsWithFallbackAsync(CodeActionScope.QuickFix).ConfigureAwait(false);
    }

    private async Task RunLangRuleFallbackAsync()
    {
        var captured = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (EditorTextBox?.Document is null || CurrentLanguageExtension?.LangRules is not { HasCodeActionProvider: true } rules)
                return (Document: (AvaloniaEdit.Document.TextDocument?)null, Snapshot: (EditorTextSnapshot?)null, Revision: 0L, Caret: 0,
                    Rules: (LangRulesAdapter?)null, Pane: (EditorPaneState?)null, Editor: (AvaloniaEdit.TextEditor?)null,
                    Tab: (EditorTab?)null, Version: (AvaloniaEdit.Document.ITextSourceVersion?)null,
                    FilePath: (string?)null, RevisionToken: CancellationToken.None, IsLargeFile: false);
            var editor = EditorTextBox;
            return (Document: editor.Document, Snapshot: EditorTextSnapshotStore.GetSnapshot(editor.Document),
                Revision: EditorSession.Revision, Caret: editor.TextArea.Caret.Offset, Rules: rules,
                Pane: (EditorPaneState?)_activeEditorPane, Editor: (AvaloniaEdit.TextEditor?)editor,
                Tab: _activeEditorPane.ActiveTab, Version: editor.Document.Version,
                FilePath: _currentFilePath,
                RevisionToken: EditorSession.RevisionCancellationToken,
                IsLargeFile: PerformanceBudget.IsLargeFile(editor.Document));
        });
        if (captured.Document is null || captured.Snapshot is null || captured.Rules is null)
        {
            SetLspFeatureStatus("No quick fixes available at the caret.");
            return;
        }
        bool IsRequestCurrent() =>
            ReferenceEquals(_activeEditorPane, captured.Pane) && ReferenceEquals(EditorTextBox, captured.Editor) &&
            ReferenceEquals(captured.Pane.ActiveTab, captured.Tab) && ReferenceEquals(captured.Editor.Document, captured.Document) &&
            ReferenceEquals(captured.Document.Version, captured.Version) && EditorSession.Revision == captured.Revision &&
            captured.Editor.TextArea.Caret.Offset == captured.Caret;

        if (captured.IsLargeFile)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsRequestCurrent())
                    ExtensionsStatusText = "Local quick fixes are skipped for large files to keep the editor responsive.";
            });
            return;
        }

        IReadOnlyList<LangRuleCodeAction>? actions;
        string textSnapshot;
        try
        {
            textSnapshot = await captured.Snapshot.GetTextAsync(captured.RevisionToken).ConfigureAwait(false);
            actions = await EditorBackgroundWork.RunAsync(_ => captured.Rules.GetCodeActions(textSnapshot, captured.Caret), captured.RevisionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var matchingActions = GetApplicableLangRuleCodeActions(
            actions,
            captured.Caret,
            textSnapshot);
        if (matchingActions.Count == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsRequestCurrent())
                    ExtensionsStatusText = "No quick fixes available at the caret.";
            });
            return;
        }

        using var rawAction = JsonDocument.Parse("{}");
        var menuItems = matchingActions.Select(action => new CodeActionItem
        {
            Title = string.IsNullOrWhiteSpace(action.Title) ? "Apply local quick fix" : action.Title,
            Kind = string.IsNullOrWhiteSpace(action.Kind) ? "quickfix" : action.Kind,
            Detail = string.IsNullOrWhiteSpace(action.DiagnosticCode) ? null : action.DiagnosticCode,
            Raw = rawAction.RootElement.Clone(),
            LocalAction = action
        }).ToList();
        var snapshot = new CodeActionSnapshot(
            captured.FilePath ?? string.Empty,
            textSnapshot,
            captured.Document,
            captured.Version!,
            captured.Revision,
            captured.Caret,
            0,
            0,
            captured.Pane,
            captured.Editor,
            captured.Tab);
        var selected = await PresentCodeActionsAsync(menuItems, snapshot).ConfigureAwait(false);
        var action = selected?.LocalAction;
        if (action is null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsRequestCurrent()) return;
            try
            {
                captured.Document.Replace(action.Start, action.Length, action.NewText);
                ExtensionsStatusText = string.IsNullOrWhiteSpace(action.Title)
                    ? "Applied local fix."
                    : $"Applied: {action.Title}";
            }
            catch (Exception ex) { KodoDiagnostics.LogDebug("LangRules quick fix failed", ex); }
        });
    }

    private async Task UpdateInsightAsync(string? textSnapshot = null)
    {
        var scanToken = RenewAnalysisCancellation(ref _insightAnalysisCancellation).Token;
        var lspCompletionSettingsRevision = _lspCompletionSettingsRevision;
        if (!IsInsightEnabled || !IsInsightCodeSuggestionsEnabled)
        {
            CloseCompletionWindow();
            return;
        }

        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null)
        {
            CloseCompletionWindow();
            return;
        }

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
        var scanVersion = _insightDocVersion;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox;
        var requestTab = ActiveEditorTab;
        bool IsRequestCurrent() =>
            ReferenceEquals(_activeEditorPane, requestPane) &&
            ReferenceEquals(EditorTextBox, requestEditor) &&
            ReferenceEquals(requestPane.ActiveTab, requestTab) &&
            ReferenceEquals(requestEditor.Document, doc) &&
            _insightDocVersion == scanVersion &&
            requestEditor.TextArea.Caret.Offset == offset;
        var editorSnapshot = EditorTextSnapshotStore.GetSnapshot(doc);
        var sourceVersion = editorSnapshot.Version;
        var fileKey = ActiveEditorTab?.Path ?? "untitled";
        var languageExtension = CurrentLanguageExtension;
        var lspForFile = ResolveLspExtensionForFile(_currentFilePath);
        var hasConfiguredLspForCompletion = lspForFile?.HasLsp == true;
        var isLspPrimaryForCompletion = LspEnabled && LspCompletionEnabled && hasConfiguredLspForCompletion && lspForFile!.HasLsp && ResolveLspConfigurationForFile(_currentFilePath) is { } cfgForCompletion && _lspManager.TryGetClient(GetWorkspaceRootForFile(_currentFilePath), cfgForCompletion) is { IsInitialized: true };
        var needsFullDocumentText = !isLspPrimaryForCompletion &&
            !PerformanceBudget.IsLargeFile(doc, LocalInsightSkipThreshold);
        var text = string.Empty;
        int wordStart;
        string prefix;
        if (needsFullDocumentText)
        {
            try
            {
                text = await editorSnapshot.GetTextAsync(EditorSession.RevisionCancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (scanVersion != _insightDocVersion) return;
            wordStart = InsightEngine.FindWordStart(text, offset);
            prefix = text[wordStart..offset];
        }
        else
        {
            var line = doc.GetLineByOffset(offset);
            var linePrefixLength = Math.Clamp(offset - line.Offset, 0, line.Length);
            var linePrefix = doc.GetTextAsMemory(line.Offset, linePrefixLength).Span;
            var wordStartInLine = linePrefix.Length;
            while (wordStartInLine > 0 && InsightEngine.IsWordChar(linePrefix[wordStartInLine - 1]))
                wordStartInLine--;
            var prefixLength = linePrefix.Length - wordStartInLine;
            wordStart = offset - prefixLength;
            prefix = doc.GetText(wordStart, prefixLength);
            if (scanVersion != _insightDocVersion) return;
        }

        if (doc.TextLength == 0)
        {
            CloseCompletionWindow();
            return;
        }

        var hasLspForCompletion = lspForFile is not null;
        if (prefix.Length == 0)
        {
            if (!hasLspForCompletion)
            {
                CloseCompletionWindow();
                return;
            }

            if (offset == 0 || offset > doc.TextLength)
            {
                CloseCompletionWindow();
                return;
            }

            var prevChar = doc.GetCharAt(offset - 1);
            if (prevChar == ' ' || prevChar == '\t' || prevChar == '\n' || prevChar == '\r')
            {
                CloseCompletionWindow();
                return;
            }
            var isTrigger = prevChar is '.' or '(' or '[' or '{' or ':' or '/' or '\\' or '"' or '\'' or '<' or ',';

            if (!isTrigger)
            {
                CloseCompletionWindow();
                return;
            }
        }

        List<InsightSuggestion> suggestions;
        if (!needsFullDocumentText)
        {
            suggestions = new List<InsightSuggestion>();
            KodoDiagnostics.LogDebug($"Local insight completion skipped (large file or LSP primary for {lspForFile?.Id})");
        }
        else
        {
            try
            {
                suggestions = await Task.Run(async () =>
                {
                    var gateway = InsightWorkerGateway.Default;
                    if (gateway.Healthy)
                    {
                        try
                        {
                            await gateway.ScanRoundAsync(_InsightEngine, fileKey, text, languageExtension, scanToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            KodoDiagnostics.LogDebug("Insight worker scan unavailable; running in-process scan.", ex);
                            _InsightEngine.ScanDocument(fileKey, text, languageExtension, scanToken);
                        }
                    }
                    else
                    {
                        _InsightEngine.ScanDocument(fileKey, text, languageExtension, scanToken);
                    }
                    return _InsightEngine.GetSuggestions(prefix, fileKey, languageExtension, text, offset);
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (lspCompletionSettingsRevision != _lspCompletionSettingsRevision || !IsRequestCurrent()) return;

        var completionCts = new CancellationTokenSource();
        var previousCompletionCts = _lspCompletionCts;
        _lspCompletionCts = completionCts;
        previousCompletionCts?.Cancel();
        try
        {
            var lspSuggestions = await GetLspCompletionSuggestionsAsync(_currentFilePath, offset, prefix, sourceVersion, completionCts.Token);
            if (!LspEnabled || !LspCompletionEnabled || lspCompletionSettingsRevision != _lspCompletionSettingsRevision)
                lspSuggestions = Array.Empty<InsightSuggestion>();
            if (lspSuggestions.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var merged = new List<InsightSuggestion>(Math.Min(25, lspSuggestions.Count + suggestions.Count));
                foreach (var suggestion in lspSuggestions)
                    if (seen.Add(suggestion.Text)) merged.Add(suggestion);
                foreach (var suggestion in suggestions)
                    if (seen.Add(suggestion.Text)) merged.Add(suggestion);
                suggestions = merged;
                suggestions = suggestions.OrderByDescending(s => s.Priority).ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
            }
            else if (isLspPrimaryForCompletion)
            {
                KodoDiagnostics.LogDebug($"LSP completion: no results for prefix '{prefix}' at offset {offset}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP completion merge failed", ex); }
        finally
        {
            if (ReferenceEquals(_lspCompletionCts, completionCts)) _lspCompletionCts = null;
            completionCts.Dispose();
        }

        if (lspCompletionSettingsRevision != _lspCompletionSettingsRevision || !IsRequestCurrent()) return;
        if (EditorTextBox?.TextArea is null) return;
        if (fileKey != "untitled" && !FileSystemPaths.Equals(_currentFilePath, fileKey)) return;

        InsightSuggestion.PanelForeground = PrimaryTextBrush;
        InsightSuggestion.MutedForeground = MutedTextBrush;

        if (suggestions.Count == 0)
        {
            CloseCompletionWindow();
            return;
        }

        var suggestionsText = string.Join(",", suggestions.Select(s => s.Text));
        if (_completionWindow is null)
        {
            _completionWindow = CreateCompletionWindow();
            _completionEditor = EditorTextBox;
            _completionWindow.StartOffset = wordStart;
            foreach (var suggestion in suggestions)
                _completionWindow.CompletionList.CompletionData.Add(suggestion);
            _completionWindow.Show();
            _lastSuggestionsText = suggestionsText;
        }
        else if (_lastSuggestionsText != suggestionsText)
        {
            _completionWindow.StartOffset = wordStart;
            _completionWindow.CompletionList.CompletionData.Clear();
            foreach (var suggestion in suggestions)
                _completionWindow.CompletionList.CompletionData.Add(suggestion);
            _lastSuggestionsText = suggestionsText;
        }
        else
        {
            _completionWindow.StartOffset = wordStart;
        }
    }

    private void CloseCompletionWindow()
    {
        _completionWindow?.Close();
        _completionWindow = null;
        _completionEditor = null;
    }

    private readonly object _deadCodeAnalysisCacheLock = new();
    private long _cachedDeadCodeAnalysisVersion;
    private string? _cachedDeadCodeAnalysisPath;
    private string? _cachedDeadCodeAnalysisExtension;
    private string? _cachedDeadCodeAnalysisFolder;
    private string? _cachedDeadCodeAnalysisText;
    private List<DeadCodeSpan>? _cachedDeadCodeAnalysisSpans;

    private async Task UpdateDeadCodeHighlightingAsync(string? textSnapshot = null)
    {
        var scanToken = RenewAnalysisCancellation(ref _deadCodeAnalysisCancellation).Token;
        if (!IsInsightEnabled || !IsInsightDeadCodeEnabled ||
            EditorTextBox?.Document is null ||
            ActiveEditorTab is null || ActiveEditorTab.IsUntitled ||
            IsPlainTextFile(_currentFilePath) ||
            HasNoFileExtension(_currentFilePath) ||
            IsInsightBlacklisted(_currentFilePath) ||
            IsErrorDeadCodeBlacklisted(_currentFilePath))
        {
            ClearDeadCodeHighlighting();
            HideDiagnosticPopup();
            return;
        }

        if (IsLspPrimaryForFile(_currentFilePath))
        {
            ClearDeadCodeHighlighting();
            HideDiagnosticPopup();
            return;
        }

        if (PerformanceBudget.IsLargeFile(EditorTextBox.Document))
        {
            ClearDeadCodeHighlighting();
            HideDiagnosticPopup();
            return;
        }
        var document = EditorTextBox.Document;
        var languageExtension = CurrentLanguageExtension;
        var folderPath = _currentFolderPath;
        var filePath = _currentFilePath;
        var scanVersion = _insightDocVersion;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            scanToken, EditorSession.RevisionCancellationToken);
        var analysisToken = linkedCancellation.Token;
        var scanExtension = languageExtension is null ? string.Empty : $"{languageExtension.Id}\u001F{languageExtension.Version}";
        var signature = ComputeDeadCodeSignature(scanVersion, filePath, scanExtension, folderPath);
        if (signature == _deadCodeAppliedSignature) return;

        List<DeadCodeSpan> rawSpans;
        List<DeadCodeSpan>? cachedSpans;
        string? cachedText;
        lock (_deadCodeAnalysisCacheLock)
        {
            if (_cachedDeadCodeAnalysisVersion == scanVersion &&
                FileSystemPaths.Equals(_cachedDeadCodeAnalysisPath, filePath) &&
                string.Equals(_cachedDeadCodeAnalysisExtension, scanExtension, StringComparison.Ordinal) &&
                FileSystemPaths.Equals(_cachedDeadCodeAnalysisFolder, folderPath))
            {
                cachedSpans = _cachedDeadCodeAnalysisSpans is null ? null : new List<DeadCodeSpan>(_cachedDeadCodeAnalysisSpans);
                cachedText = _cachedDeadCodeAnalysisText;
            }
            else
            {
                cachedSpans = null;
                cachedText = null;
            }
        }

        string? documentText = cachedText;
        if (cachedSpans is not null)
        {
            rawSpans = cachedSpans;
            if (documentText is null)
            {
                try
                {
                    documentText = await EditorTextSnapshotStore.GetSnapshot(document)
                        .GetTextAsync(analysisToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        else
        {
            try
            {
                documentText = textSnapshot ?? await EditorTextSnapshotStore.GetSnapshot(document)
                    .GetTextAsync(analysisToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                rawSpans = await InsightWorkerGateway.Default.FindDeadCodeAsync(_InsightEngine, documentText, languageExtension, folderPath, filePath, analysisToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (scanVersion != _insightDocVersion) return;
            if (EditorTextBox?.Document is null) return;
            lock (_deadCodeAnalysisCacheLock)
            {
                if (scanVersion == _insightDocVersion && FileSystemPaths.Equals(filePath, _currentFilePath))
                {
                    _cachedDeadCodeAnalysisVersion = scanVersion;
                    _cachedDeadCodeAnalysisPath = filePath;
                    _cachedDeadCodeAnalysisExtension = scanExtension;
                    _cachedDeadCodeAnalysisFolder = folderPath;
                    _cachedDeadCodeAnalysisText = documentText;
                    _cachedDeadCodeAnalysisSpans = new List<DeadCodeSpan>(rawSpans);
                }
            }
        }

        var dismissedDiagnostics = new HashSet<string>(_dismissedDiagnostics, StringComparer.Ordinal);
        var filtered = await Task.Run(() =>
        {
            analysisToken.ThrowIfCancellationRequested();
            var spans = FilterDismissedDeadCodeSpans(rawSpans, documentText, filePath, dismissedDiagnostics, analysisToken);
            analysisToken.ThrowIfCancellationRequested();
            var index = spans.Count == 0
                ? SpanOverlapIndex.Empty
                : SpanOverlapIndex.Build(spans, span => span.StartOffset, span => (long)span.StartOffset + span.Length + 1);
            return (Spans: spans, Index: index);
        }, analysisToken);
        if (scanVersion != _insightDocVersion || signature != ComputeDeadCodeSignature(scanVersion, _currentFilePath, scanExtension, _currentFolderPath) ||
            !ReferenceEquals(EditorTextBox?.Document, document)) return;
        var spans = filtered.Spans;
        var spanIndex = filtered.Index;
        _deadCodeHighlightRenderer.SetSpans(spans, spanIndex);
        _deadCodeTextBrightener.SetSpans(spans);
        _errorHighlightRenderer.SetDeadCodeSpans(spans, spanIndex);
        _errorTextDarkener.SetSpans(_errorHighlightRenderer.Spans, _errorHighlightRenderer.SpanIndex, spans, spanIndex);
        var otherEditor = ReferenceEquals(EditorTextBox, SecondaryEditorTextBox)
            ? PrimaryEditorTextBox
            : SecondaryEditorTextBox;
        if (otherEditor is not null && ReferenceEquals(otherEditor.Document, document))
        {
            var otherDeadRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryDeadCodeHighlightRenderer
                : _primaryDeadCodeHighlightRenderer;
            var otherDeadBrightener = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryDeadCodeTextBrightener
                : _primaryDeadCodeTextBrightener;
            var otherErrorRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryErrorHighlightRenderer
                : _primaryErrorHighlightRenderer;
            var otherErrorDarkener = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryErrorTextDarkener
                : _primaryErrorTextDarkener;
            otherDeadRenderer.SetSpans(spans, spanIndex);
            otherDeadBrightener.SetSpans(spans);
            otherErrorRenderer.SetDeadCodeSpans(spans, spanIndex);
            otherErrorDarkener.SetSpans(otherErrorRenderer.Spans, otherErrorRenderer.SpanIndex, spans, spanIndex);
            RedrawVisibleEditorLines(otherEditor);
            otherEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        if (spans.Count != rawSpans.Count)
            HideDiagnosticPopup();
        RedrawVisibleEditorLines();
        RefreshStatusBarDiagnostics();
        _deadCodeAppliedSignature = signature;
    }

    private static List<DeadCodeSpan> FilterDismissedDeadCodeSpans(
        List<DeadCodeSpan> spans,
        string text,
        string? filePath,
        HashSet<string> dismissedDiagnostics,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dismissedDiagnostics.Count == 0 || spans.Count == 0) return spans;
        var lineStarts = LspPath.GetLineStarts(text);
        var lineCache = new Dictionary<int, string>();
        var filtered = new List<DeadCodeSpan>(spans.Count);
        foreach (var span in spans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineText = GetDiagnosticLineText(text, lineStarts, span.StartOffset, lineCache);
            if (dismissedDiagnostics.Contains(GetDiagnosticSignature(filePath, lineText, span.Reason))) continue;
            filtered.Add(span);
        }
        return filtered;
    }

    private CompletionWindow CreateCompletionWindow()
    {
        var window = new CompletionWindow(EditorTextBox.TextArea)
        {
            MaxHeight = InsightRowHeight * InsightVisibleRows
                + InsightListVerticalPadding + InsightBorderThickness,
            MaxWidth = 720,
            Width = 600,
            MinWidth = 420,
            WindowManagerAddShadowHint = true,
        };

        var panelBrush = CardBrush;
        window.CompletionList.Background = panelBrush;
        window.CompletionList.BorderBrush = SurfaceBorderBrush;
        window.CompletionList.BorderThickness = new Thickness(1);
        window.CompletionList.FontFamily = EditorTextBox.FontFamily;
        window.CompletionList.HorizontalAlignment = HorizontalAlignment.Stretch;

        var panelCornerStyle = new Style(x => x.OfType<CompletionList>().Template().OfType<Border>());
        panelCornerStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, KodoDesignTokens.ControlRadius));
        panelCornerStyle.Setters.Add(new Setter(Border.BackgroundProperty, panelBrush));
        panelCornerStyle.Setters.Add(new Setter(Border.BoxShadowProperty, new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 8, Blur = 28, Spread = 0, Color = Color.FromArgb(38, 0, 0, 0) },
            new BoxShadow[] { new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 10, Spread = 0, Color = Color.FromArgb(20, 0, 0, 0) } })));
        window.Styles.Add(panelCornerStyle);

        if (window.CompletionList.ListBox is { } listBox)
        {
            listBox.Background = panelBrush;
            listBox.BorderThickness = new Thickness(0);
            listBox.Padding = new Thickness(6, 4);
            listBox.Margin = new Thickness(0);
            listBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            listBox.ClipToBounds = true;
        }

        var noTransitionStyle = new Style(x => x.OfType<ListBoxItem>());
        noTransitionStyle.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions()));
        window.Styles.Add(noTransitionStyle);

        var baseRowStyle = new Style(x => x.OfType<ListBoxItem>());
        baseRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, Brushes.Transparent));
        baseRowStyle.Setters.Add(new Setter(Avalonia.Controls.ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        baseRowStyle.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0, 1)));
        window.Styles.Add(baseRowStyle);

        var itemCornerStyle = new Style(x => x.OfType<ListBoxItem>().Template().OfType<Border>());
        itemCornerStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, KodoDesignTokens.ControlRadius));
        window.Styles.Add(itemCornerStyle);

        var accentTint = AccentBrush.ToImmutable() is ISolidColorBrush accentSolid
            ? new SolidColorBrush(accentSolid.Color, 0.16)
            : AccentBrush;

        var selectedRowStyle = new Style(x => x.OfType<ListBoxItem>().Class(":selected"));
        selectedRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, accentTint));
        window.Styles.Add(selectedRowStyle);

        var selectedHoverStyle = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pointerover"));
        selectedHoverStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, accentTint));
        window.Styles.Add(selectedHoverStyle);

        var hoverRowStyle = new Style(x => x.OfType<ListBoxItem>().Class(":pointerover"));
        hoverRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, ButtonHoverBrush));
        window.Styles.Add(hoverRowStyle);

        var rowPaddingStyle = new Style(x => x.OfType<ListBoxItem>());
        rowPaddingStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(10, 7)));
        rowPaddingStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.MinHeightProperty, 32d));
        window.Styles.Add(rowPaddingStyle);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window))
            {
                _completionWindow = null;
                _completionEditor = null;
            }
        };
        return window;
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

}
