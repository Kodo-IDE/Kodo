// Licensed under the GNU GPL-v3.0
using Avalonia.Threading;
using AvaloniaEdit.Document;
using System;
using System.Threading;

namespace Kodo;

public static class PerformanceBudget
{
    public const int ViewportCullThreshold = 30_000;

    public const int MaximumCustomColorizationLineLength = 16_384;

    public const int SnapshotSkipThreshold = 80_000;

    public const int LargeFileLineCountThreshold = 50_000;

    public const int HugeFileLineCountThreshold = 100_000;

    public static bool IsLargeFile(TextDocument? doc, int threshold = SnapshotSkipThreshold) =>
        doc is not null && (doc.TextLength > threshold || doc.LineCount >= LargeFileLineCountThreshold);

    public static bool IsHugeFile(TextDocument? doc, int threshold = 250_000) =>
        doc is not null && (doc.TextLength > threshold || doc.LineCount >= HugeFileLineCountThreshold);

    private static int _modalDepth;

    public static bool ModalOpen => Volatile.Read(ref _modalDepth) > 0;

    public static IDisposable TrackModal() => new ModalScope();

    private sealed class ModalScope : IDisposable
    {
        public ModalScope() => Interlocked.Increment(ref _modalDepth);
        public void Dispose() => Interlocked.Decrement(ref _modalDepth);
    }
}

public sealed class UiStallWatchdog : IDisposable
{
    private const long MaxReportableGapMs = 30_000;

    private readonly DispatcherTimer _timer;
    private readonly EventHandler _onTick;
    private long _lastTickMs;
    private long _lastAllocatedBytes;
    private int _lastGen0Collections;
    private int _lastGen1Collections;
    private int _lastGen2Collections;
    private long _lastGcMetricReportMs;

    public UiStallWatchdog()
    {
        _lastTickMs = Environment.TickCount64;
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _lastGen0Collections = GC.CollectionCount(0);
        _lastGen1Collections = GC.CollectionCount(1);
        _lastGen2Collections = GC.CollectionCount(2);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _onTick = (_, _) =>
        {
            var now = Environment.TickCount64;
            var gap = now - _lastTickMs;
            _lastTickMs = now;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            var allocatedDelta = Math.Max(0, allocatedBytes - _lastAllocatedBytes);
            _lastAllocatedBytes = allocatedBytes;
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var gen0Delta = gen0 - _lastGen0Collections;
            var gen1Delta = gen1 - _lastGen1Collections;
            var gen2Delta = gen2 - _lastGen2Collections;
            _lastGen0Collections = gen0;
            _lastGen1Collections = gen1;
            _lastGen2Collections = gen2;
            var allocatedBytesPerSecond = gap > 0 ? allocatedDelta * 1000 / gap : 0;
            if ((allocatedBytesPerSecond >= 96L * 1024 * 1024 || gen2Delta > 0) &&
                now - _lastGcMetricReportMs >= 30_000)
            {
                _lastGcMetricReportMs = now;
                KodoDiagnostics.ReportRuntimeMetric(
                    "GC pressure",
                    $"{allocatedBytesPerSecond / (1024 * 1024)}MiB/s",
                    $"gen0Delta={gen0Delta} gen1Delta={gen1Delta} gen2Delta={gen2Delta} heap={GC.GetTotalMemory(false) / (1024 * 1024)}MiB");
            }
            if (gap < 2500 || gap > MaxReportableGapMs) return;
            if (PerformanceBudget.ModalOpen) return;
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
