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
    private int _stderrLoggedChars;
    private int _stderrTruncationLogged;
    private bool _disposed;
    private bool _shutdownRequested;
    private string _shutdownReason = "";

    public bool IsStarted
    {
        get
        {
            lock (_writeGate)
            {
                if (_transportError is not null || _process is null) return false;
                try { return !_process.HasExited; }
                catch { return false; }
            }
        }
    }
    public bool IsInitialized { get; private set; }
    internal LspConfiguration Configuration => _config;

    private string? ResolvedExecutablePath => string.IsNullOrWhiteSpace(_config.Command) ? null : _config.Command;
    internal string ClientWorkspaceRoot => _workspaceRoot;
    public string Id => _config.Command;
    public IReadOnlyList<string> SemanticTokenTypes { get; private set; } = Array.Empty<string>();

    public string PositionEncoding { get; private set; } = "utf-16";

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
            "textDocument/codeAction" => "codeActionProvider",
            "textDocument/completion" => "completionProvider",
            "textDocument/hover" => "hoverProvider",
            "textDocument/references" => "referencesProvider",
            "textDocument/rename" => "renameProvider",
            "textDocument/formatting" => "documentFormattingProvider",
            "textDocument/rangeFormatting" => "documentRangeFormattingProvider",
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
        cancellationToken.ThrowIfCancellationRequested();
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
            foreach (var arg in LspInstallationManager.ResolveLaunchPlan(_config, null, ResolvedExecutablePath).PrefixArgs)
                psi.ArgumentList.Add(ExpandPlaceholder(arg));
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
            cancellationToken.ThrowIfCancellationRequested();
            if (!_process.Start())
                throw new InvalidOperationException($"Failed to start LSP '{_config.Command}'");
        }
        catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
        {
            KodoDiagnostics.LogDebug($"LSP start failed for '{_config.Command}'", ex);
            throw new FileNotFoundException($"Language server '{_config.Command}' could not be started. Check that it is installed and available on PATH.", ex);
        }

        try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _writer = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: false) { AutoFlush = false };
        _readLoop = Task.Run(() => ReadLoopAsync(_process.StandardOutput.BaseStream, _cts.Token), _cts.Token);
        _stderrLoop = Task.Run(() => StderrLoopAsync(_process.StandardError, _cts.Token), _cts.Token);

        await Task.Yield();
        if (cancellationToken.IsCancellationRequested)
        {
            var canceled = new OperationCanceledException(cancellationToken);
            TerminateTransport(canceled);
            cancellationToken.ThrowIfCancellationRequested();
        }
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
                    ["general"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["positionEncodings"] = new[] { "utf-16" }
                    },
                    ["workspace"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["configuration"] = true,
                        ["workspaceFolders"] = true,
                        ["workspaceEdit"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["documentChanges"] = true
                        }
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
                        ["references"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                        ["codeAction"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["codeActionLiteralSupport"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["codeActionKind"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["valueSet"] = new[]
                                    {
                                        "", "quickfix", "refactor", "refactor.extract", "refactor.inline",
                                        "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"
                                    }
                                }
                            },
                            ["dataSupport"] = true,
                            ["disabledSupport"] = true,
                            ["isPreferredSupport"] = true,
                            ["resolveSupport"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["properties"] = new[] { "edit" }
                            }
                        },
                        ["rename"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["prepareSupport"] = true
                        },
                        ["formatting"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                        ["rangeFormatting"] = new Dictionary<string, object?>(StringComparer.Ordinal),
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
            if (result.HasValue && result.Value.ValueKind == JsonValueKind.Object)
            {
                if (result.Value.TryGetProperty("positionEncoding", out var encoding) && encoding.ValueKind == JsonValueKind.String)
                {
                    PositionEncoding = encoding.GetString() ?? "utf-16";
                    if (!string.Equals(PositionEncoding, "utf-16", StringComparison.OrdinalIgnoreCase))
                        KodoDiagnostics.LogWarning("LspClient.StartAsync",
                            new InvalidOperationException(
                                $"Server '{_config.Command}' negotiated positionEncoding '{PositionEncoding}', but Kodo uses utf-16. Positions in non-ASCII text may be inaccurate."),
                            operation: "LSP position encoding negotiation");
                }
                else
                {
                    PositionEncoding = "utf-16";
                }

                if (result.Value.TryGetProperty("capabilities", out var caps))
                {
                ServerCapabilities = caps.Clone();
                if (caps.TryGetProperty("semanticTokensProvider", out var semanticProvider) && semanticProvider.ValueKind == JsonValueKind.Object &&
                    semanticProvider.TryGetProperty("legend", out var legend) && legend.TryGetProperty("tokenTypes", out var tokenTypes) && tokenTypes.ValueKind == JsonValueKind.Array)
                    SemanticTokenTypes = tokenTypes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).ToArray();
                }
            }

            await SendNotificationAsync("initialized", new Dictionary<string, object?>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
            KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialized. Server caps: {LspProtocol.Preview(ServerCapabilities?.GetRawText() ?? "<none>")}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
                KodoDiagnostics.LogDebug($"LSP '{_config.Command}' initialization failed", ex);
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
                if (KodoDiagnostics.VerboseLoggingEnabled)
                {
                    try
                    {
                        var preview = json.Length > 800 ? json.Substring(0, 800) + "..." : json;
                        KodoDiagnostics.LogDebug($"LSP request id={id} method={method} json={preview}");
                    }
                    catch { }
                }
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
        if (KodoDiagnostics.VerboseLoggingEnabled)
        {
            var stack = Environment.StackTrace.Split('\n').Take(8).Select(s => s.Trim()).Where(s => s.Contains("Kodo.")).Take(3);
            KodoDiagnostics.LogDebug($"LSP shutdown requested reason={reason} command={_config.Command} pid={_process?.Id} stack={string.Join(" | ", stack)}");
        }
        else
        {
            KodoDiagnostics.LogDebug($"LSP shutdown requested reason={reason} command={_config.Command} pid={_process?.Id}");
        }
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

                while (!foundHeaderEnd)
                {
                    if (!skipRead)
                    {
                        var read = await stdout.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (read == 0) return;
                        headerAccum.Write(buffer, 0, read);
                    }
                    skipRead = false;
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
                        var remainingInAccum = (int)headerAccum.Length - headerBytesLen;
                        if (contentLength < 0)
                        {
                            KodoDiagnostics.LogDebug($"LSP missing Content-Length headers: {string.Join("|", headerLines)}");
                            headerAccum.SetLength(0);
                            if (remainingInAccum > 0) headerAccum.Write(headerAccum.GetBuffer(), headerBytesLen, remainingInAccum);
                            break;
                        }
                        if (contentLength == 0) { headerAccum.SetLength(0); if (remainingInAccum > 0) headerAccum.Write(headerAccum.GetBuffer(), headerBytesLen, remainingInAccum); foundHeaderEnd = true; break; }
                        if (contentLength > 8 * 1024 * 1024)
                        {
                            KodoDiagnostics.LogDebug($"LSP message too large: {contentLength}");
                            int toDrain = contentLength - remainingInAccum;
                            var drainBuf = new byte[8192];
                            while (toDrain > 0)
                            {
                                var r = await stdout.ReadAsync(drainBuf, 0, Math.Min(drainBuf.Length, toDrain), ct).ConfigureAwait(false);
                                if (r == 0) return; toDrain -= r;
                            }
                            headerAccum.SetLength(0);
                            if (remainingInAccum > 0 && remainingInAccum > contentLength)
                            {
                                var extra = remainingInAccum - contentLength;
                                headerAccum.Write(headerAccum.GetBuffer(), headerBytesLen + contentLength, extra);
                            }
                            foundHeaderEnd = true;
                            break;
                        }
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
            using var doc = JsonDocument.Parse(json);
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
            KodoDiagnostics.LogDebug($"LSP invalid JSON: {LspProtocol.Preview(json)}", ex);
            return;
        }
        if (id.HasValue && method is null)
        {
            try
            {
                if (KodoDiagnostics.VerboseLoggingEnabled)
                {
                    var preview = json.Length > 800 ? json.Substring(0, 800) + "..." : json;
                    KodoDiagnostics.LogDebug($"LSP response id={id} json={preview}");
                }
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
                    try { KodoDiagnostics.LogDebug($"LSP workspace/configuration response: {LspProtocol.Preview(JsonSerializer.Serialize(result), 500)}"); } catch { }
                }
                else if (method == "window/showMessage" || method == "window/logMessage")
                {
                    if (KodoDiagnostics.VerboseLoggingEnabled &&
                        root.Value.TryGetProperty("params", out var p2))
                        KodoDiagnostics.LogDebug($"LSP {method}: {LspProtocol.Preview(p2.GetRawText())}");
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
                if (!string.IsNullOrWhiteSpace(section) && _config.InitializationOptions is JsonElement initOpts && initOpts.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetSection(initOpts, section, out var sectionValue))
                    {
                        try { results.Add(JsonSerializer.Deserialize<object>(sectionValue.GetRawText())); continue; }
                        catch { }
                    }
                    if (section == "python" && initOpts.TryGetProperty("python", out var py))
                    {
                        try { results.Add(JsonSerializer.Deserialize<object>(py.GetRawText())); continue; }
                        catch { }
                    }
                }
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
                if (!string.IsNullOrWhiteSpace(section))
                {
                    var lower = section.ToLowerInvariant();
                    if (lower == "python" || lower == "python.analysis" || lower == "pyright")
                    {
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
                var loggedChars = Interlocked.Add(ref _stderrLoggedChars, text.Length);
                if (loggedChars <= 8192)
                {
                    if (KodoDiagnostics.VerboseLoggingEnabled)
                        KodoDiagnostics.LogDebug($"LSP stderr [{_config.Command}]: {LspProtocol.Preview(text.Trim(), 1000)}");
                }
                else if (loggedChars - text.Length < 8192 && Interlocked.Exchange(ref _stderrTruncationLogged, 1) == 0)
                    KodoDiagnostics.LogDebug($"LSP stderr [{_config.Command}] logging truncated after 8192 characters.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("LSP stderr loop failed", ex); }
    }

    private (string fileName, bool useCmdWrapper, string cmdArgs) ResolveProcessStartInfo()
    {
        var plan = LspInstallationManager.ResolveLaunchPlan(_config, null, ResolvedExecutablePath);

        if (plan.PrefixArgs.Count > 0)
            return (plan.FileName, false, string.Empty);

        var command = plan.FileName;
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
