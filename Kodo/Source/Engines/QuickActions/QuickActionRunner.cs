// Licensed under the GNU GPL-v3.0
using System;
using System.Threading.Tasks;

namespace Kodo.QuickActions;

public sealed record QuickActionExecutionResult(bool Succeeded, Exception? Error);

public static class QuickActionRunner
{
    public static async Task<QuickActionExecutionResult> RunAsync(Func<Task<bool>> action, Action onSuccess)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(onSuccess);

        bool succeeded;
        try
        {
            succeeded = await action();
        }
        catch (Exception ex)
        {
            return new QuickActionExecutionResult(false, ex);
        }

        if (!succeeded) return new QuickActionExecutionResult(false, null);

        onSuccess();
        return new QuickActionExecutionResult(true, null);
    }
}
