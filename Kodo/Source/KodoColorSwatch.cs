// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Kodo;

public sealed class ColorSwatchElementGenerator : VisualLineElementGenerator
{
    public IBrush PanelBrush { get; set; } = Brush.Parse("#1E1E1E");
    public IBrush BorderBrush { get; set; } = Brush.Parse("#3A3A3A");
    public IBrush TextBrush { get; set; } = Brushes.White;
    public IBrush AccentBrush { get; set; } = Brush.Parse("#8C00FF");
    public string PickerTitle { get; set; } = "Colour Picker";
    public string EditColorTooltip { get; set; } = "Edit Colour";

    private static readonly Regex HexColorRegex =
        new(@"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])", RegexOptions.Compiled);

    private int _cachedLineNumber = -1;
    private string _cachedLineText = string.Empty;
    private MatchCollection _cachedMatches = default!;

    public bool IsEnabled { get; set; } = true;

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!IsEnabled) return -1;

        var line = CurrentContext.VisualLine;
        var document = CurrentContext.Document;
        var lineNumber = line.FirstDocumentLine.LineNumber;
        var lineText = document.GetText(line.FirstDocumentLine.Offset, line.FirstDocumentLine.Length);

        if (lineNumber != _cachedLineNumber || !string.Equals(lineText, _cachedLineText, StringComparison.Ordinal))
        {
            _cachedLineNumber = lineNumber;
            _cachedLineText = lineText;
            _cachedMatches = HexColorRegex.Matches(lineText);
        }

        var relativeStart = startOffset - line.FirstDocumentLine.Offset;
        foreach (Match match in _cachedMatches)
        {
            if (match.Index >= relativeStart)
                return line.FirstDocumentLine.Offset + match.Index;
        }

        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.VisualLine;
        var document = CurrentContext.Document;
        var lineNumber = line.FirstDocumentLine.LineNumber;
        var lineText = document.GetText(line.FirstDocumentLine.Offset, line.FirstDocumentLine.Length);

        if (lineNumber != _cachedLineNumber || !string.Equals(lineText, _cachedLineText, StringComparison.Ordinal))
        {
            _cachedLineNumber = lineNumber;
            _cachedLineText = lineText;
            _cachedMatches = HexColorRegex.Matches(lineText);
        }

        var relativeOffset = offset - line.FirstDocumentLine.Offset;
        var match = _cachedMatches.FirstOrDefault(m => m.Index == relativeOffset);
        if (match is null) return null;

        if (!TryParseHexColor(match.Value, out var color)) return null;

        var swatch = BuildSwatch(document, offset, match.Length, match.Value.Length == 9, color);
        return new InlineObjectElement(0, swatch);
    }

    private Control BuildSwatch(TextDocument document, int offset, int initialLength, bool hasAlpha, Color initialColor)
    {
        var anchor = document.CreateAnchor(offset);
        anchor.SurviveDeletion = true;
        var currentLength = initialLength;

        var (initialH, initialS, initialV) = RgbToHsv(initialColor.R, initialColor.G, initialColor.B);
        var currentH = initialH;
        var currentS = initialS;
        var currentV = initialV;
        var currentA = initialColor.A;
        var currentHasAlpha = hasAlpha;

        var swatchBorder = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(initialColor),
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(swatchBorder, EditColorTooltip);

        const double svWidth = 200;
        const double svHeight = 130;
        const double hueBarWidth = 18;

        var svBase = new Border { Width = svWidth, Height = svHeight, CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
        const double svCornerRadius = 8;
        var svWhiteOverlay = new Rectangle
        {
            Width = svWidth,
            Height = svHeight,
            RadiusX = svCornerRadius,
            RadiusY = svCornerRadius,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.Transparent, 1) }
            }
        };
        var svBlackOverlay = new Rectangle
        {
            Width = svWidth,
            Height = svHeight,
            RadiusX = svCornerRadius,
            RadiusY = svCornerRadius,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 1) }
            }
        };
        var svIndicator = new Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };

        var svPad = new Canvas { Width = svWidth, Height = svHeight, Background = Brushes.Transparent };
        svPad.Children.Add(svBase);
        svPad.Children.Add(svWhiteOverlay);
        svPad.Children.Add(svBlackOverlay);
        svPad.Children.Add(svIndicator);

        var hueGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(255, 0, 0), 0.0),
                new GradientStop(Color.FromRgb(255, 255, 0), 1.0 / 6),
                new GradientStop(Color.FromRgb(0, 255, 0), 2.0 / 6),
                new GradientStop(Color.FromRgb(0, 255, 255), 3.0 / 6),
                new GradientStop(Color.FromRgb(0, 0, 255), 4.0 / 6),
                new GradientStop(Color.FromRgb(255, 0, 255), 5.0 / 6),
                new GradientStop(Color.FromRgb(255, 0, 0), 1.0)
            }
        };
        var hueBarBackground = new Border
        {
            Width = hueBarWidth,
            Height = svHeight,
            CornerRadius = new CornerRadius(8),
            Background = hueGradient,
            IsHitTestVisible = false
        };
        var hueIndicator = new Rectangle
        {
            Width = hueBarWidth,
            Height = 4,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        var hueBar = new Canvas { Width = hueBarWidth, Height = svHeight, Background = Brushes.Transparent };
        hueBar.Children.Add(hueBarBackground);
        hueBar.Children.Add(hueIndicator);

        var previewBorder = new Border
        {
            Width = 40,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(initialColor),
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1)
        };

        var hexBox = new TextBox
        {
            Width = 140,
            Text = FormatHex(initialColor, hasAlpha),
            PlaceholderText = hasAlpha ? "#RRGGBBAA" : "#RRGGBB",
            Foreground = TextBrush,
            Background = PanelBrush,
            BorderBrush = PanelBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
        };
        var rgbValueText = new TextBlock
        {
            Width = 140,
            Text = $"{initialColor.R}, {initialColor.G}, {initialColor.B}",
            Foreground = TextBrush,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,Monospace"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hexValueText = new TextBlock
        {
            Width = 72,
            Text = FormatHex(initialColor, hasAlpha),
            Foreground = TextBrush,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,Monospace"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void UpdatePreview(bool writeHex)
        {
            var (r, g, b) = HsvToRgb(currentH, currentS, currentV);
            var color = new Color(currentA, r, g, b);

            swatchBorder.Background = new SolidColorBrush(color);
            previewBorder.Background = new SolidColorBrush(color);

            var (hueR, hueG, hueB) = HsvToRgb(currentH, 1, 1);
            svBase.Background = new SolidColorBrush(Color.FromRgb(hueR, hueG, hueB));

            Canvas.SetLeft(svIndicator, currentS * svWidth - svIndicator.Width / 2);
            Canvas.SetTop(svIndicator, (1 - currentV) * svHeight - svIndicator.Height / 2);
            Canvas.SetTop(hueIndicator, currentH / 360.0 * svHeight - hueIndicator.Height / 2);

            rgbValueText.Text = $"{r}, {g}, {b}";

            if (writeHex)
            {
                hexBox.Text = FormatHex(color, currentHasAlpha);
                hexValueText.Text = hexBox.Text;
            }
        }

        void CommitToDocument()
        {
            if (anchor.IsDeleted) return;

            try
            {
                var availableLength = document.TextLength - anchor.Offset;
                if (anchor.Offset < 0 || availableLength < 0)
                    return;

                var safeLength = Math.Min(currentLength, availableLength);
                var (r, g, b) = HsvToRgb(currentH, currentS, currentV);
                var color = new Color(currentA, r, g, b);
                var newText = FormatHex(color, currentHasAlpha);

                if (safeLength != currentLength || newText == document.GetText(anchor.Offset, safeLength))
                    return;

                document.Replace(anchor.Offset, safeLength, newText);
                currentLength = newText.Length;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        void UpdateFromSvPointer(Point p)
        {
            currentS = Math.Clamp(p.X / svWidth, 0, 1);
            currentV = Math.Clamp(1 - p.Y / svHeight, 0, 1);
            UpdatePreview(writeHex: true);
        }

        void UpdateFromHuePointer(Point p)
        {
            currentH = Math.Clamp(p.Y / svHeight, 0, 1) * 360;
            UpdatePreview(writeHex: true);
        }

        svPad.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(svPad);
            UpdateFromSvPointer(e.GetPosition(svPad));
            e.Handled = true;
        };
        svPad.PointerMoved += (_, e) =>
        {
            if (ReferenceEquals(e.Pointer.Captured, svPad))
                UpdateFromSvPointer(e.GetPosition(svPad));
        };
        svPad.PointerReleased += (_, e) => e.Pointer.Capture(null);

        hueBar.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(hueBar);
            UpdateFromHuePointer(e.GetPosition(hueBar));
            e.Handled = true;
        };
        hueBar.PointerMoved += (_, e) =>
        {
            if (ReferenceEquals(e.Pointer.Captured, hueBar))
                UpdateFromHuePointer(e.GetPosition(hueBar));
        };
        hueBar.PointerReleased += (_, e) => e.Pointer.Capture(null);

        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = hexBox.Text ?? string.Empty;
            if (!TryParseHexColor(text, out var parsed)) return;

            currentA = parsed.A;
            currentHasAlpha = text.TrimStart('#').Length == 8;
            (currentH, currentS, currentV) = RgbToHsv(parsed.R, parsed.G, parsed.B);
            UpdatePreview(writeHex: true);
        };

        hexBox.GotFocus += (_, _) => hexBox.SelectAll();

        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { svPad, hueBar } };
        var hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "HEX", Width = 40, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center },
                hexValueText,
                hexBox,
                previewBorder
            }
        };
        var rgbRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "RGB", Width = 40, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center },
                rgbValueText
            }
        };
        var pickerChildren = new List<Control> { hexRow, rgbRow, pickerRow };

        var pickerHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(2), Background = AccentBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = PickerTitle, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        var pickerDivider = new Border { Height = 1, Background = BorderBrush, Opacity = 0.9, Margin = new Thickness(0, 4) };
        var pickerDivider2 = new Border { Height = 1, Background = BorderBrush, Opacity = 0.9, Margin = new Thickness(0, 4) };
        var picker = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Padding = new Thickness(16),
            Child = new StackPanel { Spacing = 10 }
        };
        var pickerStack = (StackPanel)picker.Child!;
        pickerStack.Children.Add(pickerHeader);
        pickerStack.Children.Add(pickerDivider);
        foreach (var child in pickerChildren)
            pickerStack.Children.Add(child);
        pickerStack.Children.Add(pickerDivider2);

        var popup = new Popup
        {
            PlacementTarget = swatchBorder,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = picker
        };
        popup.Opened += (_, _) =>
        {
            if (TopLevel.GetTopLevel(picker) is not TopLevel popupRoot) return;

            popupRoot.Background = Brushes.Transparent;
            popupRoot.TransparencyBackgroundFallback = Brushes.Transparent;
            popupRoot.CornerRadius = new CornerRadius(12);
        };
        popup.Closed += (_, _) => CommitToDocument();

        UpdatePreview(writeHex: false);

        swatchBorder.PointerPressed += (_, e) =>
        {
            popup.IsOpen = !popup.IsOpen;
            e.Handled = true;
        };

        return swatchBorder;
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        var rd = r / 255.0;
        var gd = g / 255.0;
        var bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        double h = 0;
        if (delta > 0.00001)
        {
            if (Math.Abs(max - rd) < 0.00001) h = 60 * (((gd - bd) / delta) % 6);
            else if (Math.Abs(max - gd) < 0.00001) h = 60 * (((bd - rd) / delta) + 2);
            else h = 60 * (((rd - gd) / delta) + 4);
        }
        if (h < 0) h += 360;

        var s = max <= 0 ? 0 : delta / max;
        var v = max;
        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        double rd, gd, bd;
        if (h < 60) (rd, gd, bd) = (c, x, 0);
        else if (h < 120) (rd, gd, bd) = (x, c, 0);
        else if (h < 180) (rd, gd, bd) = (0, c, x);
        else if (h < 240) (rd, gd, bd) = (0, x, c);
        else if (h < 300) (rd, gd, bd) = (x, 0, c);
        else (rd, gd, bd) = (c, 0, x);

        return ((byte)Math.Round((rd + m) * 255), (byte)Math.Round((gd + m) * 255), (byte)Math.Round((bd + m) * 255));
    }

    private static string FormatHex(Color color, bool hasAlpha) =>
        hasAlpha
            ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = default;
        var digits = hex.TrimStart('#');

        try
        {
            switch (digits.Length)
            {
                case 3:
                    var r3 = Convert.ToByte(new string(digits[0], 2), 16);
                    var g3 = Convert.ToByte(new string(digits[1], 2), 16);
                    var b3 = Convert.ToByte(new string(digits[2], 2), 16);
                    color = new Color(255, r3, g3, b3);
                    return true;
                case 6:
                    var r6 = Convert.ToByte(digits.Substring(0, 2), 16);
                    var g6 = Convert.ToByte(digits.Substring(2, 2), 16);
                    var b6 = Convert.ToByte(digits.Substring(4, 2), 16);
                    color = new Color(255, r6, g6, b6);
                    return true;
                case 8:
                    var a8 = Convert.ToByte(digits.Substring(0, 2), 16);
                    var r8 = Convert.ToByte(digits.Substring(2, 2), 16);
                    var g8 = Convert.ToByte(digits.Substring(4, 2), 16);
                    var b8 = Convert.ToByte(digits.Substring(6, 2), 16);
                    color = new Color(a8, r8, g8, b8);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
