// Licensed under GPL v3.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Kodo;

public sealed class LanguageWorker : IDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly Dictionary<string, LanguageDocumentSnapshot> _documents = new(FileSystemPaths.Comparer);
    private readonly object _docLock = new();
    private bool _disposed;

    public LanguageWorker()
    {
        _loop = Task.Run(ProcessAsync);
    }

    public LanguageWorkerResponse Send(
        LanguageWorkerRequest request,
        Func<LanguageWorkerRequest, object?> handler,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(request.Method, request.Document.Version, null, "cancelled");

        var completion = new TaskCompletionSource<LanguageWorkerResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new(request, handler, completion)))
            return new(request.Method, request.Document.Version, null, "worker busy - dropped oldest");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(120));
            return completion.Task.WaitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new(request.Method, request.Document.Version, null, "cancelled");
        }
    }

    public async ValueTask<LanguageWorkerResponse> SendAsync(
        LanguageWorkerRequest request,
        Func<LanguageWorkerRequest, object?> handler,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(request.Method, request.Document.Version, null, "cancelled");
        var completion = new TaskCompletionSource<LanguageWorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new(request, handler, completion)))
            return new(request.Method, request.Document.Version, null, "worker unavailable");
        try { return await completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return new(request.Method, request.Document.Version, null, "cancelled"); }
    }

    public LanguageWorkerResponse Open(LanguageDocumentSnapshot document) =>
        Send(new("textDocument/didOpen", document), request =>
        {
            lock (_docLock) { _documents[request.Document.Uri] = request.Document; }
            return request.Document;
        });

    public LanguageWorkerResponse Change(string uri, long version, IReadOnlyList<LanguageTextChange> changes, CancellationToken cancellationToken = default) =>
        Send(new("textDocument/didChange", new(uri, version, string.Empty), Changes: changes), request =>
        {
            lock (_docLock)
            {
                if (!_documents.TryGetValue(uri, out var current)) return null;
                var text = current.Text;
                if (request.Changes is null || request.Changes.Count == 0) return current;
                if (request.Changes.Count == 1)
                {
                    var c = request.Changes[0];
                    if (c.Start < 0 || c.Start > text.Length || c.Length < 0 || c.Start + c.Length > text.Length) return current;
                    text = string.Concat(text.AsSpan(0, c.Start), c.NewText, text.AsSpan(c.Start + c.Length));
                }
                else
                {
                    foreach (var change in request.Changes)
                    {
                        if (change.Start < 0 || change.Length < 0 || change.Start + change.Length > text.Length) return current;
                    }
                    for (var i = 0; i < request.Changes.Count - 1; i++)
                    {
                        var a = request.Changes[i];
                        var b = request.Changes[i + 1];
                        var aEnd = a.Start + a.Length;
                        var bEnd = b.Start + b.Length;
                        if (a.Start < bEnd && b.Start < aEnd) return current;
                    }
                    var sb = new System.Text.StringBuilder(text.Length + 256);
                    sb.Append(text);
                    foreach (var change in request.Changes.OrderByDescending(ch => ch.Start))
                    {
                        if (change.Start < 0 || change.Start > sb.Length || change.Length < 0 || change.Start + change.Length > sb.Length) continue;
                        sb.Remove(change.Start, change.Length);
                        sb.Insert(change.Start, change.NewText);
                    }
                    text = sb.ToString();
                }
                var updated = new LanguageDocumentSnapshot(uri, version, text);
                _documents[uri] = updated;
                return updated;
            }
        }, cancellationToken);

    public LanguageWorkerResponse Close(string uri, long version = 0) =>
        Send(new("textDocument/didClose", new(uri, version, string.Empty)), request =>
        {
            lock (_docLock) { return _documents.Remove(request.Document.Uri); }
        });

    public LanguageDocumentSnapshot? GetDocument(string uri)
    {
        lock (_docLock) { return _documents.TryGetValue(uri, out var document) ? document : null; }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    var result = work.Handler(work.Request);
                    work.Completion.TrySetResult(new(work.Request.Method, work.Request.Document.Version, result));
                }
                catch (Exception ex)
                {
                    work.Completion.TrySetResult(new(work.Request.Method, work.Request.Document.Version, null, ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try { _shutdown.Cancel(); } catch { }
        while (_queue.Reader.TryRead(out var pending))
        {
            try { pending.Completion.TrySetResult(new(pending.Request.Method, pending.Request.Document.Version, null, "disposed")); } catch { }
        }
        try { _shutdown.Dispose(); } catch { }
    }

    private sealed record WorkItem(
        LanguageWorkerRequest Request,
        Func<LanguageWorkerRequest, object?> Handler,
        TaskCompletionSource<LanguageWorkerResponse> Completion);
}
