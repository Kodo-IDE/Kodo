// Licensed under GPL-v3.0
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using Microsoft.Win32;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Animation;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using DiscordAssetsModel = DiscordRPC.Assets;
using DiscordRpcClient = DiscordRPC.DiscordRpcClient;
using DiscordRichPresenceModel = DiscordRPC.RichPresence;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{

    private static double NormalizeExplorerPanelWidth(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinExplorerPanelWidth, MaxExplorerPanelWidth)
            : AppSettings.DefaultExplorerPanelWidth;

    private void ExplorerPanelSplitter_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not InputElement element) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;

        _isResizingExplorerPanel = true;
        _explorerPanelDragStartPointerX = e.GetPosition(this).X;
        _explorerPanelDragStartWidth = ExplorerPanelWidth;
        e.Pointer.Capture(element);
        e.Handled = true;
    }

    private void ExplorerPanelSplitter_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        var deltaX = e.GetPosition(this).X - _explorerPanelDragStartPointerX;
        ExplorerPanelWidth = _explorerPanelDragStartWidth + deltaX;
        e.Handled = true;
    }

    private void ExplorerPanelSplitter_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        _isResizingExplorerPanel = false;
        e.Pointer.Capture(null);
        // Flushes the final width immediately instead of waiting on the debounce timer, in case of a quick resize-then-close.
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private void ExplorerPanelSplitter_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isResizingExplorerPanel) return;

        _isResizingExplorerPanel = false;
        SaveSettings(immediate: true);
    }

    private void ExplorerPanelSplitter_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ExplorerPanelWidth = ComputeAutoFitExplorerPanelWidth();
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private double ComputeAutoFitExplorerPanelWidth()
    {
        if (FileTreeItems.Count == 0) return AppSettings.DefaultExplorerPanelWidth;

        var typeface = new Typeface("Cascadia Code,Consolas,Menlo,Monospace");
        var widest = 0.0;

        foreach (var item in FileTreeItems)
        {
            var formatted = new FormattedText(
                item.Name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black);

            var total = item.IndentWidth + formatted.Width;
            if (total > widest) widest = total;
        }

        return NormalizeExplorerPanelWidth(widest + FileTreeRowFixedOverhead);
    }

    private void FileTreeItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressExplorerWidthRefresh)
            OnPropertyChanged(nameof(ExplorerPanelWidth));
    }

    private void SaveCurrentEditorStateIntoTab()
    {
        if (ActiveEditorTab is null || EditorTextBox?.Document is null)
            return;

        if (HasImagePreview)
        {
            ActiveEditorTab.IsDirty = false;
            if (!ActiveEditorTab.IsUntitled && !string.IsNullOrWhiteSpace(_currentFilePath))
                ActiveEditorTab.Path = _currentFilePath;
            return;
        }

        ActiveEditorTab.Content = EditorTextBox.Document.Text;
        ActiveEditorTab.IsDirty = _isDirty;
        var scrollOffset = EditorTextBox.TextArea.TextView.ScrollOffset;
        ActiveEditorTab.TopLineNumber = EditorTextBox.TextArea.TextView.GetDocumentLineByVisualTop(
            scrollOffset.Y)?.LineNumber ?? 1;
        // Also save the exact pixel offset so restoration is sub-line-accurate.
        ActiveEditorTab.ScrollOffsetY = scrollOffset.Y;
        ActiveEditorTab.CaretOffset = EditorTextBox.TextArea.Caret.Offset;
        if (!ActiveEditorTab.IsUntitled && !string.IsNullOrWhiteSpace(_currentFilePath))
            ActiveEditorTab.Path = _currentFilePath;
    }

    private EditorTab CreateUntitledTab()
    {
        var displayName = $"untitled-{_nextUntitledTabNumber++}.txt";
        return new EditorTab(displayName, displayName, string.Empty, isUntitled: true);
    }

    private void ActivateTab(EditorTab tab, bool focusEditor = true, bool preserveCurrentState = true)
    {
        if (ReferenceEquals(ActiveEditorTab, tab))
        {
            // Force page state even if NavigateTo bails early due to no change
            _isHomePageVisible = false;
            NavigateTo(AppPage.Editor);
            RefreshState(fullRefresh: true);
            if (focusEditor)
                FocusEditor();
            return;
        }

        CloseCompletionWindow();
        if (preserveCurrentState)
            SaveCurrentEditorStateIntoTab();
        ActiveEditorTab = tab;
        _currentFilePath = tab.IsUntitled ? null : tab.Path;
        _hasUntitledDocument = tab.IsUntitled;
        _isDirty = tab.IsDirty;
        _autoSaveTimer.Stop();
        ClearAutoSaveStatus();
        SetFileCorrupted(_corruptedTabs.Contains(tab));
        SetEditorContent(IsImagePreviewFile(_currentFilePath) ? string.Empty : tab.Content);
        // Restores this tab's caret position (clamped to the current document length).
        EditorTextBox.TextArea.Caret.Offset = Math.Clamp(tab.CaretOffset, 0, EditorTextBox.Document.TextLength);
        // Restores scroll position: ScrollToLine first, then a pixel offset at Background priority.
        EditorTextBox.ScrollToLine(tab.TopLineNumber);
        var savedOffsetY = tab.ScrollOffsetY;
        if (savedOffsetY > 0.0)
        {
            // Posts at Background priority so AvaloniaEdit finishes its layout pass before we reposition the viewport.
            Dispatcher.UIThread.Post(() =>
            {
                var sv = EditorTextBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (sv is not null)
                    sv.Offset = new Vector(sv.Offset.X, savedOffsetY);
            }, DispatcherPriority.Background);
        }
        UpdateCurrentDocumentPresentation();

        // Directly set the backing field before NavigateTo so the bail-early
        // check doesn't short-circuit when we're already on the editor page.
        _isHomePageVisible = false;
        NavigateTo(AppPage.Editor);
        RefreshState(fullRefresh: true);

        if (focusEditor)
            FocusEditor();
    }

    private void CloseTab(EditorTab tab)
    {
        var closingActiveTab = ReferenceEquals(tab, ActiveEditorTab);
        var index = OpenTabs.IndexOf(tab);
        if (index < 0)
            return;

        if (closingActiveTab)
            CloseCompletionWindow();
        _InsightEngine.ForgetFile(tab.Path);
        OpenTabs.RemoveAt(index);
        _corruptedTabs.Remove(tab);

        if (!closingActiveTab)
        {
            RefreshState(fullRefresh: true);
            return;
        }

        if (OpenTabs.Count > 0)
        {
            var nextIndex = Math.Min(index, OpenTabs.Count - 1);
            ActivateTab(OpenTabs[nextIndex], focusEditor: true, preserveCurrentState: false);
            return;
        }

        ActiveEditorTab = null;
        _currentFilePath = null;
        _hasUntitledDocument = false;
        _isDirty = false;
        CurrentLanguageExtension = null;
        CurrentImagePreview = null;
        SetFileCorrupted(false);
        EditorTextBox.SyntaxHighlighting = null;
        ConfigureRainbowBrackets(null);
        SetEditorContent(string.Empty);

        // No tabs and no folder left open - the explorer would otherwise be
        // stuck visible with nothing to show and no folder-gated toggle to
        // close it (that toggle only appears when IsFolderOpen).
        if (_currentFolderPath is null)
            IsFileExplorerVisible = false;

        RefreshState(fullRefresh: true);
    }

    private async Task<bool> RequestCloseTabAsync(EditorTab tab)
    {
        if (tab.IsDirty && IsConfirmBeforeClosingUnsavedTabsEnabled)
        {
            var originalActiveTab = ActiveEditorTab;
            var action = await ShowUnsavedTabDialogAsync(tab);
            switch (action)
            {
                case UnsavedTabAction.Cancel:
                    return false;
                case UnsavedTabAction.Save:
                    if (!ReferenceEquals(tab, ActiveEditorTab))
                    {
                        ActivateTab(tab, focusEditor: false);
                    }

                    await SaveAsync();

                    if (tab.IsDirty)
                    {
                        if (originalActiveTab is not null &&
                            !ReferenceEquals(originalActiveTab, tab) &&
                            OpenTabs.Contains(originalActiveTab))
                        {
                            ActivateTab(originalActiveTab, focusEditor: false);
                        }

                        return false;
                    }

                    if (originalActiveTab is not null &&
                        !ReferenceEquals(originalActiveTab, tab) &&
                        OpenTabs.Contains(originalActiveTab))
                    {
                        ActivateTab(originalActiveTab, focusEditor: false);
                    }
                    break;
            }
        }

        CloseTab(tab);
        return true;
    }

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = false
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        await OpenFileFromPathAsync(path);
    }

    private async Task OpenFileFromPathAsync(string path)
    {
        EnsureCurrentDocumentHasTab();

        var existingTab = OpenTabs.FirstOrDefault(tab =>
            !tab.IsUntitled &&
            string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existingTab is not null)
        {
            AddRecentFile(path);
            ActivateTab(existingTab);
            return;
        }

        string content;
        bool isCorrupted;
        try
        {
            if (IsImagePreviewFile(path))
            {
                content = string.Empty;
                isCorrupted = false;
                _currentFileEncoding = System.Text.Encoding.UTF8;
            }
            else
            {
                // Offload both the binary-sniff read and the encoding-BOM read to a
                // thread-pool thread so the UI stays responsive on large or slow files.
                var (encoding, corrupted) = await Task.Run(() =>
                {
                    if (IsBinaryContent(path))
                        return (System.Text.Encoding.UTF8, true);
                    return (DetectFileEncoding(path), false);
                });

                isCorrupted = corrupted;
                _currentFileEncoding = encoding;
                content = isCorrupted ? string.Empty : await File.ReadAllTextAsync(path, encoding);
            }
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Open file", ex);
            return;
        }

        // Navigates away from Home before adding the tab (mirrors NewFile()).
        NavigateTo(AppPage.Editor);

        var tab = new EditorTab(path, Path.GetFileName(path), content);
        if (isCorrupted)
            _corruptedTabs.Add(tab);
        OpenTabs.Add(tab);
        AddRecentFile(path);
        ActivateTab(tab);
    }

    private async Task OpenFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Folder",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null) return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        await OpenFolderFromPathAsync(path);
    }

    private async Task PopulateFileTreeAsync(string folderPath)
    {
        var items = await CreateFileTreeItemsAsync(folderPath, depth: 0);
        ReplaceFileTreeItems(items);
    }

    private void SetupProjectFolderWatcher(string folderPath)
    {
        DisposeProjectFolderWatcher();

        try
        {
            var watcher = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Created += ProjectFolderWatcher_OnChanged;
            watcher.Deleted += ProjectFolderWatcher_OnChanged;
            watcher.Renamed += ProjectFolderWatcher_OnChanged;
            watcher.Error   += ProjectFolderWatcher_OnError;
            watcher.EnableRaisingEvents = true;

            _projectFolderWatcher = watcher;
        }
        catch
        {
            // Some folders (network shares, permission-restricted directories, huge
            // trees) can't be watched - the explorer just won't auto-refresh for them.
        }
    }

    private void DisposeProjectFolderWatcher()
    {
        _fileTreeRefreshTimer.Stop();

        if (_projectFolderWatcher is null) return;

        _projectFolderWatcher.Created -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Deleted -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Renamed -= ProjectFolderWatcher_OnChanged;
        _projectFolderWatcher.Error   -= ProjectFolderWatcher_OnError;
        _projectFolderWatcher.Dispose();
        _projectFolderWatcher = null;
    }

    private void ProjectFolderWatcher_OnChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(RestartFileTreeRefreshTimer);

    private void RestartFileTreeRefreshTimer()
    {
        _fileTreeRefreshTimer.Stop();
        _fileTreeRefreshTimer.Start();
    }

    private async void FileTreeRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _fileTreeRefreshTimer.Stop();
        _searchFileCache = null;
        await RefreshFileTreePreservingExpansionAsync();
    }

    private async Task RefreshFileTreePreservingExpansionAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath) || !Directory.Exists(_currentFolderPath))
            return;

        var expandedPaths = FileTreeItems
            .Where(i => i.IsDirectory && i.IsExpanded)
            .Select(i => i.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = await BuildFileTreeItemsAsync(_currentFolderPath, depth: 0, expandedPaths);
        ReplaceFileTreeItems(items);
    }

    private static async Task<List<FileTreeItem>> BuildFileTreeItemsAsync(
        string dirPath, int depth, HashSet<string> expandedPaths)
    {
        var result = new List<FileTreeItem>();
        foreach (var item in await CreateFileTreeItemsAsync(dirPath, depth))
        {
            result.Add(item);
            if (item.IsDirectory && expandedPaths.Contains(item.FullPath))
            {
                item.IsExpanded = true;
                result.AddRange(await BuildFileTreeItemsAsync(item.FullPath, depth + 1, expandedPaths));
            }
        }
        return result;
    }

    private async Task AppendDirectoryContentsAsync(string dirPath, int depth, int insertAfterIndex = -1)
    {
        var items = await CreateFileTreeItemsAsync(dirPath, depth);
        if (items.Count == 0) return;

        var pos = insertAfterIndex + 1;
        foreach (var item in items)
        {
            if (insertAfterIndex < 0)
                FileTreeItems.Add(item);
            else
            {
                FileTreeItems.Insert(pos, item);
                pos++;
            }
        }
    }

    private void ReplaceFileTreeItems(IReadOnlyList<FileTreeItem> items)
    {
        _suppressExplorerWidthRefresh = true;
        try
        {
            FileTreeItems.Clear();
            foreach (var item in items)
                FileTreeItems.Add(item);
        }
        finally
        {
            _suppressExplorerWidthRefresh = false;
            OnPropertyChanged(nameof(ExplorerPanelWidth));
        }
    }

    private static Task<List<FileTreeItem>> CreateFileTreeItemsAsync(string dirPath, int depth) =>
        Task.Run(() => GetSortedEntries(dirPath)
            .Select(entry => new FileTreeItem
            {
                Name        = Path.GetFileName(entry),
                FullPath    = entry,
                IsDirectory = Directory.Exists(entry),
                Depth       = depth,
            })
            .ToList());

    private async Task ToggleDirectoryExpansionAsync(FileTreeItem dirItem)
    {
        var index = FileTreeItems.IndexOf(dirItem);
        if (index < 0) return;

        if (dirItem.IsExpanded)
        {
            dirItem.IsExpanded = false;
            // Collect all descendants first, then remove in reverse index order
            // so each removal doesn't shift the indices of subsequent items.
            var toRemove = FileTreeItems
                .Skip(index + 1)
                .TakeWhile(i => i.Depth > dirItem.Depth)
                .ToList();
            for (var i = toRemove.Count - 1; i >= 0; i--)
                FileTreeItems.Remove(toRemove[i]);
        }
        else
        {
            dirItem.IsExpanded = true;
            await AppendDirectoryContentsAsync(dirItem.FullPath, dirItem.Depth + 1, insertAfterIndex: index);
        }
    }

    private void LoadRecentFiles(IEnumerable<RecentFileEntry>? recentFiles)
    {
        RecentFiles.Clear();

        // Doesn't filter files under a recent folder's tree - only active-folder files collapse.
        // Doesn't filter by File/Directory.Exists - unreachable paths should still reappear.
        foreach (var entry in (recentFiles ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .OrderByDescending(entry => entry.IsPinned)
            .ThenByDescending(entry => entry.LastOpened))
        {
            RecentFiles.Add(new RecentFileItem(entry.Path, entry.IsFolder, entry.LastOpened, entry.IsPinned));
        }
    }

    private void AddRecentFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            !string.IsNullOrWhiteSpace(_currentFolderPath) &&
            IsPathInsideDirectory(path, _currentFolderPath))
        {
            AddRecentFolder(_currentFolderPath);
            return;
        }

        AddRecentPath(path, isFolder: false);
    }

    private void AddRecentFolder(string? path) =>
        AddRecentPath(path, isFolder: true);

    private void AddRecentPath(string? path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = RecentFiles.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            RecentFiles.Remove(existing);
            RecentFiles.Add(new RecentFileItem(path, isFolder, DateTime.Now, existing.IsPinned));
        }
        else
        {
            RecentFiles.Add(new RecentFileItem(path, isFolder, DateTime.Now));
        }

        ReorderRecentFiles();
        SaveSettings();
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private void TogglePinnedRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;

        existing.IsPinned = !existing.IsPinned;
        ReorderRecentFiles();
        SaveSettings();
    }

    private void RemoveRecentFile(string path)
    {
        var existing = RecentFiles.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        RecentFiles.Remove(existing);
        SaveSettings();
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private void ReorderRecentFiles()
    {
        var ordered = RecentFiles
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastOpened)
            .ToList();

        RecentFiles.Clear();
        foreach (var item in ordered)
            RecentFiles.Add(item);

        while (RecentFiles.Count(item => !item.IsPinned) > MaxRecentFiles)
        {
            var removable = RecentFiles.LastOrDefault(item => !item.IsPinned);
            if (removable is null)
                break;

            RecentFiles.Remove(removable);
        }
    }

    private void ZoomInButton_OnClick(object? sender, RoutedEventArgs e)  => ZoomImageIn();

    private void ZoomOutButton_OnClick(object? sender, RoutedEventArgs e) => ZoomImageOut();

    private void ZoomResetButton_OnClick(object? sender, RoutedEventArgs e) => ZoomImageReset();

    private void ZoomImageIn()  => ImageZoomLevel = SnapToNiceZoom(_imageZoomLevel + ImageZoomStep);

    private void ZoomImageOut() => ImageZoomLevel = SnapToNiceZoom(_imageZoomLevel - ImageZoomStep);

    private void ZoomImageReset() => ImageZoomLevel = 1.0;

    private static double SnapToNiceZoom(double zoom)
    {
        var snapped = Math.Round(zoom / ImageZoomStep) * ImageZoomStep;
        return Math.Clamp(snapped, ImageZoomMin, ImageZoomMax);
    }

    private void ImageScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!HasImagePreview) return;
        var hasControl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (!hasControl) return;

        // Ctrl+wheel → zoom. Mark handled so the ScrollViewer does NOT also scroll.
        if (e.Delta.Y > 0)
            ZoomImageIn();
        else if (e.Delta.Y < 0)
            ZoomImageOut();

        e.Handled = true;
    }

    private void CollapseExplorerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        // Only toggles panel visibility - previously wiped folder state via CloseFolder().
        IsFileExplorerVisible = !IsFileExplorerVisible;
    }

    private async void FileTreeItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileTreeItem item })
        {
            if (item.IsDirectory)
                await ToggleDirectoryExpansionAsync(item);
            else
                await OpenFileFromPathAsync(item.FullPath);
        }
    }

    private async void RecentFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentFileItem item }) return;

        if (item.IsFolder)
        {
            if (!Directory.Exists(item.Path))
            {
                await ShowNotFoundDialogAsync(item.Path, isFolder: true);
                return;
            }
            _currentFolderPath = item.Path;
            _searchFileCache = null;
            AddRecentFolder(item.Path);
            await PopulateFileTreeAsync(item.Path);
            SetupProjectFolderWatcher(item.Path);
            IsFileExplorerVisible = true;
            RefreshState(fullRefresh: true);
        }
        else
        {
            if (!File.Exists(item.Path))
            {
                await ShowNotFoundDialogAsync(item.Path, isFolder: false);
                return;
            }
            await OpenFileFromPathAsync(item.Path);
        }
    }

    private void TogglePinnedRecentFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        TogglePinnedRecentFile(path);
        e.Handled = true;
    }

    private void OpenEditorTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorTab tab })
            ActivateTab(tab);
    }

    private async void CloseEditorTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is { } tab)
            await RequestCloseTabAsync(tab);
    }

    private string GetRelativePathOrFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath))
            return path;

        try
        {
            return Path.GetRelativePath(_currentFolderPath, path);
        }
        catch
        {
            return path;
        }
    }

    private string GetExplorerTargetDirectory(FileTreeItem item) =>
        item.IsDirectory
            ? item.FullPath
            : Path.GetDirectoryName(item.FullPath) ?? _currentFolderPath ?? item.FullPath;

    private string GetExplorerRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath))
            throw new InvalidOperationException("No folder is currently open in the explorer.");

        return _currentFolderPath;
    }

    private static string CreateUniqueSiblingPath(string path, bool isDirectory)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileName(path);
        string extension;
        string baseName;

        if (isDirectory || (fileName.StartsWith('.') && fileName.Count(ch => ch == '.') == 1))
        {
            extension = string.Empty;
            baseName = fileName;
        }
        else
        {
            extension = Path.GetExtension(fileName);
            baseName = Path.GetFileNameWithoutExtension(fileName);
        }

        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? " - Copy" : $" - Copy ({index})";
            var candidate = Path.Combine(directory, $"{baseName}{suffix}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            var destinationChild = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectoryRecursive(directory, destinationChild);
        }
    }

    private async Task OpenPathInSystemExplorer(string path, bool selectItem)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startInfo = selectItem && File.Exists(path)
                    ? new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    }
                    : new ProcessStartInfo
                    {
                        FileName = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path,
                        UseShellExecute = true
                    };

                Process.Start(startInfo);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                // `open -R <path>` reveals and selects the item in Finder.
                // Falls back to opening the parent directory if path doesn't exist.
                var target = selectItem && (File.Exists(path) || Directory.Exists(path)) ? path
                    : Directory.Exists(path) ? path
                    : Path.GetDirectoryName(path) ?? path;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{target}\"",
                    UseShellExecute = false
                });
                return;
            }

            // Linux: try common file managers that support --select / reveal flags,
            // then fall back to opening the parent directory via xdg-open.
            if (OperatingSystem.IsLinux() && selectItem && File.Exists(path))
            {
                var fileManagers = new[]
                {
                    ("nautilus", $"--select \"{path}\""),   // GNOME
                    ("dolphin",  $"--select \"{path}\""),   // KDE
                    ("nemo",     $"\"{path}\""),             // Cinnamon
                    ("thunar",   $"\"{Path.GetDirectoryName(path)}\""), // XFCE (no select)
                };

                foreach (var (binary, args) in fileManagers)
                {
                    // Check the binary exists before trying to launch it.
                    var which = Process.Start(new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = binary,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    });
                    which?.WaitForExit();
                    if (which?.ExitCode != 0) continue;

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = binary,
                        Arguments = args,
                        UseShellExecute = false
                    });
                    return;
                }
            }

            // Universal fallback: open the containing directory.
            var fallbackDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
            Process.Start(new ProcessStartInfo { FileName = fallbackDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Could not open path: {ex.Message}";
            await ShowWarningDialogAsync("Open in system explorer", ex);
        }
    }

    private async Task RefreshExplorerTreeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
        {
            // Snapshot which directories are expanded before wiping the tree,
            // then restore them afterward so the user's expansion state is preserved.
            var expandedPaths = FileTreeItems
                .Where(i => i.IsDirectory && i.IsExpanded)
                .Select(i => i.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await PopulateFileTreeAsync(_currentFolderPath);

            if (expandedPaths.Count > 0)
                await RestoreExpandedPathsAsync(expandedPaths);
        }
    }

    private async Task<bool> CloseTabsForPathAsync(string path, bool isDirectory)
    {
        var matchingTabs = OpenTabs
            .Where(tab => !tab.IsUntitled && (
                string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase) ||
                (isDirectory && tab.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        foreach (var tab in matchingTabs)
        {
            if (!await RequestCloseTabAsync(tab))
                return false;
        }

        return true;
    }

    private void CloseTabsWithoutPrompt(IEnumerable<EditorTab> tabs)
    {
        foreach (var tab in tabs.ToList())
            CloseTab(tab);
    }

    private async Task<string?> ShowRenameDialogAsync(string currentName)
    {
        string? result = null;
        Window? dialog = null;

        var inputBox = new TextBox
        {
            Text             = currentName,
            Background       = ButtonBrush,
            Foreground       = PrimaryTextBrush,
            BorderBrush      = SurfaceBorderBrush,
            Padding          = new Thickness(8, 6),
            FontSize         = 14,
            CaretBrush       = PrimaryTextBrush,
        };

        var confirmButton = CreateDialogButton("Rename", AccentBrush, AccentBrush, AccentForegroundBrush, () =>
        {
            result = inputBox.Text?.Trim();
            dialog!.Close();
        });

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)  { result = inputBox.Text?.Trim(); dialog!.Close(); }
            if (e.Key == Key.Escape) { dialog!.Close(); }
        };

        dialog = new Window
        {
            Width                   = 380,
            Height                  = 160,
            CanResize               = false,
            ShowInTaskbar           = false,
            WindowStartupLocation   = WindowStartupLocation.CenterOwner,
            Title                   = "Rename",
            Background              = CardBrush,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child   = new StackPanel
                {
                    Spacing  = 14,
                    Children =
                    {
                        new TextBlock { Text = "Enter a new name:", FontSize = 15,
                            FontWeight = FontWeight.SemiBold, Foreground = PrimaryTextBrush },
                        inputBox,
                        new StackPanel
                        {
                            Orientation         = Orientation.Horizontal,
                            Spacing             = 10,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children =
                            {
                                CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush,
                                    () => dialog!.Close()),
                                confirmButton
                            }
                        }
                    }
                }
            }
        };

        dialog.Opened += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        await dialog.ShowDialog(this);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private async void CloseTabsToTheRightMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { } pivotTab) return;
        var pivotIndex = OpenTabs.IndexOf(pivotTab);
        if (pivotIndex < 0) return;

        var toClose = OpenTabs.Skip(pivotIndex + 1).ToList();
        foreach (var tab in toClose)
        {
            if (!await RequestCloseTabAsync(tab))
                break;
        }
    }

    private async void RevealEditorTabInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<EditorTab>(sender) is not { IsUntitled: false } tab) return;
        await OpenPathInSystemExplorer(tab.Path, selectItem: true);
    }

    private async void NewFileInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var directory = GetExplorerTargetDirectory(item);
            var newFilePath = CreateUniqueChildPath(directory, "new-file", ".txt");
            await File.WriteAllTextAsync(newFilePath, string.Empty);
            await RefreshExplorerTreeAsync();
            await OpenFileFromPathAsync(newFilePath);
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New file failed: {ex.Message}";
            await ShowWarningDialogAsync("New file in explorer", ex);
        }
    }

    private async void ExplorerHeaderNewFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var directory = GetExplorerRootDirectory();
            var newFilePath = CreateUniqueChildPath(directory, "new-file", ".txt");
            await File.WriteAllTextAsync(newFilePath, string.Empty);
            await RefreshExplorerTreeAsync();
            await OpenFileFromPathAsync(newFilePath);
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New file failed: {ex.Message}";
            await ShowWarningDialogAsync("New file in explorer", ex);
        }
    }

    private async void NewFolderInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var directory = GetExplorerTargetDirectory(item);
            var newFolderPath = CreateUniqueChildPath(directory, "New Folder");
            Directory.CreateDirectory(newFolderPath);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New folder failed: {ex.Message}";
            await ShowWarningDialogAsync("New folder in explorer", ex);
        }
    }

    private async void ExplorerHeaderNewFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var directory = GetExplorerRootDirectory();
            var newFolderPath = CreateUniqueChildPath(directory, "New Folder");
            Directory.CreateDirectory(newFolderPath);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"New folder failed: {ex.Message}";
            await ShowWarningDialogAsync("New folder in explorer", ex);
        }
    }

    private async void OpenExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        if (item.IsDirectory)
            await ToggleDirectoryExpansionAsync(item);
        else
            await OpenFileFromPathAsync(item.FullPath);
    }

    private async void RevealExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        await OpenPathInSystemExplorer(item.FullPath, selectItem: !item.IsDirectory);
    }

    private void OpenExplorerItemInTerminalMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        var directory = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        CreateTerminalSession(workingDirectoryOverride: directory);
    }

    private void CopyFileNameMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(item.Name);
    }

    private void CopyFilePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is { } item)
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(item.FullPath);
    }

    private async void DuplicateExplorerItemMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;

        try
        {
            var duplicatePath = CreateUniqueSiblingPath(item.FullPath, item.IsDirectory);
            if (item.IsDirectory)
                CopyDirectoryRecursive(item.FullPath, duplicatePath);
            else
                File.Copy(item.FullPath, duplicatePath, overwrite: false);

            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Duplicate failed: {ex.Message}";
            await ShowWarningDialogAsync("Duplicate file", ex);
        }
    }

    private async void DeleteFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        try
        {
            var kind = item.IsDirectory ? "folder" : "file";
            var confirmed = await ShowConfirmationDialogAsync(
                $"Delete {kind}?",
                item.IsDirectory
                    ? $"This permanently deletes '{item.Name}' and everything inside it from disk. This can't be undone."
                    : $"This permanently deletes '{item.Name}' from disk. This can't be undone.",
                confirmLabel: "Delete",
                isDestructive: true);

            if (!confirmed) return;

            var matchingTabs = OpenTabs
                .Where(tab => !tab.IsUntitled && (
                    string.Equals(tab.Path, item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    (item.IsDirectory && tab.Path.StartsWith(item.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (!await EnsureTabsReadyForDeletionAsync(matchingTabs))
                return;

            if (item.IsDirectory)
                Directory.Delete(item.FullPath, recursive: true);
            else
                File.Delete(item.FullPath);

            CloseTabsWithoutPrompt(matchingTabs);
            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Delete failed: {ex.Message}";
            await ShowWarningDialogAsync("Delete file", ex);
        }
    }

    private void CutFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        _clipboardItemPath        = item.FullPath;
        _clipboardItemIsDirectory = item.IsDirectory;
        _clipboardIsCut           = true;
        ExtensionsStatusText      = $"Cut: {item.Name}";
    }

    private void CopyFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        _clipboardItemPath        = item.FullPath;
        _clipboardItemIsDirectory = item.IsDirectory;
        _clipboardIsCut           = false;
        ExtensionsStatusText      = $"Copied: {item.Name}";
    }

    private async void PasteFileMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_clipboardItemPath is null) return;
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } target) return;

        // Paste destination is the target's directory (or the target itself if it's a folder)
        var destDir = target.IsDirectory ? target.FullPath : Path.GetDirectoryName(target.FullPath)!;
        var itemName = Path.GetFileName(_clipboardItemPath.TrimEnd(Path.DirectorySeparatorChar));
        var destPath = CreateUniqueSiblingPath(Path.Combine(destDir, itemName), _clipboardItemIsDirectory);

        try
        {
            if (_clipboardIsCut)
            {
                if (_clipboardItemIsDirectory)
                    Directory.Move(_clipboardItemPath, destPath);
                else
                    File.Move(_clipboardItemPath, destPath);

                RetargetTabPaths(_clipboardItemPath, destPath, _clipboardItemIsDirectory);
                _clipboardItemPath = null; // cut is consumed
            }
            else
            {
                if (_clipboardItemIsDirectory)
                    CopyDirectoryRecursive(_clipboardItemPath, destPath);
                else
                    File.Copy(_clipboardItemPath, destPath, overwrite: false);
            }

            await RefreshExplorerTreeAsync();
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Paste failed: {ex.Message}";
            await ShowWarningDialogAsync("Paste file", ex);
        }
    }

    private async void ClearRecentFilesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!HasRecentFiles) return;

        // "Clear Recent Files" only removes non-pinned entries.
        var clearable = RecentFiles.Where(f => !f.IsPinned).ToList();
        if (clearable.Count == 0) return;

        var confirmed = await ShowConfirmationDialogAsync(
            "Clear Recent Files?",
            "This removes every non-pinned entry from your Recent Files list. Pinned entries are kept. The files themselves aren't touched - only the list of shortcuts to them. This can't be undone.",
            confirmLabel: "Clear",
            isDestructive: true);

        if (!confirmed) return;

        foreach (var item in clearable)
            RecentFiles.Remove(item);

        SaveSettings();
        OnPropertyChanged(nameof(HasRecentFiles));
    }

}
