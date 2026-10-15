// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
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

    public static IReadOnlyList<string> EditorFontChoices { get; } = new[]
    {
        "Default",
        "Cascadia Code",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "Source Code Pro",
        "Iosevka",
        "Hack",
        "Menlo",
        "SF Mono",
        "DejaVu Sans Mono",
        "Ubuntu Mono",
        "Liberation Mono",
        "Noto Sans Mono",
        "Courier New"
    };

    public static FontFamily ResolveEditorFamily(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice) ||
            string.Equals(choice, "Default", StringComparison.OrdinalIgnoreCase))
            return MonoFamily;

        return new FontFamily($"{choice},{MonoFontStack}");
    }
}
