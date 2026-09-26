// Licensed under the GNU GPL-v3.0
using System;
using Avalonia.Media;

namespace Kodo;

internal static class KodoFonts
{
    public const string WindowsMonoStack =
        "Cascadia Code,Consolas,Menlo,Segoe UI Emoji,Apple Color Emoji,Noto Color Emoji,Monospace";

    public const string LinuxMonoStack =
        "JetBrains Mono,DejaVu Sans Mono,Ubuntu Mono,Noto Sans Mono,Cascadia Code,Consolas,Menlo,Noto Color Emoji,Segoe UI Emoji,Apple Color Emoji,Monospace";

    public const string MacMonoStack =
        "Menlo,JetBrains Mono,SF Mono,Cascadia Code,Consolas,Monospace";

    public static string MonoFontStack =>
        OperatingSystem.IsWindows() ? WindowsMonoStack :
        OperatingSystem.IsMacOS() ? MacMonoStack :
        LinuxMonoStack;

    public static FontFamily MonoFamily => new(MonoFontStack);
}
