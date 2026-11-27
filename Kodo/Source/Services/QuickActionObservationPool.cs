// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;

namespace Kodo.QuickActions;

public sealed class QuickActionObservationPool
{
    private readonly Dictionary<string, QuickActionItem> _actions = new(StringComparer.Ordinal);
    private string? _signature;

    public string? Signature => _signature;

    public int Count => _actions.Count;

    public void Update(string? signature, IEnumerable<QuickActionItem> actions)
    {
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            _signature = signature;
            _actions.Clear();
        }

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Id)) continue;
            _actions[action.Id] = action;
        }
    }

    public void Clear()
    {
        _signature = null;
        _actions.Clear();
    }

    public QuickActionItem? Resolve(string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return null;
        return _actions.TryGetValue(actionId.Trim(), out var action) ? action : null;
    }
}
