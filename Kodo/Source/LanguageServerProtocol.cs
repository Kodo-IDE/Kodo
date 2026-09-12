// Licensed under GPL-v3.0
// CONSOLIDATED LSP - all LSP logic in one file (was 7 files in Source/Lsp/)
using Avalonia.Threading;
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

// --- LspClient.cs ---
/// <summary>Generic LSP client – speaks LSP, not Python/clangd/etc. One instance...
internal sealed class LspClient : IDisposable
{
    private readonly LspConfiguration _config;
    private readonly string _workspaceRoot;
    private Process? _process;
    private StreamWriter? _writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private Task? _stderrLoop;
    private int _nextId = 1;
    private readonly object _writeLock = new();
    private readonly StringBuilder _stderrBuffer = new();
    private bool _disposed;
    private bool _shutdownRequested;
    private string _shutdownReason = "";

    public bool IsStarted => _process is not null && !_process.HasExited;
    public bool IsInitialized { get; private set; }
    public string Id => _config.Command;

    public event Action<string, JsonElement?>? OnNotification;
    public event Action<JsonElement>? OnResponse;
    public event Action<string>? OnStderr;
    public event Action<int>? OnExit;

    public LspClient(LspConfiguration config, string workspaceRoot)
    {
        _config = config;
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? Environment.CurrentDirectory : workspaceRoot;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsStarted) return;

        var (fileName, useCmdWrapper, cmdArgs) = ResolveProcessStartInfo();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ResolveWorkingDirectory(),
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = Encoding.UTF8
        };

        if (useCmdWrapper)
        {
            // Windows .cmd/.bat must run via cmd.exe /c to preserve stdio redirection.
            // Generic: any .cmd/.bat LSP (Pyright, etc.) works without Python-specific code.
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cmdArgs);
            foreach (var arg in _config.Arguments)
                psi.ArgumentList.Add(ExpandPlaceholder(arg));
        }
        else
        {
            foreach (var arg in _config.Arguments)
                psi.ArgumentList.Add(ExpandPlaceholder(arg));
        }

        foreach (var kv in _config.Env)
            psi.Environment[kv.Key] = ExpandPlaceholder(kv.Value);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) =>
        {
            var code = -1;
            try { code = _process?.HasExited == true ? _process.ExitCode : -1; } catch { }
            var wasShutdown = _shutdownRequested;
            KodoDiagnostics.LogDebug($"LSP '{_config.Command}' exited with {code}. wasShutdownRequested={wasShutdown} reason={_shutdownReason} Stderr: {_stderrBuffer}");
            OnExit?.Invoke(code);
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IOException($"LSP server exited ({code})"));
            _pending.Clear();
        };

        try
        {
            if (!_process.Start())
                throw new InvalidOperationException($"Failed to start LSP '{_config.Command}'");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            // FileNotFound / Win32Exception -> wrap with useful message
            KodoDiagnostics.LogDebug($"LSP start failed for '{_config.Command}'", ex);
            throw new FileNotFoundException($"Language server '{_config.Command}' could not be started. Check that it is installed and available on PATH.", ex);
        }

        _writer = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: false) { AutoFlush = true };
        _readLoop = Task.Run(() => ReadLoopAsync(_process.StandardOutput.BaseStream, _cts.Token), _cts.Token);
        _stderrLoop = Task.Run(() => StderrLoopAsync(_process.StandardError, _cts.Token), _cts.Token);

        await Task.Yield();
        KodoDiagnostics.LogDebug($"LSP '{_config.Command}' started pid={_process.Id} args=[{string.Join(" ", _config.Arguments)}] cwd={psi.WorkingDirectory}");
    }

    public JsonElement? ServerCapabilities { get; private set; }
    private Task<JsonElement?>? _initTask;
    private readonly object _initLock = new();

    public Task<JsonElement?> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_initLock)
        {
            if (IsInitialized) return Task.FromResult<JsonElement?>(ServerCapabilities);
            if (_initTask is not null) return _initTask;
            _initTask = InitializeCoreAsync(cancellationToken);
            return _initTask;
        }
    }

    private async Task<JsonElement?> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsStarted) await StartAsync(cancellationToken).ConfigureAwait(false);
            if (IsInitialized) return ServerCapabilities;

            var rootUri = new Uri(_workspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? _workspaceRoot : _workspaceRoot + Path.DirectorySeparatorChar).AbsoluteUri;

            var initParams = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["rootPath"] = _workspaceRoot,
                ["workspaceFolders"] = new[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["uri"] = rootUri, ["name"] = Path.GetFileName(_workspaceRoot) } },
                ["capabilities"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["workspace"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["configuration"] = true,
                        ["workspaceFolders"] = true
                    },
                    ["textDocument"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["synchronization"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["didOpen"] = true, ["didChange"] = true, ["didClose"] = true, ["willSave"] = false },
                        ["completion"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["completionItem"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["snippetSupport"] = false, ["documentationFormat"] = new[] { "markdown", "plaintext" } } },
                        ["hover"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["contentFormat"] = new[] { "markdown", "plaintext" } },
                        ["definition"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["linkSupport"] = false },
                        ["publishDiagnostics"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["relatedInformation"] = true }
                    }
                },
                ["clientInfo"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "Kodo", ["version"] = KodoDiagnostics.AppVersion },
                ["initializationOptions"] = _config.InitializationOptions?.Clone() is JsonElement je ? JsonSerializer.Deserialize<object>(je.GetRawText()) : null,
                ["trace"] = "off"
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            JsonElement? result;
            try
            {
                result = await SendRequestAsync("initialize", initParams, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialize timed out after 10s");
                throw new TimeoutException($"Language server '{_config.Command}' did not respond to initialize within 10s");
            }

            // Store server capabilities if present
            if (result.HasValue && result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("capabilities", out var caps))
                ServerCapabilities = caps.Clone();

            await SendNotificationAsync("initialized", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
            KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialized. Server caps: {ServerCapabilities?.GetRawText() ?? "<none>"}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
                KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialization failed", ex);
            // Do not mark initialized; allow retry. Clear init task so next call retries.
            lock (_initLock) _initTask = null;
            if (ex is FileNotFoundException or TimeoutException or InvalidOperationException or IOException)
                throw;
            throw new InvalidOperationException($"LSP '{_config.Command}' initialization failed: {ex.Message}", ex);
        }
    }

    public Task<JsonElement?> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        if (_writer is null || _process?.HasExited != false)
            return Task.FromException<JsonElement?>(new IOException("LSP server not running"));

        var id = Interlocked.Increment(ref _nextId);
        var json = LspProtocol.CreateRequest(id, method, @params);
        var frame = LspProtocol.Frame(json);
        try
        {
            var preview = json.Length > 800 ? json.Substring(0, 800) + "..." : json;
            KodoDiagnostics.LogDebug($"LSP request id={id} method={method} json={preview}");
        }
        catch { }
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            lock (_writeLock)
            {
                _writer.Write(frame);
                _writer.Flush();
            }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetException(ex);
        }

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        return tcs.Task.ContinueWith(t =>
        {
            _pending.TryRemove(id, out _);
            if (t.IsFaulted) throw t.Exception!.InnerException!;
            if (t.IsCanceled) throw new OperationCanceledException(cancellationToken);
            return (JsonElement?)t.Result;
        }, TaskScheduler.Default);
    }

    public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        if (_writer is null || _process?.HasExited != false)
            return Task.FromException(new IOException("LSP server not running"));

        var json = LspProtocol.CreateNotification(method, @params);
        var frame = LspProtocol.Frame(json);
        try
        {
            lock (_writeLock)
            {
                _writer.Write(frame);
                _writer.Flush();
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP notification '{method}' failed", ex);
            return Task.FromException(ex);
        }
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(string reason = "unknown")
    {
        if (!IsStarted) return;
        _shutdownRequested = true;
        _shutdownReason = reason;
        var stack = Environment.StackTrace.Split('\n').Take(8).Select(s => s.Trim()).Where(s => s.Contains("Kodo.")).Take(3);
        KodoDiagnostics.LogDebug($"LSP shutdown requested reason={reason} command={_config.Command} pid={_process?.Id} stack={string.Join(" | ", stack)}");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendRequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
            await SendNotificationAsync("exit", null, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP shutdown failed reason={reason}", ex); }

        try
        {
            _cts.Cancel();
            if (_process is not null && !_process.HasExited)
            {
                if (!_process.WaitForExit(1000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }

    private async Task ReadLoopAsync(Stream stdout, CancellationToken ct)
    {
        var headerBuf = new List<string>(4);
        var headerBytes = new List<byte>(256);
        var singleByte = new byte[1];

        try
        {
            while (!ct.IsCancellationRequested && _process?.HasExited == false)
            {
                // Read headers until empty line (\r\n)
                headerBuf.Clear();
                headerBytes.Clear();
                var contentLength = -1;

                while (true)
                {
                    // Read one line terminated by \n (handle \r\n)
                    var lineBytes = new List<byte>(64);
                    while (true)
                    {
                        var read = await stdout.ReadAsync(singleByte, 0, 1, ct).ConfigureAwait(false);
                        if (read == 0) return; // EOF
                        var b = singleByte[0];
                        if (b == (byte)'\n')
                            break;
                        lineBytes.Add(b);
                    }
                    var line = Encoding.UTF8.GetString(lineBytes.ToArray()).TrimEnd('\r');
                    if (line.Length == 0) break; // empty line -> end headers
                    headerBuf.Add(line);
                    if (LspProtocol.TryParseContentLength(line, out var len)) contentLength = len;
                }

                if (contentLength < 0)
                {
                    KodoDiagnostics.LogDebug($"LSP missing Content-Length headers: {string.Join("|", headerBuf)}");
                    continue;
                }
                if (contentLength == 0) continue;
                if (contentLength > 8 * 1024 * 1024)
                {
                    KodoDiagnostics.LogDebug($"LSP message too large: {contentLength}");
                    // drain
                    var drain = new byte[contentLength];
                    var drainRead = 0;
                    while (drainRead < contentLength)
                    {
                        var r = await stdout.ReadAsync(drain, drainRead, contentLength - drainRead, ct).ConfigureAwait(false);
                        if (r == 0) return;
                        drainRead += r;
                    }
                    continue;
                }

                var body = new byte[contentLength];
                var offset = 0;
                while (offset < contentLength)
                {
                    var r = await stdout.ReadAsync(body, offset, contentLength - offset, ct).ConfigureAwait(false);
                    if (r == 0) return;
                    offset += r;
                }
                var json = Encoding.UTF8.GetString(body);
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP read loop failed", ex); }
    }

    private void HandleMessage(string json)
    {
        JsonElement? root;
        int? id = null;
        string? method = null;
        try
        {
            var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            if (root.Value.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
            {
                if (idEl.ValueKind == JsonValueKind.Number) id = idEl.GetInt32();
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var si)) id = si;
            }
            if (root.Value.TryGetProperty("method", out var mEl)) method = mEl.GetString();
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP invalid JSON: {json}", ex);
            return;
        }

        // Response (has id and no method)
        if (id.HasValue && method is null)
        {
            try
            {
                var preview = json.Length > 800 ? json.Substring(0, 800) + "..." : json;
                KodoDiagnostics.LogDebug($"LSP response id={id} json={preview}");
            }
            catch { }
            if (_pending.TryRemove(id.Value, out var tcs))
            {
                try
                {
                    if (root.Value.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                        tcs.TrySetException(new InvalidOperationException($"LSP error {err.GetRawText()}"));
                    else if (root.Value.TryGetProperty("result", out var res))
                        tcs.TrySetResult(res.Clone());
                    else
                        tcs.TrySetResult(default);
                    OnResponse?.Invoke(root.Value);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }
            return;
        }

        // Notification or server->client request
        if (method is not null)
        {
            // Server request (has id + method) – handle workspace/configuration generically
            if (id.HasValue)
            {
                object? result = null;
                if (method == "workspace/configuration")
                {
                    var reqText = root.Value.GetRawText();
                    KodoDiagnostics.LogDebug($"LSP workspace/configuration request: {reqText.Substring(0, Math.Min(500, reqText.Length))}");
                    result = HandleWorkspaceConfiguration(root.Value);
                    try { KodoDiagnostics.LogDebug($"LSP workspace/configuration response: {JsonSerializer.Serialize(result).Substring(0, Math.Min(500, JsonSerializer.Serialize(result).Length))}"); } catch { }
                }
                else if (method == "window/showMessage" || method == "window/logMessage")
                {
                    // Log and acknowledge
                    if (root.Value.TryGetProperty("params", out var p2))
                        KodoDiagnostics.LogDebug($"LSP {method}: {p2.GetRawText()}");
                }
                var resp = LspProtocol.Frame(LspProtocol.CreateResponse(id.Value, result));
                try { lock (_writeLock) { _writer?.Write(resp); _writer?.Flush(); } } catch { }
            }

            JsonElement? @params = null;
            if (root.Value.TryGetProperty("params", out var p)) @params = p.Clone();
            OnNotification?.Invoke(method, @params);
        }
    }

    private object? HandleWorkspaceConfiguration(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("params", out var parms) || !parms.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<object>();

            var results = new List<object?>();
            foreach (var item in items.EnumerateArray())
            {
                var section = item.TryGetProperty("section", out var sec) ? sec.GetString() : null;
                // Use init options for workspace/configuration
                if (!string.IsNullOrWhiteSpace(section) && _config.InitializationOptions is JsonElement initOpts && initOpts.ValueKind == JsonValueKind.Object)
                {
                    // Find section in init options
                    if (TryGetSection(initOpts, section, out var sectionValue))
                    {
                        // Deserialize to object for response
                        try { results.Add(JsonSerializer.Deserialize<object>(sectionValue.GetRawText())); continue; }
                        catch { }
                    }
                    // Also try without prefix (e.g., request for "python" when init has "python.analysis")
                    if (section == "python" && initOpts.TryGetProperty("python", out var py))
                    {
                        try { results.Add(JsonSerializer.Deserialize<object>(py.GetRawText())); continue; }
                        catch { }
                    }
                }
                // Generic: try to satisfy request from .kox initializationOptions; otherwise...
                // Do NOT invent generic defaults like typeCheckingMode: off – let the extension's config drive it
                if (!string.IsNullOrWhiteSpace(section) && _config.InitializationOptions is JsonElement initOptsCheck && initOptsCheck.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetSection(initOptsCheck, section, out var directValue))
                    {
                        try
                        {
                            var obj = JsonSerializer.Deserialize<object>(directValue.GetRawText());
                            results.Add(obj);
                            continue;
                        }
                        catch { }
                    }
                    // Handle "python" vs "python.analysis" nesting
                    if (section == "python" && initOptsCheck.TryGetProperty("python", out var py))
                    {
                        try { results.Add(JsonSerializer.Deserialize<object>(py.GetRawText())); continue; }
                        catch { }
                    }
                    if (section == "pyright" && initOptsCheck.TryGetProperty("pyright", out var pr))
                    {
                        try { results.Add(JsonSerializer.Deserialize<object>(pr.GetRawText())); continue; }
                        catch { }
                    }
                }
                // For python analysis, if .kox has python.analysis, return it; otherwise let...
                // Don't force typeCheckingMode: off globally – respect project config
                if (!string.IsNullOrWhiteSpace(section))
                {
                    var lower = section.ToLowerInvariant();
                    if (lower == "python" || lower == "python.analysis" || lower == "pyright")
                    {
                        // Check .kox for python.analysis specifically
                        if (_config.InitializationOptions is JsonElement init2 && init2.ValueKind == JsonValueKind.Object)
                        {
                            JsonElement analysisEl = default;
                            bool hasAnalysis = false;
                            if (init2.TryGetProperty("python", out var py2) && py2.ValueKind == JsonValueKind.Object && py2.TryGetProperty("analysis", out analysisEl))
                                hasAnalysis = true;
                            else if (TryGetSection(init2, section, out var secVal) && secVal.ValueKind == JsonValueKind.Object)
                            {
                                analysisEl = secVal;
                                hasAnalysis = true;
                            }
                            if (hasAnalysis)
                            {
                                try
                                {
                                    var obj = JsonSerializer.Deserialize<object>(analysisEl.GetRawText());
                                    // For "python" section, wrap as {analysis: obj} if needed
                                    if (section == "python" && lower == "python" && analysisEl.ValueKind == JsonValueKind.Object && !analysisEl.TryGetProperty("analysis", out _))
                                    {
                                        results.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["analysis"] = obj });
                                    }
                                    else
                                    {
                                        results.Add(obj);
                                    }
                                    continue;
                                }
                                catch { }
                            }
                        }
                    }
                }
                // For other sections or if no .kox config, return null to let server use its...
                results.Add(null);
            }
            return results.ToArray();
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"HandleWorkspaceConfiguration failed: {ex.Message}");
            return Array.Empty<object>();
        }
    }

    private static bool TryGetSection(JsonElement root, string section, out JsonElement value)
    {
        value = default;
        var parts = section.Split('.');
        var current = root;
        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
                return false;
            current = next;
        }
        value = current;
        return true;
    }

    private async Task StderrLoopAsync(StreamReader stderr, CancellationToken ct)
    {
        try
        {
            var buf = new char[1024];
            while (!ct.IsCancellationRequested && _process?.HasExited == false)
            {
                var read = await stderr.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (read == 0) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
                var text = new string(buf, 0, read);
                lock (_stderrBuffer) { _stderrBuffer.Append(text); if (_stderrBuffer.Length > 8192) _stderrBuffer.Remove(0, _stderrBuffer.Length - 8192); }
                OnStderr?.Invoke(text);
                KodoDiagnostics.LogDebug($"LSP stderr [{_config.Command}]: {text.Trim()}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP stderr loop failed", ex); }
    }

    private (string fileName, bool useCmdWrapper, string cmdArgs) ResolveProcessStartInfo()
    {
        var command = _config.Command.Trim().Trim('"');
        var isCmdScript = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            if (isCmdScript)
            {
                var cmdExe = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrWhiteSpace(cmdExe) || !File.Exists(cmdExe))
                    cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                if (!File.Exists(cmdExe))
                    cmdExe = "cmd.exe";
                return (cmdExe, true, command);
            }
            return (command, false, string.Empty);
        }
        // Non-Windows: strip .cmd/.bat for portability so a Windows-declared...
        if (isCmdScript)
            return (command[..^4], false, string.Empty);
        return (command, false, string.Empty);
    }

    private string ResolveWorkingDirectory()
    {
        var raw = _config.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(raw)) return _workspaceRoot;
        var expanded = ExpandPlaceholder(raw);
        return Directory.Exists(expanded) ? expanded : _workspaceRoot;
    }

    private string ExpandPlaceholder(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Replace("{workspace}", _workspaceRoot, StringComparison.OrdinalIgnoreCase)
                    .Replace("{workspaceFolder}", _workspaceRoot, StringComparison.OrdinalIgnoreCase)
                    .Replace("{root}", _workspaceRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _writer?.Dispose(); } catch { }
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            _process?.Dispose();
        }
        catch { }
        _cts.Dispose();
        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        _pending.Clear();
    }
}

// --- LspDocuments.cs ---
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
        // Fallback to top-level extensions (already copied into Lsp.FileExtensions if...
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

    private AppSettings BuildLspResolverSettings()
    {
        return new AppSettings
        {
            LspEnabled = _lspEnabled,
            LspAutoInstall = _lspAutoInstall,
            LspPreferManaged = _lspPreferManaged,
            LspPreferSystem = _lspPreferSystem,
            LspInstallDir = _lspInstallDir,
            LspExecutableOverrides = new Dictionary<string, string>(_lspExecutableOverrides, StringComparer.OrdinalIgnoreCase),
            LspDisabledLanguages = new Dictionary<string, bool>(_lspDisabledLanguages, StringComparer.OrdinalIgnoreCase),
            LspDismissedInstallPrompts = new HashSet<string>(_lspDismissedInstallPrompts, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task<bool> HandleLspNotReadyAsync(LoadedExtension lspExt, LspResolution resolution, string filePath)
    {
        // Respect dismissed prompts unless auto-install enabled
        if (resolution.Source == LspServerSource.Disabled)
        {
            KodoDiagnostics.LogDebug($"LSP disabled for {lspExt.Id}");
            return false;
        }
        if (resolution.Source == LspServerSource.RuntimeMissing)
        {
            var runtime = lspExt.Lsp?.Runtime ?? "required runtime";
            var msg = resolution.Error ?? $"Runtime '{runtime}' is required for {lspExt.Name}.";
            KodoDiagnostics.LogDebug($"LSP runtime missing for {lspExt.Id}: {msg}");
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ExtensionsStatusText = msg;
                await ShowWarningDialogAsync($"{lspExt.Name} – runtime required", new InvalidOperationException($"{msg}\n\nPlease install {runtime} and restart Kodo.\n\nHighlighting still works."));
            });
            return false;
        }
        if (resolution.Source == LspServerSource.Installable)
        {
            // Check if dismissed
            if (!_lspAutoInstall && _lspDismissedInstallPrompts.Contains(lspExt.Id))
            {
                KodoDiagnostics.LogDebug($"LSP install prompt dismissed for {lspExt.Id}");
                return false;
            }
            if (_lspAutoInstall)
            {
                // Auto-install without prompt
                var autoResult = await PromptAndInstallLspAsync(lspExt, resolution, autoInstall: true).ConfigureAwait(false);
                return autoResult;
            }
            // Offer install
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
        // Generic missing
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
            var providerName = lspExt.Lsp?.DisplayName ?? lspExt.Lsp?.EffectiveProviderId ?? lspExt.Name;
            var title = $"{lspExt.Name} – language server required";
            var body = autoInstall
                ? $"Installing {providerName} for {lspExt.Name}..."
                : $"{lspExt.Name} language support requires {providerName}.\n\nStatus: Not installed\n\nInstall {providerName} now?\n\nKodo will download it to %LocalAppData%\\Kodo\\Lsp\\{lspExt.Lsp?.EffectiveProviderId} and verify it before use. You can also use an existing system installation.";
            bool shouldInstall = autoInstall;
            if (!autoInstall)
            {
                // Use confirmation dialog with Install / Not Now
                shouldInstall = await ShowConfirmationDialogAsync(title, body, confirmLabel: $"Install {providerName}", isDestructive: false).ConfigureAwait(false);
                if (!shouldInstall)
                {
                    _lspDismissedInstallPrompts.Add(lspExt.Id);
                    SaveSettings(immediate: true);
                    return false;
                }
            }
            if (shouldInstall)
            {
                ExtensionsStatusText = $"Installing {providerName}...";
                var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() => ExtensionsStatusText = msg));
                var result = await LspInstallationManager.InstallAsync(lspExt.Lsp!, progress).ConfigureAwait(false);
                if (result.Kind == LspInstallationManager.InstallResultKind.Success)
                {
                    ExtensionsStatusText = $"{providerName} installed successfully.";
                    KodoDiagnostics.LogDebug($"LSP installed {providerName}: {result.InstalledPath}");
                    // Clear dismissed and missing flags so next open succeeds
                    _lspDismissedInstallPrompts.Remove(lspExt.Id);
                    _lspMissingNotified.Remove(lspExt.Id);
                    SaveSettings(immediate: true);
                    // Trigger retry by reopening current file if still same
                    if (!string.IsNullOrWhiteSpace(_currentFilePath) && IsSameDocument(_currentFilePath, _currentFilePath))
                    {
                        // Remove pending open so next didOpen can proceed
                        lock (_lspOpenLock) _lspPendingOpens.Remove(NormalizeFilePath(_currentFilePath));
                        if (EditorTextBox?.Document != null)
                            _ = LspNotifyDidOpenAsync(_currentFilePath, EditorTextBox.Document.Text);
                    }
                    return true;
                }
                else if (result.Kind == LspInstallationManager.InstallResultKind.Offline)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        ExtensionsStatusText = $"{providerName} could not be downloaded because Kodo is offline.";
                        await ShowWarningDialogAsync($"{providerName} – offline", new IOException(result.Message ?? "Offline"));
                    });
                }
                else if (result.Kind == LspInstallationManager.InstallResultKind.RuntimeMissing)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowWarningDialogAsync($"{providerName} – runtime missing", new InvalidOperationException(result.Message ?? "Runtime missing"));
                    });
                }
                else
                {
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
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt?.Lsp is null) return;
        lock (_lspOpenLock)
        {
            if (_lspOpenDocuments.Contains(filePath) || _lspPendingOpens.Contains(filePath)) return;
            _lspPendingOpens.Add(filePath);
        }

        // Centralized resolution: managed -> system -> installable
        var settings = BuildLspResolverSettings();
        var resolution = await LspServerResolver.ResolveAsync(lspExt, settings).ConfigureAwait(false);
        KodoDiagnostics.LogDebug($"LSP resolve {lspExt.Id} source={resolution.Source} exe={resolution.ExecutablePath} canInstall={resolution.CanInstall} err={resolution.Error}");
        if (!resolution.IsReady)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            await HandleLspNotReadyAsync(lspExt, resolution, filePath).ConfigureAwait(false);
            return;
        }

        var resolvedConfig = resolution.ResolvedConfiguration;
        var workspace = GetWorkspaceRootForFile(filePath);
        LspClient client;
        try
        {
            client = await _lspManager.GetOrStartAsync(workspace, resolvedConfig).ConfigureAwait(false);
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
        }
        catch (Exception ex)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            KodoDiagnostics.LogDebug($"LSP initialize failed for '{lspExt.Id}'", ex);
            await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = $"Language server '{resolvedConfig.Command}' initialization failed: {ex.Message}");
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
        // Resolve using centralized resolver to match didOpen's resolved path
        var settings2 = BuildLspResolverSettings();
        var res2 = await LspServerResolver.ResolveAsync(lspExt, settings2).ConfigureAwait(false);
        var effectiveConfig = res2.IsReady ? res2.ResolvedConfiguration : lspExt.Lsp;
        var client = _lspManager.TryGetClient(workspace, effectiveConfig);
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
        // Try resolved config first, then fallback to original for backward compat
        LspClient? client = null;
        try
        {
            var cs = BuildLspResolverSettings();
            var res = await LspServerResolver.ResolveAsync(lspExt, cs).ConfigureAwait(false);
            var effective = res.IsReady ? res.ResolvedConfiguration : lspExt.Lsp;
            client = _lspManager.TryGetClient(workspace, effective) ?? _lspManager.TryGetClient(workspace, lspExt.Lsp);
        }
        catch { client = _lspManager.TryGetClient(workspace, lspExt.Lsp); }
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
            // Invalidate Insight cache so next UpdateErrorHighlightingAsync merges fresh LSP...
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
                    // LocalPath on Windows is already "C:\..." but for "file:///c%3A/..." it can be...
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

// --- LspInstallationManager.cs ---
/// <summary>Manages Kodo-managed LSP installations under %LocalAppData%\Kodo\Lsp\...
/// Hardened: HTTPS-only, mandatory SHA-256 for github artifacts, atomic temp-dir install,
/// ZipSlip guard, TAR support, per-provider concurrency lock, path-traversal safe.</summary>
internal static class LspInstallationManager
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Kodo/2.0.0-DEV (https://github.com/Kodo-IDE/Kodo)");
        return c;
    }

    // --- Managed root handling (respects custom LspInstallDir setting if provided) ---
    public static string ManagedRoot => GetManagedRoot(null);

    public static string GetManagedRoot(AppSettings? settings)
    {
        var custom = settings?.LspInstallDir?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                // Must be absolute and inside user's profile or local app data; reject traversal
                var full = Path.GetFullPath(custom);
                // Allow any absolute path that is not system root, but ensure it's under...
                // For now allow any absolute path that exists or can be created, but sanitize...
                if (Path.IsPathRooted(full)) return full;
            }
            catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "Lsp");
    }

    public static string GetManagedRoot() => ManagedRoot;

    private static string SanitizeProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("ProviderId required");
        // Allow only alphanum, dash, underscore, dot
        var sanitized = new string(providerId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "lsp";
        // Prevent .. traversal
        sanitized = sanitized.Replace("..", "_");
        return sanitized.ToLowerInvariant();
    }

    public static string GetProviderDir(LspConfiguration cfg) => GetProviderDir(cfg, null);
    public static string GetProviderDir(LspConfiguration cfg, AppSettings? settings)
    {
        var root = GetManagedRoot(settings);
        var sanitized = SanitizeProviderId(cfg.EffectiveProviderId);
        var dir = Path.Combine(root, sanitized);
        // Ensure dir is inside root
        var fullRoot = Path.GetFullPath(root);
        var fullDir = Path.GetFullPath(dir);
        if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Provider directory escapes managed root: {fullDir}");
        return fullDir;
    }

    public static string GetManagedExecutablePath(LspConfiguration cfg) => GetManagedExecutablePath(cfg, null);
    public static string GetManagedExecutablePath(LspConfiguration cfg, AppSettings? settings)
    {
        var dir = GetProviderDir(cfg, settings);
        var cmd = cfg.Command.Trim().Trim('"');
        var fileName = Path.GetFileName(cmd);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = SanitizeProviderId(cfg.EffectiveProviderId);
        // Prevent path traversal in fileName
        fileName = Path.GetFileName(fileName);
        return Path.Combine(dir, fileName);
    }

    /// <summary>True only if the expected executable exists and is not stale. Does NOT...
    public static bool IsManagedInstalled(LspConfiguration cfg) => IsManagedInstalled(cfg, null);
    public static bool IsManagedInstalled(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = FindManagedExecutable(cfg, settings);
        return exe != null && File.Exists(exe);
    }

    public static string? FindSystemExecutable(LspConfiguration cfg)
    {
        if (!cfg.AllowSystem) return null;
        return LspRuntimeDetector.FindOnPath(cfg.Command);
    }

    public static string? FindManagedExecutable(LspConfiguration cfg) => FindManagedExecutable(cfg, null);
    public static string? FindManagedExecutable(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = GetManagedExecutablePath(cfg, settings);
        if (File.Exists(exe)) return exe;
        // For npm providers, executable is in <providerDir>/node_modules/.bin/<command>...
        var dir = GetProviderDir(cfg, settings);
        if (!Directory.Exists(dir)) return null;
        // Strict: look for file matching Command fileName exactly, recursively, but only...
        try
        {
            var targetName = Path.GetFileName(exe);
            var targetWithoutExt = Path.GetFileNameWithoutExtension(targetName);
            // Search up to 3 levels deep, prefer .bin
            var candidates = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => {
                    var name = Path.GetFileName(f);
                    // Exact match or without extension match
                    return name.Equals(targetName, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(name).Equals(targetWithoutExt, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            // Prefer node_modules/.bin location
            var binPref = candidates.FirstOrDefault(c => c.Contains("node_modules" + Path.DirectorySeparatorChar + ".bin", StringComparison.OrdinalIgnoreCase));
            if (binPref != null) return binPref;
            return candidates.FirstOrDefault();
        }
        catch { return null; }
    }

    public static async Task<(bool ok, string? version, string? error)> TryGetVersionAsync(string exePath, string[] versionArgs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return (false, null, "Executable not found");
        var args = versionArgs != null && versionArgs.Length > 0 ? string.Join(" ", versionArgs) : "--version";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, "Failed to start");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var outText = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(outText))
                return (false, null, $"Exit code {proc.ExitCode}: {stderr.Trim()}");
            return (true, outText?.Trim(), null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public enum InstallResultKind { Success, AlreadyInstalled, Failed, Cancelled, Offline, RuntimeMissing, NotInstallable }

    public sealed record InstallResult(InstallResultKind Kind, string? Message, string? InstalledPath);

    public static bool IsOffline(Exception ex)
    {
        return ex is HttpRequestException || ex is TaskCanceledException || ex is IOException && ex.Message.Contains("offline", StringComparison.OrdinalIgnoreCase);
    }

    private static SemaphoreSlim GetLock(string providerId) => Locks.GetOrAdd(SanitizeProviderId(providerId), _ => new SemaphoreSlim(1, 1));

    public static async Task<InstallResult> InstallAsync(LspConfiguration cfg, IProgress<string>? progress, CancellationToken ct = default)
        => await InstallAsync(cfg, null, progress, ct).ConfigureAwait(false);

    public static async Task<InstallResult> InstallAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (cfg == null) return new(InstallResultKind.Failed, "Provider not configured", null);
        if (cfg.InstallMethod != null && cfg.InstallMethod.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' requires manual installation.", null);
        if (!cfg.AllowAutoInstall)
            return new(InstallResultKind.NotInstallable, $"Automatic installation disabled for '{cfg.EffectiveProviderId}'.", null);

        // Runtime check first
        if (!string.IsNullOrWhiteSpace(cfg.Runtime))
        {
            var rt = await LspRuntimeDetector.DetectAsync(cfg.Runtime!, cfg.RuntimeMinVersion, ct).ConfigureAwait(false);
            if (!rt.Found || !string.IsNullOrWhiteSpace(rt.Error))
                return new(InstallResultKind.RuntimeMissing, rt.Error ?? $"Runtime '{cfg.Runtime}' required.", null);
        }

        var providerId = SanitizeProviderId(cfg.EffectiveProviderId);
        var sem = GetLock(providerId);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock: maybe another thread installed while we waited
            if (IsManagedInstalled(cfg, settings))
                return new(InstallResultKind.AlreadyInstalled, "Already installed", GetProviderDir(cfg, settings));

            var method = (cfg.InstallMethod ?? "").Trim().ToLowerInvariant();
            if (method == "npm" || (!string.IsNullOrWhiteSpace(cfg.PackageName) && string.IsNullOrWhiteSpace(cfg.DownloadUrl)))
                return await InstallViaNpmAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.DownloadUrl))
            {
                // Enforce SHA-256 for github/standalone artifacts
                var isGithub = method == "github" || cfg.DownloadUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) || cfg.DownloadUrl.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
                if (isGithub && string.IsNullOrWhiteSpace(cfg.Sha256))
                    return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' download requires SHA-256 verification. No checksum provided – please install manually from {cfg.DownloadUrl} and configure an override, or update the provider to include a trusted checksum.", null);
                if (string.IsNullOrWhiteSpace(cfg.Sha256) && method != "npm")
                {
                    // For non-npm standalone, also require checksum
                    return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' cannot be auto-installed without a SHA-256 checksum. This is a security requirement. Install manually or provide a checksum.", null);
                }
                return await InstallViaDownloadAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            }

            return new(InstallResultKind.NotInstallable, $"No install source configured for '{cfg.EffectiveProviderId}'. Install manually and ensure '{cfg.Command}' is on PATH.", null);
        }
        finally { sem.Release(); }
    }

    private static async Task<InstallResult> InstallViaNpmAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var pkg = cfg.PackageName ?? cfg.EffectiveProviderId;
        // Detect node + npm
        var nodeRt = await LspRuntimeDetector.DetectAsync("node", cfg.RuntimeMinVersion ?? "16.0.0", ct).ConfigureAwait(false);
        if (!nodeRt.Found) return new(InstallResultKind.RuntimeMissing, nodeRt.Error ?? "Node.js is required. Install Node.js 16+ from https://nodejs.org/", null);
        var npmRt = await LspRuntimeDetector.DetectAsync("npm", null, ct).ConfigureAwait(false);
        if (!npmRt.Found) return new(InstallResultKind.RuntimeMissing, "npm is required but not found. Install Node.js which includes npm.", null);

        progress?.Report($"Installing {pkg} via npm...");
        KodoDiagnostics.LogDebug($"LSP npm install {pkg}");
        var providerDir = GetProviderDir(cfg, settings);
        var stagingDir = providerDir + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(stagingDir);
            var args = $"install --prefix \"{stagingDir}\" {pkg}";
            var npmExe = LspRuntimeDetector.FindOnPath("npm") ?? "npm";
            // On Windows npm is npm.cmd – must invoke via cmd.exe wrapper handling already in...
            // Use npm.cmd if found
            if (File.Exists(npmExe) && npmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                // keep as is; Process will handle via cmd? We need to invoke via cmd.exe /c for .cmd
                // Instead, use npx or node? Simpler: if npm is .cmd, invoke via cmd.exe
                // We'll handle by switching to use cmd.exe
            }
            var useCmdWrapper = npmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || npmExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            string fileName = npmExe;
            string cmdArgs = args;
            if (useCmdWrapper)
            {
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                fileName = comSpec;
                cmdArgs = $"/c \"{npmExe}\" {args}";
            }
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = useCmdWrapper ? cmdArgs : args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return new(InstallResultKind.Failed, "Failed to start npm", null);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"npm install stdout: {stdout}\nstderr: {stderr}");
            if (proc.ExitCode != 0)
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"npm install failed (exit {proc.ExitCode}): {stderr.Trim()}", null);
            }
            // Validate staging contains executable
            var stagedExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(pkg, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals(cfg.Command, StringComparison.OrdinalIgnoreCase));
            // For vscode-langservers-extracted, we expect multiple servers; check at least...
            if (stagedExe == null && !Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "npm install produced no files", null);
            }
            // Atomic move staging -> providerDir
            try
            {
                if (Directory.Exists(providerDir)) Directory.Delete(providerDir, true);
            }
            catch { }
            Directory.Move(stagingDir, providerDir);
            progress?.Report($"Installed {pkg}");
            return new(InstallResultKind.Success, $"Installed {pkg} via npm", providerDir);
        }
        catch (Exception ex) when (IsOffline(ex))
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            return new(InstallResultKind.Offline, $"Offline – could not download {pkg}: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            KodoDiagnostics.LogDebug($"LSP npm install failed for {pkg}", ex);
            return new(InstallResultKind.Failed, $"Installation failed: {ex.Message}", null);
        }
    }

    private static async Task<InstallResult> InstallViaDownloadAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var url = cfg.DownloadUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return new(InstallResultKind.NotInstallable, "No download URL", null);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new(InstallResultKind.Failed, "Download URL must be HTTPS", null);

        progress?.Report($"Downloading {cfg.EffectiveProviderId}...");
        KodoDiagnostics.LogDebug($"LSP download {url}");
        var providerDir = GetProviderDir(cfg, settings);
        var stagingDir = providerDir + ".staging-" + Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(Path.GetTempPath(), $"kodo-lsp-{SanitizeProviderId(cfg.EffectiveProviderId)}-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDir);
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Verify final request URI is still HTTPS (followed redirects)
            if (resp.RequestMessage?.RequestUri != null && !resp.RequestMessage.RequestUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return new(InstallResultKind.Failed, "Download redirected to non-HTTPS URL – blocked for security", null);
            if (!resp.IsSuccessStatusCode)
                return new(InstallResultKind.Failed, $"Download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", null);
            await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            IncrementalHash? hasher = null;
            if (!string.IsNullOrWhiteSpace(cfg.Sha256))
                hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            else
                return new(InstallResultKind.NotInstallable, "SHA-256 checksum required for verification – not provided", null);
            var buffer = new byte[81920];
            long totalRead = 0;
            var contentLength = resp.Content.Headers.ContentLength;
            int read;
            while ((read = await netStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                hasher?.AppendData(buffer, 0, read);
                totalRead += read;
                if (contentLength.HasValue && contentLength.Value > 0)
                    progress?.Report($"Downloading {cfg.EffectiveProviderId}... {totalRead * 100 / contentLength.Value}%");
            }
            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            fileStream.Close();

            // Verify checksum (mandatory)
            var hash = hasher!.GetHashAndReset();
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var expected = cfg.Sha256!.Trim().ToLowerInvariant().Replace(" ", "").Replace("0x", "");
            if (!hex.Equals(expected, StringComparison.Ordinal))
            {
                try { File.Delete(tempFile); } catch { }
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Checksum mismatch for {cfg.EffectiveProviderId}. Expected {expected}, got {hex}.", null);
            }

            progress?.Report($"Installing {cfg.EffectiveProviderId}...");
            bool isZip = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || IsZipFile(tempFile);
            bool isTarGz = url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || IsTarGzFile(tempFile);
            if (isZip)
            {
                await ExtractZipSecureAsync(tempFile, stagingDir, ct).ConfigureAwait(false);
            }
            else if (isTarGz)
            {
                await ExtractTarGzSecureAsync(tempFile, stagingDir, ct).ConfigureAwait(false);
            }
            else
            {
                // Single binary – copy into staging dir
                var fileName = Path.GetFileName(GetManagedExecutablePath(cfg, settings));
                var dest = Path.Combine(stagingDir, fileName);
                File.Copy(tempFile, dest, true);
            }
            // Validate staging contains at least one file
            if (!Directory.EnumerateFileSystemEntries(stagingDir).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "Archive extracted no files", null);
            }
            // Additional validation: ensure expected executable exists (or at least one exe in staging)
            var foundExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(GetManagedExecutablePath(cfg, settings)), StringComparison.OrdinalIgnoreCase));
            if (foundExe == null)
            {
                // For zip that contains nested dir, and command not at top level, we accept any executable
                if (!Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Any())
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                    return new(InstallResultKind.Failed, "Extracted archive contains no executable", null);
                }
            }
            try { File.Delete(tempFile); } catch { }
            // Atomic move: delete old providerDir and move staging
            try
            {
                if (Directory.Exists(providerDir)) Directory.Delete(providerDir, true);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to clean old installation: {ex.Message}", null);
            }
            try
            {
                Directory.Move(stagingDir, providerDir);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to finalize installation: {ex.Message}", null);
            }
            progress?.Report($"Installed {cfg.EffectiveProviderId}");
            KodoDiagnostics.LogDebug($"LSP installed {cfg.EffectiveProviderId} to {providerDir}");
            return new(InstallResultKind.Success, $"Installed {cfg.EffectiveProviderId}", providerDir);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            return new(InstallResultKind.Cancelled, "Installation cancelled", null);
        }
        catch (Exception ex) when (IsOffline(ex))
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            return new(InstallResultKind.Offline, $"Offline – could not download {cfg.EffectiveProviderId}: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            KodoDiagnostics.LogDebug($"LSP download/install failed for {cfg.EffectiveProviderId}", ex);
            return new(InstallResultKind.Failed, $"Installation failed: {ex.Message}", null);
        }
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return false;
            return header[0] == 0x50 && header[1] == 0x4B; // PK
        }
        catch { return false; }
    }

    private static bool IsTarGzFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = new byte[2];
            if (fs.Read(header, 0, 2) < 2) return false;
            return header[0] == 0x1F && header[1] == 0x8B; // gzip magic
        }
        catch { return false; }
    }

    private static async Task ExtractZipSecureAsync(string zipPath, string destDir, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue; // directory
            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            var fullDestDir = Path.GetFullPath(destDir);
            if (!destPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry escapes destination: {entry.FullName}");
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Prevent overwriting with directory
            if (entry.FullName.EndsWith("/")) continue;
            await Task.Run(() => entry.ExtractToFile(destPath, overwrite: true), ct).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarGzSecureAsync(string tgzPath, string destDir, CancellationToken ct)
    {
        // Use System.Formats.Tar if available (.NET 7+); fallback to manual error if not
        await Task.Run(() =>
        {
            using var fs = File.OpenRead(tgzPath);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            // Try System.Formats.Tar
            var tarType = Type.GetType("System.Formats.Tar.TarReader, System.Formats.Tar");
            if (tarType != null)
            {
                // Use reflection to avoid compile-time dependency if not available
                dynamic reader = Activator.CreateInstance(tarType, gz)!;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = reader.GetNextEntry();
                        if (entry == null) break;
                        string entryName = entry.Name;
                        if (string.IsNullOrWhiteSpace(entryName)) continue;
                        var destPath = Path.GetFullPath(Path.Combine(destDir, entryName));
                        var fullDestDir = Path.GetFullPath(destDir);
                        if (!destPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Tar entry escapes destination: {entryName}");
                        if (entry.EntryType.ToString() == "Directory")
                        {
                            Directory.CreateDirectory(destPath);
                        }
                        else
                        {
                            var dir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            using var outFs = File.Create(destPath);
                            entry.DataStream.CopyTo(outFs);
                        }
                    }
                }
                finally { (reader as IDisposable)?.Dispose(); }
                return;
            }
            throw new InvalidOperationException("TAR extraction not supported on this runtime – archive is .tar.gz but System.Formats.Tar not available. Please install manually.");
        }, ct).ConfigureAwait(false);
    }

    private static bool HasInternetConnection()
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; }
    }

    public static InstallResult Uninstall(LspConfiguration cfg) => Uninstall(cfg, null);
    public static InstallResult Uninstall(LspConfiguration cfg, AppSettings? settings)
    {
        try
        {
            var dir = GetProviderDir(cfg, settings);
            var fullRoot = Path.GetFullPath(GetManagedRoot(settings));
            var fullDir = Path.GetFullPath(dir);
            if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || fullDir.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                return new(InstallResultKind.Failed, "Uninstall blocked: invalid provider directory", null);
            if (!Directory.Exists(dir)) return new(InstallResultKind.Failed, "Not installed", null);
            Directory.Delete(dir, true);
            return new(InstallResultKind.Success, "Uninstalled", null);
        }
        catch (Exception ex) { return new(InstallResultKind.Failed, ex.Message, null); }
    }

    public static async Task<InstallResult> UpdateAsync(LspConfiguration cfg, IProgress<string>? progress, CancellationToken ct = default)
        => await UpdateAsync(cfg, null, progress, ct).ConfigureAwait(false);

    public static async Task<InstallResult> UpdateAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct = default)
    {
        var uninstall = Uninstall(cfg, settings);
        // ignore uninstall failure if not installed
        return await InstallAsync(cfg, settings, progress, ct).ConfigureAwait(false);
    }
}

// --- LspManager.cs ---
/// <summary>Manages <see cref="LspClient"/> lifetimes – one per (workspace, command).
/// Generic, no language-specific logic.</summary>
internal sealed class LspManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LspClient? TryGetClient(string workspaceRoot, LspConfiguration config)
    {
        var key = MakeKey(workspaceRoot, config);
        lock (_lock) return _clients.TryGetValue(key, out var c) ? c : null;
    }

    public async Task<LspClient> GetOrStartAsync(string workspaceRoot, LspConfiguration config, CancellationToken ct = default)
    {
        var key = MakeKey(workspaceRoot, config);
        LspClient? client;
        bool needsStart = false;

        lock (_lock)
        {
            if (_clients.TryGetValue(key, out client) && client.IsStarted)
                return client;

            // stale entry
            if (client is not null)
            {
                _clients.Remove(key);
                try { client.Dispose(); } catch { }
            }

            client = new LspClient(config, workspaceRoot);
            _clients[key] = client;
            needsStart = true;
        }

        if (needsStart)
        {
            client.OnExit += code =>
            {
                lock (_lock) _clients.Remove(key);
                KodoDiagnostics.LogDebug($"LSP manager removed exited client '{config.Command}' ws={workspaceRoot} code={code}");
            };

            try
            {
                await client.StartAsync(ct).ConfigureAwait(false);
                // initialize is caller's responsibility (Phase 4) – but we can lazy-initialize here if needed
            }
            catch (Exception ex)
            {
                lock (_lock) _clients.Remove(key);
                try { client.Dispose(); } catch { }
                // Surface useful message without crashing – caller shows dialog
                KodoDiagnostics.LogDebug($"LSP manager failed to start '{config.Command}'", ex);
                throw;
            }
        }

        return client;
    }

    public async Task ShutdownAsync(string workspaceRoot, LspConfiguration config, string reason = "manager shutdown")
    {
        var key = MakeKey(workspaceRoot, config);
        LspClient? client;
        lock (_lock) _clients.TryGetValue(key, out client);
        if (client is null) return;
        await client.ShutdownAsync(reason).ConfigureAwait(false);
        lock (_lock) _clients.Remove(key);
        client.Dispose();
    }

    public async Task ShutdownAllAsync(string reason = "manager shutdown all")
    {
        List<LspClient> snapshot;
        lock (_lock)
        {
            snapshot = new List<LspClient>(_clients.Values);
            _clients.Clear();
        }
        foreach (var c in snapshot)
        {
            try { await c.ShutdownAsync(reason).ConfigureAwait(false); } catch { }
            try { c.Dispose(); } catch { }
        }
    }

    public IReadOnlyCollection<LspClient> AllClients
    {
        get { lock (_lock) return new List<LspClient>(_clients.Values).AsReadOnly(); }
    }

    private static string MakeKey(string workspaceRoot, LspConfiguration config)
    {
        var root = Path.GetFullPath(workspaceRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"{root}|{config.Command}|{string.Join(" ", config.Arguments)}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ShutdownAllAsync("manager dispose").GetAwaiter().GetResult(); } catch { }
    }
}

// --- LspProtocol.cs ---
/// <summary>Generic JSON-RPC + LSP Content-Length framing. No language-specific logic.</summary>
internal static class LspProtocol
{
    public const string JsonRpcVersion = "2.0";

    public static string CreateRequest(int id, string method, object? @params)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = id,
            ["method"] = method
        };
        if (@params is not null)
            payload["params"] = @params;
        return JsonSerializer.Serialize(payload);
    }

    public static string CreateNotification(string method, object? @params)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["method"] = method
        };
        if (@params is not null)
            payload["params"] = @params;
        return JsonSerializer.Serialize(payload);
    }

    public static string CreateResponse(int id, object? result)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = id,
            ["result"] = result
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string Frame(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        return $"Content-Length: {bytes}\r\n\r\n{json}";
    }

    public static bool TryParseContentLength(string headerLine, out int length)
    {
        length = 0;
        var idx = headerLine.IndexOf(':', StringComparison.Ordinal);
        if (idx < 0) return false;
        var name = headerLine[..idx].Trim();
        if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(headerLine[(idx + 1)..].Trim(), out length);
    }

    public static JsonElement? ParseMessage(string json, out int? id, out string? method)
    {
        id = null;
        method = null;
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
            {
                if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var i)) id = i;
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var si)) id = si;
            }
            if (root.TryGetProperty("method", out var mEl)) method = mEl.GetString();
            return root.Clone();
        }
        catch { return null; }
    }
}

// --- LspRuntimeDetector.cs ---
/// <summary>Detects external runtimes required by LSPs (Node, Java, dotnet, PowerShell).
/// Lightweight – only probes version, does not install runtimes.</summary>
internal static class LspRuntimeDetector
{
    public sealed record RuntimeInfo(bool Found, string? Version, string? RawOutput, string? Error);

    public static async Task<RuntimeInfo> DetectAsync(string runtime, string? minVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return new(true, null, null, null);
        runtime = runtime.Trim().ToLowerInvariant();
        string exe;
        string args;
        switch (runtime)
        {
            case "node": exe = "node"; args = "--version"; break;
            case "npm": exe = "npm"; args = "--version"; break;
            case "java": exe = "java"; args = "--version"; break;
            case "dotnet": exe = "dotnet"; args = "--version"; break;
            case "powershell":
            case "pwsh": exe = "pwsh"; args = "--version"; break;
            case "python": exe = "python"; args = "--version"; break;
            default: exe = runtime; args = "--version"; break;
        }
        var (found, output, error) = await TryRunAsync(exe, args, ct).ConfigureAwait(false);
        if (!found) return new(false, null, output, error ?? $"Runtime '{runtime}' not found on PATH. Install {runtime} to use this language server.");
        var version = ExtractVersion(output ?? "");
        if (!string.IsNullOrWhiteSpace(minVersion) && !string.IsNullOrWhiteSpace(version))
        {
            if (!IsVersionAtLeast(version!, minVersion!))
                return new(true, version, output, $"Runtime '{runtime}' version {version} is below required {minVersion}. Please update {runtime}.");
        }
        return new(true, version, output, null);
    }

    public static bool IsVersionAtLeast(string found, string required)
    {
        try
        {
            var f = ParseVersion(found);
            var r = ParseVersion(required);
            var len = Math.Max(f.Length, r.Length);
            for (int i = 0; i < len; i++)
            {
                var fv = i < f.Length ? f[i] : 0;
                var rv = i < r.Length ? r[i] : 0;
                if (fv > rv) return true;
                if (fv < rv) return false;
            }
            return true;
        }
        catch { return true; }
    }

    private static int[] ParseVersion(string v)
    {
        // strip leading 'v' and trailing non-numeric
        v = v.Trim().TrimStart('v', 'V');
        var parts = new System.Collections.Generic.List<int>();
        var cur = "";
        foreach (var c in v)
        {
            if (char.IsDigit(c)) cur += c;
            else if (c == '.' && cur.Length > 0) { parts.Add(int.Parse(cur)); cur = ""; }
            else if (cur.Length > 0) break;
        }
        if (cur.Length > 0 && int.TryParse(cur, out var last)) parts.Add(last);
        if (parts.Count == 0) return new[] { 0 };
        return parts.ToArray();
    }

    private static string? ExtractVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        // find first version-like token
        var m = System.Text.RegularExpressions.Regex.Match(output, @"v?(\d+\.\d+(\.\d+)?)");
        return m.Success ? m.Groups[1].Value : output.Trim().Split(' ')[0].TrimStart('v');
    }

    private static async Task<(bool found, string? output, string? error)> TryRunAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, $"Failed to start {exe}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            // Even if exit code non-zero, we got output – treat as found
            if (proc.ExitCode == 0 || !string.IsNullOrWhiteSpace(combined))
                return (true, combined.Trim(), null);
            return (false, combined, $"Exit code {proc.ExitCode}");
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is System.ComponentModel.Win32Exception)
        {
            return (false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static string? FindOnPath(string command)
    {
        try
        {
            var fileName = command.Trim().Trim('"');
            if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return Path.GetFullPath(fileName);
            // Try with extensions on Windows
            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var trimmed = dir.Trim().Trim('"');
                if (!Directory.Exists(trimmed)) continue;
                // direct
                var candidate = Path.Combine(trimmed, fileName);
                if (File.Exists(candidate)) return candidate;
                // try with pathext
                if (Path.HasExtension(fileName)) continue;
                foreach (var ext in pathext.Split(';'))
                {
                    var withExt = candidate + ext;
                    if (File.Exists(withExt)) return withExt;
                }
            }
            return null;
        }
        catch { return null; }
    }
}

// --- LspServerResolver.cs ---
public enum LspServerSource
{
    Managed,
    System,
    UserOverride,
    Installable,
    RuntimeMissing,
    ManualRequired,
    Missing,
    Disabled
}

public sealed record LspResolution(
    LspServerSource Source,
    string? ExecutablePath,
    LspConfiguration ResolvedConfiguration,
    string? Version,
    string? Error,
    bool CanInstall)
{
    public bool IsReady => Source == LspServerSource.Managed || Source == LspServerSource.System || Source == LspServerSource.UserOverride;
}

internal static class LspServerResolver
{
    /// <summary>Resolves the best available executable for a provider, in order:
    /// 1. User override (AppSettings)
    /// 2. Managed install
    /// 3. System PATH
    /// 4. Installable / manual / missing
    /// Also checks runtime requirements.
    /// </summary>
    public static async Task<LspResolution> ResolveAsync(
        LoadedExtension extension,
        AppSettings? settings = null,
        CancellationToken ct = default)
    {
        var cfg = extension.Lsp;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Command))
            return new(LspServerSource.Missing, null, cfg ?? new LspConfiguration(), null, "No LSP configured", false);
        // Per-language disabled?
        if (settings != null && settings.LspDisabledLanguages.TryGetValue(extension.Id, out var disabled) && disabled)
            return new(LspServerSource.Disabled, null, cfg, null, "LSP disabled for this language", false);
        if (settings != null && !settings.LspEnabled)
            return new(LspServerSource.Disabled, null, cfg, null, "LSP globally disabled", false);

        // Runtime check first – if missing, report before path checks
        if (!string.IsNullOrWhiteSpace(cfg.Runtime))
        {
            var rt = await LspRuntimeDetector.DetectAsync(cfg.Runtime!, cfg.RuntimeMinVersion, ct).ConfigureAwait(false);
            if (!rt.Found || !string.IsNullOrWhiteSpace(rt.Error))
            {
                return new(LspServerSource.RuntimeMissing, null, cfg, null, rt.Error, false);
            }
        }

        // 1. User override
        if (settings != null && settings.LspExecutableOverrides.TryGetValue(extension.Id, out var ov) && !string.IsNullOrWhiteSpace(ov))
        {
            var trimmed = ov.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                var resolved = CloneWithCommand(cfg, trimmed);
                var ver = await ProbeVersionAsync(trimmed, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.UserOverride, trimmed, resolved, ver, null, false);
            }
            // also try FindOnPath for override value
            var found = LspRuntimeDetector.FindOnPath(trimmed);
            if (found != null)
            {
                var resolved = CloneWithCommand(cfg, found);
                var ver = await ProbeVersionAsync(found, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.UserOverride, found, resolved, ver, null, false);
            }
            // override points to non-existent – treat as error but fallback to other sources
            KodoDiagnostics.LogDebug($"LSP user override for {extension.Id} not found: {trimmed}");
        }

        // 2 & 3: Deterministic preference handling
        bool preferManaged = settings?.LspPreferManaged ?? true;
        bool preferSystem = settings?.LspPreferSystem ?? true;
        // Validate managed presence (requires actual executable, not just dir)
        var managedExe = LspInstallationManager.FindManagedExecutable(cfg, settings);
        bool managedExists = managedExe != null && File.Exists(managedExe);
        string? systemExe = null;
        if (cfg.AllowSystem && preferSystem)
            systemExe = LspInstallationManager.FindSystemExecutable(cfg);
        // Also try .cmd fallback for system
        if (systemExe == null && cfg.AllowSystem && preferSystem && cfg.Command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var without = cfg.Command[..^4];
            systemExe = LspRuntimeDetector.FindOnPath(without);
        }
        bool systemExists = systemExe != null && File.Exists(systemExe);

        // Deterministic order:
        // - UserOverride already returned
        // - If preferManaged && preferSystem => Managed first, then System
        // - If preferManaged && !preferSystem => Managed only
        // - If !preferManaged && preferSystem => System first, then Managed
        // - If !preferManaged && !preferSystem => neither (go to installable)
        if (preferManaged && preferSystem)
        {
            if (managedExists)
            {
                var resolved = CloneWithCommand(cfg, managedExe!);
                var ver = await ProbeVersionAsync(managedExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                // Verify executable actually works (version probe not required to succeed, but file must exist)
                return new(LspServerSource.Managed, managedExe!, resolved, ver, null, false);
            }
            if (systemExists)
            {
                var resolved = CloneWithCommand(cfg, systemExe!);
                var ver = await ProbeVersionAsync(systemExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.System, systemExe!, resolved, ver, null, false);
            }
        }
        else if (preferManaged && !preferSystem)
        {
            if (managedExists)
            {
                var resolved = CloneWithCommand(cfg, managedExe!);
                var ver = await ProbeVersionAsync(managedExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.Managed, managedExe!, resolved, ver, null, false);
            }
            // System explicitly disabled – don't fall back
        }
        else if (!preferManaged && preferSystem)
        {
            if (systemExists)
            {
                var resolved = CloneWithCommand(cfg, systemExe!);
                var ver = await ProbeVersionAsync(systemExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.System, systemExe!, resolved, ver, null, false);
            }
            if (managedExists)
            {
                var resolved = CloneWithCommand(cfg, managedExe!);
                var ver = await ProbeVersionAsync(managedExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.Managed, managedExe!, resolved, ver, null, false);
            }
        }
        // If both disabled, skip both and go to installable/manual

        // 4. Installable? Enforce SHA-256 for github artifacts
        bool isGithub = (cfg.InstallMethod?.Equals("github", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cfg.DownloadUrl?.Contains("github.com", StringComparison.OrdinalIgnoreCase) ?? false);
        bool hasSha = !string.IsNullOrWhiteSpace(cfg.Sha256);
        bool canInstall = cfg.AllowAutoInstall && !string.Equals(cfg.InstallMethod, "manual", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(cfg.DownloadUrl) || !string.IsNullOrWhiteSpace(cfg.PackageName) || cfg.InstallMethod == "npm" || cfg.InstallMethod == "github");
        // For github/standalone without checksum, treat as manual – security requirement
        if (canInstall && isGithub && !hasSha)
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' download requires SHA-256 verification (no checksum provided). Please install manually from {cfg.DownloadUrl} or update provider metadata.", false);
        }
        if (canInstall && !isGithub && !hasSha && !string.IsNullOrWhiteSpace(cfg.DownloadUrl) && cfg.InstallMethod != "npm")
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' cannot be auto-installed without SHA-256. Install manually.", false);
        }
        if (canInstall)
            return new(LspServerSource.Installable, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' not installed. Can be installed on demand.", true);
        if (cfg.InstallMethod != null && cfg.InstallMethod.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' requires manual installation. Ensure '{cfg.Command}' is on PATH.", false);
        return new(LspServerSource.Missing, null, cfg, null, $"Language server '{cfg.Command}' not found on PATH and no install source configured.", false);
    }

    private static LspConfiguration CloneWithCommand(LspConfiguration cfg, string newCommand) => new()
    {
        Command = newCommand,
        Arguments = cfg.Arguments,
        Languages = cfg.Languages,
        FileExtensions = cfg.FileExtensions,
        Env = new Dictionary<string, string>(cfg.Env, StringComparer.OrdinalIgnoreCase),
        WorkingDirectory = cfg.WorkingDirectory,
        InitializationOptions = cfg.InitializationOptions,
        RootMarkers = cfg.RootMarkers,
        ProviderId = cfg.ProviderId,
        DisplayName = cfg.DisplayName,
        Version = cfg.Version,
        InstallMethod = cfg.InstallMethod,
        PackageName = cfg.PackageName,
        DownloadUrl = cfg.DownloadUrl,
        Sha256 = cfg.Sha256,
        Runtime = cfg.Runtime,
        RuntimeMinVersion = cfg.RuntimeMinVersion,
        AllowAutoInstall = cfg.AllowAutoInstall,
        AllowSystem = cfg.AllowSystem,
        VersionArgs = cfg.VersionArgs
    };

    private static async Task<string?> ProbeVersionAsync(string exe, string[] versionArgs, CancellationToken ct)
    {
        try
        {
            var (ok, ver, err) = await LspInstallationManager.TryGetVersionAsync(exe, versionArgs, ct).ConfigureAwait(false);
            if (ok && !string.IsNullOrWhiteSpace(ver))
            {
                // return first line
                var first = ver.Split('\n')[0].Trim();
                return first.Length > 120 ? first[..120] : first;
            }
            return null;
        }
        catch { return null; }
    }

    public static string GetStatusText(LspResolution r)
    {
        return r.Source switch
        {
            LspServerSource.Managed => $"Managed ({r.Version ?? "installed"})",
            LspServerSource.System => $"System ({r.Version ?? "found"})",
            LspServerSource.UserOverride => $"Custom ({r.Version ?? "override"})",
            LspServerSource.Installable => "Not installed – can install",
            LspServerSource.RuntimeMissing => $"Runtime missing: {r.Error}",
            LspServerSource.ManualRequired => "Manual install required",
            LspServerSource.Disabled => "Disabled",
            _ => "Not installed"
        };
    }
}

