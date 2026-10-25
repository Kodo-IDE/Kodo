// Licensed under the GNU GPL-v3.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo.Editor;

internal static class EditorBackgroundWork
{
    public static Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var originalPriority = thread.Priority;
            var priorityLowered = false;
            try
            {
                try
                {
                    thread.Priority = ThreadPriority.BelowNormal;
                    priorityLowered = true;
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Security.SecurityException)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                return work(cancellationToken);
            }
            finally
            {
                if (priorityLowered)
                {
                    try { thread.Priority = originalPriority; }
                    catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Security.SecurityException) { }
                }
            }
        }, cancellationToken);
}
