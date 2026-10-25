// Licensed under the GNU GPL-v3.0
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;

namespace Kodo.Renderers;

public sealed class EditorMinimapControl : Control
{
    private const int MaximumSamples = 2048;
    private int[] _lineLengths = [];
    private int _lineCount;
    private int _maximumLineLength = 1;
    private int _viewportTopLine = 1;
    private int _viewportBottomLine = 1;
    private bool _isDragging;
    private TextDocument? _document;
    private ITextSourceVersion? _documentVersion;
    private IBrush _backgroundBrush = Brushes.Transparent;
    private IBrush _lineBrush = Brushes.Gray;
    private IBrush _viewportBrush = Brushes.Transparent;
    private IBrush _viewportBorderBrush = Brushes.Gray;

    public event Action<int>? LineSelected;

    public EditorMinimapControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public void SetTheme(IBrush background, IBrush mutedText, IBrush accent)
    {
        _backgroundBrush = background;
        _lineBrush = WithOpacity(mutedText, 0.48);
        _viewportBrush = WithOpacity(accent, 0.17);
        _viewportBorderBrush = WithOpacity(accent, 0.85);
        InvalidateVisual();
    }

    public void SetDocument(TextDocument? document)
    {
        if (!ReferenceEquals(_document, document))
        {
            if (_document is not null) _document.Changed -= OnDocumentChanged;
            _document = document;
            if (_document is not null) _document.Changed += OnDocumentChanged;
            _documentVersion = null;
            _lineLengths = [];
        }

        _lineCount = document?.LineCount ?? 0;
        if (document is null || _lineCount <= 0)
        {
            _documentVersion = null;
            _lineLengths = [];
            _maximumLineLength = 1;
            _viewportTopLine = 1;
            _viewportBottomLine = 1;
            InvalidateVisual();
            return;
        }

        var version = document.Version;
        if (ReferenceEquals(_documentVersion, version)) return;
        _documentVersion = version;
        var sampleCount = Math.Min(MaximumSamples, _lineCount);
        var samples = new int[sampleCount];
        var maximumLineLength = 1;
        var sampleStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var index = 0; index < sampleCount; index++)
        {
            var lineNumber = 1 + (int)((long)index * _lineCount / sampleCount);
            var length = document.GetLineByNumber(lineNumber).Length;
            samples[index] = length;
            maximumLineLength = Math.Max(maximumLineLength, Math.Min(160, length));
        }
        var sampleElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(sampleStartedAt);
        KodoDiagnostics.ReportSlowStage("editor minimap line sampling", (long)sampleElapsed.TotalMilliseconds, 16,
            $"lines={_lineCount} samples={sampleCount}");
        _lineLengths = samples;
        _maximumLineLength = maximumLineLength;
        _viewportTopLine = Math.Clamp(_viewportTopLine, 1, _lineCount);
        _viewportBottomLine = Math.Clamp(_viewportBottomLine, _viewportTopLine, _lineCount);
        InvalidateVisual();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (sender is not TextDocument document || !ReferenceEquals(document, _document) ||
            _lineLengths.Length == 0 || document.LineCount != _lineCount)
            return;

        var insertedText = e.InsertedText.Text;
        if (insertedText.IndexOfAny(['\r', '\n']) >= 0)
            return;

        var offset = Math.Clamp(e.Offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        var sampleIndex = FindSampleIndex(line.LineNumber, _lineCount, _lineLengths.Length);
        if (sampleIndex >= 0)
        {
            var previousMaximum = Math.Min(160, _lineLengths[sampleIndex]);
            var updatedMaximum = Math.Min(160, line.Length);
            _lineLengths[sampleIndex] = line.Length;
            if (updatedMaximum >= _maximumLineLength)
            {
                _maximumLineLength = Math.Max(1, updatedMaximum);
            }
            else if (previousMaximum == _maximumLineLength)
            {
                var maximum = 1;
                foreach (var length in _lineLengths)
                    maximum = Math.Max(maximum, Math.Min(160, length));
                _maximumLineLength = maximum;
            }
        }
        _documentVersion = document.Version;
        InvalidateVisual();
    }

    private static int FindSampleIndex(int lineNumber, int lineCount, int sampleCount)
    {
        var low = 0;
        var high = sampleCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var sampledLine = 1 + (int)((long)middle * lineCount / sampleCount);
            if (sampledLine == lineNumber) return middle;
            if (sampledLine < lineNumber) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    public void SetViewport(int topLine, int bottomLine)
    {
        if (_lineCount <= 0) return;
        topLine = Math.Clamp(topLine, 1, _lineCount);
        bottomLine = Math.Clamp(bottomLine, topLine, _lineCount);
        if (_viewportTopLine == topLine && _viewportBottomLine == bottomLine) return;
        _viewportTopLine = topLine;
        _viewportBottomLine = bottomLine;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        context.FillRectangle(_backgroundBrush, new Rect(0, 0, width, height));
        if (_lineLengths.Length == 0 || _lineCount <= 0) return;

        var rowCount = Math.Clamp((int)Math.Ceiling(height), 1, MaximumSamples);
        var rowHeight = height / rowCount;
        var maximumLength = _maximumLineLength;
        var innerWidth = Math.Max(1, width - 8);
        for (var row = 0; row < rowCount; row++)
        {
            var sampleIndex = Math.Min(_lineLengths.Length - 1, (int)((long)row * _lineLengths.Length / rowCount));
            var length = _lineLengths[sampleIndex];
            if (length <= 0) continue;
            var proportion = Math.Clamp((double)length / maximumLength, 0.035, 1.0);
            var lineWidth = Math.Max(2, innerWidth * proportion);
            context.FillRectangle(_lineBrush, new Rect(4, row * rowHeight, Math.Min(innerWidth, lineWidth), Math.Min(1.25, rowHeight)));
        }

        var viewportTop = Math.Clamp((_viewportTopLine - 1d) / _lineCount * height, 0, height);
        var viewportBottom = Math.Clamp(_viewportBottomLine / (double)_lineCount * height, viewportTop, height);
        var viewportHeight = Math.Min(height, Math.Max(12, viewportBottom - viewportTop));
        viewportTop = Math.Clamp(viewportTop, 0, Math.Max(0, height - viewportHeight));
        var viewportRect = new Rect(0.5, viewportTop + 0.5, Math.Max(0, width - 1), Math.Max(0, viewportHeight - 1));
        context.FillRectangle(_viewportBrush, viewportRect);
        context.DrawRectangle(null, new Pen(_viewportBorderBrush, 1), viewportRect);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _isDragging = true;
        e.Pointer.Capture(this);
        SelectLine(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;
        SelectLine(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void SelectLine(double y)
    {
        if (_lineCount <= 0 || Bounds.Height <= 0) return;
        var fraction = Math.Clamp(y / Bounds.Height, 0, 0.999999);
        LineSelected?.Invoke(1 + (int)(fraction * _lineCount));
    }

    private static IBrush WithOpacity(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;
}
