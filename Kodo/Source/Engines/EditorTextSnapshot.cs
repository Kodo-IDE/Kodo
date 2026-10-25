// Licensed under the GNU GPL-v3.0
using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaEdit.Document;

namespace Kodo.Editor;

internal sealed class EditorTextSnapshot
{
    private const int MaximumRetainedTextLength = 8 * 1024 * 1024;

    private sealed class SnapshotBuild(Task<string> task, CancellationTokenSource cancellation)
    {
        public Task<string> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Waiters { get; set; }
    }

    private sealed class LineStartsBuild(Task<int[]> task, CancellationTokenSource cancellation)
    {
        public Task<int[]> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Waiters { get; set; }
    }

    private readonly ITextSource _source;
    private readonly int _lineCount;
    private readonly object _gate = new();
    private SnapshotBuild? _activeBuild;
    private LineStartsBuild? _activeLineStartsBuild;
    private string? _text;
    private int[]? _lineStarts;

    internal EditorTextSnapshot(TextDocument document)
    {
        Version = document.Version;
        _source = document.CreateSnapshot();
        _lineCount = Math.Max(1, document.LineCount);
    }

    public ITextSourceVersion Version { get; }
    internal ITextSource Source => _source;

    public Task<string> GetTextPrefixAsync(int maximumLength, CancellationToken cancellationToken = default)
    {
        var length = Math.Clamp(maximumLength, 0, _source.TextLength);
        return EditorBackgroundWork.RunAsync(_ => _source.GetText(0, length), cancellationToken);
    }

    public Task<int[]> GetLineStartsAsync(CancellationToken cancellationToken = default) => GetLineStartsCoreAsync(cancellationToken);

    private Task<int[]> GetLineStartsCoreAsync(CancellationToken cancellationToken)
    {
        LineStartsBuild build;
        lock (_gate)
        {
            if (_lineStarts is not null) return Task.FromResult(_lineStarts);
            if (_activeLineStartsBuild is null || _activeLineStartsBuild.Task.IsCanceled || _activeLineStartsBuild.Task.IsFaulted)
            {
                var cancellation = new CancellationTokenSource();
                var task = EditorBackgroundWork.RunAsync(ReadLineStarts, cancellation.Token);
                _ = task.ContinueWith(
                    _ => cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _activeLineStartsBuild = new LineStartsBuild(task, cancellation);
            }
            build = _activeLineStartsBuild;
            build.Waiters++;
        }
        return AwaitLineStartsAsync(build, cancellationToken);
    }

    private async Task<int[]> AwaitLineStartsAsync(LineStartsBuild build, CancellationToken cancellationToken)
    {
        try
        {
            var starts = await build.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) return _lineStarts ??= starts;
        }
        finally
        {
            lock (_gate)
            {
                build.Waiters = Math.Max(0, build.Waiters - 1);
                if (build.Waiters == 0 && !build.Task.IsCompleted)
                {
                    try { build.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                    if (ReferenceEquals(_activeLineStartsBuild, build)) _activeLineStartsBuild = null;
                }
                else if (build.Waiters == 0 && ReferenceEquals(_activeLineStartsBuild, build))
                {
                    _activeLineStartsBuild = null;
                }
            }
        }
    }

    public Task<string> GetTextAsync(CancellationToken cancellationToken = default)
    {
        SnapshotBuild build;
        lock (_gate)
        {
            if (_text is not null)
                return Task.FromResult(_text);

            if (_activeBuild is null || _activeBuild.Task.IsCanceled || _activeBuild.Task.IsFaulted)
            {
                var cancellation = new CancellationTokenSource();
                var task = EditorBackgroundWork.RunAsync(ReadText, cancellation.Token);
                _ = task.ContinueWith(
                    _ => cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _activeBuild = new SnapshotBuild(task, cancellation);
            }
            build = _activeBuild;
            build.Waiters++;
        }
        return AwaitTextAsync(build, cancellationToken);
    }

    private async Task<string> AwaitTextAsync(SnapshotBuild build, CancellationToken cancellationToken)
    {
        try
        {
            var text = await build.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_source.TextLength <= MaximumRetainedTextLength)
            {
                lock (_gate) _text ??= text;
            }
            return text;
        }
        finally
        {
            lock (_gate)
            {
                build.Waiters = Math.Max(0, build.Waiters - 1);
                if (build.Waiters == 0 && !build.Task.IsCompleted)
                {
                    try { build.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                    if (ReferenceEquals(_activeBuild, build)) _activeBuild = null;
                }
                else if (build.Waiters == 0 && _source.TextLength > MaximumRetainedTextLength &&
                         ReferenceEquals(_activeBuild, build))
                {
                    _activeBuild = null;
                }
            }
        }
    }

    private string ReadText(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = _source.TextLength;
        if (length == 0) return string.Empty;
        return string.Create(length, (_source, cancellationToken), static (destination, state) =>
        {
            var buffer = ArrayPool<char>.Shared.Rent(32 * 1024);
            try
            {
                using var reader = state._source.CreateReader();
                var offset = 0;
                while (offset < destination.Length)
                {
                    state.cancellationToken.ThrowIfCancellationRequested();
                    var read = reader.Read(buffer, 0, Math.Min(buffer.Length, destination.Length - offset));
                    if (read == 0)
                    {
                        destination[offset..].Clear();
                        break;
                    }
                    buffer.AsSpan(0, read).CopyTo(destination[offset..]);
                    offset += read;
                }
                state.cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        });
    }

    private int[] ReadLineStarts(CancellationToken cancellationToken)
    {
        var starts = new int[_lineCount];
        var startCount = 1;
        starts[0] = 0;
        var buffer = new char[32 * 1024];
        using var reader = _source.CreateReader();
        var absoluteOffset = 0;
        var pendingCarriageReturn = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++, absoluteOffset++)
            {
                var current = buffer[i];
                if (pendingCarriageReturn)
                {
                    AddLineStart(current == '\n' ? absoluteOffset + 1 : absoluteOffset);
                    pendingCarriageReturn = false;
                    if (current == '\n') continue;
                }

                if (current == '\r') pendingCarriageReturn = true;
                else if (current == '\n') AddLineStart(absoluteOffset + 1);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pendingCarriageReturn) AddLineStart(absoluteOffset);
        if (startCount != starts.Length) Array.Resize(ref starts, startCount);
        return starts;

        void AddLineStart(int offset)
        {
            if (startCount == starts.Length)
                Array.Resize(ref starts, Math.Max(startCount + 1, starts.Length * 2));
            starts[startCount++] = offset;
        }
    }
}

internal static class EditorTextSnapshotStore
{
    private sealed class DocumentEntry
    {
        private readonly object _gate = new();
        private EditorTextSnapshot? _snapshot;

        public EditorTextSnapshot GetSnapshot(TextDocument document)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_snapshot?.Version, document.Version)) return _snapshot!;
                return _snapshot = new EditorTextSnapshot(document);
            }
        }
    }

    private static readonly ConditionalWeakTable<TextDocument, DocumentEntry> Documents = new();

    public static EditorTextSnapshot GetSnapshot(TextDocument document) =>
        Documents.GetValue(document, static _ => new DocumentEntry()).GetSnapshot(document);
}
