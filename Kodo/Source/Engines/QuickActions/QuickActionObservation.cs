// Licensed under the GNU GPL-v3.0
using System;

namespace Kodo.QuickActions;

public static class QuickActionObservation
{
    public static Action Wrap(string actionId, Action? onSuccess, Action<string, Guid> record, QuickActionExecution? execution = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        QuickActionExecution? current = execution;
        return () =>
        {
            onSuccess?.Invoke();
            current ??= QuickActionExecution.Begin();
            record(actionId, current.Id);
        };
    }
}
