// Licensed under the GNU GPL-v3.0
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
    public const int ViewportCullThreshold = 30_000;

    public const int SnapshotSkipThreshold = 80_000;

    public static bool IsLargeFile(TextDocument? doc, int threshold = SnapshotSkipThreshold) => (doc?.TextLength ?? 0) > threshold;
    public static bool IsHugeFile(TextDocument? doc, int threshold = 250_000) => (doc?.TextLength ?? 0) > threshold;
}

public sealed class UiStallWatchdog : IDisposable
{
    private const long MaxReportableGapMs = 30_000;

    private readonly DispatcherTimer _timer;
    private readonly EventHandler _onTick;
    private long _lastTickMs;

    public UiStallWatchdog()
    {
        _lastTickMs = Environment.TickCount64;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _onTick = (_, _) =>
        {
            var now = Environment.TickCount64;
            var gap = now - _lastTickMs;
            _lastTickMs = now;
            if (gap >= 2500 && gap <= MaxReportableGapMs)
                KodoDiagnostics.ReportSlowStage("UI-thread stall (no dispatch)", gap, 2500);
        };
        _timer.Tick += _onTick;
        _timer.Start();
    }

    public void Reset() => _lastTickMs = Environment.TickCount64;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= _onTick;
    }
}
