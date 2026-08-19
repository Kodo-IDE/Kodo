// Licensed under GPL-v3.0
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Kodo.Models;
public static class InlinesBehavior
{
    public static readonly AttachedProperty<IEnumerable<Inline>?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IEnumerable<Inline>?>(
            "Source", typeof(InlinesBehavior));

    static InlinesBehavior()
    {
        SourceProperty.Changed.AddClassHandler<TextBlock>((textBlock, e) =>
        {
            textBlock.Inlines?.Clear();

            if (e.NewValue is IEnumerable<Inline> inlines)
                textBlock.Inlines?.AddRange(inlines);
        });
    }

    public static void SetSource(TextBlock element, IEnumerable<Inline>? value) =>
        element.SetValue(SourceProperty, value);

    public static IEnumerable<Inline>? GetSource(TextBlock element) =>
        element.GetValue(SourceProperty);
}