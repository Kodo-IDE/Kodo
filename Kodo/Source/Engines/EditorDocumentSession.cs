// Licensed under the GNU GPL-v3.0
using AvaloniaEdit.Document;
using System;
using System.Threading;

namespace Kodo.Editor;

internal sealed class EditorDocumentSession : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _revisionCancellation = new();
    private string? _filePath;
    private long _revision;
    private bool _disposed;

    public EditorDocumentSession(TextDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public TextDocument Document { get; }

    public string? FilePath
    {
        get
        {
            lock (_gate) return _filePath;
        }
    }

    public long Revision
    {
        get
        {
            lock (_gate) return _revision;
        }
    }

    public CancellationToken RevisionCancellationToken
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return CancellationToken.None;
                return _revisionCancellation.Token;
            }
        }
    }

    public void SetFilePath(string? filePath)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (string.Equals(_filePath, filePath, StringComparison.Ordinal)) return;
            _filePath = filePath;
            AdvanceRevisionUnderLock();
        }
    }

    public void NotifyDocumentChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            AdvanceRevisionUnderLock();
        }
    }

    private void AdvanceRevisionUnderLock()
    {
        if (_disposed) return;
        _revision++;
        var previous = _revisionCancellation;
        _revisionCancellation = new CancellationTokenSource();
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
        try { previous.Dispose(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _revisionCancellation.Cancel();
            _revisionCancellation.Dispose();
        }
    }
}
