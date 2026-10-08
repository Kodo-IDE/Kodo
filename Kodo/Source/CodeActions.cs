// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

internal enum CodeActionScope
{
    QuickFix,

    All
}

internal sealed class CodeActionItem
{
    public required string Title { get; init; }

    public required string Kind { get; init; }

    public required JsonElement Raw { get; init; }

    public JsonElement? Edit { get; set; }

    public string? Command { get; set; }

    public JsonElement? CommandArguments { get; set; }

    public JsonElement? Data { get; set; }

    public bool IsPreferred { get; init; }

    public string? DisabledReason { get; set; }

    public bool IsEnabled => DisabledReason is null;

    public bool IsQuickFix => Kind.StartsWith("quickfix", StringComparison.OrdinalIgnoreCase);

    public bool IsFixAll => Kind.EndsWith(".fixAll", StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle => IsPreferred && !IsQuickFix ? $"{Title}  (preferred)" : Title;
}

public partial class MainWindow
{
    private const int CodeActionRequestTimeoutSeconds = 20;
    private static readonly TimeSpan CodeActionDiagnosticsWait = TimeSpan.FromMilliseconds(200);

    private enum CodeActionOutcome
    {
        Applied,
        NoActions,
        Cancelled,
        Failed
    }

    private async Task<bool> RunCodeActionsAsync(CodeActionScope scope)
    {
        if (EditorTextBox?.Document is null) return false;
        var filePath = _currentFilePath;
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var client = ResolveCodeActionClient(filePath);
            if (client is null)
            {
                SetLspFeatureStatus("Language server is not ready yet - try again in a moment.");
                return false;
            }

            var snapshot = await CaptureCodeActionSnapshotAsync(filePath).ConfigureAwait(false);
            if (snapshot is null)
            {
                SetLspFeatureStatus("The file changed while the quick fix was loading - try again.");
                return false;
            }

            await FlushLspDocumentAsync(filePath, snapshot.Text).ConfigureAwait(false);

            var actions = await RequestCodeActionsAsync(client, snapshot, scope).ConfigureAwait(false);
            if (actions.Count == 0)
            {
                SetLspFeatureStatus(scope == CodeActionScope.QuickFix
                    ? "No quick fixes available here."
                    : "No code actions available here.");
                return false;
            }

            var enabled = actions.Where(a => a.IsEnabled).ToList();
            if (enabled.Count == 0)
            {
                var why = actions.Select(a => a.DisabledReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                SetLspFeatureStatus(string.IsNullOrWhiteSpace(why)
                    ? "No quick fixes available here."
                    : TruncateStatusText($"No fix available: {why}", 90));
                return false;
            }

            var chosen = enabled.Count == 1
                ? enabled[0]
                : await PresentCodeActionsAsync(actions, snapshot).ConfigureAwait(false);

            if (chosen is null) return false;

            return await ApplyCodeActionAsync(client, chosen, snapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetLspFeatureStatus("The language server did not answer in time.");
            return false;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Code action pipeline failed", ex);
            SetLspFeatureStatus("Quick fix failed - see the log for details.");
            return false;
        }
    }

    private sealed record CodeActionSnapshot(string FilePath, string Text, int Caret, int Start, int Length);

    private async Task<CodeActionSnapshot?> CaptureCodeActionSnapshotAsync(string filePath)
    {
        var currentText = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) ? EditorTextBox?.Document?.Text : null);
        if (currentText is null) return null;
        await FlushLspDocumentAsync(filePath, currentText).ConfigureAwait(false);

        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = EditorTextBox?.Document;
            if (doc is null || !FileSystemPaths.Equals(_currentFilePath, filePath)) return null;
            var caret = EditorTextBox!.TextArea.Caret.Offset;
            var selection = EditorTextBox.TextArea.Selection;
            var segment = selection.IsEmpty ? null : selection.SurroundingSegment;
            var start = segment?.Offset ?? caret;
            var length = segment is null ? 1 : Math.Max(1, segment.EndOffset - segment.Offset);

            start = Math.Clamp(start, 0, doc.TextLength);
            length = Math.Clamp(length, 1, Math.Max(1, doc.TextLength - start));
            return new CodeActionSnapshot(filePath, doc.Text, caret, start, length);
        });
        if (snapshot is null) return null;
        if (CurrentLspDocumentVersion(filePath) is int version)
            await WaitForLspDiagnosticsVersionAsync(NormalizeFilePath(filePath), version, CodeActionDiagnosticsWait).ConfigureAwait(false);
        var isCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is not null &&
            string.Equals(EditorTextBox.Document.Text, snapshot.Text, StringComparison.Ordinal));
        if (!isCurrent) return null;
        return snapshot;
    }

    private int? CurrentLspDocumentVersion(string filePath)
    {
        lock (_lspOpenLock)
            return _lspDocumentVersions.TryGetValue(NormalizeFilePath(filePath), out var version) ? version : null;
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
        var (startLine, startChar) = LspPath.OffsetToLspPosition(snapshot.Text, snapshot.Start, client.PositionEncoding);
        var (endLine, endChar) = LspPath.OffsetToLspPosition(snapshot.Text, snapshot.Start + snapshot.Length, client.PositionEncoding);

        var result = new List<JsonElement>();
        int? sentVersion;
        lock (_lspOpenLock)
            sentVersion = _lspDocumentVersions.TryGetValue(NormalizeFilePath(snapshot.FilePath), out var version) ? version : null;
        lock (_lspDiagnosticsLock)
        {
            if (sentVersion.HasValue && _lspDiagnosticVersions.TryGetValue(NormalizeFilePath(snapshot.FilePath), out var diagnosticVersion) && diagnosticVersion < sentVersion.Value)
                return result;
            if (!_lspRawDiagnostics.TryGetValue(NormalizeFilePath(snapshot.FilePath), out var raws)) return result;
            foreach (var raw in raws)
            {
                if (!raw.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object) continue;
                if (!range.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object) continue;
                var dLine = start.TryGetProperty("line", out var dl) && dl.ValueKind == JsonValueKind.Number ? dl.GetInt32() : 0;
                var dChar = start.TryGetProperty("character", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : 0;
                var onLine = dLine >= startLine && dLine <= endLine;
                if (!onLine) continue;
                if (dLine == startLine && dChar > endChar) continue;
                result.Add(raw.Clone());
            }
        }
        return result;
    }

    private static List<CodeActionItem> ParseCodeActions(JsonElement array, CodeActionScope scope)
    {
        var items = new List<CodeActionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
                !kind.StartsWith("quickfix", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add($"{kind}|{title}")) continue;

            string? disabledReason = null;
            if (element.TryGetProperty("disabled", out var disabled))
            {
                if (disabled.ValueKind == JsonValueKind.True) disabledReason = "not available in this context";
                else if (disabled.ValueKind == JsonValueKind.Object && disabled.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    disabledReason = reason.GetString();
            }

            var item = new CodeActionItem
            {
                Title = title,
                Kind = kind,
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
                    if (command.TryGetProperty("arguments", out var args)) item.CommandArguments = args.Clone();
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

    private async Task ResolveCodeActionIfNeededAsync(LspClient client, CodeActionItem action)
    {
        if (action.Edit is not null || action.Data is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CodeActionRequestTimeoutSeconds));
            var resolved = await client.SendRequestAsync("codeAction/resolve", action.Raw, cts.Token).ConfigureAwait(false);
            if (resolved is not { ValueKind: JsonValueKind.Object }) return;
            if (resolved.Value.TryGetProperty("edit", out var edit) && edit.ValueKind == JsonValueKind.Object)
                action.Edit = edit.Clone();
            if (resolved.Value.TryGetProperty("command", out var command))
            {
                if (command.ValueKind == JsonValueKind.String)
                    action.Command = command.GetString();
                else if (command.ValueKind == JsonValueKind.Object && command.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    action.Command = name.GetString();
                    if (command.TryGetProperty("arguments", out var args)) action.CommandArguments = args.Clone();
                }
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("codeAction/resolve failed", ex);
        }
    }

    private async Task<bool> ApplyCodeActionAsync(LspClient client, CodeActionItem action, CodeActionSnapshot snapshot)
    {
        await ResolveCodeActionIfNeededAsync(client, action).ConfigureAwait(false);

        if (action.Edit is null && string.IsNullOrWhiteSpace(action.Command))
        {
            KodoDiagnostics.LogDebug($"Code action '{action.Title}' carries neither an edit nor a command; ignoring");
            return false;
        }

        if (action.Edit is { } edit)
        {
            var applied = await ApplyWorkspaceEditAsync(edit, snapshot).ConfigureAwait(false);
            if (applied == CodeActionOutcome.Applied)
            {
                SetLspFeatureStatus($"Applied '{TruncateStatusText(action.Title, 60)}'.");
                return true;
            }
            if (applied == CodeActionOutcome.Failed) return false;
        }

        if (!string.IsNullOrWhiteSpace(action.Command))
        {
            var executeParams = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = action.Command,
                ["arguments"] = action.CommandArguments
            };
            try
            {
                using var executeCts = new CancellationTokenSource(TimeSpan.FromSeconds(CodeActionRequestTimeoutSeconds));
                await client.SendRequestAsync("workspace/executeCommand", executeParams, executeCts.Token).ConfigureAwait(false);
                SetLspFeatureStatus($"Ran '{TruncateStatusText(action.Title, 60)}'.");
                return true;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("workspace/executeCommand failed for a code action", ex);
                SetLspFeatureStatus("The language server command failed.");
                return false;
            }
        }

        return false;
    }

    private sealed record CodeActionPlan(
        string Path,
        string Original,
        string Updated,
        bool IsActive,
        List<(int Start, int End, string NewText)> Edits);

    private enum EditScope
    {
        OpenDocumentsOnly,

        ConfirmedProjectWide,

        WorkspaceEdit
    }

    private async Task<bool> ApplyExternalWorkspaceEditAsync(JsonElement edit, string filePath, string text)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = EditorTextBox?.Document;
            if (doc is null || !FileSystemPaths.Equals(_currentFilePath, filePath)) return null;
            if (!string.Equals(doc.Text, text, StringComparison.Ordinal)) return null;
            return new CodeActionSnapshot(filePath, text, EditorTextBox!.TextArea.Caret.Offset, 0, 1);
        });
        if (snapshot is null)
        {
            KodoDiagnostics.LogDebug("Workspace edit abandoned: the document changed before it could be applied");
            return false;
        }
        return await ApplyWorkspaceEditAsync(edit, snapshot, EditScope.WorkspaceEdit).ConfigureAwait(false)
            == CodeActionOutcome.Applied;
    }

    private async Task<CodeActionOutcome> ApplyWorkspaceEditAsync(
        JsonElement edit,
        CodeActionSnapshot snapshot,
        EditScope scope = EditScope.OpenDocumentsOnly)
    {
        var plan = new List<CodeActionPlan>();

        var openTabs = await Dispatcher.UIThread.InvokeAsync(() =>
            OpenTabs
                .Where(t => !string.IsNullOrWhiteSpace(t.Path))
                .GroupBy(t => t.Path!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Content ?? string.Empty, StringComparer.OrdinalIgnoreCase));

        if (edit.TryGetProperty("documentChanges", out var documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (change.TryGetProperty("kind", out _))
                {
                    KodoDiagnostics.LogDebug("Code action requested a create/delete/rename resource operation; refusing");
                    SetLspFeatureStatus("That fix also creates, deletes or renames files, which Kodo will not do from a quick fix.");
                    return CodeActionOutcome.Failed;
                }
                if (!change.TryGetProperty("textDocument", out var textDocument) ||
                    !textDocument.TryGetProperty("uri", out var uriElement) ||
                    !change.TryGetProperty("edits", out var editsElement)) continue;
                var uri = uriElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uri)) continue;
                if (textDocument.TryGetProperty("version", out var expectedVersion) && expectedVersion.ValueKind == JsonValueKind.Number)
                {
                    var targetPath = FileUriToPath(uri);
                    var currentVersion = string.IsNullOrWhiteSpace(targetPath) ? null : CurrentLspDocumentVersion(targetPath);
                    if (currentVersion.HasValue && currentVersion.Value != expectedVersion.GetInt32())
                    {
                        KodoDiagnostics.LogDebug($"Workspace edit rejected stale document version for {targetPath}: expected={expectedVersion.GetInt32()} current={currentVersion.Value}");
                        SetLspFeatureStatus("A file changed while the language server prepared the edit, so it was not applied.");
                        return CodeActionOutcome.Failed;
                    }
                }

                if (!TryPlanDocumentEdit(uri, editsElement, snapshot, scope, openTabs, plan, out var failure)) return failure;
            }
        }
        else if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in changes.EnumerateObject())
            {
                var uri = property.Name;
                if (string.IsNullOrWhiteSpace(uri)) continue;
                if (!TryPlanDocumentEdit(uri, property.Value, snapshot, scope, openTabs, plan, out var failure)) return failure;
            }
        }
        else
        {
            return CodeActionOutcome.Failed;
        }

        if (plan.Count == 0) return CodeActionOutcome.NoActions;

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

        var committed = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var doc = EditorTextBox?.Document;
            if (doc is null) return false;
            if (!string.Equals(doc.Text, snapshot.Text, StringComparison.Ordinal))
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
                    if (!string.Equals(tab.Content, entry.Original, StringComparison.Ordinal))
                    {
                        KodoDiagnostics.LogDebug($"Code action abandoned: tab {entry.Path} changed since the edit was computed");
                        SetLspFeatureStatus("Another open file changed - the fix was not applied.");
                        return false;
                    }
                    continue;
                }
                if (entry.IsActive) continue;
                try
                {
                    if (!string.Equals(File.ReadAllText(entry.Path), entry.Original, StringComparison.Ordinal))
                    {
                        KodoDiagnostics.LogDebug($"Code action abandoned: {entry.Path} changed on disk since the edit was computed");
                        SetLspFeatureStatus("A file on disk changed - the fix was not applied.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Code action abandoned: could not re-read {entry.Path}", ex);
                    return false;
                }
            }

            foreach (var entry in plan)
            {
                if (entry.IsActive)
                {
                    ApplyLspEditsToActiveDocument(entry.Edits);
                    continue;
                }
                var tab = OpenTabs.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Path) && IsSameDocument(t.Path, entry.Path));
                if (tab is not null)
                {
                    tab.Content = entry.Updated;
                    tab.IsDirty = true;
                    var path = NormalizeFilePath(entry.Path);
                    lock (_lspOpenLock)
                    {
                        if (_lspOpenDocuments.Contains(path)) _lspPendingTexts[path] = string.Empty;
                    }
                    continue;
                }
                File.WriteAllText(entry.Path, entry.Updated);
            }
            if (plan.Any(entry => !entry.IsActive && OpenTabs.Any(tab => !string.IsNullOrWhiteSpace(tab.Path) && IsSameDocument(tab.Path, entry.Path))))
                ArmLspDidChangeTimer();
            return true;
        });


        return committed ? CodeActionOutcome.Applied : CodeActionOutcome.Failed;
    }

    private bool TryPlanDocumentEdit(
        string uri,
        JsonElement editsElement,
        CodeActionSnapshot snapshot,
        EditScope scope,
        IReadOnlyDictionary<string, string> openTabs,
        List<CodeActionPlan> plan,
        out CodeActionOutcome failure)
    {
        failure = CodeActionOutcome.Failed;
        var targetPath = FileUriToPath(uri);
        if (string.IsNullOrWhiteSpace(targetPath)) return false;
        var isActive = IsSameDocument(uri, snapshot.FilePath);
        var existingIndex = plan.FindIndex(entry => IsSameDocument(entry.Path, targetPath));
        var existingPlan = existingIndex >= 0 ? plan[existingIndex] : null;

        string original;
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
            else if (scope is EditScope.ConfirmedProjectWide or EditScope.WorkspaceEdit && File.Exists(targetPath))
            {
                try { original = File.ReadAllText(targetPath); }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Could not read {targetPath} for a workspace edit", ex);
                    return false;
                }
            }
            else
            {
                KodoDiagnostics.LogDebug($"Code action also edits {targetPath}, which is not open in a tab; refusing");
                SetLspFeatureStatus($"That fix also changes {Path.GetFileName(targetPath)}, which is not open - so nothing was changed.");
                return false;
            }
        }

        var client = GetLspClientForFile(targetPath);
        if (!TryReadLspTextEdits(original, editsElement, client?.PositionEncoding ?? "utf-16", out var parsed, out var updated))
        {
            KodoDiagnostics.LogDebug($"Code action edits for {targetPath} were out of range or overlapping");
            SetLspFeatureStatus("That fix no longer matches the file and was not applied.");
            return false;
        }

        if (string.Equals(original, updated, StringComparison.Ordinal)) return true;
        if (existingPlan is not null)
        {
            var editsFromInitial = CreateSingleLspEdit(existingPlan.Original, updated);
            plan[existingIndex] = new CodeActionPlan(targetPath, existingPlan.Original, updated, isActive, editsFromInitial);
        }
        else
        {
            plan.Add(new CodeActionPlan(targetPath, original, updated, isActive, parsed));
        }
        return true;
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

            var textView = EditorTextBox.TextArea.TextView;
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
                        CornerRadius = new CornerRadius(12),
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
                    PlaceCodeActionMenu(placement.PopupSize, placement.AnchorRectangle.Size, textView)
            };
            _codeActionPopup = popup;

            void Finish(CodeActionItem? picked)
            {
                completion.TrySetResult(picked);
                popup.IsOpen = false;
                _codeActionPopup = null;
                EditorTextBox?.Focus();
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

            list.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
                if (FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: CodeActionItem item }) return;
                if (item.IsEnabled) Finish(item);
            };

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

    private Point PlaceCodeActionMenu(Size popupSize, Size targetSize, AvaloniaEdit.Rendering.TextView textView)
    {
        var fallback = new Point(8, Math.Max(0, Math.Min(24, targetSize.Height - popupSize.Height)));
        try
        {
            var caret = EditorTextBox.TextArea.Caret;
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
        if (!action.IsEnabled && !string.IsNullOrWhiteSpace(action.DisabledReason))
        {
            text.Children.Add(new TextBlock
            {
                Text = action.DisabledReason,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
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
