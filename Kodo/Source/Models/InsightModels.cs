// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kodo;

public enum InsightKind { Variable, Function, Property, Type, Namespace, Keyword }

public sealed class InsightSuggestion : ICompletionData
{
    public static IBrush PanelForeground { get; set; } = Brushes.WhiteSmoke;
    public static IBrush MutedForeground { get; set; } = new SolidColorBrush(KodoDesignTokens.SlateGrey);

    public InsightKind Kind { get; }
    public string Text { get; }
    public int? ReplaceStartOffset { get; }
    public int? ReplaceEndOffset { get; }
    public string? SourceText { get; }
    public ITextSourceVersion? SourceVersion { get; }
    public IReadOnlyList<(int Start, int End, string NewText)> AdditionalEdits { get; }
    private readonly double? _priority;
    public IImage? Image => null;
    private Control? _content;
    public object Content => _content ??= BuildContentVisual();
    public object? Description => null;
    public double Priority => _priority ?? Kind switch
    {
        InsightKind.Variable => 5,
        InsightKind.Function => 4,
        InsightKind.Property => 3,
        InsightKind.Type => 2,
        InsightKind.Namespace => 1,
        InsightKind.Keyword => 0,
        _ => 0,
    };

    public InsightSuggestion(
        string text,
        InsightKind kind,
        int? replaceStartOffset = null,
        int? replaceEndOffset = null,
        string? sourceText = null,
        IReadOnlyList<(int Start, int End, string NewText)>? additionalEdits = null,
        double? priority = null,
        ITextSourceVersion? sourceVersion = null)
    {
        Text = text;
        Kind = kind;
        ReplaceStartOffset = replaceStartOffset;
        ReplaceEndOffset = replaceEndOffset;
        SourceText = sourceText;
        SourceVersion = sourceVersion;
        AdditionalEdits = additionalEdits ?? Array.Empty<(int Start, int End, string NewText)>();
        _priority = priority;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        try
        {
            ApplyCompletion(textArea, completionSegment);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("InsightSuggestion.Complete: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try { ApplyCompletion(textArea, completionSegment); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void ApplyCompletion(TextArea textArea, ISegment completionSegment)
    {
        var document = textArea.Document;
        if (SourceVersion is not null && !ReferenceEquals(document.Version, SourceVersion)) return;
        if (SourceVersion is null && SourceText is not null && !string.Equals(document.Text, SourceText, StringComparison.Ordinal)) return;
        var start = ReplaceStartOffset ?? completionSegment.Offset;
        var end = ReplaceEndOffset ?? completionSegment.EndOffset;
        if (start < 0 || end < start || end > document.TextLength) return;
        var edits = AdditionalEdits.Append((Start: start, End: end, NewText: Text)).OrderByDescending(edit => edit.Start).ThenByDescending(edit => edit.End).ToArray();
        var previousStart = int.MaxValue;
        foreach (var edit in edits)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > document.TextLength || edit.End > previousStart || edit.Start == previousStart && edit.Start == edit.End) return;
            previousStart = edit.Start;
        }
        document.UndoStack.StartUndoGroup();
        var appliedAny = false;
        try
        {
            foreach (var edit in edits)
            {
                document.Replace(edit.Start, edit.End - edit.Start, edit.NewText);
                appliedAny = true;
            }
        }
        catch
        {
            document.UndoStack.EndUndoGroup();
            if (appliedAny && document.UndoStack.CanUndo)
                document.UndoStack.Undo();
            throw;
        }
        document.UndoStack.EndUndoGroup();
    }

    private static string KindLabel(InsightKind kind) => kind switch
    {
        InsightKind.Variable => "Variable (this file)",
        InsightKind.Function => "Function",
        InsightKind.Property => "Property",
        InsightKind.Type => "Type",
        InsightKind.Namespace => "Namespace",
        InsightKind.Keyword => "Keyword",
        _ => string.Empty,
    };

    private static (string Glyph, string Color) GlyphAndColorFor(InsightKind kind) => kind switch
    {
        InsightKind.Variable => ("V", "#3a79df"),
        InsightKind.Function => ("F", "#9c51e2"),
        InsightKind.Property => ("P", "#1bc0ad"),
        InsightKind.Type => ("T", "#e76e17"),
        InsightKind.Namespace => ("N", "#0db373"),
        InsightKind.Keyword => ("K", "#5b5dda"),
        _ => ("•", "#6B7280"),
    };

    private static readonly Dictionary<InsightKind, IBrush> GlyphBrushes =
        Enum.GetValues<InsightKind>().ToDictionary(
            k => k,
            k => (IBrush)new SolidColorBrush(Color.Parse(GlyphAndColorFor(k).Color)));

    private static readonly FontFamily MonoFontFamily = KodoFonts.MonoFamily;

    private static Avalonia.Svg.Skia.SvgSource SymbolIconFor(InsightKind kind)
    {
        string svg = kind switch
        {
            InsightKind.Function => KodoIcons.SymbolFunction,
            InsightKind.Property => KodoIcons.SymbolProperty,
            InsightKind.Type => KodoIcons.SymbolType,
            InsightKind.Namespace => KodoIcons.SymbolNamespace,
            InsightKind.Keyword => KodoIcons.SymbolKeyword,
            _ => KodoIcons.SymbolVariable,
        };

        return Avalonia.Svg.Skia.SvgSource.LoadFromSvg(svg)!;
    }

    private Control BuildContentVisual()
    {
        var symbolIcon = SymbolIconFor(Kind);

        var iconChip = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = KodoDesignTokens.ControlRadius,
            Background = GlyphBrushes[Kind],
            BorderBrush = new SolidColorBrush(Color.FromArgb(18, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Children =
                {
                    new Viewbox
                    {
                        Width = 12,
                        Height = 12,
                        Stretch = Stretch.Uniform,
                        StretchDirection = StretchDirection.Both,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new Image
                        {
                            Source = new Avalonia.Svg.Skia.SvgImage { Source = symbolIcon },
                            Width = 12,
                            Height = 12,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            },
        };

        var nameBlock = new TextBlock
        {
            Text = Text,
            Foreground = PanelForeground,
            FontFamily = MonoFontFamily,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        var kindBlock = new TextBlock
        {
            Text = KindLabel(Kind),
            Foreground = MutedForeground,
            FontSize = 11,
            FontWeight = FontWeight.Normal,
            Opacity = 0.84,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 220,
            Margin = new Thickness(0, 0, 2, 0),
        };

        var row = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            ColumnSpacing = 0,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 120 });
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        Grid.SetColumn(iconChip, 0);
        Grid.SetColumn(nameBlock, 1);
        Grid.SetColumn(kindBlock, 2);
        row.Children.Add(iconChip);
        row.Children.Add(nameBlock);
        row.Children.Add(kindBlock);

        return row;
    }
}
