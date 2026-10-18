// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;

namespace Kodo;

public partial class MainWindow
{
    private readonly object _appLoadingLock = new();
    private readonly Dictionary<long, (string Label, double? Progress)> _appLoadingOperations = new();
    private readonly DispatcherTimer _appLoadingRevealTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private long _nextAppLoadingOperationId;
    private bool _appLoadingTimerHooked;
    private bool _isAppLoadingVisible;
    private string _appLoadingLabel = string.Empty;
    private bool _appLoadingIsIndeterminate = true;
    private double _appLoadingProgress;

    public bool IsAppLoadingVisible => _isAppLoadingVisible;
    public string AppLoadingLabel => _appLoadingLabel;
    public bool AppLoadingIsIndeterminate => _appLoadingIsIndeterminate;
    public double AppLoadingProgress => _appLoadingProgress;

    private AppLoadingOperation BeginAppLoading(string label)
    {
        long id;
        lock (_appLoadingLock)
        {
            id = ++_nextAppLoadingOperationId;
            _appLoadingOperations[id] = (label, null);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!_appLoadingRevealTimer.IsEnabled)
            {
                if (!_appLoadingTimerHooked)
                {
                    _appLoadingRevealTimer.Tick += AppLoadingRevealTimer_OnTick;
                    _appLoadingTimerHooked = true;
                }
                _appLoadingRevealTimer.Start();
            }
            UpdateAppLoadingState();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_appLoadingRevealTimer.IsEnabled)
                {
                    if (!_appLoadingTimerHooked)
                    {
                        _appLoadingRevealTimer.Tick += AppLoadingRevealTimer_OnTick;
                        _appLoadingTimerHooked = true;
                    }
                    _appLoadingRevealTimer.Start();
                }
                UpdateAppLoadingState();
            });
        }

        return new AppLoadingOperation(this, id);
    }

    private void AppLoadingRevealTimer_OnTick(object? sender, EventArgs e)
    {
        _appLoadingRevealTimer.Stop();
        lock (_appLoadingLock)
        {
            if (_appLoadingOperations.Count == 0)
                return;
        }
        _isAppLoadingVisible = true;
        OnPropertyChanged(nameof(IsAppLoadingVisible));
        UpdateAppLoadingState();
    }

    private void UpdateAppLoadingOperation(long id, string? label, double? progress)
    {
        lock (_appLoadingLock)
        {
            if (!_appLoadingOperations.TryGetValue(id, out var operation))
                return;
            _appLoadingOperations[id] = (label ?? operation.Label,
                progress.HasValue ? Math.Clamp(progress.Value, 0, 1) : operation.Progress);
        }
        DispatchAppLoadingStateUpdate();
    }

    private void EndAppLoadingOperation(long id)
    {
        bool hasOperations;
        lock (_appLoadingLock)
        {
            _appLoadingOperations.Remove(id);
            hasOperations = _appLoadingOperations.Count > 0;
        }

        if (!hasOperations)
        {
            if (Dispatcher.UIThread.CheckAccess())
                HideAppLoadingIndicator();
            else
                Dispatcher.UIThread.Post(HideAppLoadingIndicator);
        }
        else
        {
            DispatchAppLoadingStateUpdate();
        }
    }

    private void DispatchAppLoadingStateUpdate()
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdateAppLoadingState();
        else
            Dispatcher.UIThread.Post(UpdateAppLoadingState);
    }

    private void UpdateAppLoadingState()
    {
        (string Label, double? Progress) current;
        int count;
        lock (_appLoadingLock)
        {
            count = _appLoadingOperations.Count;
            if (count == 0)
                return;
            current = _appLoadingOperations.MaxBy(operation => operation.Key).Value;
        }

        var label = count > 1 ? $"{current.Label} ({count} tasks)" : current.Label;
        var indeterminate = count > 1 || !current.Progress.HasValue;
        var progress = current.Progress ?? 0;
        if (_appLoadingLabel != label)
        {
            _appLoadingLabel = label;
            OnPropertyChanged(nameof(AppLoadingLabel));
        }
        if (_appLoadingIsIndeterminate != indeterminate)
        {
            _appLoadingIsIndeterminate = indeterminate;
            OnPropertyChanged(nameof(AppLoadingIsIndeterminate));
        }
        if (Math.Abs(_appLoadingProgress - progress) > 0.001)
        {
            _appLoadingProgress = progress;
            OnPropertyChanged(nameof(AppLoadingProgress));
        }
    }

    private void HideAppLoadingIndicator()
    {
        _appLoadingRevealTimer.Stop();
        if (!_isAppLoadingVisible)
            return;
        _isAppLoadingVisible = false;
        OnPropertyChanged(nameof(IsAppLoadingVisible));
    }

    private sealed class AppLoadingOperation(MainWindow owner, long id) : IDisposable
    {
        private MainWindow? _owner = owner;

        public void Report(double? progress = null, string? label = null)
            => _owner?.UpdateAppLoadingOperation(id, label, progress);

        public void Dispose()
            => System.Threading.Interlocked.Exchange(ref _owner, null)?.EndAppLoadingOperation(id);
    }
}
