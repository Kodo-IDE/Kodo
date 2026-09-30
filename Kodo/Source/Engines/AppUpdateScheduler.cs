// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shared = Kodo.HotfixShared.HotfixShared;

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
                await UpdateService.CheckAndHandleHotfixAsync(installInBackground: _installInBackground()).ConfigureAwait(true);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Periodic update check failed", ex); }
    }
}
