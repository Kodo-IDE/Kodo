// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo.QuickActions;

public sealed class QuickActionObservationLog
{
    private const int MaximumTrackedExecutions = 256;

    private readonly HashSet<(string ActionId, Guid ExecutionId)> _recorded = new();
    private readonly Queue<(string ActionId, Guid ExecutionId)> _order = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _recorded.Count; }
    }

    public bool TryRecord(string actionId, Guid executionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return false;

        var key = (actionId.Trim(), executionId);
        lock (_gate)
        {
            if (!_recorded.Add(key)) return false;

            _order.Enqueue(key);
            while (_order.Count > MaximumTrackedExecutions) _recorded.Remove(_order.Dequeue());
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _recorded.Clear();
            _order.Clear();
        }
    }
}
