// Licensed under GPL-v3.0
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Kodo;

public static class PerformanceBudget
{
    // Budgets per frame (16ms at 60fps). Intensive work must yield if over budget.
    public const int InteractionBudgetMs = 4;
    public const int VisibleRenderBudgetMs = 6;
    public const int BackgroundBudgetMs = 12;

    public static bool IsLargeFile(TextDocument? doc, int threshold = 80_000) => (doc?.TextLength ?? 0) > threshold;
    public static bool IsHugeFile(TextDocument? doc, int threshold = 250_000) => (doc?.TextLength ?? 0) > threshold;

    public static bool ShouldSkipForViewport(TextView? tv, DocumentLine line, int buffer = 80)
    {
        if (tv == null || !tv.VisualLinesValid || tv.VisualLines.Count == 0) return false;
        if (tv.Document?.TextLength <= 30_000) return false;
        var first = tv.VisualLines[0].FirstDocumentLine.LineNumber;
        var last = tv.VisualLines[tv.VisualLines.Count - 1].FirstDocumentLine.LineNumber;
        return line.LineNumber < first - buffer || line.LineNumber > last + buffer;
    }
}

public sealed class DebouncedWork : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<CancellationToken, Task> _action;
    private CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    public DebouncedWork(TimeSpan delay, Func<CancellationToken, Task> action)
    {
        _action = action;
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += OnTick;
    }

    public void Trigger()
    {
        lock (_lock) { _cts.Cancel(); _cts.Dispose(); _cts = new CancellationTokenSource(); }
        _timer.Stop(); _timer.Start();
    }

    public void UpdateDelay(TimeSpan delay) => _timer.Interval = delay;

    private async void OnTick(object? s, EventArgs e)
    {
        _timer.Stop();
        var token = _cts.Token;
        try { await _action(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { KodoDiagnostics.LogDebug("DebouncedWork failed", ex); }
    }

    public void Dispose() { _timer.Stop(); _cts.Cancel(); _cts.Dispose(); }
}

public sealed class UiStallWatchdog : IDisposable
{
    private readonly DispatcherTimer _timer;
    private long _lastTickMs;

    public UiStallWatchdog()
    {
        _lastTickMs = Environment.TickCount64;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            var gap = now - _lastTickMs;
            _lastTickMs = now;
            if (gap >= 2500)
                KodoDiagnostics.ReportSlowStage("UI-thread stall (no dispatch)", gap, 2500);
        };
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}

public sealed class ViewportTracker
{
    private int _firstVisible = 1;
    private int _lastVisible = 1;
    private int _totalLines = 1;

    public void Update(TextView tv)
    {
        if (tv?.VisualLinesValid != true || tv.VisualLines.Count == 0) return;
        _firstVisible = tv.VisualLines[0].FirstDocumentLine.LineNumber;
        _lastVisible = tv.VisualLines[tv.VisualLines.Count - 1].FirstDocumentLine.LineNumber;
        _totalLines = tv.Document?.LineCount ?? 1;
    }

    public bool IsLineVisible(int lineNumber, int buffer = 80) => lineNumber >= _firstVisible - buffer && lineNumber <= _lastVisible + buffer;
    public (int first, int last) VisibleRange(int buffer = 80) => (_firstVisible - buffer, _lastVisible + buffer);
}
