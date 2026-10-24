// Licensed under the GNU GPL-v3.0
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace Kodo;

internal sealed class AppUpdateScheduler
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isManualCheckInProgress;
    private readonly Func<bool> _installInBackground;
    public AppUpdateScheduler(Func<bool> isEnabled, Func<bool> isManualCheckInProgress, Func<bool> installInBackground) { _isEnabled = isEnabled; _isManualCheckInProgress = isManualCheckInProgress; _installInBackground = installInBackground; _timer.Tick += async (_, _) => await OnTickAsync().ConfigureAwait(true); }
    public void UpdateLifecycle() { _timer.Stop(); if (_isEnabled()) _timer.Start(); }
    public void Stop() => _timer.Stop();
    private async Task OnTickAsync()
    {
        if (!_isEnabled() || _isManualCheckInProgress()) return;
        try
        {
            var full = await UpdateService.CheckAndHandleUpdateAsync(installInBackground: _installInBackground()).ConfigureAwait(true);
            if (full is null)
                await UpdateService.CheckAndHandleHotfixAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Periodic update check failed", ex); }
    }
}
