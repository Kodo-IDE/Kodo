// Licensed under the GNU GPL-v3.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

internal sealed class InsightWorkerGateway
{
    public static InsightWorkerGateway Default { get; } = new();

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<WorkerResponse>> _pending = new();
    private readonly CancellationTokenSource _dispose = new();
    private Process? _process;
    private Func<IReadOnlyList<LoadedExtension>>? _registryProvider;
    private volatile bool _registryDirty = true;
    private volatile bool _ready;
    private int _nextId;
    private int _strikes;
    private Task? _pump;

    private const int MaxStrikes = 2;

    internal static Func<string?>? SpawnEntryPointOverride { get; set; }
    internal static Action<string>? RequestTrace { get; set; }

    public bool Healthy =>
        Volatile.Read(ref _strikes) < MaxStrikes &&
        _ready &&
        _process is { HasExited: false };

    private static bool RunsFromDll
    {
        get
        {
            var proc = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(proc)) return true;
            var fileName = Path.GetFileNameWithoutExtension(proc);
            return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? EntryAssemblyPath =>
        Assembly.GetEntryAssembly()?.GetName().Name is { Length: > 0 } name
            ? Path.Combine(AppContext.BaseDirectory, name + ".dll")
            : null;

    public void SetRegistryProvider(Func<IReadOnlyList<LoadedExtension>> provider)
    {
        _registryProvider = provider;
        MarkRegistryDirty();
    }

    public void MarkRegistryDirty() => _registryDirty = true;

    public async Task<List<DeadCodeSpan>> FindDeadCodeAsync(
        InsightEngine engine,
        string documentText,
        LoadedExtension? languageExtension,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _strikes) >= MaxStrikes)
            return InProcessDead(engine, documentText, languageExtension, folderPath, filePath, cancellationToken);
        try
        {
            var response = await RequestAsync("dead", filePath, languageExtension, documentText, folderPath, cancellationToken).ConfigureAwait(false);
            if (!response.Ok || response.Dead is null) throw new InvalidOperationException(response.Message ?? "worker dead-code request failed");
            var spans = new List<DeadCodeSpan>(response.Dead.Count);
            foreach (var s in response.Dead)
                spans.Add(new DeadCodeSpan(s.Start, s.Length, s.Reason));
            return spans;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Insight worker request failed; falling back in-process.", ex);
            RequestTrace?.Invoke("EXC dead " + ex.GetType().Name + ": " + ex.Message);
            return InProcessDead(engine, documentText, languageExtension, folderPath, filePath, cancellationToken);
        }
    }

    public async Task<List<ErrorSpan>> FindErrorsAsync(
        InsightEngine engine,
        string documentText,
        LoadedExtension? languageExtension,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _strikes) >= MaxStrikes)
            return InProcessErrors(engine, documentText, languageExtension, cancellationToken);
        try
        {
            var response = await RequestAsync("errs", null, languageExtension, documentText, null, cancellationToken).ConfigureAwait(false);
            if (!response.Ok || response.Errors is null) throw new InvalidOperationException(response.Message ?? "worker diagnostics request failed");
            var spans = new List<ErrorSpan>(response.Errors.Count);
            foreach (var s in response.Errors)
                spans.Add(new ErrorSpan(s.Start, s.Length, s.Message, s.Severity, s.Code, s.Source));
            return spans;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Insight worker diagnostics request failed; falling back in-process.", ex);
            RequestTrace?.Invoke("EXC errs " + ex.GetType().Name + ": " + ex.Message);
            return InProcessErrors(engine, documentText, languageExtension, cancellationToken);
        }
    }

    public async Task<bool> ScanRoundAsync(
        InsightEngine engine,
        string fileKey,
        string documentText,
        LoadedExtension? languageExtension,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _strikes) >= MaxStrikes)
            return false;
        try
        {
            var response = await RequestAsync("scan", fileKey, languageExtension, documentText, null, cancellationToken).ConfigureAwait(false);
            if (!response.Ok) throw new InvalidOperationException(response.Message ?? "worker scan request failed");
            engine.AdoptFileVariables(fileKey, response.Variables ?? []);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Insight worker scan request failed; adopting skipped.", ex);
            return false;
        }
    }

    public async Task ForgetAsync(InsightEngine engine, string fileKey)
    {
        engine.ForgetFile(fileKey);
        if (Volatile.Read(ref _strikes) >= MaxStrikes) return;
        try
        {
            await RequestAsync("forget", fileKey, null, null, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static List<DeadCodeSpan> InProcessDead(InsightEngine engine, string documentText, LoadedExtension? ext, string? folderPath, string? filePath, CancellationToken ct)
        => engine.FindDeadCode(documentText, ext, folderPath, filePath, ct, filePath);

    private static List<ErrorSpan> InProcessErrors(InsightEngine engine, string documentText, LoadedExtension? ext, CancellationToken ct)
        => engine.FindErrors(documentText, ext, null, ct);

    private async Task<WorkerResponse> RequestAsync(
        string op,
        string? key,
        LoadedExtension? extension,
        string? text,
        string? folderPath,
        CancellationToken cancellationToken)
    {
        var process = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (process is null || _process is null || _process.HasExited)
            throw new InvalidOperationException("insight worker is not running");

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new WorkerRequest
            {
                Id = id,
                Op = op,
                Key = key,
                Extension = extension is null ? null : WorkerExtensionSnapshot.From(extension),
                FolderPath = folderPath
            };
            var textBytes = text is null ? 0 : Encoding.UTF8.GetByteCount(text);
            if (text is not null)
            {
                request.TextByteLength = textBytes;
            }
            if (_registryDirty && _registryProvider is not null)
            {
                request.Registry = new List<WorkerExtensionSnapshot>();
                foreach (var ext in _registryProvider())
                    request.Registry.Add(WorkerExtensionSnapshot.From(ext));
                _registryDirty = false;
            }

            var completion = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending)
            {
                _pending[id] = completion;
            }
            var payload = JsonSerializer.Serialize(request, WorkerExtensionSnapshot.Options);
            RequestTrace?.Invoke("REQ " + (payload.Length > 300 ? payload[..300] + "…(len " + payload.Length + ")" : payload));

            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n"), cancellationToken).ConfigureAwait(false);
            if (text is not null && textBytes > 0)
            {
                if (textBytes <= 1024)
                {
                    await stdin.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(textBytes);
                    try
                    {
                        var written = Encoding.UTF8.GetBytes(text, pooled.AsSpan(0, textBytes));
                        await stdin.WriteAsync(pooled.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
                    }
                }
            }
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
            var finished = await Task.WhenAny(
                completion.Task,
                Task.Delay(TimeSpan.FromSeconds(60), cancellationToken)).ConfigureAwait(false);
            if (finished == completion.Task)
            {
                var response = await completion.Task.ConfigureAwait(false);
                RequestTrace?.Invoke("RESP id=" + response.Id + " ok=" + response.Ok + " msg=" + response.Message + " dead=" + (response.Dead?.Count ?? -1) + " errs=" + (response.Errors?.Count ?? -1) + " vars=" + (response.Variables?.Count ?? -1));
                return response;
            }
                bool removed;
                lock (_pending)
                {
                    removed = _pending.Remove(id);
                }
                if (removed) ThrowCanceled(cancellationToken);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static void ThrowCanceled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("insight worker request did not complete");
    }

    private async Task<Process?> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (Healthy) return _process;
        if (Volatile.Read(ref _strikes) >= MaxStrikes) return null;
        if (_process is not null)
        {
            TryKillWorker();
        }
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Healthy) return _process;
            var (entryPath, args) = ResolveSpawnTarget();
            if (entryPath is null) return null;

            var psi = new ProcessStartInfo
            {
                FileName = entryPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(entryPath) ?? Environment.CurrentDirectory,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            psi.ArgumentList.Add(args.Payload);
            if (!string.IsNullOrEmpty(args.Flag)) psi.ArgumentList.Add(args.Flag);

            var process = Process.Start(psi);
            if (process is null) return null;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnWorkerDied();

            _process?.Dispose();
            _process = process;
            lock (_pending)
            {
                _pending.Clear();
            }
            _ready = true;
            _pump = Task.Run(() => ReadLoopAsync(process));
            return process;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Insight worker failed to start; using in-process engine.", ex);
            Volatile.Write(ref _strikes, Volatile.Read(ref _strikes) + 1);
            return null;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static (string? Path, (string Payload, string Flag) Args) ResolveSpawnTarget()
    {
        var executable = Environment.ProcessPath;
        var entry = EntryAssemblyPath;
        if (SpawnEntryPointOverride is { } overrideEntry && overrideEntry() is { Length: > 0 } customEntry)
        {
            entry = customEntry;
        }
        if (RunsFromDll && !string.IsNullOrEmpty(entry))
        {
            return (executable is { Length: > 0 } dotnetPath ? dotnetPath : "dotnet", (entry, "--insight-worker"));
        }
        if (executable is { Length: > 0 } apphost)
        {
            return (apphost, (apphost, "--insight-worker"));
        }
        return (null, (string.Empty, string.Empty));
    }

    private void OnWorkerDied()
    {
        _ready = false;
        if (Volatile.Read(ref _strikes) < MaxStrikes)
        {
            Volatile.Write(ref _strikes, Volatile.Read(ref _strikes) + 1);
            KodoDiagnostics.LogDebug($"Insight worker exited unexpectedly (strikes={Volatile.Read(ref _strikes)}).");
        }
        FailAllPending("insight worker exited unexpectedly");
    }

    private void FailAllPending(string message)
    {
        TaskCompletionSource<WorkerResponse>[] callbacks;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            callbacks = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var completion in callbacks)
            completion.TrySetException(new InvalidOperationException(message));
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (_dispose.IsCancellationRequested) return;
                WorkerResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<WorkerResponse>(line, WorkerExtensionSnapshot.Options);
                }
                catch
                {
                    continue;
                }
                if (response is null) continue;
                if (response.Id == 0)
                {
                    _ready = true;
                    continue;
                }
                TaskCompletionSource<WorkerResponse>? completion;
                lock (_pending)
                {
                    _pending.Remove(response.Id, out completion);
                }
                if (completion is not null)
                    completion.TrySetResult(response);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _ready = false;
            FailAllPending("insight worker output pipe ended");
        }
    }

    private void TryKillWorker()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try
        {
            process.Dispose();
        }
        catch { }
    }
}
