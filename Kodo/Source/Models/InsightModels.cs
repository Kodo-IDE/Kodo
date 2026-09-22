// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia.Threading;

namespace Kodo;

public enum InsightKind { Variable, Function, Property, Type, Namespace, Keyword }

public sealed class InsightSuggestion : ICompletionData
{
    public static IBrush PanelForeground { get; set; } = Brushes.WhiteSmoke;
    public static IBrush MutedForeground { get; set; } = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public InsightKind Kind { get; }
    public string Text { get; }
    public IImage? Image => null;
    private Control? _content;
    public object Content => _content ??= BuildContentVisual();
    public object? Description => null;
    public double Priority => Kind switch
    {
        InsightKind.Variable => 5,
        InsightKind.Function => 4,
        InsightKind.Property => 3,
        InsightKind.Type => 2,
        InsightKind.Namespace => 1,
        InsightKind.Keyword => 0,
        _ => 0,
    };

    public InsightSuggestion(string text, InsightKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        try
        {
            textArea.Document.Replace(completionSegment, Text);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("InsightSuggestion.Complete: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try { textArea.Document.Replace(completionSegment, Text); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
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

    private static readonly Geometry VariableIconGeometry = Geometry.Parse(
        "M0 7.008v-3.008q0-1.632 1.184-2.816t2.816-1.184h4q1.664 0 2.816 1.184t1.184 2.816h4q2.496 0 4.256 1.76l9.984 10.016q1.76 1.728 1.76 4.224t-1.76 4.256l-5.984 6.016q-1.76 1.728-4.224 1.728t-4.256-1.728l-10.016-10.016q-1.76-1.76-1.76-4.256v-12h6.016q0-0.832-0.608-1.408t-1.408-0.576h-4q-0.832 0-1.408 0.576t-0.576 1.408v3.008q0 0.608-0.512 0.864t-0.992 0-0.512-0.864zM8 16q0 0.832 0.608 1.408l9.984 10.016q0.608 0.576 1.44 0.576t1.376-0.576l6.016-6.016q0.576-0.576 0.576-1.408t-0.576-1.408l-10.016-10.016q-0.576-0.576-1.408-0.576h-1.024q1.024 1.376 1.024 3.008 0 1.12-0.384 2.048t-0.992 1.536-1.472 0.992-1.728 0.416-1.76-0.192-1.664-0.832v1.024zM8 11.008q0 0.8 0.32 1.44t0.864 0.928 1.184 0.48 1.28 0 1.152-0.48 0.864-0.928 0.352-1.44q0-0.96-0.576-1.728t-1.44-1.056v2.784q0 0.608-0.512 0.864t-0.992 0-0.48-0.864v-2.784q-0.896 0.288-1.44 1.056t-0.576 1.728z");

    private static readonly Geometry FunctionIconGeometry = Geometry.Parse(
        "M16.6582 9.28638C18.098 10.1862 18.8178 10.6361 19.0647 11.2122C19.2803 11.7152 19.2803 12.2847 19.0647 12.7878C18.8178 13.3638 18.098 13.8137 16.6582 14.7136L9.896 18.94C8.29805 19.9387 7.49907 20.4381 6.83973 20.385C6.26501 20.3388 5.73818 20.0469 5.3944 19.584C5 19.053 5 18.1108 5 16.2264V7.77357C5 5.88919 5 4.94701 5.3944 4.41598C5.73818 3.9531 6.26501 3.66111 6.83973 3.6149C7.49907 3.5619 8.29805 4.06126 9.896 5.05998L16.6582 9.28638Z");

    private static readonly Geometry PropertyIconGeometry = Geometry.Parse(
        "M3 8 H19 M3 16 H19 M6 5.5 H10 V10.5 H6 Z M14 13.5 H18 V18.5 H14 Z");

    private static readonly Geometry TypeIconGeometry = Geometry.Parse(
        "M0 12L6 1.6H18L24 12L18 22.4H6L0 12Z M9 8 H15 V10 H13 V16 H11 V10 H9 Z");

    private static readonly Geometry NamespaceIconGeometry = Geometry.Parse(
        "M4 10 H14 V20 H4 Z M7 7 H17 V17 H15 V9 H7 Z M10 4 H20 V14 H10 Z");

    private static readonly Geometry KeywordIconGeometry = Geometry.Parse(
        "M9 4 L11 20 M13 4 L15 20 M4 9 H20 M4 15 H20");

    private Control BuildContentVisual()
    {
        var iconGeometry = Kind switch
        {
            InsightKind.Variable => VariableIconGeometry,
            InsightKind.Function => FunctionIconGeometry,
            InsightKind.Property => PropertyIconGeometry,
            InsightKind.Type => TypeIconGeometry,
            InsightKind.Namespace => NamespaceIconGeometry,
            InsightKind.Keyword => KeywordIconGeometry,
            _ => KeywordIconGeometry,
        };

        var iconChip = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(7),
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
                        Child = new Path
                        {
                            Data = iconGeometry,
                            Stretch = Stretch.Uniform,
                            Fill = Brushes.White,
                            Stroke = Brushes.White,
                            StrokeThickness = 0.9,
                            StrokeLineCap = PenLineCap.Round,
                            StrokeJoin = PenLineJoin.Round,
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
