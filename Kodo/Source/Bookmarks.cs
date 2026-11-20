// Licensed under the GNU GPL-v3.0
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Kodo;

public partial class MainWindow
{
    private bool _isBookmarksPanelOpen;

    public bool IsBookmarksPanelOpen
    {
        get => _isBookmarksPanelOpen;
        private set
        {
            if (_isBookmarksPanelOpen == value) return;
            _isBookmarksPanelOpen = value;
            OnPropertyChanged();
            SyncSidebarSplitLayout();
            if (value) RefreshBookmarksList();
        }
    }

    internal void SetBookmarksPanelOpen(bool isOpen)
    {
        if (isOpen && !IsEditorPageVisible)
            NavigateTo(AppPage.Editor);
        IsBookmarksPanelOpen = isOpen;
    }

    public ObservableCollection<BookmarkFileGroup> BookmarkGroups { get; } = new();

    public bool HasBookmarks => BookmarkGroups.Count > 0;

    public string BookmarksCountText
    {
        get
        {
            var count = BookmarkGroups.Sum(group => group.Items.Count);
            return count == 0 ? string.Empty : count.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void RefreshBookmarksList()
    {
        BookmarkGroups.Clear();

        if (!string.IsNullOrWhiteSpace(_currentFolderPath))
        {
            var bookmarks = BookmarkService.GetBookmarks()
                .OrderBy(bookmark => bookmark.RelativePath, FileSystemPaths.Comparer)
                .ThenBy(bookmark => bookmark.Line);

            BookmarkFileGroup? currentGroup = null;
            foreach (var bookmark in bookmarks)
            {
                if (currentGroup is null ||
                    !FileSystemPaths.Equals(currentGroup.RelativePath, bookmark.RelativePath))
                {
                    currentGroup = new BookmarkFileGroup
                    {
                        RelativePath = bookmark.RelativePath,
                        FileName = Path.GetFileName(bookmark.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                        DisplayPath = bookmark.RelativePath,
                        CountText = string.Empty,
                    };
                    BookmarkGroups.Add(currentGroup);
                }

                currentGroup.Items.Add(new CodeBookmarkListItem { Bookmark = bookmark });
            }

            foreach (var group in BookmarkGroups)
                group.CountText = group.Items.Count == 1 ? "1 bookmark" : $"{group.Items.Count} bookmarks";
        }

        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(BookmarksCountText));
        RefreshBookmarkMinimaps();
    }

    private string? ActiveRelativePath()
    {
        var path = PathForEditor(EditorTextBox) ?? _currentFilePath;
        return BookmarkService.TryMakeRelativePath(path, _currentFolderPath);
    }

    private string? PathForEditor(TextEditor editor)
    {
        EditorPaneState? pane = null;
        if (ReferenceEquals(editor, SecondaryEditorTextBox)) pane = _secondaryEditorPane;
        else if (ReferenceEquals(editor, PrimaryEditorTextBox)) pane = _editorPane;

        var path = pane?.ActiveTab is { IsUntitled: false } tab ? tab.Path : null;
        if (!string.IsNullOrWhiteSpace(path)) return path;

        return editor.Document?.FileName ?? (ReferenceEquals(editor, EditorTextBox) ? _currentFilePath : null);
    }

    private int ActiveCaretLine()
    {
        var document = EditorTextBox.Document;
        if (document is null) return 1;

        var offset = EditorTextBox.TextArea.Caret?.Offset ?? 0;
        offset = Math.Clamp(offset, 0, Math.Max(0, document.TextLength - 1));
        return document.GetLineByOffset(offset).LineNumber;
    }

    private async Task NavigateToBookmarkAsync(CodeBookmark bookmark)
    {
        var absolute = BookmarkService.ToAbsolutePath(bookmark.RelativePath);
        if (absolute is null || !File.Exists(absolute))
        {
            BookmarkService.SetUnresolved(bookmark.Id, true);
            RefreshBookmarksList();
            return;
        }

        await OpenFileFromPathAsync(absolute);

        var document = EditorTextBox.Document;
        if (document is null) return;

        GoToLineInActiveEditor(Math.Clamp(bookmark.Line, 1, document.LineCount), 1);
        RefreshBookmarksList();
    }

    private void ToggleBookmarkAtCaret()
    {
        if (!IsTextEditorVisible) return;
        ToggleBookmarkInEditor(EditorTextBox);
    }

    private void ToggleBookmarkInEditor(TextEditor editor, int? line = null)
    {
        var document = editor.Document;
        if (document is null) return;

        var relative = BookmarkService.TryMakeRelativePath(PathForEditor(editor), _currentFolderPath);
        if (relative is null) return;

        var lineNumber = line ?? 1;
        if (line is null)
        {
            var offset = editor.TextArea.Caret?.Offset ?? 0;
            offset = Math.Clamp(offset, 0, Math.Max(0, document.TextLength - 1));
            lineNumber = document.GetLineByOffset(offset).LineNumber;
        }

        lineNumber = Math.Clamp(lineNumber, 1, Math.Max(1, document.LineCount));
        var lineText = document.GetText(document.GetLineByNumber(lineNumber));

        BookmarkService.ToggleBookmark(relative, lineNumber, lineText);
        InvalidateBookmarkMargins();
        RefreshBookmarksList();
    }

    private void EnsureBookmarkMargin(TextEditor editor)
    {
        Func<string?> filePathProvider = () => PathForEditor(editor);

        var existing = editor.TextArea.LeftMargins.OfType<BookmarkLineNumberMargin>().FirstOrDefault();
        if (existing is not null)
        {
            existing.AccentBrushProvider = () => AccentBrush;
            existing.FilePathProvider = filePathProvider;
            return;
        }

        var margin = new BookmarkLineNumberMargin
        {
            AccentBrushProvider = () => AccentBrush,
            FilePathProvider = filePathProvider,
        };
        margin.BookmarkToggled += line => ToggleBookmarkAtLine(editor, line);

        var lineNumbers = editor.TextArea.LeftMargins.OfType<LineNumberMargin>().FirstOrDefault();
        if (lineNumbers is not null)
        {
            margin.SetValue(TextBlock.ForegroundProperty, lineNumbers.GetValue(TextBlock.ForegroundProperty));
            var index = editor.TextArea.LeftMargins.IndexOf(lineNumbers);
            editor.TextArea.LeftMargins[index] = margin;
        }
        else
        {
            margin.SetValue(TextBlock.ForegroundProperty, editor.LineNumbersForeground);
            editor.TextArea.LeftMargins.Insert(0, margin);
        }
    }

    private void ToggleBookmarkAtLine(TextEditor editor, int line)
    {
        if (IsFirstRunTutorialActive) return;
        ToggleBookmarkInEditor(editor, line);
    }

    private void MoveToBookmark(int direction)
    {
        var relative = ActiveRelativePath();
        if (relative is null) return;

        var line = ActiveCaretLine();
        int? target;
        if (direction > 0)
            target = BookmarkService.NextLine(relative, line) ?? BookmarkService.NextLine(relative, 0);
        else
            target = BookmarkService.PreviousLine(relative, line) ?? BookmarkService.PreviousLine(relative, int.MaxValue);

        if (target is { } destination)
            GoToLineInActiveEditor(destination, 1);
    }

    private void NextBookmark() => MoveToBookmark(1);

    private void PreviousBookmark() => MoveToBookmark(-1);

    private void ClearFileBookmarks()
    {
        var relative = ActiveRelativePath();
        if (relative is null) return;

        BookmarkService.RemoveAll(relative);
        InvalidateBookmarkMargins();
        RefreshBookmarksList();
    }

    private void InvalidateBookmarkMargins()
    {
        if (PrimaryEditorTextBox?.TextArea is { } primary)
            foreach (var margin in primary.LeftMargins.OfType<BookmarkLineNumberMargin>())
                margin.RefreshBookmarks();

        if (SecondaryEditorTextBox?.TextArea is { } secondary)
            foreach (var margin in secondary.LeftMargins.OfType<BookmarkLineNumberMargin>())
                margin.RefreshBookmarks();
    }

    private void UpdateEditorMinimapBookmarks(TextEditor? editor, Kodo.Renderers.EditorMinimapControl? minimap)
    {
        if (minimap is null) return;
        if (editor is null)
        {
            minimap.SetBookmarks(Array.Empty<int>(), Array.Empty<int>());
            return;
        }

        var lines = new List<int>();
        var unresolvedLines = new List<int>();
        var relative = BookmarkService.TryMakeRelativePath(PathForEditor(editor), _currentFolderPath);
        if (relative is not null)
        {
            foreach (var bookmark in BookmarkService.ForFile(relative))
            {
                if (bookmark.Unresolved) unresolvedLines.Add(bookmark.Line);
                else lines.Add(bookmark.Line);
            }
        }

        minimap.SetBookmarks(lines, unresolvedLines);
    }

    private void RefreshBookmarkMinimaps()
    {
        UpdateEditorMinimapBookmarks(PrimaryEditorTextBox, EditorMinimapView);
        UpdateEditorMinimapBookmarks(SecondaryEditorTextBox, SecondaryEditorMinimapView);
    }

    private void ShowBookmarks() => SetBookmarksPanelOpen(true);

    private void ToggleBookmarksPanel() => SetBookmarksPanelOpen(!IsBookmarksPanelOpen);

    private async void GoToBookmarkButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        await NavigateToBookmarkAsync(item.Bookmark);
    }

    private void RemoveBookmarkButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;

        BookmarkService.RemoveBookmarkById(item.Bookmark.Id);
        InvalidateBookmarkMargins();
        RefreshBookmarksList();
    }

    private async void ClearBookmarksButton_OnClick(object? sender, RoutedEventArgs e) =>
        await ClearAllBookmarksAsync();

    private async void ClearAllBookmarksMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        await ClearAllBookmarksAsync();

    private async Task ClearAllBookmarksAsync()
    {
        if (!HasBookmarks) return;

        var confirmed = await ShowConfirmationDialogAsync(
            "Clear all bookmarks?",
            "This removes every bookmark in the current workspace. The files themselves are not touched. This cannot be undone.",
            "Clear bookmarks",
            "Cancel",
            isDestructive: true);

        if (!confirmed) return;

        BookmarkService.Clear();
        BookmarkService.Flush();
        InvalidateBookmarkMargins();
        RefreshBookmarksList();
    }

    private void AddBookmarkButton_OnClick(object? sender, RoutedEventArgs e) => ToggleBookmarkAtCaret();

    private void NextBookmarkMenuItem_OnClick(object? sender, RoutedEventArgs e) => NextBookmark();

    private void PreviousBookmarkMenuItem_OnClick(object? sender, RoutedEventArgs e) => PreviousBookmark();

    private void RemoveCurrentFileBookmarksMenuItem_OnClick(object? sender, RoutedEventArgs e) => ClearFileBookmarks();

    private async void OpenBookmarkFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        await OpenBookmarkFileAsync(item.Bookmark);
    }

    private async void OpenBookmarkGroupMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<BookmarkFileGroup>(sender) is not { } group) return;
        var absolute = BookmarkService.ToAbsolutePath(group.RelativePath);
        if (absolute is null || !File.Exists(absolute)) return;
        await OpenFileFromPathAsync(absolute);
    }

    private void CopyBookmarkRelativePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(item.Bookmark.RelativePath);
    }

    private void CopyBookmarkGroupRelativePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<BookmarkFileGroup>(sender) is not { } group) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(group.RelativePath);
    }

    private void CopyBookmarkLineTextMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        if (string.IsNullOrEmpty(item.Preview)) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(item.Preview);
    }

    private async void RevealBookmarkMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        await RevealBookmarkPathAsync(item.Bookmark.RelativePath);
    }

    private async void RevealBookmarkGroupMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<BookmarkFileGroup>(sender) is not { } group) return;
        await RevealBookmarkPathAsync(group.RelativePath);
    }

    private async Task RevealBookmarkPathAsync(string relativePath)
    {
        var absolute = BookmarkService.ToAbsolutePath(relativePath);
        if (absolute is null || !File.Exists(absolute)) return;
        await OpenPathInSystemExplorer(absolute, selectItem: true);
    }

    private void EditBookmarkLabelMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        item.EditLabel = item.Bookmark.Label ?? string.Empty;
        item.IsEditingLabel = true;
    }

    private void BookmarkLabelTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not CodeBookmarkListItem item) return;
        if (e.Key == Key.Escape)
        {
            item.IsEditingLabel = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SaveBookmarkLabel(item);
            e.Handled = true;
        }
    }

    private void BookmarkLabelTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not CodeBookmarkListItem item || !item.IsEditingLabel) return;
        SaveBookmarkLabel(item);
    }

    private void SaveBookmarkLabel(CodeBookmarkListItem item)
    {
        BookmarkService.SetLabel(item.Bookmark.Id, item.EditLabel);
        item.IsEditingLabel = false;
        RefreshBookmarksList();
    }

    private void RemoveBookmarkFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<CodeBookmarkListItem>(sender) is not { } item) return;
        RemoveBookmarksForFile(item.Bookmark.RelativePath);
    }

    private void RemoveBookmarkGroupMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<BookmarkFileGroup>(sender) is not { } group) return;
        RemoveBookmarksForFile(group.RelativePath);
    }

    private void RemoveBookmarksForFile(string relativePath)
    {
        BookmarkService.RemoveAll(relativePath);
        InvalidateBookmarkMargins();
        RefreshBookmarksList();
    }

    private async Task OpenBookmarkFileAsync(CodeBookmark bookmark)
    {
        var absolute = BookmarkService.ToAbsolutePath(bookmark.RelativePath);
        if (absolute is null || !File.Exists(absolute))
        {
            BookmarkService.SetUnresolved(bookmark.Id, true);
            RefreshBookmarksList();
            return;
        }

        await OpenFileFromPathAsync(absolute);
    }
}
