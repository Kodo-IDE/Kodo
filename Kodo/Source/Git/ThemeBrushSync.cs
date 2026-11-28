// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Media;
using Kodo;
using System;
using System.ComponentModel;

namespace GitSourceControl;

internal static class ThemeBrushSync
{
    public static IDisposable Track(AvaloniaObject target, AvaloniaProperty property, MainWindow window, string brushPropertyName)
        => Track(target, property, window, brushPropertyName, () => ReadBrush(window, brushPropertyName));

    public static IDisposable Track(AvaloniaObject target, AvaloniaProperty property, MainWindow window, string brushPropertyName, Func<IBrush?> resolve)
    {
        void Apply()
        {
            try
            {
                if (resolve() is { } brush) target.SetValue(property, brush);
            }
            catch { }
        }

        Apply();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == brushPropertyName) Apply();
        };
        ((INotifyPropertyChanged)window).PropertyChanged += handler;
        return new Subscription(() => ((INotifyPropertyChanged)window).PropertyChanged -= handler);
    }

    internal static IBrush? ReadBrush(MainWindow window, string brushPropertyName)
    {
        try { return window.GetType().GetProperty(brushPropertyName)?.GetValue(window) as IBrush; }
        catch { return null; }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
