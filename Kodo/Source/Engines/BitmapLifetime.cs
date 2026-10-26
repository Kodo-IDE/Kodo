// Licensed under the GNU GPL-v3.0
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Kodo;

internal static class BitmapLifetime
{
    public static void DisposeAfterLayout(Bitmap? bitmap)
    {
        if (bitmap is null) return;
        Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
    }
}
