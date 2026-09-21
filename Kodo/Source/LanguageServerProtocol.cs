// Licensed under GPL-v3.0
#pragma warning disable CA1416 // Validate platform compatibility - guarded with IsOSPlatform checks
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
    private readonly object _writeGate = new();
    private readonly HashSet<PendingWrite> _writes = new();
    private Task _writeTail = Task.CompletedTask;
    private Exception? _transportError;

    private sealed class PendingWrite
    {
        public required Func<string> Serialize { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Writing { get; set; }
    }

    private readonly StringBuilder _stderrBuffer = new();
    private bool _disposed;
    private bool _shutdownRequested;
    private string _shutdownReason = "";

    public bool IsStarted
    {
        get
        {
            lock (_writeGate)
                return _transportError is null && _process is not null && !_process.HasExited;
        }
    }
    public bool IsInitialized { get; private set; }
    internal LspConfiguration Configuration => _config;
    internal string ClientWorkspaceRoot => _workspaceRoot;
    public string Id => _config.Command;
    public IReadOnlyList<string> SemanticTokenTypes { get; private set; } = Array.Empty<string>();

    public bool SupportsIncrementalSync()
    {
        if (ServerCapabilities is not JsonElement caps || caps.ValueKind != JsonValueKind.Object) return false;
        if (!caps.TryGetProperty("textDocumentSync", out var sync)) return false;
        if (sync.ValueKind == JsonValueKind.Number) return sync.GetInt32() == 2;
        if (sync.ValueKind == JsonValueKind.Object && sync.TryGetProperty("change", out var change) && change.ValueKind == JsonValueKind.Number)
            return change.GetInt32() == 2;
        return false;
    }

    public bool Supports(string method)
    {
        if (ServerCapabilities is not JsonElement caps || caps.ValueKind != JsonValueKind.Object) return true;
        var property = method switch
        {
            "textDocument/signatureHelp" => "signatureHelpProvider",
            "textDocument/documentSymbol" => "documentSymbolProvider",
            "textDocument/foldingRange" => "foldingRangeProvider",
            "textDocument/semanticTokens/full" => "semanticTokensProvider",
            "textDocument/inlayHint" => "inlayHintProvider",
            "textDocument/documentHighlight" => "documentHighlightProvider",
            "textDocument/typeDefinition" => "typeDefinitionProvider",
            "textDocument/implementation" => "implementationProvider",
            "textDocument/definition" => "definitionProvider",
            _ => null
        };
        if (property is null || !caps.TryGetProperty(property, out var value)) return true;
        return value.ValueKind != JsonValueKind.False;
    }

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
        lock (_writeGate)
        {
            if (_transportError is not null) throw _transportError;
        }
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
            TerminateTransport(new IOException($"LSP server exited ({code})"));
            OnExit?.Invoke(code);
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

        try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _writer = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: false) { AutoFlush = false };
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
                        ["completion"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["completionItem"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["snippetSupport"] = true, ["documentationFormat"] = new[] { "markdown", "plaintext" }, ["resolveSupport"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["properties"] = new[] { "documentation", "detail", "additionalTextEdits" } } } },
                        ["hover"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["contentFormat"] = new[] { "markdown", "plaintext" } },
                        ["signatureHelp"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["signatureInformation"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["documentationFormat"] = new[] { "markdown", "plaintext" }, ["parameterInformation"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["labelOffsetSupport"] = true } } },
                        ["definition"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["linkSupport"] = false },
                        ["typeDefinition"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["linkSupport"] = false },
                        ["implementation"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["linkSupport"] = false },
                        ["documentSymbol"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["hierarchicalDocumentSymbolSupport"] = true, ["symbolKind"] = new Dictionary<string, object?>(StringComparer.Ordinal) },
                        ["documentHighlight"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                        ["foldingRange"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["lineFoldingOnly"] = false },
                        ["semanticTokens"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["requests"] = new Dictionary<string, object?> { ["range"] = true, ["full"] = new Dictionary<string, object?> { ["delta"] = true } }, ["tokenTypes"] = Array.Empty<string>(), ["tokenModifiers"] = Array.Empty<string>(), ["formats"] = new[] { "relative" } },
                        ["inlayHint"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["resolveSupport"] = new Dictionary<string, object?> { ["properties"] = new[] { "tooltip", "textEdits" } } },
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
            if (result.HasValue && result.Value.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("capabilities", out var caps))
            {
                ServerCapabilities = caps.Clone();
                if (caps.TryGetProperty("semanticTokensProvider", out var semanticProvider) && semanticProvider.ValueKind == JsonValueKind.Object &&
                    semanticProvider.TryGetProperty("legend", out var legend) && legend.TryGetProperty("tokenTypes", out var tokenTypes) && tokenTypes.ValueKind == JsonValueKind.Array)
                    SemanticTokenTypes = tokenTypes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).ToArray();
            }

            await SendNotificationAsync("initialized", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
            KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialized. Server caps: {ServerCapabilities?.GetRawText() ?? "<none>"}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
                KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialization failed", ex);
            // Do not mark initialized; allow retry.
            lock (_initLock) _initTask = null;
            if (ex is FileNotFoundException or TimeoutException or InvalidOperationException or IOException)
                throw;
            throw new InvalidOperationException($"LSP '{_config.Command}' initialization failed: {ex.Message}", ex);
        }
    }

    public async Task<JsonElement?> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task write;
        lock (_writeGate)
        {
            if (_transportError is not null) throw _transportError;
            _pending[id] = tcs;
            write = EnqueueWriteAsync(() =>
            {
                var json = LspProtocol.CreateRequest(id, method, @params);
                try
                {
                    var preview = json.Length > 800 ? json.Substring(0, 800) + "..." : json;
                    KodoDiagnostics.LogDebug($"LSP request id={id} method={method} json={preview}");
                }
                catch { }
                return json;
            }, cancellationToken);
        }

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            await write.ConfigureAwait(false);
            var response = await tcs.Task.ConfigureAwait(false);
            return response.ValueKind == JsonValueKind.Undefined ? null : response;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetCanceled();
            if (tcs.Task.IsFaulted) _ = tcs.Task.Exception;
        }
    }

    public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        return EnqueueWriteAsync(() => LspProtocol.CreateNotification(method, @params), cancellationToken);
    }

    private async Task SendResponseAsync(int id, object? result)
    {
        try
        {
            await EnqueueWriteAsync(() => LspProtocol.CreateResponse(id, result), CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private Task EnqueueWriteAsync(Func<string> serialize, CancellationToken cancellationToken)
    {
        lock (_writeGate)
        {
            if (_transportError is not null) return Task.FromException(_transportError);
            if (_writer is null)
                return Task.FromException(new IOException("LSP server not running"));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            var item = new PendingWrite { Serialize = serialize, CancellationToken = cancellationToken };
            _writes.Add(item);
            _writeTail = _writeTail.ContinueWith(_ => WriteFrameAsync(item), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return AwaitWriteAsync(item);
        }
    }

    private async Task AwaitWriteAsync(PendingWrite item)
    {
        using var registration = item.CancellationToken.Register(() =>
        {
            lock (_writeGate)
            {
                if (item.Completion.Task.IsCompleted) return;
                item.Completion.TrySetCanceled(item.CancellationToken);
                _writes.Remove(item);
                if (item.Writing)
                    TerminateTransport(new IOException("LSP frame write canceled; transport closed"));
            }
        });
        await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(PendingWrite item)
    {
        try
        {
            lock (_writeGate)
            {
                if (item.Completion.Task.IsCompleted) return;
            }
            var frame = LspProtocol.Frame(item.Serialize());
            StreamWriter writer;
            lock (_writeGate)
            {
                if (item.Completion.Task.IsCompleted) return;
                item.CancellationToken.ThrowIfCancellationRequested();
                writer = _writer!;
                item.Writing = true;
            }
            await writer.WriteAsync(frame.AsMemory(), item.CancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(item.CancellationToken).ConfigureAwait(false);
            lock (_writeGate)
            {
                item.Writing = false;
                item.Completion.TrySetResult(true);
            }
        }
        catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested)
        {
            lock (_writeGate)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
                if (item.Writing)
                    TerminateTransport(new IOException("LSP frame write canceled; transport closed"));
            }
        }
        catch (Exception ex)
        {
            lock (_writeGate)
            {
                item.Completion.TrySetException(ex);
                if (item.Writing) TerminateTransport(ex);
            }
        }
        finally
        {
            lock (_writeGate) _writes.Remove(item);
        }
    }

    private void TerminateTransport(Exception error)
    {
        lock (_writeGate)
        {
            if (_transportError is not null) return;
            _transportError = error;
            foreach (var item in _writes) item.Completion.TrySetException(error);
            _writes.Clear();
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var pending)) pending.TrySetException(error);
            var writer = _writer;
            var process = _process;
            var tail = _writeTail;
            _writer = null;
            _ = Task.Run(async () =>
            {
                try { _cts.Cancel(); } catch { }
                try
                {
                    if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
                try { writer?.BaseStream.Dispose(); } catch { }
                try { await tail.ConfigureAwait(false); } catch { }
                try { writer?.Dispose(); } catch { }
                try { process?.Dispose(); } catch { }
                _cts.Dispose();
            });
        }
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
        stdout = new BufferedStream(stdout, 32768);
        var headerLines = new List<string>(4);
        var buffer = new byte[8192];
        var headerAccum = new System.IO.MemoryStream(512);
        var bodyBuffer = Array.Empty<byte>();

        var skipRead = false;
        try
        {
            while (!ct.IsCancellationRequested && _process?.HasExited == false)
            {
                headerLines.Clear();
                var contentLength = -1;
                var foundHeaderEnd = false;

                // Read headers chunk-wise until \r\n\r\n or \n\n
                while (!foundHeaderEnd)
                {
                    if (!skipRead)
                    {
                        var read = await stdout.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (read == 0) return;
                        headerAccum.Write(buffer, 0, read);
                    }
                    skipRead = false;
                    // Search for \r\n\r\n
                    var headerText = Encoding.UTF8.GetString(headerAccum.GetBuffer(), 0, (int)headerAccum.Length);
                    int headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    int headerEndLen = 4;
                    if (headerEnd < 0) { headerEnd = headerText.IndexOf("\n\n", StringComparison.Ordinal); headerEndLen = 2; }
                    if (headerEnd >= 0)
                    {
                        var headerPart = headerText.Substring(0, headerEnd);
                        var lines = headerPart.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            headerLines.Add(line);
                            if (LspProtocol.TryParseContentLength(line, out var len)) contentLength = len;
                        }
                        var bodyStartInBuffer = headerEnd + headerEndLen;
                        var headerBytesLen = Encoding.UTF8.GetByteCount(headerText.Substring(0, bodyStartInBuffer));
                        // Handle body bytes already in headerAccum beyond header
                        var remainingInAccum = (int)headerAccum.Length - headerBytesLen;
                        // Prepare to read body, but first handle leftover bytes
                        if (contentLength < 0)
                        {
                            KodoDiagnostics.LogDebug($"LSP missing Content-Length headers: {string.Join("|", headerLines)}");
                            // Drain remaining and continue
                            headerAccum.SetLength(0);
                            if (remainingInAccum > 0) headerAccum.Write(headerAccum.GetBuffer(), headerBytesLen, remainingInAccum);
                            break;
                        }
                        if (contentLength == 0) { headerAccum.SetLength(0); if (remainingInAccum > 0) headerAccum.Write(headerAccum.GetBuffer(), headerBytesLen, remainingInAccum); foundHeaderEnd = true; break; }
                        if (contentLength > 8 * 1024 * 1024)
                        {
                            KodoDiagnostics.LogDebug($"LSP message too large: {contentLength}");
                            // Drain body
                            int toDrain = contentLength - remainingInAccum;
                            var drainBuf = new byte[8192];
                            while (toDrain > 0)
                            {
                                var r = await stdout.ReadAsync(drainBuf, 0, Math.Min(drainBuf.Length, toDrain), ct).ConfigureAwait(false);
                                if (r == 0) return; toDrain -= r;
                            }
                            // Keep any extra bytes beyond body? For simplicity reset
                            headerAccum.SetLength(0);
                            if (remainingInAccum > 0 && remainingInAccum > contentLength)
                            {
                                // Extra bytes after body - keep for next message
                                var extra = remainingInAccum - contentLength;
                                headerAccum.Write(headerAccum.GetBuffer(), headerBytesLen + contentLength, extra);
                            }
                            foundHeaderEnd = true;
                            break;
                        }
                        // Ensure bodyBuffer sized
                        if (bodyBuffer.Length < contentLength) bodyBuffer = new byte[contentLength];
                        int bodyOffset = 0;
                        if (remainingInAccum > 0)
                        {
                            var copy = Math.Min(remainingInAccum, contentLength);
                            Buffer.BlockCopy(headerAccum.GetBuffer(), headerBytesLen, bodyBuffer, 0, copy);
                            bodyOffset = copy;
                            var leftover = remainingInAccum - copy;
                            if (leftover > 0)
                                Buffer.BlockCopy(headerAccum.GetBuffer(), headerBytesLen + copy, headerAccum.GetBuffer(), 0, leftover);
                            headerAccum.SetLength(leftover);
                            headerAccum.Position = leftover;
                        }
                        else headerAccum.SetLength(0);
                        while (bodyOffset < contentLength)
                        {
                            var r = await stdout.ReadAsync(bodyBuffer, bodyOffset, contentLength - bodyOffset, ct).ConfigureAwait(false);
                            if (r == 0) return; bodyOffset += r;
                        }
                        var json = Encoding.UTF8.GetString(bodyBuffer, 0, contentLength);
                        HandleMessage(json);
                        foundHeaderEnd = true;
                        skipRead = headerAccum.Length > 0;
                        break;
                    }
                    // If header too large without terminator, protect
                    if (headerAccum.Length > 8192)
                    {
                        KodoDiagnostics.LogDebug($"LSP header too large, draining");
                        headerAccum.SetLength(0);
                        break;
                    }
                }
                if (!foundHeaderEnd) continue;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP read loop failed", ex); }
        finally { TerminateTransport(new System.IO.EndOfStreamException("LSP server stdout closed")); }
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
        if (method is not null)
        {
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
                    if (root.Value.TryGetProperty("params", out var p2))
                        KodoDiagnostics.LogDebug($"LSP {method}: {p2.GetRawText()}");
                }
                _ = SendResponseAsync(id.Value, result);
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
                    if (TryGetSection(initOpts, section, out var sectionValue))
                    {
                        // Deserialize to object for response
                        try { results.Add(JsonSerializer.Deserialize<object>(sectionValue.GetRawText())); continue; }
                        catch { }
                    }
                    // Also try without prefix (e.g., request for "python" when init
                    if (section == "python" && initOpts.TryGetProperty("python", out var py))
                    {
                        try { results.Add(JsonSerializer.Deserialize<object>(py.GetRawText())); continue; }
                        catch { }
                    }
                }
                // Generic: try to satisfy request from .kox
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
                // For python analysis, if .kox has python.analysis, return it;
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
                // For other sections or if no .kox config, return null to let
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
        // Non-Windows: strip .cmd/.bat for portability so a
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
        lock (_writeGate)
        {
            if (_disposed) return;
            _disposed = true;
            TerminateTransport(new ObjectDisposedException(nameof(LspClient)));
        }
    }
}

public partial class MainWindow
{
    private readonly LspManager _lspManager = new();
    // Filesystem document paths follow OS case semantics via FileSystemPaths.
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

    private LoadedExtension? ResolveLspExtensionForFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || IsPlainTextFile(filePath) || HasNoFileExtension(filePath))
            return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var candidates = LoadedExtensions.Where(e => e.HasLsp).ToList();
        var match = candidates.FirstOrDefault(e => e.AllLspConfigurations.Any(c => c.FileExtensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase))));
        if (match is not null) return match;
        match = candidates.FirstOrDefault(e => e.Extensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase)));
        return match;
    }

    private LspConfiguration? ResolveLspConfigurationForFile(string? filePath)
    {
        var ext = ResolveLspExtensionForFile(filePath);
        if (ext is null) return null;
        if (string.IsNullOrWhiteSpace(filePath)) return ext.Lsp ?? ext.Lsps.FirstOrDefault();
        var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
        var specific = ext.AllLspConfigurations.FirstOrDefault(c => c.FileExtensions.Any(fe => fe.Equals(fileExt, StringComparison.OrdinalIgnoreCase)));
        return specific ?? ext.Lsp ?? ext.Lsps.FirstOrDefault();
    }

    private string GetWorkspaceRootForFile(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var markers = ResolveLspConfigurationForFile(filePath)?.RootMarkers ?? ResolveLspExtensionForFile(filePath)?.Lsp?.RootMarkers;
            if (markers != null && markers.Length > 0)
            {
                var dir = Path.GetDirectoryName(filePath);
                while (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    foreach (var m in markers)
                        if (File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)))
                            return dir;
                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }
            }
        }
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
        if (!string.Equals(path, _currentFilePath, StringComparison.OrdinalIgnoreCase))
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
        var text = EditorTextBox.Document.Text;
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
        await LspSyncDocumentAsync(path, text, edits).ConfigureAwait(false);
    }

    private void LspDocument_Changing(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e)
    {
        try
        {
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
                var start = doc.GetLocation(startOffset);
                var end = doc.GetLocation(endOffset);
                list.Add(new LspPendingEdit(start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1, e.InsertedText?.Text ?? string.Empty));
            }
        }
        catch { }
    }

    private void QueueLspDidChange(string filePath)
    {
        if (ResolveLspExtensionForFile(filePath) is null) return;
        var len = EditorTextBox?.Document?.TextLength ?? 0;
        lock (_lspPendingLock) _pendingLspChangePath = filePath;
        Dispatcher.UIThread.Post(() =>
        {
            var curLen = EditorTextBox?.Document?.TextLength ?? len;
            if (curLen > 250_000) _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(3000);
            else if (curLen > 120_000) _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(2000);
            else if (curLen > 80_000) _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(600);
            else _lspDidChangeTimer.Interval = TimeSpan.FromMilliseconds(300);
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
        // Update provider registry and extension status
        var providerId = resolution.ResolvedConfiguration.EffectiveProviderId;
        LspProviderRegistry.SetStatus(providerId, resolution.ToDependencyStatus(), resolution.Error, resolution.Version, resolution.ExecutablePath, resolution.Source == LspServerSource.Managed);
        lspExt.LspStatus = resolution.ToDependencyStatus();
        lspExt.LspStatusMessage = resolution.Error;
        lspExt.LspProviderStatuses[providerId] = (resolution.ToDependencyStatus(), resolution.Error);

        // Respect dismissed prompts unless auto-install enabled
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
                // Use confirmation dialog with Install / Not Now
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
                    // Clear dismissed and missing flags so next open succeeds
                    _lspDismissedInstallPrompts.Remove(lspExt.Id);
                    _lspMissingNotified.Remove(lspExt.Id);
                    _lspMissingNotified.Remove(lspExt.Id + ":" + providerId);
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
        if (isLargeFileForLsp)
        {
            try { await Task.Delay(500).ConfigureAwait(false); } catch { }
            // If user switched away, still continue but at background priority – don't block UI.
            await Task.Yield();
        }
        else
        {
            // Small cooperative yield so file open isn't blocked by LSP resolve/start.
            await Task.Yield();
        }

        // Centralized resolution: managed -> system -> installable
        var settings = BuildLspResolverSettings();
        var resolution = await LspServerResolver.ResolveAsync(targetCfg, settings, lspExt.Id).ConfigureAwait(false);
        KodoDiagnostics.LogDebug($"LSP resolve {lspExt.Id} source={resolution.Source} exe={resolution.ExecutablePath} canInstall={resolution.CanInstall} err={resolution.Error}");
        if (!resolution.IsReady)
        {
            lock (_lspOpenLock) _lspPendingOpens.Remove(filePath);
            await HandleLspNotReadyAsync(lspExt, resolution, filePath).ConfigureAwait(false);
            return;
        }
        else
        {
            // Record successful resolution
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
        // Resolve using centralized resolver to match didOpen's resolved
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

    private async Task LspSyncDocumentAsync(string filePath, string text, List<LspPendingEdit>? edits)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        filePath = NormalizeFilePath(filePath);
        bool isOpen;
        lock (_lspOpenLock) isOpen = _lspOpenDocuments.Contains(filePath);
        if (!isOpen)
        {
            await LspNotifyDidOpenAsync(filePath, text).ConfigureAwait(false);
            return;
        }
        var prepared = await PrepareLspChangeAsync(filePath).ConfigureAwait(false);
        if (prepared is null) return;
        var (uri, version, client) = prepared.Value;

        if (edits is { Count: > 0 } && text.Length > 120_000 && client.SupportsIncrementalSync() &&
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
                KodoDiagnostics.LogDebug($"LSP incremental didChange {uri} edits={edits.Count} ver={version}");
                return;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"LSP incremental didChange failed for {uri}, falling back to full text", ex);
                lock (_lspOpenLock) _lspForceFullSync.Add(filePath);
            }
        }

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
            _lspDiagnosticVersions.Remove(filePath);
            _lspDiagnosticRefreshPending.Remove(filePath);
            var _uri = FilePathToUri(filePath);
            _lspDiagnostics.Remove(_uri);
            _lspDiagnostics.Remove(NormalizeFilePath(_uri));
            // Remove any stale uri-keyed entries that outlive filePath via
            List<string>? _stale = null;
            foreach (var _k in _lspDiagnostics.Keys)
                if (IsSameDocument(_k, filePath)) (_stale ??= new List<string>()).Add(_k);
            if (_stale != null) foreach (var _k in _stale) { _lspDiagnostics.Remove(_k); _lspDiagnosticVersions.Remove(_k); }
        }
        // Refresh tab diagnostics even if LSP not configured (clear previous LSP counts)
        Dispatcher.UIThread.Post(() => UpdateInactiveTabDiagnosticsForFile(filePath));

        var lspExt = ResolveLspExtensionForFile(filePath);
        if (lspExt is null || !lspExt.HasLsp) return;
        var closeCfg = ResolveLspConfigurationForFile(filePath) ?? lspExt.Lsp ?? lspExt.Lsps.FirstOrDefault();
        if (closeCfg is null) return;
        var workspace = GetWorkspaceRootForFile(filePath);
        // Try resolved config first, then fallback to original for
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
                // Normalize so lookups use consistent key; filePath came from
                var normPath = NormalizeFilePath(filePath);
                if (!_lspOpenDocuments.Contains(normPath) && !_lspOpenDocuments.Contains(filePath) && !_lspPendingOpens.Contains(normPath) && !_lspPendingOpens.Contains(filePath)) return;
                _lspDiagnostics[normPath] = diagnostics;
                if (publishVersion.HasValue)
                    _lspDiagnosticVersions[normPath] = publishVersion.Value;
                refreshKey = normPath;
                if (!_lspDiagnosticRefreshPending.Add(refreshKey)) return;
            }
            KodoDiagnostics.LogDebug($"LSP publishDiagnostics {filePath} count={diagnostics.Count} uri={uri} version={(publishVersion?.ToString() ?? "<none>")}");
            for (var i = 0; i < Math.Min(diagnostics.Count, 3); i++)
                KodoDiagnostics.LogDebug($"  LSP diag {i}: [{diagnostics[i].StartLine}:{diagnostics[i].StartChar}-{diagnostics[i].EndLine}:{diagnostics[i].EndChar}] {diagnostics[i].Severity} {diagnostics[i].Message}");
            // Invalidate Insight cache so next UpdateErrorHighlightingAsync
            lock (_insightAnalysisCacheLock)
            {
                _cachedInsightAnalysisVersion = -1;
                _cachedInsightAnalysisText = null;
            }
            // Trigger UI refresh if current file (normalize both sides to
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
                }
                else
                {
                    // Still ensure diagnostics for that file will be shown when it
                    KodoDiagnostics.LogDebug($"LSP diagnostics stored for inactive file {filePath} (current {_currentFilePath}) – will show on activation");
                    UpdateInactiveTabDiagnosticsForFile(filePath);
                }
            });
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP publishDiagnostics handling failed", ex); }
    }

    private static string FileUriToPath(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath)) return uriOrPath;
        // Already a rooted Windows path (C:\ or C:/ or \\), just
        if (Path.IsPathRooted(uriOrPath) && !uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Path.GetFullPath(uriOrPath); } catch { return uriOrPath; }
        }
        if (uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Use Uri to handle percent-encoding and drive letters correctly
                var u = new Uri(uriOrPath, UriKind.Absolute);
                if (u.IsFile)
                {
                    var local = u.LocalPath;
                    // LocalPath on Windows is already "C:\..." but for
                    if (local.Length >= 3 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':')
                        local = local.Substring(1);
                    // If local somehow is still ":\Users" (missing drive), recover
                    if (local.StartsWith(":\\", StringComparison.Ordinal) || local.StartsWith(":/", StringComparison.Ordinal) || local.StartsWith(":", StringComparison.Ordinal))
                    {
                        var abs = Uri.UnescapeDataString(u.AbsolutePath);
                        // AbsolutePath is "/c:/Users/..." or "/C:/Users/..." – trim
                        abs = abs.TrimStart('/');
                        abs = abs.Replace('/', Path.DirectorySeparatorChar);
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
                // If still not rooted, don't combine with current directory –
                return part;
            }
            catch { }
            return uriOrPath;
        }
        // Plain relative path – don't combine with Kodo source; return as-is
        return uriOrPath;
    }

    private List<ErrorSpan> GetLspDiagnosticsForFile(string? filePath, string text)
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
                    // Fallback: linear scan for same document (handles any remaining
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
        if (spans.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans={spans.Count} first=[{spans[0].StartOffset}:{spans[0].Length}] {spans[0].Message}");
        else if (raw.Count > 0)
            KodoDiagnostics.LogDebug($"LSP GetDiagnosticsForFile {filePath} textLen={text.Length} raw={raw.Count} spans=0 (all filtered)");
        return spans;
    }

    private const int LspLineIndexCacheCapacity = 8;
    private static readonly object LspLineIndexCacheLock = new();
    private static readonly (string Text, Lazy<int[]> Starts)[] LspLineIndexCache = new (string, Lazy<int[]>)[LspLineIndexCacheCapacity];
    private static int _nextLspLineIndexSlot;

    private static int CountLspLineBreaks(string text, int endExclusive)
    {
        var breaks = 0;
        for (var i = 0; i < endExclusive && i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                breaks++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (text[i] == '\n')
            {
                breaks++;
            }
        }
        return breaks;
    }

    private static int[] GetLspLineStarts(string text)
    {
        Lazy<int[]>? starts = null;
        lock (LspLineIndexCacheLock)
        {
            foreach (var entry in LspLineIndexCache)
            {
                if (ReferenceEquals(entry.Text, text))
                {
                    starts = entry.Starts;
                    break;
                }
            }
            if (starts is null)
            {
                starts = new Lazy<int[]>(() =>
                {
                    var offsets = new List<int> { 0 };
                    for (var offset = 0; offset < text.Length; offset++)
                    {
                        if (text[offset] == '\r')
                        {
                            if (offset + 1 < text.Length && text[offset + 1] == '\n') { offsets.Add(offset + 2); offset++; }
                            else offsets.Add(offset + 1);
                        }
                        else if (text[offset] == '\n')
                        {
                            offsets.Add(offset + 1);
                        }
                    }
                    return offsets.ToArray();
                }, LazyThreadSafetyMode.ExecutionAndPublication);
                LspLineIndexCache[_nextLspLineIndexSlot] = (text, starts);
                _nextLspLineIndexSlot = (_nextLspLineIndexSlot + 1) % LspLineIndexCacheCapacity;
            }
        }
        return starts.Value;
    }

    private static int OffsetFromLspPosition(string text, int line, int character)
    {
        if (line < 0) line = 0;
        if (character < 0) character = 0;
        var starts = GetLspLineStarts(text);
        if (line >= starts.Length) return text.Length;
        var offset = starts[line];
        var lineEnd = line + 1 < starts.Length ? starts[line + 1] - 1 : text.Length;
        // Exclude trailing \r for CRLF files when computing column limit
        var lineLen = lineEnd - offset;
        if (lineLen > 0 && lineEnd > offset && text[lineEnd - 1] == '\r')
            lineLen--;
        var col = Math.Min(character, Math.Max(0, lineLen));
        if (col > 0 && col < lineLen && offset + col < text.Length && char.IsHighSurrogate(text[offset + col - 1]) && char.IsLowSurrogate(text[offset + col]))
            col++;
        return Math.Clamp(offset + col, 0, Math.Max(0, text.Length));
    }

    private static (int line, int character) OffsetToLspPosition(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    if (i + 1 < offset) { line++; lineStart = i + 2; i++; }
                }
                else { line++; lineStart = i + 1; }
            }
            else if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        var character = offset - lineStart;
        return (line, character);
    }

    // Phase 7 – Completion (generic)
    private async Task<IReadOnlyList<InsightSuggestion>> GetLspCompletionSuggestionsAsync(string? filePath, int offset, string text, string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<InsightSuggestion>();
        // Allow empty prefix for trigger characters like '.' – LSP will
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
        var seenSuggestionTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!item.TryGetProperty("label", out var labelEl)) continue;
            var label = labelEl.GetString();
            if (string.IsNullOrWhiteSpace(label)) continue;
            // Filter by prefix (LSP should already filter, but ensure)
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
                2 or 3 or 4 => InsightKind.Function, // Method, Function, Constructor
                5 or 10 => InsightKind.Property, // Field, Property
                7 or 8 or 22 or 13 or 25 => InsightKind.Type, // Class, Interface, Struct, Enum, TypeParameter
                9 => InsightKind.Namespace, // Module
                14 => InsightKind.Keyword,
                6 or 12 or 21 => InsightKind.Variable, // Variable, Value, Constant
                _ => InsightKind.Variable
            };

            if (!seenSuggestionTexts.Add(insertText)) continue;
            suggestions.Add(new InsightSuggestion(insertText, kind));
            if (suggestions.Count >= 25) break;
        }
        KodoDiagnostics.LogDebug($"LSP completion parsed items={items.Count} suggestions={suggestions.Count} for {filePath} samples={string.Join(", ", suggestions.Take(3).Select(s => s.Text))}");

        return suggestions;
    }

    // Phase 8 – Hover (generic) with coalescing and dedup
    private async Task<string?> GetLspHoverAsync(string? filePath, int offset, int line, int character, CancellationToken ct = default)
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

        // contents can be string, {language, value}, MarkupContent
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

    // Phase 9c – Document Formatting (generic, HasFormatter)
    private async Task<bool> TryLspFormatDocumentAsync(string? filePath, string text, CancellationToken ct = default)
    {
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
            if (!string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal))
            {
                KodoDiagnostics.LogDebug($"LSP formatting rejected as stale for {filePath}");
                return;
            }
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
                try
                {
                    doc.Replace(sOff, len, nt.GetString() ?? "");
                }
                catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
                {
                    KodoDiagnostics.LogDebug("LSP edit: Visual line race suppressed", ex);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { doc.Replace(sOff, len, nt.GetString() ?? ""); } catch { }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            }
        });
        return true;
    }

    // Phase 9d – Code Actions (generic, HasCodeActionProvider)
    private async Task<bool> TryLspCodeActionsAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normPath = NormalizeFilePath(filePath);
            int? serverVersion;
            int? sentVersion;
            lock (_lspDiagnosticsLock) serverVersion = _lspDiagnosticVersions.TryGetValue(normPath, out var sv) ? sv : null;
            lock (_lspOpenLock) sentVersion = _lspDocumentVersions.TryGetValue(normPath, out var ev) ? ev : null;
            if (serverVersion.HasValue && sentVersion.HasValue && serverVersion.Value < sentVersion.Value)
            {
                KodoDiagnostics.LogDebug($"LSP codeAction diagnostics stale for {filePath} server={serverVersion} sent={sentVersion}; flushing and waiting");
                SetLspFeatureStatus("Waiting for fresh diagnostics…");
                List<LspPendingEdit>? pending = null;
                lock (_lspOpenLock)
                {
                    if (_lspPendingEdits.TryGetValue(normPath, out var list) && list.Count > 0)
                    {
                        pending = new(list);
                        _lspPendingEdits.Remove(normPath);
                    }
                }
                if (pending is { Count: > 0 })
                    await LspSyncDocumentAsync(filePath, text, pending).ConfigureAwait(false);
                int wantVersion;
                lock (_lspOpenLock) wantVersion = _lspDocumentVersions.TryGetValue(normPath, out var nv) ? nv : sentVersion.Value + 1;
                if (!await WaitForLspDiagnosticsVersionAsync(normPath, wantVersion, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
                {
                    SetLspFeatureStatus("Diagnostics are stale – try the quick fix again in a moment.");
                    return false;
                }
                var fresh = await Dispatcher.UIThread.InvokeAsync<(string Text, int Caret, string? Path)?>(() =>
                {
                    if (EditorTextBox?.Document is null || !string.Equals(_currentFilePath, filePath, StringComparison.OrdinalIgnoreCase)) return null;
                    return (EditorTextBox.Document.Text, EditorTextBox.TextArea.Caret.Offset, _currentFilePath);
                });
                if (fresh is null || string.IsNullOrWhiteSpace(fresh.Value.Path)) return false;
                return await TryLspCodeActionsCoreAsync(fresh.Value.Path, fresh.Value.Caret, fresh.Value.Text, ct).ConfigureAwait(false);
            }
        }
        return await TryLspCodeActionsCoreAsync(filePath, offset, text, ct).ConfigureAwait(false);
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

    private async Task<bool> TryLspCodeActionsCoreAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
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
        if (client is null || !client.IsInitialized)
        {
            KodoDiagnostics.LogDebug($"LSP codeAction skipped for {filePath}: language server not ready");
            SetLspFeatureStatus("Language server is not ready yet – try again in a moment.");
            return false;
        }
        var uri = FilePathToUri(filePath);
        var (line, character) = OffsetToLspPosition(text, offset);
        var contextDiag = GetDiagnosticAtCaret();
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
                ["diagnostics"] = GetDiagnosticContextForLsp(text, offset),
                ["only"] = new[] { "quickfix" }
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
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP codeAction failed for {filePath}", ex);
            SetLspFeatureStatus("Quick fix request failed – the language server timed out.");
            return false;
        }
        if (result is null || result.Value.ValueKind == JsonValueKind.Null || result.Value.ValueKind != JsonValueKind.Array)
        {
            KodoDiagnostics.LogDebug($"LSP codeAction response empty for {filePath}");
            SetLspFeatureStatus("No quick fixes available at the caret.");
            return false;
        }
        var actions = result.Value.EnumerateArray().ToList();
        KodoDiagnostics.LogDebug($"LSP codeAction response count={actions.Count} for {filePath}");
        for (var ai = 0; ai < Math.Min(actions.Count, 5); ai++)
        {
            var raw = actions[ai].GetRawText();
            KodoDiagnostics.LogDebug($"  LSP action {ai}: {(raw.Length > 1500 ? raw[..1500] + "…" : raw)}");
        }
        static bool IsSourceAction(JsonElement action) =>
            action.TryGetProperty("kind", out var kind) &&
            kind.ValueKind == JsonValueKind.String &&
            (kind.GetString() ?? string.Empty).StartsWith("source.", StringComparison.OrdinalIgnoreCase);
        static bool IsDisabledAction(JsonElement action) =>
            action.TryGetProperty("disabled", out var disabled) &&
            (disabled.ValueKind == JsonValueKind.True ||
             (disabled.ValueKind == JsonValueKind.Object && disabled.TryGetProperty("reason", out _)));
        actions = actions.Where(a => !IsDisabledAction(a)).ToList();
        if (actions.Count == 0)
        {
            KodoDiagnostics.LogDebug($"LSP codeAction all disabled for {filePath}");
            SetLspFeatureStatus("No quick fixes available at the caret.");
            return false;
        }
        var quickfixes = actions.Where(a => !IsSourceAction(a)).ToList();
        KodoDiagnostics.LogDebug($"LSP codeAction quickfix-kind count={quickfixes.Count} for {filePath}");
        JsonElement first;
        if (quickfixes.Count == 1)
        {
            first = quickfixes[0];
        }
        else if (quickfixes.Count > 1)
        {
            first = await ChooseLspCodeActionAsync(quickfixes).ConfigureAwait(false);
        }
        else if (actions.Count > 0)
        {
            first = await ChooseLspCodeActionAsync(actions).ConfigureAwait(false);
            if (first.ValueKind != JsonValueKind.Undefined && first.ValueKind != JsonValueKind.Null)
                KodoDiagnostics.LogDebug($"LSP codeAction offering source-level action '{(first.TryGetProperty("title", out var st) ? st.GetString() : "?")}' for user confirmation");
        }
        else
        {
            SetLspFeatureStatus("No quick fixes available at the caret.");
            return false;
        }
        if (first.ValueKind == JsonValueKind.Undefined || first.ValueKind == JsonValueKind.Null) return false;
        var firstTitle = first.TryGetProperty("title", out var firstTitleEl) ? firstTitleEl.GetString() ?? "quick fix" : "quick fix";
        if (contextDiag is { } cd)
        {
            var diagnosticCurrent = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (EditorTextBox?.Document is null || !string.Equals(EditorTextBox.Document.Text, text, StringComparison.Ordinal)) return false;
                foreach (var span in _errorHighlightRenderer.Spans)
                {
                    if (!string.Equals(span.Message, cd.message, StringComparison.Ordinal)) continue;
                    if (span.StartOffset <= cd.offset + cd.length && span.StartOffset + span.Length >= cd.offset) return true;
                }
                return false;
            });
            if (!diagnosticCurrent)
            {
                KodoDiagnostics.LogDebug($"LSP codeAction diagnostic gone for {filePath}; refusing stale fix");
                SetLspFeatureStatus("That diagnostic is already fixed.");
                return false;
            }
        }
        if (first.TryGetProperty("edit", out var edit) && edit.ValueKind == JsonValueKind.Object)
        {
            if (await ApplyLspWorkspaceEditAsync(edit, filePath, text))
            {
                SetLspFeatureStatus($"Applied '{TruncateStatusText(firstTitle, 60)}'.");
                return true;
            }
            SetLspFeatureStatus("Could not apply the quick fix – the file changed.");
            return false;
        }
        if (first.TryGetProperty("command", out var cmd))
        {
            KodoDiagnostics.LogDebug($"LSP codeAction is command: {cmd.GetRawText()}");
            string? commandText = null;
            JsonElement? commandArgs = null;
            if (cmd.ValueKind == JsonValueKind.String)
                commandText = cmd.GetString();
            else if (cmd.ValueKind == JsonValueKind.Object && cmd.TryGetProperty("command", out var commandName))
            {
                commandText = commandName.GetString();
                if (cmd.TryGetProperty("arguments", out var args)) commandArgs = args;
            }
            if (string.IsNullOrWhiteSpace(commandText)) return false;
            var executeParams = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = commandText,
                ["arguments"] = commandArgs.HasValue ? JsonSerializer.Deserialize<object>(commandArgs.Value.GetRawText()) : null
            };
            try
            {
                using var executeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                executeCts.CancelAfter(TimeSpan.FromSeconds(5));
                await client.SendRequestAsync("workspace/executeCommand", executeParams, executeCts.Token).ConfigureAwait(false);
                SetLspFeatureStatus($"Ran '{TruncateStatusText(commandText, 60)}'.");
                return true;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("LSP executeCommand failed", ex);
                SetLspFeatureStatus("The language server command failed.");
                return false;
            }
        }
        SetLspFeatureStatus("No quick fixes available at the caret.");
        return false;
    }

    private void SetLspFeatureStatus(string message) =>
        Dispatcher.UIThread.Post(() => { ExtensionsStatusText = message; });

    private static string TruncateStatusText(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength] + "…";

    private async Task<JsonElement> ChooseLspCodeActionAsync(IReadOnlyList<JsonElement> actions)
    {
        var selected = -1;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var labels = actions.Select(action => action.TryGetProperty("title", out var title) ? title.GetString() ?? "Untitled code action" : "Untitled code action").ToArray();
            var list = new ListBox { ItemsSource = labels, MinHeight = 220, MinWidth = 520 };
            var apply = new Button { Content = "Apply", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
            var cancel = new Button { Content = "Cancel", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(8, 8, 0, 0) };
            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, apply } };
            var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Children = { new TextBlock { Text = "Select a code action", FontWeight = Avalonia.Media.FontWeight.Bold }, list, buttons } };
            var window = new Window { Title = "LSP Code Actions", Width = 620, Height = 380, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            apply.Click += (_, _) => { selected = list.SelectedIndex; window.Close(); };
            cancel.Click += (_, _) => window.Close();
            list.DoubleTapped += (_, _) => { selected = list.SelectedIndex; window.Close(); };
            list.SelectedIndex = 0;
            await window.ShowDialog(this);
        });
        return selected >= 0 && selected < actions.Count ? actions[selected] : default;
    }

    private async Task<bool> TryLspRenameAsync(string? filePath, int offset, string text, string newName, CancellationToken ct = default)
    {
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
            return await ApplyLspWorkspaceEditAsync(result.Value, filePath, text).ConfigureAwait(false);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP rename failed for {filePath}", ex); return false; }
    }

    private async Task<JsonElement?> RequestLspFeatureAsync(string? filePath, string method, object parameters, CancellationToken ct)
    {
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

    private object CreateLspPositionParams(string filePath, string text, int offset)
    {
        var (line, character) = OffsetToLspPosition(text, offset);
        return new Dictionary<string, object?>
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath) },
            ["position"] = new Dictionary<string, object?> { ["line"] = line, ["character"] = character }
        };
    }

    private Task<JsonElement?> GetLspSignatureHelpAsync(string? filePath, int offset, string text, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/signatureHelp", CreateLspPositionParams(filePath!, text, offset), ct);

    private Task<JsonElement?> GetLspDocumentSymbolsAsync(string? filePath, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/documentSymbol", new Dictionary<string, object?> { ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) } }, ct);

    private Task<JsonElement?> GetLspFoldingRangesAsync(string? filePath, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/foldingRange", new Dictionary<string, object?> { ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) } }, ct);

    private Task<JsonElement?> GetLspSemanticTokensAsync(string? filePath, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/semanticTokens/full", new Dictionary<string, object?> { ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) } }, ct);

    private Task<JsonElement?> GetLspInlayHintsAsync(string? filePath, string text, CancellationToken ct = default) =>
        RequestLspFeatureAsync(filePath, "textDocument/inlayHint", new Dictionary<string, object?>
        {
            ["textDocument"] = new Dictionary<string, object?> { ["uri"] = FilePathToUri(filePath!) },
            ["range"] = new Dictionary<string, object?>
            {
                ["start"] = new Dictionary<string, object?> { ["line"] = 0, ["character"] = 0 },
                ["end"] = new Dictionary<string, object?> { ["line"] = CountLspLineBreaks(text, text.Length), ["character"] = 0 }
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

    private object[] GetDiagnosticContextForLsp(string text, int offset)
    {
        var diagnostic = GetDiagnosticAtCaret();
        if (diagnostic is not { } d) return Array.Empty<object>();
        var (line, character) = OffsetToLspPosition(text, d.offset);
        var (endLine, endCharacter) = OffsetToLspPosition(text, d.offset + Math.Max(1, d.length));
        return new object[] { new Dictionary<string, object?>
        {
            ["range"] = new Dictionary<string, object?>
            {
                ["start"] = new Dictionary<string, object?> { ["line"] = line, ["character"] = character },
                ["end"] = new Dictionary<string, object?> { ["line"] = endLine, ["character"] = endCharacter }
            },
            ["message"] = d.message,
            ["severity"] = 1
        }};
    }

    internal static string ApplyLspTextEdits(string text, IReadOnlyList<(int Start, int End, string NewText)> edits)
    {
        var ordered = edits
            .Select((e, i) => (e.Start, e.End, e.NewText, i))
            .OrderByDescending(e => e.Start)
            .ThenByDescending(e => e.End)
            .ThenByDescending(e => e.i)
            .ToList();
        var result = text;
        foreach (var (start, end, newText, _) in ordered)
        {
            var s = Math.Clamp(start, 0, result.Length);
            var e2 = Math.Clamp(Math.Max(end, s), 0, result.Length);
            result = result.Remove(s, e2 - s).Insert(s, newText ?? string.Empty);
        }
        return result;
    }

    private async Task<bool> ApplyLspWorkspaceEditAsync(JsonElement edit, string filePath, string text)
    {
        try
        {
            var applied = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (EditorTextBox?.Document is null) return;
                var doc = EditorTextBox.Document;
                if (!string.Equals(doc.Text, text, StringComparison.Ordinal))
                {
                    KodoDiagnostics.LogDebug($"LSP workspace edit rejected as stale for {filePath}");
                    return;
                }
                // edit can be {changes: {uri: [edits]}} or {documentChanges: [...]}
if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in changes.EnumerateObject())
                    {
                        var uri = prop.Name;
                        if (!IsSameDocument(uri, filePath))
                        {
                            var targetPath = FileUriToPath(uri);
                            var targetTab = OpenTabs.FirstOrDefault(t => IsSameDocument(t.Path, targetPath));
                            if (targetTab is null)
                            {
                                if (!File.Exists(targetPath)) continue;
                                var originalFile = File.ReadAllText(targetPath);
                                var fileEdits = new List<(int Start, int End, string NewText)>();
                                foreach (var e in prop.Value.EnumerateArray())
                                {
                                    if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                                    var s = range.GetProperty("start"); var ee = range.GetProperty("end");
                                    var so = OffsetFromLspPosition(originalFile, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                                    var eo = OffsetFromLspPosition(originalFile, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                                    fileEdits.Add((so, eo, nt.GetString() ?? string.Empty));
                                }
                                var updatedFile = ApplyLspTextEdits(originalFile, fileEdits);
                                if (!string.Equals(originalFile, updatedFile, StringComparison.Ordinal)) { File.WriteAllText(targetPath, updatedFile); applied = true; }
                                continue;
                            }
                            var originalTabText = targetTab.Content;
                            var tabEdits = new List<(int Start, int End, string NewText)>();
                            foreach (var e in prop.Value.EnumerateArray())
                            {
                                if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                                var s = range.GetProperty("start"); var ee = range.GetProperty("end");
                                var so = OffsetFromLspPosition(originalTabText, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                                var eo = OffsetFromLspPosition(originalTabText, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                                tabEdits.Add((so, eo, nt.GetString() ?? string.Empty));
                            }
                            var updated = ApplyLspTextEdits(originalTabText, tabEdits);
                            var changed = !string.Equals(updated, targetTab.Content, StringComparison.Ordinal);
                            targetTab.Content = updated;
                            targetTab.IsDirty = true;
                            applied |= changed;
                            continue;
                        }
                        var activeEdits = new List<(int Start, int End, string NewText, int Index)>();
                        var editIndex = 0;
                        foreach (var e in prop.Value.EnumerateArray())
                        {
                            if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                            var s = range.GetProperty("start");
                            var ee = range.GetProperty("end");
                            var sOff = OffsetFromLspPosition(text, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                            var eOff = OffsetFromLspPosition(text, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                            activeEdits.Add((sOff, Math.Max(eOff, sOff), nt.GetString() ?? "", editIndex++));
                        }
                        foreach (var (sOff, eOff, newText, _) in activeEdits.OrderByDescending(x => x.Start).ThenByDescending(x => x.End).ThenByDescending(x => x.Index))
                        {
                            KodoDiagnostics.LogDebug($"LSP apply changes [{sOff},{eOff}) -> '{(newText.Length > 80 ? newText[..80] + "…" : newText)}' docLen={doc.TextLength}");
                            var len = Math.Max(0, Math.Min(eOff, doc.TextLength) - Math.Min(sOff, doc.TextLength));
                            var start = Math.Clamp(sOff, 0, doc.TextLength);
                            try
                            {
                                doc.Replace(start, len, newText);
                                applied = true;
                            }
                            catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
                            {
                                KodoDiagnostics.LogDebug("LSP changes edit: Visual line race suppressed", ex);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { doc.Replace(start, len, newText); } catch { }
                                }, Avalonia.Threading.DispatcherPriority.Background);
                            }
                        }
                    }
                }
                else if (edit.TryGetProperty("documentChanges", out var docChanges) && docChanges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dc in docChanges.EnumerateArray())
                    {
                        if (dc.TryGetProperty("kind", out var operationKind) && operationKind.ValueKind == JsonValueKind.String)
                        {
                            var kind = operationKind.GetString();
                            var operationUri = dc.TryGetProperty("uri", out var opUri) ? opUri.GetString() : null;
                            var operationPath = string.IsNullOrWhiteSpace(operationUri) ? null : FileUriToPath(operationUri);
                            try
                            {
                                if (kind == "create" && !string.IsNullOrWhiteSpace(operationPath))
                                {
                                    var overwrite = dc.TryGetProperty("options", out var options) && options.TryGetProperty("overwrite", out var ow) && ow.GetBoolean();
                                    if (File.Exists(operationPath) && !overwrite) continue;
                                    Directory.CreateDirectory(Path.GetDirectoryName(operationPath)!);
                                    if (!File.Exists(operationPath) || overwrite) File.WriteAllText(operationPath, string.Empty);
                                    applied = true;
                                }
                                else if (kind == "delete" && !string.IsNullOrWhiteSpace(operationPath) && File.Exists(operationPath))
                                {
                                    var ignoreIfNotExists = dc.TryGetProperty("options", out var options) && options.TryGetProperty("ignoreIfNotExists", out var ign) && ign.GetBoolean();
                                    if (!ignoreIfNotExists || File.Exists(operationPath)) File.Delete(operationPath);
                                    applied = true;
                                }
                                else if (kind == "rename" && dc.TryGetProperty("oldUri", out var oldUriEl) && !string.IsNullOrWhiteSpace(operationUri))
                                {
                                    var oldPath = FileUriToPath(oldUriEl.GetString() ?? string.Empty);
                                    if (!File.Exists(oldPath)) continue;
                                    Directory.CreateDirectory(Path.GetDirectoryName(operationPath!)!);
                                    File.Move(oldPath, operationPath!, overwrite: dc.TryGetProperty("options", out var options) && options.TryGetProperty("overwrite", out var ow) && ow.GetBoolean());
                                    applied = true;
                                }
                            }
                            catch (Exception ex) { KodoDiagnostics.LogDebug($"LSP resource operation {kind} failed", ex); }
                            continue;
                        }
                        if (!dc.TryGetProperty("edits", out var edits)) continue;
                        var uri = dc.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var u) ? u.GetString() : filePath;
                        if (!IsSameDocument(uri, filePath))
                        {
                            var targetPath = FileUriToPath(uri ?? string.Empty);
                            var targetTab = OpenTabs.FirstOrDefault(t => IsSameDocument(t.Path, targetPath));
                            var originalUpdated = targetTab?.Content ?? (File.Exists(targetPath) ? File.ReadAllText(targetPath) : null);
                            if (originalUpdated is null) continue;
                            var otherEdits = new List<(int Start, int End, string NewText)>();
                            foreach (var e in edits.EnumerateArray())
                            {
                                if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                                var s = range.GetProperty("start"); var ee = range.GetProperty("end");
                                var so = OffsetFromLspPosition(originalUpdated, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                                var eo = OffsetFromLspPosition(originalUpdated, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                                otherEdits.Add((so, eo, nt.GetString() ?? string.Empty));
                            }
                            var updated = ApplyLspTextEdits(originalUpdated, otherEdits);
                            if (targetTab is not null) { targetTab.Content = updated; targetTab.IsDirty = true; }
                            else if (!string.IsNullOrWhiteSpace(targetPath)) File.WriteAllText(targetPath, updated);
                            applied = true;
                            continue;
                        }
                        var activeDocEdits = new List<(int Start, int End, string NewText, int Index)>();
                        var activeDocIndex = 0;
                        foreach (var e in edits.EnumerateArray())
                        {
                            if (!e.TryGetProperty("newText", out var nt) || !e.TryGetProperty("range", out var range)) continue;
                            var s = range.GetProperty("start");
                            var ee = range.GetProperty("end");
                            var sOff = OffsetFromLspPosition(text, s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
                            var eOff = OffsetFromLspPosition(text, ee.GetProperty("line").GetInt32(), ee.GetProperty("character").GetInt32());
                            activeDocEdits.Add((sOff, Math.Max(eOff, sOff), nt.GetString() ?? "", activeDocIndex++));
                        }
                        foreach (var (sOff, eOff, newText, _) in activeDocEdits.OrderByDescending(x => x.Start).ThenByDescending(x => x.End).ThenByDescending(x => x.Index))
                        {
                            KodoDiagnostics.LogDebug($"LSP apply documentChanges [{sOff},{eOff}) -> '{(newText.Length > 80 ? newText[..80] + "…" : newText)}' docLen={doc.TextLength}");
                            var start = Math.Clamp(sOff, 0, doc.TextLength);
                            var len = Math.Max(0, Math.Min(eOff, doc.TextLength) - start);
                            try
                            {
                                doc.Replace(start, len, newText);
                                applied = true;
                            }
                            catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
                            {
                                KodoDiagnostics.LogDebug("LSP code action edit: Visual line race suppressed", ex);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { doc.Replace(start, len, newText); } catch { }
                                }, Avalonia.Threading.DispatcherPriority.Background);
                            }
                        }
                    }
                }
            });
            return applied;
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"ApplyLspWorkspaceEdit failed: {ex.Message}", ex); return false; }
    }

    // Phase 9 – Go to Definition (generic)
    private async Task<bool> TryLspGoToDefinitionAsync(string? filePath, int offset, string text, CancellationToken ct = default)
    {
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

internal static class LspInstallationManager
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Kodo/{KodoDiagnostics.AppVersion} (https://github.com/Kodo-IDE/Kodo)");
        return c;
    }

    public static string ManagedRoot => GetManagedRoot(null);

    public static string GetManagedRoot(AppSettings? settings)
    {
        var custom = settings?.LspInstallDir?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                // Must be absolute and inside user's profile or local app data;
                var full = Path.GetFullPath(custom);
                // Allow any absolute path that is not system root, but ensure
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

    public static bool IsManagedInstalled(LspConfiguration cfg) => IsManagedInstalled(cfg, null);
    public static bool IsManagedInstalled(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = FindManagedExecutable(cfg, settings);
        return exe != null && File.Exists(exe);
    }

    public static string? FindSystemExecutable(LspConfiguration cfg)
    {
        if (!cfg.AllowSystem) return null;
        var found = LspRuntimeDetector.FindOnPath(cfg.Command);
        if (found != null) return found;
        // Dotnet tool fallback: check %USERPROFILE%\.dotnet\tools
        if (cfg.InstallMethod?.Equals("dotnet", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
                var candidates = new[] { Path.Combine(toolsDir, cfg.Command), Path.Combine(toolsDir, cfg.Command + ".exe"), Path.Combine(toolsDir, cfg.EffectiveProviderId), Path.Combine(toolsDir, cfg.EffectiveProviderId + ".exe") };
                foreach (var c in candidates) if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    public static string? FindManagedExecutable(LspConfiguration cfg) => FindManagedExecutable(cfg, null);
    public static string? FindManagedExecutable(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = GetManagedExecutablePath(cfg, settings);
        if (File.Exists(exe)) return exe;
        var dir = GetProviderDir(cfg, settings);
        if (!Directory.Exists(dir)) return null;
        // Strict: look for file matching Command fileName exactly,
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
            var binPref = candidates.FirstOrDefault(c => c.Contains("node_modules" + Path.DirectorySeparatorChar + ".bin", StringComparison.OrdinalIgnoreCase));
            if (binPref != null) return binPref;
            return candidates.FirstOrDefault();
        }
        catch { return null; }
    }

    private static readonly ConcurrentDictionary<string, (bool ok, string? version, string? error)> VersionProbeCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<(bool ok, string? version, string? error)> TryGetVersionAsync(string exePath, string[] versionArgs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return (false, null, "Executable not found");
        var args = versionArgs != null && versionArgs.Length > 0 ? string.Join(" ", versionArgs) : "--version";
        var cacheKey = exePath + "\0" + args;
        if (VersionProbeCache.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            bool isCmdScript = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            string fileName = exePath;
            string arguments = args;
            if (isCmdScript && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                fileName = comSpec;
                arguments = $"/c \"{exePath}\" {args}";
            }
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
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
            var result = (true, outText?.Trim(), (string?)null);
            VersionProbeCache[cacheKey] = result;
            return result;
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
            // Double-check after acquiring lock: maybe another thread
            if (IsManagedInstalled(cfg, settings))
                return new(InstallResultKind.AlreadyInstalled, "Already installed", GetProviderDir(cfg, settings));

            var method = (cfg.InstallMethod ?? "").Trim().ToLowerInvariant();
            if (method == "dotnet")
                return await InstallViaDotnetAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            if (method == "npm" || (!string.IsNullOrWhiteSpace(cfg.PackageName) && string.IsNullOrWhiteSpace(cfg.DownloadUrl)))
                return await InstallViaNpmAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.DownloadUrl))
            {
                // Enforce SHA-256 for github/standalone artifacts
                var isGithub = method == "github" || cfg.DownloadUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) || cfg.DownloadUrl.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(cfg.Sha256))
                    return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' download requires SHA-256 verification. No checksum provided – please install manually from {cfg.DownloadUrl} and configure an override, or update the provider to include a trusted checksum.", null);
                return await InstallViaDownloadAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            }

            return new(InstallResultKind.NotInstallable, $"No install source configured for '{cfg.EffectiveProviderId}'. Install manually and ensure '{cfg.Command}' is on PATH.", null);
        }
        finally { sem.Release(); }
    }

    private static readonly System.Text.RegularExpressions.Regex NpmPackageNameRegex = new(@"^(@[a-z0-9-~][a-z0-9-._~]*\/)?[a-z0-9-~][a-z0-9-._~]*(@[a-z0-9-._~][a-z0-9-._~.-]*)?$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsValidNpmPackageName(string pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg)) return false;
        // Reject shell metacharacters, traversal, absolute paths
        if (pkg.IndexOfAny(new[] { ';', '&', '|', '`', '$', '(', ')', '<', '>', '"', '\'', '\\', ' ', '\n', '\r', '\t' }) >= 0) return false;
        if (pkg.Contains("..", StringComparison.Ordinal)) return false;
        return NpmPackageNameRegex.IsMatch(pkg.Trim());
    }

    private static ProcessStartInfo BuildNpmProcessStartInfo(string npmExe, string[] npmArgs, string workingDirectory)
    {
        var isCmdScript = npmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || npmExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (isCmdScript && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            // Escape each arg for cmd: quote if contains space or quote
            static string EscapeArg(string a)
            {
                if (string.IsNullOrEmpty(a)) return "\"\"";
                if (!a.Contains(' ') && !a.Contains('"') && !a.Contains('\t')) return a;
                return "\"" + a.Replace("\"", "\"\"") + "\"";
            }
            var inner = $"\"{npmExe}\" {string.Join(" ", npmArgs.Select(EscapeArg))}";
            return new ProcessStartInfo
            {
                FileName = comSpec,
                Arguments = $"/d /s /c \"{inner}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = npmExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
            foreach (var a in npmArgs) psi.ArgumentList.Add(a);
            return psi;
        }
    }

    private static async Task<InstallResult> InstallViaNpmAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var pkg = cfg.PackageName ?? cfg.EffectiveProviderId;
        if (!IsValidNpmPackageName(pkg))
            return new(InstallResultKind.Failed, $"Invalid npm package name '{pkg}'. Package name contains illegal characters or pattern.", null);
        // Detect node + npm via enhanced lookup (PATH + known locations
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
            var npmExe = LspRuntimeDetector.FindExecutable("npm") ?? LspRuntimeDetector.FindOnPath("npm") ?? "npm";
            // Harden npm install: disable lifecycle scripts (supply-chain),
            var npmArgs = new[] { "install", "--prefix", stagingDir, "--ignore-scripts", "--no-audit", "--no-fund", "--progress=false", "--loglevel=error", pkg };
            var psi = BuildNpmProcessStartInfo(npmExe, npmArgs, Path.GetTempPath());
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
            var stagedExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(pkg, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals(cfg.Command, StringComparison.OrdinalIgnoreCase));
            // For vscode-langservers-extracted, we expect multiple servers;
            if (stagedExe == null && !Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "npm install produced no files", null);
            }
            // Atomic move staging -> providerDir with retry for locked files
            const int maxRetries = 5;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    if (Directory.Exists(providerDir)) Directory.Delete(providerDir, true);
                    break;
                }
                catch (IOException) when (attempt < maxRetries - 1)
                {
                    await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < maxRetries - 1)
                {
                    await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
            try
            {
                Directory.Move(stagingDir, providerDir);
            }
            catch (IOException ex)
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to finalize installation (directory locked or in use): {ex.Message}", null);
            }
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

    private static async Task<InstallResult> InstallViaDotnetAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var pkg = cfg.PackageName ?? cfg.EffectiveProviderId;
        var dotnetRt = await LspRuntimeDetector.DetectAsync("dotnet", cfg.RuntimeMinVersion ?? "6.0.0", ct).ConfigureAwait(false);
        if (!dotnetRt.Found) return new(InstallResultKind.RuntimeMissing, dotnetRt.Error ?? "dotnet SDK is required. Install from https://dotnet.microsoft.com/download", null);
        progress?.Report($"Installing {pkg} via dotnet...");
        KodoDiagnostics.LogDebug($"LSP dotnet tool install {pkg}");
        try
        {
            var listPsi = new ProcessStartInfo { FileName = "dotnet", Arguments = "tool list --global", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var listProc = new Process { StartInfo = listPsi };
            if (listProc.Start())
            {
                var outTxt = await listProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await listProc.WaitForExitAsync(ct).ConfigureAwait(false);
                if (outTxt.Contains(pkg, StringComparison.OrdinalIgnoreCase))
                {
                    var updPsi = new ProcessStartInfo { FileName = "dotnet", Arguments = $"tool update --global {pkg}", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using var updProc = new Process { StartInfo = updPsi };
                    if (updProc.Start())
                    {
                        var uo = await updProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                        var ue = await updProc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                        await updProc.WaitForExitAsync(ct).ConfigureAwait(false);
                        KodoDiagnostics.LogDebug($"dotnet tool update stdout: {uo} stderr: {ue} exit:{updProc.ExitCode}");
                        if (updProc.ExitCode == 0) return new(InstallResultKind.Success, $"Updated {pkg} via dotnet tool", null);
                    }
                }
            }
            var psi = new ProcessStartInfo { FileName = "dotnet", Arguments = $"tool install --global {pkg}", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return new(InstallResultKind.Failed, "Failed to start dotnet", null);
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"dotnet tool install stdout: {stdout} stderr: {stderr} exit:{proc.ExitCode}");
            if (proc.ExitCode != 0)
                return new(InstallResultKind.Failed, $"dotnet tool install failed (exit {proc.ExitCode}): {stderr.Trim()}", null);
            return new(InstallResultKind.Success, $"Installed {pkg} via dotnet tool", null);
        }
        catch (Exception ex) when (IsOffline(ex))
        {
            return new(InstallResultKind.Offline, $"Offline – could not download {pkg}: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP dotnet install failed for {pkg}", ex);
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
                // Phase 1 Linux: single-file downloads lose the exec bit on copy.
                MakeExecutable(dest);
            }
            // Validate staging contains at least one file
            if (!Directory.EnumerateFileSystemEntries(stagingDir).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "Archive extracted no files", null);
            }
            // Additional validation: ensure expected executable exists (or
            var foundExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(GetManagedExecutablePath(cfg, settings)), StringComparison.OrdinalIgnoreCase));
            if (foundExe == null)
            {
                // For zip that contains nested dir, and command not at top
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
            // Phase 1 Linux: ZipSlip check must be case-sensitive on case-sensitive filesystems.
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!destPath.StartsWith(fullDestDir, pathComparison))
                throw new InvalidDataException($"Zip entry escapes destination: {entry.FullName}");
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (entry.FullName.EndsWith("/")) continue;
            await Task.Run(() => entry.ExtractToFile(destPath, overwrite: true), ct).ConfigureAwait(false);
        }
        RestoreUnixExecBit(destDir);
    }

    private static async Task ExtractTarGzSecureAsync(string tgzPath, string destDir, CancellationToken ct)
    {
        // Use System.Formats.Tar if available (.NET 7+); fallback to
        await Task.Run(() =>
        {
            using var fs = File.OpenRead(tgzPath);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
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
                        var tarPathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        if (!destPath.StartsWith(fullDestDir, tarPathComparison))
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
        RestoreUnixExecBit(destDir);
    }

    private static void RestoreUnixExecBit(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is ".md" or ".txt" or ".json" or ".png" or ".svg" or ".xml" or ".pdb" or ".dll")
                        continue;
                    var mode = File.GetUnixFileMode(file);
                    mode |= System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute;
                    File.SetUnixFileMode(file, mode);
                }
                catch { }
            }
        }
        catch { }
    }

    internal static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch { }
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

internal sealed class LspManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<LspClient>> _starting = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LspClient? TryGetClient(string workspaceRoot, LspConfiguration config)
    {
        var key = MakeKey(workspaceRoot, config);
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var c)) return c;
            var root = NormalizeRoot(workspaceRoot);
            foreach (var candidate in _clients.Values)
            {
                if (!candidate.IsInitialized) continue;
                if (!string.Equals(NormalizeRoot(candidate.ClientWorkspaceRoot), root, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (string.Equals(candidate.Configuration.EffectiveProviderId, config.EffectiveProviderId, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch { }
            }
            return null;
        }
    }

    private static string NormalizeRoot(string workspaceRoot)
    {
        try { return Path.GetFullPath(workspaceRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return workspaceRoot ?? string.Empty; }
    }

    public Task<LspClient> GetOrStartAsync(string workspaceRoot, LspConfiguration config, CancellationToken ct = default)
    {
        var key = MakeKey(workspaceRoot, config);
        Task<LspClient> taskToAwait;
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LspManager));
            if (_clients.TryGetValue(key, out var existing) && existing.IsStarted)
                return Task.FromResult(existing);
            if (_starting.TryGetValue(key, out var inProgress))
                return inProgress;

            // stale entry (IsStarted == false and no in-progress task)
            if (existing is not null)
            {
                _clients.Remove(key);
                try { existing.Dispose(); } catch { }
            }

            var newClient = new LspClient(config, workspaceRoot);
            var startTask = StartAndRegisterAsync(newClient, key, config, workspaceRoot, ct);
            _starting[key] = startTask;
            taskToAwait = startTask;
        }
        return taskToAwait;
    }

    private async Task<LspClient> StartAndRegisterAsync(LspClient client, string key, LspConfiguration config, string workspaceRoot, CancellationToken ct)
    {
        client.OnExit += code =>
        {
            lock (_lock) _clients.Remove(key);
            KodoDiagnostics.LogDebug($"LSP manager removed exited client '{config.Command}' ws={workspaceRoot} code={code}");
        };

        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_disposed)
                    _clients[key] = client;
                else
                {
                    // Manager disposed while starting – clean up
                    try { client.Dispose(); } catch { }
                    _starting.Remove(key);
                    throw new ObjectDisposedException(nameof(LspManager));
                }
                _starting.Remove(key);
            }
            return client;
        }
        catch (Exception ex)
        {
            lock (_lock) _starting.Remove(key);
            try { client.Dispose(); } catch { }
            KodoDiagnostics.LogDebug($"LSP manager failed to start '{config.Command}'", ex);
            throw;
        }
    }

    public async Task ShutdownAsync(string workspaceRoot, LspConfiguration config, string reason = "manager shutdown")
    {
        var key = MakeKey(workspaceRoot, config);
        LspClient? client = null;
        Task<LspClient>? startingTask = null;
        lock (_lock)
        {
            _clients.TryGetValue(key, out client);
            if (client is null) _starting.TryGetValue(key, out startingTask);
        }
        if (startingTask != null)
        {
            try { client = await startingTask.ConfigureAwait(false); } catch { return; }
        }
        if (client is null) return;
        await client.ShutdownAsync(reason).ConfigureAwait(false);
        lock (_lock) _clients.Remove(key);
        client.Dispose();
    }

    public async Task ShutdownAllAsync(string reason = "manager shutdown all")
    {
        List<LspClient> snapshot;
        List<Task<LspClient>> startingSnapshot;
        lock (_lock)
        {
            snapshot = new List<LspClient>(_clients.Values);
            startingSnapshot = new List<Task<LspClient>>(_starting.Values);
            _clients.Clear();
            _starting.Clear();
        }
        // Wait for any in-progress starts to finish, then shut them down too
        foreach (var t in startingSnapshot)
        {
            try
            {
                var c = await t.ConfigureAwait(false);
                if (!snapshot.Contains(c)) snapshot.Add(c);
            }
            catch { }
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
        var expandedArgs = string.Join(" ", config.Arguments.Select(a => a.Replace("{workspace}", root, StringComparison.OrdinalIgnoreCase).Replace("{workspaceFolder}", root, StringComparison.OrdinalIgnoreCase).Replace("{root}", root, StringComparison.OrdinalIgnoreCase)));
        return $"{root}|{config.Command}|{expandedArgs}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Avoid sync-over-async deadlock if called on UI thread
            if (Dispatcher.UIThread.CheckAccess())
            {
                var task = Task.Run(async () => await ShutdownAllAsync("manager dispose").ConfigureAwait(false));
                if (!task.Wait(TimeSpan.FromSeconds(5)))
                    KodoDiagnostics.LogDebug("LSP manager dispose timed out waiting for shutdown");
            }
            else
            {
                ShutdownAllAsync("manager dispose").GetAwaiter().GetResult();
            }
        }
        catch { }
        // Fallback: ensure any remaining clients are disposed even if
        lock (_lock)
        {
            foreach (var c in _clients.Values) try { c.Dispose(); } catch { }
            _clients.Clear();
            _starting.Clear();
        }
    }
}

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
        var value = headerLine[(idx + 1)..].Trim();
        var digits = 0;
        while (digits < value.Length && char.IsDigit(value[digits])) digits++;
        return digits > 0 && int.TryParse(value.Substring(0, digits), out length);
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
        var m = System.Text.RegularExpressions.Regex.Match(output, @"v?(\d+\.\d+(?:\.\d+)?(?:[.-]\w+)*)");
        if (m.Success) return m.Groups[1].Value;
        // Fallback for 'openjdk 21.0.1' or 'Python 3.11.5'
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var mm = System.Text.RegularExpressions.Regex.Match(p, @"\d+\.\d+.*");
            if (mm.Success) return mm.Value.TrimStart('v');
        }
        return output.Trim().Split(' ')[0].TrimStart('v');
    }

    private static async Task<(bool found, string? output, string? error)> TryRunAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            // Resolve exe via enhanced PATH + known locations and handle
            var resolvedExe = FindExecutable(exe) ?? exe;
            var isCmdScript = resolvedExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolvedExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            ProcessStartInfo psi;
            if (isCmdScript && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                // Use /d /s /c ""exe" args" so paths with spaces are handled
                psi = new ProcessStartInfo
                {
                    FileName = comSpec,
                    Arguments = $"/d /s /c \"\"{resolvedExe}\" {args}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = resolvedExe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
            }
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

    public static string? FindExecutable(string command)
    {
        var viaPath = FindOnPath(command);
        if (viaPath != null) return viaPath;
        var viaKnown = FindInKnownLocations(command);
        if (viaKnown != null) return viaKnown;
        var viaWhere = FindViaWhere(command);
        if (viaWhere != null) return viaWhere;
        return null;
    }

    private static string? FindInKnownLocations(string command)
    {
        try
        {
            var lower = command.Trim().ToLowerInvariant();
            if (lower != "node" && lower != "npm" && lower != "npx") return null;
            var isWindows = OperatingSystem.IsWindows();
            var target = lower switch
            {
                "node" => isWindows ? "node.exe" : "node",
                "npm" => isWindows ? "npm.cmd" : "npm",
                "npx" => isWindows ? "npx.cmd" : "npx",
                _ => lower
            };
            var candidates = new List<string>();
            if (isWindows)
            {
                var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
                if (!string.IsNullOrWhiteSpace(nvmHome)) { candidates.Add(Path.Combine(nvmHome, "node.exe")); candidates.Add(Path.Combine(nvmHome, "npm.cmd")); }
                var nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
                if (!string.IsNullOrWhiteSpace(nvmSymlink)) { candidates.Add(Path.Combine(nvmSymlink, "node.exe")); candidates.Add(Path.Combine(nvmSymlink, "npm.cmd")); }
                try { var voltaBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta", "bin"); candidates.Add(Path.Combine(voltaBin, "node.exe")); candidates.Add(Path.Combine(voltaBin, "npm.cmd")); } catch { }
                var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fnm");
                if (!string.IsNullOrWhiteSpace(fnmDir) && Directory.Exists(fnmDir))
                    try { var fnmNode = Directory.EnumerateFiles(fnmDir, "node.exe", SearchOption.AllDirectories).FirstOrDefault(); if (fnmNode != null) candidates.Add(fnmNode); } catch { }
                try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")); candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd")); } catch { }
                try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe")); candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "npm.cmd")); } catch { }
                foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
                {
                    try
                    {
                        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                        using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + (lower == "node" ? "node.exe" : "npm.cmd"));
                        var pathVal = appPaths?.GetValue(null) as string ?? appPaths?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(pathVal) && File.Exists(pathVal)) candidates.Add(pathVal);
                    }
                    catch { }
                    try
                    {
                        using var baseKey32 = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry32);
                        using var appPaths32 = baseKey32.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + (lower == "node" ? "node.exe" : "npm.cmd"));
                        var pathVal32 = appPaths32?.GetValue(null) as string ?? appPaths32?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(pathVal32) && File.Exists(pathVal32)) candidates.Add(pathVal32);
                    }
                    catch { }
                }
            }
            else
            {
                // Unix: nvm (NVM_DIR), fnm, volta, standard paths.
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var nvmDir = Environment.GetEnvironmentVariable("NVM_DIR");
                if (string.IsNullOrWhiteSpace(nvmDir)) nvmDir = Path.Combine(home, ".nvm");
                if (!string.IsNullOrWhiteSpace(nvmDir) && Directory.Exists(nvmDir))
                {
                    try
                    {
                        var nvmNode = Directory.EnumerateFiles(nvmDir, "node", SearchOption.AllDirectories)
                            .FirstOrDefault(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}node", StringComparison.Ordinal));
                        if (nvmNode != null) candidates.Add(nvmNode);
                    }
                    catch { }
                }
                var voltaHome = Environment.GetEnvironmentVariable("VOLTA_HOME");
                var voltaBinCandidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(voltaHome)) voltaBinCandidates.Add(Path.Combine(voltaHome, "bin"));
                voltaBinCandidates.Add(Path.Combine(home, ".volta", "bin"));
                try { voltaBinCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta", "bin")); } catch { }
                foreach (var vb in voltaBinCandidates.Distinct(StringComparer.Ordinal))
                    candidates.Add(Path.Combine(vb, target));
                var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR");
                if (string.IsNullOrWhiteSpace(fnmDir))
                    fnmDir = Path.Combine(home, ".local", "share", "fnm");
                if (!string.IsNullOrWhiteSpace(fnmDir) && Directory.Exists(fnmDir))
                {
                    try { var fnmNode = Directory.EnumerateFiles(fnmDir, "node", SearchOption.AllDirectories).FirstOrDefault(); if (fnmNode != null) candidates.Add(fnmNode); } catch { }
                    try { var fnmMultis = Directory.GetDirectories(fnmDir, "*", SearchOption.TopDirectoryOnly); foreach (var d in fnmMultis) candidates.Add(Path.Combine(d, "installation", "bin", target)); } catch { }
                }
                foreach (var p in new[] { "/usr/local/bin", "/usr/bin", "/opt/nodejs/bin", "/snap/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".fnm", "current", "bin") })
                    candidates.Add(Path.Combine(p, target));
            }
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var file = Path.GetFileName(c);
                if (!file.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(c)) return Path.GetFullPath(c);
            }
            return null;
        }
        catch { return null; }
    }

    private static string? FindViaWhere(string command)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var bin = isWindows
                ? (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"))
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe") : "where")
                : "which";
            var psi = new ProcessStartInfo { FileName = bin, Arguments = command, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            if (proc.ExitCode != 0) return null;
            var first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return Path.GetFullPath(first);
            return null;
        }
        catch { return null; }
    }

    public static string? FindOnPath(string command)
    {
        try
        {
            var fileName = command.Trim().Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return Path.GetFullPath(fileName);
            var isWindows = OperatingSystem.IsWindows();
            var rawPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] pathexts = [];
            if (isWindows)
            {
                var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
                pathexts = pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            foreach (var rawDir in rawPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(rawDir)) continue;
                var expanded = Environment.ExpandEnvironmentVariables(rawDir.Trim().Trim('"').Trim());
                if (string.IsNullOrWhiteSpace(expanded)) continue;
                string dir;
                try { dir = Path.GetFullPath(expanded); } catch { dir = expanded; }
                if (!Directory.Exists(dir)) continue;
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                if (!isWindows) continue;
                if (Path.HasExtension(fileName)) continue;
                foreach (var ext in pathexts)
                {
                    var withExt = candidate + (ext.StartsWith(".") ? ext : "." + ext);
                    if (File.Exists(withExt)) return Path.GetFullPath(withExt);
                }
            }
            // Unix fallback: if command had a Windows extension, retry without it.
            if (!isWindows && (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                var stripped = fileName[..^4];
                foreach (var rawDir in rawPath.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(rawDir)) continue;
                    var expanded = Environment.ExpandEnvironmentVariables(rawDir.Trim().Trim('"').Trim());
                    if (string.IsNullOrWhiteSpace(expanded)) continue;
                    string dir;
                    try { dir = Path.GetFullPath(expanded); } catch { dir = expanded; }
                    if (!Directory.Exists(dir)) continue;
                    var candidate = Path.Combine(dir, stripped);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return null;
        }
        catch { return null; }
    }
}

internal static class LspProviderRegistry
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, HashSet<string>> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LspDependencyStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _statusMessage = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _installedVersion = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _executablePath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> _isManaged = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterConsumer(string providerId, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(extensionId)) return;
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_consumers.TryGetValue(pid, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _consumers[pid] = set;
            }
            set.Add(extensionId);
        }
    }

    public static void UnregisterConsumer(string providerId, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (_consumers.TryGetValue(pid, out var set))
            {
                set.Remove(extensionId);
                if (set.Count == 0) _consumers.Remove(pid);
            }
        }
    }

    public static IReadOnlyCollection<string> GetConsumers(string providerId)
    {
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock) return _consumers.TryGetValue(pid, out var set) ? set.ToList().AsReadOnly() : Array.Empty<string>();
    }

    public static bool HasOtherConsumers(string providerId, string excludingExtensionId)
    {
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_consumers.TryGetValue(pid, out var set)) return false;
            return set.Any(id => !id.Equals(excludingExtensionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static void SetStatus(string providerId, LspDependencyStatus status, string? message = null, string? version = null, string? exePath = null, bool isManaged = false)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _status[pid] = status;
            _statusMessage[pid] = message;
            if (version != null) _installedVersion[pid] = version;
            if (exePath != null) _executablePath[pid] = exePath;
            _isManaged[pid] = isManaged;
        }
    }

    public static (LspDependencyStatus status, string? message, string? version, string? exePath, bool isManaged) GetProviderInfo(string providerId)
    {
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _status.TryGetValue(pid, out var st);
            _statusMessage.TryGetValue(pid, out var msg);
            _installedVersion.TryGetValue(pid, out var ver);
            _executablePath.TryGetValue(pid, out var exe);
            _isManaged.TryGetValue(pid, out var managed);
            return (st, msg, ver, exe, managed);
        }
    }

    public static void RefreshFromLoadedExtensions(IEnumerable<LoadedExtension> extensions)
    {
        lock (_lock) _consumers.Clear();
        foreach (var ext in extensions)
        {
            foreach (var cfg in ext.AllLspConfigurations)
                RegisterConsumer(cfg.EffectiveProviderId, ext.Id);
        }
    }
}

internal static class LspServerResolver
{
    public static Task<LspResolution> ResolveAsync(
        LspConfiguration cfg,
        AppSettings? settings = null,
        string extensionId = "",
        CancellationToken ct = default)
        => ResolveInternalAsync(cfg, extensionId, settings, ct);

    public static async Task<LspResolution> ResolveAsync(
        LoadedExtension extension,
        AppSettings? settings = null,
        CancellationToken ct = default)
    {
        var cfg = extension.Lsp;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Command))
        {
            // Try first from Lsps collection if Lsp is null but Lsps has entries
            if (extension.Lsps.Count > 0) cfg = extension.Lsps[0];
        }
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Command))
            return new(LspServerSource.Missing, null, cfg ?? new LspConfiguration(), null, "No LSP configured", false);
        return await ResolveInternalAsync(cfg, extension.Id, settings, ct).ConfigureAwait(false);
    }

    private static async Task<LspResolution> ResolveInternalAsync(
        LspConfiguration cfg,
        string extensionId,
        AppSettings? settings,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(extensionId) && settings != null && settings.LspDisabledLanguages.TryGetValue(extensionId, out var disabled) && disabled)
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

        // 1.
        string? overrideKey = null;
        string? ov = null;
        if (settings != null)
        {
            if (!string.IsNullOrWhiteSpace(extensionId) && settings.LspExecutableOverrides.TryGetValue(extensionId, out var ov1) && !string.IsNullOrWhiteSpace(ov1))
            { ov = ov1; overrideKey = extensionId; }
            else if (settings.LspExecutableOverrides.TryGetValue(cfg.EffectiveProviderId, out var ov2) && !string.IsNullOrWhiteSpace(ov2))
            { ov = ov2; overrideKey = cfg.EffectiveProviderId; }
        }
        if (!string.IsNullOrWhiteSpace(ov))
        {
            var trimmed = ov!.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                var resolved = CloneWithCommand(cfg, trimmed);
                var ver = await ProbeVersionAsync(trimmed, cfg.VersionArgs, ct).ConfigureAwait(false);
                if (!IsVersionCompatible(ver, cfg.Version))
                    return new(LspServerSource.Incompatible, trimmed, resolved, ver, $"Language server '{cfg.EffectiveProviderId}' version {ver ?? "unknown"} is incompatible with required {cfg.Version}.", cfg.AllowAutoInstall);
                return new(LspServerSource.UserOverride, trimmed, resolved, ver, null, false);
            }
            var found = LspRuntimeDetector.FindOnPath(trimmed);
            if (found != null)
            {
                var resolved = CloneWithCommand(cfg, found);
                var ver = await ProbeVersionAsync(found, cfg.VersionArgs, ct).ConfigureAwait(false);
                if (!IsVersionCompatible(ver, cfg.Version))
                    return new(LspServerSource.Incompatible, found, resolved, ver, $"Language server '{cfg.EffectiveProviderId}' version {ver ?? "unknown"} is incompatible with required {cfg.Version}.", cfg.AllowAutoInstall);
                return new(LspServerSource.UserOverride, found, resolved, ver, null, false);
            }
            // override points to non-existent – treat as error but fallback
            KodoDiagnostics.LogDebug($"LSP user override for {overrideKey} not found: {trimmed}");
        }

        // 2 & 3: Deterministic preference handling
        bool preferManaged = settings?.LspPreferManaged ?? true;
        bool preferSystem = settings?.LspPreferSystem ?? true;
        // Validate managed presence (requires actual executable, not
        var managedExe = LspInstallationManager.FindManagedExecutable(cfg, settings);
        bool managedExists = managedExe != null && File.Exists(managedExe);
        string? systemExe = null;
        if (cfg.AllowSystem && preferSystem)
            systemExe = LspInstallationManager.FindSystemExecutable(cfg);
        if (systemExe == null && cfg.AllowSystem && preferSystem && cfg.Command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var without = cfg.Command[..^4];
            systemExe = LspRuntimeDetector.FindOnPath(without);
        }
        bool systemExists = systemExe != null && File.Exists(systemExe);

        // Deterministic order: - UserOverride already returned
        async Task<LspResolution?> TryResolveManagedOrSystemAsync(string exe, LspServerSource source)
        {
            var resolved = CloneWithCommand(cfg, exe);
            var ver = await ProbeVersionAsync(exe, cfg.VersionArgs, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.Version) && !string.IsNullOrWhiteSpace(ver) && !IsVersionCompatible(ver, cfg.Version))
            {
                KodoDiagnostics.LogDebug($"LSP {source} {cfg.EffectiveProviderId} version {ver} incompatible with required {cfg.Version}");
                return new(LspServerSource.Incompatible, exe, resolved, ver, $"Language server '{cfg.EffectiveProviderId}' version {ver} is incompatible with required {cfg.Version}.", cfg.AllowAutoInstall);
            }
            // Version unknown treated as compatible (don't block valid install)
            if (!string.IsNullOrWhiteSpace(cfg.Version) && string.IsNullOrWhiteSpace(ver))
                KodoDiagnostics.LogDebug($"LSP {source} {cfg.EffectiveProviderId} version unknown; treating as compatible with required {cfg.Version}");
            return new(source, exe, resolved, ver, null, false);
        }

        if (preferManaged && preferSystem)
        {
            if (managedExists)
            {
                var r = await TryResolveManagedOrSystemAsync(managedExe!, LspServerSource.Managed).ConfigureAwait(false);
                return r!;
            }
            if (systemExists)
            {
                var r = await TryResolveManagedOrSystemAsync(systemExe!, LspServerSource.System).ConfigureAwait(false);
                return r!;
            }
        }
        else if (preferManaged && !preferSystem)
        {
            if (managedExists)
            {
                var r = await TryResolveManagedOrSystemAsync(managedExe!, LspServerSource.Managed).ConfigureAwait(false);
                return r!;
            }
            // System explicitly disabled – don't fall back
        }
        else if (!preferManaged && preferSystem)
        {
            if (systemExists)
            {
                var r = await TryResolveManagedOrSystemAsync(systemExe!, LspServerSource.System).ConfigureAwait(false);
                return r!;
            }
            if (managedExists)
            {
                var r = await TryResolveManagedOrSystemAsync(managedExe!, LspServerSource.Managed).ConfigureAwait(false);
                return r!;
            }
        }
        // If both disabled, skip both and go to installable/manual

        // 4. Installable? Enforce SHA-256 for github artifacts
        bool isGithub = (cfg.InstallMethod?.Equals("github", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cfg.DownloadUrl?.Contains("github.com", StringComparison.OrdinalIgnoreCase) ?? false);
        bool hasSha = !string.IsNullOrWhiteSpace(cfg.Sha256);
        bool canInstall = cfg.AllowAutoInstall && !string.Equals(cfg.InstallMethod, "manual", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(cfg.DownloadUrl) || !string.IsNullOrWhiteSpace(cfg.PackageName) || cfg.InstallMethod == "npm" || cfg.InstallMethod == "github" || cfg.InstallMethod == "dotnet");
        // For github/standalone without checksum, treat as manual –
        if (canInstall && isGithub && !hasSha)
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' download requires SHA-256 verification (no checksum provided). Please install manually from {cfg.DownloadUrl} or update provider metadata.", false);
        }
        if (canInstall && !isGithub && !hasSha && !string.IsNullOrWhiteSpace(cfg.DownloadUrl) && cfg.InstallMethod != "npm" && cfg.InstallMethod != "dotnet")
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
            LspServerSource.Incompatible => $"Incompatible ({r.Version ?? "unknown"} vs required {r.ResolvedConfiguration.Version})",
            LspServerSource.RuntimeMissing => $"Runtime missing: {r.Error}",
            LspServerSource.ManualRequired => "Manual install required",
            LspServerSource.Disabled => "Disabled",
            _ => "Not installed"
        };
    }

    internal static bool IsVersionCompatible(string? installedRaw, string? required)
    {
        if (string.IsNullOrWhiteSpace(required)) return true;
        if (string.IsNullOrWhiteSpace(installedRaw)) return true; // unknown -> don't block
        try
        {
            // Extract versions via same logic as LspRuntimeDetector
            string Extract(string s)
            {
                var m = System.Text.RegularExpressions.Regex.Match(s, @"v?(\d+\.\d+(?:\.\d+)?)");
                if (m.Success) return m.Groups[1].Value;
                return s.Trim().TrimStart('v','V').Split(' ')[0];
            }
            var inst = Extract(installedRaw);
            var req = Extract(required);
            var f = ParseVersionNumbers(inst);
            var r = ParseVersionNumbers(req);
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

    private static int[] ParseVersionNumbers(string v)
    {
        var list = new List<int>();
        var cur = "";
        foreach (var c in v)
        {
            if (char.IsDigit(c)) cur += c;
            else if (c == '.' && cur.Length > 0) { if (int.TryParse(cur, out var n)) list.Add(n); cur = ""; }
            else if (cur.Length > 0) break;
        }
        if (cur.Length > 0 && int.TryParse(cur, out var last)) list.Add(last);
        if (list.Count == 0) return new[] { 0 };
        return list.ToArray();
    }
}
