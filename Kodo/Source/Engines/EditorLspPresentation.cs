// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
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
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Avalonia.Threading;
using Kodo.Editor;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{
    private sealed class LspPresentationContext
    {
        private readonly EditorTextSnapshot _snapshot;
        private const int MaximumInlineLineMapLength = 16 * 1024;
        private const int MaximumInlineLineMapTotalLength = 64 * 1024;
        private const int LineMapBufferLines = 40;

        public LspPresentationContext(EditorPaneState pane, TextEditor editor, EditorTab tab, TextDocument document, string filePath, long version, int caret, int visibleStartLine, int visibleEndLine, LspClient? client, bool includeCaretPosition, CancellationToken cancellationToken)
        {
            Pane = pane;
            Editor = editor;
            Tab = tab;
            Document = document;
            DocumentVersion = document.Version;
            _snapshot = EditorTextSnapshotStore.GetSnapshot(document);
            CancellationToken = cancellationToken;
            LineCount = document.LineCount;
            TextLength = document.TextLength;
            FilePath = filePath;
            Version = version;
            Caret = caret;
            VisibleStartLine = visibleStartLine;
            VisibleEndLine = visibleEndLine;
            Client = client;
            PositionEncoding = LspPath.NormalizePositionEncoding(client?.PositionEncoding);
            CaretPosition = includeCaretPosition
                ? LspPath.OffsetToLspPosition(document, caret, PositionEncoding)
                : default;

            LineMapStartLine = Math.Max(0, visibleStartLine - LineMapBufferLines);
            var lineMapEndLine = Math.Min(document.LineCount, visibleEndLine + LineMapBufferLines);
            var lineMapCount = Math.Max(0, lineMapEndLine - LineMapStartLine);
            VisibleLineOffsets = new int[lineMapCount];
            VisibleLineLengths = new int[lineMapCount];
            VisibleLineTexts = PositionEncoding == "utf-16" ? null : new string?[lineMapCount];
            var remainingInlineTextBudget = MaximumInlineLineMapTotalLength;
            var cacheVisibleLineText = VisibleLineTexts is not null && TextLength <= 500_000 && lineMapCount <= 120;
            for (var index = 0; index < lineMapCount; index++)
            {
                var line = document.GetLineByNumber(LineMapStartLine + index + 1);
                VisibleLineOffsets[index] = line.Offset;
                VisibleLineLengths[index] = line.Length;
                if (cacheVisibleLineText && VisibleLineTexts is not null && line.Length <= MaximumInlineLineMapLength && line.Length <= remainingInlineTextBudget)
                {
                    VisibleLineTexts[index] = EditorLineTextCache.GetText(document, line);
                    remainingInlineTextBudget -= line.Length;
                }
            }
        }

        public TextDocument Document { get; }
        public ITextSourceVersion DocumentVersion { get; }
        public EditorPaneState Pane { get; }
        public TextEditor Editor { get; }
        public EditorTab Tab { get; }
        public string FilePath { get; }
        public long Version { get; }
        public int Caret { get; }
        public int VisibleStartLine { get; }
        public int VisibleEndLine { get; }
        public LspClient? Client { get; }
        public CancellationToken CancellationToken { get; }
        public int TextLength { get; }
        public string PositionEncoding { get; }
        public int[] VisibleLineOffsets { get; }
        public int[] VisibleLineLengths { get; }
        public string?[]? VisibleLineTexts { get; }
        public int LineMapStartLine { get; }

        public int LineCount { get; }
        public (int Line, int Character) CaretPosition { get; }

        public string GetVisibleLineText(int index)
        {
            if (index < 0 || index >= VisibleLineOffsets.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return VisibleLineTexts?[index] ?? _snapshot.Source.GetText(VisibleLineOffsets[index], VisibleLineLengths[index]);
        }

        public string GetLineText(int offset, int length) => _snapshot.Source.GetText(offset, length);

        public Task<int[]> GetLineStartsAsync() => _snapshot.GetLineStartsAsync(CancellationToken);

        public Task<string> GetDocumentTextAsync() => _snapshot.GetTextAsync(CancellationToken);
    }

    private sealed class LspPanePresentationState
    {
        public string? FoldedPath { get; set; }
        public ITextSourceVersion? FoldedVersion { get; set; }
        public bool FoldingDeferred { get; set; }
        public string? PresentedPath { get; set; }
        public ITextSourceVersion? PresentedVersion { get; set; }
        public LspClient? PresentedClient { get; set; }
        public int PresentedStartLine { get; set; } = -1;
        public int PresentedEndLine { get; set; } = -1;
        public int PresentedCaret { get; set; } = -1;
    }

    private readonly Dictionary<Guid, LspPanePresentationState> _lspPanePresentationStates = new();

    private LspPanePresentationState GetLspPanePresentationState(EditorPaneState pane)
    {
        if (!_lspPanePresentationStates.TryGetValue(pane.Id, out var state))
        {
            state = new LspPanePresentationState();
            _lspPanePresentationStates.Add(pane.Id, state);
        }
        return state;
    }

    private void InvalidateLspPanePresentation(EditorPaneState pane)
    {
        var state = GetLspPanePresentationState(pane);
        state.FoldedPath = null;
        state.FoldedVersion = null;
        state.FoldingDeferred = false;
        state.PresentedPath = null;
        state.PresentedVersion = null;
        state.PresentedClient = null;
        state.PresentedStartLine = -1;
        state.PresentedEndLine = -1;
        state.PresentedCaret = -1;
    }

    private void InvalidateAllLspPanePresentations()
    {
        InvalidateLspPanePresentation(_editorPane);
        InvalidateLspPanePresentation(_secondaryEditorPane);
    }

    private bool LspPresentationContextIsCurrent(LspPresentationContext ctx, bool lspEnabled) =>
        lspEnabled &&
        ReferenceEquals(ctx.Pane.ActiveTab, ctx.Tab) &&
        ReferenceEquals(ctx.Editor.Document, ctx.Document) &&
        ReferenceEquals(ctx.Document.Version, ctx.DocumentVersion) &&
        FileSystemPaths.Equals(ctx.FilePath, ctx.Tab.IsUntitled ? null : ctx.Tab.Path);

    private static (int StartLine, int EndLine) GetEditorVisibleLines(TextEditor editor)
    {
        var document = editor.Document;
        var textView = editor.TextArea.TextView;
        if (textView.VisualLinesValid && textView.VisualLines.Count > 0)
            return (textView.VisualLines[0].FirstDocumentLine.LineNumber - 1,
                textView.VisualLines[^1].LastDocumentLine.LineNumber);

        var line = document.GetLineByOffset(Math.Clamp(editor.TextArea.Caret.Offset, 0, document.TextLength)).LineNumber - 1;
        return (line, Math.Min(document.LineCount, line + 1));
    }

    private static bool LspMethodUnavailable(LspPresentationContext ctx, string method) =>
        ctx.Client is null || !ctx.Client.Supports(method);

    private async Task UpdateLspSignatureHelpAsync(LspPresentationContext ctx)
    {
        if (!ReferenceEquals(ctx.Editor, EditorTextBox)) return;
        if (!LspSignatureHelpEnabled)
        {
            ClearLspSignatureHelpTooltip();
            return;
        }
        if (EditorTextBox?.Document is null || string.IsNullOrWhiteSpace(ctx.FilePath)) return;
        var (positionLine, positionCharacter) = ctx.CaretPosition;
        var result = await GetLspSignatureHelpAsync(ctx.FilePath, positionLine, positionCharacter, ctx.CancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Object || !result.Value.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array || signatures.GetArrayLength() == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(ctx.Editor, EditorTextBox) && LspPresentationContextIsCurrent(ctx, LspEnabled) && EditorTextBox?.TextArea.Caret.Offset == ctx.Caret)
                    ClearLspSignatureHelpTooltip();
            });
            return;
        }
        var signature = signatures[0];
        var label = signature.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(label))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(ctx.Editor, EditorTextBox) && LspPresentationContextIsCurrent(ctx, LspEnabled) && EditorTextBox?.TextArea.Caret.Offset == ctx.Caret)
                    ClearLspSignatureHelpTooltip();
            });
            return;
        }
        var documentation = signature.TryGetProperty("documentation", out var documentationEl) ? documentationEl.ToString() : "";
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!LspSignatureHelpEnabled || !ReferenceEquals(ctx.Editor, EditorTextBox) || !LspPresentationContextIsCurrent(ctx, LspEnabled) || EditorTextBox?.Document is null || EditorTextBox.TextArea.Caret.Offset != ctx.Caret) return;
            _lspSignatureHelpTooltip = string.IsNullOrWhiteSpace(documentation) ? label : $"{label}\n\n{documentation}";
            ToolTip.SetTip(EditorTextBox.TextArea.TextView, _lspSignatureHelpTooltip);
            ToolTip.SetShowDelay(EditorTextBox.TextArea.TextView, 80);
        });
    }

    private bool _lspDocumentRefreshPending = true;
    private bool _lspRefreshRunning;
    private bool _lspViewportRefreshPending;
    private int _lspViewportRequestGeneration;
    private LspClient? _lastPresentationClient;
    private LspClient? _lspCaretFeatureClient;
    private string? _lspCaretFeaturePath;
    private long _lspCaretFeatureVersion = -1;
    private int _lspCaretFeatureOffset = -1;
    private int _lspCaretFeatureRequestFailures;
    private int _lspDocumentRefreshFailures;
    private string? _lspLastViewportPath;
    private int _lspLastViewportStart = -1;
    private int _lspLastViewportEnd = -1;
    private int _lspViewportRefreshFailures;
    private bool _lspOtherPaneRefreshRunning;
    private bool _lspOtherPaneRefreshPending;

    private bool _lspDiagnosticsSeenForFile;

    private void ScheduleLspViewportRetry()
    {
        if (!IsActive || !_lspViewportRefreshPending || _lspRefreshDebounceTimer.IsEnabled) return;
        var retry = Math.Clamp(_lspViewportRefreshFailures, 1, 4);
        _lspRefreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(5_000, 500 * (1 << (retry - 1))));
        _lspRefreshDebounceTimer.Start();
    }

    private void ResetLspFoldingState()
    {
        InvalidateLspPanePresentation(_activeEditorPane);
        _lspDiagnosticsSeenForFile = HasStoredLspDiagnosticsForFile(_currentFilePath);
    }

    private async Task ClearLspFoldingsAsync()
    {
        var manager = _lspFoldingManager;
        if (manager is null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(_lspFoldingManager, manager))
                manager.UpdateFoldings(Array.Empty<AvaloniaEdit.Folding.NewFolding>(), 0);
        }, DispatcherPriority.Send);
    }

    private const int LspDocumentRefreshMaxRetries = 6;

    private void QueueLspViewportRefresh()
    {
        if (!LspEnabled || !IsActive) return;
        _lspViewportRefreshFailures = 0;
        _lspViewportRefreshPending = true;
        _lspViewportRequestGeneration++;
        _lspRefreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(120);
        _lspRefreshDebounceTimer.Stop();
        _lspRefreshDebounceTimer.Start();
    }

    private async Task RefreshOtherPaneLspPresentationAsync()
    {
        if (!IsSplitScreenVisible)
        {
            _lspOtherPaneRefreshPending = false;
            return;
        }

        if (_lspOtherPaneRefreshRunning)
        {
            _lspOtherPaneRefreshPending = true;
            return;
        }

        var pane = ReferenceEquals(_activeEditorPane, _editorPane) ? _secondaryEditorPane : _editorPane;
        var editor = GetEditorForPane(pane);
        var tab = pane.ActiveTab;
        if (tab is null || editor.Document is not { } document || tab.IsUntitled || string.IsNullOrWhiteSpace(tab.Path))
            return;

        var path = tab.Path;
        var configuration = ResolveLspConfigurationForFile(path);
        if (configuration is null)
            return;
        var client = _lspManager.TryGetClient(GetWorkspaceRootForFile(path), configuration);
        if (client is not { IsInitialized: true, IsStarted: true })
            return;

        var state = GetLspPanePresentationState(pane);
        var visible = GetEditorVisibleLines(editor);
        var caret = editor.TextArea.Caret.Offset;
        var refreshDocument = !ReferenceEquals(state.PresentedVersion, document.Version) ||
            !FileSystemPaths.Equals(state.PresentedPath, path) || !ReferenceEquals(state.PresentedClient, client) ||
            state.FoldingDeferred;
        var refreshViewport = refreshDocument || visible.StartLine != state.PresentedStartLine ||
            visible.EndLine != state.PresentedEndLine;
        var refreshCaretFeatures = refreshDocument || caret != state.PresentedCaret;
        if (!refreshDocument && !refreshViewport && !refreshCaretFeatures)
            return;

        using var cancellation = new CancellationTokenSource();
        var ctx = new LspPresentationContext(
            pane, editor, tab, document, path, 0, caret, visible.StartLine, visible.EndLine,
            client, refreshCaretFeatures, cancellation.Token);
        _lspOtherPaneRefreshRunning = true;
        try
        {
            var documentFeatures = new List<Task<bool>>();
            var viewportFeatures = new List<Task<bool>>();
            if (refreshDocument && LspFoldingEnabled)
                documentFeatures.Add(UpdateLspFoldingAsync(ctx));
            if (refreshDocument || refreshViewport)
                viewportFeatures.Add(UpdateLspInlayHintsAsync(ctx));
            var hasSemanticRange = client.Supports("textDocument/semanticTokens/range");
            if (refreshDocument || (hasSemanticRange && refreshViewport))
                viewportFeatures.Add(UpdateLspSemanticTokensAsync(ctx, hasSemanticRange));
            if (LspDocumentHighlightsEnabled && refreshCaretFeatures)
                await UpdateLspDocumentHighlightsAsync(ctx);

            await Task.WhenAll(documentFeatures.Cast<Task>().Concat(viewportFeatures));
            var documentApplied = true;
            foreach (var feature in documentFeatures)
                documentApplied &= await feature.ConfigureAwait(false);
            var viewportApplied = true;
            foreach (var feature in viewportFeatures)
                viewportApplied &= await feature.ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!LspPresentationContextIsCurrent(ctx, LspEnabled) || !viewportApplied ||
                    (!documentApplied && !state.FoldingDeferred))
                    return;

                state.PresentedPath = path;
                state.PresentedVersion = ctx.DocumentVersion;
                state.PresentedClient = client;
                state.PresentedStartLine = visible.StartLine;
                state.PresentedEndLine = visible.EndLine;
                state.PresentedCaret = caret;
                state.FoldingDeferred = !documentApplied;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Other-pane LSP presentation refresh failed.", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _lspOtherPaneRefreshRunning = false;
                if (!_lspOtherPaneRefreshPending || !IsSplitScreenVisible || !LspEnabled || !IsActive)
                    return;

                _lspOtherPaneRefreshPending = false;
                _lspRefreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(120);
                if (!_lspRefreshDebounceTimer.IsEnabled)
                    _lspRefreshDebounceTimer.Start();
            });
        }
    }

    private async void LspRefreshDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _lspRefreshDebounceTimer.Stop();
        if (!LspEnabled || !IsActive) return;
        var otherPaneRefresh = RefreshOtherPaneLspPresentationAsync();
        if (EditorTextBox?.Document is null || string.IsNullOrWhiteSpace(_currentFilePath) || _lspRefreshRunning)
        {
            await otherPaneRefresh;
            return;
        }
        var configuration = ResolveLspConfigurationForFile(_currentFilePath);
        if (configuration is null)
        {
            await otherPaneRefresh;
            return;
        }
        var client = _lspManager.TryGetClient(GetWorkspaceRootForFile(_currentFilePath), configuration);
        if (client is not { IsInitialized: true, IsStarted: true })
        {
            await otherPaneRefresh;
            return;
        }
        var document = EditorTextBox.Document;
        var revision = _insightDocVersion;
        var caret = EditorTextBox.TextArea.Caret.Offset;
        var path = _currentFilePath;
        var refreshDocument = _lspDocumentRefreshPending || !ReferenceEquals(client, _lastPresentationClient);
        var textView = EditorTextBox.TextArea.TextView;
        var visibleStartLine = document.GetLineByOffset(caret).LineNumber - 1;
        var visibleEndLine = Math.Min(document.LineCount, visibleStartLine + 1);
        if (textView.VisualLinesValid && textView.VisualLines.Count > 0)
        {
            visibleStartLine = textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
            visibleEndLine = textView.VisualLines[^1].LastDocumentLine.LineNumber;
        }
        var refreshViewport = _lspViewportRefreshPending || !FileSystemPaths.Equals(path, _lspLastViewportPath) ||
            visibleStartLine != _lspLastViewportStart || visibleEndLine != _lspLastViewportEnd;
        var hasCaretFeatures = LspSignatureHelpEnabled || LspDocumentHighlightsEnabled;
        var refreshCaretFeatures = hasCaretFeatures && (refreshDocument || !ReferenceEquals(client, _lspCaretFeatureClient) ||
            !FileSystemPaths.Equals(path, _lspCaretFeaturePath) || revision != _lspCaretFeatureVersion || caret != _lspCaretFeatureOffset);
        var caretFeatureFailureCount = Volatile.Read(ref _lspCaretFeatureRequestFailures);
        var viewportRequestGeneration = _lspViewportRequestGeneration;
        if (_activeEditorPane.ActiveTab is not { } activePresentationTab) return;
        var presentationCancellation = CancellationTokenSource.CreateLinkedTokenSource(EditorSession.RevisionCancellationToken);
        _lspPresentationCancellation = presentationCancellation;
        var ctx = new LspPresentationContext(_activeEditorPane, EditorTextBox, activePresentationTab, document, path, revision, caret, visibleStartLine, visibleEndLine, client, refreshCaretFeatures, presentationCancellation.Token);
        _lspRefreshRunning = true;
        try
        {
            var requests = new List<Task>();
            if (LspSignatureHelpEnabled && refreshCaretFeatures)
                requests.Add(UpdateLspSignatureHelpAsync(ctx));
            var documentFeatures = new List<Task<bool>>();
            var viewportFeatures = new List<Task<bool>>();
            if (refreshDocument)
            {
                if (LspFoldingEnabled)
                    documentFeatures.Add(UpdateLspFoldingAsync(ctx));
            }
            if ((refreshDocument || refreshViewport) && LspInlayHintsEnabled)
                viewportFeatures.Add(UpdateLspInlayHintsAsync(ctx));
            var hasSemanticRange = client.Supports("textDocument/semanticTokens/range");
            if (LspSemanticHighlightingEnabled && (refreshDocument || (hasSemanticRange && refreshViewport)))
                viewportFeatures.Add(UpdateLspSemanticTokensAsync(ctx, hasSemanticRange));
            requests.AddRange(documentFeatures.Cast<Task>());
            requests.AddRange(viewportFeatures.Cast<Task>());
            if (LspDocumentHighlightsEnabled && refreshCaretFeatures)
                requests.Add(UpdateLspDocumentHighlightsAsync(ctx));
            await Task.WhenAll(requests);
            await otherPaneRefresh;

            if (refreshCaretFeatures && caretFeatureFailureCount == Volatile.Read(ref _lspCaretFeatureRequestFailures) &&
                revision == _insightDocVersion && caret == EditorTextBox.TextArea.Caret.Offset &&
                FileSystemPaths.Equals(path, _currentFilePath))
            {
                _lspCaretFeatureClient = client;
                _lspCaretFeaturePath = path;
                _lspCaretFeatureVersion = revision;
                _lspCaretFeatureOffset = caret;
            }

            var documentApplied = true;
            if (documentFeatures.Count > 0)
            {
                foreach (var feature in documentFeatures)
                    documentApplied &= await feature.ConfigureAwait(false);
            }

            var viewportApplied = true;
            foreach (var feature in viewportFeatures)
                viewportApplied &= await feature.ConfigureAwait(false);
            if (viewportApplied)
            {
                _lspViewportRefreshFailures = 0;
                _lspViewportRefreshPending = viewportRequestGeneration != _lspViewportRequestGeneration;
                _lspLastViewportPath = path;
                _lspLastViewportStart = visibleStartLine;
                _lspLastViewportEnd = visibleEndLine;
            }
            else
            {
                _lspViewportRefreshPending = true;
                _lspViewportRefreshFailures = Math.Min(4, _lspViewportRefreshFailures + 1);
            }

            if (documentApplied && revision == _insightDocVersion && FileSystemPaths.Equals(path, _currentFilePath))
            {
                _lspDocumentRefreshPending = false;
                _lspDocumentRefreshFailures = 0;
                _lastPresentationClient = client;
            }
            else if (GetLspPanePresentationState(_activeEditorPane).FoldingDeferred)
            {
                GetLspPanePresentationState(_activeEditorPane).FoldingDeferred = false;
                _lspDocumentRefreshPending = true;
                KodoDiagnostics.LogDebug("LSP folding deferred until diagnostics arrive");
            }
            else if (_lspDocumentRefreshFailures < LspDocumentRefreshMaxRetries)
            {
                _lspDocumentRefreshPending = true;
                _lspDocumentRefreshFailures++;
                KodoDiagnostics.LogDebug($"LSP document presentation incomplete, retry {_lspDocumentRefreshFailures}/{LspDocumentRefreshMaxRetries}");
                if (IsActive && !_lspRefreshDebounceTimer.IsEnabled)
                    _lspRefreshDebounceTimer.Start();
            }
            else
            {
                KodoDiagnostics.LogDebug("LSP document presentation gave up after repeated failures; waiting for the next edit or diagnostics publish");
                _lspDocumentRefreshPending = false;
            }
            ScheduleLspViewportRetry();
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP presentation refresh failed", ex); }
        finally
        {
            _lspRefreshRunning = false;
            if (ReferenceEquals(_lspPresentationCancellation, presentationCancellation)) _lspPresentationCancellation = null;
            presentationCancellation.Dispose();
            if (IsActive && (revision != _insightDocVersion || caret != EditorTextBox.TextArea.Caret.Offset || _lspViewportRefreshPending))
            {
                if (_lspViewportRefreshPending)
                    ScheduleLspViewportRetry();
                else
                    _lspRefreshDebounceTimer.Start();
            }
        }
    }

    private async Task<bool> UpdateLspFoldingAsync(LspPresentationContext ctx)
    {
        var paneState = GetLspPanePresentationState(ctx.Pane);
        paneState.FoldingDeferred = false;
        var editor = ctx.Editor;
        var manager = GetLspFoldingManagerForPane(ctx.Pane);
        if (!LspFoldingEnabled)
        {
            return true;
        }
        if (manager is null || string.IsNullOrWhiteSpace(ctx.FilePath) || editor.Document is null) return true;
        if (LspMethodUnavailable(ctx, "textDocument/foldingRange")) return true;
        if (ReferenceEquals(paneState.FoldedVersion, ctx.DocumentVersion) && FileSystemPaths.Equals(paneState.FoldedPath, ctx.FilePath)) return true;
        var diagnosticsSeen = ReferenceEquals(ctx.Pane, _activeEditorPane)
            ? _lspDiagnosticsSeenForFile
            : HasStoredLspDiagnosticsForFile(ctx.FilePath);
        if (!diagnosticsSeen)
        {
            paneState.FoldingDeferred = true;
            return false;
        }
        var result = await GetLspFoldingRangesAsync(ctx.FilePath, ctx.CancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return false;
        var ranges = new List<(int StartLine, int EndLine)>();
        var foldingIndex = 0;
        foreach (var item in result.Value.EnumerateArray())
        {
            if ((foldingIndex++ & 0x3ff) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!item.TryGetProperty("startLine", out var sl) || !item.TryGetProperty("endLine", out var el)) continue;
            if (sl.ValueKind != JsonValueKind.Number || el.ValueKind != JsonValueKind.Number) continue;
            if (!sl.TryGetInt32(out var startLine0) || !el.TryGetInt32(out var endLine0)) continue;
            if (startLine0 < 0 || endLine0 <= startLine0) continue;
            ranges.Add((startLine0, endLine0));
        }
        if (ranges.Count > LspMaxFoldingsApplied)
        {
            ranges.Sort(static (left, right) => left.StartLine.CompareTo(right.StartLine));
            KodoDiagnostics.LogDebug($"LSP folding ranges truncated from {ranges.Count} to {LspMaxFoldingsApplied} before editor mapping for {ctx.FilePath}");
            ranges.RemoveRange(LspMaxFoldingsApplied, ranges.Count - LspMaxFoldingsApplied);
        }
        var foldings = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!LspPresentationContextIsCurrent(ctx, LspEnabled) || editor.Document is not { } currentDocument)
                return null;
            var lastLine0 = currentDocument.LineCount - 1;
            var mapped = new List<AvaloniaEdit.Folding.NewFolding>(Math.Min(ranges.Count, LspMaxFoldingsApplied));
            foreach (var (startLine0, requestedEndLine0) in ranges)
            {
                if (startLine0 > lastLine0) continue;
                var endLine0 = Math.Min(requestedEndLine0, lastLine0);
                if (endLine0 <= startLine0) continue;
                var startLine = currentDocument.GetLineByNumber(startLine0 + 1);
                var endLine = currentDocument.GetLineByNumber(endLine0 + 1);
                var start = startLine.Offset;
                var end = endLine.Offset + endLine.Length;
                if (end > start) mapped.Add(new AvaloniaEdit.Folding.NewFolding(start, end));
            }
            return mapped;
        }, DispatcherPriority.Background);
        if (foldings is null) return false;
        var applied = false;
        if (foldings.Count > LspMaxFoldingsApplied)
        {
            KodoDiagnostics.LogDebug($"LSP folding ranges truncated from {foldings.Count} to {LspMaxFoldingsApplied} for {ctx.FilePath}");
            foldings.Sort(static (left, right) => left.StartOffset.CompareTo(right.StartOffset));
            foldings.RemoveRange(LspMaxFoldingsApplied, foldings.Count - LspMaxFoldingsApplied);
        }
        var orderedFoldings = foldings.OrderBy(f => f.StartOffset).ToArray();
        var updates = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (manager is null || !LspFoldingEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled) || editor.Document is not { } currentDocument)
                return null;
            return BuildLspFoldingUpdates(manager, currentDocument, orderedFoldings);
        }, DispatcherPriority.Background);

        if (updates is null || manager is null) return false;
        const int updateBatchSize = 8;
        for (var index = 0; index < updates.Count; index += updateBatchSize)
        {
            var start = index;
            var end = Math.Min(index + updateBatchSize, updates.Count);
            applied = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(GetLspFoldingManagerForPane(ctx.Pane), manager) || !LspFoldingEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled))
                    return false;

                for (var i = start; i < end; i++)
                    updates[i]();
                return true;
            }, DispatcherPriority.Background);
            if (!applied) return false;
        }

        TextEditor? otherEditor = null;
        AvaloniaEdit.Folding.FoldingManager? otherManager = null;
        var otherPaneHasSameDocument = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            otherEditor = ReferenceEquals(editor, SecondaryEditorTextBox)
                ? PrimaryEditorTextBox
                : SecondaryEditorTextBox;
            otherManager = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                ? _secondaryLspFoldingManager
                : _primaryLspFoldingManager;
            return otherEditor is not null && otherManager is not null &&
                   ReferenceEquals(otherEditor.Document, ctx.Document);
        }, DispatcherPriority.Background);
        if (otherPaneHasSameDocument && otherEditor is not null && otherManager is not null)
        {
            var otherUpdates = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!LspFoldingEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled) ||
                    !ReferenceEquals(otherEditor.Document, ctx.Document) ||
                    !ReferenceEquals(otherManager, ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                        ? _secondaryLspFoldingManager
                        : _primaryLspFoldingManager))
                    return null;
                return BuildLspFoldingUpdates(otherManager, ctx.Document, orderedFoldings);
            }, DispatcherPriority.Background);
            if (otherUpdates is null) return false;

            for (var index = 0; index < otherUpdates.Count; index += updateBatchSize)
            {
                var start = index;
                var end = Math.Min(index + updateBatchSize, otherUpdates.Count);
                applied = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!LspFoldingEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled) ||
                        !ReferenceEquals(otherEditor.Document, ctx.Document) ||
                        !ReferenceEquals(otherManager, ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                            ? _secondaryLspFoldingManager
                            : _primaryLspFoldingManager))
                        return false;

                    for (var i = start; i < end; i++)
                        otherUpdates[i]();
                    return true;
                }, DispatcherPriority.Background);
                if (!applied) return false;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(GetLspFoldingManagerForPane(ctx.Pane), manager) || !LspFoldingEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled)) return;
            paneState.FoldedPath = ctx.FilePath;
            paneState.FoldedVersion = ctx.DocumentVersion;
            applied = true;

            if (otherEditor is not null && otherManager is not null &&
                ReferenceEquals(otherEditor.Document, ctx.Document) &&
                ReferenceEquals(otherManager, GetLspFoldingManagerForPane(
                    ReferenceEquals(otherEditor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane)))
            {
                var otherPane = ReferenceEquals(otherEditor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane;
                var otherPaneState = GetLspPanePresentationState(otherPane);
                otherPaneState.FoldedPath = ctx.FilePath;
                otherPaneState.FoldedVersion = ctx.DocumentVersion;
                otherPaneState.FoldingDeferred = false;
            }
        }, DispatcherPriority.Background);
        return applied;
    }

    private AvaloniaEdit.Folding.FoldingManager? GetLspFoldingManagerForPane(EditorPaneState pane) =>
        ReferenceEquals(pane, _secondaryEditorPane) ? _secondaryLspFoldingManager : _primaryLspFoldingManager;

    private static List<Action> BuildLspFoldingUpdates(
        AvaloniaEdit.Folding.FoldingManager manager,
        TextDocument document,
        IReadOnlyList<AvaloniaEdit.Folding.NewFolding> orderedFoldings)
    {
        var changes = new List<Action>();
        var existing = manager.AllFoldings.ToArray();
        if (FoldingsMatchByLineRanges(document, existing, orderedFoldings))
            return changes;

        var existingIndex = 0;
        foreach (var folding in orderedFoldings)
        {
            while (existingIndex < existing.Length && folding.StartOffset > existing[existingIndex].StartOffset)
            {
                var stale = existing[existingIndex++];
                changes.Add(() => manager.RemoveFolding(stale));
            }

            if (existingIndex < existing.Length && folding.StartOffset == existing[existingIndex].StartOffset)
            {
                var section = existing[existingIndex++];
                changes.Add(() =>
                {
                    section.Length = folding.EndOffset - folding.StartOffset;
                    section.Title = folding.Name;
                    section.Tag = folding;
                });
            }
            else
            {
                changes.Add(() =>
                {
                    var section = manager.CreateFolding(folding.StartOffset, folding.EndOffset);
                    section.Title = folding.Name;
                    section.Tag = folding;
                });
            }
        }

        while (existingIndex < existing.Length)
        {
            var stale = existing[existingIndex++];
            changes.Add(() => manager.RemoveFolding(stale));
        }

        return changes;
    }

    private static bool FoldingsMatchByLineRanges(
        TextDocument document,
        IReadOnlyList<AvaloniaEdit.Folding.FoldingSection> existing,
        IReadOnlyList<AvaloniaEdit.Folding.NewFolding> requested)
    {
        if (existing.Count != requested.Count) return false;
        for (var index = 0; index < existing.Count; index++)
        {
            var current = existing[index];
            var next = requested[index];
            var currentStartLine = document.GetLineByOffset(Math.Clamp(current.StartOffset, 0, document.TextLength)).LineNumber;
            var nextStartLine = document.GetLineByOffset(Math.Clamp(next.StartOffset, 0, document.TextLength)).LineNumber;
            var currentEndLine = document.GetLineByOffset(Math.Clamp(current.EndOffset - 1, 0, document.TextLength)).LineNumber;
            var nextEndLine = document.GetLineByOffset(Math.Clamp(next.EndOffset - 1, 0, document.TextLength)).LineNumber;
            if (currentStartLine != nextStartLine || currentEndLine != nextEndLine) return false;
        }
        return true;
    }

    private async Task UpdateLspDocumentHighlightsAsync(LspPresentationContext ctx)
    {
        var editor = ctx.Editor;
        var renderer = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryLspHighlightRenderer
            : _primaryLspHighlightRenderer;
        if (!LspDocumentHighlightsEnabled)
        {
            if (renderer.HasMatches)
            {
                renderer.Clear();
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            }
            return;
        }
        if (editor.Document is null || string.IsNullOrWhiteSpace(ctx.FilePath)) return;
        var wasHighlighted = renderer.HasMatches;
        renderer.Clear();
        if (wasHighlighted) editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        var (line, character) = ctx.CaretPosition;
        var result = await GetLspDocumentHighlightsAsync(ctx.FilePath, line, character, ctx.CancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return;
        var lineStarts = await ctx.GetLineStartsAsync().ConfigureAwait(false);
        var matches = await EditorBackgroundWork.RunAsync(_ =>
        {
            var mappedMatches = new List<(int Offset, int Length)>();
            var lineTexts = new Dictionary<int, (int Start, int ContentLength, string Text)>();
            var highlightIndex = 0;
            foreach (var item in result.Value.EnumerateArray())
            {
                if ((highlightIndex++ & 0xff) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
                if (!item.TryGetProperty("range", out var range)) continue;
                if (!range.TryGetProperty("start", out var start) || !range.TryGetProperty("end", out var end)) continue;
                if (!TryReadLspPosition(start, out var startLine, out var startCharacter)) continue;
                if (!TryReadLspPosition(end, out var endLine, out var endCharacter)) continue;
                var matchStart = OffsetFromSnapshotPosition(startLine, startCharacter);
                var matchEnd = OffsetFromSnapshotPosition(endLine, endCharacter);
                if (matchEnd > matchStart) mappedMatches.Add((matchStart, matchEnd - matchStart));
            }
            mappedMatches.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
            return mappedMatches;

            int OffsetFromSnapshotPosition(int requestedLine, int requestedCharacter)
            {
                var safeLine = Math.Clamp(requestedLine, 0, Math.Max(0, ctx.LineCount - 1));
                if (safeLine >= lineStarts.Length) return ctx.TextLength;
                if (!lineTexts.TryGetValue(safeLine, out var lineInfo))
                {
                    var startOffset = lineStarts[safeLine];
                    var endOffset = safeLine + 1 < lineStarts.Length ? lineStarts[safeLine + 1] : ctx.TextLength;
                    var text = ctx.GetLineText(startOffset, Math.Max(0, endOffset - startOffset));
                    var contentLength = text.Length;
                    while (contentLength > 0 && text[contentLength - 1] is '\r' or '\n') contentLength--;
                    lineInfo = (startOffset, contentLength, text);
                    lineTexts.Add(safeLine, lineInfo);
                }

                var characterOffset = string.Equals(ctx.PositionEncoding, "utf-16", StringComparison.Ordinal)
                    ? Math.Clamp(requestedCharacter, 0, lineInfo.ContentLength)
                    : LspPath.Utf16UnitsForPosition(lineInfo.Text.AsSpan(0, lineInfo.ContentLength), requestedCharacter, ctx.PositionEncoding);
                return lineInfo.Start + characterOffset;
            }
        }, ctx.CancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!LspDocumentHighlightsEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled) || editor.Document is null || editor.TextArea.Caret.Offset != ctx.Caret) return;
            renderer.SetMatches(matches);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        });
    }

    private async Task<bool> UpdateLspInlayHintsAsync(LspPresentationContext ctx)
    {
        if (!LspEnabled || !LspInlayHintsEnabled)
        {
            ApplyLspInlayHints(ctx, Array.Empty<(int, string)>());
            return true;
        }
        if (ctx.Editor.Document is null || string.IsNullOrWhiteSpace(ctx.FilePath)) return true;
        if (LspMethodUnavailable(ctx, "textDocument/inlayHint")) return true;
        const int inlayBufferLines = 40;
        var startLine = Math.Max(0, ctx.VisibleStartLine - inlayBufferLines);
        var endLine = Math.Min(ctx.LineCount, ctx.VisibleEndLine + inlayBufferLines);
        var result = await GetLspInlayHintsAsync(ctx.FilePath, startLine, endLine, ctx.CancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return false;
        var hints = await EditorBackgroundWork.RunAsync(_ =>
        {
            var mappedHints = new List<(int Offset, string Label)>();
            var hintIndex = 0;
            foreach (var item in result.Value.EnumerateArray())
            {
                if ((hintIndex++ & 0x3ff) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
                if (!item.TryGetProperty("position", out var position) || !TryReadLspPosition(position, out var positionLine, out var positionCharacter)) continue;
                var label = item.TryGetProperty("label", out var labelEl) ? ReadInlayHintLabel(labelEl) : null;
                if (string.IsNullOrWhiteSpace(label)) continue;
                var lineMapIndex = positionLine - ctx.LineMapStartLine;
                if (lineMapIndex < 0 || lineMapIndex >= ctx.VisibleLineOffsets.Length) continue;
                var lineLength = ctx.VisibleLineLengths[lineMapIndex];
                var lineText = ctx.PositionEncoding == "utf-16" ? null : ctx.GetVisibleLineText(lineMapIndex);
                var column = ctx.PositionEncoding == "utf-16"
                    ? Math.Clamp(positionCharacter, 0, lineLength)
                    : LspPath.Utf16UnitsForPosition(lineText.AsSpan(), positionCharacter, ctx.PositionEncoding);
                var offset = ctx.VisibleLineOffsets[lineMapIndex] + column;
                if (offset <= ctx.TextLength) mappedHints.Add((offset, label));
            }
            mappedHints.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
            return mappedHints;
        }, ctx.CancellationToken).ConfigureAwait(false);
        var applied = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!LspInlayHintsEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled) || ctx.Editor.Document is null) return;
            ApplyLspInlayHints(ctx, hints, startLine, endLine);
            applied = true;
        });
        return applied;
    }

    private void ApplyLspInlayHints(
        LspPresentationContext ctx,
        IReadOnlyList<(int Offset, string Label)> hints,
        int? coveredStartLine = null,
        int? coveredEndLine = null)
    {
        var editor = ctx.Editor;
        var renderer = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryLspInlayHintRenderer
            : _primaryLspInlayHintRenderer;
        var changedRange = renderer.SetHints(hints);
        if (changedRange is { } range && editor.TextArea?.TextView is { } textView)
            textView.Redraw(range.Offset, range.Length);

        var otherEditor = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? PrimaryEditorTextBox
            : SecondaryEditorTextBox;
        if (coveredStartLine is null || coveredEndLine is null || otherEditor is null ||
            !ReferenceEquals(otherEditor.Document, ctx.Document))
            return;

        var otherVisible = GetEditorVisibleLines(otherEditor);
        if (otherVisible.StartLine < coveredStartLine.Value || otherVisible.EndLine > coveredEndLine.Value)
            return;

        var otherRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
            ? _secondaryLspInlayHintRenderer
            : _primaryLspInlayHintRenderer;
        var otherChangedRange = otherRenderer.SetHints(hints);
        if (otherChangedRange is { } otherRange)
            otherEditor.TextArea.TextView.Redraw(otherRange.Offset, otherRange.Length);
    }

    private static bool TryReadLspPosition(JsonElement position, out int line, out int character)
    {
        line = 0;
        character = 0;
        return position.ValueKind == JsonValueKind.Object &&
            position.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number &&
            position.TryGetProperty("character", out var characterEl) && characterEl.ValueKind == JsonValueKind.Number &&
            lineEl.TryGetInt32(out line) && characterEl.TryGetInt32(out character);
    }

    private static string? ReadInlayHintLabel(System.Text.Json.JsonElement labelEl)
    {
        if (labelEl.ValueKind == System.Text.Json.JsonValueKind.String) return labelEl.GetString();
        if (labelEl.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var builder = new System.Text.StringBuilder();
        foreach (var part in labelEl.EnumerateArray())
        {
            if (part.ValueKind == System.Text.Json.JsonValueKind.String) builder.Append(part.GetString());
            else if (part.ValueKind == System.Text.Json.JsonValueKind.Object && part.TryGetProperty("value", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                builder.Append(value.GetString());
        }
        var label = builder.ToString();
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    private async Task<bool> UpdateLspSemanticTokensAsync(LspPresentationContext ctx, bool useRange)
    {
        var editor = ctx.Editor;
        var renderer = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryLspSemanticTokenRenderer
            : _primaryLspSemanticTokenRenderer;
        if (!LspEnabled || !LspSemanticHighlightingEnabled)
        {
            if (renderer.HasTokens)
            {
                renderer.SetTokens(Array.Empty<(int, int, IBrush)>());
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            }
            return true;
        }
        if (editor.Document is null || string.IsNullOrWhiteSpace(ctx.FilePath)) return true;
        if (!useRange && PerformanceBudget.IsHugeFile(ctx.Document, 1_000_000))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!LspPresentationContextIsCurrent(ctx, LspEnabled) || !renderer.HasTokens) return;
                renderer.SetTokens(Array.Empty<(int, int, IBrush)>());
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            });
            return true;
        }
        if (LspMethodUnavailable(ctx, useRange ? "textDocument/semanticTokens/range" : "textDocument/semanticTokens/full")) return true;
        var result = useRange
            ? await GetLspSemanticTokensRangeAsync(ctx.FilePath, ctx.VisibleStartLine, ctx.VisibleEndLine, ctx.CancellationToken).ConfigureAwait(false)
            : await GetLspSemanticTokensAsync(ctx.FilePath, ctx.CancellationToken).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind != JsonValueKind.Object || !result.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;
        var dataLength = data.GetArrayLength();
        if (dataLength % 5 != 0) return false;
        var semanticDecodeWatch = System.Diagnostics.Stopwatch.StartNew();
        int[]? fullLineStarts = null;
        string? fullDocumentText = null;
        if (!useRange)
        {
            if (ctx.PositionEncoding != "utf-16")
            {
                fullDocumentText = await ctx.GetDocumentTextAsync().ConfigureAwait(false);
                fullLineStarts = await ctx.GetLineStartsAsync().ConfigureAwait(false);
            }
            else
            {
                fullLineStarts = await ctx.GetLineStartsAsync().ConfigureAwait(false);
            }
        }
        var tokens = await EditorBackgroundWork.RunAsync(_ =>
        {
            var tokens = new List<(int Offset, int Length, IBrush Brush)>(dataLength / 5);
            var textLength = ctx.TextLength;
            var legend = GetLspSemanticTokenTypes(ctx.FilePath);
            var palette = SemanticTokenPalette;
            var line = 0;
            var character = 0;
            var groupIndex = 0;
            var deltaLine = 0;
            var deltaCharacter = 0;
            var length = 0;
            var tokenType = 0;
            var previousOffset = -1;
            var tokensAreOrdered = true;
            var dataIndex = 0;
            foreach (var valueElement in data.EnumerateArray())
            {
                if ((dataIndex++ & 0x1fff) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
                if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetInt32(out var value)) return null;
                switch (groupIndex)
                {
                    case 0: deltaLine = value; break;
                    case 1: deltaCharacter = value; break;
                    case 2: length = value; break;
                    case 3: tokenType = value; break;
                }
                groupIndex++;
                if (groupIndex < 5) continue;
                groupIndex = 0;
                line += deltaLine;
                character = deltaLine == 0 ? character + deltaCharacter : deltaCharacter;
                var endCharacter = (int)Math.Min(int.MaxValue, (long)character + length);
                int offset;
                int tokenLength;
                if (useRange)
                {
                    var visibleIndex = line - ctx.LineMapStartLine;
                    if (visibleIndex < 0 || visibleIndex >= ctx.VisibleLineOffsets.Length) continue;
                    var lineLength = ctx.VisibleLineLengths[visibleIndex];
                    var lineText = ctx.PositionEncoding == "utf-16" ? null : ctx.GetVisibleLineText(visibleIndex);
                    var startColumn = ctx.PositionEncoding == "utf-16"
                        ? Math.Clamp(character, 0, lineLength)
                        : LspPath.Utf16UnitsForPosition(lineText.AsSpan(), character, ctx.PositionEncoding);
                    var endColumn = ctx.PositionEncoding == "utf-16"
                        ? Math.Clamp(endCharacter, 0, lineLength)
                        : LspPath.Utf16UnitsForPosition(lineText.AsSpan(), endCharacter, ctx.PositionEncoding);
                    offset = ctx.VisibleLineOffsets[visibleIndex] + startColumn;
                    tokenLength = Math.Max(0, endColumn - startColumn);
                }
                else
                {
                    if (fullLineStarts is null || line < 0 || line >= fullLineStarts.Length) continue;
                    if (ctx.PositionEncoding == "utf-16")
                    {
                        var lineStart = fullLineStarts[line];
                        var lineEnd = line + 1 < fullLineStarts.Length ? fullLineStarts[line + 1] : textLength;
                        var lineLength = Math.Max(0, lineEnd - lineStart);
                        var startColumn = Math.Clamp(character, 0, lineLength);
                        var endColumn = Math.Clamp(endCharacter, startColumn, lineLength);
                        offset = lineStart + startColumn;
                        tokenLength = endColumn - startColumn;
                    }
                    else
                    {
                        if (fullDocumentText is null) continue;
                        var startOffset = LspPath.OffsetFromLspPosition(fullLineStarts, fullDocumentText, line, character, ctx.PositionEncoding);
                        var endOffset = LspPath.OffsetFromLspPosition(fullLineStarts, fullDocumentText, line, endCharacter, ctx.PositionEncoding);
                        offset = startOffset;
                        tokenLength = Math.Max(0, endOffset - startOffset);
                    }
                }
                if (tokenLength > 0 && offset < textLength)
                {
                    var tokenTypeName = tokenType >= 0 && tokenType < legend.Count ? legend[tokenType] : null;
                    var paletteIndex = tokenTypeName?.ToLowerInvariant() switch
                    {
                        "keyword" or "modifier" => 0,
                        "type" or "class" or "interface" or "struct" or "enum" or "typeparameter" => 1,
                        "function" or "method" or "macro" => 2,
                        "property" or "field" => 3,
                        "variable" or "parameter" => 4,
                        "string" or "regexp" => 5,
                        "number" => 6,
                        "comment" => 7,
                        _ => ((tokenType % palette.Length) + palette.Length) % palette.Length
                    };
                    if (offset < previousOffset) tokensAreOrdered = false;
                    previousOffset = offset;
                    tokens.Add((offset, Math.Min(tokenLength, textLength - offset), SemanticTokenBrushes[paletteIndex]));
                }
            }
            if (!tokensAreOrdered) tokens.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
            semanticDecodeWatch.Stop();
            KodoDiagnostics.ReportSlowStage(
                "LSP semantic token decode",
                semanticDecodeWatch.ElapsedMilliseconds,
                16,
                $"tokens={tokens.Count} full={!useRange} textLen={textLength}");
            return tokens;
        }, ctx.CancellationToken).ConfigureAwait(false);
        if (tokens is null) return false;
        var applied = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!LspSemanticHighlightingEnabled || !LspPresentationContextIsCurrent(ctx, LspEnabled) || editor.Document is null) return;
            renderer.SetTokens(tokens);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            var otherEditor = ReferenceEquals(editor, SecondaryEditorTextBox)
                ? PrimaryEditorTextBox
                : SecondaryEditorTextBox;
            if (otherEditor is not null && ReferenceEquals(otherEditor.Document, ctx.Document))
            {
                var otherVisible = GetEditorVisibleLines(otherEditor);
                if (!useRange || (otherVisible.StartLine >= ctx.VisibleStartLine && otherVisible.EndLine <= ctx.VisibleEndLine))
                {
                    var otherRenderer = ReferenceEquals(otherEditor, SecondaryEditorTextBox)
                        ? _secondaryLspSemanticTokenRenderer
                        : _primaryLspSemanticTokenRenderer;
                    otherRenderer.SetTokens(tokens);
                    otherEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                }
            }
            applied = true;
        });
        return applied;
    }

}
