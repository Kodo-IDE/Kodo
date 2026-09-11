// Licensed under GPL-v3.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

/// <summary>Generic LSP client – speaks LSP, not Python/clangd/etc. One instance per server/workspace.</summary>
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
                // Use initializationOptions as base for workspace/configuration responses (generic)
                if (!string.IsNullOrWhiteSpace(section) && _config.InitializationOptions is JsonElement initOpts && initOpts.ValueKind == JsonValueKind.Object)
                {
                    // Try to find section in initializationOptions (e.g., "python.analysis" or "python")
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
                // Generic: for any python/pyright related section, return a config that matches VS Code defaults
                // This makes Termyx clean like VS Code without hardcoding per-language in core
                var lowerSection = section?.ToLowerInvariant() ?? "";
                if (lowerSection.Contains("python") || lowerSection.Contains("pyright"))
                {
                    // Check if initializationOptions has this exact section
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
                    }
                    // Fallback: return VS Code-like defaults for python analysis
                    // VS Code default is typeCheckingMode: off, diagnosticMode: openFilesOnly
                    var defaultConfig = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["typeCheckingMode"] = "off",
                        ["diagnosticMode"] = "openFilesOnly",
                        ["autoImportCompletions"] = true,
                        ["useLibraryCodeForTypes"] = true
                    };
                    // Merge any python.analysis from initializationOptions
                    if (_config.InitializationOptions is JsonElement init2 && init2.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement analysisEl = default;
                        bool hasAnalysis = false;
                        if (init2.TryGetProperty("python", out var py2) && py2.ValueKind == JsonValueKind.Object && py2.TryGetProperty("analysis", out analysisEl))
                            hasAnalysis = true;
                        else if (TryGetSection(init2, "python.analysis", out var secVal) && secVal.ValueKind == JsonValueKind.Object)
                        {
                            analysisEl = secVal;
                            hasAnalysis = true;
                        }
                        if (hasAnalysis)
                        {
                            try
                            {
                                var anaDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(analysisEl.GetRawText());
                                if (anaDict != null)
                                    foreach (var kv in anaDict) defaultConfig[kv.Key] = kv.Value;
                            }
                            catch { }
                        }
                    }
                    // For "python" section (not python.analysis), return {analysis: defaultConfig}
                    if (section == "python" && lowerSection == "python")
                    {
                        results.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["analysis"] = defaultConfig });
                    }
                    else
                    {
                        results.Add(defaultConfig);
                    }
                    continue;
                }
                // For other sections, try initializationOptions, otherwise null
                if (!string.IsNullOrWhiteSpace(section) && _config.InitializationOptions is JsonElement initOpts2 && initOpts2.ValueKind == JsonValueKind.Object && TryGetSection(initOpts2, section, out var secVal2))
                {
                    try { results.Add(JsonSerializer.Deserialize<object>(secVal2.GetRawText())); continue; }
                    catch { }
                }
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
        // Non-Windows: strip .cmd/.bat for portability so a Windows-declared `pyright-langserver.cmd` still works on Linux/macOS
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
