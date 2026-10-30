// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Editor;
using Kodo.Models;

namespace Kodo;

internal enum CodeActionScope
{
    QuickFix,

    All
}

internal enum CodeActionRunOutcome
{
    Applied,
    NoLspActions,
    Cancelled,
    Failed
}

internal sealed class CodeActionItem
{
    public required string Title { get; init; }

    public required string Kind { get; init; }

    public string? Detail { get; init; }

    public required JsonElement Raw { get; init; }

    public JsonElement? Edit { get; set; }

    public string? Command { get; set; }

    public JsonElement? CommandArguments { get; set; }

    public JsonElement? Data { get; set; }

    public bool IsPreferred { get; init; }

    public string? DisabledReason { get; set; }

    public bool HasMalformedCommandArguments { get; set; }

    public LangRuleCodeAction? LocalAction { get; init; }

    public bool IsEnabled => DisabledReason is null && !HasMalformedCommandArguments;

    public bool IsQuickFix => KindMatches(Kind, "quickfix");

    public bool IsFixAll => Kind.EndsWith(".fixAll", StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle => IsPreferred && !IsQuickFix ? $"{Title}  (preferred)" : Title;

    internal static bool KindMatches(string kind, string requestedKind) =>
        string.Equals(kind, requestedKind, StringComparison.OrdinalIgnoreCase) ||
        kind.StartsWith(requestedKind + ".", StringComparison.OrdinalIgnoreCase);
}

internal sealed record WorkspaceEditConfirmation(string AnnotationId, string Label, string? Description);

public partial class MainWindow
{
    private const int CodeActionRequestTimeoutSeconds = 20;
    private static readonly TimeSpan CodeActionDiagnosticsWait = TimeSpan.FromMilliseconds(750);

    internal enum CodeActionOutcome
    {
        Applied,
        NoActions,
        Cancelled,
        Failed
    }

    private sealed record ClosedFileEditSource(
        string Content,
        System.Text.Encoding Encoding,
        DateTime LastWriteTimeUtc,
        long Length);

    private async Task<CodeActionRunOutcome> RunCodeActionsAsync(CodeActionScope scope)
    {
        if (EditorTextBox?.Document is null) return CodeActionRunOutcome.NoLspActions;
        var filePath = _currentFilePath;
        if (string.IsNullOrWhiteSpace(filePath)) return CodeActionRunOutcome.NoLspActions;

        try
        {
            var snapshot = await CaptureCodeActionSnapshotAsync(filePath, scope).ConfigureAwait(false);
            if (snapshot is null)
            {
                SetLspFeatureStatus("The file changed while the quick fix was loading - try again.");
                return CodeActionRunOutcome.Failed;
            }

            await LspNotifyDidOpenAsync(filePath, snapshot.Text).ConfigureAwait(false);
            if (!IsLspDocumentOpen(filePath))
            {
                SetLspFeatureStatus("The language server could not open this file - try again after it is ready.");
                return CodeActionRunOutcome.NoLspActions;
            }
            if (!await IsCodeActionSnapshotCurrentAsync(snapshot).ConfigureAwait(false))
            {
                var currentText = await CaptureActiveDocumentTextAsync(filePath).ConfigureAwait(false);
                if (currentText is not null)
                    await FlushLspDocumentAsync(filePath, currentText).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed while the language server was starting - try again."));
                return CodeActionRunOutcome.Failed;
            }

            var client = ResolveCodeActionClient(filePath);
            if (client is null)
            {
                SetLspFeatureStatus("Language server is not ready yet - try again in a moment.");
                return CodeActionRunOutcome.NoLspActions;
            }
            if (!client.Supports("textDocument/codeAction"))
            {
                SetLspFeatureStatus("This language server does not provide code actions for this file.");
                return CodeActionRunOutcome.NoLspActions;
            }

            await FlushLspDocumentAsync(filePath, snapshot.Text).ConfigureAwait(false);
            if (!await FlushPendingLspDocumentsAsync(client, filePath).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("An open file is still syncing with the language server - try the quick fix again."));
                return CodeActionRunOutcome.Failed;
            }
            if (!await IsCodeActionSnapshotCurrentAsync(snapshot).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed while the quick fix was loading - try again."));
                return CodeActionRunOutcome.Failed;
            }
            if (LspDiagnosticsEnabled && CurrentLspDocumentVersion(filePath) is int version)
                await WaitForLspDiagnosticsVersionAsync(NormalizeFilePath(filePath), version, CodeActionDiagnosticsWait).ConfigureAwait(false);
            if (!await IsCodeActionSnapshotCurrentAsync(snapshot).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed while the quick fix was loading - try again."));
                return CodeActionRunOutcome.Failed;
            }

            var syncedOpenDocuments = await CaptureSyncedOpenCodeActionDocumentsAsync(client).ConfigureAwait(false);
            if (syncedOpenDocuments is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("Open files are still syncing with the language server - try the quick fix again."));
                return CodeActionRunOutcome.Failed;
            }
            snapshot = snapshot with { SyncedOpenDocuments = syncedOpenDocuments };

            var actions = await RequestCodeActionsAsync(client, snapshot, scope).ConfigureAwait(false);
            if (actions.Count == 0)
            {
                SetLspFeatureStatus(scope == CodeActionScope.QuickFix
                    ? "No quick fixes available here."
                    : "No code actions available here.");
                return CodeActionRunOutcome.NoLspActions;
            }

            var enabled = actions.Where(a => a.IsEnabled).ToList();
            if (enabled.Count == 0)
            {
                var why = actions.Select(a => a.DisabledReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                SetLspFeatureStatus(string.IsNullOrWhiteSpace(why)
                    ? "No quick fixes available here."
                    : TruncateStatusText($"No fix available: {why}", 90));
                return CodeActionRunOutcome.NoLspActions;
            }

            var chosen = enabled.Count == 1
                ? enabled[0]
                : await PresentCodeActionsAsync(actions, snapshot).ConfigureAwait(false);

            if (chosen is null) return CodeActionRunOutcome.Cancelled;

            return await ApplyCodeActionAsync(client, chosen, snapshot).ConfigureAwait(false)
                ? CodeActionRunOutcome.Applied
                : CodeActionRunOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            SetLspFeatureStatus("The language server did not answer in time.");
            return CodeActionRunOutcome.Failed;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Code action pipeline failed", ex);
            SetLspFeatureStatus("Quick fix failed - see the log for details.");
            return CodeActionRunOutcome.Failed;
        }
    }

    internal static bool ShouldRunLocalCodeActionFallback(CodeActionRunOutcome outcome) =>
        outcome == CodeActionRunOutcome.NoLspActions;

    private sealed record CodeActionSnapshot(
        string FilePath,
        string Text,
        TextDocument Document,
        ITextSourceVersion DocumentVersion,
        long Revision,
        int Caret,
        int Start,
        int Length,
        EditorPaneState? Pane = null,
        TextEditor? Editor = null,
        EditorTab? Tab = null,
        IReadOnlyDictionary<string, SyncedOpenCodeActionDocument>? SyncedOpenDocuments = null);

    private sealed record SyncedOpenCodeActionDocument(
        string Path,
        LspClient Client,
        EditorTab Tab,
        TextDocument? Document,
        ITextSourceVersion? DocumentVersion,
        string? Content,
        int LspVersion);

    private async Task<CodeActionSnapshot?> CaptureCodeActionSnapshotAsync(string filePath, CodeActionScope scope)
    {
        var captured = await Dispatcher.UIThread.InvokeAsync<(TextDocument Document, ITextSourceVersion DocumentVersion, EditorTextSnapshot Text, long Revision, CancellationToken RevisionToken, int Caret, int Start, int Length, EditorPaneState Pane, TextEditor Editor, EditorTab? Tab)?>(() =>
        {
            var editor = EditorTextBox;
            var pane = _activeEditorPane;
            var doc = editor?.Document;
            if (doc is null || !FileSystemPaths.Equals(_currentFilePath, filePath)) return null;
            var caret = editor!.TextArea.Caret.Offset;
            var selection = editor.TextArea.Selection;
            var segment = selection.IsEmpty ? null : selection.SurroundingSegment;
            var caretLine = doc.GetLineByOffset(caret);
            var (start, length) = GetCodeActionRequestRange(
                doc.TextLength,
                caret,
                segment?.Offset,
                segment is null ? 0 : segment.EndOffset - segment.Offset,
                caretLine.Offset,
                caretLine.Length,
                scope == CodeActionScope.QuickFix);
            return (Document: doc, DocumentVersion: doc.Version, Text: EditorTextSnapshotStore.GetSnapshot(doc), Revision: EditorSession.Revision,
                RevisionToken: EditorSession.RevisionCancellationToken, Caret: caret, Start: start, Length: length,
                Pane: pane, Editor: editor, Tab: pane.ActiveTab);
        });
        if (captured is null) return null;
        string text;
        try { text = await captured.Value.Text.GetTextAsync(captured.Value.RevisionToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        var stillCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            ReferenceEquals(_activeEditorPane, captured.Value.Pane) && ReferenceEquals(EditorTextBox, captured.Value.Editor) &&
            ReferenceEquals(captured.Value.Pane.ActiveTab, captured.Value.Tab) &&
            ReferenceEquals(captured.Value.Editor.Document, captured.Value.Document) &&
            ReferenceEquals(captured.Value.Document.Version, captured.Value.DocumentVersion) && EditorSession.Revision == captured.Value.Revision &&
            FileSystemPaths.Equals(_currentFilePath, filePath));
        if (!stillCurrent) return null;
        var snapshot = new CodeActionSnapshot(filePath, text, captured.Value.Document, captured.Value.DocumentVersion,
            captured.Value.Revision, captured.Value.Caret, captured.Value.Start, captured.Value.Length,
            captured.Value.Pane, captured.Value.Editor, captured.Value.Tab);
        var isCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) && ReferenceEquals(_activeEditorPane, snapshot.Pane) &&
            ReferenceEquals(EditorTextBox, snapshot.Editor) && ReferenceEquals(snapshot.Pane?.ActiveTab, snapshot.Tab) &&
            ReferenceEquals(snapshot.Editor?.Document, snapshot.Document) && ReferenceEquals(snapshot.Document.Version, snapshot.DocumentVersion) &&
            EditorSession.Revision == snapshot.Revision);
        if (!isCurrent) return null;
        return snapshot;
    }

    private int? CurrentLspDocumentVersion(string filePath)
    {
        lock (_lspOpenLock)
            return _lspDocumentVersions.TryGetValue(NormalizeFilePath(filePath), out var version) ? version : null;
    }

    private bool IsCodeActionFilePathCurrent(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath)
            ? string.IsNullOrWhiteSpace(_currentFilePath)
            : FileSystemPaths.Equals(_currentFilePath, filePath);

    internal static (int Start, int Length) GetCodeActionRequestRange(
        int documentLength,
        int caretOffset,
        int? selectionOffset,
        int selectionLength,
        int caretLineOffset,
        int caretLineLength,
        bool useCaretLineWhenSelectionIsEmpty)
    {
        var start = selectionOffset ?? caretOffset;
        var length = selectionOffset.HasValue ? Math.Max(0, selectionLength) : 0;
        if (!selectionOffset.HasValue && useCaretLineWhenSelectionIsEmpty)
        {
            start = caretLineOffset;
            length = caretLineLength;
        }
        start = Math.Clamp(start, 0, Math.Max(0, documentLength));
        length = Math.Clamp(length, 0, Math.Max(0, documentLength - start));
        return (start, length);
    }

    private async Task WaitForLspDiagnosticsVersionAsync(string path, int version, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            lock (_lspDiagnosticsLock)
                if (_lspDiagnosticVersions.TryGetValue(path, out var diagnosticVersion) && diagnosticVersion >= version) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private async Task<bool> FlushPendingLspDocumentsAsync(LspClient client, string activePath)
    {
        List<(string Path, bool BelongsToClient, bool HasPendingSync)> documents;
        lock (_lspOpenLock)
        {
            documents = _lspOpenDocuments.Select(path =>
            {
                var belongsToClient = _lspDocumentClients.TryGetValue(path, out var documentClient) &&
                    ReferenceEquals(documentClient, client);
                var hasPendingSync = _lspPendingTexts.ContainsKey(path) || _lspPendingChanges.ContainsKey(path);
                return (Path: path, BelongsToClient: belongsToClient, HasPendingSync: hasPendingSync);
            }).ToList();
        }

        foreach (var path in SelectPendingLspDocuments(documents, activePath))
        {
            var text = await CaptureOpenDocumentTextAsync(path).ConfigureAwait(false);
            if (text is null)
                return false;
            await FlushLspDocumentAsync(path, text).ConfigureAwait(false);
            if (HasPendingLspDocumentSync(path))
                return false;
        }
        return true;
    }

    private async Task<IReadOnlyDictionary<string, SyncedOpenCodeActionDocument>?> CaptureSyncedOpenCodeActionDocumentsAsync(LspClient client)
    {
        List<(string Path, int Version)> serverDocuments;
        lock (_lspOpenLock)
        {
            if (_lspPendingOpens.Count > 0)
                return null;
            serverDocuments = new List<(string Path, int Version)>();
            foreach (var path in _lspOpenDocuments)
            {
                if (!_lspDocumentClients.TryGetValue(path, out var documentClient) || !ReferenceEquals(documentClient, client))
                    continue;
                if (_lspPendingTexts.ContainsKey(path) || _lspPendingChanges.ContainsKey(path) ||
                    !_lspDocumentVersions.TryGetValue(path, out var version))
                    return null;
                serverDocuments.Add((path, version));
            }
        }

        var captured = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var documents = new Dictionary<string, SyncedOpenCodeActionDocument>(FileSystemPaths.Comparer);
            foreach (var (path, version) in serverDocuments)
            {
                var tab = OpenTabs.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Path) && FileSystemPaths.Equals(candidate.Path, path));
                if (tab is null)
                    return (Valid: false, Documents: documents);
                documents[path] = new SyncedOpenCodeActionDocument(
                    path, client, tab, tab.Document, tab.Document?.Version, tab.Document is null ? tab.Content : null, version);
            }
            return (Valid: true, Documents: documents);
        });
        if (!captured.Valid)
            return null;

        var documentsStillCurrent = await Dispatcher.UIThread.InvokeAsync(() => captured.Documents.Values.All(document =>
            OpenTabs.Contains(document.Tab) && FileSystemPaths.Equals(document.Tab.Path, document.Path) &&
            ReferenceEquals(document.Tab.Document, document.Document) &&
            (document.Document is null
                ? string.Equals(document.Tab.Content, document.Content, StringComparison.Ordinal)
                : ReferenceEquals(document.Document.Version, document.DocumentVersion))));
        if (!documentsStillCurrent)
            return null;

        lock (_lspOpenLock)
        {
            foreach (var document in captured.Documents.Values)
            {
                if (!_lspOpenDocuments.Contains(document.Path) ||
                    !_lspDocumentClients.TryGetValue(document.Path, out var documentClient) || !ReferenceEquals(documentClient, client) ||
                    _lspPendingTexts.ContainsKey(document.Path) || _lspPendingChanges.ContainsKey(document.Path) ||
                    !_lspDocumentVersions.TryGetValue(document.Path, out var currentVersion) || currentVersion != document.LspVersion)
                    return null;
            }
        }

        return captured.Documents;
    }

    internal static IReadOnlyList<string> SelectPendingLspDocuments(
        IEnumerable<(string Path, bool BelongsToClient, bool HasPendingSync)> documents,
        string activePath) =>
        documents
            .Where(document => document.BelongsToClient && document.HasPendingSync && !IsSameDocument(document.Path, activePath))
            .Select(document => document.Path)
            .ToArray();

    private LspClient? ResolveCodeActionClient(string filePath)
    {
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return null;
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return null;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is { IsInitialized: true }) return client;
        foreach (var candidate in lspExt.AllLspConfigurations)
        {
            client = _lspManager.TryGetClient(workspace, candidate);
            if (client is { IsInitialized: true }) return client;
        }
        return null;
    }

    private async Task<List<CodeActionItem>> RequestCodeActionsAsync(
        LspClient client,
        CodeActionSnapshot snapshot,
        CodeActionScope scope)
    {
        var uri = FilePathToUri(snapshot.FilePath);
        var text = snapshot.Text;

        var (startLine, startChar) = LspPath.OffsetToLspPosition(text, snapshot.Start, client.PositionEncoding);
        var (endLine, endChar) = LspPath.OffsetToLspPosition(text, snapshot.Start + snapshot.Length, client.PositionEncoding);
        var contextDiagnostics = CollectRawDiagnosticsForRequest(client, snapshot);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["range"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["start"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = startLine, ["character"] = startChar },
                ["end"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = endLine, ["character"] = endChar }
            },
            ["context"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["diagnostics"] = contextDiagnostics
            }
        };

        if (scope == CodeActionScope.QuickFix)
        {
            parameters["context"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["diagnostics"] = contextDiagnostics,
                ["only"] = new[] { "quickfix" }
            };
        }

        KodoDiagnostics.LogDebug(
            $"LSP codeAction request file={snapshot.FilePath} offset={snapshot.Caret} scope={scope} diagnostics={contextDiagnostics.Count}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CodeActionRequestTimeoutSeconds));
        var result = await client.SendRequestAsync("textDocument/codeAction", parameters, cts.Token).ConfigureAwait(false);
        if (result is not { ValueKind: JsonValueKind.Array }) return new List<CodeActionItem>();
        return ParseCodeActions(result.Value, scope);
    }

    private List<JsonElement> CollectRawDiagnosticsForRequest(LspClient client, CodeActionSnapshot snapshot)
    {
        var requestStart = Math.Clamp(snapshot.Start, 0, snapshot.Text.Length);
        var requestEnd = Math.Clamp(snapshot.Start + snapshot.Length, requestStart, snapshot.Text.Length);
        var lineStarts = LspPath.GetLineStarts(snapshot.Text);

        var result = new List<JsonElement>();
        int? sentVersion;
        lock (_lspOpenLock)
            sentVersion = _lspDocumentVersions.TryGetValue(NormalizeFilePath(snapshot.FilePath), out var version) ? version : null;
        lock (_lspDiagnosticsLock)
        {
            var hasDiagnosticVersion = _lspDiagnosticVersions.TryGetValue(NormalizeFilePath(snapshot.FilePath), out var diagnosticVersion);
            if (!AreCodeActionDiagnosticsCurrent(sentVersion, hasDiagnosticVersion ? diagnosticVersion : null))
                return result;
            if (!_lspRawDiagnostics.TryGetValue(NormalizeFilePath(snapshot.FilePath), out var raws)) return result;
            foreach (var raw in raws)
            {
                if (!raw.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadDiagnosticPosition(range, "start", out var startLine, out var startCharacter) ||
                    !TryReadDiagnosticPosition(range, "end", out var endLine, out var endCharacter) ||
                    !TryOffsetFromLspPosition(lineStarts, snapshot.Text, startLine, startCharacter, client.PositionEncoding, out var diagnosticStart) ||
                    !TryOffsetFromLspPosition(lineStarts, snapshot.Text, endLine, endCharacter, client.PositionEncoding, out var diagnosticEnd) ||
                    diagnosticEnd < diagnosticStart)
                    continue;

                if (!CodeActionRangesIntersect(requestStart, requestEnd, diagnosticStart, diagnosticEnd)) continue;
                result.Add(raw.Clone());
            }
        }
        return result;
    }

    internal static bool AreCodeActionDiagnosticsCurrent(int? sentDocumentVersion, int? diagnosticVersion) =>
        !sentDocumentVersion.HasValue || diagnosticVersion >= sentDocumentVersion.Value;

    private static bool TryReadDiagnosticPosition(JsonElement range, string propertyName, out int line, out int character)
    {
        line = 0;
        character = 0;
        return range.TryGetProperty(propertyName, out var position) && position.ValueKind == JsonValueKind.Object &&
            position.TryGetProperty("line", out var lineElement) && lineElement.TryGetInt32(out line) && line >= 0 &&
            position.TryGetProperty("character", out var characterElement) && characterElement.TryGetInt32(out character) && character >= 0;
    }

    internal static bool CodeActionRangesIntersect(int requestStart, int requestEnd, int diagnosticStart, int diagnosticEnd)
    {
        if (requestStart < 0 || requestEnd < requestStart || diagnosticStart < 0 || diagnosticEnd < diagnosticStart)
            return false;
        if (diagnosticStart == diagnosticEnd)
            return diagnosticStart >= requestStart && diagnosticStart <= requestEnd;
        return requestStart == requestEnd
            ? diagnosticStart <= requestStart && requestStart <= diagnosticEnd
            : diagnosticStart < requestEnd && requestStart < diagnosticEnd;
    }

    internal static List<CodeActionItem> ParseCodeActions(JsonElement array, CodeActionScope scope)
    {
        var items = new List<CodeActionItem>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var title = element.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(title)) continue;

            var kind = element.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString() ?? string.Empty
                : string.Empty;

            if (scope == CodeActionScope.QuickFix && !string.IsNullOrEmpty(kind) &&
                !CodeActionItem.KindMatches(kind, "quickfix"))
                continue;

            string? disabledReason = null;
            if (element.TryGetProperty("disabled", out var disabled) && disabled.ValueKind != JsonValueKind.Null)
            {
                disabledReason = disabled.ValueKind == JsonValueKind.Object &&
                    disabled.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(reason.GetString())
                    ? reason.GetString()
                    : "not available in this context";
            }

            var item = new CodeActionItem
            {
                Title = title,
                Kind = kind,
                Detail = element.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : null,
                Raw = element.Clone(),
                IsPreferred = element.TryGetProperty("isPreferred", out var pref) && pref.ValueKind == JsonValueKind.True,
                DisabledReason = disabledReason,
                Data = element.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ? data.Clone() : null
            };

            if (element.TryGetProperty("edit", out var edit) && edit.ValueKind == JsonValueKind.Object)
                item.Edit = edit.Clone();

            if (element.TryGetProperty("command", out var command))
            {
                if (command.ValueKind == JsonValueKind.String) item.Command = command.GetString();
                else if (command.ValueKind == JsonValueKind.Object && command.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    item.Command = name.GetString();
                    if (command.TryGetProperty("arguments", out var args))
                    {
                        if (args.ValueKind == JsonValueKind.Array) item.CommandArguments = args.Clone();
                        else item.HasMalformedCommandArguments = true;
                    }
                }
            }

            items.Add(item);
        }

        return items
            .OrderByDescending(a => a.IsEnabled)
            .ThenByDescending(a => a.IsQuickFix)
            .ThenByDescending(a => a.IsPreferred)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<bool> ResolveCodeActionIfNeededAsync(LspClient client, CodeActionItem action)
    {
        if (action.Data is null ||
            action.Edit is not null && !string.IsNullOrWhiteSpace(action.Command) ||
            !client.Supports("codeAction/resolve"))
            return HasExecutableCodeAction(action);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CodeActionRequestTimeoutSeconds));
            var resolved = await client.SendRequestAsync("codeAction/resolve", action.Raw, cts.Token).ConfigureAwait(false);
            if (resolved is not { ValueKind: JsonValueKind.Object }) return HasExecutableCodeAction(action);
            MergeResolvedCodeAction(action, resolved.Value);
            return HasExecutableCodeAction(action);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("codeAction/resolve failed", ex);
            return HasExecutableCodeAction(action);
        }
    }

    private static bool HasExecutableCodeAction(CodeActionItem action) =>
        action.IsEnabled && (action.Edit is not null || !string.IsNullOrWhiteSpace(action.Command));

    internal static void MergeResolvedCodeAction(CodeActionItem action, JsonElement resolved)
    {
        if (resolved.ValueKind != JsonValueKind.Object) return;
        if (resolved.TryGetProperty("edit", out var edit))
            action.Edit = edit.ValueKind == JsonValueKind.Object ? edit.Clone() : null;
        if (!resolved.TryGetProperty("command", out var command)) return;

        action.Command = null;
        action.CommandArguments = null;
        action.HasMalformedCommandArguments = false;
        if (command.ValueKind == JsonValueKind.String)
            action.Command = command.GetString();
        else if (command.ValueKind == JsonValueKind.Object &&
                 command.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String)
        {
            action.Command = name.GetString();
            if (command.TryGetProperty("arguments", out var arguments))
            {
                if (arguments.ValueKind == JsonValueKind.Array) action.CommandArguments = arguments.Clone();
                else action.HasMalformedCommandArguments = true;
            }
        }
    }

    internal static bool WorkspaceEditVersionMatches(int? expectedVersion, int? currentVersion) =>
        !expectedVersion.HasValue || expectedVersion == currentVersion;

    internal static bool IsWorkspaceEditTargetAllowed(string targetPath, string activePath, string? workspaceRoot)
    {
        if (IsSameDocument(targetPath, activePath)) return true;
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return false;
        return IsPathInsideDirectory(targetPath, workspaceRoot);
    }

    internal static bool TryGetWorkspaceEditFilePath(string uriOrPath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(uriOrPath)) return false;
        try
        {
            if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri)
            {
                if (!string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                    return false;
                path = Path.GetFullPath(FileUriToPath(uriOrPath));
                return true;
            }

            if (!Path.IsPathRooted(uriOrPath)) return false;
            path = Path.GetFullPath(uriOrPath);
            return true;
        }
        catch
        {
            path = string.Empty;
            return false;
        }
    }

    internal static IReadOnlyList<string> GetWorkspaceEditTargetPaths(JsonElement edit)
    {
        if (!IsValidWorkspaceEditShape(edit)) return Array.Empty<string>();
        var paths = new HashSet<string>(FileSystemPaths.Comparer);
        if (edit.TryGetProperty("documentChanges", out var documentChanges))
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object ||
                    !change.TryGetProperty("textDocument", out var textDocument) || textDocument.ValueKind != JsonValueKind.Object ||
                    !textDocument.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String ||
                    !TryGetWorkspaceEditFilePath(uri.GetString() ?? string.Empty, out var path))
                    continue;
                paths.Add(path);
            }
        }
        else if (edit.TryGetProperty("changes", out var changes))
        {
            foreach (var change in changes.EnumerateObject())
                if (TryGetWorkspaceEditFilePath(change.Name, out var path))
                    paths.Add(path);
        }
        return paths.ToArray();
    }

    internal static bool IsValidWorkspaceEditShape(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object) return false;
        var hasDocumentChanges = edit.TryGetProperty("documentChanges", out var documentChanges);
        var hasChanges = edit.TryGetProperty("changes", out var changes);
        return !(hasDocumentChanges && hasChanges) &&
               (!hasDocumentChanges || documentChanges.ValueKind == JsonValueKind.Array) &&
               (!hasChanges || changes.ValueKind == JsonValueKind.Object);
    }

    internal static bool TryCollectRequiredWorkspaceEditConfirmations(
        JsonElement edit,
        out List<WorkspaceEditConfirmation> confirmations)
    {
        confirmations = new List<WorkspaceEditConfirmation>();
        if (!IsValidWorkspaceEditShape(edit)) return false;

        var hasAnnotations = edit.TryGetProperty("changeAnnotations", out var annotations);
        if (hasAnnotations && annotations.ValueKind != JsonValueKind.Object) return false;
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        var editArrays = new List<JsonElement>();
        if (edit.TryGetProperty("documentChanges", out var documentChanges))
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object ||
                    !change.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array)
                    return false;
                editArrays.Add(edits);
            }
        }
        else if (edit.TryGetProperty("changes", out var changes))
        {
            foreach (var change in changes.EnumerateObject())
            {
                if (change.Value.ValueKind != JsonValueKind.Array) return false;
                editArrays.Add(change.Value);
            }
        }

        foreach (var edits in editArrays)
        {
            foreach (var textEdit in edits.EnumerateArray())
            {
                if (textEdit.ValueKind != JsonValueKind.Object) return false;
                if (!textEdit.TryGetProperty("annotationId", out var annotationIdElement)) continue;
                if (!hasAnnotations || annotationIdElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(annotationIdElement.GetString()))
                    return false;
                referencedIds.Add(annotationIdElement.GetString()!);
            }
        }

        foreach (var id in referencedIds)
        {
            if (!annotations.TryGetProperty(id, out var annotation) || annotation.ValueKind != JsonValueKind.Object ||
                !annotation.TryGetProperty("label", out var labelElement) || labelElement.ValueKind != JsonValueKind.String)
                return false;
            var needsConfirmation = false;
            if (annotation.TryGetProperty("needsConfirmation", out var needsConfirmationElement))
            {
                if (needsConfirmationElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                needsConfirmation = needsConfirmationElement.ValueKind == JsonValueKind.True;
            }
            if (!needsConfirmation) continue;

            string? description = null;
            if (annotation.TryGetProperty("description", out var descriptionElement))
            {
                if (descriptionElement.ValueKind == JsonValueKind.String)
                    description = descriptionElement.GetString();
                else if (descriptionElement.ValueKind == JsonValueKind.Object &&
                         descriptionElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                    description = value.GetString();
            }
            confirmations.Add(new WorkspaceEditConfirmation(id, labelElement.GetString() ?? id, description));
        }

        return true;
    }

    internal static bool ClosedFileMatchesSnapshot(
        string path,
        string expectedContent,
        System.Text.Encoding encoding,
        DateTime? expectedWriteTimeUtc,
        long? expectedLength)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists &&
                   (!expectedWriteTimeUtc.HasValue || info.LastWriteTimeUtc == expectedWriteTimeUtc.Value) &&
                   (!expectedLength.HasValue || info.Length == expectedLength.Value) &&
                   string.Equals(File.ReadAllText(path, encoding), expectedContent, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static System.Text.Encoding CreateStrictWorkspaceEditEncoding(System.Text.Encoding encoding)
    {
        var strict = (System.Text.Encoding)encoding.Clone();
        strict.DecoderFallback = System.Text.DecoderFallback.ExceptionFallback;
        strict.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback;
        return strict;
    }

    internal static bool TryReadWorkspaceEditVersion(JsonElement textDocument, out int? version)
    {
        version = null;
        if (textDocument.ValueKind != JsonValueKind.Object) return false;
        if (!textDocument.TryGetProperty("version", out var versionElement) || versionElement.ValueKind == JsonValueKind.Null)
            return true;
        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var parsedVersion))
            return false;
        version = parsedVersion;
        return true;
    }

    internal static Dictionary<string, object?> CreateExecuteCommandParams(string command, JsonElement? arguments)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command
        };
        if (arguments is { ValueKind: JsonValueKind.Array } argumentArray)
            parameters["arguments"] = argumentArray.Clone();
        return parameters;
    }

    internal static bool CanContinueCodeActionAfterEdit(CodeActionOutcome editOutcome) =>
        editOutcome is CodeActionOutcome.Applied or CodeActionOutcome.NoActions;

    internal static IReadOnlyList<LangRuleCodeAction> GetApplicableLangRuleCodeActions(
        IReadOnlyList<LangRuleCodeAction>? actions,
        int caretOffset,
        int documentLength)
    {
        if (actions is null || documentLength < 0 || caretOffset < 0 || caretOffset > documentLength)
            return Array.Empty<LangRuleCodeAction>();

        return actions.Where(action =>
                action.Start >= 0 && action.Start <= documentLength &&
                action.Length >= 0 && action.Length <= documentLength - action.Start &&
                caretOffset >= action.Start &&
                (long)caretOffset <= (long)action.Start + Math.Max(1, action.Length))
            .ToArray();
    }

    internal static IReadOnlyList<LangRuleCodeAction> GetApplicableLangRuleCodeActions(
        IReadOnlyList<LangRuleCodeAction>? actions,
        int caretOffset,
        string documentSnapshot) =>
        GetApplicableLangRuleCodeActions(actions, caretOffset, documentSnapshot.Length);

    private async Task<bool> IsCodeActionSnapshotCurrentAsync(CodeActionSnapshot snapshot) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
            snapshot.Pane is { } pane && snapshot.Editor is { } editor && snapshot.Tab is { } tab &&
            OpenTabs.Contains(tab) && ReferenceEquals(_activeEditorPane, pane) && ReferenceEquals(EditorTextBox, editor) &&
            ReferenceEquals(pane.ActiveTab, tab) && ReferenceEquals(editor.Document, snapshot.Document) &&
            ReferenceEquals(snapshot.Document.Version, snapshot.DocumentVersion) && EditorSession.Revision == snapshot.Revision &&
            editor.TextArea.Caret.Offset == snapshot.Caret && IsCodeActionFilePathCurrent(snapshot.FilePath));

    private async Task<bool> AreCodeActionOpenTargetsCurrentAsync(
        CodeActionSnapshot snapshot,
        IEnumerable<string> targetPaths,
        bool requireAllOpenDocuments = false)
    {
        var paths = targetPaths.Distinct(FileSystemPaths.Comparer).ToArray();
        var documents = snapshot.SyncedOpenDocuments;
        if (documents is null)
            return true;

        var targetsCurrent = await Dispatcher.UIThread.InvokeAsync(() => AreCodeActionOpenTargetsCurrentOnUi(snapshot, paths));
        if (!targetsCurrent)
            return false;

        lock (_lspOpenLock)
        {
            if (requireAllOpenDocuments)
            {
                var clients = new HashSet<LspClient>(documents.Values.Select(document => document.Client));
                foreach (var path in _lspOpenDocuments)
                {
                    if (_lspDocumentClients.TryGetValue(path, out var currentClient) && clients.Contains(currentClient) &&
                        !documents.ContainsKey(path))
                        return false;
                }
            }
            foreach (var path in paths)
            {
                var expected = documents.Values.FirstOrDefault(document => FileSystemPaths.Equals(document.Path, path));
                if (expected is null)
                    continue;
                if (!_lspOpenDocuments.Contains(expected.Path) || _lspPendingTexts.ContainsKey(expected.Path) ||
                    _lspPendingChanges.ContainsKey(expected.Path) ||
                    !_lspDocumentClients.TryGetValue(expected.Path, out var currentClient) || !ReferenceEquals(currentClient, expected.Client) ||
                    !_lspDocumentVersions.TryGetValue(expected.Path, out var currentVersion) || currentVersion != expected.LspVersion)
                    return false;
            }
        }
        return true;
    }

    private bool AreCodeActionOpenTargetsCurrentOnUi(CodeActionSnapshot snapshot, IEnumerable<string> targetPaths)
    {
        var documents = snapshot.SyncedOpenDocuments;
        if (documents is null)
            return true;

        foreach (var path in targetPaths)
        {
            var expected = documents.Values.FirstOrDefault(document => FileSystemPaths.Equals(document.Path, path));
            var current = OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, path));
            var sameDocument = expected is not null && ReferenceEquals(current?.Document, expected.Document);
            var sameVersion = expected is not null && expected.Document is not null &&
                ReferenceEquals(expected.Document.Version, expected.DocumentVersion);
            var sameContent = expected is not null && expected.Document is null &&
                string.Equals(current?.Content, expected.Content, StringComparison.Ordinal);
            if (!CodeActionOpenDocumentSnapshotMatches(
                    wasOpenAtRequest: expected is not null,
                    isCurrentlyOpen: current is not null,
                    sameTab: expected is not null && ReferenceEquals(current, expected.Tab) && OpenTabs.Contains(expected.Tab),
                    sameDocument,
                    sameVersion,
                    sameContent))
                return false;
        }
        return true;
    }

    internal static bool CodeActionOpenDocumentSnapshotMatches(
        bool wasOpenAtRequest,
        bool isCurrentlyOpen,
        bool sameTab,
        bool sameDocument,
        bool sameVersion,
        bool sameContent) =>
        wasOpenAtRequest
            ? isCurrentlyOpen && sameTab && sameDocument && (sameVersion || sameContent)
            : !isCurrentlyOpen;

    private async Task<bool> IsCodeActionTargetStillCurrentAsync(
        CodeActionSnapshot snapshot,
        ITextSourceVersion? expectedVersion = null,
        int? expectedCaret = null) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
            snapshot.Pane is { } pane && snapshot.Editor is { } editor && snapshot.Tab is { } tab &&
            OpenTabs.Contains(tab) && ReferenceEquals(_activeEditorPane, pane) && ReferenceEquals(EditorTextBox, editor) &&
            ReferenceEquals(pane.ActiveTab, tab) && ReferenceEquals(editor.Document, snapshot.Document) &&
            ReferenceEquals(snapshot.Document.Version, expectedVersion ?? snapshot.DocumentVersion) &&
            editor.TextArea.Caret.Offset == (expectedCaret ?? snapshot.Caret) &&
            IsSameDocument(tab.Path, snapshot.FilePath));

    private async Task<bool> ApplyCodeActionAsync(LspClient client, CodeActionItem action, CodeActionSnapshot snapshot)
    {
        if (!await IsCodeActionSnapshotCurrentAsync(snapshot).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed - the fix was not applied."));
            return false;
        }

        if (!await ResolveCodeActionIfNeededAsync(client, action).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The language server could not prepare this quick fix."));
            return false;
        }

        if (!await IsCodeActionSnapshotCurrentAsync(snapshot).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed - the fix was not applied."));
            return false;
        }

        var editTargets = action.Edit is { } actionEdit
            ? GetWorkspaceEditTargetPaths(actionEdit)
            : Array.Empty<string>();
        var requiresAllOpenDocumentsCurrent = string.IsNullOrWhiteSpace(action.Command) == false;
        var documentsToValidate = requiresAllOpenDocumentsCurrent
            ? (snapshot.SyncedOpenDocuments?.Values.Select(document => document.Path) ?? editTargets)
            : editTargets;
        if (!await AreCodeActionOpenTargetsCurrentAsync(snapshot, documentsToValidate, requiresAllOpenDocumentsCurrent).ConfigureAwait(false))
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("An open file changed while the language server prepared the fix - try again."));
            return false;
        }

        if (action.Edit is null && string.IsNullOrWhiteSpace(action.Command))
        {
            KodoDiagnostics.LogDebug($"Code action '{action.Title}' carries neither an edit nor a command; ignoring");
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("This quick fix did not include an edit to apply."));
            return false;
        }

        var editApplied = false;
        ITextSourceVersion? expectedDocumentVersion = snapshot.DocumentVersion;
        var expectedCaret = snapshot.Caret;
        if (action.Edit is { } edit)
        {
            var applied = await ApplyWorkspaceEditAsync(
                edit,
                snapshot,
                EditScope.WorkspaceEdit,
                client.PositionEncoding,
                onCommittedVersionCaptured: (version, caret) =>
                {
                    expectedDocumentVersion = version;
                    expectedCaret = caret;
                }).ConfigureAwait(false);
            if (!CanContinueCodeActionAfterEdit(applied))
            {
                if (applied == CodeActionOutcome.Cancelled)
                    await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The quick fix was cancelled."));
                return false;
            }
            editApplied = applied == CodeActionOutcome.Applied;
        }

        if (!string.IsNullOrWhiteSpace(action.Command))
        {
            if (!await IsCodeActionTargetStillCurrentAsync(snapshot, expectedDocumentVersion, expectedCaret).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed - the quick fix command was not run."));
                return false;
            }
            if (editApplied)
            {
                try
                {
                    foreach (var targetPath in GetWorkspaceEditTargetPaths(action.Edit!.Value))
                    {
                        var updatedText = await CaptureOpenDocumentTextAsync(targetPath).ConfigureAwait(false);
                        if (updatedText is not null)
                            await FlushLspDocumentAsync(targetPath, updatedText, forceIfNoPending: true).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug("Could not synchronize all documents changed by a code action", ex);
                    await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The edit was applied, but language server sync failed, so its command was not run."));
                    return false;
                }
                if (!await IsCodeActionTargetStillCurrentAsync(snapshot, expectedDocumentVersion, expectedCaret).ConfigureAwait(false))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The file changed while syncing - the quick fix command was not run."));
                    return false;
                }
            }
            var executeParams = CreateExecuteCommandParams(action.Command, action.CommandArguments);
            try
            {
                using var executeCts = new CancellationTokenSource(TimeSpan.FromSeconds(CodeActionRequestTimeoutSeconds));
                await client.SendRequestAsync("workspace/executeCommand", executeParams, executeCts.Token).ConfigureAwait(false);
                var status = editApplied
                    ? $"Applied and ran '{TruncateStatusText(action.Title, 60)}'."
                    : $"Ran '{TruncateStatusText(action.Title, 60)}'.";
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus(status));
                return true;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("workspace/executeCommand failed for a code action", ex);
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("The language server command failed."));
                return false;
            }
        }

        if (editApplied)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus($"Applied '{TruncateStatusText(action.Title, 60)}'."));
            return true;
        }
        return false;
    }

    internal sealed record CodeActionPlan(
        string Path,
        string Original,
        string Updated,
        bool IsActive,
        List<(int Start, int End, string NewText)> Edits,
        System.Text.Encoding? Encoding = null,
        DateTime? LastWriteTimeUtc = null,
        long? LastLength = null);

    private sealed record WorkspaceDiskCommit(string? Failure, IReadOnlyList<CodeActionPlan> WrittenFiles);

    private enum EditScope
    {
        OpenDocumentsOnly,

        ConfirmedProjectWide,

        WorkspaceEdit
    }

    private async Task<bool> ApplyExternalWorkspaceEditAsync(
        JsonElement edit,
        CodeActionSnapshot snapshot,
        string positionEncoding,
        string? workspaceRoot = null)
    {
        return await ApplyWorkspaceEditAsync(edit, snapshot, EditScope.WorkspaceEdit, positionEncoding, workspaceRoot).ConfigureAwait(false)
            == CodeActionOutcome.Applied;
    }

    private async Task<CodeActionOutcome> ApplyWorkspaceEditAsync(
        JsonElement edit,
        CodeActionSnapshot snapshot,
        EditScope scope = EditScope.OpenDocumentsOnly,
        string? positionEncoding = null,
        string? workspaceRoot = null,
        Action<ITextSourceVersion, int>? onCommittedVersionCaptured = null)
    {
        var plan = new List<CodeActionPlan>();
        workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? GetWorkspaceRootForFile(snapshot.FilePath)
            : workspaceRoot;
        if (!IsValidWorkspaceEditShape(edit))
        {
            KodoDiagnostics.LogDebug("Workspace edit rejected an invalid or conflicting edit shape");
            SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
            return CodeActionOutcome.Failed;
        }
        var hasDocumentChanges = edit.TryGetProperty("documentChanges", out var targetDocumentChanges);
        var hasChanges = edit.TryGetProperty("changes", out var targetChanges);

        var targetPaths = new HashSet<string>(FileSystemPaths.Comparer);
        if (hasDocumentChanges)
        {
            foreach (var change in targetDocumentChanges.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object)
                {
                    KodoDiagnostics.LogDebug("Workspace edit rejected a malformed document change");
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (change.TryGetProperty("kind", out _))
                {
                    KodoDiagnostics.LogDebug("Code action requested a create/delete/rename resource operation; refusing");
                    SetLspFeatureStatus("That fix also creates, deletes or renames files, which Kodo will not do from a quick fix.");
                    return CodeActionOutcome.Failed;
                }
                if (!change.TryGetProperty("textDocument", out var textDocument) || textDocument.ValueKind != JsonValueKind.Object ||
                    !textDocument.TryGetProperty("uri", out var uriElement) || uriElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(uriElement.GetString()) ||
                    !change.TryGetProperty("edits", out var editsElement) || editsElement.ValueKind != JsonValueKind.Array)
                {
                    KodoDiagnostics.LogDebug("Workspace edit rejected an incomplete document change");
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                var uri = uriElement.GetString()!;
                if (!TryReadWorkspaceEditVersion(textDocument, out var expectedVersion))
                {
                    KodoDiagnostics.LogDebug("Workspace edit rejected a malformed text document version");
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (!TryGetWorkspaceEditFilePath(uri, out var targetPath))
                {
                    KodoDiagnostics.LogDebug("Workspace edit rejected a document change with an invalid URI");
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (expectedVersion.HasValue)
                {
                    var currentVersion = CurrentLspDocumentVersion(targetPath);
                    if (!WorkspaceEditVersionMatches(expectedVersion.Value, currentVersion))
                    {
                        KodoDiagnostics.LogDebug($"Workspace edit rejected stale or unknown document version for {targetPath}: expected={expectedVersion.Value} current={currentVersion?.ToString() ?? "<unknown>"}");
                        SetLspFeatureStatus("A file changed while the language server prepared the edit, so it was not applied.");
                        return CodeActionOutcome.Failed;
                    }
                }
                if (!IsWorkspaceEditTargetAllowed(targetPath, snapshot.FilePath, workspaceRoot))
                {
                    KodoDiagnostics.LogDebug($"Code action attempted to read or edit a file outside the LSP workspace: {targetPath}");
                    SetLspFeatureStatus("That fix targets a file outside this language server's workspace, so it was not applied.");
                    return CodeActionOutcome.Failed;
                }
                targetPaths.Add(targetPath);
            }
        }
        else if (hasChanges)
        {
            foreach (var property in targetChanges.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Array)
                {
                    KodoDiagnostics.LogDebug("Workspace edit rejected a malformed changes entry");
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (!TryGetWorkspaceEditFilePath(property.Name, out var targetPath) ||
                    !IsWorkspaceEditTargetAllowed(targetPath, snapshot.FilePath, workspaceRoot))
                {
                    KodoDiagnostics.LogDebug($"Code action attempted to read or edit a file outside the LSP workspace or used an invalid URI: {property.Name}");
                    SetLspFeatureStatus("That fix targets an invalid or outside file, so no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                targetPaths.Add(targetPath);
            }
        }

        if (!TryCollectRequiredWorkspaceEditConfirmations(edit, out var requiredConfirmations))
        {
            KodoDiagnostics.LogDebug("Workspace edit rejected malformed or unsupported change annotations");
            SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
            return CodeActionOutcome.Failed;
        }

        var openTabSources = await Dispatcher.UIThread.InvokeAsync(() =>
            OpenTabs
                .Where(t => !string.IsNullOrWhiteSpace(t.Path) && targetPaths.Contains(t.Path))
                .GroupBy(t => t.Path!, FileSystemPaths.Comparer)
                .Select(group =>
                {
                    var tab = group.First();
                    var document = tab.Document;
                    return (Path: group.Key, Tab: tab, Snapshot: document is null ? null : EditorTextSnapshotStore.GetSnapshot(document),
                        Document: document, Version: document?.Version, Content: tab.Content ?? string.Empty);
                })
                .ToList());
        var openTabs = new Dictionary<string, string>(FileSystemPaths.Comparer);
        var openTabDocuments = new Dictionary<string, (AvaloniaEdit.Document.TextDocument Document, AvaloniaEdit.Document.ITextSourceVersion Version)>(FileSystemPaths.Comparer);
        foreach (var source in openTabSources)
        {
            var content = source.Snapshot is null
                ? source.Content
                : await source.Snapshot.GetTextAsync().ConfigureAwait(false);
            openTabs[source.Path] = content;
            if (source.Document is not null && source.Version is not null)
                openTabDocuments[source.Path] = (source.Document, source.Version);
        }

        var closedFileSources = new Dictionary<string, ClosedFileEditSource>(FileSystemPaths.Comparer);
        if (scope is EditScope.ConfirmedProjectWide or EditScope.WorkspaceEdit)
        {
            foreach (var targetPath in targetPaths)
            {
                if (openTabs.ContainsKey(targetPath))
                    continue;
                try
                {
                    var fileSource = await Task.Run(() => ReadClosedFileForWorkspaceEdit(targetPath)).ConfigureAwait(false);
                    if (fileSource is not null)
                        closedFileSources[targetPath] = fileSource;
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Could not read {targetPath} for a workspace edit", ex);
                    await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus(
                        $"Could not safely read {Path.GetFileName(targetPath)} - no workspace edits were applied."));
                    return CodeActionOutcome.Failed;
                }
            }
        }

        if (hasDocumentChanges)
        {
            foreach (var change in targetDocumentChanges.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object)
                {
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (change.TryGetProperty("kind", out _))
                {
                    KodoDiagnostics.LogDebug("Code action requested a create/delete/rename resource operation; refusing");
                    SetLspFeatureStatus("That fix also creates, deletes or renames files, which Kodo will not do from a quick fix.");
                    return CodeActionOutcome.Failed;
                }
                if (!change.TryGetProperty("textDocument", out var textDocument) || textDocument.ValueKind != JsonValueKind.Object ||
                    !textDocument.TryGetProperty("uri", out var uriElement) || uriElement.ValueKind != JsonValueKind.String ||
                    !change.TryGetProperty("edits", out var editsElement))
                {
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                var uri = uriElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uri))
                {
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (!TryPlanDocumentEdit(uri, editsElement, snapshot, scope, openTabs, closedFileSources, plan, positionEncoding, workspaceRoot, out var failure)) return failure;
            }
        }
        else if (hasChanges)
        {
            foreach (var property in targetChanges.EnumerateObject())
            {
                var uri = property.Name;
                if (string.IsNullOrWhiteSpace(uri) || property.Value.ValueKind != JsonValueKind.Array)
                {
                    SetLspFeatureStatus("The language server sent an invalid edit - no changes were applied.");
                    return CodeActionOutcome.Failed;
                }
                if (!TryPlanDocumentEdit(uri, property.Value, snapshot, scope, openTabs, closedFileSources, plan, positionEncoding, workspaceRoot, out var failure)) return failure;
            }
        }
        else
        {
            return CodeActionOutcome.Failed;
        }

        if (plan.Count == 0) return CodeActionOutcome.NoActions;

        if (requiredConfirmations.Count > 0)
        {
            var shownConfirmations = requiredConfirmations.Take(8).Select(confirmation =>
            {
                var label = TruncateStatusText(confirmation.Label, 100);
                var description = string.IsNullOrWhiteSpace(confirmation.Description)
                    ? string.Empty
                    : $" — {TruncateStatusText(confirmation.Description, 150)}";
                return $"  • {label}{description}";
            }).ToList();
            var moreConfirmations = requiredConfirmations.Count - shownConfirmations.Count;
            var body = "The language server marked these changes as requiring your confirmation:\n\n" +
                       string.Join("\n", shownConfirmations) +
                       (moreConfirmations > 0 ? $"\n  … and {moreConfirmations} more" : string.Empty);
            if (!await ShowConfirmationDialogAsync("Confirm language server changes", body, "Apply", "Cancel", isDestructive: false).ConfigureAwait(false))
            {
                KodoDiagnostics.LogDebug("Workspace edit cancelled: user declined required change annotations");
                return CodeActionOutcome.Cancelled;
            }
        }

        if (scope == EditScope.ConfirmedProjectWide)
        {
            var closedFiles = plan
                .Where(p => !p.IsActive && !openTabs.Keys.Any(open => IsSameDocument(open, p.Path)))
                .Select(p => p.Path)
                .Distinct()
                .ToList();
            if (closedFiles.Count > 0)
            {
                var shown = closedFiles.Take(8).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n));
                var more = closedFiles.Count - shown.Count();
                var body = $"This will also change {closedFiles.Count} file(s) that are not open:\n\n"
                           + string.Join("\n", shown.Select(n => "  • " + n))
                           + (more > 0 ? $"\n  … and {more} more" : string.Empty);
                if (!await ShowConfirmationDialogAsync("Apply to other files?", body, "Apply", "Cancel", isDestructive: false).ConfigureAwait(false))
                {
                    KodoDiagnostics.LogDebug("Workspace edit cancelled: user declined the project-wide file list");
                    return CodeActionOutcome.Cancelled;
                }
            }
        }

        var openDocumentsStillCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!AreCodeActionOpenTargetsCurrentOnUi(snapshot, targetPaths)) return false;
            var requestDocumentIsCurrent = snapshot.Tab is { } tab && OpenTabs.Contains(tab) &&
                ReferenceEquals(tab.Document, snapshot.Document) && ReferenceEquals(snapshot.Document.Version, snapshot.DocumentVersion) &&
                IsSameDocument(tab.Path, snapshot.FilePath);
            var activePlanTargetsSnapshot = plan.Any(entry => entry.IsActive && IsSameDocument(entry.Path, snapshot.FilePath));
            if (!requestDocumentIsCurrent && activePlanTargetsSnapshot) return false;
            foreach (var source in openTabSources)
            {
                if (!OpenTabs.Contains(source.Tab)) return false;
                if (source.Document is not null)
                {
                    if (!ReferenceEquals(source.Tab.Document, source.Document) ||
                        !ReferenceEquals(source.Document.Version, source.Version))
                        return false;
                }
                else if (!string.Equals(source.Tab.Content, source.Content, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        });
        if (!openDocumentsStillCurrent)
        {
            KodoDiagnostics.LogDebug("Code action abandoned: an open document changed before the workspace edit could be applied");
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus("A file changed - the fix was not applied."));
            return CodeActionOutcome.Failed;
        }

        var openTabSnapshot = openTabs;

        var diskCommit = await Task.Run<WorkspaceDiskCommit>(async () =>
        {
            var writes = new List<CodeActionPlan>();
            foreach (var entry in plan)
            {
                if (openTabSnapshot.TryGetValue(entry.Path, out var tabContent))
                {
                    if (!string.Equals(tabContent, entry.Original, StringComparison.Ordinal))
                    {
                        KodoDiagnostics.LogDebug($"Code action abandoned: tab {entry.Path} changed since the edit was computed");
                        return new WorkspaceDiskCommit("Another open file changed - the fix was not applied.", Array.Empty<CodeActionPlan>());
                    }
                    continue;
                }
                if (entry.IsActive) continue;
                try
                {
                    var info = new FileInfo(entry.Path);
                    if (!info.Exists || entry.LastWriteTimeUtc is not { } expectedWriteTime || entry.LastLength is not { } expectedLength ||
                        info.LastWriteTimeUtc != expectedWriteTime || info.Length != expectedLength || entry.Encoding is null ||
                        !string.Equals(File.ReadAllText(entry.Path, entry.Encoding), entry.Original, StringComparison.Ordinal))
                    {
                        KodoDiagnostics.LogDebug($"Code action abandoned: {entry.Path} changed on disk since the edit was computed");
                        return new WorkspaceDiskCommit("A file on disk changed - the fix was not applied.", Array.Empty<CodeActionPlan>());
                    }
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Code action abandoned: could not re-read {entry.Path}", ex);
                    return new WorkspaceDiskCommit($"Could not safely verify {Path.GetFileName(entry.Path)} - no workspace edits were applied.", Array.Empty<CodeActionPlan>());
                }
                writes.Add(entry);
            }
            var writtenFiles = new List<CodeActionPlan>();
            try
            {
                foreach (var write in writes)
                {
                    await WriteTextFileAtomicallyAsync(
                        write.Path, write.Updated, write.Encoding!, write.LastWriteTimeUtc, write.LastLength,
                        () => Task.FromResult(ClosedFileMatchesSnapshot(
                            write.Path, write.Original, write.Encoding!, write.LastWriteTimeUtc, write.LastLength)));
                    writtenFiles.Add(write);
                }
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Workspace edit disk commit failed; rolling back earlier file writes", ex);
                var rollbackSucceeded = await RollbackClosedWorkspaceEditsAsync(writtenFiles);
                var message = rollbackSucceeded
                    ? "The fix could not be written - earlier file changes were rolled back."
                    : "The fix could not be fully written or rolled back; check the affected files. See the log for details.";
                return new WorkspaceDiskCommit(message, Array.Empty<CodeActionPlan>());
            }
            return new WorkspaceDiskCommit(null, writtenFiles);
        });

        if (diskCommit.Failure is { } diskFailure)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus(diskFailure));
            return CodeActionOutcome.Failed;
        }

        var committed = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!AreCodeActionOpenTargetsCurrentOnUi(snapshot, targetPaths))
            {
                KodoDiagnostics.LogDebug("Code action abandoned: an open target changed after the language server prepared the edit");
                SetLspFeatureStatus("An open file changed - the fix was not applied.");
                return false;
            }
            var requestDocumentIsCurrent = snapshot.Tab is { } requestTab && OpenTabs.Contains(requestTab) &&
                ReferenceEquals(requestTab.Document, snapshot.Document) && ReferenceEquals(snapshot.Document.Version, snapshot.DocumentVersion) &&
                IsSameDocument(requestTab.Path, snapshot.FilePath);
            var activePlanTargetsSnapshot = plan.Any(entry => entry.IsActive && IsSameDocument(entry.Path, snapshot.FilePath));
            if (!requestDocumentIsCurrent && activePlanTargetsSnapshot)
            {
                KodoDiagnostics.LogDebug("Code action abandoned: the active document changed before the edit could be applied");
                SetLspFeatureStatus("The file changed - the fix was not applied.");
                return false;
            }

            foreach (var entry in plan)
            {
                var tab = OpenTabs.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Path) && IsSameDocument(t.Path, entry.Path));
                if (tab is not null)
                {
                    var isUnchanged = openTabDocuments.TryGetValue(entry.Path, out var capturedDocument)
                        ? ReferenceEquals(tab.Document, capturedDocument.Document) && ReferenceEquals(capturedDocument.Document.Version, capturedDocument.Version)
                        : string.Equals(tab.Content, entry.Original, StringComparison.Ordinal);
                    if (!isUnchanged)
                    {
                        KodoDiagnostics.LogDebug($"Code action abandoned: tab {entry.Path} changed since the edit was computed");
                        SetLspFeatureStatus("Another open file changed - the fix was not applied.");
                        return false;
                    }
                }
            }

            foreach (var entry in plan)
            {
                if (entry.IsActive)
                {
                    var activeTab = snapshot.Tab;
                    if (activeTab is null || !ReferenceEquals(activeTab.Document, snapshot.Document) ||
                        !IsSameDocument(activeTab.Path, entry.Path))
                    {
                        KodoDiagnostics.LogDebug("Code action abandoned: captured tab no longer owns the target document");
                        SetLspFeatureStatus("The file changed - the fix was not applied.");
                        return false;
                    }
                    ApplyLspEditsToDocument(snapshot.Document, entry.Edits, [PrimaryEditorTextBox, SecondaryEditorTextBox]);
                    activeTab.IsDirty = true;
                    activeTab.Content = string.Empty;
                    continue;
                }
                var tab = OpenTabs.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Path) && IsSameDocument(t.Path, entry.Path));
                if (tab is not null)
                {
                    if (tab.Document is { } document)
                    {
                        ApplyLspEditsToDocument(document, entry.Edits,
                            [PrimaryEditorTextBox, SecondaryEditorTextBox]);
                        tab.Content = string.Empty;
                    }
                    else
                    {
                        tab.Content = entry.Updated;
                    }
                    tab.IsDirty = true;
                    var path = NormalizeFilePath(entry.Path);
                    lock (_lspOpenLock)
                    {
                        if (_lspOpenDocuments.Contains(path))
                        {
                            _lspPendingTexts[path] = string.Empty;
                            _lspFullSyncChangedAt[path] = Environment.TickCount64;
                        }
                    }
                    continue;
                }
            }
            if (plan.Any(entry => !entry.IsActive && OpenTabs.Any(tab => !string.IsNullOrWhiteSpace(tab.Path) && IsSameDocument(tab.Path, entry.Path))))
                ArmLspDidChangeTimer();
            onCommittedVersionCaptured?.Invoke(snapshot.Document.Version, snapshot.Editor?.TextArea.Caret.Offset ?? snapshot.Caret);
            return true;
        });


        if (!committed && diskCommit.WrittenFiles.Count > 0)
        {
            var rollbackSucceeded = await RollbackClosedWorkspaceEditsAsync(diskCommit.WrittenFiles).ConfigureAwait(false);
            if (!rollbackSucceeded)
                await Dispatcher.UIThread.InvokeAsync(() => SetLspFeatureStatus(
                    "A file changed before the quick fix could finish, and rollback was incomplete. Check the affected files."));
        }

        return committed ? CodeActionOutcome.Applied : CodeActionOutcome.Failed;
    }

    internal static async Task<bool> RollbackClosedWorkspaceEditsAsync(IReadOnlyList<CodeActionPlan> writtenFiles)
    {
        var succeeded = true;
        for (var index = writtenFiles.Count - 1; index >= 0; index--)
        {
            var entry = writtenFiles[index];
            try
            {
                if (entry.Encoding is null)
                {
                    succeeded = false;
                    continue;
                }

                var info = new FileInfo(entry.Path);
                if (!info.Exists || !string.Equals(File.ReadAllText(entry.Path, entry.Encoding), entry.Updated, StringComparison.Ordinal))
                {
                    KodoDiagnostics.LogDebug($"Could not roll back {entry.Path}: contents no longer match the applied edit");
                    succeeded = false;
                    continue;
                }

                await WriteTextFileAtomicallyAsync(
                    entry.Path,
                    entry.Original,
                    entry.Encoding,
                    info.LastWriteTimeUtc,
                    info.Length,
                    () => Task.FromResult(ClosedFileMatchesSnapshot(
                        entry.Path, entry.Updated, entry.Encoding, info.LastWriteTimeUtc, info.Length)));
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"Could not roll back workspace edit for {entry.Path}", ex);
                succeeded = false;
            }
        }

        return succeeded;
    }

    private bool TryPlanDocumentEdit(
        string uri,
        JsonElement editsElement,
        CodeActionSnapshot snapshot,
        EditScope scope,
        IReadOnlyDictionary<string, string> openTabs,
        IReadOnlyDictionary<string, ClosedFileEditSource> closedFileSources,
        List<CodeActionPlan> plan,
        string? positionEncoding,
        string? workspaceRoot,
        out CodeActionOutcome failure)
    {
        failure = CodeActionOutcome.Failed;
        if (!TryGetWorkspaceEditFilePath(uri, out var targetPath)) return false;
        if (!IsWorkspaceEditTargetAllowed(targetPath, snapshot.FilePath, workspaceRoot))
        {
            KodoDiagnostics.LogDebug($"Code action attempted to edit a file outside the LSP workspace: {targetPath}");
            SetLspFeatureStatus("That fix targets a file outside this language server's workspace, so it was not applied.");
            return false;
        }
        var isActive = IsSameDocument(uri, snapshot.FilePath);
        var existingIndex = plan.FindIndex(entry => IsSameDocument(entry.Path, targetPath));
        var existingPlan = existingIndex >= 0 ? plan[existingIndex] : null;

        string original;
        System.Text.Encoding? diskEncoding = null;
        DateTime? diskWriteTimeUtc = null;
        long? diskLength = null;
        if (existingPlan is not null)
        {
            original = existingPlan.Updated;
        }
        else if (isActive)
        {
            original = snapshot.Text;
        }
        else
        {
            var openMatch = openTabs.FirstOrDefault(kv => IsSameDocument(kv.Key, targetPath));
            if (openMatch.Key is not null)
            {
                original = openMatch.Value;
            }
            else if (scope is EditScope.ConfirmedProjectWide or EditScope.WorkspaceEdit &&
                     closedFileSources.TryGetValue(targetPath, out var fileSource))
            {
                original = fileSource.Content;
                diskEncoding = fileSource.Encoding;
                diskWriteTimeUtc = fileSource.LastWriteTimeUtc;
                diskLength = fileSource.Length;
            }
            else
            {
                KodoDiagnostics.LogDebug($"Code action also edits {targetPath}, which is not open in a tab; refusing");
                SetLspFeatureStatus($"That fix also changes {Path.GetFileName(targetPath)}, which is not open - so nothing was changed.");
                return false;
            }
        }

        if (!TryReadLspTextEdits(original, editsElement, positionEncoding ?? "utf-16", out var parsed, out var updated))
        {
            KodoDiagnostics.LogDebug($"Code action edits for {targetPath} were out of range or overlapping");
            SetLspFeatureStatus("That fix no longer matches the file and was not applied.");
            return false;
        }

        if (string.Equals(original, updated, StringComparison.Ordinal)) return true;
        if (existingPlan is not null)
        {
            var editsFromInitial = CreateSingleLspEdit(existingPlan.Original, updated);
            plan[existingIndex] = new CodeActionPlan(
                targetPath, existingPlan.Original, updated, isActive, editsFromInitial,
                existingPlan.Encoding, existingPlan.LastWriteTimeUtc, existingPlan.LastLength);
        }
        else
        {
            plan.Add(new CodeActionPlan(targetPath, original, updated, isActive, parsed,
                diskEncoding, diskWriteTimeUtc, diskLength));
        }
        return true;
    }

    private static ClosedFileEditSource? ReadClosedFileForWorkspaceEdit(string path)
    {
        var beforeRead = new FileInfo(path);
        if (!beforeRead.Exists)
            return null;

        var beforeWriteTime = beforeRead.LastWriteTimeUtc;
        var beforeLength = beforeRead.Length;
        var encoding = CreateStrictWorkspaceEditEncoding(SearchEngine.DetectFileEncoding(path));
        var content = File.ReadAllText(path, encoding);
        var afterRead = new FileInfo(path);
        if (!afterRead.Exists || afterRead.LastWriteTimeUtc != beforeWriteTime || afterRead.Length != beforeLength)
            throw new IOException($"{path} changed while its workspace edit was being prepared.");

        return new ClosedFileEditSource(content, encoding, beforeWriteTime, beforeLength);
    }

    private static List<(int Start, int End, string NewText)> CreateSingleLspEdit(string original, string updated)
    {
        var prefix = 0;
        var prefixLimit = Math.Min(original.Length, updated.Length);
        while (prefix < prefixLimit && original[prefix] == updated[prefix]) prefix++;
        if (prefix > 0 && prefix < original.Length && char.IsHighSurrogate(original[prefix - 1]) && char.IsLowSurrogate(original[prefix])) prefix--;
        if (prefix > 0 && prefix < updated.Length && char.IsHighSurrogate(updated[prefix - 1]) && char.IsLowSurrogate(updated[prefix])) prefix--;
        var originalEnd = original.Length;
        var updatedEnd = updated.Length;
        while (originalEnd > prefix && updatedEnd > prefix && original[originalEnd - 1] == updated[updatedEnd - 1])
        {
            originalEnd--;
            updatedEnd--;
        }
        if (originalEnd < original.Length && originalEnd > 0 && char.IsHighSurrogate(original[originalEnd - 1]) && char.IsLowSurrogate(original[originalEnd]))
        {
            originalEnd++;
            updatedEnd++;
        }
        return new List<(int Start, int End, string NewText)> { (prefix, originalEnd, updated[prefix..updatedEnd]) };
    }


    private Popup? _codeActionPopup;

    private Task<CodeActionItem?> PresentCodeActionsAsync(List<CodeActionItem> actions, CodeActionSnapshot snapshot)
    {
        var completion = new TaskCompletionSource<CodeActionItem?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            _codeActionPopup?.IsOpen = false;

            if (snapshot.Editor is null || snapshot.Pane is null ||
                !ReferenceEquals(_activeEditorPane, snapshot.Pane) || !ReferenceEquals(EditorTextBox, snapshot.Editor) ||
                !ReferenceEquals(snapshot.Pane.ActiveTab, snapshot.Tab) || !ReferenceEquals(snapshot.Editor.Document, snapshot.Document) ||
                !ReferenceEquals(snapshot.Document.Version, snapshot.DocumentVersion) || !IsCodeActionFilePathCurrent(snapshot.FilePath) ||
                snapshot.Editor.TextArea.Caret.Offset != snapshot.Caret)
            {
                completion.TrySetResult(null);
                return;
            }

            var requestEditor = snapshot.Editor;
            var textView = requestEditor.TextArea.TextView;
            var firstEnabled = 0;
            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i].IsEnabled) { firstEnabled = i; break; }
            }

            var list = new ListBox
            {
                ItemsSource = actions,
                SelectedIndex = firstEnabled,
                ItemTemplate = new FuncDataTemplate<CodeActionItem>((item, _) => BuildCodeActionRow(item), supportsRecycling: false)
            };
            list.Classes.Add("codeactions");

            var popup = new Popup
            {
                Child = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(12),
                    Child = new Border
                    {
                        Background = CardBrush,
                        BorderBrush = SurfaceBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = KodoDesignTokens.CardRadius,
                        Padding = new Thickness(16, 12),
                        MaxWidth = 520,
                        MaxHeight = 420,
                        Child = list
                    }
                },
                PlacementTarget = textView,
                Placement = PlacementMode.AnchorAndGravity,
                PlacementAnchor = PopupAnchor.TopLeft,
                PlacementGravity = PopupGravity.BottomRight,
                IsLightDismissEnabled = true,
                WindowManagerAddShadowHint = false,
                CustomPopupPlacementCallback = placement =>
                    PlaceCodeActionMenu(placement.PopupSize, placement.AnchorRectangle.Size, requestEditor, textView)
            };
            _codeActionPopup = popup;

            void Finish(CodeActionItem? picked)
            {
                completion.TrySetResult(picked);
                popup.IsOpen = false;
                _codeActionPopup = null;
                if (ReferenceEquals(_activeEditorPane, snapshot.Pane) && ReferenceEquals(EditorTextBox, requestEditor) &&
                    ReferenceEquals(snapshot.Pane.ActiveTab, snapshot.Tab))
                    requestEditor.Focus();
            }

            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedItem is CodeActionItem item && item.IsEnabled) Finish(item);
            };

            list.KeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Enter:
                        if (list.SelectedItem is CodeActionItem enter && enter.IsEnabled) Finish(enter);
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        Finish(null);
                        e.Handled = true;
                        break;
                }
            };

            list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
                if (FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: CodeActionItem item }) return;
                if (item.IsEnabled) Finish(item);
            }, RoutingStrategies.Bubble, handledEventsToo: true);

            popup.Opened += (_, _) => list.Focus();

            popup.Closed += (_, _) =>
            {
                _codeActionPopup = null;
                completion.TrySetResult(null);
            };

            popup.IsOpen = true;
        });

        return completion.Task;
    }

    private Point PlaceCodeActionMenu(Size popupSize, Size targetSize, TextEditor requestEditor, AvaloniaEdit.Rendering.TextView textView)
    {
        var fallback = new Point(8, Math.Max(0, Math.Min(24, targetSize.Height - popupSize.Height)));
        try
        {
            var caret = requestEditor.TextArea.Caret;
            var lineTop = textView.GetVisualPosition(
                new AvaloniaEdit.TextViewPosition(caret.Line, 1),
                AvaloniaEdit.Rendering.VisualYPosition.LineTop).Y - textView.ScrollOffset.Y;
            if (double.IsNaN(lineTop) || double.IsInfinity(lineTop)) return fallback;

            var below = lineTop + textView.DefaultLineHeight + popupSize.Height;
            var y = below <= targetSize.Height - 8
                ? lineTop + textView.DefaultLineHeight
                : Math.Max(4, lineTop - popupSize.Height);
            y = Math.Clamp(y, 4, Math.Max(4, targetSize.Height - popupSize.Height));
            return new Point(8, y);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Could not position the code action menu at the caret", ex);
            return fallback;
        }
    }

    private Control BuildCodeActionRow(CodeActionItem action)
    {
        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = action.IsQuickFix ? AccentBrush : SurfaceBorderBrush,
            Margin = new Thickness(0, 2, 10, 2)
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = action.DisplayTitle,
            FontSize = 13,
            Foreground = action.IsEnabled ? PrimaryTextBrush : MutedTextBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var subtitle = action.IsEnabled ? action.Detail : action.DisabledReason;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                FontStyle = action.IsEnabled ? FontStyle.Normal : FontStyle.Italic,
                Foreground = MutedTextBrush,
                Opacity = 0.85,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { bar, text }
        };
    }

    private static T? FindAncestor<T>(Visual? visual) where T : class
    {
        while (visual is not null)
        {
            if (visual is T match) return match;
            visual = visual.GetVisualParent();
        }
        return null;
    }
}
