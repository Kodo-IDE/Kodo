// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Concurrent;

namespace Kodo.Editor;

[Flags]
internal enum InsightRefreshKind
{
    None = 0,
    Suggestions = 1,
    Errors = 2,
    DeadCode = 4,
    Diagnostics = Errors | DeadCode,
    All = Suggestions | Diagnostics
}

internal static class InsightAnalysisMetrics
{
    private static readonly ConcurrentDictionary<InsightRefreshKind, double> AverageCosts = new();

    public static void RecordCost(InsightRefreshKind kind, long elapsedMilliseconds) =>
        AverageCosts.AddOrUpdate(kind, Math.Clamp(elapsedMilliseconds, 0, 10_000), (_, previous) =>
            (previous * 0.75) + (Math.Clamp(elapsedMilliseconds, 0, 10_000) * 0.25));

    public static double GetCost(InsightRefreshKind kind) => AverageCosts.TryGetValue(kind, out var cost) ? cost : 0;
}

internal sealed class InsightRefreshPolicy
{
    private long _lastEditTimestamp;
    private double _averageCostMilliseconds = 120;
    private int _rapidEditStreak;

    public void RecordEdit(long timestamp)
    {
        if (_lastEditTimestamp != 0)
        {
            var interval = System.Diagnostics.Stopwatch.GetElapsedTime(_lastEditTimestamp, timestamp).TotalMilliseconds;
            _rapidEditStreak = interval < 220 ? Math.Min(_rapidEditStreak + 1, 8) : Math.Max(0, _rapidEditStreak - 2);
        }
        _lastEditTimestamp = timestamp;
    }

    public void RecordCost(long elapsedMilliseconds) =>
        _averageCostMilliseconds = (_averageCostMilliseconds * 0.75) + (Math.Clamp(elapsedMilliseconds, 0, 10_000) * 0.25);

    public int GetIdleDelayMilliseconds(int quietWindowMilliseconds = 500)
    {
        if (_lastEditTimestamp == 0) return 0;
        var idleMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(_lastEditTimestamp).TotalMilliseconds;
        return (int)Math.Clamp(quietWindowMilliseconds - idleMilliseconds, 0, 400);
    }

    public int GetDelayMilliseconds(int textLength, int lineCount, int activeLineLength, bool hasLsp, InsightRefreshKind kind)
    {
        var sizeDelay = textLength >= 1_000_000 || lineCount >= 50_000 ? 900
            : textLength >= 250_000 || lineCount >= 5_000 ? 450
            : hasLsp ? 120 : 500;
        var typingDelay = Math.Min(700, _rapidEditStreak * 75);
        var operationCost = Math.Max(_averageCostMilliseconds, Math.Max(
            (kind & InsightRefreshKind.Errors) != 0 ? InsightAnalysisMetrics.GetCost(InsightRefreshKind.Errors) : 0,
            (kind & InsightRefreshKind.DeadCode) != 0 ? InsightAnalysisMetrics.GetCost(InsightRefreshKind.DeadCode) : 0));
        var costDelay = (int)Math.Clamp((operationCost - 120) * 0.35, 0, 350);
        var lineComplexityDelay = (int)Math.Clamp((activeLineLength - 16_384) / 64d, 0, 350);
        return Math.Clamp(sizeDelay + typingDelay + costDelay + lineComplexityDelay, 80, 1_500);
    }
}
