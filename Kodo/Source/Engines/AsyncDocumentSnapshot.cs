// Licensed under the GNU GPL-v3.0
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace Kodo.Editor;

internal static class EditorRendererSnapshotScheduler
{
    public static readonly SemaphoreSlim LargeDocumentBuildGate = new(1, 1);
}

internal sealed class AsyncDocumentSnapshot<T> where T : class
{
    private readonly object _gate = new();
    private TextDocument? _document;
    private ITextSourceVersion? _version;
    private T? _snapshot;
    private CancellationTokenSource? _pending;

    public T? GetOrSchedule(TextDocument document, Func<string, int, CancellationToken, T> build, Action completed)
    {
        var version = document.Version;
        var documentLength = document.TextLength;
        var documentLineCount = Math.Max(1, document.LineCount);
        lock (_gate)
        {
            if (ReferenceEquals(_document, document) && ReferenceEquals(_version, version)) return _snapshot;

            _pending?.Cancel();
            _pending?.Dispose();
            _version = version;
            _document = document;
            _snapshot = null;
            var source = EditorTextSnapshotStore.GetSnapshot(document);
            var cancellation = _pending = new CancellationTokenSource();
            var token = cancellation.Token;
            _ = BuildSnapshotAfterDelayAsync(document, version, documentLength, documentLineCount, source, build, completed, cancellation);
            return null;
        }
    }

    private async Task BuildSnapshotAfterDelayAsync(
        TextDocument document,
        ITextSourceVersion version,
        int documentLength,
        int documentLineCount,
        EditorTextSnapshot source,
        Func<string, int, CancellationToken, T> build,
        Action completed,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var delay = documentLength > 500_000 || documentLineCount >= 50_000
                ? 1_000
                : documentLength > PerformanceBudget.SnapshotSkipThreshold || documentLineCount >= 5_000
                    ? 650
                    : 100;
            await Task.Delay(delay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var serializeLargeBuild = documentLength > PerformanceBudget.SnapshotSkipThreshold || documentLineCount >= 5_000;
            var queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (serializeLargeBuild)
                await EditorRendererSnapshotScheduler.LargeDocumentBuildGate.WaitAsync(token).ConfigureAwait(false);
            var queueElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt);
            try
            {
                var snapshotStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var text = await source.GetTextAsync(token).ConfigureAwait(false);
                var materializeElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(snapshotStartedAt);
                var parseStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = await EditorBackgroundWork.RunAsync(
                    backgroundToken => build(text, documentLineCount, backgroundToken), token).ConfigureAwait(false);
                var parseElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(parseStartedAt);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(snapshotStartedAt);
                KodoDiagnostics.ReportSlowStage("editor renderer snapshot materialize", (long)materializeElapsed.TotalMilliseconds, 16,
                    $"type={typeof(T).Name} len={documentLength}");
                KodoDiagnostics.ReportSlowStage("editor renderer snapshot queue", (long)queueElapsed.TotalMilliseconds, 16,
                    $"type={typeof(T).Name} len={documentLength}");
                KodoDiagnostics.ReportSlowStage("editor renderer snapshot parse", (long)parseElapsed.TotalMilliseconds, 16,
                    $"type={typeof(T).Name} len={documentLength}");
                KodoDiagnostics.ReportSlowStage("editor renderer snapshot build", (long)elapsed.TotalMilliseconds, 16,
                    $"type={typeof(T).Name} len={documentLength}");
                token.ThrowIfCancellationRequested();
                Dispatcher.UIThread.Post(() =>
                {
                    lock (_gate)
                    {
                        if (token.IsCancellationRequested || !ReferenceEquals(_pending, cancellation) ||
                            !ReferenceEquals(_document, document) ||
                            !ReferenceEquals(document.Version, version)) return;
                        _snapshot = result;
                        _pending = null;
                        cancellation.Dispose();
                    }
                    completed();
                }, DispatcherPriority.Background);
            }
            finally
            {
                if (serializeLargeBuild) EditorRendererSnapshotScheduler.LargeDocumentBuildGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Editor background snapshot failed.", ex);
            Dispatcher.UIThread.Post(() =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_pending, cancellation)) return;
                    _pending = null;
                    cancellation.Dispose();
                }
            }, DispatcherPriority.Background);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
            _document = null;
            _version = null;
            _snapshot = null;
        }
    }
}
