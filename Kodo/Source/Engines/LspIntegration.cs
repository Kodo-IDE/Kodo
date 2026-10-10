// Licensed under the GNU GPL-v3.0
#pragma warning disable CA1416
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Kodo.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Kodo;

public partial class MainWindow
{
    private readonly LspManager _lspManager = new();
    private static StringComparer LspPathComparer => FileSystemPaths.Comparer;
    private readonly Dictionary<string, int> _lspDocumentVersions = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspOpenDocuments = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, LspClient> _lspDocumentClients = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspMissingNotified = new(FileSystemPaths.Comparer);
    private readonly DispatcherTimer _lspDidChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<string, string> _lspPendingTexts = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, List<LspIncrementalChange>> _lspPendingChanges = new(FileSystemPaths.Comparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lspDocumentSyncGates = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, List<LspRawDiagnostic>> _lspDiagnostics = new(FileSystemPaths.Comparer);

    private readonly Dictionary<string, List<JsonElement>> _lspRawDiagnostics = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, int> _lspDiagnosticVersions = new(FileSystemPaths.Comparer);
    private readonly HashSet<LspClient> _lspSubscribedClients = new();
    private readonly object _lspDiagnosticsLock = new();
    private readonly HashSet<string> _lspDiagnosticRefreshPending = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspPendingOpens = new(FileSystemPaths.Comparer);
    private readonly object _lspOpenLock = new();
    private CancellationTokenSource? _lspHoverCts;
    private readonly object _lspHoverLock = new();
    private readonly Dictionary<string, Task<string?>> _lspPendingHovers = new();
    private readonly object _lspHoverCacheLock = new();

    private sealed record LspRawDiagnostic(int StartLine, int StartChar, int EndLine, int EndChar, string Message, string Severity, string Code, string Source);
    private sealed record LspIncrementalChange(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Text);
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

            var sweep = await Task.Run(() => SweepLanguageServersAsync(extensionSnapshot, settingsSnapshot));

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
                    sweep = await Task.Run(() => SweepLanguageServersAsync(extensionSnapshot, settingsSnapshot));
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
        AppSettings settings)
    {
        var ready = new List<string>();
        var installable = new List<(LoadedExtension Ext, LspConfiguration Cfg, LspResolution Res)>();
        var blockedByRuntime = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var manualOnly = new List<string>();
        var missing = new List<string>();

        foreach (var ext in extensions)
        {
            foreach (var cfg in ext.AllLspConfigurations.ToList())
            {
                LspResolution res;
                try
                {
                    LspProviderRegistry.RegisterConsumer(cfg.EffectiveProviderId, ext.Id);
                    res = await LspServerResolver.ResolveAsync(cfg, settings, ext.Id).ConfigureAwait(false);
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

    private static string FixCorruptedPath(string p) => LspPath.FixCorruptedPath(p);

    private static string GetLanguageId(LspConfiguration lsp, string? filePath) => LspPath.GetLanguageId(lsp, filePath);

    private SemaphoreSlim GetLspDocumentSyncGate(string filePath) =>
        _lspDocumentSyncGates.GetOrAdd(NormalizeFilePath(filePath), static _ => new SemaphoreSlim(1, 1));

    private void InitLspDocumentSync()
    {
        _lspDidChangeTimer.Tick += LspDidChangeTimer_OnTick;
        if (EditorTextBox is not null)
        {
            EditorTextBox.DocumentChanged += LspEditorDocument_OnChanged;
            TrackLspDocument(EditorTextBox.Document);
        }
    }

    private void LspEditorDocument_OnChanged(object? sender, AvaloniaEdit.Document.DocumentChangedEventArgs e) =>
        TrackLspDocument(e.NewDocument);

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
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(path) || !_lspDocumentClients.TryGetValue(path, out client)) return;
        }
        var start = GetLspDocumentPosition(document, e.Offset, client.PositionEncoding);
        var end = GetLspDocumentPosition(document, e.Offset + e.RemovalLength, client.PositionEncoding);
        var change = new LspIncrementalChange(start.line, start.character, end.line, end.character, e.InsertedText.Text);
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(path) || !_lspDocumentClients.TryGetValue(path, out var currentClient) || !ReferenceEquals(client, currentClient)) return;
            if (!_lspPendingChanges.TryGetValue(path, out var changes))
                _lspPendingChanges[path] = changes = new List<LspIncrementalChange>();
            changes.Add(change);
        }
        Dispatcher.UIThread.Post(ArmLspDidChangeTimer);
    }

    private static (int line, int character) GetLspDocumentPosition(AvaloniaEdit.Document.TextDocument document, int offset, string encoding)
    {
        offset = Math.Clamp(offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        var columnOffset = Math.Clamp(offset - line.Offset, 0, line.Length);
        var columnText = document.GetText(line.Offset, columnOffset);
        return (line.LineNumber - 1, LspPath.PositionLength(columnText, encoding));
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
        if (!FileSystemPaths.Equals(filePath, _currentFilePath) || EditorTextBox?.Document is null) return;
        lock (_lspOpenLock) _lspPendingTexts[normalizedPath] = string.Empty;
        ArmLspDidChangeTimer();
    }

    private void ArmLspDidChangeTimer()
    {
        _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(50);
        _lspDidChangeTimer.Stop();
        _lspDidChangeTimer.Start();
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
                    if (!string.IsNullOrWhiteSpace(_currentFilePath) && IsSameDocument(_currentFilePath, _currentFilePath))
                    {
                        lock (_lspOpenLock) _lspPendingOpens.Remove(NormalizeFilePath(_currentFilePath));
                        var currentPath = _currentFilePath;
                        Dispatcher.UIThread.Post(() =>
                        {
                            var text = EditorTextBox?.Document?.Text;
                            if (text is not null) _ = LspNotifyDidOpenAsync(currentPath, text);
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

        var settings = BuildLspResolverSettings();
        var resolution = await LspServerResolver.ResolveAsync(targetCfg, settings, lspExt.Id).ConfigureAwait(false);
        if (!LspEnabled)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            return;
        }
        KodoDiagnostics.LogDebug($"LSP resolve {lspExt.Id} source={resolution.Source} exe={resolution.ExecutablePath} canInstall={resolution.CanInstall} err={resolution.Error}");
        if (!resolution.IsReady)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            await HandleLspNotReadyAsync(lspExt, resolution, filePath).ConfigureAwait(false);
            return;
        }
        else
        {
            lspExt.LspStatus = LspDependencyStatus.Available;
            lspExt.LspStatusMessage = null;
            var pidOk = resolution.ResolvedConfiguration.EffectiveProviderId;
            lspExt.LspProviderStatuses[pidOk] = (LspDependencyStatus.Available, null);
            LspProviderRegistry.RegisterConsumer(pidOk, lspExt.Id);
            LspProviderRegistry.SetStatus(pidOk, LspDependencyStatus.Available, null, resolution.Version, resolution.ExecutablePath, resolution.Source == LspServerSource.Managed);
        }

        var resolvedConfig = resolution.ResolvedConfiguration;
        var workspace = GetWorkspaceRootForFile(filePath);
        LspClient client;
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
                    ExtensionsStatusText = $"Language server '{resolvedConfig.Command}' not found for '{lspExt.Name}'. Install it and ensure it is on PATH. Highlighting still works.";
                    await ShowWarningDialogAsync($"{lspExt.Name} – language server not found",
                        new FileNotFoundException($"{lspExt.Name} language server could not be started.\n\n'{resolvedConfig.Command}' was not found.\nCheck that {resolvedConfig.Command} is installed and available on PATH.\n\nHighlighting remains available.", ex));
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

        var activeText = await Dispatcher.UIThread.InvokeAsync(() =>
            FileSystemPaths.Equals(_currentFilePath, filePath) ? EditorTextBox?.Document?.Text : null);
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

    private async Task FlushLspDocumentAsync(string filePath, string text)
    {
        if (!LspEnabled || string.IsNullOrWhiteSpace(filePath)) return;
        var normalized = NormalizeFilePath(filePath);
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(normalized)) return;
            if (!_lspPendingTexts.ContainsKey(normalized) && !_lspPendingChanges.ContainsKey(normalized))
                return;
        }
        if (Dispatcher.UIThread.CheckAccess()) _lspDidChangeTimer.Stop();
        await LspSyncDocumentAsync(normalized, () => text).ConfigureAwait(false);
    }

    private async Task LspSyncDocumentAsync(string filePath, Func<string> textFactory)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        bool isOpen;
        lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
        if (!isOpen)
        {
            var text = await Dispatcher.UIThread.InvokeAsync(() =>
                FileSystemPaths.Equals(_currentFilePath, filePath) ? EditorTextBox?.Document?.Text :
                OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, filePath))?.Content);
            if (text is null) return;
            await LspNotifyDidOpenAsync(filePath, text).ConfigureAwait(false);
            return;
        }
        var gate = GetLspDocumentSyncGate(filePath);
        await gate.WaitAsync().ConfigureAwait(false);
        List<LspIncrementalChange> pendingChanges = new();
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
                    if (!pendingSync) return (Text: (string?)null, UsesIncrementalChanges: false, HasPending: false);
                    pendingSync = _lspPendingTexts.Remove(filePath) || _lspPendingChanges.ContainsKey(filePath);
                    if (_lspPendingChanges.Remove(filePath, out var changes)) pendingChanges = changes;
                    syncCaptured = true;
                }
                LspClient? syncClient;
                lock (_lspOpenLock) _lspDocumentClients.TryGetValue(filePath, out syncClient);
                var usesIncrementalChanges = pendingChanges.Count > 0 && syncClient is not null && syncClient.SupportsIncrementalSync();
                AvaloniaEdit.Document.TextDocument? activeDocument = null;
                EditorTab? inactiveTab = null;
                if (FileSystemPaths.Equals(_currentFilePath, filePath)) activeDocument = EditorTextBox?.Document;
                else inactiveTab = OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, filePath));
                var currentText = usesIncrementalChanges ? null : activeDocument?.Text ?? inactiveTab?.Content;
                return (Text: currentText, UsesIncrementalChanges: usesIncrementalChanges, HasPending: true);
            });
            if (!snapshot.HasPending) return;
            var text = snapshot.Text ?? (Dispatcher.UIThread.CheckAccess() ? textFactory() : await Dispatcher.UIThread.InvokeAsync(textFactory));
            if (!pendingSync && pendingChanges.Count == 0) return;
            var prepared = await PrepareLspChangeAsync(filePath).ConfigureAwait(false);
            if (prepared is null) throw new InvalidOperationException($"LSP client unavailable for document sync: {filePath}");
            var (uri, version, client) = prepared.Value;
            if (snapshot.UsesIncrementalChanges && !client.SupportsIncrementalSync())
                throw new InvalidOperationException($"LSP sync capability changed before sending changes: {filePath}");
            object[] contentChanges;
            if (snapshot.UsesIncrementalChanges && client.SupportsIncrementalSync())
            {
                var incrementalChanges = new List<object?>(pendingChanges.Count);
                foreach (var pendingChange in pendingChanges)
                {
                    incrementalChanges.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["range"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["start"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = pendingChange.StartLine, ["character"] = pendingChange.StartCharacter },
                            ["end"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = pendingChange.EndLine, ["character"] = pendingChange.EndCharacter }
                        },
                        ["text"] = pendingChange.Text
                    });
                }
                contentChanges = incrementalChanges.ToArray()!;
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
                            if (!_lspPendingChanges.TryGetValue(filePath, out var remaining)) _lspPendingChanges[filePath] = remaining = new List<LspIncrementalChange>();
                            remaining.InsertRange(0, pendingChanges);
                        }
                    }
                }
            }
            KodoDiagnostics.LogDebug($"LSP didChange failed for {filePath}", ex);
        }
        finally { gate.Release(); }
    }

    private async Task LspNotifyDidCloseAsync(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalized = NormalizeFilePath(filePath);
            await LspSyncDocumentAsync(normalized, static () => string.Empty).ConfigureAwait(false);
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
        lock (_lspOpenLock)
        {
            if (!_lspOpenDocuments.Contains(filePath)) return;
            _lspOpenDocuments.Remove(filePath);
            _lspDocumentVersions.Remove(filePath);
            _lspPendingChanges.Remove(filePath);
            _lspDocumentClients.Remove(filePath);
            _lspPendingOpens.Remove(filePath);
            _lspPendingTexts.Remove(filePath);
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
                    _lspDocumentClients.Remove(path);
                    _lspOpenDocuments.Remove(path);
                    _lspDocumentVersions.Remove(path);
                    _lspPendingChanges.Remove(path);
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
            }
            Dispatcher.UIThread.Post(() =>
            {
                _ = UpdateErrorHighlightingAsync();
                _ = ReopenLspDocumentsAfterExitAsync(documents);
            });
        };
    }

    private async Task ReopenLspDocumentsAfterExitAsync(IReadOnlyList<string> documents)
    {
        foreach (var path in documents)
        {
            var text = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (FileSystemPaths.Equals(_currentFilePath, path)) return EditorTextBox?.Document?.Text;
                return OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, path))?.Content;
            });
            if (text is null) continue;
            try { await LspNotifyDidOpenAsync(path, text).ConfigureAwait(false); }
            catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP document reopen failed for {path}", ex); }
        }
    }

    private async Task<object?> HandleLspServerRequestAsync(string method, JsonElement? parameters)
    {
        if (method != "workspace/applyEdit" || parameters is not JsonElement request ||
            !request.TryGetProperty("edit", out var edit) || edit.ValueKind != JsonValueKind.Object)
            return null;
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var document = EditorTextBox?.Document;
            if (document is null || string.IsNullOrWhiteSpace(_currentFilePath)) return null;
            return new CodeActionSnapshot(_currentFilePath, document.Text, EditorTextBox!.TextArea.Caret.Offset, 0, Math.Min(1, document.TextLength));
        });
        if (snapshot is null) return new Dictionary<string, object?>(StringComparer.Ordinal) { ["applied"] = false, ["failureReason"] = "No active document" };
        var outcome = await ApplyWorkspaceEditAsync(edit, snapshot, EditScope.WorkspaceEdit).ConfigureAwait(false);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applied"] = outcome == CodeActionOutcome.Applied,
            ["failureReason"] = outcome == CodeActionOutcome.Applied ? null : "The workspace edit could not be applied to the open documents"
        };
    }

    private void HandleLspNotification(string method, JsonElement? @params)
    {
        if (method == "textDocument/publishDiagnostics")
            HandlePublishDiagnostics(@params);
    }

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

            var diagnostics = new List<LspRawDiagnostic>();
            var rawDiagnostics = new List<JsonElement>();
            if (root.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in diags.EnumerateArray())
                {
                    rawDiagnostics.Add(d.Clone());
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
                    if (d.TryGetProperty("code", out var ce2) && ce2.ValueKind == JsonValueKind.Object && ce2.TryGetProperty("value", out var cv)) code = cv.ToString();
                    else if (ce2.ValueKind == JsonValueKind.Number) code = ce2.GetInt32().ToString();
                    var source = d.TryGetProperty("source", out var se2) ? se2.GetString() ?? "lsp" : "lsp";
                    diagnostics.Add(new LspRawDiagnostic(sLine, sChar, eLine, eChar, msg, severity, code, source));
                }
            }

            string refreshKey;
            int? publishVersion = root.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.Number ? versionEl.GetInt32() : null;
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
                _lspDiagnostics[normPath] = diagnostics;
                _lspRawDiagnostics[normPath] = rawDiagnostics;
                if (publishVersion.HasValue)
                    _lspDiagnosticVersions[normPath] = publishVersion.Value;
                else
                    _lspDiagnosticVersions.Remove(normPath);
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
                    _ = UpdateErrorHighlightingAsync();
                    if (_lspDocumentRefreshPending || _lspDocumentRefreshFailures > 0)
                        QueueLspPresentationRefresh();
                }
                else
                {
                    KodoDiagnostics.LogDebug($"LSP diagnostics stored for inactive file {filePath} (current {_currentFilePath}) – will show on activation");
                    UpdateInactiveTabDiagnosticsForFile(filePath);
                }
            });
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP publishDiagnostics handling failed", ex); }
    }

    private static string FileUriToPath(string uriOrPath) => LspPath.FileUriToPath(uriOrPath);

    private List<ErrorSpan> GetLspDiagnosticsForFile(string? filePath, string text)
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
        var encoding = GetLspClientForFile(filePath)?.PositionEncoding ?? "utf-16";
        foreach (var d in raw)
        {
            if (string.IsNullOrWhiteSpace(d.Message)) continue;
            var start = LspPath.OffsetFromLspPosition(text, d.StartLine, d.StartChar, encoding);
            var end = LspPath.OffsetFromLspPosition(text, d.EndLine, d.EndChar, encoding);
            if (start < 0 || start >= text.Length)
            {
                KodoDiagnostics.LogDebug($"LSP diag filtered: start out of range line={d.StartLine} char={d.StartChar} -> offset {start} len {text.Length}");
                continue;
            }
            var len = Math.Max(1, end - start);
            if (start + len > text.Length) len = Math.Max(1, text.Length - start);
            spans.Add(new ErrorSpan(start, len, d.Message.Trim(), d.Severity, d.Code ?? "", d.Source ?? "lsp"));
        }
        if (KodoDiagnostics.VerboseLoggingEnabled && spans.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans={spans.Count} first=[{spans[0].StartOffset}:{spans[0].Length}] {spans[0].Message}");
        else if (KodoDiagnostics.VerboseLoggingEnabled && raw.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans=0 (all filtered)");
        return spans;
    }

    private LspClient? GetLspClientForFile(string filePath)
    {
        var extension = ResolveLspExtensionForFile(filePath);
        var configuration = ResolveLspConfigurationForFile(filePath) ?? extension?.Lsp ?? extension?.Lsps.FirstOrDefault();
        return configuration is null ? null : _lspManager.TryGetClient(GetWorkspaceRootForFile(filePath), configuration);
    }

    private async Task<string?> GetLspTargetTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var openText = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (FileSystemPaths.Equals(_currentFilePath, filePath)) return EditorTextBox?.Document?.Text;
            return OpenTabs.FirstOrDefault(tab => !string.IsNullOrWhiteSpace(tab.Path) && FileSystemPaths.Equals(tab.Path, filePath))?.Content;
        });
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

    private async Task<IReadOnlyList<InsightSuggestion>> GetLspCompletionSuggestionsAsync(string? filePath, int offset, string text, string prefix, CancellationToken ct = default)
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
        await FlushLspDocumentAsync(filePath, text).ConfigureAwait(false);
        KodoDiagnostics.LogDebug($"LSP completion request file={filePath} offset={offset} prefix='{prefix}'");

        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(client, text, offset);
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
            FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is not null &&
            string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal) &&
            EditorTextBox.TextArea.Caret.Offset == offset);
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
            if (!item.TryGetProperty("label", out var labelEl)) continue;
            var label = labelEl.GetString();
            if (string.IsNullOrWhiteSpace(label)) continue;
            string insertText = label;
            int? replaceStartOffset = null;
            int? replaceEndOffset = null;
            var additionalEdits = new List<(int Start, int End, string NewText)>();
            if (item.TryGetProperty("textEdit", out var te) && te.ValueKind == JsonValueKind.Object && te.TryGetProperty("newText", out var nt))
            {
                insertText = nt.GetString() ?? insertText;
                if (te.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Object &&
                    range.TryGetProperty("start", out var start) && range.TryGetProperty("end", out var end) &&
                    start.TryGetProperty("line", out var startLine) && start.TryGetProperty("character", out var startCharacter) &&
                    end.TryGetProperty("line", out var endLine) && end.TryGetProperty("character", out var endCharacter) &&
                    TryOffsetFromLspPosition(text, startLine.GetInt32(), startCharacter.GetInt32(), client.PositionEncoding, out var startOffset) &&
                    TryOffsetFromLspPosition(text, endLine.GetInt32(), endCharacter.GetInt32(), client.PositionEncoding, out var endOffset) && endOffset >= startOffset)
                {
                    replaceStartOffset = startOffset;
                    replaceEndOffset = endOffset;
                }
            }
            else if (item.TryGetProperty("insertText", out var ins) && !string.IsNullOrWhiteSpace(ins.GetString()))
                insertText = ins.GetString()!;

            if (item.TryGetProperty("additionalTextEdits", out var additional) && additional.ValueKind == JsonValueKind.Array)
            {
                var validAdditionalEdits = true;
                foreach (var edit in additional.EnumerateArray())
                {
                    if (!edit.TryGetProperty("newText", out var editText) || editText.ValueKind != JsonValueKind.String ||
                        !edit.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object ||
                        !range.TryGetProperty("start", out var start) || !range.TryGetProperty("end", out var end) ||
                        !start.TryGetProperty("line", out var startLine) || !start.TryGetProperty("character", out var startCharacter) ||
                        !end.TryGetProperty("line", out var endLine) || !end.TryGetProperty("character", out var endCharacter) ||
                        !TryOffsetFromLspPosition(text, startLine.GetInt32(), startCharacter.GetInt32(), client.PositionEncoding, out var startOffset) ||
                        !TryOffsetFromLspPosition(text, endLine.GetInt32(), endCharacter.GetInt32(), client.PositionEncoding, out var endOffset) || endOffset < startOffset)
                    {
                        validAdditionalEdits = false;
                        break;
                    }
                    additionalEdits.Add((startOffset, endOffset, editText.GetString() ?? string.Empty));
                }
                if (!validAdditionalEdits) continue;
            }

            var primaryStart = replaceStartOffset ?? Math.Clamp(offset - prefix.Length, 0, text.Length);
            var primaryEnd = replaceEndOffset ?? offset;
            if (additionalEdits.Any(edit => LspTextRangesOverlap(edit.Start, edit.End, primaryStart, primaryEnd)))
                continue;
            var orderedAdditionalEdits = additionalEdits.OrderBy(edit => edit.Start).ThenBy(edit => edit.End).ToArray();
            if (orderedAdditionalEdits.Zip(orderedAdditionalEdits.Skip(1), (left, right) => LspTextRangesOverlap(left.Start, left.End, right.Start, right.End)).Any(overlap => overlap))
                continue;

            var kindInt = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 0;
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
            suggestions.Add(new InsightSuggestion(insertText, kind, replaceStartOffset, replaceEndOffset, text, additionalEdits, 100 - suggestions.Count));
            if (suggestions.Count >= 25) break;
        }
        KodoDiagnostics.LogDebug($"LSP completion parsed items={items.Count} suggestions={suggestions.Count} for {filePath} samples={string.Join(", ", suggestions.Take(3).Select(s => s.Text))}");

        return suggestions;
    }

    private async Task<string?> GetLspHoverAsync(string? filePath, int offset, int line, int character, CancellationToken ct = default)
    {
        if (!LspEnabled || !LspHoverEnabled) return null;
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var hoverKey = $"{NormalizeFilePath(filePath)}:{offset}";
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
        var hoverTask = GetLspHoverInnerAsync(filePath, offset, line, character, ct);
        lock (_lspHoverCacheLock)
        {
            _lspPendingHovers[hoverKey] = hoverTask;
        }
        try { return await hoverTask.ConfigureAwait(false); }
        finally { lock (_lspHoverCacheLock) _lspPendingHovers.Remove(hoverKey); }
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
        var hoverText = await Dispatcher.UIThread.InvokeAsync(() => FileSystemPaths.Equals(_currentFilePath, filePath) ? EditorTextBox?.Document?.Text : null);
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

    private async Task<bool> TryLspFindReferencesAsync(string? filePath, int offset, string text, CancellationToken ct = default)
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
        await FlushLspDocumentAsync(filePath, text).ConfigureAwait(false);
        KodoDiagnostics.LogDebug($"LSP references request file={filePath} offset={offset}");
        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(client, text, offset);
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
            FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is not null &&
            string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal) &&
            EditorTextBox.TextArea.Caret.Offset == offset);
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
        var firstStart = firstRange.GetProperty("start");
        var firstPath = FileUriToPath(firstUri);
        if (string.IsNullOrWhiteSpace(firstPath)) return false;
        var firstText = await GetLspTargetTextAsync(firstPath, ct).ConfigureAwait(false);
        if (firstText is null) return false;
        var firstClient = GetLspClientForFile(firstPath);
        var firstOffset = LspPath.OffsetFromLspPosition(firstText, firstStart.GetProperty("line").GetInt32(), firstStart.GetProperty("character").GetInt32(), firstClient?.PositionEncoding ?? "utf-16");
        var word = GetLanguageWordAtOffset(text, offset);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(firstPath);
            Dispatcher.UIThread.Post(() =>
            {
                if (EditorTextBox?.Document is null) return;
                EditorTextBox.TextArea.Caret.Offset = Math.Clamp(firstOffset, 0, EditorTextBox.Document.TextLength);
                EditorTextBox.TextArea.Caret.BringCaretToView();
                EditorTextBox.Focus();
            }, DispatcherPriority.Background);
        });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ExtensionsStatusText = locations.Count == 1
                ? $"1 reference{(string.IsNullOrWhiteSpace(word) ? "" : $" to '{word}'")}."
                : $"{locations.Count} references{(string.IsNullOrWhiteSpace(word) ? "" : $" to '{word}'")} (jumped to first).";
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
        var applied = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (EditorTextBox?.Document is null) return;
            if (EditorTextBox.Document.TextLength != text.Length ||
                !string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal))
            {
                KodoDiagnostics.LogDebug($"LSP formatting rejected as stale for {filePath}");
                return;
            }
            if (!TryReadLspTextEdits(text, result.Value, client.PositionEncoding, out var parsedEdits, out var formatted))
            {
                KodoDiagnostics.LogDebug($"LSP formatting rejected invalid, overlapping, or out-of-range edits for {filePath}");
                return;
            }
            if (string.Equals(text, formatted, StringComparison.Ordinal)) return;
            ApplyLspEditsToActiveDocument(parsedEdits);
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
        await FlushLspDocumentAsync(filePath, text).ConfigureAwait(false);
        var (line, character) = OffsetToLspPosition(client, text, offset);
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
                FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is not null &&
                string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal) &&
                EditorTextBox.TextArea.Caret.Offset == offset);
            if (!requestIsCurrent) return false;
            return await ApplyExternalWorkspaceEditAsync(result.Value, filePath, text).ConfigureAwait(false);
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
                if (!FileSystemPaths.Equals(_currentFilePath, filePath) || EditorTextBox?.Document is null) return (Text: (string?)null, Document: (AvaloniaEdit.Document.TextDocument?)null, Version: _insightDocVersion);
                return (Text: EditorTextBox.Document.Text, Document: EditorTextBox.Document, Version: _insightDocVersion);
            });
            if (snapshot.Text is not null) await FlushLspDocumentAsync(filePath, snapshot.Text).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await client.SendRequestAsync(method, parameters, timeout.Token).ConfigureAwait(false);
            return await Dispatcher.UIThread.InvokeAsync(() =>
                FileSystemPaths.Equals(_currentFilePath, filePath) && ReferenceEquals(EditorTextBox?.Document, snapshot.Document) && _insightDocVersion == snapshot.Version
                    ? result
                    : null);
        }
        catch (Exception ex)
        {
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

    private Task<JsonElement?> GetLspInlayHintsAsync(string? filePath, int lineCount, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/inlayHint", new Dictionary<string, object?>
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) },
            ["range"] = new Dictionary<string, object?>
            {
                ["start"] = new Dictionary<string, object?> { ["line"] = 0, ["character"] = 0 },
                ["end"] = new Dictionary<string, object?> { ["line"] = Math.Max(0, lineCount), ["character"] = 0 }
            }
        }, ct);

    private Task<JsonElement?> GetLspDocumentHighlightsAsync(string? filePath, int offset, string text, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/documentHighlight", CreateLspPositionParams(filePath!, text, offset), ct);

    private Task<JsonElement?> GetLspTypeDefinitionAsync(string? filePath, int offset, string text, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/typeDefinition", CreateLspPositionParams(filePath!, text, offset), ct);

    private Task<JsonElement?> GetLspImplementationAsync(string? filePath, int offset, string text, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/implementation", CreateLspPositionParams(filePath!, text, offset), ct);

    private async Task<bool> TryLspNavigateFeatureAsync(string? filePath, int offset, string text, string method, CancellationToken ct = default)
    {
        var result = await (method == "textDocument/typeDefinition"
            ? GetLspTypeDefinitionAsync(filePath, offset, text, ct)
            : GetLspImplementationAsync(filePath, offset, text, ct)).ConfigureAwait(false);
        if (result is null || result.Value.ValueKind == JsonValueKind.Null) return false;
        var item = result.Value.ValueKind == JsonValueKind.Array ? result.Value.EnumerateArray().FirstOrDefault() : result.Value;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : item.TryGetProperty("targetUri", out var tu) ? tu.GetString() : null;
        var range = item.TryGetProperty("range", out var r) ? r : item.TryGetProperty("targetSelectionRange", out var tsr) ? tsr : item.TryGetProperty("targetRange", out var tr) ? tr : default;
        if (string.IsNullOrWhiteSpace(uri) || range.ValueKind != JsonValueKind.Object) return false;
        var start = range.GetProperty("start");
        var path = FileUriToPath(uri);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var targetText = await GetLspTargetTextAsync(path, ct).ConfigureAwait(false);
        if (targetText is null) return false;
        var client = GetLspClientForFile(path);
        var targetOffset = LspPath.OffsetFromLspPosition(targetText, start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(), client?.PositionEncoding ?? "utf-16");
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(path);
            Dispatcher.UIThread.Post(() =>
            {
                if (EditorTextBox?.Document is null) return;
                EditorTextBox.TextArea.Caret.Offset = Math.Clamp(targetOffset, 0, EditorTextBox.Document.TextLength);
                EditorTextBox.TextArea.Caret.BringCaretToView();
                EditorTextBox.Focus();
            }, DispatcherPriority.Background);
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

    private static bool TryOffsetFromLspPosition(int[] starts, string text, int line, int character, string encoding, out int offset)
    {
        offset = 0;
        if (line < 0 || line >= starts.Length)
            return false;
        offset = LspPath.OffsetFromLspPosition(starts, text, line, character, encoding);
        return true;
    }

    private static bool TryReadLspTextEdits(string text, JsonElement editsElement, string encoding, out List<(int Start, int End, string NewText)> edits, out string updated)
    {
        edits = new();
        updated = text;
        if (editsElement.ValueKind != JsonValueKind.Array)
            return false;
        var lineStarts = GetLspLineStarts(text);
        foreach (var item in editsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("newText", out var newTextElement) || newTextElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object ||
                !range.TryGetProperty("start", out var start) || !range.TryGetProperty("end", out var end) ||
                !start.TryGetProperty("line", out var startLine) || !start.TryGetProperty("character", out var startCharacter) ||
                !end.TryGetProperty("line", out var endLine) || !end.TryGetProperty("character", out var endCharacter) ||
                !TryOffsetFromLspPosition(lineStarts, text, startLine.GetInt32(), startCharacter.GetInt32(), encoding, out var startOffset) ||
                !TryOffsetFromLspPosition(lineStarts, text, endLine.GetInt32(), endCharacter.GetInt32(), encoding, out var endOffset) || endOffset < startOffset)
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
        var document = EditorTextBox.Document;
        var mappedCaret = MapCaretThroughLspEdits(EditorTextBox.TextArea.Caret.Offset, edits);
        var updated = ApplyLspTextEdits(document.Text, edits);
        document.UndoStack.StartUndoGroup();
        try
        {
            document.Text = updated;
        }
        finally
        {
            document.UndoStack.EndUndoGroup();
        }
        EditorTextBox.TextArea.Caret.Offset = Math.Clamp(mappedCaret, 0, document.TextLength);
    }

    private async Task<bool> TryLspGoToDefinitionAsync(string? filePath, int offset, string text, CancellationToken ct = default)
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
        await FlushLspDocumentAsync(filePath, text).ConfigureAwait(false);
        var (line, character) = OffsetToLspPosition(client, text, offset);
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
            FileSystemPaths.Equals(_currentFilePath, filePath) && EditorTextBox?.Document is not null &&
            string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal) &&
            EditorTextBox.TextArea.Caret.Offset == offset);
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
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            KodoDiagnostics.LogDebug($"LSP definition target not found: {first.targetUri}");
            return false;
        }
        var targetText = await GetLspTargetTextAsync(targetPath, ct).ConfigureAwait(false);
        if (targetText is null) return false;
        var targetClient = GetLspClientForFile(targetPath);
        var targetOffset = LspPath.OffsetFromLspPosition(targetText, first.sLine, first.sChar, targetClient?.PositionEncoding ?? "utf-16");

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
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
                await OpenFileFromPathAsync(targetPath);
                if (EditorTextBox?.Document is null) return;
                EditorTextBox.TextArea.Caret.Offset = Math.Clamp(targetOffset, 0, EditorTextBox.Document.TextLength);
                EditorTextBox.TextArea.Caret.BringCaretToView();
                EditorTextBox.Focus();
            }
        });

        return true;
    }
}
