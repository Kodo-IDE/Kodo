using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Kodo;

public sealed class LanguageWorker : IDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly Dictionary<string, LanguageDocumentSnapshot> _documents = new(StringComparer.OrdinalIgnoreCase);

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
            return new(request.Method, request.Document.Version, null, "worker unavailable");

        try
        {
            return completion.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
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
            _documents[request.Document.Uri] = request.Document;
            return request.Document;
        });

    public LanguageWorkerResponse Change(string uri, long version, IReadOnlyList<LanguageTextChange> changes, CancellationToken cancellationToken = default) =>
        Send(new("textDocument/didChange", new(uri, version, string.Empty), Changes: changes), request =>
        {
            if (!_documents.TryGetValue(uri, out var current)) return null;
            var text = current.Text;
            foreach (var change in request.Changes ?? [])
            {
                if (change.Start < 0 || change.Start > text.Length || change.Length < 0 || change.Start + change.Length > text.Length) continue;
                text = text.Remove(change.Start, change.Length).Insert(change.Start, change.NewText);
            }
            var updated = new LanguageDocumentSnapshot(uri, version, text);
            _documents[uri] = updated;
            return updated;
        }, cancellationToken);

    public LanguageWorkerResponse Close(string uri, long version = 0) =>
        Send(new("textDocument/didClose", new(uri, version, string.Empty)), request => _documents.Remove(request.Document.Uri));

    public LanguageDocumentSnapshot? GetDocument(string uri) => _documents.TryGetValue(uri, out var document) ? document : null;

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
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private sealed record WorkItem(
        LanguageWorkerRequest Request,
        Func<LanguageWorkerRequest, object?> Handler,
        TaskCompletionSource<LanguageWorkerResponse> Completion);
}
