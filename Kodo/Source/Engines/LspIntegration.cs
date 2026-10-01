// Licensed under the GNU GPL-v3.0
#pragma warning disable CA1416
using Avalonia.Threading;
using Avalonia.Controls;
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
    private readonly HashSet<string> _lspMissingNotified = new(FileSystemPaths.Comparer);
    private readonly DispatcherTimer _lspDidChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private string? _pendingLspChangePath;
    private readonly object _lspPendingLock = new();
    private readonly Dictionary<string, List<LspPendingEdit>> _lspPendingEdits = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> _lspForceFullSync = new(FileSystemPaths.Comparer);
    private readonly Dictionary<string, DateTime> _lspLastSyncUtc = new(FileSystemPaths.Comparer);
    private static readonly TimeSpan LspHugeFileSyncInterval = TimeSpan.FromSeconds(8);
    private const int LspIncrementalMaxEdits = 500;
    private const int LspIncrementalMaxChars = 100_000;
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
    private string _lastHoverKey = "";
    private DateTime _lastHoverTime = DateTime.MinValue;

    private sealed record LspRawDiagnostic(int StartLine, int StartChar, int EndLine, int EndChar, string Message, string Severity, string Code, string Source);

    private readonly LspFileResolver _lspFileResolver = new();

    private void InvalidateLspResolutionCaches() => _lspFileResolver.Invalidate();

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

    private void InitLspDocumentSync()
    {
        _lspDidChangeTimer.Tick += LspDidChangeTimer_OnTick;
    }

    private async void LspDidChangeTimer_OnTick(object? sender, EventArgs e)
    {
        _lspDidChangeTimer.Stop();
        if (!LspEnabled) return;
        string? path;
        lock (_lspPendingLock) { path = _pendingLspChangePath; _pendingLspChangePath = null; }
        if (string.IsNullOrWhiteSpace(path) || EditorTextBox?.Document is null) return;
        if (!FileSystemPaths.Equals(path, _currentFilePath))
        {
            lock (_lspOpenLock) _lspPendingEdits.Remove(NormalizeFilePath(path));
            return;
        }
        var remaining = LspSyncThrottleRemaining(path, EditorTextBox.Document.TextLength);
        if (remaining > TimeSpan.Zero)
        {
            lock (_lspPendingLock) _pendingLspChangePath = path;
            _lspDidChangeTimer.Interval = remaining;
            _lspDidChangeTimer.Start();
            KodoDiagnostics.LogDebug($"LSP sync throttled for {path} ({remaining.TotalSeconds:F1}s remaining)");
            return;
        }
        var document = EditorTextBox.Document;
        var textLength = document.TextLength;
        List<LspPendingEdit>? edits = null;
        lock (_lspOpenLock)
        {
            var key = NormalizeFilePath(path);
            if (_lspForceFullSync.Remove(key))
            {
                _lspPendingEdits.Remove(key);
            }
            else if (_lspPendingEdits.TryGetValue(key, out var list) && list.Count > 0)
            {
                edits = new(list);
                _lspPendingEdits.Remove(key);
            }
        }
        await LspSyncDocumentAsync(path, () => document.Text, textLength, edits).ConfigureAwait(false);
    }

    private void LspDocument_Changing(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e)
    {
        try
        {
            if (!LspEnabled) return;
            var path = _currentFilePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (ResolveLspExtensionForFile(path) is null) return;
            var doc = EditorTextBox?.Document;
            if (doc is null || !ReferenceEquals(sender, doc)) return;
            var key = NormalizeFilePath(path);
            var startOffset = Math.Clamp(e.Offset, 0, doc.TextLength);
            var endOffset = Math.Clamp(e.Offset + e.RemovalLength, 0, doc.TextLength);
            var startLine = doc.GetLineByOffset(startOffset);
            var endLine = doc.GetLineByOffset(endOffset);
            lock (_lspOpenLock)
            {
                if (_lspPendingEdits.Count > 16) _lspPendingEdits.Clear();
                if (startOffset < startLine.Offset || startOffset > startLine.Offset + startLine.Length ||
                    endOffset < endLine.Offset || endOffset > endLine.Offset + endLine.Length)
                {
                    _lspForceFullSync.Add(key);
                    return;
                }
                if (!_lspPendingEdits.TryGetValue(key, out var list))
                    _lspPendingEdits[key] = list = new();
                if (list.Count >= LspIncrementalMaxEdits ||
                    list.Sum(edit => (long)(edit.Text?.Length ?? 0)) + (e.InsertedText?.Text?.Length ?? 0) > LspIncrementalMaxChars)
                {
                    _lspPendingEdits.Remove(key);
                    _lspForceFullSync.Add(key);
                    return;
                }
                var start = doc.GetLocation(startOffset);
                var end = doc.GetLocation(endOffset);
                list.Add(new LspPendingEdit(start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1, e.InsertedText?.Text ?? string.Empty));
            }
        }
        catch { }
    }

    private void QueueLspDidChange(string filePath)
    {
        if (!LspEnabled) return;
        if (ResolveLspExtensionForFile(filePath) is null) return;
        lock (_lspPendingLock) _pendingLspChangePath = filePath;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ArmLspDidChangeTimer);
            return;
        }
        ArmLspDidChangeTimer();
    }

    private void ArmLspDidChangeTimer()
    {
        var curLen = EditorTextBox?.Document?.TextLength ?? 0;
        if (curLen > 250_000) _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(3000);
        else if (curLen > 120_000) _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(2000);
        else if (curLen > 80_000) _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(600);
        else _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(300);
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
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = $"Language server '{lspExt.Lsp!.Command}' not found for '{lspExt.Name}'. Manual install required.";
                await ShowWarningDialogAsync($"{lspExt.Name} – language server not found",
                    new FileNotFoundException($"{lspExt.Name} language support requires manual installation.\n\n'{lspExt.Lsp.Command}' was not found on PATH and cannot be auto-installed.\n\nPlease install {lspExt.Lsp.DisplayName ?? lspExt.Lsp.EffectiveProviderId} manually and ensure it is on PATH.\n\nHighlighting remains available."));
            });
            return false;
        }
        if (_lspMissingNotified.Add(lspExt.Id))
        {
            KodoDiagnostics.LogDebug($"LSP missing for {lspExt.Id}: {resolution.Error}");
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = resolution.Error ?? $"Language server '{lspExt.Lsp!.Command}' not found.";
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
            var body = autoInstall
                ? $"Installing {providerName} for {lspExt.Name}..."
                : $"{lspExt.Name} language support requires {providerName}.\n\nStatus: {(resolution.Source == LspServerSource.Incompatible ? $"Incompatible ({resolution.Version ?? "unknown"})" : "Not installed")}\n\nInstall {providerName} now?\n\nKodo will download it to %LocalAppData%\\Kodo\\Lsp\\{targetCfg.EffectiveProviderId} and verify it before use. You can also use an existing system installation.";
            bool shouldInstall = autoInstall;
            if (!autoInstall)
            {
                shouldInstall = await ShowConfirmationDialogAsync(title, body, confirmLabel: $"Install {providerName}", isDestructive: false).ConfigureAwait(false);
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
                ExtensionsStatusText = $"Installing {providerName}...";
                lspExt.LspStatus = LspDependencyStatus.Installing;
                if (!string.IsNullOrWhiteSpace(providerId))
                {
                    lspExt.LspProviderStatuses[providerId] = (LspDependencyStatus.Installing, null);
                    LspProviderRegistry.SetStatus(providerId, LspDependencyStatus.Installing);
                }
                var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() => ExtensionsStatusText = msg));
                var settings = BuildLspResolverSettings();
                var result = await LspInstallationManager.InstallAsync(targetCfg, settings, progress).ConfigureAwait(false);
                if (result.Kind == LspInstallationManager.InstallResultKind.Success || result.Kind == LspInstallationManager.InstallResultKind.AlreadyInstalled)
                {
                    ExtensionsStatusText = $"{providerName} installed successfully.";
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
                        if (EditorTextBox?.Document != null)
                            _ = LspNotifyDidOpenAsync(_currentFilePath, EditorTextBox.Document.Text);
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

        var isLargeFileForLsp = content.Length > 120_000;
        await Task.Delay(isLargeFileForLsp ? 500 : 0).ConfigureAwait(false);
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

        var uri = FilePathToUri(filePath);
        int version;
        lock (_lspOpenLock)
        {
            _lspPendingOpens.Remove(filePath);
            version = _lspDocumentVersions.TryGetValue(filePath, out var v) ? v + 1 : 1;
            _lspDocumentVersions[filePath] = version;
            _lspOpenDocuments.Add(filePath);
            _lspPendingEdits.Remove(filePath);
            _lspForceFullSync.Remove(filePath);
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
                ["text"] = content
            }
        };

        try
        {
            await client.SendNotificationAsync("textDocument/didOpen", didOpenParams).ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"LSP didOpen {uri} lang={languageId} ver={version} via {resolution.Source} {resolution.ExecutablePath}");
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP didOpen failed for {uri}", ex); }
        Dispatcher.UIThread.Post(() =>
        {
            if (LspEnabled && FileSystemPaths.Equals(_currentFilePath, filePath))
                QueueLspPresentationRefresh();
        });
    }

    private TimeSpan LspSyncThrottleRemaining(string filePath, int length)
    {
        if (length <= 120_000) return TimeSpan.Zero;
        lock (_lspOpenLock)
        {
            if (!_lspLastSyncUtc.TryGetValue(NormalizeFilePath(filePath), out var last)) return TimeSpan.Zero;
            var wait = LspHugeFileSyncInterval - (DateTime.UtcNow - last);
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    private async Task<(string uri, int version, LspClient client)?> PrepareLspChangeAsync(string filePath)
    {
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return null;
        var targetCfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (targetCfg is null) return null;
        var workspace = GetWorkspaceRootForFile(filePath);
        var resolveWatch = System.Diagnostics.Stopwatch.StartNew();
        var settings2 = BuildLspResolverSettings();
        var res2 = await LspServerResolver.ResolveAsync(targetCfg, settings2, lspExt.Id).ConfigureAwait(false);
        resolveWatch.Stop();
        KodoDiagnostics.ReportSlowStage("LSP didChange resolve", resolveWatch.ElapsedMilliseconds, 2000);
        var effectiveConfig = res2.IsReady ? res2.ResolvedConfiguration : targetCfg;
        var client = _lspManager.TryGetClient(workspace, effectiveConfig);
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

    private async Task LspNotifyDidChangeAsync(string filePath, string newContent)
    {
        if (!LspEnabled) return;
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        bool isOpen;
        lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
        if (!isOpen)
        {
            await LspNotifyDidOpenAsync(filePath, newContent).ConfigureAwait(false);
            return;
        }
        var prepared = await PrepareLspChangeAsync(filePath).ConfigureAwait(false);
        if (prepared is null) return;
        var (uri, version, client) = prepared.Value;

        var didChangeParams = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = newContent } }
        };

        var sendWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await client.SendNotificationAsync("textDocument/didChange", didChangeParams).ConfigureAwait(false);
            lock (_lspOpenLock) { _lspPendingEdits.Remove(filePath); _lspForceFullSync.Remove(filePath); _lspLastSyncUtc[filePath] = DateTime.UtcNow; }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP didChange failed for {uri}", ex);
            lock (_lspOpenLock) _lspForceFullSync.Add(filePath);
        }
        sendWatch.Stop();
        KodoDiagnostics.ReportSlowStage("LSP didChange send", sendWatch.ElapsedMilliseconds, 2000, $"len={newContent.Length}");
    }

    private async Task LspSyncDocumentAsync(string filePath, Func<string> textFactory, int textLength, List<LspPendingEdit>? edits)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        bool isOpen;
        lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
        if (!isOpen)
        {
            await LspNotifyDidOpenAsync(filePath, textFactory()).ConfigureAwait(false);
            return;
        }
        var prepared = await PrepareLspChangeAsync(filePath).ConfigureAwait(false);
        if (prepared is null) return;
        var (uri, version, client) = prepared.Value;

        if (edits is { Count: > 0 } && textLength > 80_000 && client.SupportsIncrementalSync() &&
            edits.Count <= LspIncrementalMaxEdits && edits.Sum(e => (long)(e.Text?.Length ?? 0)) <= LspIncrementalMaxChars)
        {
            var changes = edits.Select(e => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["range"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["start"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = e.StartLine, ["character"] = e.StartChar },
                    ["end"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["line"] = e.EndLine, ["character"] = e.EndChar }
                },
                ["text"] = e.Text
            }).ToArray();
            var incrementalParams = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri, ["version"] = version },
                ["contentChanges"] = changes
            };
            try
            {
                await client.SendNotificationAsync("textDocument/didChange", incrementalParams).ConfigureAwait(false);
                lock (_lspOpenLock) _lspLastSyncUtc[filePath] = DateTime.UtcNow;
                if (KodoDiagnostics.VerboseLoggingEnabled)
                    KodoDiagnostics.LogDebug($"LSP incremental didChange {uri} edits={edits.Count} ver={version}");
                return;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"LSP incremental didChange failed for {uri}, falling back to full text", ex);
                lock (_lspOpenLock) _lspForceFullSync.Add(filePath);
            }
        }

        var text = Dispatcher.UIThread.CheckAccess()
            ? textFactory()
            : await Dispatcher.UIThread.InvokeAsync(textFactory);
        var didChangeParams = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = text } }
        };

        var sendWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await client.SendNotificationAsync("textDocument/didChange", didChangeParams).ConfigureAwait(false);
            lock (_lspOpenLock) { _lspPendingEdits.Remove(filePath); _lspForceFullSync.Remove(filePath); _lspLastSyncUtc[filePath] = DateTime.UtcNow; }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP didChange failed for {uri}", ex);
            lock (_lspOpenLock) _lspForceFullSync.Add(filePath);
        }
        sendWatch.Stop();
        KodoDiagnostics.ReportSlowStage("LSP didChange send", sendWatch.ElapsedMilliseconds, 2000, $"len={text.Length}");
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
            _lspPendingEdits.Remove(filePath);
            _lspForceFullSync.Remove(filePath);
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
        LspClient? client = null;
        try
        {
            var cs = BuildLspResolverSettings();
            var res = await LspServerResolver.ResolveAsync(closeCfg, cs, lspExt.Id).ConfigureAwait(false);
            var effective = res.IsReady ? res.ResolvedConfiguration : closeCfg;
            client = _lspManager.TryGetClient(workspace, effective) ?? _lspManager.TryGetClient(workspace, closeCfg);
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
        client.OnExit += code =>
        {
            try { client.OnNotification -= HandleLspNotification; } catch { }
            lock (_lspDiagnosticsLock) _lspSubscribedClients.Remove(client);
            Dispatcher.UIThread.Post(() => { _ = UpdateErrorHighlightingAsync(); });
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
            lock (_lspDiagnosticsLock)
            {
                var normPath = NormalizeFilePath(filePath);
                if (!_lspOpenDocuments.Contains(normPath) && !_lspOpenDocuments.Contains(filePath) && !_lspPendingOpens.Contains(normPath) && !_lspPendingOpens.Contains(filePath)) return;
                _lspDiagnostics[normPath] = diagnostics;
                _lspRawDiagnostics[normPath] = rawDiagnostics;
                if (publishVersion.HasValue)
                    _lspDiagnosticVersions[normPath] = publishVersion.Value;
                else
                {
                    lock (_lspOpenLock)
                    {
                        var sent = _lspDocumentVersions.TryGetValue(normPath, out var sv0) ||
                                   _lspDocumentVersions.TryGetValue(filePath, out sv0);
                        if (sent) _lspDiagnosticVersions[normPath] = sv0;
                        else _lspDiagnosticVersions.Remove(normPath);
                    }
                }
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
            spans.Add(new ErrorSpan(start, len, d.Message.Trim(), d.Severity, d.Code ?? "", d.Source ?? "lsp"));
        }
        if (KodoDiagnostics.VerboseLoggingEnabled && spans.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans={spans.Count} first=[{spans[0].StartOffset}:{spans[0].Length}] {spans[0].Message}");
        else if (KodoDiagnostics.VerboseLoggingEnabled && raw.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans=0 (all filtered)");
        return spans;
    }

    private static int[] GetLspLineStarts(string text) => LspPath.GetLineStarts(text);

    private static int OffsetFromLspPosition(string text, int line, int character) =>
        LspPath.OffsetFromLspPosition(text, line, character);

    private static int OffsetFromLspPosition(int[] starts, string text, int line, int character) =>
        LspPath.OffsetFromLspPosition(starts, text, line, character);

    private static (int line, int character) OffsetToLspPosition(string text, int offset) =>
        LspPath.OffsetToLspPosition(text, offset);

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
        var client = _lspManager.TryGetClient(workspace, cfg) ?? _lspManager.TryGetClient(workspace, lspExt.Lsp!);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
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
        if (KodoDiagnostics.VerboseLoggingEnabled) KodoDiagnostics.LogDebug($"LSP completion response rawKind={rawKind} isList={isList} isArray={isArray} isIncomplete={isIncomplete} for {filePath} raw={result.Value.GetRawText().Substring(0, Math.Min(800, result.Value.GetRawText().Length))}");

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
            var filterText = item.TryGetProperty("filterText", out var filterEl) ? filterEl.GetString() ?? label : label;
            if (!filterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

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
                2 or 3 or 4 => InsightKind.Function,
                5 or 10 => InsightKind.Property,
                7 or 8 or 22 or 13 or 25 => InsightKind.Type,
                9 => InsightKind.Namespace,
                14 => InsightKind.Keyword,
                6 or 12 or 21 => InsightKind.Variable,
                _ => InsightKind.Variable
            };

            if (!seenSuggestionTexts.Add(insertText)) continue;
            suggestions.Add(new InsightSuggestion(insertText, kind));
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
        var hoverTask = GetLspHoverInnerAsync(filePath, offset, line, character, ct);
        lock (_lspHoverCacheLock)
        {
            _lspPendingHovers[hoverKey] = hoverTask;
            _lastHoverKey = hoverKey;
            _lastHoverTime = DateTime.UtcNow;
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
        var client = _lspManager.TryGetClient(workspace, cfg) ?? _lspManager.TryGetClient(workspace, lspExt.Lsp!);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized) return null;
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
        var client = _lspManager.TryGetClient(workspace, cfg) ?? _lspManager.TryGetClient(workspace, lspExt.Lsp!);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
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
        var firstLocation = locations[0];
        var firstUri = firstLocation.TryGetProperty("uri", out var firstU) ? firstU.GetString() : null;
        var firstRange = firstLocation.TryGetProperty("range", out var firstR) ? firstR : default;
        if (string.IsNullOrWhiteSpace(firstUri) || firstRange.ValueKind != JsonValueKind.Object) return false;
        var firstStart = firstRange.GetProperty("start");
        var firstPath = FileUriToPath(firstUri);
        if (!File.Exists(firstPath)) return false;
        var firstText = await File.ReadAllTextAsync(firstPath, ct).ConfigureAwait(false);
        var firstOffset = OffsetFromLspPosition(firstText, firstStart.GetProperty("line").GetInt32(), firstStart.GetProperty("character").GetInt32());
        var word = GetLanguageWordAtOffset(text, offset);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(firstPath).ConfigureAwait(false);
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
        var client = _lspManager.TryGetClient(workspace, cfg) ?? _lspManager.TryGetClient(workspace, lspExt.Lsp!);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
        if (client is null || !client.IsInitialized) return false;
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
            // Cheap O(1) staleness reject first; only materialise the whole
            // document for the exact comparison when lengths already agree.
            if (EditorTextBox.Document.TextLength != text.Length ||
                !string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal))
            {
                KodoDiagnostics.LogDebug($"LSP formatting rejected as stale for {filePath}");
                return;
            }
            if (!TryReadLspTextEdits(text, result.Value, out var parsedEdits, out var formatted))
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

    private async Task<bool> WaitForLspDiagnosticsVersionAsync(string normPath, int minVersion, TimeSpan timeout, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            lock (_lspDiagnosticsLock)
                if (_lspDiagnosticVersions.TryGetValue(normPath, out var v) && v >= minVersion) return true;
        }
        return false;
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
        if (client is null || !client.IsInitialized) return false;
        var (line, character) = OffsetToLspPosition(text, offset);
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await client.SendRequestAsync(method, parameters, timeout.Token).ConfigureAwait(false);
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
        var (line, character) = OffsetToLspPosition(text, offset);
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
        if (!File.Exists(path)) return false;
        var targetText = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var targetOffset = OffsetFromLspPosition(targetText, start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32());
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await OpenFileFromPathAsync(path).ConfigureAwait(false);
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

        result = text;
        for (var index = ordered.Length - 1; index >= 0; index--)
        {
            var item = ordered[index];
            result = result.Remove(item.Start, item.End - item.Start).Insert(item.Start, item.NewText ?? string.Empty);
        }
        return true;
    }

    private static bool TryOffsetFromLspPosition(string text, int line, int character, out int offset)
    {
        offset = 0;
        var starts = GetLspLineStarts(text);
        if (line < 0 || line >= starts.Length || character < 0)
            return false;
        var lineStart = starts[line];
        var lineEnd = line + 1 < starts.Length ? starts[line + 1] - 1 : text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            lineEnd--;
        var lineLength = lineEnd - lineStart;
        if (character > lineLength)
            return false;
        offset = lineStart + character;
        if (offset > lineStart && offset < lineEnd && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]))
            return false;
        return true;
    }

    private static bool TryReadLspTextEdits(string text, JsonElement editsElement, out List<(int Start, int End, string NewText)> edits, out string updated)
    {
        edits = new();
        updated = text;
        if (editsElement.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in editsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("newText", out var newTextElement) || newTextElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object ||
                !range.TryGetProperty("start", out var start) || !range.TryGetProperty("end", out var end) ||
                !start.TryGetProperty("line", out var startLine) || !start.TryGetProperty("character", out var startCharacter) ||
                !end.TryGetProperty("line", out var endLine) || !end.TryGetProperty("character", out var endCharacter) ||
                !TryOffsetFromLspPosition(text, startLine.GetInt32(), startCharacter.GetInt32(), out var startOffset) ||
                !TryOffsetFromLspPosition(text, endLine.GetInt32(), endCharacter.GetInt32(), out var endOffset) || endOffset < startOffset)
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
        var client = _lspManager.TryGetClient(workspace, cfg) ?? _lspManager.TryGetClient(workspace, lspExt.Lsp!);
        if (client is null || !client.IsInitialized)
        {
            foreach (var c in lspExt.AllLspConfigurations)
            {
                client = _lspManager.TryGetClient(workspace, c);
                if (client is not null && client.IsInitialized) break;
            }
        }
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
