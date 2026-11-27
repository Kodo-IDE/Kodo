// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kodo;

internal sealed class BookmarkLineNumberMargin : LineNumberMargin
{
    private static readonly Color FallbackAccentColor = Color.FromRgb(140, 0, 255);
    private static readonly Color UnresolvedColor = Color.FromArgb(36, 150, 150, 150);

    private readonly Dictionary<string, (TextAnchor Anchor, EventHandler Handler)> _anchors = new(StringComparer.Ordinal);
    private HashSet<int> _unresolvedLines = new();
    private string? _relativePath;

    public BookmarkLineNumberMargin()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event Action<int>? BookmarkToggled;

    public Func<IBrush?>? AccentBrushProvider { get; set; }

    public Func<string?>? FilePathProvider { get; set; }

    public void RefreshBookmarks() => RebuildAnchors();

    public override void Render(DrawingContext drawingContext)
    {
        DrawMarkerBackgrounds(drawingContext);
        base.Render(drawingContext);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None)
        {
            var line = LineAt(e.GetPosition(this).Y);
            if (line > 0)
            {
                BookmarkToggled?.Invoke(line);
                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

    protected override void OnDocumentChanged(TextDocument? oldDocument, TextDocument? newDocument)
    {
        if (oldDocument is not null)
            oldDocument.Changed -= DocumentChanged;

        DetachAnchors();
        base.OnDocumentChanged(oldDocument!, newDocument!);

        if (newDocument is not null)
            newDocument.Changed += DocumentChanged;

        RebuildAnchors();
    }

    private void DrawMarkerBackgrounds(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return;
        if (_anchors.Count == 0 && _unresolvedLines.Count == 0) return;

        var accent = AccentBrushProvider?.Invoke() ?? Brushes.MediumPurple;
        var accentColor = accent is ISolidColorBrush solid ? solid.Color : FallbackAccentColor;
        var accentFill = new SolidColorBrush(Color.FromArgb(48, accentColor.R, accentColor.G, accentColor.B));
        var unresolvedFill = new SolidColorBrush(UnresolvedColor);
        var width = Bounds.Width;
        if (width <= 0) return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            var bookmarked = false;
            foreach (var (anchor, _) in _anchors.Values)
            {
                if (anchor.IsDeleted || anchor.Line != lineNumber) continue;
                bookmarked = true;
                break;
            }

            var unresolved = !bookmarked && _unresolvedLines.Contains(lineNumber);
            if (!bookmarked && !unresolved) continue;

            var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
            var height = visualLine.TextLines[0].Height;
            var rect = new Rect(0, y, width, height);
            drawingContext.DrawRectangle(bookmarked ? accentFill : unresolvedFill, null, rect, 3, 3);
        }
    }

    private int LineAt(double y)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return -1;

        var verticalOffset = textView.VerticalOffset;
        foreach (var visualLine in textView.VisualLines)
        {
            var top = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop) - verticalOffset;
            var bottom = top + visualLine.TextLines[0].Height;
            if (y >= top && y < bottom)
                return visualLine.FirstDocumentLine.LineNumber;
        }

        return -1;
    }

    private void DocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (Document is not { } document || _relativePath is null) return;

        var updates = new List<(string Id, int Line)>(_anchors.Count);
        foreach (var (id, entry) in _anchors)
        {
            if (entry.Anchor.IsDeleted) continue;
            updates.Add((id, entry.Anchor.Line));
        }

        if (updates.Count > 0)
            BookmarkService.SyncLines(_relativePath, updates);

        var ids = _anchors.Keys.ToList();
        if (ids.Count > 0)
            BookmarkService.RefreshFingerprints(_relativePath, line => LineTextAt(document, line), ids);

        RefreshUnresolvedLines();
        InvalidateVisual();
    }

    private void OnAnchorDeleted(string bookmarkId)
    {
        _anchors.Remove(bookmarkId);
        BookmarkService.SetUnresolved(bookmarkId, true);
        RefreshUnresolvedLines();
        InvalidateVisual();
    }

    private void RebuildAnchors()
    {
        DetachAnchors();
        _relativePath = null;

        var document = Document;
        if (document is null)
        {
            _unresolvedLines = new HashSet<int>();
            InvalidateVisual();
            return;
        }

        var relative = RelativePathFor(document);
        if (relative is null || document.LineCount == 0)
        {
            _unresolvedLines = new HashSet<int>();
            InvalidateVisual();
            return;
        }

        _relativePath = relative;

        var lines = new List<string>(document.LineCount);
        for (var line = 1; line <= document.LineCount; line++)
            lines.Add(document.GetText(document.GetLineByNumber(line)));

        BookmarkService.RecoverFile(relative, lines);

        foreach (var bookmark in BookmarkService.ForFile(relative))
        {
            if (bookmark.Unresolved) continue;
            if (bookmark.Line < 1 || bookmark.Line > document.LineCount) continue;

            try
            {
                var anchor = document.CreateAnchor(document.GetLineByNumber(bookmark.Line).Offset);
                anchor.MovementType = AnchorMovementType.AfterInsertion;
                anchor.SurviveDeletion = false;
                EventHandler handler = (_, _) => OnAnchorDeleted(bookmark.Id);
                anchor.Deleted += handler;
                _anchors[bookmark.Id] = (anchor, handler);
            }
            catch
            {
            }
        }

        RefreshUnresolvedLines();
        InvalidateVisual();
    }

    private void RefreshUnresolvedLines()
    {
        if (_relativePath is null)
        {
            _unresolvedLines = new HashSet<int>();
            return;
        }

        _unresolvedLines = BookmarkService.ForFile(_relativePath)
            .Where(b => b.Unresolved)
            .Select(b => b.Line)
            .ToHashSet();
    }

    private void DetachAnchors()
    {
        foreach (var (anchor, handler) in _anchors.Values)
            anchor.Deleted -= handler;
        _anchors.Clear();
    }

    private string? RelativePathFor(TextDocument document)
    {
        var path = FilePathProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
            path = document.FileName;
        return BookmarkService.TryMakeRelativePath(path, BookmarkService.CurrentWorkspaceRoot);
    }

    private static string? LineTextAt(TextDocument document, int line) =>
        line >= 1 && line <= document.LineCount
            ? document.GetText(document.GetLineByNumber(line))
            : null;
}
