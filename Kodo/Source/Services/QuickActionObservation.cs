// Licensed under the GNU GPL-v3.0
using System;

namespace Kodo.QuickActions;

public static class QuickActionObservation
{
    public static Action Wrap(string actionId, Action? onSuccess, Action<string, Guid> record, QuickActionExecution? execution = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        return () =>
        {
            onSuccess?.Invoke();
            record(actionId, execution?.Id ?? QuickActionExecution.Begin().Id);
        };
    }
}
