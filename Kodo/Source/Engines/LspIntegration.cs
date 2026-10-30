// Licensed under the GNU GPL-v3.0
#pragma warning disable CA1416
using Avalonia.Threading;
using Avalonia.Interactivity;
using Kodo.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using Kodo.Editor;

namespace Kodo;

public partial class MainWindow
{
    private readonly LspManager _lspManager = new();
    private readonly SemaphoreSlim _lspStartupGate = new(1, 1);
    private static StringComparer LspPathComparer => FileSystemPaths.Comparer;
    private readonly Dictionary<string, int> _lspDocumentVersions = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspOpenDocuments = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, LspClient> _lspDocumentClients = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspMissingNotified = new(FileSystemPaths.Comparer);
    private readonly DispatcherTimer _lspDidChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<string, string> _lspPendingTexts = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, List<PendingLspIncrementalChange>> _lspPendingChanges = new(FileSystemPaths.Comparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lspDocumentSyncGates = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, List<LspRawDiagnostic>> _lspDiagnostics = new(FileSystemPaths.Comparer);
    private readonly object _inactiveTabDiagnosticsLock = new();
    private readonly Dictionary<EditorTab, CancellationTokenSource> _inactiveTabDiagnosticScans = new();

    private CancellationTokenSource BeginInactiveTabDiagnosticScan(EditorTab tab)
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous = null;
        lock (_inactiveTabDiagnosticsLock)
        {
            _inactiveTabDiagnosticScans.TryGetValue(tab, out previous);
            _inactiveTabDiagnosticScans[tab] = cancellation;
        }
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        return cancellation;
    }

    private bool IsCurrentInactiveTabDiagnosticScan(EditorTab tab, CancellationTokenSource cancellation)
    {
        lock (_inactiveTabDiagnosticsLock)
            return _inactiveTabDiagnosticScans.TryGetValue(tab, out var current) &&
                ReferenceEquals(current, cancellation) && !cancellation.IsCancellationRequested;
    }

    private void FinishInactiveTabDiagnosticScan(EditorTab tab, CancellationTokenSource cancellation)
    {
        lock (_inactiveTabDiagnosticsLock)
        {
            if (_inactiveTabDiagnosticScans.TryGetValue(tab, out var current) && ReferenceEquals(current, cancellation))
                _inactiveTabDiagnosticScans.Remove(tab);
        }
        cancellation.Dispose();
    }

    private void CancelInactiveTabDiagnosticScan(EditorTab tab)
    {
        CancellationTokenSource? cancellation = null;
        lock (_inactiveTabDiagnosticsLock)
        {
            if (_inactiveTabDiagnosticScans.Remove(tab, out var existing))
                cancellation = existing;
        }
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void CancelInactiveTabDiagnosticScans()
    {
        CancellationTokenSource[] cancellations;
        lock (_inactiveTabDiagnosticsLock)
        {
            cancellations = _inactiveTabDiagnosticScans.Values.ToArray();
            _inactiveTabDiagnosticScans.Clear();
        }
        foreach (var cancellation in cancellations)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private readonly Dictionary<string, List<JsonElement>> _lspRawDiagnostics = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, int> _lspDiagnosticVersions = new(FileSystemPaths.Comparer);
    private readonly HashSet<LspClient> _lspSubscribedClients = new();
    private readonly object _lspDiagnosticsLock = new();
    private readonly HashSet<string> _lspDiagnosticRefreshPending = new(FileSystemPaths.Comparer);
    private readonly object _lspDiagnosticPullLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _lspDiagnosticPulls = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspPendingOpens = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, long> _lspFullSyncChangedAt = new(FileSystemPaths.Comparer);
    private const int LspFullSyncDelayerMilliseconds = 200;
    private const int LspMaxPendingIncrementalChanges = 128;
    private readonly object _lspOpenLock = new();
    private CancellationTokenSource? _lspHoverCts;
    private readonly object _lspHoverLock = new();
    private readonly Dictionary<string, Task<string?>> _lspPendingHovers = new();
    private readonly object _lspHoverCacheLock = new();

    private sealed record LspRawDiagnostic(int StartLine, int StartChar, int EndLine, int EndChar, string Message, string Severity, string Code, string Source);
    private sealed record PendingLspIncrementalChange(
        AvaloniaEdit.Document.ITextSource? Snapshot,
        int StartLine,
        int StartOffset,
        int StartUtf16Column,
        int EndLine,
        int EndOffset,
        int EndUtf16Column,
        string Text);
    private readonly record struct LspChangeLinePosition(int Line, int LineOffset, int Utf16Column);
    private AvaloniaEdit.Document.TextDocument? _lspTrackedDocument;

    private readonly LspFileResolver _lspFileResolver = new();

    private void InvalidateLspResolutionCaches() => _lspFileResolver.Invalidate();

    private bool HasRunningLspForFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (ResolveLspExtensionForFile(path)?.HasLsp != true) return false;
        var cfg = ResolveLspConfigurationForFile(path);
        if (cfg is null) return false;
        try
        {
            return _lspManager.TryGetClient(GetWorkspaceRootForFile(path), cfg) is { IsInitialized: true };
        }
        catch { return false; }
    }

    private void SetExtensionsStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess()) ExtensionsStatusText = text;
        else Dispatcher.UIThread.Post(() => ExtensionsStatusText = text);
    }

    private async void RecheckLanguageServersButton_OnClick(object? sender, RoutedEventArgs e) =>
        await RecheckLanguageServersAsync();

    private sealed record LanguageServerSweep(
        List<string> Ready,
        List<(LoadedExtension Ext, LspConfiguration Cfg, LspResolution Res)> Installable,
        Dictionary<string, List<string>> BlockedByRuntime,
        List<string> ManualOnly,
        List<string> Missing);

    private async Task RecheckLanguageServersAsync()
    {
        if (IsRecheckingLanguageServers) return;
        if (!_lspEnabled)
        {
            ExtensionsStatusText = "Language server features are turned off in Settings.";
            return;
        }

        IsRecheckingLanguageServers = true;
        LspRecheckStatusText = "Checking language servers...";

        try
        {
            _lspDismissedInstallPrompts.Clear();
            _lspMissingNotified.Clear();
            LspRuntimeDetector.InvalidateCache();
            InvalidateLspResolutionCaches();

            var extensionSnapshot = LoadedExtensions.ToList();
            var settingsSnapshot = BuildLspResolverSettings();

            LanguageServerSweep sweep;
            using (BeginAppLoading("Checking language servers"))
                sweep = await Task.Run(() => SweepLanguageServersAsync(extensionSnapshot, settingsSnapshot, status =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsRecheckingLanguageServers) LspRecheckStatusText = status;
                    })));

            if (sweep.Installable.Count > 0)
            {
                var offer = sweep.Installable
                    .Select(i => $"{i.Ext.Name} ({i.Cfg.DisplayName ?? i.Cfg.EffectiveProviderId})")
                    .ToList();
                var proceed = _lspAutoInstall || await ShowConfirmationDialogAsync(
                    "Install language servers",
                    BuildInstallOfferText(offer),
                    confirmLabel: "Install",
                    cancelLabel: "Not now");

                if (proceed)
                {
                    LspRecheckStatusText = $"Installing {sweep.Installable.Count} language server{(sweep.Installable.Count == 1 ? "" : "s")}...";
                    foreach (var (ext, _, res) in sweep.Installable)
                        await PromptAndInstallLspAsync(ext, res, autoInstall: true);

                    LspRuntimeDetector.InvalidateCache();
                    InvalidateLspResolutionCaches();
                    extensionSnapshot = LoadedExtensions.ToList();
                    settingsSnapshot = BuildLspResolverSettings();
                    using (BeginAppLoading("Checking language servers"))
                        sweep = await Task.Run(() => SweepLanguageServersAsync(extensionSnapshot, settingsSnapshot, status =>
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (IsRecheckingLanguageServers) LspRecheckStatusText = status;
                            })));
                }
            }

            SummariseLanguageServerCheck(sweep);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("MainWindow.RecheckLanguageServers", ex, operation: "Re-check language servers");
            LspRecheckStatusText = $"Re-check failed: {ex.Message}";
        }
        finally
        {
            IsRecheckingLanguageServers = false;
            SaveSettings(immediate: true);
        }
    }

    private async Task<LanguageServerSweep> SweepLanguageServersAsync(
        IReadOnlyList<LoadedExtension> extensions,
        AppSettings settings,
        Action<string>? reportProgress = null)
    {
        var ready = new List<string>();
        var installable = new List<(LoadedExtension Ext, LspConfiguration Cfg, LspResolution Res)>();
        var blockedByRuntime = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var manualOnly = new List<string>();
        var missing = new List<string>();
        using var sweepCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        foreach (var ext in extensions)
        {
            foreach (var cfg in ext.AllLspConfigurations.ToList())
            {
                if (sweepCts.IsCancellationRequested)
                {
                    missing.Add("The language-server check timed out before all providers were checked.");
                    return new LanguageServerSweep(ready, installable, blockedByRuntime, manualOnly, missing);
                }

                reportProgress?.Invoke($"Checking {ext.Name} ({cfg.DisplayName ?? cfg.EffectiveProviderId})...");
                LspResolution res;
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(sweepCts.Token);
                probeCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    LspProviderRegistry.RegisterConsumer(cfg.EffectiveProviderId, ext.Id);
                    res = await LspServerResolver.ResolveAsync(cfg, settings, ext.Id, probeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!sweepCts.IsCancellationRequested)
                {
                    missing.Add($"{ext.Name} ({cfg.DisplayName ?? cfg.EffectiveProviderId}): check timed out.");
                    continue;
                }
                catch (OperationCanceledException) when (sweepCts.IsCancellationRequested)
                {
                    missing.Add("The language-server check timed out before all providers were checked.");
                    return new LanguageServerSweep(ready, installable, blockedByRuntime, manualOnly, missing);
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"LSP re-check failed for {ext.Id} provider {cfg.EffectiveProviderId}", ex);
                    missing.Add($"{ext.Name} ({ex.Message})");
                    continue;
                }

                var status = res.ToDependencyStatus();
                ext.LspProviderStatuses[cfg.EffectiveProviderId] = (status, res.Error);
                ext.LspStatus = status;
                ext.LspStatusMessage = res.Error;
                LspProviderRegistry.SetStatus(cfg.EffectiveProviderId, status, res.Error,
                    res.Version, res.ExecutablePath, res.Source == LspServerSource.Managed);

                if (res.IsReady)
                {
                    ready.Add(ext.Name);
                    continue;
                }

                switch (res.Source)
                {
                    case LspServerSource.Installable when res.CanInstall && cfg.AllowAutoInstall:
                        installable.Add((ext, cfg, res));
                        break;
                    case LspServerSource.RuntimeMissing:
                        {
                            var runtime = cfg.Runtime ?? "required runtime";
                            if (!blockedByRuntime.TryGetValue(runtime, out var langs))
                                blockedByRuntime[runtime] = langs = new List<string>();
                            langs.Add(ext.Name);
                            break;
                        }
                    case LspServerSource.ManualRequired:
                        manualOnly.Add($"{ext.Name} ({cfg.DisplayName ?? cfg.EffectiveProviderId})");
                        break;
                    default:
                        missing.Add($"{ext.Name} ({cfg.DisplayName ?? cfg.EffectiveProviderId}): {res.Error ?? "no install source"}");
                        break;
                }
            }
        }

        return new LanguageServerSweep(ready, installable, blockedByRuntime, manualOnly, missing);
    }

    private static string BuildInstallOfferText(IEnumerable<string> names)
    {
        var list = names.ToList();
        var preview = string.Join("\n", list.Take(8).Select(n => $"\u2022 {n}"));
        var rest = list.Count > 8 ? $"\n\u2026and {list.Count - 8} more." : string.Empty;
        return list.Count == 1
            ? $"Kodo can install the language server for {preview}.{rest}"
            : $"Kodo can install {list.Count} language servers:\n{preview}{rest}";
    }

    private void SummariseLanguageServerCheck(LanguageServerSweep sweep)
    {
        var distinctReady = sweep.Ready
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(distinctReady.Count > 0
            ? $"Ready: {string.Join(", ", distinctReady)}."
            : "No language servers are ready yet.");

        if (sweep.BlockedByRuntime.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Missing runtime:");
            foreach (var entry in sweep.BlockedByRuntime.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"\u2022 {DescribeRuntimeRequirement(entry.Key)}");
                sb.AppendLine($"  Needed by: {string.Join(", ", entry.Value.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}");
            }
        }

        if (sweep.ManualOnly.Count > 0)
            AppendCheckDetail(sb, "Needs a manual install:", sweep.ManualOnly);

        if (sweep.Missing.Count > 0)
            AppendCheckDetail(sb, "No install source:", sweep.Missing);

        var summary = sb.ToString();
        LspRecheckStatusText = distinctReady.Count > 0
            ? $"{distinctReady.Count} language extension{(distinctReady.Count == 1 ? "" : "s")} ready."
            : "No language servers ready.";
        ExtensionsStatusText = LspRecheckStatusText;

        KodoDiagnostics.LogDebug($"Language server re-check: {summary.Replace('\n', ' ')}");
        if (distinctReady.Count > 0 && sweep.BlockedByRuntime.Count == 0 && sweep.ManualOnly.Count == 0 && sweep.Missing.Count == 0)
            return;
        _ = ShowWarningDialogAsync("Language servers", new InvalidOperationException(summary));
    }

    private static void AppendCheckDetail(StringBuilder sb, string heading, IEnumerable<string> items)
    {
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(heading);
        foreach (var item in items.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"\u2022 {item}");
    }

    private static string DescribeRuntimeRequirement(string runtime) => runtime switch
    {
        "node" => "Node.js 16 or newer - install from https://nodejs.org, then use this button again.",
        "npm" => "npm - install Node.js from https://nodejs.org, then use this button again.",
        "npx" => "npx - install Node.js from https://nodejs.org, then use this button again.",
        "python" or "python3" => "Python 3 - install from https://www.python.org/downloads/, then use this button again.",
        "java" => "A JDK (17 or newer) - install from https://adoptium.net, then use this button again.",
        "dotnet" => "The .NET SDK - install from https://dotnet.microsoft.com/download, then use this button again.",
        "pwsh" or "powershell" => "PowerShell 7 or newer - install from https://aka.ms/powershell, then use this button again.",
        _ => $"'{runtime}' - install it and make sure it is on your PATH, then use this button again."
    };

    private LoadedExtension? ResolveLspExtensionForFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || IsPlainTextFile(filePath) || HasNoFileExtension(filePath))
            return null;
        return _lspFileResolver.ResolveExtensionByFileExtension(Path.GetExtension(filePath), LoadedExtensions);
    }

    private LspConfiguration? ResolveLspConfigurationForFile(string? filePath) =>
        _lspFileResolver.ResolveConfigurationForFile(filePath, ResolveLspExtensionForFile);

    private string GetWorkspaceRootForFile(string? filePath) =>
        _lspFileResolver.GetWorkspaceRootForFile(
            filePath,
            _currentFolderPath,
            ResolveLspConfigurationForFile,
            ResolveLspExtensionForFile,
            IsPathInsideDirectory);

    private static string FilePathToUri(string filePath) => LspPath.FilePathToUri(filePath);

    private static string NormalizeFilePath(string pathOrUri) => LspPath.NormalizeFilePath(pathOrUri);

    private static bool IsSameDocument(string? a, string? b) => LspPath.IsSameDocument(a, b);

    private bool IsLspDocumentOpenOrOpening(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var normalizedPath = NormalizeFilePath(filePath);
        lock (_lspOpenLock)
            return _lspOpenDocuments.Contains(normalizedPath) || _lspPendingOpens.Contains(normalizedPath);
    }

    private bool IsLspDocumentOpen(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var normalizedPath = NormalizeFilePath(filePath);
        lock (_lspOpenLock) return _lspOpenDocuments.Contains(normalizedPath);
    }

    private async Task RetargetLspDocumentAsync(string oldPath, string newPath, EditorTab tab)
    {
        if (!IsLspDocumentOpenOrOpening(oldPath)) return;

        try
        {
            await LspNotifyDidCloseAsync(oldPath).ConfigureAwait(false);
            if (!LspEnabled) return;

            while (LspEnabled)
            {
                var targetPath = await Dispatcher.UIThread.InvokeAsync(() =>
                    OpenTabs.Contains(tab) ? tab.Path : null);
                if (string.IsNullOrWhiteSpace(targetPath)) return;

                var text = await GetEditorTabContentAsync(tab).ConfigureAwait(false);
                var pathStillCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
                    OpenTabs.Contains(tab) && FileSystemPaths.Equals(tab.Path, targetPath));
                if (!pathStillCurrent) continue;

                await LspNotifyDidOpenAsync(targetPath, text).ConfigureAwait(false);
                var reopenPath = await Dispatcher.UIThread.InvokeAsync(() =>
                    OpenTabs.Contains(tab) ? tab.Path : null);
                if (string.IsNullOrWhiteSpace(reopenPath))
                {
                    await LspNotifyDidCloseAsync(targetPath).ConfigureAwait(false);
                    return;
                }
                if (FileSystemPaths.Equals(reopenPath, targetPath)) return;

                await LspNotifyDidCloseAsync(targetPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP document retarget failed from '{oldPath}' to '{newPath}'", ex);
        }
    }

    private static string FixCorruptedPath(string p) => LspPath.FixCorruptedPath(p);

    private static string GetLanguageId(LspConfiguration lsp, string? filePath) => LspPath.GetLanguageId(lsp, filePath);

    private SemaphoreSlim GetLspDocumentSyncGate(string filePath) =>
        _lspDocumentSyncGates.GetOrAdd(NormalizeFilePath(filePath), static _ => new SemaphoreSlim(1, 1));

    private void InitLspDocumentSync()
    {
        _lspDidChangeTimer.Tick += LspDidChangeTimer_OnTick;
        PrimaryEditorTextBox.DocumentChanged += LspEditorDocument_OnChanged;
        SecondaryEditorTextBox.DocumentChanged += LspEditorDocument_OnChanged;
        _lspInlayHintRenderer.SetDocument(EditorTextBox.Document);
        TrackLspDocument(EditorTextBox.Document);
    }

    private void LspEditorDocument_OnChanged(object? sender, AvaloniaEdit.Document.DocumentChangedEventArgs e)
    {
        if (sender is not AvaloniaEdit.TextEditor editor || !ReferenceEquals(editor, EditorTextBox)) return;
        _lspInlayHintRenderer.SetDocument(e.NewDocument);
        TrackLspDocument(e.NewDocument);
    }

    private void TrackLspDocument(AvaloniaEdit.Document.TextDocument? document)
    {
        if (ReferenceEquals(_lspTrackedDocument, document)) return;
        if (_lspTrackedDocument is not null) _lspTrackedDocument.Changing -= LspDocument_OnChanging;
        _lspTrackedDocument = document;
        if (_lspTrackedDocument is not null) _lspTrackedDocument.Changing += LspDocument_OnChanging;
    }

    private void LspDocument_OnChanging(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e)
    {
        if (sender is not AvaloniaEdit.Document.TextDocument document || !LspEnabled || string.IsNullOrWhiteSpace(_currentFilePath)) return;
        var path = NormalizeFilePath(_currentFilePath);
        LspClient? client;
        var pendingFullSync = false;
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(path) || !_lspDocumentClients.TryGetValue(path, out client)) return;
            if (client.SupportsIncrementalSync() && _lspPendingTexts.ContainsKey(path))
            {
                _lspFullSyncChangedAt[path] = Environment.TickCount64;
                pendingFullSync = true;
            }
        }
        if (pendingFullSync)
        {
            if (Dispatcher.UIThread.CheckAccess()) ArmLspDidChangeTimer();
            else Dispatcher.UIThread.Post(ArmLspDidChangeTimer);
            return;
        }
        if (!client.SupportsIncrementalSync())
        {
            lock (_lspOpenLock)
            {
                if (_lspOpenDocuments.Contains(path) && _lspDocumentClients.TryGetValue(path, out var currentClient) && ReferenceEquals(client, currentClient))
                    _lspFullSyncChangedAt[path] = Environment.TickCount64;
            }
            return;
        }
        var start = GetLspChangeLinePosition(document, e.Offset);
        var end = GetLspChangeLinePosition(document, e.Offset + e.RemovalLength);
        var encoding = LspPath.NormalizePositionEncoding(client.PositionEncoding);
        var snapshot = encoding == "utf-16" ? null : document.CreateSnapshot();
        var change = new PendingLspIncrementalChange(snapshot, start.Line, start.LineOffset, start.Utf16Column,
            end.Line, end.LineOffset, end.Utf16Column, e.InsertedText.Text);
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(path) || !_lspDocumentClients.TryGetValue(path, out var currentClient) || !ReferenceEquals(client, currentClient)) return;
            if (!_lspPendingChanges.TryGetValue(path, out var changes))
                _lspPendingChanges[path] = changes = new List<PendingLspIncrementalChange>();
            changes.Add(change);
            if (changes.Count >= LspMaxPendingIncrementalChanges)
            {
                _lspPendingChanges.Remove(path);
                _lspPendingTexts[path] = string.Empty;
            }
            _lspFullSyncChangedAt[path] = Environment.TickCount64;
        }
        if (Dispatcher.UIThread.CheckAccess())
            ArmLspDidChangeTimer();
        else
            Dispatcher.UIThread.Post(ArmLspDidChangeTimer);
    }

    private static LspChangeLinePosition GetLspChangeLinePosition(AvaloniaEdit.Document.TextDocument document, int offset)
    {
        offset = Math.Clamp(offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        return new LspChangeLinePosition(line.LineNumber - 1, line.Offset,
            Math.Clamp(offset - line.Offset, 0, line.Length));
    }

    private static int GetPendingChangePosition(PendingLspIncrementalChange change, bool isStart, string? encoding)
    {
        var lineOffset = isStart ? change.StartOffset : change.EndOffset;
        var utf16Column = isStart ? change.StartUtf16Column : change.EndUtf16Column;
        if (LspPath.NormalizePositionEncoding(encoding) == "utf-16") return utf16Column;
        var linePrefix = change.Snapshot?.GetText(lineOffset, utf16Column) ?? string.Empty;
        return LspPath.PositionLength(linePrefix, encoding);
    }

    private async void LspDidChangeTimer_OnTick(object? sender, EventArgs e)
    {
        _lspDidChangeTimer.Stop();
        if (!LspEnabled)
        {
            lock (_lspOpenLock)
            {
                _lspPendingTexts.Clear();
                _lspPendingChanges.Clear();
            }
            return;
        }
        List<string> pending;
        lock (_lspOpenLock)
        {
            pending = _lspPendingTexts.Keys
                .Concat(_lspPendingChanges.Keys)
                .Distinct(FileSystemPaths.Comparer)
                .ToList();
        }
        foreach (var path in pending)
        {
            await LspSyncDocumentAsync(path, static () => string.Empty).ConfigureAwait(false);
        }
    }

    private void QueueLspDidChange(string filePath)
    {
        if (!LspEnabled) return;
        if (ResolveLspExtensionForFile(filePath) is null) return;
        var normalizedPath = NormalizeFilePath(filePath);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => QueueLspDidChange(filePath));
            return;
        }
        var isCurrentFile = FileSystemPaths.Equals(filePath, _currentFilePath);
        if (isCurrentFile && EditorTextBox?.Document is null) return;


        if (!isCurrentFile &&
            OpenTabs.FirstOrDefault(tab => !tab.IsUntitled && FileSystemPaths.Equals(tab.Path, filePath)) is null)
            return;
        var requiresFullSync = true;
        lock (_lspOpenLock)
        {
            if (_lspOpenDocuments.Contains(normalizedPath) &&
                _lspDocumentClients.TryGetValue(normalizedPath, out var client) &&
                client.SupportsIncrementalSync())
                requiresFullSync = false;
            if (!requiresFullSync) return;
            _lspPendingTexts[normalizedPath] = string.Empty;
            _lspFullSyncChangedAt[normalizedPath] = Environment.TickCount64;
        }
        ArmLspDidChangeTimer();
    }

    private void ArmLspDidChangeTimer()
    {
        var document = EditorTextBox?.Document;
        var debounceMilliseconds = document is { } doc && (doc.LineCount >= 50_000 || doc.TextLength >= 1_000_000)
            ? 750
            : document is { } mediumDocument && (mediumDocument.LineCount >= 5_000 || mediumDocument.TextLength >= 250_000)
                ? 300
                : 50;
        _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(debounceMilliseconds);
        _lspDidChangeTimer.Stop();
        _lspDidChangeTimer.Start();
    }

    private void RequeueFullLspSync(string filePath)
    {
        lock (_lspOpenLock)
        {
            if (_lspOpenDocuments.Contains(filePath))
            {
                _lspPendingChanges.Remove(filePath);
                _lspPendingTexts[filePath] = string.Empty;
            }
        }
        Dispatcher.UIThread.Post(ArmLspDidChangeTimer);
    }

    private AppSettings BuildLspResolverSettings()

    {
        return new AppSettings
        {
            LspEnabled = _lspEnabled,
            LspAutoInstall = _lspAutoInstall,
            LspPreferManaged = _lspPreferManaged,
            LspPreferSystem = _lspPreferSystem,
            LspInstallDir = _lspInstallDir,
            LspExecutableOverrides = new Dictionary<string, string>(LspExecutableOverrides, StringComparer.OrdinalIgnoreCase),
            LspDisabledLanguages = new Dictionary<string, bool>(_lspDisabledLanguages, StringComparer.OrdinalIgnoreCase),
            LspDismissedInstallPrompts = new HashSet<string>(_lspDismissedInstallPrompts, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task<bool> HandleLspNotReadyAsync(LoadedExtension lspExt, LspResolution resolution, string filePath)
    {
        var providerId = resolution.ResolvedConfiguration.EffectiveProviderId;
        LspProviderRegistry.SetStatus(providerId, resolution.ToDependencyStatus(), resolution.Error, resolution.Version, resolution.ExecutablePath, resolution.Source == LspServerSource.Managed);
        lspExt.LspStatus = resolution.ToDependencyStatus();
        lspExt.LspStatusMessage = resolution.Error;
        lspExt.LspProviderStatuses[providerId] = (resolution.ToDependencyStatus(), resolution.Error);

        if (resolution.Source == LspServerSource.Disabled)
        {
            KodoDiagnostics.LogDebug($"LSP disabled for {lspExt.Id}");
            return false;
        }
        if (resolution.Source == LspServerSource.RuntimeMissing)
        {
            var runtime = resolution.ResolvedConfiguration.Runtime ?? lspExt.Lsp?.Runtime ?? "required runtime";
            var msg = resolution.Error ?? $"Runtime '{runtime}' is required for {lspExt.Name}.";
            KodoDiagnostics.LogDebug($"LSP runtime missing for {lspExt.Id}: {msg}");
            if (!_lspMissingNotified.Add($"{lspExt.Id}:{providerId}:runtime"))
                return false;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = msg;
                await ShowWarningDialogAsync($"{lspExt.Name} – runtime required", new InvalidOperationException($"{msg}\n\nPlease install {runtime} and restart Kodo.\n\nHighlighting still works."));
            });
            return false;
        }
        if (resolution.Source == LspServerSource.Incompatible)
        {
            var msg = resolution.Error ?? $"Language server '{providerId}' version {resolution.Version ?? "unknown"} is incompatible.";
            KodoDiagnostics.LogDebug($"LSP incompatible for {lspExt.Id}: {msg}");
            if (resolution.CanInstall && resolution.ResolvedConfiguration.AllowAutoInstall)
            {
                if (_lspAutoInstall)
                {
                    var r = await PromptAndInstallLspAsync(lspExt, resolution, autoInstall: true).ConfigureAwait(false);
                    return r;
                }
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    ExtensionsStatusText = msg;
                    await PromptAndInstallLspAsync(lspExt, resolution, autoInstall: false).ConfigureAwait(false);
                });
                return false;
            }
            if (_lspMissingNotified.Add(lspExt.Id + ":" + providerId))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    ExtensionsStatusText = msg;
                    await ShowWarningDialogAsync($"{lspExt.Name} – incompatible version", new InvalidOperationException($"{msg}\n\nHighlighting remains available. Update the language server to a compatible version."));
                });
            }
            return false;
        }
        if (resolution.Source == LspServerSource.Installable)
        {
            if (!_lspAutoInstall && _lspDismissedInstallPrompts.Contains(lspExt.Id))
            {
                KodoDiagnostics.LogDebug($"LSP install prompt dismissed for {lspExt.Id}");
                return false;
            }
            if (_lspAutoInstall)
            {
                var autoResult = await PromptAndInstallLspAsync(lspExt, resolution, autoInstall: true).ConfigureAwait(false);
                return autoResult;
            }
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = $"{lspExt.Name} language support requires {resolution.ResolvedConfiguration.EffectiveProviderId}.";
                await PromptAndInstallLspAsync(lspExt, resolution, autoInstall: false).ConfigureAwait(false);
            });
            return false;
        }
        if (resolution.Source == LspServerSource.ManualRequired)
        {
            if (!_lspMissingNotified.Add(lspExt.Id)) return false;
            KodoDiagnostics.LogDebug($"LSP manual required for {lspExt.Id}");
            var missingConfig = resolution.ResolvedConfiguration;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = $"Language server '{missingConfig.Command}' not found for '{lspExt.Name}'. Manual install required.";
                await ShowWarningDialogAsync($"{lspExt.Name} – language server not found",
                    new FileNotFoundException($"{lspExt.Name} language support requires manual installation.\n\n'{missingConfig.Command}' was not found on PATH and cannot be auto-installed.\n\nPlease install {missingConfig.DisplayName ?? missingConfig.EffectiveProviderId} manually and ensure it is on PATH.\n\nHighlighting remains available."));
            });
            return false;
        }
        if (_lspMissingNotified.Add(lspExt.Id))
        {
            KodoDiagnostics.LogDebug($"LSP missing for {lspExt.Id}: {resolution.Error}");
            var missingConfig = resolution.ResolvedConfiguration;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = resolution.Error ?? $"Language server '{missingConfig.Command}' not found.";
                await ShowWarningDialogAsync($"{lspExt.Name} – language server not found",
                    new FileNotFoundException($"{resolution.Error}\n\nHighlighting remains available."));
            });
        }
        return false;
    }

    private async Task<bool> PromptAndInstallLspAsync(LoadedExtension lspExt, LspResolution resolution, bool autoInstall)
    {
        try
        {
            var targetCfg = resolution.ResolvedConfiguration ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
            if (targetCfg is null) return false;
            var providerName = targetCfg.DisplayName ?? targetCfg.EffectiveProviderId ?? lspExt.Name;
            var providerId = targetCfg.EffectiveProviderId;
            var title = $"{lspExt.Name} – language server required";

            var managedTarget = KodoDiagnostics.DisplayPath(
                Path.Combine(LspInstallationManager.GetManagedRoot(), targetCfg.EffectiveProviderId ?? providerName));

            var body = autoInstall
                ? $"Installing {providerName} for {lspExt.Name}..."
                : $"{lspExt.Name} language support requires {providerName}.\n\nStatus: {(resolution.Source == LspServerSource.Incompatible ? $"Incompatible ({resolution.Version ?? "unknown"})" : "Not installed")}\n\nInstall {providerName} now?\n\nKodo will download it to {managedTarget} and verify it before use. You can also use an existing system installation.";
            bool shouldInstall = autoInstall;
            if (!autoInstall)
            {
                shouldInstall = await ShowConfirmationDialogAsync(title, body, confirmLabel: $"Install {providerName}", isDestructive: false);
                if (!shouldInstall)
                {
                    _lspDismissedInstallPrompts.Add(lspExt.Id);
                    lspExt.LspStatus = LspDependencyStatus.Declined;
                    lspExt.LspStatusMessage = $"User declined installation of {providerName}";
                    if (!string.IsNullOrWhiteSpace(providerId))
                    {
                        lspExt.LspProviderStatuses[providerId] = (LspDependencyStatus.Declined, "User declined");
                        LspProviderRegistry.SetStatus(providerId, LspDependencyStatus.Declined, "User declined");
                    }
                    SaveSettings(immediate: true);
                    return false;
                }
            }
            if (shouldInstall)
            {
                using var loading = BeginAppLoading($"Installing {providerName}");
                SetExtensionsStatus($"Installing {providerName}...");
                lspExt.LspStatus = LspDependencyStatus.Installing;
                if (!string.IsNullOrWhiteSpace(providerId))
                {
                    lspExt.LspProviderStatuses[providerId] = (LspDependencyStatus.Installing, null);
                    LspProviderRegistry.SetStatus(providerId, LspDependencyStatus.Installing);
                }
                var progress = new Progress<string>(SetExtensionsStatus);
                var settings = BuildLspResolverSettings();
                var result = await LspInstallationManager.InstallAsync(targetCfg, settings, progress).ConfigureAwait(false);
                if (result.Kind == LspInstallationManager.InstallResultKind.Success || result.Kind == LspInstallationManager.InstallResultKind.AlreadyInstalled)
                {
                    SetExtensionsStatus($"{providerName} installed successfully.");
                    KodoDiagnostics.LogDebug($"LSP installed {providerName}: {result.InstalledPath}");
                    lspExt.LspStatus = LspDependencyStatus.Installed;
                    lspExt.LspStatusMessage = null;
                    lspExt.LspProviderStatuses[providerId!] = (LspDependencyStatus.Installed, null);
                    LspProviderRegistry.RegisterConsumer(providerId!, lspExt.Id);
                    LspProviderRegistry.SetStatus(providerId!, LspDependencyStatus.Installed, null, result.InstalledPath, result.InstalledPath, true);
                    _lspDismissedInstallPrompts.Remove(lspExt.Id);
                    _lspMissingNotified.Remove(lspExt.Id);
                    _lspMissingNotified.Remove(lspExt.Id + ":" + providerId);
                    SaveSettings(immediate: true);
                    if (!string.IsNullOrWhiteSpace(_currentFilePath))
                    {
                        lock (_lspOpenLock) _lspPendingOpens.Remove(NormalizeFilePath(_currentFilePath));
                        var currentPath = _currentFilePath;
                        Dispatcher.UIThread.Post(() =>
                        {
                            _ = LspNotifyActiveDocumentDidOpenAsync(currentPath);
                        });
                    }
                    return true;
                }
                else if (result.Kind == LspInstallationManager.InstallResultKind.Offline)
                {
                    lspExt.LspStatus = LspDependencyStatus.Failed;
                    lspExt.LspStatusMessage = result.Message;
                    lspExt.LspProviderStatuses[providerId!] = (LspDependencyStatus.Failed, result.Message);
                    LspProviderRegistry.SetStatus(providerId!, LspDependencyStatus.Failed, result.Message);
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        ExtensionsStatusText = $"{providerName} could not be downloaded because Kodo is offline.";
                        await ShowWarningDialogAsync($"{providerName} – offline", new IOException(result.Message ?? "Offline"));
                    });
                }
                else if (result.Kind == LspInstallationManager.InstallResultKind.RuntimeMissing)
                {
                    lspExt.LspStatus = LspDependencyStatus.RuntimeMissing;
                    lspExt.LspStatusMessage = result.Message;
                    lspExt.LspProviderStatuses[providerId!] = (LspDependencyStatus.RuntimeMissing, result.Message);
                    LspProviderRegistry.SetStatus(providerId!, LspDependencyStatus.RuntimeMissing, result.Message);
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowWarningDialogAsync($"{providerName} – runtime missing", new InvalidOperationException(result.Message ?? "Runtime missing"));
                    });
                }
                else
                {
                    lspExt.LspStatus = LspDependencyStatus.Failed;
                    lspExt.LspStatusMessage = result.Message;
                    lspExt.LspProviderStatuses[providerId!] = (LspDependencyStatus.Failed, result.Message);
                    LspProviderRegistry.SetStatus(providerId!, LspDependencyStatus.Failed, result.Message);
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        ExtensionsStatusText = $"Failed to install {providerName}: {result.Message}";
                        await ShowWarningDialogAsync($"{providerName} – installation failed", new InvalidOperationException(result.Message ?? "Installation failed"));
                    });
                }
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP install prompt failed for {lspExt.Id}", ex);
            await Dispatcher.UIThread.InvokeAsync(async () => await ShowWarningDialogAsync($"{lspExt.Name} – installation error", ex));
        }
        return false;
    }

    private async Task LspNotifyDidOpenAsync(string filePath, string content)
    {
        var gate = GetLspDocumentSyncGate(filePath);
        await gate.WaitAsync().ConfigureAwait(false);
        try { await LspNotifyDidOpenCoreAsync(filePath, content).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task LspNotifyActiveDocumentDidOpenAsync(string filePath)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is { } document
                ? EditorTextSnapshotStore.GetSnapshot(document)
                : null);
        if (snapshot is null) return;
        try
        {
            var text = await snapshot.GetTextAsync().ConfigureAwait(false);
            await LspNotifyDidOpenAsync(filePath, text).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP active document open failed for {filePath}", ex); }
    }

    private async Task<string?> CaptureActiveDocumentTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is { } document
                ? EditorTextSnapshotStore.GetSnapshot(document)
                : null);
        return snapshot is null ? null : await snapshot.GetTextAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> CaptureOpenDocumentTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var captured = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is { } activeDocument)
                return (Snapshot: (EditorTextSnapshot?)EditorTextSnapshotStore.GetSnapshot(activeDocument), Text: (string?)null);
            var tab = OpenTabs.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Path) && FileSystemPaths.Equals(candidate.Path, filePath));
            if (tab?.Document is { } document)
                return (Snapshot: (EditorTextSnapshot?)EditorTextSnapshotStore.GetSnapshot(document), Text: (string?)null);
            return (Snapshot: (EditorTextSnapshot?)null, Text: tab?.Content);
        });
        if (captured.Snapshot is not null)
            return await captured.Snapshot.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return captured.Text;
    }

    private async Task LspNotifyDidOpenCoreAsync(string filePath, string content)
    {
        if (!LspEnabled) return;
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return;
        var targetCfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (targetCfg is null) return;
        lock (_lspOpenLock)
        {
            if (_lspOpenDocuments.Contains(filePath) || _lspPendingOpens.Contains(filePath)) return;
            _lspPendingOpens.Add(filePath);
        }

        if (!LspEnabled)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            return;
        }

        await _lspStartupGate.WaitAsync().ConfigureAwait(false);
        LspResolution resolution;
        try
        {
            var settings = BuildLspResolverSettings();
            resolution = await LspServerResolver.ResolveAsync(targetCfg, settings, lspExt.Id).ConfigureAwait(false);
        }
        catch
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            _lspStartupGate.Release();
            throw;
        }
        if (!LspEnabled)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            _lspStartupGate.Release();
            return;
        }
        KodoDiagnostics.LogDebug($"LSP resolve {lspExt.Id} source={resolution.Source} exe={resolution.ExecutablePath} canInstall={resolution.CanInstall} err={resolution.Error}");
        if (!resolution.IsReady)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            _lspStartupGate.Release();
            await HandleLspNotReadyAsync(lspExt, resolution, filePath).ConfigureAwait(false);
            return;
        }
        var resolvedConfig = resolution.ResolvedConfiguration;
        string workspace;
        LspClient client;
        try
        {
            lspExt.LspStatus = LspDependencyStatus.Available;
            lspExt.LspStatusMessage = null;
            var providerId = resolvedConfig.EffectiveProviderId;
            lspExt.LspProviderStatuses[providerId] = (LspDependencyStatus.Available, null);
            LspProviderRegistry.RegisterConsumer(providerId, lspExt.Id);
            LspProviderRegistry.SetStatus(providerId, LspDependencyStatus.Available, null, resolution.Version, resolution.ExecutablePath, resolution.Source == LspServerSource.Managed);
            workspace = GetWorkspaceRootForFile(filePath);
            try
            {
                client = await _lspManager.GetOrStartAsync(workspace, resolvedConfig).ConfigureAwait(false);
                SetupLspClientHandlers(client);
                if (!LspEnabled)
                {
                    lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
                    await _lspManager.ShutdownAsync(workspace, resolvedConfig, "LSP disabled").ConfigureAwait(false);
                    return;
                }
            }
            catch (FileNotFoundException ex)
            {
                lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
                if (_lspMissingNotified.Add(lspExt.Id))
                {
                    KodoDiagnostics.LogDebug($"LSP start missing for '{lspExt.Id}'", ex);
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        ExtensionsStatusText = $"Language server '{resolvedConfig.Command}' failed to start for '{lspExt.Name}'. Highlighting still works.";
                        await ShowWarningDialogAsync($"{lspExt.Name} – language server failed to start",
                            new InvalidOperationException($"{lspExt.Name} language server could not be started.\n\n{ex.InnerException?.Message ?? ex.Message}\n\nHighlighting remains available.", ex));
                    });
                }
                return;
            }
            catch (Exception ex)
            {
                lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
                KodoDiagnostics.LogDebug($"LSP start failed for '{lspExt.Id}'", ex);
                await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = $"Language server '{resolvedConfig.Command}' failed to start: {ex.Message}");
                return;
            }
            try
            {
                await client.InitializeAsync().ConfigureAwait(false);
                if (!LspEnabled)
                {
                    lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
                    await _lspManager.ShutdownAsync(workspace, resolvedConfig, "LSP disabled").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
                KodoDiagnostics.LogDebug($"LSP initialize failed for '{lspExt.Id}'", ex);
                await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = $"Language server '{resolvedConfig.Command}' initialization failed: {ex.Message}");
                return;
            }
        }
        catch
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            throw;
        }
        finally { _lspStartupGate.Release(); }

        var activeText = await CaptureActiveDocumentTextAsync(filePath).ConfigureAwait(false);
        if (activeText is not null) content = activeText;
        var uri = FilePathToUri(filePath);
        int version;
        var initialText = content;
        lock (_lspOpenLock)
        {
            _lspPendingOpens.Remove(filePath);
            version = _lspDocumentVersions.TryGetValue(filePath, out var v) ? v + 1 : 1;
            _lspDocumentVersions[filePath] = version;
            _lspOpenDocuments.Add(filePath);
            _lspDocumentClients[filePath] = client;
        }
        lock (_lspDiagnosticsLock)
        {
            _lspDiagnosticVersions.Remove(filePath);
        }
        var languageId = GetLanguageId(resolvedConfig, filePath);

        var didOpenParams = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["uri"] = uri,
                ["languageId"] = languageId,
                ["version"] = version,
                ["text"] = initialText
            }
        };

        try
        {
            await client.SendNotificationAsync("textDocument/didOpen", didOpenParams).ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"LSP didOpen {uri} lang={languageId} ver={version} via {resolution.Source} {resolution.ExecutablePath}");
            if (LspDiagnosticsEnabled && client.Supports("textDocument/diagnostic"))
            {
                var pullVersion = version;
                var pullClient = client;
                ScheduleLspPullDiagnostics(filePath, pullClient, uri, pullVersion);
            }
        }
        catch (Exception ex)
        {
            lock (_lspOpenLock)
            {
                if (_lspDocumentClients.TryGetValue(filePath, out var documentClient) && ReferenceEquals(documentClient, client))
                {
                    _lspDocumentClients.Remove(filePath);
                    _lspOpenDocuments.Remove(filePath);
                    _lspDocumentVersions.Remove(filePath);
                    _lspPendingChanges.Remove(filePath);
                    _lspPendingTexts.Remove(filePath);
                }
            }
            KodoDiagnostics.LogDebug($"LSP didOpen failed for {uri}", ex);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (LspEnabled && FileSystemPaths.Equals(_currentFilePath, filePath))
                QueueLspPresentationRefresh();
        });
    }

    private async Task<(string uri, int version, LspClient client)?> PrepareLspChangeAsync(string filePath)
    {
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return null;
        var targetCfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (targetCfg is null) return null;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, targetCfg);
        if (client is null)
        {
            var resolveWatch = System.Diagnostics.Stopwatch.StartNew();
            var resolution = await LspServerResolver.ResolveAsync(targetCfg, BuildLspResolverSettings(), lspExt.Id).ConfigureAwait(false);
            resolveWatch.Stop();
            KodoDiagnostics.ReportSlowStage("LSP didChange resolve", resolveWatch.ElapsedMilliseconds, 2000);
            client = _lspManager.TryGetClient(workspace, resolution.IsReady ? resolution.ResolvedConfiguration : targetCfg);
        }
        if (client is null || !client.IsInitialized) return null;

        var uri = FilePathToUri(filePath);
        int version;
        lock (_lspOpenLock)
        {
            version = _lspDocumentVersions.TryGetValue(filePath, out var v) ? v + 1 : 1;
            _lspDocumentVersions[filePath] = version;
        }
        return (uri, version, client);
    }

    private async Task FlushLspDocumentAsync(string filePath, string text, bool forceIfNoPending = false)
    {
        if (!LspEnabled || string.IsNullOrWhiteSpace(filePath)) return;
        var normalized = NormalizeFilePath(filePath);
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(normalized)) return;
            if (!forceIfNoPending && !_lspPendingTexts.ContainsKey(normalized) && !_lspPendingChanges.ContainsKey(normalized))
                return;
        }
        if (Dispatcher.UIThread.CheckAccess()) _lspDidChangeTimer.Stop();
        await LspSyncDocumentAsync(normalized, () => text, force: true, forceSend: forceIfNoPending).ConfigureAwait(false);
    }

    private bool HasPendingLspDocumentSync(string filePath)
    {
        var normalized = NormalizeFilePath(filePath);
        lock (_lspOpenLock)
            return _lspOpenDocuments.Contains(normalized) &&
                (_lspPendingTexts.ContainsKey(normalized) || _lspPendingChanges.ContainsKey(normalized));
    }

    private async Task LspSyncDocumentAsync(string filePath, Func<string> textFactory, bool force = false, bool forceSend = false)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        bool isOpen;
        lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
        if (!isOpen)
        {
            var documentSource = await Dispatcher.UIThread.InvokeAsync(() =>
            {



                if (FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is { } activeDocument)
                {
                    var ownerTab = OpenTabs.FirstOrDefault(tab => FileSystemPaths.Equals(tab.Path, filePath));
                    if (ownerTab is null || ReferenceEquals(ownerTab.Document, activeDocument))
                        return (Snapshot: EditorTextSnapshotStore.GetSnapshot(activeDocument), Text: (string?)null);
                }
                var inactiveTab = OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, filePath));
                var inactiveSnapshot = inactiveTab?.Document is { } inactiveDocument
                    ? EditorTextSnapshotStore.GetSnapshot(inactiveDocument)
                    : null;
                return (Snapshot: inactiveSnapshot, Text: inactiveTab?.Content);
            });
            var text = documentSource.Snapshot is { } openSnapshot
                ? await openSnapshot.GetTextAsync().ConfigureAwait(false)
                : documentSource.Text;
            if (text is null) return;
            await LspNotifyDidOpenAsync(filePath, text).ConfigureAwait(false);
            return;
        }
        if (!force)
        {
            LspClient? peekClient = null;
            var lastChangedAt = 0L;
            lock (_lspOpenLock)
            {
                _lspDocumentClients.TryGetValue(filePath, out peekClient);
                _lspFullSyncChangedAt.TryGetValue(filePath, out lastChangedAt);
            }
            if (peekClient is not null && !peekClient.SupportsIncrementalSync() &&
                Environment.TickCount64 - lastChangedAt < LspFullSyncDelayerMilliseconds)
            {
                Dispatcher.UIThread.Post(ArmLspDidChangeTimer);
                return;
            }
        }
        var gate = GetLspDocumentSyncGate(filePath);
        await gate.WaitAsync().ConfigureAwait(false);
        List<PendingLspIncrementalChange> pendingChanges = new();
        var pendingSync = false;
        var syncCaptured = false;
        var syncSent = false;
        try
        {
            lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
            if (!isOpen) return;
            var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_lspOpenLock)
                {
                    pendingSync = _lspPendingTexts.ContainsKey(filePath) || _lspPendingChanges.ContainsKey(filePath);
                    if (!pendingSync && !forceSend) return (Text: (string?)null, Snapshot: (EditorTextSnapshot?)null, UsesIncrementalChanges: false, HasPending: false, Document: (AvaloniaEdit.Document.TextDocument?)null, DocumentVersion: (AvaloniaEdit.Document.ITextSourceVersion?)null, RevisionToken: CancellationToken.None);
                    var removedPendingText = _lspPendingTexts.Remove(filePath);
                    var hasPendingChanges = _lspPendingChanges.ContainsKey(filePath);
                    pendingSync = removedPendingText || hasPendingChanges;
                    if (_lspPendingChanges.Remove(filePath, out var changes)) pendingChanges = changes;
                    syncCaptured = true;
                }
                LspClient? syncClient;
                lock (_lspOpenLock) _lspDocumentClients.TryGetValue(filePath, out syncClient);
                var usesIncrementalChanges = pendingChanges.Count > 0 && syncClient is not null && syncClient.SupportsIncrementalSync();
                AvaloniaEdit.Document.TextDocument? activeDocument = null;
                EditorTab? inactiveTab = null;
                if (FileSystemPaths.Equals(_currentFilePath, filePath))
                {
                    var ownerTab = OpenTabs.FirstOrDefault(tab => FileSystemPaths.Equals(tab.Path, filePath));


                    if (ownerTab is null || ReferenceEquals(ownerTab.Document, EditorTextBox?.Document))
                        activeDocument = EditorTextBox?.Document;
                }
                inactiveTab ??= OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, filePath));
                var snapshotDocument = activeDocument ?? inactiveTab?.Document;
                var documentSnapshot = usesIncrementalChanges || snapshotDocument is null ? null : EditorTextSnapshotStore.GetSnapshot(snapshotDocument);
                var currentText = usesIncrementalChanges || documentSnapshot is not null ? null : inactiveTab?.Content;
                var revisionToken = activeDocument is not null && EditorTextBox is not null
                    ? EditorSession.RevisionCancellationToken
                    : CancellationToken.None;
                return (Text: currentText, Snapshot: documentSnapshot, UsesIncrementalChanges: usesIncrementalChanges, HasPending: true,
                    Document: snapshotDocument, DocumentVersion: snapshotDocument?.Version, RevisionToken: revisionToken);
            });
            if (!snapshot.HasPending) return;
            var text = snapshot.Snapshot is { } documentSnapshot
                ? await documentSnapshot.GetTextAsync(snapshot.RevisionToken).ConfigureAwait(false)
                : snapshot.Text ?? (Dispatcher.UIThread.CheckAccess() ? textFactory() : await Dispatcher.UIThread.InvokeAsync(textFactory));
            if (!pendingSync && pendingChanges.Count == 0 && !forceSend) return;
            if (snapshot.Document is { } capturedDocument && snapshot.DocumentVersion is { } capturedVersion &&
                !await Dispatcher.UIThread.InvokeAsync(() => ReferenceEquals(capturedDocument.Version, capturedVersion)))
            {
                RequeueFullLspSync(filePath);
                return;
            }
            var prepared = await PrepareLspChangeAsync(filePath).ConfigureAwait(false);
            if (prepared is null) throw new InvalidOperationException($"LSP client unavailable for document sync: {filePath}");
            if (snapshot.Document is { } documentToSync && snapshot.DocumentVersion is { } documentVersion &&
                !await Dispatcher.UIThread.InvokeAsync(() => ReferenceEquals(documentToSync.Version, documentVersion)))
            {
                RequeueFullLspSync(filePath);
                return;
            }
            var (uri, version, client) = prepared.Value;
            if (snapshot.UsesIncrementalChanges && !client.SupportsIncrementalSync())
                throw new InvalidOperationException($"LSP sync capability changed before sending changes: {filePath}");
            object[] contentChanges;
            if (snapshot.UsesIncrementalChanges && client.SupportsIncrementalSync())
            {
                object[] BuildIncrementalChanges()
                {
                    var incrementalChanges = new object[pendingChanges.Count];
                    for (var changeIndex = 0; changeIndex < pendingChanges.Count; changeIndex++)
                    {
                        var pendingChange = pendingChanges[changeIndex];
                        var startCharacter = GetPendingChangePosition(pendingChange, isStart: true, client.PositionEncoding);
                        var endCharacter = GetPendingChangePosition(pendingChange, isStart: false, client.PositionEncoding);
                        incrementalChanges[changeIndex] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["range"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["start"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = pendingChange.StartLine, ["character"] = startCharacter },
                                ["end"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = pendingChange.EndLine, ["character"] = endCharacter }
                            },
                            ["text"] = pendingChange.Text
                        };
                    }
                    return incrementalChanges;
                }
                if (Dispatcher.UIThread.CheckAccess() && LspPath.NormalizePositionEncoding(client.PositionEncoding) != "utf-16")
                    contentChanges = await Task.Run(BuildIncrementalChanges).ConfigureAwait(false);
                else
                    contentChanges = BuildIncrementalChanges();
            }
            else
            {
                contentChanges = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = text } };
            }
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri, ["version"] = version },
                ["contentChanges"] = contentChanges
            };
            var sendWatch = System.Diagnostics.Stopwatch.StartNew();
            await client.SendNotificationAsync("textDocument/didChange", parameters).ConfigureAwait(false);
            syncSent = true;
            sendWatch.Stop();
            KodoDiagnostics.ReportSlowStage("LSP didChange send", sendWatch.ElapsedMilliseconds, 2000, $"len={(snapshot.Text?.Length ?? -1)}");
            if (LspDiagnosticsEnabled && client.Supports("textDocument/diagnostic"))
            {
                var pullVersion = version;
                var pullClient = client;
                var pullPath = filePath;
                var pullUri = uri;
                ScheduleLspPullDiagnostics(pullPath, pullClient, pullUri, pullVersion);
            }
        }
        catch (Exception ex)
        {
            if (syncCaptured && !syncSent)
            {
                lock (_lspOpenLock)
                {
                    if (_lspOpenDocuments.Contains(filePath))
                    {
                        _lspPendingTexts[filePath] = string.Empty;
                        if (pendingChanges.Count > 0)
                        {
                            if (!_lspPendingChanges.TryGetValue(filePath, out var remaining)) _lspPendingChanges[filePath] = remaining = new List<PendingLspIncrementalChange>();
                            remaining.InsertRange(0, pendingChanges);
                        }
                    }
                }
            }
            if (ex is not OperationCanceledException)
                KodoDiagnostics.LogDebug($"LSP didChange failed for {filePath}", ex);
        }
        finally { gate.Release(); }
    }

    private async Task LspNotifyDidCloseAsync(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalized = NormalizeFilePath(filePath);
            await LspSyncDocumentAsync(normalized, static () => string.Empty, force: true).ConfigureAwait(false);
        }
        var gate = GetLspDocumentSyncGate(filePath);
        await gate.WaitAsync().ConfigureAwait(false);
        try { await LspNotifyDidCloseCoreAsync(filePath).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task LspNotifyDidCloseCoreAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        CancelLspPullDiagnostics(filePath);
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(filePath)) return;
            _lspOpenDocuments.Remove(filePath);
            _lspDocumentVersions.Remove(filePath);
            _lspPendingChanges.Remove(filePath);
            _lspDocumentClients.Remove(filePath);
            _lspPendingOpens.Remove(filePath);
            _lspPendingTexts.Remove(filePath);
            _lspFullSyncChangedAt.Remove(filePath);
        }
        lock (_lspDiagnosticsLock)
        {
            _lspDiagnostics.Remove(filePath);
            _lspRawDiagnostics.Remove(filePath);
            _lspDiagnosticVersions.Remove(filePath);
            _lspDiagnosticRefreshPending.Remove(filePath);
            var _uri = FilePathToUri(filePath);
            _lspDiagnostics.Remove(_uri);
            _lspRawDiagnostics.Remove(_uri);
            _lspDiagnostics.Remove(NormalizeFilePath(_uri));
            _lspRawDiagnostics.Remove(NormalizeFilePath(_uri));
            List<string>? _stale = null;
            foreach (var _k in _lspDiagnostics.Keys)
                if (IsSameDocument(_k, filePath)) (_stale ??= new List<string>()).Add(_k);
            if (_stale != null) foreach (var _k in _stale) { _lspDiagnostics.Remove(_k); _lspRawDiagnostics.Remove(_k); _lspDiagnosticVersions.Remove(_k); }
            BumpLspDiagnosticsRevision();
        }
        Dispatcher.UIThread.Post(() => UpdateInactiveTabDiagnosticsForFile(filePath));

        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return;
        var closeCfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (closeCfg is null) return;
        var workspace = GetWorkspaceRootForFile(filePath);
        LspClient? client = _lspManager.TryGetClient(workspace, closeCfg);
        try
        {
            if (client is null)
            {
                var cs = BuildLspResolverSettings();
                var res = await LspServerResolver.ResolveAsync(closeCfg, cs, lspExt.Id).ConfigureAwait(false);
                var effective = res.IsReady ? res.ResolvedConfiguration : closeCfg;
                client = _lspManager.TryGetClient(workspace, effective) ?? _lspManager.TryGetClient(workspace, closeCfg);
            }
        }
        catch { client = _lspManager.TryGetClient(workspace, closeCfg); }
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
        List<string> open;
        lock (_lspOpenLock) open = _lspOpenDocuments.ToList();
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
        client.OnServerRequest += HandleLspServerRequestAsync;
        client.OnExit += code =>
        {
            try { client.OnNotification -= HandleLspNotification; } catch { }
            try { client.OnServerRequest -= HandleLspServerRequestAsync; } catch { }
            lock (_lspDiagnosticsLock) _lspSubscribedClients.Remove(client);
            List<string> documents;
            lock (_lspOpenLock)
            {
                documents = _lspDocumentClients.Where(pair => ReferenceEquals(pair.Value, client)).Select(pair => pair.Key).ToList();
                foreach (var path in documents)
                {
                    CancelLspPullDiagnostics(path);
                    _lspDocumentClients.Remove(path);
                    _lspOpenDocuments.Remove(path);
                    _lspDocumentVersions.Remove(path);
                    _lspPendingChanges.Remove(path);
                    _lspFullSyncChangedAt.Remove(path);
                }
            }
            lock (_lspDiagnosticsLock)
            {
                foreach (var path in documents)
                {
                    _lspDiagnostics.Remove(path);
                    _lspRawDiagnostics.Remove(path);
                    _lspDiagnosticVersions.Remove(path);
                    _lspDiagnosticRefreshPending.Remove(path);
                }
                BumpLspDiagnosticsRevision();
            }
            Dispatcher.UIThread.Post(() =>
            {
                _ = UpdateErrorHighlightingSafelyAsync();
                _ = ReopenLspDocumentsAfterExitAsync(documents);
            });
        };
    }

    private async Task ReopenLspDocumentsAfterExitAsync(IReadOnlyList<string> documents)
    {
        foreach (var path in documents)
        {
            var text = await CaptureOpenDocumentTextAsync(path).ConfigureAwait(false);
            if (text is null) continue;
            try { await LspNotifyDidOpenAsync(path, text).ConfigureAwait(false); }
            catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP document reopen failed for {path}", ex); }
        }
    }

    private async Task<object?> HandleLspServerRequestAsync(LspClient client, string method, JsonElement? parameters)
    {
        if (method != "workspace/applyEdit") return null;
        try
        {
            if (parameters is not JsonElement request || request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("edit", out var edit) || edit.ValueKind != JsonValueKind.Object)
                return CreateWorkspaceApplyEditResult(false, "The language server sent an invalid workspace edit request");
        var captured = await Dispatcher.UIThread.InvokeAsync<(string? ActiveFilePath, string ContextPath, AvaloniaEdit.Document.TextDocument Document, AvaloniaEdit.Document.ITextSourceVersion DocumentVersion, EditorTextSnapshot Text, long Revision, CancellationToken RevisionToken, int Caret, int Length, EditorPaneState Pane, AvaloniaEdit.TextEditor Editor, EditorTab? Tab)?>(() =>
        {
            var editor = EditorTextBox;
            var pane = _activeEditorPane;
            var document = editor?.Document;
            if (document is null) return null;
            var activeFilePath = _currentFilePath;
            var contextPath = string.IsNullOrWhiteSpace(activeFilePath) ? client.ClientWorkspaceRoot : activeFilePath;
            return (ActiveFilePath: activeFilePath, ContextPath: contextPath, Document: document, DocumentVersion: document.Version, Text: EditorTextSnapshotStore.GetSnapshot(document),
                Revision: EditorSession.Revision, RevisionToken: EditorSession.RevisionCancellationToken,
                Caret: editor!.TextArea.Caret.Offset, Length: Math.Min(1, document.TextLength), Pane: pane, Editor: editor, Tab: pane.ActiveTab);
        });
        if (captured is null) return CreateWorkspaceApplyEditResult(false, "No active document");
        string text;
        try { text = await captured.Value.Text.GetTextAsync(captured.Value.RevisionToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CreateWorkspaceApplyEditResult(false, "The active document changed"); }
        var stillCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            ReferenceEquals(_activeEditorPane, captured.Value.Pane) && ReferenceEquals(EditorTextBox, captured.Value.Editor) &&
            ReferenceEquals(captured.Value.Pane.ActiveTab, captured.Value.Tab) && ReferenceEquals(captured.Value.Editor.Document, captured.Value.Document) &&
            ReferenceEquals(captured.Value.Document.Version, captured.Value.DocumentVersion) &&
            EditorSession.Revision == captured.Value.Revision &&
            FileSystemPaths.Equals(_currentFilePath, captured.Value.ActiveFilePath));
        if (!stillCurrent) return CreateWorkspaceApplyEditResult(false, "The active document changed");
        var snapshot = new CodeActionSnapshot(captured.Value.ContextPath, text, captured.Value.Document, captured.Value.DocumentVersion,
            captured.Value.Revision, captured.Value.Caret, 0, captured.Value.Length,
            captured.Value.Pane, captured.Value.Editor, captured.Value.Tab);
        var outcome = await ApplyWorkspaceEditAsync(edit, snapshot, EditScope.WorkspaceEdit,
            client.PositionEncoding, client.ClientWorkspaceRoot).ConfigureAwait(false);
        return CreateWorkspaceApplyEditResult(outcome == CodeActionOutcome.Applied,
            outcome == CodeActionOutcome.Applied ? null : "The workspace edit could not be applied");
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("LSP workspace/applyEdit request failed", ex);
            return CreateWorkspaceApplyEditResult(false, "Kodo could not safely apply the workspace edit");
        }
    }

    internal static Dictionary<string, object?> CreateWorkspaceApplyEditResult(bool applied, string? failureReason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["applied"] = applied };
        if (!applied && !string.IsNullOrWhiteSpace(failureReason))
            result["failureReason"] = failureReason;
        return result;
    }

    private void HandleLspNotification(string method, JsonElement? @params)
    {
        if (method == "textDocument/publishDiagnostics")
            HandlePublishDiagnostics(@params);
    }

    private bool IsLspPrimaryForFile(string? filePath)
    {
        if (!LspEnabled || !LspDiagnosticsEnabled || string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.HasLsp != true) return false;
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return false;
        return _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), cfg) is { IsInitialized: true };
    }

    private (List<LspRawDiagnostic> Diagnostics, List<JsonElement> RawDiagnostics) ConvertLspDiagnostics(JsonElement diags)
    {
        var diagnostics = new List<LspRawDiagnostic>();
        var rawDiagnostics = new List<JsonElement>();
        if (diags.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in diags.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                rawDiagnostics.Add(d.Clone());
                var range = d.TryGetProperty("range", out var r) ? r : default;
                var start = LspDiagnosticChild(range, "start");
                var end = LspDiagnosticChild(range, "end");
                var sLine = LspDiagnosticInt(LspDiagnosticChild(start, "line"), 0);
                var sChar = LspDiagnosticInt(LspDiagnosticChild(start, "character"), 0);
                var eLine = LspDiagnosticInt(LspDiagnosticChild(end, "line"), sLine);
                var eChar = LspDiagnosticInt(LspDiagnosticChild(end, "character"), sChar + 1);
                var msg = LspDiagnosticString(d.TryGetProperty("message", out var me) ? me : default, "");
                var sev = LspDiagnosticInt(d.TryGetProperty("severity", out var se) ? se : default, 1);
                var severity = sev switch { 1 => "error", 2 => "warning", 3 => "info", 4 => "hint", _ => "error" };
                var code = d.TryGetProperty("code", out var ce) ? ce.ToString() ?? "" : "";
                if (d.TryGetProperty("code", out var ce2) && ce2.ValueKind == JsonValueKind.Object && ce2.TryGetProperty("value", out var cv)) code = cv.ToString() ?? "";
                else if (ce2.ValueKind == JsonValueKind.Number && ce2.TryGetInt32(out var cvInt)) code = cvInt.ToString();
                var source = LspDiagnosticString(d.TryGetProperty("source", out var se2) ? se2 : default, "lsp");
                diagnostics.Add(new LspRawDiagnostic(sLine, sChar, eLine, eChar, msg, severity, code, source));
            }
        }
        return (diagnostics, rawDiagnostics);
    }

    private static JsonElement LspDiagnosticChild(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;

    private static int LspDiagnosticInt(JsonElement element, int fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : fallback;

    private static int? LspDiagnosticNullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

    private static string LspDiagnosticString(JsonElement element, string fallback) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : fallback;

    private void HandlePublishDiagnostics(JsonElement? @params)
    {
        if (!LspEnabled || !LspDiagnosticsEnabled) return;
        if (@params is null) return;
        try
        {
            var root = @params.Value;
            if (!root.TryGetProperty("uri", out var uriEl)) return;
            var uri = uriEl.GetString() ?? "";
            var filePath = FixCorruptedPath(FileUriToPath(uri));
            if (string.IsNullOrWhiteSpace(filePath)) filePath = FixCorruptedPath(uri);

            List<LspRawDiagnostic> diagnostics;
            List<JsonElement> rawDiagnostics;
            if (root.TryGetProperty("diagnostics", out var diags))
                (diagnostics, rawDiagnostics) = ConvertLspDiagnostics(diags);
            else
                (diagnostics, rawDiagnostics) = (new List<LspRawDiagnostic>(), new List<JsonElement>());

            string refreshKey;
            int? publishVersion = root.TryGetProperty("version", out var versionEl) ? LspDiagnosticNullableInt(versionEl) : null;
            var normPath = NormalizeFilePath(filePath);
            bool isOpen;
            int? sentVersion;
            lock (_lspOpenLock)
            {
                isOpen = _lspOpenDocuments.Contains(normPath) || _lspOpenDocuments.Contains(filePath) ||
                    _lspPendingOpens.Contains(normPath) || _lspPendingOpens.Contains(filePath);
                sentVersion = _lspDocumentVersions.TryGetValue(normPath, out var currentVersion) ||
                    _lspDocumentVersions.TryGetValue(filePath, out currentVersion) ? currentVersion : null;
            }
            if (!isOpen) return;
            publishVersion ??= sentVersion;
            if (publishVersion.HasValue && sentVersion.HasValue && publishVersion.Value < sentVersion.Value) return;
            lock (_lspDiagnosticsLock)
            {
                if (publishVersion.HasValue && _lspDiagnosticVersions.TryGetValue(normPath, out var knownVersion) && publishVersion.Value < knownVersion) return;
                var diagnosticsChanged = !_lspDiagnostics.TryGetValue(normPath, out var previousDiagnostics) || !LspDiagnosticsEqual(previousDiagnostics, diagnostics);
                _lspDiagnostics[normPath] = diagnostics;
                _lspRawDiagnostics[normPath] = rawDiagnostics;
                if (diagnosticsChanged) BumpLspDiagnosticsRevision();
                if (publishVersion.HasValue)
                    _lspDiagnosticVersions[normPath] = publishVersion.Value;
                else
                    _lspDiagnosticVersions.Remove(normPath);
                if (!diagnosticsChanged) return;
                refreshKey = normPath;
                if (!_lspDiagnosticRefreshPending.Add(refreshKey)) return;
            }
            if (KodoDiagnostics.VerboseLoggingEnabled)
            {
                KodoDiagnostics.LogDebug($"LSP publishDiagnostics {filePath} count={diagnostics.Count} uri={uri} version={(publishVersion?.ToString() ?? "<none>")}");
                for (var i = 0; i < Math.Min(diagnostics.Count, 3); i++)
                    KodoDiagnostics.LogDebug($"  LSP diag {i}: [{diagnostics[i].StartLine}:{diagnostics[i].StartChar}-{diagnostics[i].EndLine}:{diagnostics[i].EndChar}] {diagnostics[i].Severity} {diagnostics[i].Message}");
            }
            lock (_insightAnalysisCacheLock)
            {
                _cachedInsightAnalysisVersion = -1;
                _cachedInsightAnalysisText = null;
            }
            if (FileSystemPaths.Equals(filePath, _currentFilePath))
                _lspDiagnosticsSeenForFile = true;
            Dispatcher.UIThread.Post(() =>
            {
                lock (_lspDiagnosticsLock) _lspDiagnosticRefreshPending.Remove(refreshKey);
                int? appliedVersion;
                int? sentVersion;
                lock (_lspDiagnosticsLock) appliedVersion = _lspDiagnosticVersions.TryGetValue(refreshKey, out var av) ? av : null;
                lock (_lspOpenLock) sentVersion = _lspDocumentVersions.TryGetValue(refreshKey, out var sv) ? sv : null;
                if (appliedVersion.HasValue && sentVersion.HasValue && appliedVersion.Value < sentVersion.Value)
                {
                    KodoDiagnostics.LogDebug($"LSP stale publishDiagnostics dropped for {filePath} version={appliedVersion} sent={sentVersion}");
                    return;
                }
                var isActive = IsSameDocument(_currentFilePath, filePath) || IsSameDocument(_currentFilePath, uri);
                KodoDiagnostics.LogDebug($"LSP publishDiagnostics UI refresh isActive={isActive} current={_currentFilePath} diagFile={filePath} uri={uri} normCurrent={NormalizeFilePath(_currentFilePath ?? "")} normDiag={NormalizeFilePath(filePath)}");
                if (isActive)
                {
                    _ = UpdateErrorHighlightingSafelyAsync();
                    if (_lspDocumentRefreshPending || _lspDocumentRefreshFailures > 0)
                        QueueLspPresentationRefresh();
                }
                else
                {
                    KodoDiagnostics.LogDebug($"LSP diagnostics stored for inactive file {filePath} (current {_currentFilePath}) – will show on activation");
                    UpdateInactiveTabDiagnosticsForFile(filePath);
                    if (IsSplitScreenVisible &&
                        (FileSystemPaths.Equals(_editorPane.ActiveTab?.Path, filePath) ||
                         FileSystemPaths.Equals(_secondaryEditorPane.ActiveTab?.Path, filePath)))
                        QueueLspPresentationRefresh();
                }
            });
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP publishDiagnostics handling failed", ex); }
    }

    private void ScheduleLspPullDiagnostics(string filePath, LspClient client, string uri, int capturedVersion)
    {
        var normalizedPath = NormalizeFilePath(filePath);
        var cancellation = new CancellationTokenSource();
        lock (_lspDiagnosticPullLock)
        {
            if (_lspDiagnosticPulls.TryGetValue(normalizedPath, out var previous)) previous.Cancel();
            _lspDiagnosticPulls[normalizedPath] = cancellation;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cancellation.Token).ConfigureAwait(false);
                await LspPullDiagnosticsAsync(normalizedPath, client, uri, capturedVersion, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_lspDiagnosticPullLock)
                {
                    if (_lspDiagnosticPulls.TryGetValue(normalizedPath, out var current) && ReferenceEquals(current, cancellation))
                        _lspDiagnosticPulls.Remove(normalizedPath);
                }
                cancellation.Dispose();
            }
        });
    }

    private void CancelLspPullDiagnostics(string filePath)
    {
        var normalizedPath = NormalizeFilePath(filePath);
        lock (_lspDiagnosticPullLock)
        {
            if (_lspDiagnosticPulls.TryGetValue(normalizedPath, out var cancellation))
                cancellation.Cancel();
        }
    }

    private async Task LspPullDiagnosticsAsync(string filePath, LspClient client, string uri, int capturedVersion, CancellationToken cancellationToken)
    {
        if (!LspEnabled || !LspDiagnosticsEnabled) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri }
            };
            var result = await client.SendRequestAsync("textDocument/diagnostic", parameters, cts.Token).ConfigureAwait(false);
            if (result is not JsonElement root || root.ValueKind != JsonValueKind.Object) return;
            var kind = root.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString()
                : null;
            var diagnosticsUnchanged = string.Equals(kind, "unchanged", StringComparison.Ordinal);
            if (!diagnosticsUnchanged && (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)) return;
            var (diagnostics, rawDiagnostics) = diagnosticsUnchanged
                ? (new List<LspRawDiagnostic>(), new List<JsonElement>())
                : ConvertLspDiagnostics(root.GetProperty("items"));
            var normPath = NormalizeFilePath(filePath);
            bool diagnosticsChanged;
            lock (_lspOpenLock)
            {
                var isOpen = _lspOpenDocuments.Contains(normPath) || _lspOpenDocuments.Contains(filePath);
                int? latestVersion = _lspDocumentVersions.TryGetValue(normPath, out var version) ? version :
                    _lspDocumentVersions.TryGetValue(filePath, out var fallbackVersion) ? fallbackVersion : null;
                if (!isOpen) return;

                lock (_lspDiagnosticsLock)
                {
                    if (!TryRecordPulledDiagnosticsVersion(_lspDiagnosticVersions, normPath, capturedVersion, latestVersion)) return;

                    diagnosticsChanged = !diagnosticsUnchanged &&
                        (!_lspDiagnostics.TryGetValue(normPath, out var previousDiagnostics) || !LspDiagnosticsEqual(previousDiagnostics, diagnostics));
                    if (!diagnosticsUnchanged)
                    {
                        _lspDiagnostics[normPath] = diagnostics;
                        _lspRawDiagnostics[normPath] = rawDiagnostics;
                        if (diagnosticsChanged) BumpLspDiagnosticsRevision();
                    }
                }
            }
            if (diagnosticsUnchanged || !diagnosticsChanged) return;
            lock (_insightAnalysisCacheLock)
            {
                _cachedInsightAnalysisVersion = -1;
                _cachedInsightAnalysisText = null;
            }
            if (FileSystemPaths.Equals(filePath, _currentFilePath)) _lspDiagnosticsSeenForFile = true;
            Dispatcher.UIThread.Post(() =>
            {
                var isActive = IsSameDocument(_currentFilePath, filePath) || IsSameDocument(_currentFilePath, uri);
                if (isActive)
                    _ = UpdateErrorHighlightingSafelyAsync();
                else
                {
                    UpdateInactiveTabDiagnosticsForFile(filePath);
                    if (IsSplitScreenVisible &&
                        (FileSystemPaths.Equals(_editorPane.ActiveTab?.Path, filePath) ||
                         FileSystemPaths.Equals(_secondaryEditorPane.ActiveTab?.Path, filePath)))
                        QueueLspPresentationRefresh();
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP pull diagnostics failed for {filePath}", ex); }
    }

    internal static bool PulledDiagnosticsVersionIsCurrent(
        int resultVersion,
        int? currentDocumentVersion,
        int? currentDiagnosticVersion) =>
        (!currentDocumentVersion.HasValue || resultVersion >= currentDocumentVersion.Value) &&
        (!currentDiagnosticVersion.HasValue || resultVersion >= currentDiagnosticVersion.Value);

    internal static bool TryRecordPulledDiagnosticsVersion(
        IDictionary<string, int> diagnosticVersions,
        string path,
        int resultVersion,
        int? currentDocumentVersion)
    {
        int? currentDiagnosticVersion = diagnosticVersions.TryGetValue(path, out var knownVersion)
            ? knownVersion
            : null;
        if (!PulledDiagnosticsVersionIsCurrent(resultVersion, currentDocumentVersion, currentDiagnosticVersion))
            return false;
        diagnosticVersions[path] = resultVersion;
        return true;
    }

    private static string FileUriToPath(string uriOrPath) => LspPath.FileUriToPath(uriOrPath);

    private static bool LspDiagnosticsEqual(List<LspRawDiagnostic> previous, List<LspRawDiagnostic> next)
    {
        if (previous.Count != next.Count) return false;
        for (var i = 0; i < previous.Count; i++)
        {
            if (!previous[i].Equals(next[i])) return false;
        }
        return true;
    }

    private List<ErrorSpan> GetLspDiagnosticsForFile(string? filePath, AvaloniaEdit.Document.TextDocument document, string encoding)
    {
        if (!LspEnabled || !LspDiagnosticsEnabled) return new();
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
        var spans = new List<ErrorSpan>(raw.Count);
        foreach (var d in raw)
        {
            if (string.IsNullOrWhiteSpace(d.Message)) continue;
            if (d.StartLine < 0 || d.StartLine >= document.LineCount) continue;
            var start = LspPath.OffsetFromLspPosition(document, d.StartLine, d.StartChar, encoding);
            var end = LspPath.OffsetFromLspPosition(document, d.EndLine, d.EndChar, encoding);
            if (start < 0 || start >= document.TextLength)
            {
                KodoDiagnostics.LogDebug($"LSP diag filtered: start out of range line={d.StartLine} char={d.StartChar} -> offset {start} len {document.TextLength}");
                continue;
            }
            var len = Math.Max(1, end - start);
            if (start + len > document.TextLength) len = Math.Max(1, document.TextLength - start);
            spans.Add(new ErrorSpan(start, len, d.Message.Trim(), d.Severity, d.Code ?? "", d.Source ?? "lsp"));
        }
        if (KodoDiagnostics.VerboseLoggingEnabled && spans.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={document.TextLength} raw={raw.Count} spans={spans.Count} first=[{spans[0].StartOffset}:{spans[0].Length}] {spans[0].Message}");
        else if (KodoDiagnostics.VerboseLoggingEnabled && raw.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={document.TextLength} raw={raw.Count} spans=0 (all filtered)");
        return spans;
    }

    private bool HasStoredLspDiagnosticsForFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var normalizedPath = NormalizeFilePath(filePath);
        var uri = FilePathToUri(filePath);
        var normalizedUri = NormalizeFilePath(uri);
        lock (_lspDiagnosticsLock)
        {
            if (_lspDiagnostics.ContainsKey(normalizedPath) ||
                _lspDiagnostics.ContainsKey(filePath) ||
                _lspDiagnostics.ContainsKey(uri) ||
                _lspDiagnostics.ContainsKey(normalizedUri))
                return true;

            return _lspDiagnostics.Keys.Any(key => IsSameDocument(key, filePath));
        }
    }

    private LspClient? GetLspClientForFile(string filePath)
    {
        var extension = ResolveLspExtensionForFile(filePath);
        var configuration = ResolveLspConfigurationForFile(filePath) ?? extension?.Lsp ?? extension?.Lsps.FirstOrDefault();
        return configuration is null ? null : _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), configuration);
    }

    private async Task<string?> GetLspTargetTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var openText = await CaptureOpenDocumentTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (openText is not null) return openText;
        if (!File.Exists(filePath)) return null;
        try { return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException) KodoDiagnostics.LogDebug($"LSP target document read failed for {filePath}", ex);
            return null;
        }
    }

    private static int[] GetLspLineStarts(string text) => LspPath.GetLineStarts(text);

    private static int OffsetFromLspPosition(string text, int line, int character) =>
        LspPath.OffsetFromLspPosition(text, line, character);

    private static int OffsetFromLspPosition(int[] starts, string text, int line, int character) =>
        LspPath.OffsetFromLspPosition(starts, text, line, character);

    private static (int line, int character) OffsetToLspPosition(string text, int offset) =>
        LspPath.OffsetToLspPosition(text, offset);

    private static (int line, int character) OffsetToLspPosition(LspClient client, string text, int offset) =>
        LspPath.OffsetToLspPosition(text, offset, client.PositionEncoding);

    private async Task<IReadOnlyList<InsightSuggestion>> GetLspCompletionSuggestionsAsync(string? filePath, int offset, string prefix, AvaloniaEdit.Document.ITextSourceVersion sourceVersion, CancellationToken ct = default)
    {
        if (!LspEnabled || !LspCompletionEnabled)
            return Array.Empty<InsightSuggestion>();
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<InsightSuggestion>();
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return Array.Empty<InsightSuggestion>();
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return Array.Empty<InsightSuggestion>();
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized || !client.Supports("textDocument/completion")) return Array.Empty<InsightSuggestion>();
        var request = await Dispatcher.UIThread.InvokeAsync<(
            AvaloniaEdit.Document.TextDocument Document,
            EditorTextSnapshot Snapshot,
            AvaloniaEdit.Document.ITextSourceVersion Version,
            int Length,
            long Revision,
            EditorPaneState Pane,
            AvaloniaEdit.TextEditor Editor,
            EditorTab? Tab,
            (int line, int character) Position)?>(() =>
        {
            var editor = EditorTextBox;
            var document = editor.Document;
            if (!ReferenceEquals(document.Version, sourceVersion)) return null;
            var pane = _activeEditorPane;
            return (Document: document,
                Snapshot: EditorTextSnapshotStore.GetSnapshot(document),
                Version: document.Version,
                Length: document.TextLength,
                Revision: EditorSession.Revision,
                Pane: pane,
                Editor: editor,
                Tab: pane.ActiveTab,
                Position: LspPath.OffsetToLspPosition(document, offset, client.PositionEncoding));
        });
        if (request is null) return Array.Empty<InsightSuggestion>();
        var requestState = request.Value;
        string documentText;
        try
        {
            documentText = await requestState.Snapshot.GetTextAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<InsightSuggestion>();
        }

        if (offset < 0 || offset > documentText.Length || documentText.Length != requestState.Length)
            return Array.Empty<InsightSuggestion>();
        var (line, character) = requestState.Position;
        await FlushLspDocumentAsync(filePath, string.Empty).ConfigureAwait(false);
        KodoDiagnostics.LogDebug($"LSP completion request file={filePath} offset={offset} prefix='{prefix}'");

        var uri = FilePathToUri(filePath);
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

        var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) && ReferenceEquals(_activeEditorPane, requestState.Pane) &&
            ReferenceEquals(EditorTextBox, requestState.Editor) && ReferenceEquals(requestState.Pane.ActiveTab, requestState.Tab) &&
            ReferenceEquals(requestState.Editor.Document, requestState.Document) && ReferenceEquals(requestState.Document.Version, requestState.Version) &&
            EditorSession.Revision == requestState.Revision && requestState.Editor.TextArea.Caret.Offset == offset);
        if (!requestIsCurrent) return Array.Empty<InsightSuggestion>();

        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            KodoDiagnostics.LogDebug($"LSP completion response empty for {filePath}");
            return Array.Empty<InsightSuggestion>();
        }
        var rawKind = result.Value.ValueKind;
        var isIncomplete = result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("isIncomplete", out var inc) && inc.ValueKind == JsonValueKind.True;
        if (KodoDiagnostics.VerboseLoggingEnabled) KodoDiagnostics.LogDebug($"LSP completion response rawKind={rawKind} isIncomplete={isIncomplete} for {filePath} raw={result.Value.GetRawText().Substring(0, Math.Min(800, result.Value.GetRawText().Length))}");

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
        var seenSuggestionTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.String) continue;
            var label = labelEl.GetString();
            if (string.IsNullOrWhiteSpace(label)) continue;
            string insertText = label;
            int? replaceStartOffset = null;
            int? replaceEndOffset = null;
            var additionalEdits = new List<(int Start, int End, string NewText)>();
            if (item.TryGetProperty("textEdit", out var te))
            {
                if (!TryReadLspCompletionTextEdit(documentText, te, client.PositionEncoding,
                        out replaceStartOffset, out replaceEndOffset, out insertText))
                    continue;
            }
            else if (item.TryGetProperty("insertText", out var ins) && ins.ValueKind == JsonValueKind.String)
                insertText = ins.GetString() ?? insertText;

            if (item.TryGetProperty("additionalTextEdits", out var additional))
            {
                if (additional.ValueKind != JsonValueKind.Array) continue;
                var validAdditionalEdits = true;
                foreach (var edit in additional.EnumerateArray())
                {
                    if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("newText", out var editText) || editText.ValueKind != JsonValueKind.String ||
                        !edit.TryGetProperty("range", out var range) ||
                        !TryReadLspRangeOffsets(documentText, range, client.PositionEncoding, out var startOffset, out var endOffset))
                    {
                        validAdditionalEdits = false;
                        break;
                    }
                    additionalEdits.Add((startOffset, endOffset, editText.GetString() ?? string.Empty));
                }
                if (!validAdditionalEdits) continue;
            }

            var primaryStart = replaceStartOffset ?? Math.Clamp(offset - prefix.Length, 0, documentText.Length);
            var primaryEnd = replaceEndOffset ?? offset;
            if (additionalEdits.Any(edit => LspTextRangesOverlap(edit.Start, edit.End, primaryStart, primaryEnd)))
                continue;
            var orderedAdditionalEdits = additionalEdits.OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
            if (orderedAdditionalEdits.Zip(orderedAdditionalEdits.Skip(1), (left, right) => LspTextRangesOverlap(left.Start, left.End, right.Start, right.End)).Any(overlap => overlap))
                continue;

            var kindInt = item.TryGetProperty("kind", out var k) ? LspDiagnosticInt(k, 0) : 0;
            var kind = kindInt switch
            {
                2 or 3 or 4 => InsightKind.Function,
                5 or 10 => InsightKind.Property,
                7 or 8 or 22 or 13 or 25 => InsightKind.Type,
                9 => InsightKind.Namespace,
                14 => InsightKind.Keyword,
                6 or 12 or 21 => InsightKind.Variable,
                _ => InsightKind.Variable
            };

            var identity = string.Join("\u001f", new[]
            {
                insertText,
                replaceStartOffset?.ToString() ?? string.Empty,
                replaceEndOffset?.ToString() ?? string.Empty,
                string.Join("\u001e", additionalEdits.Select(edit => $"{edit.Start}:{edit.End}:{edit.NewText}"))
            });
            if (!seenSuggestionTexts.Add(identity)) continue;
            suggestions.Add(new InsightSuggestion(insertText, kind, replaceStartOffset, replaceEndOffset,
                sourceText: null, additionalEdits: additionalEdits, priority: 100 - suggestions.Count, sourceVersion: sourceVersion));
            if (suggestions.Count >= 25) break;
        }
        KodoDiagnostics.LogDebug($"LSP completion parsed items={items.Count} suggestions={suggestions.Count} for {filePath} samples={string.Join(", ", suggestions.Take(3).Select(s => s.Text))}");

        return suggestions;
    }

    internal static bool TryReadLspCompletionTextEdit(
        string documentText,
        JsonElement edit,
        string encoding,
        out int? startOffset,
        out int? endOffset,
        out string newText)
    {
        startOffset = null;
        endOffset = null;
        newText = string.Empty;
        if (edit.ValueKind != JsonValueKind.Object ||
            !edit.TryGetProperty("newText", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            return false;

        JsonElement range;
        if (edit.TryGetProperty("range", out range))
        {
            if (edit.TryGetProperty("insert", out _) || edit.TryGetProperty("replace", out _)) return false;
        }
        else
        {
            if (!edit.TryGetProperty("insert", out var insertRange) ||
                !edit.TryGetProperty("replace", out range) ||
                !TryReadLspRangeOffsets(documentText, insertRange, encoding, out var insertStart, out var insertEnd) ||
                !TryReadLspRangeOffsets(documentText, range, encoding, out var replaceStart, out var replaceEnd) ||
                insertStart != replaceStart || insertEnd > replaceEnd)
                return false;
        }

        if (!TryReadLspRangeOffsets(documentText, range, encoding, out var start, out var end))
            return false;
        startOffset = start;
        endOffset = end;
        newText = textElement.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadLspRangeOffsets(
        string documentText,
        JsonElement range,
        string encoding,
        out int startOffset,
        out int endOffset)
    {
        startOffset = 0;
        endOffset = 0;
        var lineStarts = GetLspLineStarts(documentText);
        if (range.ValueKind != JsonValueKind.Object ||
            !range.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object ||
            !range.TryGetProperty("end", out var end) || end.ValueKind != JsonValueKind.Object ||
            !start.TryGetProperty("line", out var startLine) || startLine.ValueKind != JsonValueKind.Number || !startLine.TryGetInt32(out var startLineNumber) ||
            !start.TryGetProperty("character", out var startCharacter) || startCharacter.ValueKind != JsonValueKind.Number || !startCharacter.TryGetInt32(out var startCharacterNumber) ||
            !end.TryGetProperty("line", out var endLine) || endLine.ValueKind != JsonValueKind.Number || !endLine.TryGetInt32(out var endLineNumber) ||
            !end.TryGetProperty("character", out var endCharacter) || endCharacter.ValueKind != JsonValueKind.Number || !endCharacter.TryGetInt32(out var endCharacterNumber) ||
            !TryOffsetFromLspPosition(lineStarts, documentText, startLineNumber, startCharacterNumber, encoding, out startOffset) ||
            !TryOffsetFromLspPosition(lineStarts, documentText, endLineNumber, endCharacterNumber, encoding, out endOffset) ||
            endOffset < startOffset)
            return false;
        return true;
    }

    private async Task<string?> GetLspHoverAsync(string? filePath, int offset, int line, int character, int documentVersionId, CancellationToken ct = default)
    {
        if (!LspEnabled || !LspHoverEnabled) return null;
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var hoverKey = $"{NormalizeFilePath(filePath)}:{offset}:{documentVersionId}";
        Task<string?> hoverTask;
        var coalesced = false;
        lock (_lspHoverCacheLock)
        {
            if (_lspPendingHovers.TryGetValue(hoverKey, out var pending))
            {
                hoverTask = pending;
                coalesced = true;
            }
            else
            {
                hoverTask = GetLspHoverInnerAsync(filePath, offset, line, character, ct);
                _lspPendingHovers[hoverKey] = hoverTask;
            }
        }
        if (coalesced)
            KodoDiagnostics.LogDebug($"LSP hover coalesced duplicate for {hoverKey}");
        try { return await hoverTask.ConfigureAwait(false); }
        finally
        {
            if (!coalesced)
            {
                lock (_lspHoverCacheLock)
                {
                    if (_lspPendingHovers.TryGetValue(hoverKey, out var pending) && ReferenceEquals(pending, hoverTask))
                        _lspPendingHovers.Remove(hoverKey);
                }
            }
        }
    }

    private async Task<string?> GetLspHoverInnerAsync(string? filePath, int offset, int line, int character, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return null;
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return null;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized || !client.Supports("textDocument/hover")) return null;
        var hoverText = await CaptureOpenDocumentTextAsync(filePath, ct).ConfigureAwait(false);
        if (hoverText is null) return null;
        await FlushLspDocumentAsync(filePath, hoverText).ConfigureAwait(false);
        (line, character) = OffsetToLspPosition(client, hoverText, offset);
        KodoDiagnostics.LogDebug($"LSP hover request file={filePath} offset={offset}");

        var uri = FilePathToUri(filePath);
        var @params = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri },
            ["position"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = line, ["character"] = character }
        };
        KodoDiagnostics.LogDebug($"LSP hover request id=? file={filePath} uri={uri} offset={offset} -> line={line} char={character}");

        JsonElement? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            result = await client.SendRequestAsync("textDocument/hover", @params, cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (KodoDiagnostics.VerboseLoggingEnabled) KodoDiagnostics.LogDebug($"LSP hover response raw for {filePath} offset={offset} line={line} char={character} took={sw.ElapsedMilliseconds}ms raw={(result?.GetRawText()?.Substring(0, Math.Min(600, result?.GetRawText()?.Length ?? 0)) ?? "null")}");
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP hover failed for {filePath} offset={offset} line={line} char={character}", ex); return null; }

        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            KodoDiagnostics.LogDebug($"LSP hover response empty (null) for {filePath} offset={offset} line={line} char={character}");
            return null;
        }
        if (KodoDiagnostics.VerboseLoggingEnabled) KodoDiagnostics.LogDebug($"LSP hover response received for {filePath} offset={offset} line={line} char={character} hasContents={result.Value.TryGetProperty("contents", out _)} rawLen={result.Value.GetRawText().Length}");
        var root = result.Value;
        if (!root.TryGetProperty("contents", out var contents))
        {
            if (KodoDiagnostics.VerboseLoggingEnabled) KodoDiagnostics.LogDebug($"LSP hover no contents for {filePath}: {root.GetRawText().Substring(0, Math.Min(200, root.GetRawText().Length))}");
            return null;
        }

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

    private async Task<bool> TryLspFindReferencesAsync(string? filePath, int offset, string? word, CancellationToken ct = default)
    {
        if (!LspEnabled) return false;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return false;
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized || !client.Supports("textDocument/references")) return false;
        var requestPane = _activeEditorPane;
        var requestTab = requestPane.ActiveTab;
        var requestEditor = GetEditorForPane(requestPane);
        var activeDocument = requestEditor?.Document;
        if (activeDocument is null) return false;
        var requestVersion = activeDocument.Version;
        var (line, character) = LspPath.OffsetToLspPosition(activeDocument, offset, client.PositionEncoding);
        await FlushLspDocumentAsync(filePath, string.Empty).ConfigureAwait(false);
        KodoDiagnostics.LogDebug($"LSP references request file={filePath} offset={offset}");
        var uri = FilePathToUri(filePath);
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
        var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            requestEditor is not null && ReferenceEquals(requestPane.ActiveTab, requestTab) &&
            ReferenceEquals(requestEditor.Document, activeDocument) && ReferenceEquals(activeDocument.Version, requestVersion) &&
            FileSystemPaths.Equals(requestTab is { IsUntitled: false } ? requestTab.Path : null, filePath) &&
            requestEditor.TextArea.Caret.Offset == offset);
        if (!requestIsCurrent) return false;
        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind != JsonValueKind.Array)
        {
            KodoDiagnostics.LogDebug($"LSP references response empty for {filePath}");
            return false;
        }
        var locations = result.Value.EnumerateArray().ToList();
        KodoDiagnostics.LogDebug($"LSP references response count={locations.Count} for {filePath}");
        if (locations.Count == 0) return false;
        var firstLocation = locations[0];
        var firstUri = firstLocation.TryGetProperty("uri", out var firstU) ? firstU.GetString() : null;
        var firstRange = firstLocation.TryGetProperty("range", out var firstR) ? firstR : default;
        if (string.IsNullOrWhiteSpace(firstUri) || firstRange.ValueKind != JsonValueKind.Object) return false;
        var firstStart = firstRange.TryGetProperty("start", out var firstS) ? firstS : default;
        var firstPath = FileUriToPath(firstUri);
        if (string.IsNullOrWhiteSpace(firstPath)) return false;
        var firstClient = GetLspClientForFile(firstPath);
        var firstLine = LspDiagnosticInt(LspDiagnosticChild(firstStart, "line"), 0);
        var firstCharacter = LspDiagnosticInt(LspDiagnosticChild(firstStart, "character"), 0);
        var firstEncoding = firstClient?.PositionEncoding ?? "utf-16";
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(firstPath, requestPane);
            var targetEditor = GetEditorForPane(ResolveEditorPaneForOpen(requestPane));
            if (targetEditor.Document is null) return;
            var firstOffset = LspPath.OffsetFromLspPosition(targetEditor.Document, firstLine, firstCharacter, firstEncoding);
            targetEditor.TextArea.Caret.Offset = Math.Clamp(firstOffset, 0, targetEditor.Document.TextLength);
            targetEditor.TextArea.Caret.BringCaretToView();
            targetEditor.Focus();
        });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ExtensionsStatusText = locations.Count == 1
                ? $"1 reference{(string.IsNullOrWhiteSpace(word) ? "" : $" to '{word}'")}."
                : $"{locations.Count} references{(string.IsNullOrWhiteSpace(word) ? "" : $" to '{word}'")} (jumped to first).";
        });
        return true;
    }

    private static string? GetLanguageWordAtOffset(AvaloniaEdit.Document.TextDocument document, int offset)
    {
        if (document.TextLength == 0 || offset < 0 || offset >= document.TextLength) return null;
        var line = document.GetLineByOffset(offset);
        var lineStart = line.Offset;
        var lineEnd = line.Offset + line.Length;
        var start = offset;
        while (start > lineStart && IsWordChar(document.GetCharAt(start - 1))) start--;
        var end = offset;
        while (end < lineEnd && IsWordChar(document.GetCharAt(end))) end++;
        return end > start ? document.GetText(start, end - start) : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private async Task<bool> TryLspFormatDocumentAsync(string? filePath, string text, CancellationToken ct = default)
    {
        if (!LspEnabled) return false;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return false;
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized || !client.Supports("textDocument/formatting")) return false;
        var activeDocument = EditorTextBox?.Document;
        if (activeDocument is null) return false;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox!;
        var requestTab = requestPane.ActiveTab;
        var documentVersion = activeDocument.Version;
        var requestRevision = EditorSession.Revision;
        await FlushLspDocumentAsync(filePath, text).ConfigureAwait(false);
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
        var parsed = await Task.Run(() =>
        {
            return TryReadLspTextEdits(text, result.Value, client.PositionEncoding, out var parsedEdits, out var formatted)
                ? (Edits: parsedEdits, Formatted: formatted)
                : ((List<(int Start, int End, string NewText)> Edits, string Formatted)?)null;
        }, ct).ConfigureAwait(false);
        if (parsed is null || string.Equals(text, parsed.Value.Formatted, StringComparison.Ordinal))
        {
            if (parsed is null)
                KodoDiagnostics.LogDebug($"LSP formatting rejected invalid, overlapping, or out-of-range edits for {filePath}");
            return false;
        }
        var applied = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_activeEditorPane, requestPane) || !ReferenceEquals(EditorTextBox, requestEditor) ||
                !ReferenceEquals(requestPane.ActiveTab, requestTab) || !ReferenceEquals(requestEditor.Document, activeDocument) ||
                !ReferenceEquals(activeDocument.Version, documentVersion) || EditorSession.Revision != requestRevision ||
                !FileSystemPaths.Equals(_currentFilePath, filePath))
            {
                KodoDiagnostics.LogDebug($"LSP formatting rejected as stale for {filePath}");
                return;
            }
            ApplyLspEditsToDocument(activeDocument, parsed.Value.Edits, [PrimaryEditorTextBox, SecondaryEditorTextBox]);
            if (requestTab is not null)
            {
                requestTab.IsDirty = true;
                requestTab.Content = string.Empty;
            }
            applied = true;
            KodoDiagnostics.LogDebug($"LSP formatting applied for {filePath}");
        });
        return applied;
    }

    private void SetLspFeatureStatus(string message) =>
        Dispatcher.UIThread.Post(() => { ExtensionsStatusText = message; });

    private static string TruncateStatusText(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength] + "…";

    private async Task<bool> TryLspRenameAsync(string? filePath, int offset, string text, string newName, CancellationToken ct = default)
    {
        if (!LspEnabled) return false;
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(newName)) return false;
        var ext = ResolveLspExtensionForFile(filePath);
        var cfg = ResolveLspConfigurationForFile(filePath) ?? ext?.Lsp ?? ext?.Lsps.FirstOrDefault();
        if (ext is null || cfg is null) return false;
        var client = _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), cfg);
        if (client is null || !client.IsInitialized || !client.Supports("textDocument/rename")) return false;
        var activeDocument = EditorTextBox?.Document;
        if (activeDocument is null) return false;
        var requestPane = _activeEditorPane;
        var requestEditor = EditorTextBox!;
        var requestTab = requestPane.ActiveTab;
        var documentVersion = activeDocument.Version;
        var requestRevision = EditorSession.Revision;
        var (line, character) = LspPath.OffsetToLspPosition(activeDocument, offset, client.PositionEncoding);
        await FlushLspDocumentAsync(filePath, text).ConfigureAwait(false);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath) },
            ["position"] = new Dictionary<string, object?> { ["line"] = line, ["character"] = character },
            ["newName"] = newName
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await client.SendRequestAsync("textDocument/rename", parameters, timeout.Token).ConfigureAwait(false);
            if (result is null || result.Value.ValueKind != JsonValueKind.Object) return false;
            var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
                FileSystemPaths.Equals(_currentFilePath, filePath) && ReferenceEquals(_activeEditorPane, requestPane) &&
                ReferenceEquals(EditorTextBox, requestEditor) && ReferenceEquals(requestPane.ActiveTab, requestTab) &&
                ReferenceEquals(requestEditor.Document, activeDocument) && ReferenceEquals(activeDocument.Version, documentVersion) &&
                EditorSession.Revision == requestRevision && requestEditor.TextArea.Caret.Offset == offset);
            if (!requestIsCurrent) return false;
            return await ApplyExternalWorkspaceEditAsync(
                result.Value,
                new CodeActionSnapshot(filePath, text, activeDocument, documentVersion, requestRevision, offset, 0, 1,
                    requestPane, requestEditor, requestTab),
                client.PositionEncoding).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP rename failed for {filePath}", ex); return false; }
    }

    private async Task<JsonElement?> RequestLspFeatureAsync(string? filePath, string method, object parameters, CancellationToken ct)
    {
        if (!LspEnabled) return null;
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var ext = ResolveLspExtensionForFile(filePath);
        var cfg = ResolveLspConfigurationForFile(filePath) ?? ext?.Lsp ?? ext?.Lsps.FirstOrDefault();
        if (ext is null || cfg is null) return null;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is null || !client.IsInitialized) return null;
        if (!client.Supports(method)) return null;
        try
        {
            var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is { } activeDocument)
                    return (Snapshot: (EditorTextSnapshot?)EditorTextSnapshotStore.GetSnapshot(activeDocument), Document: (AvaloniaEdit.Document.TextDocument?)activeDocument, Version: _insightDocVersion, DocumentVersion: activeDocument.Version, IsActive: true);


                var otherPane = ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _editorPane : _secondaryEditorPane;
                if (IsSplitScreenVisible &&
                    otherPane.ActiveTab is { IsUntitled: false } otherTab &&
                    FileSystemPaths.Equals(otherTab.Path, filePath) &&
                    GetEditorForPane(otherPane).Document is { } otherDocument)
                    return (Snapshot: (EditorTextSnapshot?)EditorTextSnapshotStore.GetSnapshot(otherDocument), Document: (AvaloniaEdit.Document.TextDocument?)otherDocument, Version: _insightDocVersion, DocumentVersion: otherDocument.Version, IsActive: false);
                return (Snapshot: (EditorTextSnapshot?)null, Document: (AvaloniaEdit.Document.TextDocument?)null, Version: _insightDocVersion, DocumentVersion: (AvaloniaEdit.Document.ITextSourceVersion?)null, IsActive: true);
            });
            if (snapshot.Snapshot is not null && HasPendingLspDocumentSync(filePath))
                await FlushLspDocumentAsync(filePath, string.Empty).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var requestWatch = System.Diagnostics.Stopwatch.StartNew();
            JsonElement? result;
            try
            {
                result = await client.SendRequestAsync(method, parameters, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                KodoDiagnostics.ReportSlowStage("LSP request", requestWatch.ElapsedMilliseconds, 250, $"method={method}");
            }
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (snapshot.IsActive)
                    return FileSystemPaths.Equals(_currentFilePath, filePath) && ReferenceEquals(EditorTextBox?.Document, snapshot.Document) && _insightDocVersion == snapshot.Version
                        ? result
                        : null;
                var otherPane = ReferenceEquals(_activeEditorPane, _secondaryEditorPane) ? _editorPane : _secondaryEditorPane;
                return snapshot.Document is not null &&
                       otherPane.ActiveTab is { IsUntitled: false } otherTab &&
                       FileSystemPaths.Equals(otherTab.Path, filePath) &&
                       ReferenceEquals(GetEditorForPane(otherPane).Document, snapshot.Document) &&
                       ReferenceEquals(snapshot.Document.Version, snapshot.DocumentVersion)
                    ? result
                    : null;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (method is "textDocument/signatureHelp" or "textDocument/documentHighlight")
                Interlocked.Increment(ref _lspCaretFeatureRequestFailures);
            KodoDiagnostics.LogDebug($"LSP {method} timed out for {filePath}");
            return null;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            if (method is "textDocument/signatureHelp" or "textDocument/documentHighlight")
                Interlocked.Increment(ref _lspCaretFeatureRequestFailures);
            KodoDiagnostics.LogDebug($"LSP {method} failed for {filePath}", ex);
            return null;
        }
    }

    private IReadOnlyList<string> GetLspSemanticTokenTypes(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<string>();
        var ext = ResolveLspExtensionForFile(filePath);
        var cfg = ResolveLspConfigurationForFile(filePath) ?? ext?.Lsp ?? ext?.Lsps.FirstOrDefault();
        if (ext is null || cfg is null) return Array.Empty<string>();
        var client = _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), cfg);
        return client is { IsInitialized: true } ? client.SemanticTokenTypes : Array.Empty<string>();
    }

    private string? GetLspSemanticTokenTypeName(string? filePath, int tokenType)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var ext = ResolveLspExtensionForFile(filePath);
        var cfg = ResolveLspConfigurationForFile(filePath) ?? ext?.Lsp ?? ext?.Lsps.FirstOrDefault();
        if (ext is null || cfg is null) return null;
        var client = _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), cfg);
        return client is { IsInitialized: true } && tokenType >= 0 && tokenType < client.SemanticTokenTypes.Count
            ? client.SemanticTokenTypes[tokenType]
            : null;
    }

    private object CreateLspPositionParams(string filePath, int line, int character) =>
        new Dictionary<string, object?>
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath) },
            ["position"] = new Dictionary<string, object?> { ["line"] = line, ["character"] = character }
        };

    private object CreateLspPositionParams(string filePath, string text, int offset)
    {
        var ext = ResolveLspExtensionForFile(filePath);
        var cfg = ResolveLspConfigurationForFile(filePath) ?? ext?.Lsp ?? ext?.Lsps.FirstOrDefault();
        var client = cfg is null ? null : _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), cfg);
        var encoding = client?.PositionEncoding ?? "utf-16";
        var (line, character) = LspPath.OffsetToLspPosition(text, offset, encoding);
        return CreateLspPositionParams(filePath, line, character);
    }

    private Task<JsonElement?> GetLspSignatureHelpAsync(string? filePath, int line, int character, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/signatureHelp", CreateLspPositionParams(filePath!, line, character), ct);

    private Task<JsonElement?> GetLspDocumentSymbolsAsync(string? filePath, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/documentSymbol", new Dictionary<string, object?> { ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) } }, ct);

    private Task<JsonElement?> GetLspFoldingRangesAsync(string? filePath, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/foldingRange", new Dictionary<string, object?> { ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) } }, ct);

    private Task<JsonElement?> GetLspSemanticTokensAsync(string? filePath, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/semanticTokens/full", new Dictionary<string, object?> { ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) } }, ct);

    private Task<JsonElement?> GetLspSemanticTokensRangeAsync(string? filePath, int startLine, int endLine, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/semanticTokens/range", new Dictionary<string, object?>
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) },
            ["range"] = new Dictionary<string, object?>
            {
                ["start"] = new Dictionary<string, object?> { ["line"] = Math.Max(0, startLine), ["character"] = 0 },
                ["end"] = new Dictionary<string, object?> { ["line"] = Math.Max(Math.Max(0, startLine) + 1, endLine), ["character"] = 0 }
            }
        }, ct);

    private Task<JsonElement?> GetLspInlayHintsAsync(string? filePath, int startLine, int endLine, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/inlayHint", new Dictionary<string, object?>
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) },
            ["range"] = new Dictionary<string, object?>
            {
                ["start"] = new Dictionary<string, object?> { ["line"] = Math.Max(0, startLine), ["character"] = 0 },
                ["end"] = new Dictionary<string, object?> { ["line"] = Math.Max(Math.Max(0, startLine) + 1, endLine), ["character"] = 0 }
            }
        }, ct);

    private Task<JsonElement?> GetLspDocumentHighlightsAsync(string? filePath, int line, int character, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/documentHighlight", CreateLspPositionParams(filePath!, line, character), ct);

    private Task<JsonElement?> GetLspTypeDefinitionAsync(string? filePath, int line, int character, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/typeDefinition", CreateLspPositionParams(filePath!, line, character), ct);

    private Task<JsonElement?> GetLspImplementationAsync(string? filePath, int line, int character, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/implementation", CreateLspPositionParams(filePath!, line, character), ct);

    private async Task<bool> TryLspNavigateFeatureAsync(string? filePath, int offset, string method, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var requestPane = _activeEditorPane;
        var requestTab = requestPane.ActiveTab;
        var requestEditor = GetEditorForPane(requestPane);
        var activeDocument = requestEditor?.Document;
        if (activeDocument is null) return false;
        var requestVersion = activeDocument.Version;
        var encoding = GetLspClientForFile(filePath)?.PositionEncoding ?? "utf-16";
        var (line, character) = LspPath.OffsetToLspPosition(activeDocument, offset, encoding);
        var result = await (method == "textDocument/typeDefinition"
            ? GetLspTypeDefinitionAsync(filePath, line, character, ct)
            : GetLspImplementationAsync(filePath, line, character, ct)).ConfigureAwait(false);
        var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            requestEditor is not null && ReferenceEquals(requestPane.ActiveTab, requestTab) && ReferenceEquals(requestEditor.Document, activeDocument) &&
            ReferenceEquals(activeDocument.Version, requestVersion) &&
            FileSystemPaths.Equals(requestTab is { IsUntitled: false } ? requestTab.Path : null, filePath) &&
            requestEditor.TextArea.Caret.Offset == offset);
        if (!requestIsCurrent) return false;
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return false;
        var item = result.Value.ValueKind == JsonValueKind.Array ? result.Value.EnumerateArray().FirstOrDefault() : result.Value;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : item.TryGetProperty("targetUri", out var tu) ? tu.GetString() : null;
        var range = item.TryGetProperty("range", out var r) ? r : item.TryGetProperty("targetSelectionRange", out var tsr) ? tsr : item.TryGetProperty("targetRange", out var tr) ? tr : default;
        if (string.IsNullOrWhiteSpace(uri) || range.ValueKind != JsonValueKind.Object) return false;
        var start = range.TryGetProperty("start", out var startEl) ? startEl : default;
        var path = FileUriToPath(uri);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var client = GetLspClientForFile(path);
        var targetLine = LspDiagnosticInt(LspDiagnosticChild(start, "line"), 0);
        var targetCharacter = LspDiagnosticInt(LspDiagnosticChild(start, "character"), 0);
        var targetEncoding = client?.PositionEncoding ?? "utf-16";
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(path, requestPane);
            var targetEditor = GetEditorForPane(ResolveEditorPaneForOpen(requestPane));
            if (targetEditor.Document is null) return;
            var targetOffset = LspPath.OffsetFromLspPosition(targetEditor.Document, targetLine, targetCharacter, targetEncoding);
            targetEditor.TextArea.Caret.Offset = Math.Clamp(targetOffset, 0, targetEditor.Document.TextLength);
            targetEditor.TextArea.Caret.BringCaretToView();
            targetEditor.Focus();
        });
        return true;
    }

    internal static string ApplyLspTextEdits(string text, IReadOnlyList<(int Start, int End, string NewText)> edits)
    {
        if (!TryApplyLspTextEdits(text, edits, out var result))
            throw new InvalidDataException("The language server returned invalid or overlapping text edits.");
        return result;
    }

    private static bool TryApplyLspTextEdits(string text, IReadOnlyList<(int Start, int End, string NewText)> edits, out string result)
    {
        result = text;
        if (edits.Count == 0) return true;

        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.End).ToArray();
        var previousEnd = -1;
        var previousStart = -1;
        foreach (var edit in ordered)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > text.Length)
                return false;
            if (edit.Start < previousEnd || (edit.Start == previousStart && edit.Start == edit.End))
                return false;
            previousStart = edit.Start;
            previousEnd = edit.End;
        }

        var builder = new StringBuilder(text.Length + (ordered.Length << 4));
        var copied = 0;
        foreach (var item in ordered)
        {
            builder.Append(text, copied, item.Start - copied);
            builder.Append(item.NewText ?? string.Empty);
            copied = item.End;
        }
        builder.Append(text, copied, text.Length - copied);
        result = builder.ToString();
        return true;
    }

    private static bool LspTextRangesOverlap(int firstStart, int firstEnd, int secondStart, int secondEnd)
    {
        if (firstStart == firstEnd && secondStart == secondEnd) return firstStart == secondStart;
        if (firstStart == firstEnd) return firstStart > secondStart && firstStart < secondEnd;
        if (secondStart == secondEnd) return secondStart > firstStart && secondStart < firstEnd;
        return firstStart < secondEnd && secondStart < firstEnd;
    }

    private static bool TryOffsetFromLspPosition(string text, int line, int character, string encoding, out int offset)
    {
        return TryOffsetFromLspPosition(GetLspLineStarts(text), text, line, character, encoding, out offset);
    }

    internal static bool TryOffsetFromLspPosition(AvaloniaEdit.Document.TextDocument document, int line, int character, string encoding, out int offset)
    {
        offset = 0;
        if (line < 0 || line >= document.LineCount || character < 0)
            return false;
        var documentLine = document.GetLineByNumber(line + 1);
        var lineText = document.GetTextAsMemory(documentLine.Offset, documentLine.Length).Span;
        if (character > LspPath.PositionLength(lineText, encoding))
            return false;

        offset = LspPath.OffsetFromLspPosition(document, line, character, encoding);
        var roundTrip = LspPath.OffsetToLspPosition(document, offset, encoding);
        return roundTrip.line == line && roundTrip.character == character;
    }

    private static bool TryOffsetFromLspPosition(int[] starts, string text, int line, int character, string encoding, out int offset)
    {
        offset = 0;
        if (line < 0 || line >= starts.Length || character < 0)
            return false;
        var lineStart = starts[line];
        var lineEnd = line + 1 < starts.Length ? starts[line + 1] : text.Length;
        while (lineEnd > lineStart && text[lineEnd - 1] is '\r' or '\n')
            lineEnd--;
        if (character > LspPath.PositionLength(text.AsSpan(lineStart, lineEnd - lineStart), encoding))
            return false;
        offset = LspPath.OffsetFromLspPosition(starts, text, line, character, encoding);
        var roundTrip = LspPath.OffsetToLspPosition(text, offset, encoding);
        return roundTrip.line == line && roundTrip.character == character;
    }

    internal static bool TryReadLspTextEdits(string text, JsonElement editsElement, string encoding, out List<(int Start, int End, string NewText)> edits, out string updated)
    {
        edits = new();
        updated = text;
        if (editsElement.ValueKind != JsonValueKind.Array)
            return false;
        var lineStarts = GetLspLineStarts(text);
        foreach (var item in editsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("newText", out var newTextElement) || newTextElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object ||
                !range.TryGetProperty("start", out var start) || !range.TryGetProperty("end", out var end) ||
                start.ValueKind != JsonValueKind.Object || end.ValueKind != JsonValueKind.Object ||
                !start.TryGetProperty("line", out var startLine) || !start.TryGetProperty("character", out var startCharacter) ||
                !end.TryGetProperty("line", out var endLine) || !end.TryGetProperty("character", out var endCharacter) ||
                !startLine.TryGetInt32(out var startLineNumber) || !startCharacter.TryGetInt32(out var startCharacterNumber) ||
                !endLine.TryGetInt32(out var endLineNumber) || !endCharacter.TryGetInt32(out var endCharacterNumber) ||
                startCharacterNumber < 0 || endCharacterNumber < 0 ||
                !TryOffsetFromLspPosition(lineStarts, text, startLineNumber, startCharacterNumber, encoding, out var startOffset) ||
                !TryOffsetFromLspPosition(lineStarts, text, endLineNumber, endCharacterNumber, encoding, out var endOffset) || endOffset < startOffset)
                return false;
            edits.Add((startOffset, endOffset, newTextElement.GetString() ?? string.Empty));
        }
        return TryApplyLspTextEdits(text, edits, out updated);
    }

    private static int MapCaretThroughLspEdits(int caretOffset, IReadOnlyList<(int Start, int End, string NewText)> edits)
    {
        var deltaBeforeCaret = 0;
        foreach (var item in edits.OrderBy(e => e.Start))
        {
            if (caretOffset < item.Start)
                return Math.Max(0, caretOffset + deltaBeforeCaret);
            if (caretOffset <= item.End)
            {
                var mappedWithinEdit = caretOffset == item.End
                    ? item.NewText.Length
                    : Math.Min(item.NewText.Length, caretOffset - item.Start);
                return Math.Max(0, item.Start + deltaBeforeCaret + mappedWithinEdit);
            }
            deltaBeforeCaret += item.NewText.Length - (item.End - item.Start);
        }
        return Math.Max(0, caretOffset + deltaBeforeCaret);
    }

    private void ApplyLspEditsToActiveDocument(IReadOnlyList<(int Start, int End, string NewText)> edits)
    {
        ApplyLspEditsToDocument(EditorTextBox.Document, edits, [EditorTextBox]);
    }

    private void ApplyLspEditsToDocument(
        AvaloniaEdit.Document.TextDocument document,
        IReadOnlyList<(int Start, int End, string NewText)> edits,
        IEnumerable<AvaloniaEdit.TextEditor> editors)
    {
        var caretStates = editors
            .Where(editor => ReferenceEquals(editor.Document, document))
            .Select(editor => (Editor: editor, Caret: editor.TextArea.Caret.Offset))
            .ToArray();
        document.UndoStack.StartUndoGroup();
        try
        {
            foreach (var edit in edits.OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.End))
                document.Replace(edit.Start, edit.End - edit.Start, edit.NewText);
        }
        finally
        {
            document.UndoStack.EndUndoGroup();
        }

        foreach (var state in caretStates)
            state.Editor.TextArea.Caret.Offset = Math.Clamp(MapCaretThroughLspEdits(state.Caret, edits), 0, document.TextLength);

        if (ReferenceEquals(_editorPane.ActiveTab?.Document, document) &&
            ReferenceEquals(PrimaryEditorTextBox.Document, document))
            SavePaneViewState(_editorPane, PrimaryEditorTextBox);
        if (ReferenceEquals(_secondaryEditorPane.ActiveTab?.Document, document) &&
            ReferenceEquals(SecondaryEditorTextBox.Document, document))
            SavePaneViewState(_secondaryEditorPane, SecondaryEditorTextBox);
    }

    private async Task<bool> TryLspGoToDefinitionAsync(string? filePath, int offset, CancellationToken ct = default)
    {
        if (!LspEnabled) return false;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return false;
        var cfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (cfg is null) return false;
        var workspace = GetWorkspaceRootForFile(filePath);
        var client = _lspManager.TryGetClient(workspace, cfg);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized || !client.Supports("textDocument/definition")) return false;

        var uri = FilePathToUri(filePath);
        var requestPane = _activeEditorPane;
        var requestTab = requestPane.ActiveTab;
        var requestEditor = GetEditorForPane(requestPane);
        var activeDocument = requestEditor?.Document;
        if (activeDocument is null) return false;
        var requestVersion = activeDocument.Version;
        var (line, character) = LspPath.OffsetToLspPosition(activeDocument, offset, client.PositionEncoding);
        await FlushLspDocumentAsync(filePath, string.Empty).ConfigureAwait(false);
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
        var requestIsCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            requestEditor is not null && ReferenceEquals(requestPane.ActiveTab, requestTab) && ReferenceEquals(requestEditor.Document, activeDocument) &&
            ReferenceEquals(activeDocument.Version, requestVersion) &&
            FileSystemPaths.Equals(requestTab is { IsUntitled: false } ? requestTab.Path : null, filePath) &&
            requestEditor.TextArea.Caret.Offset == offset);
        if (!requestIsCurrent) return false;

        var locations = new List<(string targetUri, int sLine, int sChar, int eLine, int eChar)>();
        void AddLoc(JsonElement loc)
        {
            if (!loc.TryGetProperty("uri", out var u))
            {
                if (!loc.TryGetProperty("targetUri", out u)) return;
            }
            var targetUri = u.GetString() ?? "";
            var range = loc.TryGetProperty("range", out var r) ? r :
                loc.TryGetProperty("targetSelectionRange", out var selectionRange) ? selectionRange :
                loc.TryGetProperty("targetRange", out var targetRange) ? targetRange : default;
            if (range.ValueKind != JsonValueKind.Object) return;
            var start = range.TryGetProperty("start", out var s) ? s : default;
            var end = range.TryGetProperty("end", out var e) ? e : default;
            var sl = LspDiagnosticInt(LspDiagnosticChild(start, "line"), 0);
            var sc = LspDiagnosticInt(LspDiagnosticChild(start, "character"), 0);
            var el = LspDiagnosticInt(LspDiagnosticChild(end, "line"), sl);
            var ec = LspDiagnosticInt(LspDiagnosticChild(end, "character"), sc);
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
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            KodoDiagnostics.LogDebug($"LSP definition target not found: {first.targetUri}");
            return false;
        }
        var targetClient = GetLspClientForFile(targetPath);
        var targetEncoding = targetClient?.PositionEncoding ?? "utf-16";

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(targetPath, requestPane);
            var targetEditor = GetEditorForPane(ResolveEditorPaneForOpen(requestPane));
            if (targetEditor.Document is null) return;
            var targetOffset = LspPath.OffsetFromLspPosition(targetEditor.Document, first.sLine, first.sChar, targetEncoding);
            targetEditor.TextArea.Caret.Offset = Math.Clamp(targetOffset, 0, targetEditor.Document.TextLength);
            targetEditor.TextArea.Caret.BringCaretToView();
            targetEditor.Focus();
        });

        return true;
    }
}
