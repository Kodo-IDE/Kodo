// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{

    private void RestartSearchFilterDebounce()
    {
        _searchFilterDebounceTimer.Stop();
        _searchFilterDebounceTimer.Start();
    }

    private void SearchFilterDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _searchFilterDebounceTimer.Stop();
        FlushPendingSearchFilters();
    }

    private void FlushPendingSearchFilters()
    {
        if (_extensionSearchPending)
        {
            _extensionSearchPending = false;
            NotifyExtensionFiltersChanged();
        }
        if (_settingsSearchPending)
        {
            _settingsSearchPending = false;
            NotifySettingsSearchChanged();
        }
    }

    private void SettingsSearchTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_searchFilterDebounceTimer.IsEnabled)
            _searchFilterDebounceTimer.Stop();
        if (_settingsSearchPending)
            FlushPendingSearchFilters();
    }

    private static void CollectSearchableText(StyledElement element, StringBuilder sb)
    {
        if (element is TextBlock { Text: { Length: > 0 } text })
            sb.Append(text).Append(' ');

        if (element is HeaderedContentControl { Header: string header } && !string.IsNullOrWhiteSpace(header))
            sb.Append(header).Append(' ');

        if (element is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
            sb.Append(content).Append(' ');

        if (element is TextBox { PlaceholderText: { Length: > 0 } placeholder })
            sb.Append(placeholder).Append(' ');

        if (element is Control control && ToolTip.GetTip(control) is string tip && !string.IsNullOrWhiteSpace(tip))
            sb.Append(tip).Append(' ');
    }

    private static string GetSettingsCardSearchText(Control? card)
    {
        if (card is null)
            return string.Empty;

        var sb = new StringBuilder();
        CollectSearchableText(card, sb);
        foreach (var descendant in card.GetVisualDescendants())
        {
            if (descendant is StyledElement styled)
                CollectSearchableText(styled, sb);
        }

        return sb.ToString();
    }

    private bool MatchesSettingsSearchCard(Control? card)
    {
        return string.IsNullOrWhiteSpace(_settingsSearchText) ||
               GetSettingsCardSearchText(card).Contains(_settingsSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifySettingsSearchChanged()
    {
        var cards = SettingsCardsPanel.Children
            .OfType<Control>()
            .Where(c => c.Name != "SettingsSearchEmptyPlaceholder" &&
                        (c.Name is null || !c.Name.StartsWith("SectionHeader", StringComparison.Ordinal)))
            .ToList();
        var headers = SettingsCardsPanel.Children
            .OfType<Control>()
            .Where(c => c.Name?.StartsWith("SectionHeader", StringComparison.Ordinal) == true)
            .ToList();
        var groupVisible = cards
            .GroupBy(SettingsCardGroupKey)
            .ToDictionary(g => g.Key, g => g.Any(MatchesSettingsSearchCard));

        var anyVisible = false;
        foreach (var card in cards)
        {
            var visible = groupVisible[SettingsCardGroupKey(card)];
            card.IsVisible = visible;
            anyVisible |= visible;
        }

        foreach (var header in headers)
        {
            var sectionTag = header.Tag as string;
            if (sectionTag is null)
            {
                header.IsVisible = true;
                continue;
            }
            header.IsVisible = cards.Any(c => c.Tag as string == sectionTag && c.IsVisible);
        }

        _isSettingsSearchEmpty = !string.IsNullOrWhiteSpace(_settingsSearchText) && !anyVisible;
        OnPropertyChanged(nameof(IsSettingsSearchEmptyVisible));
    }

    private void SearchInFolderMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<FileTreeItem>(sender) is not { } item) return;
        if (_currentFolderPath is null) return;

        // Compute relative path from project root to the selected item.
        var relativePath = GetRelativePathOrName(_currentFolderPath, item.FullPath);
        if (!string.IsNullOrEmpty(relativePath))
        {
            // If it's a directory, append /** glob; if it's a file, just
            SearchIncludeFilter = item.IsDirectory
                ? relativePath.Replace('\\', '/') + "/**"
                : relativePath.Replace('\\', '/');
        }

        OpenSearchPanel(SearchMode.ProjectSearch);
    }

    private void OpenSearchMenu_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.FindInFile);

    private void SearchModeFindInFileButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.FindInFile);

    private void SearchModeFileByNameButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.FileByName);

    private void SearchModeProjectButton_OnClick(object? sender, RoutedEventArgs e) =>
        OpenSearchPanel(SearchMode.ProjectSearch);

    private void SearchMatchCaseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchMatchCaseEnabled = !IsSearchMatchCaseEnabled;
        if (IsFindInFileSearchMode)
        {
            UpdateFindHighlights();
            FocusSearchInput();
        }
        else if (IsSearchPanelActive)
            RestartSearchDebounce();
    }

    private void SearchWholeWordButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchWholeWordEnabled = !IsSearchWholeWordEnabled;
        if (IsFindInFileSearchMode)
        {
            UpdateFindHighlights();
            FocusSearchInput();
        }
        else if (IsSearchPanelActive)
            RestartSearchDebounce();
    }

    private void SearchRegexButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchRegexEnabled = !IsSearchRegexEnabled;
        if (IsFindInFileSearchMode)
        {
            UpdateFindHighlights();
            FocusSearchInput();
        }
        else if (IsSearchPanelActive)
            RestartSearchDebounce();
    }

    private void SearchFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsFilterRowVisible = !IsFilterRowVisible;
        OnPropertyChanged(nameof(IsFilterRowSupported));
        if (IsFilterRowVisible && IsProjectSearchMode)
            RestartSearchDebounce();
    }

    private void ReplaceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReplaceCurrentMatch();
    }

    private async void ReplaceAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var count = CountCurrentMatches();
        if (count == 0)
        {
            SearchStatusText = "No matches to replace.";
            return;
        }

        var matchWord = count == 1 ? "match" : "matches";
        var confirmed = await ShowConfirmationDialogAsync(
            "Replace All?",
            $"This will replace {count} {matchWord} in the current file. This action can be undone with Ctrl+Z.",
            confirmLabel: "Replace All");

        if (confirmed)
            ReplaceAllMatches();
    }

    private int CountCurrentMatches()
    {
        if (string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return 0;

        var text = EditorTextBox.Document.Text;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        var count = 0;
        var searchIndex = 0;
        while (searchIndex <= text.Length)
        {
            var m = FindNextMatch(text, FindText, searchIndex, forward: true, comparison, IsSearchWholeWordEnabled, regex);
            if (m.Offset < 0) break;
            count++;
            searchIndex = m.Offset + m.Length;
            if (m.Length == 0) break;
        }
        return count;
    }

    private void CloseSearchPanel_OnClick(object? sender, RoutedEventArgs e)
    {
        IsSearchPanelVisible = false;
        FocusEditor();
    }

    private void SearchResultsListBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        if (e.Key == Key.Enter)
        {
            if (listBox.SelectedItem is SearchDisplayItem { IsGroupHeader: true, Group: { } group })
                ToggleGroup(group);
            else
                OpenSelectedSearchResult();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            IsSearchPanelVisible = false;
            FocusEditor();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            int nextIndex = FindNextResultIndex(listBox.SelectedIndex, e.Key == Key.Down ? 1 : -1);
            if (nextIndex >= 0)
            {
                listBox.SelectedIndex = nextIndex;
                e.Handled = true;
            }
            else
            {
                FocusSearchInput();
                ResetHistoryIndex();
                e.Handled = true;
            }
        }
    }

    private int FindNextResultIndex(int current, int direction)
    {
        var items = SearchDisplayItems;
        var next = current + direction;
        while (next >= 0 && next < items.Count)
        {
            if (!items[next].IsGroupHeader)
                return next;
            next += direction;
        }
        return -1;
    }

    private void SearchResultsListBox_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenSelectedSearchResult();
    }

    private void SearchResultsListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsListBox?.SelectedItem is SearchDisplayItem { IsGroupHeader: false, Result: { } item } displayItem)
        {
            var index = SearchDisplayItems.IndexOf(displayItem);
            if (index >= 0)
                SearchResultsListBox.ScrollIntoView(index);
        }
    }

    private void SearchGroupHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchFileGroup group })
            ToggleGroup(group);
    }

    private void OpenSelectedSearchResult()
    {
        if (SearchResultsListBox?.SelectedItem is not SearchDisplayItem { IsGroupHeader: false, Result: { } result })
            return;

        OpenSearchResult(result);
    }

    private async void OpenSearchResult(SearchResultItem result)
    {
        await OpenFileFromPathAsync(result.Path);

        if (result.LineNumber > 0 && EditorTextBox?.Document is { } doc)
        {
            var lineNumber = Math.Clamp(result.LineNumber, 1, doc.LineCount);
            var line = doc.GetLineByNumber(lineNumber);
            var matchOffset = string.IsNullOrEmpty(FindText)
                ? -1
                : doc.GetText(line.Offset, line.Length).IndexOf(FindText, StringComparison.OrdinalIgnoreCase);

            Dispatcher.UIThread.Post(() =>
            {
                var caretOffset = matchOffset >= 0 ? line.Offset + matchOffset : line.Offset;
                EditorTextBox.TextArea.Caret.Offset = caretOffset;
                if (matchOffset >= 0 && !string.IsNullOrEmpty(FindText))
                {
                    EditorTextBox.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(
                        EditorTextBox.TextArea, caretOffset, caretOffset + FindText.Length);
                }
                else
                {
                    EditorTextBox.TextArea.ClearSelection();
                }
                EditorTextBox.ScrollToLine(lineNumber);
                FocusEditor();
            }, DispatcherPriority.Background);
        }
    }

    private void OpenSearchResultMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<SearchResultItem>(sender) is not { } result) return;
        OpenSearchResult(result);
    }

    private void CopySearchResultPathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<SearchResultItem>(sender) is not { } result) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(result.Path);
    }

    private void CopySearchResultRelativePathMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<SearchResultItem>(sender) is not { } result) return;
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(GetRelativePathOrFullPath(result.Path));
    }

    private async void RevealSearchResultInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TryGetTaggedData<SearchResultItem>(sender) is not { } result) return;
        await OpenPathInSystemExplorer(result.Path, selectItem: true);
    }

    private void OpenSearchPanel(SearchMode mode)
    {
        if (!CanShowSearchPanelForMode(mode))
            return;

        _searchMode = mode;
        NotifySearchModeChanged();
        IsSearchPanelVisible = true;
        _searchResults.Clear();
        _searchDisplayItems.Clear();
        SearchResultsListBox!.SelectedIndex = -1;
        SearchStatusText = string.Empty;
        if (mode == SearchMode.FindInFile)
            UpdateFindHighlights();
        else
            _ = RunActiveSearchAsync();
        FocusSearchInput();
    }

    private void ToggleSearchPanel(SearchMode mode)
    {
        if (IsSearchPanelVisible && _searchMode == mode)
        {
            IsSearchPanelVisible = false;
            FocusEditor();
            return;
        }

        OpenSearchPanel(mode);
    }

    private void SwitchSearchMode(SearchMode mode)
    {
        ResetHistoryIndex();
        _searchMode = mode;
        NotifySearchModeChanged();

        if (!CanShowSearchPanelForMode(mode))
        {
            _searchCancellation?.Cancel();
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            SearchResultsListBox!.SelectedIndex = -1;
            SearchStatusText = GetModeUnavailableMessage(mode);
            return;
        }

        SearchStatusText = string.Empty;
        _searchResults.Clear();
        _searchDisplayItems.Clear();
        SearchResultsListBox!.SelectedIndex = -1;
        if (mode == SearchMode.FindInFile)
            UpdateFindHighlights();
        else
            _ = RunActiveSearchAsync();
        FocusSearchInput();
    }

    private bool CanShowSearchPanelForMode(SearchMode mode) => mode switch
    {
        SearchMode.FindInFile => CanShowFindInFile,
        SearchMode.FileByName => IsFolderOpen,
        SearchMode.ProjectSearch => IsFolderOpen,
        _ => false,
    };

    private static string GetModeUnavailableMessage(SearchMode mode) => mode switch
    {
        SearchMode.FileByName => "Open a folder to search files by name.",
        SearchMode.ProjectSearch => "Open a folder to search the project.",
        _ => string.Empty,
    };

    private void NotifySearchModeChanged()
    {
        RaiseMany(nameof(IsFindInFileSearchMode), nameof(IsFileByNameSearchMode), nameof(IsProjectSearchMode), nameof(IsSearchResultsVisible), nameof(SearchPlaceholderText), nameof(IsSearchPanelActive));
    }

    private void FocusSearchInput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var searchTextBox = this.FindControl<TextBox>("SearchTextBox");
            if (searchTextBox is null) return;
            searchTextBox.Focus();
            searchTextBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void SearchTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsSearchPanelVisible = false;
            FocusEditor();
            ResetHistoryIndex();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_searchMode == SearchMode.FindInFile)
            {
                if (!string.IsNullOrEmpty(FindText))
                    AddToHistory(FindText);
                FindInEditor(forward: true);
            }
            else
            {
                OpenSelectedSearchResult();
            }
            ResetHistoryIndex();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            CycleHistory(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (IsSearchResultsVisible && SearchDisplayItems.Count > 0)
            {
                SearchResultsListBox?.Focus();
                ResetHistoryIndex();
            }
            else
            {
                CycleHistory(1);
            }
            e.Handled = true;
        }
        else
        {
            ResetHistoryIndex();
        }
    }

    private void RestartSearchDebounce()
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = RunActiveSearchAsync();
    }

    private List<string> GetCurrentHistory() => _searchMode switch
    {
        SearchMode.FindInFile => _findInFileHistory,
        SearchMode.FileByName => _fileByNameHistory,
        SearchMode.ProjectSearch => _projectSearchHistory,
        _ => _findInFileHistory,
    };

    private void AddToHistory(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var history = GetCurrentHistory();
        history.Remove(query);
        history.Insert(0, query);
        if (history.Count > 10)
            history.RemoveRange(10, history.Count - 10);
    }

    private void ResetHistoryIndex()
    {
        _historyIndex = -1;
        _savedFindText = string.Empty;
    }

    private void CycleHistory(int direction)
    {
        var history = GetCurrentHistory();
        if (history.Count == 0) return;

        if (_historyIndex < 0)
        {
            _savedFindText = FindText ?? string.Empty;
            _historyIndex = direction < 0 ? 0 : history.Count - 1;
        }
        else
        {
            _historyIndex += direction;
        }

        if (_historyIndex < 0)
        {
            FindText = _savedFindText;
            ResetHistoryIndex();
        }
        else if (_historyIndex >= history.Count)
        {
            FindText = _savedFindText;
            ResetHistoryIndex();
        }
        else
        {
            FindText = history[_historyIndex];
        }
    }

    private void RebuildDisplayItems()
    {
        _searchDisplayItems.Clear();

        // In FindInFile mode or FileByName mode: no grouping, flat list.
        if (_searchMode != SearchMode.ProjectSearch)
        {
            foreach (var item in _searchResults)
                _searchDisplayItems.Add(new SearchDisplayItem { Result = item });
            UpdateSearchPanelMinWidth();
            return;
        }

        var previousExpansion = _fileGroups
            .ToDictionary(g => g.FilePath, g => g.IsExpanded, StringComparer.OrdinalIgnoreCase);

        _fileGroups.Clear();
        var grouped = _searchResults
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SearchFileGroup
            {
                FilePath = g.Key,
                FileName = Path.GetFileName(g.Key),
                RelativePath = g.First().RelativePath,
                MatchCount = g.Count(),
                IsExpanded = previousExpansion.TryGetValue(g.Key, out var wasExpanded) && wasExpanded,
            })
            .ToList();

        foreach (var group in grouped)
        {
            _fileGroups.Add(group);
            _searchDisplayItems.Add(new SearchDisplayItem { IsGroupHeader = true, Group = group });
            if (group.IsExpanded)
            {
                foreach (var item in _searchResults.Where(r =>
                    string.Equals(r.Path, group.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    _searchDisplayItems.Add(new SearchDisplayItem { Result = item });
                }
            }
        }

        UpdateSearchPanelMinWidth();
    }

    private void UpdateSearchPanelMinWidth()
    {
        if (_searchResults.Count == 0)
        {
            SearchPanelBorder.MinWidth = 0;
            return;
        }

        double maxWidth = 0;
        const double charWidth12 = 7.2;
        const double charWidth11 = 6.4;
        const double iconWidth = 22;
        const double chevronWidth = 12;
        const double paddingAndMargins = 56;

        foreach (var item in _searchResults)
        {
            double resultWidth = iconWidth + item.DisplayName.Length * charWidth12;
            if (resultWidth > maxWidth) maxWidth = resultWidth;
        }

        foreach (var group in _fileGroups)
        {
            double groupWidth = chevronWidth
                               + group.FileName.Length * charWidth12
                               + 8
                               + group.RelativePath.Length * charWidth11
                               + 8
                               + (group.MatchCount.ToString().Length + 9) * charWidth11; // "N matches"
            if (groupWidth > maxWidth) maxWidth = groupWidth;
        }

        SearchPanelBorder.MinWidth = Math.Min(maxWidth + paddingAndMargins, 560);
    }

    private void ToggleGroup(SearchFileGroup group)
    {
        group.IsExpanded = !group.IsExpanded;
        RebuildDisplayItems();
    }

    private async Task RunActiveSearchAsync()
    {
        if (!IsSearchPanelVisible)
            return;

        if (_searchMode == SearchMode.FindInFile)
        {
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            UpdateFindHighlights();
            return;
        }

        if (string.IsNullOrWhiteSpace(FindText))
        {
            _searchCancellation?.Cancel();
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            SearchStatusText = _searchMode == SearchMode.FileByName
                ? "Type a file name to find files."
                : "Type text to search the project.";
            return;
        }

        if (!CanShowSearchPanelForMode(_searchMode))
        {
            _searchCancellation?.Cancel();
            _searchResults.Clear();
            _searchDisplayItems.Clear();
            SearchStatusText = GetModeUnavailableMessage(_searchMode);
            return;
        }

        var mode = _searchMode;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCancellation = cts;
        var token = cts.Token;

        IsSearchBusy = true;
        SearchStatusText = "Searching...";

        var matchCase = IsSearchMatchCaseEnabled;
        var wholeWord = IsSearchWholeWordEnabled;
        var useRegex = IsSearchRegexEnabled;
        var includeFilter = IsProjectSearchMode ? SearchIncludeFilter : null;
        var excludeFilter = IsProjectSearchMode ? SearchExcludeFilter : null;
        var sw = Stopwatch.StartNew();

        List<SearchResultItem> results;
        bool truncated = false;
        try
        {
            if (mode == SearchMode.FileByName)
            {
                results = await Task.Run(() =>
                {
                    var cache = GetOrBuildSearchCache(_currentFolderPath!, includeFilter, excludeFilter);
                    return SearchFilesByName(FindText, _currentFolderPath!, matchCase, useRegex, cache.Files, token);
                }, token);
            }
            else
            {
                var (projectResults, wasTruncated) = await Task.Run(() =>
                {
                    var cache = GetOrBuildSearchCache(_currentFolderPath!, includeFilter, excludeFilter);
                    return SearchProjectForText(FindText, _currentFolderPath!, matchCase, wholeWord, useRegex, cache.Files, token);
                }, token);
                results = projectResults;
                truncated = wasTruncated;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one (or the panel closed
            IsSearchBusy = false;
            return;
        }

        if (token.IsCancellationRequested || !IsSearchPanelVisible || _searchMode != mode)
        {
            IsSearchBusy = false;
            return;
        }

        sw.Stop();
        _searchResults.Clear();
        foreach (var item in results)
            _searchResults.Add(item);
        RebuildDisplayItems();
        AddToHistory(FindText);
        SearchStatusText = BuildSearchStatusText(results.Count, truncated, sw.Elapsed);
        IsSearchBusy = false;
    }

    private string BuildSearchStatusText(int resultCount, bool truncated = false, TimeSpan elapsed = default)
    {
        var countText = resultCount.ToString("N0");
        var text = _searchMode switch
        {
            SearchMode.FileByName => resultCount == 0
                ? "No files found."
                : resultCount == 1 ? "1 file found." : $"{countText} files found.",
            SearchMode.ProjectSearch => resultCount == 0
                ? "No matches found."
                : resultCount == 1 ? "1 match found." : $"{countText} matches found.",
            _ => string.Empty,
        };
        if (truncated)
            text += " (showing first 2,000)";
        if (elapsed.TotalMilliseconds >= 1)
            text += $" ({elapsed.TotalMilliseconds:F0}ms)";
        return text;
    }

    private (List<string> Files, SearchIgnoreRules Rules) GetOrBuildSearchCache(string root, string? includeFilter = null, string? excludeFilter = null)
    {
        // Invalidate cache if filters changed.
        if (_searchFileCache is { } cached &&
            string.Equals(cached.Rules.IncludeFilterSnapshot, includeFilter ?? "", StringComparison.Ordinal) &&
            string.Equals(cached.Rules.ExcludeFilterSnapshot, excludeFilter ?? "", StringComparison.Ordinal))
        {
            return cached;
        }

        var rules = SearchIgnoreRules.Load(root, includeFilter, excludeFilter);
        rules.IncludeFilterSnapshot = includeFilter ?? "";
        rules.ExcludeFilterSnapshot = excludeFilter ?? "";
        var files = new List<string>();
        EnumerateProjectFiles(root, files, rules);
        _searchFileCache = (files, rules);
        return _searchFileCache.Value;
    }

    private static void EnumerateProjectFiles(string root, List<string> files, SearchIgnoreRules ignoreRules, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!visited.Add(normalized))
                return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                if (ignoreRules.ShouldSkipDirectory(dir)) continue;
                EnumerateProjectFiles(dir, files, ignoreRules, visited);
            }
            foreach (var file in Directory.GetFiles(root))
            {
                if (ignoreRules.ShouldSkipFile(file)) continue;
                files.Add(file);
            }
        }
        catch
        {
        }
    }

    private static List<SearchResultItem> SearchFilesByName(string query, string root, bool matchCase, bool useRegex, List<string> files, CancellationToken token)
    {
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options | RegexOptions.Compiled);
            }
            catch
            {
                return new List<SearchResultItem>();
            }
        }

        var scoredResults = new List<(SearchResultItem Item, int Score)>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);

            if (regex is not null)
            {
                var match = regex.Match(name);
                if (!match.Success) continue;

                var matchIndices = new List<int>();
                foreach (Group g in match.Groups)
                    foreach (Capture c in g.Captures)
                        for (var i = 0; i < c.Length; i++)
                            matchIndices.Add(c.Index + i);

                scoredResults.Add((new SearchResultItem
                {
                    Path = file,
                    DisplayName = name,
                    RelativePath = GetRelativePathOrName(root, file),
                    Icon = FileTreeItem.GetFileIcon(name),
                    MatchedIndices = matchIndices,
                    Score = match.Index == 0 ? 2000 : 1000,
                }, match.Index == 0 ? 2000 : 1000));
            }
            else
            {
                var (score, indices) = FuzzyMatch.Match(query, name, matchCase);
                if (score < 0) continue;

                scoredResults.Add((new SearchResultItem
                {
                    Path = file,
                    DisplayName = name,
                    RelativePath = GetRelativePathOrName(root, file),
                    Icon = FileTreeItem.GetFileIcon(name),
                    MatchedIndices = indices,
                    Score = score,
                }, score));
            }
        }

        scoredResults.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scoredResults.Select(r => r.Item).ToList();
    }

    private static (List<SearchResultItem> Results, bool Truncated) SearchProjectForText(string query, string root, bool matchCase, bool wholeWord, bool useRegex, List<string> files, CancellationToken token)
    {
        const int maxResults = 2000;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options | RegexOptions.Compiled);
            }
            catch
            {
                return (new List<SearchResultItem>(), false);
            }
        }

        var results = new List<SearchResultItem>();
        var truncated = false;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (IsImagePreviewFile(file) || IsBinaryContent(file)) continue;

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;

                    bool matched;
                    List<int>? matchIndices = null;
                    if (regex is not null)
                    {
                        var m = regex.Match(line);
                        matched = m.Success;
                        if (matched)
                        {
                            matchIndices = new List<int>();
                            foreach (Group g in m.Groups)
                                foreach (Capture c in g.Captures)
                                    for (var i = 0; i < c.Length; i++)
                                        matchIndices.Add(c.Index + i);
                        }
                    }
                    else
                    {
                        matched = line.Contains(query, comparison);
                        if (matched && wholeWord && !LineContainsWholeWord(line, query, comparison))
                            matched = false;
                    }

                    if (!matched) continue;

                    results.Add(new SearchResultItem
                    {
                        Path = file,
                        DisplayName = Path.GetFileName(file),
                        RelativePath = GetRelativePathOrName(root, file),
                        LineNumber = lineNumber,
                        PreviewText = $"{lineNumber}: {TrimSearchPreview(line)}",
                        Icon = FileTreeItem.GetFileIcon(file),
                        MatchedPreviewIndices = matchIndices is not null ? matchIndices.ToArray() : System.Array.Empty<int>(),
                    });
                    if (results.Count >= maxResults)
                    {
                        truncated = true;
                        return (results, truncated);
                    }
                }
            }
            catch
            {
            }
        }
        return (results, truncated);
    }

    private static bool LineContainsWholeWord(string line, string needle, StringComparison comparison)
    {
        var idx = 0;
        while (idx <= line.Length - needle.Length)
        {
            idx = line.IndexOf(needle, idx, comparison);
            if (idx < 0) return false;
            if (IsWholeWordMatch(line, idx, needle.Length))
                return true;
            idx += Math.Max(1, needle.Length);
        }
        return false;
    }

    private static string TrimSearchPreview(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 140 ? trimmed : trimmed[..140];
    }

    private static System.Text.Encoding DetectFileEncoding(string path)
    {
        try
        {
            Span<byte> bom = stackalloc byte[4];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = fs.Read(bom);

            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);   // UTF-8 BOM
            if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return System.Text.Encoding.Unicode;          // UTF-16 LE
            if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode; // UTF-16 BE
            if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                return System.Text.Encoding.UTF32;

            // No BOM - default to UTF-8 without BOM
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            return System.Text.Encoding.UTF8;
        }
    }

    private void FindNextButton_OnClick(object? sender, RoutedEventArgs e) =>
        FindInEditor(forward: true);

    private void FindPrevButton_OnClick(object? sender, RoutedEventArgs e) =>
        FindInEditor(forward: false);

    private void ReplaceCurrentMatch()
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var doc = EditorTextBox.Document.Text;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        var match = FindNextMatch(doc, FindText, Math.Max(0, caretOffset - 1), forward: true, comparison, IsSearchWholeWordEnabled, regex);
        if (match.Offset < 0)
            return;

        EditorTextBox.TextArea.Document.Replace(match.Offset, match.Length, ReplaceText ?? string.Empty);
        FindInEditor(forward: true);
    }

    private void ReplaceAllMatches()
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var doc = EditorTextBox.Document;
        var text = doc.Text;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var replacement = ReplaceText ?? string.Empty;
        var regex = BuildFindRegex();

        var matches = EnumerateFindMatches(text, FindText, comparison, IsSearchWholeWordEnabled, regex).ToList();

        if (matches.Count == 0)
            return;

        var sb = new System.Text.StringBuilder(text.Length + matches.Count * Math.Max(0, replacement.Length - FindText.Length));
        var pos = 0;
        foreach (var match in matches)
        {
            if (match.Offset > pos)
                sb.Append(text, pos, match.Offset - pos);
            sb.Append(replacement);
            pos = match.Offset + match.Length;
        }
        if (pos < text.Length)
            sb.Append(text, pos, text.Length - pos);

        doc.Replace(0, text.Length, sb.ToString());

        SearchStatusText = $"Replaced {matches.Count} match{(matches.Count == 1 ? string.Empty : "es")}.";
    }

    private void EditorFindAllOccurrencesMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var selectedText = sel.GetText();
        if (string.IsNullOrEmpty(selectedText)) return;

        FindText = selectedText;
        OpenSearchPanel(SearchMode.FindInFile);
    }

    private void FindInEditor(bool forward)
    {
        if (string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null) return;
        var doc = EditorTextBox.Document.Text;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex = BuildFindRegex();
        (int Offset, int Length) match;
        if (forward)
        {
            match = FindNextMatch(doc, FindText, caretOffset + 1, forward: true, comparison, IsSearchWholeWordEnabled, regex);
            if (match.Offset < 0)
                match = FindNextMatch(doc, FindText, 0, forward: true, comparison, IsSearchWholeWordEnabled, regex);
        }
        else
        {
            var searchTo = Math.Max(0, caretOffset - 1);
            match = FindNextMatch(doc, FindText, searchTo, forward: false, comparison, IsSearchWholeWordEnabled, regex);
            if (match.Offset < 0)
                match = FindNextMatch(doc, FindText, doc.Length - 1, forward: false, comparison, IsSearchWholeWordEnabled, regex);
        }

        if (match.Offset < 0) return;
        EditorTextBox.TextArea.Caret.Offset = match.Offset;
        EditorTextBox.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(EditorTextBox.TextArea, match.Offset, match.Offset + match.Length);
        EditorTextBox.ScrollToLine(EditorTextBox.Document.GetLineByOffset(match.Offset).LineNumber);

        // Track which match we're on for the "X of Y" status display.
        if (_findMatchOffsets.Count > 0)
        {
            _currentFindMatchIndex = _findMatchOffsets.BinarySearch(match.Offset);
            if (_currentFindMatchIndex < 0)
                _currentFindMatchIndex = ~_currentFindMatchIndex - 1;
            if (_currentFindMatchIndex < 0)
                _currentFindMatchIndex = _findMatchOffsets.Count - 1;
            UpdateFindStatusText();
        }
    }

    private static (int Offset, int Length) FindNextMatch(string text, string needle, int startIndex, bool forward, StringComparison comparison, bool wholeWord, Regex? regex = null)
    {
        if (regex is not null)
        {
            if (forward)
            {
                var m = regex.Match(text, startIndex);
                return m.Success ? (m.Index, m.Length) : (-1, 0);
            }
            else
            {
                (int Offset, int Length) last = (-1, 0);
                foreach (Match m in regex.Matches(text))
                {
                    if (m.Index > startIndex) break;
                    last = (m.Index, m.Length);
                }
                return last;
            }
        }

        if (string.IsNullOrEmpty(needle))
            return (-1, 0);

        if (forward)
        {
            var index = Math.Max(0, startIndex);
            while (index <= text.Length - needle.Length)
            {
                index = text.IndexOf(needle, index, comparison);
                if (index < 0) return (-1, 0);
                if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                    return (index, needle.Length);
                index += Math.Max(1, needle.Length);
            }

            return (-1, 0);
        }

        var searchTo = Math.Min(Math.Max(0, startIndex), text.Length - 1);
        while (searchTo >= 0)
        {
            var index = text.LastIndexOf(needle, searchTo, comparison);
            if (index < 0) return (-1, 0);
            if (!wholeWord || IsWholeWordMatch(text, index, needle.Length))
                return (index, needle.Length);
            searchTo = index - 1;
        }

        return (-1, 0);
    }

    private static bool IsWholeWordMatch(string text, int index, int length)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        var beforeOk = index == 0 || !IsWordChar(text[index - 1]);
        var afterIndex = index + length;
        var afterOk = afterIndex >= text.Length || !IsWordChar(text[afterIndex]);
        return beforeOk && afterOk;
    }
    private static IEnumerable<(int Offset, int Length)> EnumerateFindMatches(string text, string needle, StringComparison cmp, bool wholeWord, Regex? regex) { var idx = 0; while (idx <= text.Length) { var m = FindNextMatch(text, needle, idx, true, cmp, wholeWord, regex); if (m.Offset < 0) yield break; yield return m; idx = m.Offset + m.Length; if (m.Length == 0) yield break; } }

    private void UpdateFindHighlights()
    {
        if (EditorTextBox?.Document is null) return;

        // Snapshot UI state for background work; avoid O(N) on UI thread for large docs
        var snapshotText = EditorTextBox.Document.Text;
        var snapshotFind = FindText;
        var snapshotMode = IsFindInFileSearchMode;
        var snapshotCaret = EditorTextBox.TextArea.Caret.Offset;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wholeWord = IsSearchWholeWordEnabled;
        var regex = BuildFindRegex();

        if (!snapshotMode || string.IsNullOrEmpty(snapshotFind))
        {
            _findHighlightRenderer.Clear();
            _findMatchOffsets.Clear();
            _currentFindMatchIndex = -1;
            SearchStatusText = string.Empty;
            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return;
        }

        // Large document: offload regex/string scan to thread pool to keep typing smooth
        if (snapshotText.Length > 80000)
        {
            var findCopy = snapshotFind;
            var textCopy = snapshotText;
            var compCopy = comparison;
            var wholeCopy = wholeWord;
            var regexCopy = regex;
            var caretCopy = snapshotCaret;
            Task.Run(() =>
            {
                var matches = new List<(int Offset, int Length)>();
                foreach (var m in EnumerateFindMatches(textCopy, findCopy, compCopy, wholeCopy, regexCopy))
                    matches.Add(m);
                return matches;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled) return;
                // Discard stale results if find text changed while background ran
                if (FindText != findCopy) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (EditorTextBox?.Document is null) return;
                    _findHighlightRenderer.Clear();
                    _findMatchOffsets.Clear();
                    foreach (var m in t.Result) { _findMatchOffsets.Add(m.Offset); _findHighlightRenderer.AddMatch(m.Offset, m.Length); }
                    if (_findMatchOffsets.Count > 0)
                    {
                        _currentFindMatchIndex = _findMatchOffsets.BinarySearch(caretCopy);
                        if (_currentFindMatchIndex < 0) _currentFindMatchIndex = Math.Max(0, ~_currentFindMatchIndex - 1);
                    }
                    else _currentFindMatchIndex = -1;
                    UpdateFindStatusText();
                    EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                });
            }, TaskScheduler.Default);
            return;
        }

        _findHighlightRenderer.Clear();
        _findMatchOffsets.Clear();
        _currentFindMatchIndex = -1;
        foreach (var m in EnumerateFindMatches(snapshotText, snapshotFind, comparison, wholeWord, regex)) { _findMatchOffsets.Add(m.Offset); _findHighlightRenderer.AddMatch(m.Offset, m.Length); }

        if (_findMatchOffsets.Count > 0)
        {
            _currentFindMatchIndex = _findMatchOffsets.BinarySearch(snapshotCaret);
            if (_currentFindMatchIndex < 0)
                _currentFindMatchIndex = Math.Max(0, ~_currentFindMatchIndex - 1);
        }

        UpdateFindStatusText();
        EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private Regex? _cachedFindRegex;
    private string _cachedFindText = "";
    private bool _cachedFindMatchCase;
    private bool _cachedFindRegexEnabled;

    private Regex? BuildFindRegex()
    {
        if (!IsSearchRegexEnabled || string.IsNullOrEmpty(FindText)) return null;
        if (_cachedFindText == FindText && _cachedFindMatchCase == IsSearchMatchCaseEnabled && _cachedFindRegexEnabled == IsSearchRegexEnabled)
        {
            return _cachedFindRegex!;
        }
        _cachedFindText = FindText;
        _cachedFindMatchCase = IsSearchMatchCaseEnabled;
        _cachedFindRegexEnabled = IsSearchRegexEnabled;
        try
        {
            var options = IsSearchMatchCaseEnabled ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(FindText, options | RegexOptions.Compiled);
            _cachedFindRegex = regex;
            return regex;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateFindStatusText()
    {
        if (!IsFindInFileSearchMode) return;
        var total = _findMatchOffsets.Count;
        if (total == 0)
            SearchStatusText = string.IsNullOrEmpty(FindText) ? string.Empty : "No matches.";
        else
            SearchStatusText = $"{_currentFindMatchIndex + 1} of {total}";
    }

}
