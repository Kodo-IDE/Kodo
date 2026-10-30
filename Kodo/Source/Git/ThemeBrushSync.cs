// Licensed under GPL-v3.0
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Kodo;

namespace GitSourceControl;

internal static class ThemeBrushSync
{
    public static IDisposable Track(AvaloniaObject target, AvaloniaProperty property, MainWindow window, string brushPropertyName)
    {
        void Apply()
        {
            try
            {
                var brush = window.GetType().GetProperty(brushPropertyName)?.GetValue(window) as IBrush;
                if (brush is not null) target.SetValue(property, brush);
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
