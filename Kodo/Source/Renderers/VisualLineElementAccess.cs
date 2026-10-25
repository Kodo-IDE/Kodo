// Licensed under the GNU GPL-v3.0
using System;
using System.Reflection;
using AvaloniaEdit.Rendering;

namespace Kodo;

internal static class VisualLineElementAccess
{
    private static readonly MethodInfo? SetterMethod =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Action<VisualLineElement, VisualLineElementTextRunProperties>? Setter =
        SetterMethod?.CreateDelegate<Action<VisualLineElement, VisualLineElementTextRunProperties>>();

    public static void SetTextRunProperties(VisualLineElement element, VisualLineElementTextRunProperties properties)
    {
        if (Setter is not null)
        {
            Setter(element, properties);
            return;
        }

        SetterMethod?.Invoke(element, [properties]);
    }
}
