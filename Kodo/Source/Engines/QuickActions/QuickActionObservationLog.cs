// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo.QuickActions;

public sealed class QuickActionObservationLog
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    private const int MaximumTrackedActions = 256;

    private readonly Dictionary<string, DateTime> _lastRecordedUtc = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryRecord(string actionId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return false;

        var key = actionId.Trim();
        var now = utcNow.ToUniversalTime();

        lock (_gate)
        {
            if (_lastRecordedUtc.TryGetValue(key, out var lastRecorded) && now - lastRecorded < MinimumInterval)
                return false;

            if (!_lastRecordedUtc.ContainsKey(key) && _lastRecordedUtc.Count >= MaximumTrackedActions)
                RemoveOldest();

            _lastRecordedUtc[key] = now;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) _lastRecordedUtc.Clear();
    }

    private void RemoveOldest()
    {
        string? oldestKey = null;
        var oldest = DateTime.MaxValue;
        foreach (var pair in _lastRecordedUtc)
        {
            if (pair.Value >= oldest) continue;
            oldest = pair.Value;
            oldestKey = pair.Key;
        }

        if (oldestKey is not null) _lastRecordedUtc.Remove(oldestKey);
    }
}
