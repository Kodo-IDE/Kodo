// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{
    private readonly LspManager _lspManager = new();
    private readonly Dictionary<string, int> _lspDocumentVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lspOpenDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lspMissingNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _lspDidChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private string? _pendingLspChangePath;
    private readonly object _lspPendingLock = new();
    private readonly Dictionary<string, List<LspRawDiagnostic>> _lspDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<LspClient> _lspSubscribedClients = new();
    private readonly object _lspDiagnosticsLock = new();
    private readonly HashSet<string> _lspPendingOpens = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lspOpenLock = new();
    private CancellationTokenSource? _lspHoverCts;
    private readonly object _lspHoverLock = new();
    private readonly Dictionary<string, Task<string?>> _lspPendingHovers = new();
    private readonly object _lspHoverCacheLock = new();
    private string _lastHoverKey = "";
    private DateTime _lastHoverTime = DateTime.MinValue;

    private sealed record LspRawDiagnostic(int StartLine, int StartChar, int EndLine, int EndChar, string Message, string Severity, string Code, string Source);

    private LoadedExtension? ResolveLspExtensionForFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || IsPlainTextFile(filePath) || HasNoFileExtension(filePath))
            return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        // Prefer explicit LSP fileExtensions, fallback to manifest extensions
        var candidates = LoadedExtensions.Where(e => e.HasLsp).ToList();
        // First try exact fileExtension match
        var match = candidates.FirstOrDefault(e => e.Lsp!.FileExtensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase)));
        if (match is not null) return match;
        // Fallback to top-level extensions (already copied into Lsp.FileExtensions if empty, but check anyway)
        match = candidates.FirstOrDefault(e => e.Extensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase)));
        return match;
    }

    private string GetWorkspaceRootForFile(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && !string.IsNullOrWhiteSpace(filePath) && IsPathInsideDirectory(filePath, _currentFolderPath))
            return _currentFolderPath;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        }
        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
            return _currentFolderPath;
        return Environment.CurrentDirectory;
    }

    private static string FilePathToUri(string filePath)
    {
        try { return new Uri(Path.GetFullPath(filePath)).AbsoluteUri; }
        catch { try { return new Uri(filePath, UriKind.Absolute).AbsoluteUri; } catch { return filePath; } }
    }

    private static string NormalizeFilePath(string pathOrUri)
    {
        try
        {
            var p = FixCorruptedPath(FileUriToPath(pathOrUri));
            if (Path.IsPathRooted(p))
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return FixCorruptedPath(pathOrUri); }
    }

    private static bool IsSameDocument(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            var na = NormalizeFilePath(a);
            var nb = NormalizeFilePath(b);
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static string FixCorruptedPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return p;
        if (p.Contains(@":\Users\", StringComparison.OrdinalIgnoreCase) && p.Contains(@"Kodo\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = p.IndexOf(@":\Users\", StringComparison.OrdinalIgnoreCase);
            if (idx > 1)
            {
                var drive = p[idx - 1];
                if (char.IsLetter(drive))
                {
                    var correct = string.Concat(drive.ToString(), @":\", p.Substring(idx + 2).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    try { return Path.GetFullPath(correct); } catch { return correct; }
                }
            }
        }
        return p;
    }

    private string GetLanguageId(LspConfiguration lsp, string? filePath)
    {
        if (lsp.Languages.Length > 0) return lsp.Languages[0];
        // Derive from extension if no language id
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ext)) return ext;
        }
        return "plaintext";
    }

    private void InitLspDocumentSync()
    {
        _lspDidChangeTimer.Tick += LspDidChangeTimer_OnTick;
    }

    private async void LspDidChangeTimer_OnTick(object? sender, EventArgs e)
    {
        _lspDidChangeTimer.Stop();
        string? path;
        lock (_lspPendingLock) { path = _pendingLspChangePath; _pendingLspChangePath = null; }
        if (string.IsNullOrWhiteSpace(path) || EditorTextBox?.Document is null) return;
        if (!string.Equals(path, _currentFilePath, StringComparison.OrdinalIgnoreCase)) return;
        var text = EditorTextBox.Document.Text;
        await LspNotifyDidChangeAsync(path, text).ConfigureAwait(false);
    }

    private void QueueLspDidChange(string filePath)
    {
        if (ResolveLspExtensionForFile(filePath) is null) return;
        lock (_lspPendingLock) _pendingLspChangePath = filePath;
        Dispatcher.UIThread.Post(() =>
        {
            _lspDidChangeTimer.Stop();
            _lspDidChangeTimer.Start();
        });
    }

    private async Task LspNotifyDidOpenAsync(string filePath, string content)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return;
        lock (_lspOpenLock)
        {
            if (_lspOpenDocuments.Contains(filePath) || _lspPendingOpens.Contains(filePath)) return;
            _lspPendingOpens.Add(filePath);
        }

        var workspace = GetWorkspaceRootForFile(filePath);
        LspClient client;
        try
        {
            client = await _lspManager.GetOrStartAsync(workspace, lspExt.Lsp).ConfigureAwait(false);
            SetupLspClientHandlers(client);
        }
        catch (FileNotFoundException ex)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            if (_lspMissingNotified.Add(lspExt.Id))
            {
                KodoDiagnostics.LogDebug($"LSP start missing for '{lspExt.Id}'", ex);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    ExtensionsStatusText = $"Language server '{lspExt.Lsp!.Command}' not found for '{lspExt.Name}'. Install it and ensure it is on PATH. Highlighting still works.";
                    await ShowWarningDialogAsync($"{lspExt.Name} – language server not found",
                        new FileNotFoundException($"{lspExt.Name} language server could not be started.\n\n'{lspExt.Lsp.Command}' was not found.\nCheck that {lspExt.Lsp.Command} is installed and available on PATH.\n\nHighlighting remains available.", ex));
                });
            }
            return;
        }
        catch (Exception ex)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            KodoDiagnostics.LogDebug($"LSP start failed for '{lspExt.Id}'", ex);
            await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = $"Language server '{lspExt.Lsp!.Command}' failed to start: {ex.Message}");
            return;
        }

        try
        {
            await client.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            KodoDiagnostics.LogDebug($"LSP initialize failed for '{lspExt.Id}'", ex);
            await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = $"Language server '{lspExt.Lsp!.Command}' initialization failed: {ex.Message}");
            return;
        }

        SetupLspClientHandlers(client);

        var uri = FilePathToUri(filePath);
        int version;
        lock (_lspOpenLock)
        {
            _lspPendingOpens.Remove(filePath);
            version = _lspDocumentVersions.TryGetValue(filePath, out var v) ? v + 1 : 1;
            _lspDocumentVersions[filePath] = version;
            _lspOpenDocuments.Add(filePath);
        }
        var languageId = GetLanguageId(lspExt.Lsp, filePath);

        var didOpenParams = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["uri"] = uri,
                ["languageId"] = languageId,
                ["version"] = version,
                ["text"] = content
            }
        };

        try
        {
            await client.SendNotificationAsync("textDocument/didOpen", didOpenParams).ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"LSP didOpen {uri} lang={languageId} ver={version}");
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP didOpen failed for {uri}", ex); }
    }

    private async Task LspNotifyDidChangeAsync(string filePath, string newContent)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        bool isOpen;
        lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
        if (!isOpen)
        {
            await LspNotifyDidOpenAsync(filePath, newContent).ConfigureAwait(false);
            return;
        }
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return;

        var uri = FilePathToUri(filePath);
        int version;
        lock (_lspOpenLock)
        {
            version = _lspDocumentVersions.TryGetValue(filePath, out var v) ? v + 1 : 1;
            _lspDocumentVersions[filePath] = version;
        }

        var didChangeParams = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = newContent } }
        };

        try
        {
            await client.SendNotificationAsync("textDocument/didChange", didChangeParams).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP didChange failed for {uri}", ex); }
    }

    private async Task LspNotifyDidCloseAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(filePath)) return;
            _lspOpenDocuments.Remove(filePath);
            _lspDocumentVersions.Remove(filePath);
            _lspPendingOpens.Remove(filePath);
        }
        lock (_lspDiagnosticsLock)
        {
            _lspDiagnostics.Remove(filePath);
            _lspDiagnostics.Remove(FilePathToUri(filePath));
        }

        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null) return;

        var uri = FilePathToUri(filePath);
        var didCloseParams = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri }
        };

        try
        {
            await client.SendNotificationAsync("textDocument/didClose", didCloseParams).ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"LSP didClose {uri}");
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP didClose failed for {uri}", ex); }
    }

    private async Task LspShutdownAllAsync()
    {
        _lspDidChangeTimer.Stop();
        var open = _lspOpenDocuments.ToList();
        foreach (var path in open)
        {
            try { await LspNotifyDidCloseAsync(path).ConfigureAwait(false); } catch { }
        }
        try { await _lspManager.ShutdownAllAsync("MainWindow shutdown").ConfigureAwait(false); } catch (Exception ex) { KodoDiagnostics.LogDebug("LSP shutdown all failed", ex); }
    }

    private void SetupLspClientHandlers(LspClient client)
    {
        lock (_lspDiagnosticsLock)
        {
            if (!_lspSubscribedClients.Add(client)) return;
        }
        client.OnNotification += HandleLspNotification;
        client.OnExit += code => Dispatcher.UIThread.Post(() => { _ = UpdateErrorHighlightingAsync(); });
    }

    private void HandleLspNotification(string method, JsonElement? @params)
    {
        if (method == "textDocument/publishDiagnostics")
            HandlePublishDiagnostics(@params);
    }

    private void HandlePublishDiagnostics(JsonElement? @params)
    {
        if (@params is null) return;
        try
        {
            var root = @params.Value;
            if (!root.TryGetProperty("uri", out var uriEl)) return;
            var uri = uriEl.GetString() ?? "";
            var filePath = FixCorruptedPath(FileUriToPath(uri));
            if (string.IsNullOrWhiteSpace(filePath)) filePath = FixCorruptedPath(uri);

            var diagnostics = new List<LspRawDiagnostic>();
            if (root.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in diags.EnumerateArray())
                {
                    var range = d.TryGetProperty("range", out var r) ? r : default;
                    var start = range.ValueKind == JsonValueKind.Object && range.TryGetProperty("start", out var s) ? s : default;
                    var end = range.ValueKind == JsonValueKind.Object && range.TryGetProperty("end", out var e) ? e : default;
                    var sLine = start.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
                    var sChar = start.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0;
                    var eLine = end.TryGetProperty("line", out var el) ? el.GetInt32() : sLine;
                    var eChar = end.TryGetProperty("character", out var ec) ? ec.GetInt32() : sChar + 1;
                    var msg = d.TryGetProperty("message", out var me) ? me.GetString() ?? "" : "";
                    var sev = d.TryGetProperty("severity", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt32() : 1;
                    var severity = sev switch { 1 => "error", 2 => "warning", 3 => "info", 4 => "hint", _ => "error" };
                    var code = d.TryGetProperty("code", out var ce) ? ce.ToString() : "";
                    // code may be object with value
                    if (d.TryGetProperty("code", out var ce2) && ce2.ValueKind == JsonValueKind.Object && ce2.TryGetProperty("value", out var cv)) code = cv.ToString();
                    else if (ce2.ValueKind == JsonValueKind.Number) code = ce2.GetInt32().ToString();
                    var source = d.TryGetProperty("source", out var se2) ? se2.GetString() ?? "lsp" : "lsp";
                    diagnostics.Add(new LspRawDiagnostic(sLine, sChar, eLine, eChar, msg, severity, code, source));
                }
            }

            lock (_lspDiagnosticsLock)
            {
                _lspDiagnostics[filePath] = diagnostics;
                // also store by uri for fallback
                _lspDiagnostics[uri] = diagnostics;
            }
            KodoDiagnostics.LogDebug($"LSP publishDiagnostics {filePath} count={diagnostics.Count} uri={uri}");
            for (var i = 0; i < Math.Min(diagnostics.Count, 3); i++)
                KodoDiagnostics.LogDebug($"  LSP diag {i}: [{diagnostics[i].StartLine}:{diagnostics[i].StartChar}-{diagnostics[i].EndLine}:{diagnostics[i].EndChar}] {diagnostics[i].Severity} {diagnostics[i].Message}");
            // Invalidate Insight cache so next UpdateErrorHighlightingAsync merges fresh LSP diagnostics (cache is text-based, not diagnostics-based)
            lock (_insightAnalysisCacheLock)
            {
                _cachedInsightAnalysisVersion = -1;
                _cachedInsightAnalysisText = null;
            }
            // Trigger UI refresh if current file (normalize both sides to handle file:///c%3A vs C:\ etc.)
            Dispatcher.UIThread.Post(() =>
            {
                var isActive = IsSameDocument(_currentFilePath, filePath) || IsSameDocument(_currentFilePath, uri);
                KodoDiagnostics.LogDebug($"LSP publishDiagnostics UI refresh isActive={isActive} current={_currentFilePath} diagFile={filePath} uri={uri} normCurrent={NormalizeFilePath(_currentFilePath ?? "")} normDiag={NormalizeFilePath(filePath)}");
                if (isActive)
                {
                    _ = UpdateErrorHighlightingAsync();
                }
                else
                {
                    // Still ensure diagnostics for that file will be shown when it becomes active
                    KodoDiagnostics.LogDebug($"LSP diagnostics stored for inactive file {filePath} (current {_currentFilePath}) – will show on activation");
                }
            });
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP publishDiagnostics handling failed", ex); }
    }

    private static string FileUriToPath(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath)) return uriOrPath;
        // Already a rooted Windows path (C:\ or C:/ or \\), just normalize – don't treat as URI
        if (Path.IsPathRooted(uriOrPath) && !uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Path.GetFullPath(uriOrPath); } catch { return uriOrPath; }
        }
        if (uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Use Uri to handle percent-encoding and drive letters correctly
                // file:///c%3A/... and file:///C:/... both work
                var u = new Uri(uriOrPath, UriKind.Absolute);
                if (u.IsFile)
                {
                    var local = u.LocalPath;
                    // LocalPath on Windows is already "C:\..." but for "file:///c%3A/..." it can be "/c:/..." – trim leading '/'
                    if (local.Length >= 3 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':')
                        local = local.Substring(1);
                    // If local somehow is still ":\Users" (missing drive), recover from AbsolutePath
                    if (local.StartsWith(":\\", StringComparison.Ordinal) || local.StartsWith(":/", StringComparison.Ordinal) || local.StartsWith(":", StringComparison.Ordinal))
                    {
                        var abs = Uri.UnescapeDataString(u.AbsolutePath);
                        // AbsolutePath is "/c:/Users/..." or "/C:/Users/..." – trim leading '/' and fix
                        abs = abs.TrimStart('/');
                        abs = abs.Replace('/', Path.DirectorySeparatorChar);
                        // abs now "c:/Users/..." or "C:/Users/..."
                        if (abs.Length >= 2 && abs[1] == ':')
                            local = abs;
                    }
                    local = local.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(local))
                        return Path.GetFullPath(local);
                    return local;
                }
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"FileUriToPath: failed to parse URI '{uriOrPath}': {ex.Message}");
            }
            // Fallback: manual extraction without using GetFullPath on URI
            try
            {
                var idx = uriOrPath.IndexOf("://", StringComparison.Ordinal);
                var part = idx >= 0 ? uriOrPath[(idx + 3)..] : uriOrPath;
                part = Uri.UnescapeDataString(part);
                part = part.TrimStart('/');
                part = part.Replace('/', Path.DirectorySeparatorChar);
                if (part.Length >= 2 && part[1] == ':')
                    return Path.GetFullPath(part);
                // If still not rooted, don't combine with current directory – return as-is
                return part;
            }
            catch { }
            return uriOrPath;
        }
        // Plain relative path – don't combine with Kodo source; return as-is
        return uriOrPath;
    }

    private List<InsightEngine.ErrorSpan> GetLspDiagnosticsForFile(string? filePath, string text)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new();
        var normPath = NormalizeFilePath(filePath);
        List<LspRawDiagnostic>? raw;
        lock (_lspDiagnosticsLock)
        {
            if (!_lspDiagnostics.TryGetValue(normPath, out raw) && !_lspDiagnostics.TryGetValue(filePath, out raw))
            {
                var uri = FilePathToUri(filePath);
                var normUri = NormalizeFilePath(uri);
                if (!_lspDiagnostics.TryGetValue(uri, out raw) && !_lspDiagnostics.TryGetValue(normUri, out raw))
                {
                    // Fallback: linear scan for same document (handles any remaining URI encoding differences)
                    foreach (var kv in _lspDiagnostics)
                    {
                        if (IsSameDocument(kv.Key, filePath))
                        {
                            raw = kv.Value;
                            break;
                        }
                    }
                    if (raw is null) return new();
                }
            }
            raw = new List<LspRawDiagnostic>(raw);
        }
        var spans = new List<InsightEngine.ErrorSpan>(raw.Count);
        foreach (var d in raw)
        {
            if (string.IsNullOrWhiteSpace(d.Message)) continue;
            var start = OffsetFromLspPosition(text, d.StartLine, d.StartChar);
            var end = OffsetFromLspPosition(text, d.EndLine, d.EndChar);
            if (start < 0 || start >= text.Length)
            {
                KodoDiagnostics.LogDebug($"LSP diag filtered: start out of range line={d.StartLine} char={d.StartChar} -> offset {start} len {text.Length}");
                continue;
            }
            var len = Math.Max(1, end - start);
            if (start + len > text.Length) len = Math.Max(1, text.Length - start);
            spans.Add(new InsightEngine.ErrorSpan(start, len, d.Message.Trim(), d.Severity, d.Code ?? "", d.Source ?? "lsp"));
        }
        if (spans.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans={spans.Count} first=[{spans[0].StartOffset}:{spans[0].Length}] {spans[0].Message}");
        else if (raw.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans=0 (all filtered)");
        return spans;
    }

    private static int OffsetFromLspPosition(string text, int line, int character)
    {
        if (line < 0) line = 0;
        if (character < 0) character = 0;
        var offset = 0;
        var currentLine = 0;
        while (currentLine < line && offset < text.Length)
        {
            var nl = text.IndexOf('\n', offset);
            if (nl < 0) return text.Length;
            offset = nl + 1;
            currentLine++;
        }
        var lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        var lineLen = lineEnd - offset;
        // LSP character is UTF-16 code units; for ASCII, same as offset. Clamp.
        var col = Math.Min(character, Math.Max(0, lineLen));
        return Math.Clamp(offset + col, 0, Math.Max(0, text.Length - 1));
    }

    private static (int line, int character) OffsetToLspPosition(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        var character = offset - lineStart;
        return (line, character);
    }

    // Phase 7 – Completion (generic)
    private async Task<IReadOnlyList<InsightSuggestion>> GetLspCompletionSuggestionsAsync(string? filePath, int offset, string text, string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<InsightSuggestion>();
        // Allow empty prefix for trigger characters like '.' – LSP will filter
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return Array.Empty<InsightSuggestion>();
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return Array.Empty<InsightSuggestion>();
        KodoDiagnostics.LogDebug($"LSP completion request file={filePath} offset={offset} prefix='{prefix}'");

        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(text, offset);
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["position"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = line, ["character"] = character },
            ["context"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["triggerKind"] = 1 }
        };

        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            result = await client.SendRequestAsync("textDocument/completion", @params, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Array.Empty<InsightSuggestion>(); }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP completion failed for {filePath}", ex); return Array.Empty<InsightSuggestion>(); }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            KodoDiagnostics.LogDebug($"LSP completion response empty for {filePath}");
            return Array.Empty<InsightSuggestion>();
        }
        var isIncomplete = result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("isIncomplete", out var inc) && inc.GetBoolean();
        var rawKind = result.Value.ValueKind;
        var isList = rawKind == JsonValueKind.Object && result.Value.TryGetProperty("items", out _);
        var isArray = rawKind == JsonValueKind.Array;
        KodoDiagnostics.LogDebug($"LSP completion response rawKind={rawKind} isList={isList} isArray={isArray} isIncomplete={isIncomplete} for {filePath} raw={result.Value.GetRawText().Substring(0, Math.Min(800, result.Value.GetRawText().Length))}");

        var items = new List<JsonElement>();
        if (result.Value.ValueKind == JsonValueKind.Array)
            items.AddRange(result.Value.EnumerateArray());
        else if (result.Value.ValueKind == JsonValueKind.Object)
        {
            if (result.Value.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                items.AddRange(itemsEl.EnumerateArray());
            else if (result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("label", out _))
                items.Add(result.Value);
        }

        if (items.Count == 0) return Array.Empty<InsightSuggestion>();

        var suggestions = new List<InsightSuggestion>(Math.Min(items.Count, 25));
        foreach (var item in items)
        {
            if (!item.TryGetProperty("label", out var labelEl)) continue;
            var label = labelEl.GetString();
            if (string.IsNullOrWhiteSpace(label)) continue;
            // Filter by prefix (LSP should already filter, but ensure)
            if (!label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !(item.TryGetProperty("insertText", out var it) && (it.GetString() ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                // Still include if prefix is substring? Keep strict prefix for now
                // Allow if label contains prefix case-insensitive anywhere? Skip to reduce noise
                if (!label.Contains(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            }

            string insertText = label;
            if (item.TryGetProperty("insertText", out var ins) && !string.IsNullOrWhiteSpace(ins.GetString()))
                insertText = ins.GetString()!;
            else if (item.TryGetProperty("textEdit", out var te) && te.ValueKind == JsonValueKind.Object && te.TryGetProperty("newText", out var nt))
                insertText = nt.GetString() ?? insertText;
            else if (item.TryGetProperty("textEdit", out var te2) && te2.ValueKind == JsonValueKind.Object && te2.TryGetProperty("insert", out var ins2) && ins2.TryGetProperty("newText", out var nt2))
                insertText = nt2.GetString() ?? insertText;

            var kindInt = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 0;
            var kind = kindInt switch
            {
                2 or 3 or 4 => InsightKind.Function, // Method, Function, Constructor
                5 or 10 => InsightKind.Property, // Field, Property
                7 or 8 or 22 or 13 or 25 => InsightKind.Type, // Class, Interface, Struct, Enum, TypeParameter
                9 => InsightKind.Namespace, // Module
                14 => InsightKind.Keyword,
                6 or 12 or 21 => InsightKind.Variable, // Variable, Value, Constant
                _ => InsightKind.Variable
            };

            // Avoid duplicates later via HashSet, but add now
            suggestions.Add(new InsightSuggestion(insertText, kind));
            if (suggestions.Count >= 25) break;
        }
        KodoDiagnostics.LogDebug($"LSP completion parsed items={items.Count} suggestions={suggestions.Count} for {filePath} samples={string.Join(", ", suggestions.Take(3).Select(s => s.Text))}");

        return suggestions;
    }

    // Phase 8 – Hover (generic) with coalescing and dedup
    private async Task<string?> GetLspHoverAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var hoverKey = $"{NormalizeFilePath(filePath)}:{offset}";
        // Deduplicate same hover within 500ms (mouse jitter)
        lock (_lspHoverCacheLock)
        {
            if (hoverKey == _lastHoverKey && (DateTime.UtcNow - _lastHoverTime).TotalMilliseconds < 500)
            {
                KodoDiagnostics.LogDebug($"LSP hover deduplicated same offset {hoverKey} within 500ms");
                return null;
            }
        }
        Task<string?>? coalesced;
        lock (_lspHoverCacheLock)
        {
            _lspPendingHovers.TryGetValue(hoverKey, out coalesced);
        }
        if (coalesced != null)
        {
            KodoDiagnostics.LogDebug($"LSP hover coalesced duplicate for {hoverKey}");
            return await coalesced.ConfigureAwait(false);
        }
        var hoverTask = GetLspHoverInnerAsync(filePath, offset, text, ct);
        lock (_lspHoverCacheLock)
        {
            _lspPendingHovers[hoverKey] = hoverTask;
            _lastHoverKey = hoverKey;
            _lastHoverTime = DateTime.UtcNow;
        }
        try { return await hoverTask.ConfigureAwait(false); }
        finally { lock (_lspHoverCacheLock) _lspPendingHovers.Remove(hoverKey); }
    }

    private async Task<string?> GetLspHoverInnerAsync(string? filePath, int offset, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return null;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return null;
        KodoDiagnostics.LogDebug($"LSP hover request file={filePath} offset={offset}");

        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(text, offset);
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["position"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = line, ["character"] = character }
        };
        KodoDiagnostics.LogDebug($"LSP hover request id=? file={filePath} uri={uri} offset={offset} -> line={line} char={character} textAtOffset='{text.Substring(Math.Max(0, offset-10), Math.Min(20, text.Length - Math.Max(0, offset-10))).Replace("\n","\\n").Replace("\r","\\r")}'");

        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            result = await client.SendRequestAsync("textDocument/hover", @params, cts.Token).ConfigureAwait(false);
            sw.Stop();
            KodoDiagnostics.LogDebug($"LSP hover response raw for {filePath} offset={offset} line={line} char={character} took={sw.ElapsedMilliseconds}ms raw={(result?.GetRawText()?.Substring(0, Math.Min(600, result?.GetRawText()?.Length ?? 0)) ?? "null")}");
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP hover failed for {filePath} offset={offset} line={line} char={character}", ex); return null; }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            KodoDiagnostics.LogDebug($"LSP hover response empty (null) for {filePath} offset={offset} line={line} char={character}");
            return null;
        }
        KodoDiagnostics.LogDebug($"LSP hover response received for {filePath} offset={offset} line={line} char={character} hasContents={result.Value.TryGetProperty("contents", out _)} rawLen={result.Value.GetRawText().Length}");
        var root = result.Value;
        if (!root.TryGetProperty("contents", out var contents))
        {
            KodoDiagnostics.LogDebug($"LSP hover no contents for {filePath}: {root.GetRawText().Substring(0, Math.Min(200, root.GetRawText().Length))}");
            return null;
        }

        // contents can be string, {language, value}, MarkupContent {kind, value}, or array
        if (contents.ValueKind == JsonValueKind.String) return contents.GetString();
        if (contents.ValueKind == JsonValueKind.Object)
        {
            if (contents.TryGetProperty("value", out var v)) return v.GetString();
            if (contents.TryGetProperty("contents", out var c2)) return c2.GetString();
        }
        if (contents.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var el in contents.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String) parts.Add(el.GetString() ?? "");
                else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v2)) parts.Add(v2.GetString() ?? "");
                else parts.Add(el.ToString());
            }
            return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        return contents.ToString();
    }

    // Phase 9b – Find References (generic, HasReferenceProvider)
    private async Task<bool> TryLspFindReferencesAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return false;
        KodoDiagnostics.LogDebug($"LSP references request file={filePath} offset={offset}");
        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(text, offset);
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["position"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = line, ["character"] = character },
            ["context"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["includeDeclaration"] = true }
        };
        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            result = await client.SendRequestAsync("textDocument/references", @params, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP references failed for {filePath}", ex); return false; }
        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind != JsonValueKind.Array) 
        {
            KodoDiagnostics.LogDebug($"LSP references response empty for {filePath}");
            return false;
        }
        var locations = result.Value.EnumerateArray().ToList();
        KodoDiagnostics.LogDebug($"LSP references response count={locations.Count} for {filePath}");
        if (locations.Count == 0) return false;
        // Use existing search UI to show references
        var word = GetLanguageWordAtOffset(text, offset);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FindText = word ?? "";
            // Populate search results with LSP locations
            OpenSearchPanel(IsFolderOpen ? SearchMode.ProjectSearch : SearchMode.FindInFile);
            // For now, just show the word in search – LSP locations could be shown as search results
            // TODO: Populate with actual LSP locations
        });
        return true;
    }

    private static string? GetLanguageWordAtOffset(string text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset < 0 || offset >= text.Length) return null;
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = offset;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return end > start ? text[start..end] : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Phase 9c – Document Formatting (generic, HasFormatter)
    private async Task<bool> TryLspFormatDocumentAsync(string? filePath, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return false;
        // Check if server supports formatting (via capabilities)
        if (client.ServerCapabilities is JsonElement caps && caps.TryGetProperty("documentFormattingProvider", out var fmt) && fmt.ValueKind == JsonValueKind.False)
            return false;
        KodoDiagnostics.LogDebug($"LSP formatting request file={filePath}");
        var uri = FilePathToUri(filePath);
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["options"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tabSize"] = 4, ["insertSpaces"] = true }
        };
        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            result = await client.SendRequestAsync("textDocument/formatting", @params, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP formatting failed for {filePath}", ex); return false; }
        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind != JsonValueKind.Array)
        {
            KodoDiagnostics.LogDebug($"LSP formatting response empty for {filePath}");
            return false;
        }
        var edits = result.Value.EnumerateArray().ToList();
        KodoDiagnostics.LogDebug($"LSP formatting response edits={edits.Count} for {filePath}");
        if (edits.Count == 0) return false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (EditorTextBox?.Document is null) return;
            // Apply edits in reverse order to preserve offsets
            var doc = EditorTextBox.Document;
            // Simple: if single edit covering whole document, replace all
            if (edits.Count == 1)
            {
                var edit = edits[0];
                if (edit.TryGetProperty("newText", out var nt))
                {
                    var newText = nt.GetString() ?? "";
                    var caret = EditorTextBox.TextArea.Caret.Offset;
                    doc.Text = newText;
                    EditorTextBox.TextArea.Caret.Offset = Math.Min(caret, doc.TextLength);
                    KodoDiagnostics.LogDebug($"LSP formatting applied for {filePath}");
                    return;
                }
            }
            // Fallback: apply each edit
            foreach (var edit in edits.OrderByDescending(e => e.TryGetProperty("range", out var r) && r.TryGetProperty("start", out var s) ? s.GetProperty("line").GetInt32() * 10000 + s.GetProperty("character").GetInt32() : 0))
            {
                if (!edit.TryGetProperty("newText", out var nt) || !edit.TryGetProperty("range", out var range)) continue;
                var start = range.TryGetProperty("start", out var s) ? s : default;
                var end = range.TryGetProperty("end", out var e) ? e : default;
                var sLine = start.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
                var sChar = start.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0;
                var eLine = end.TryGetProperty("line", out var el) ? el.GetInt32() : sLine;
                var eChar = end.TryGetProperty("character", out var ec) ? ec.GetInt32() : sChar;
                var sOff = OffsetFromLspPosition(text, sLine, sChar);
                var eOff = OffsetFromLspPosition(text, eLine, eChar);
                var len = Math.Max(0, eOff - sOff);
                doc.Replace(sOff, len, nt.GetString() ?? "");
            }
        });
        return true;
    }

    // Phase 9d – Code Actions (generic, HasCodeActionProvider)
    private async Task<bool> TryLspCodeActionsAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return false;
        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(text, offset);
        // For code actions, we need range and context
        var endLine = line;
        var endChar = character + 1;
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["range"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["start"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = line, ["character"] = character },
                ["end"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = endLine, ["character"] = endChar }
            },
            ["context"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["diagnostics"] = Array.Empty<object>()
            }
        };
        KodoDiagnostics.LogDebug($"LSP codeAction request file={filePath} offset={offset}");
        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            result = await client.SendRequestAsync("textDocument/codeAction", @params, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP codeAction failed for {filePath}", ex); return false; }
        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind != JsonValueKind.Array)
        {
            KodoDiagnostics.LogDebug($"LSP codeAction response empty for {filePath}");
            return false;
        }
        var actions = result.Value.EnumerateArray().ToList();
        KodoDiagnostics.LogDebug($"LSP codeAction response count={actions.Count} for {filePath}");
        if (actions.Count == 0) return false;
        // For now, apply first action if it has edit
        var first = actions[0];
        if (first.TryGetProperty("edit", out var edit) && edit.ValueKind == JsonValueKind.Object)
        {
            return await ApplyLspWorkspaceEditAsync(edit, filePath, text);
        }
        // Handle Command
        if (first.TryGetProperty("command", out var cmd))
        {
            KodoDiagnostics.LogDebug($"LSP codeAction is command: {cmd.GetRawText()}");
            return false; // Commands need workspace/executeCommand
        }
        return false;
    }

    private async Task<bool> ApplyLspWorkspaceEditAsync(JsonElement edit, string filePath, string text)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (EditorTextBox?.Document is null) return;
                var doc = EditorTextBox.Document;
                // edit can be {changes: {uri: [edits]}} or {documentChanges: [...]}
                if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in changes.EnumerateObject())
                    {
                        var uri = prop.Name;
                        if (!IsSameDocument(uri, filePath)) continue;
                        foreach (var e in prop.Value.EnumerateArray())
                        {
                            if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                            var s = range.GetProperty("start");
                            var ee = range.GetProperty("end");
                            var sOff = OffsetFromLspPosition(text, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                            var eOff = OffsetFromLspPosition(text, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                            doc.Replace(sOff, Math.Max(0, eOff - sOff), nt.GetString() ?? "");
                        }
                    }
                }
                else if (edit.TryGetProperty("documentChanges", out var docChanges) && docChanges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dc in docChanges.EnumerateArray())
                    {
                        if (!dc.TryGetProperty("edits", out var edits)) continue;
                        var uri = dc.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u) ? u.GetString() : filePath;
                        if (!IsSameDocument(uri, filePath)) continue;
                        foreach (var e in edits.EnumerateArray().OrderByDescending(e => e.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() * 10000))
                        {
                            if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                            var s = range.GetProperty("start");
                            var ee = range.GetProperty("end");
                            var sOff = OffsetFromLspPosition(doc.Text, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                            var eOff = OffsetFromLspPosition(doc.Text, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                            doc.Replace(sOff, Math.Max(0, eOff - sOff), nt.GetString() ?? "");
                        }
                    }
                }
            });
            return true;
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"ApplyLspWorkspaceEdit failed: {ex.Message}", ex); return false; }
    }

    // Phase 9 – Go to Definition (generic)
    private async Task<bool> TryLspGoToDefinitionAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, lspExt.Lsp);
        if (client is null || !client.IsInitialized) return false;

        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(text, offset);
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["position"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = line, ["character"] = character }
        };

        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            result = await client.SendRequestAsync("textDocument/definition", @params, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP definition failed for {filePath}", ex); return false; }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return false;

        var locations = new List<(string targetUri, int sLine, int sChar, int eLine, int eChar)>();
        void AddLoc(JsonElement loc)
        {
            if (!loc.TryGetProperty("uri", out var u)) 
            {
                // LocationLink has targetUri
                if (!loc.TryGetProperty("targetUri", out u)) return;
            }
            var targetUri = u.GetString() ?? "";
            var range = loc.TryGetProperty("range", out var r) ? r : (loc.TryGetProperty("targetRange", out var tr) ? tr : default);
            if (range.ValueKind != JsonValueKind.Object) return;
            var start = range.TryGetProperty("start", out var s) ? s : default;
            var end = range.TryGetProperty("end", out var e) ? e : default;
            var sl = start.TryGetProperty("line", out var slEl) ? slEl.GetInt32() : 0;
            var sc = start.TryGetProperty("character", out var scEl) ? scEl.GetInt32() : 0;
            var el = end.TryGetProperty("line", out var elEl) ? elEl.GetInt32() : sl;
            var ec = end.TryGetProperty("character", out var ecEl) ? ecEl.GetInt32() : sc;
            locations.Add((targetUri, sl, sc, el, ec));
        }

        if (result.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in result.Value.EnumerateArray()) AddLoc(el);
        }
        else if (result.Value.ValueKind == JsonValueKind.Object)
        {
            AddLoc(result.Value);
        }

        if (locations.Count == 0) return false;

        var first = locations[0];
        var targetPath = FileUriToPath(first.targetUri);
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            KodoDiagnostics.LogDebug($"LSP definition target not found: {first.targetUri}");
            return false;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Ensure file is loaded to get text for offset calc
            string targetText;
            try { targetText = await File.ReadAllTextAsync(targetPath).ConfigureAwait(false); } catch { targetText = ""; }
            var targetOffset = OffsetFromLspPosition(targetText, first.sLine, first.sChar);
            var existing = OpenTabs.FirstOrDefault(t => string.Equals(t.Path, targetPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                ActivateTab(existing);
                EditorTextBox.TextArea.Caret.Offset = Math.Clamp(targetOffset, 0, EditorTextBox.Document.TextLength);
                EditorTextBox.TextArea.Caret.BringCaretToView();
                EditorTextBox.Focus();
            }
            else
            {
                await OpenFileFromPathAsync(targetPath).ConfigureAwait(false);
                // Delay caret move until document loaded
                Dispatcher.UIThread.Post(() =>
                {
                    if (EditorTextBox?.Document is null) return;
                    EditorTextBox.TextArea.Caret.Offset = Math.Clamp(targetOffset, 0, EditorTextBox.Document.TextLength);
                    EditorTextBox.TextArea.Caret.BringCaretToView();
                    EditorTextBox.Focus();
                }, DispatcherPriority.Background);
            }
        });

        return true;
    }
}
