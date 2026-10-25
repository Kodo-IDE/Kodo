// Licensed under the GNU GPL-v3.0
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
using Kodo.Editor;

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

    private bool IsSettingsCardPlatformSupported(Control card) => card.Name switch
    {
        "TerminalSettingsCard" => IsTerminalSupported,
        _ => true
    };

    private bool IsSettingsSectionPlatformSupported(Control header) => header.Name switch
    {
        "SectionHeaderTerminal" => IsTerminalSupported,
        _ => true
    };

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
            var visible = IsSettingsCardPlatformSupported(card) && groupVisible[SettingsCardGroupKey(card)];
            card.IsVisible = visible;
            anyVisible |= visible;
        }

        foreach (var header in headers)
        {
            if (!IsSettingsSectionPlatformSupported(header))
            {
                header.IsVisible = false;
                continue;
            }

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

        var relativePath = SearchEngine.GetRelativePathOrName(_currentFolderPath, item.FullPath);
        if (!string.IsNullOrEmpty(relativePath))
        {
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

        var document = EditorTextBox.Document;
        var revision = EditorSession.Revision;
        var revisionToken = EditorSession.RevisionCancellationToken;
        var query = FindText;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wholeWord = IsSearchWholeWordEnabled;
        var replacement = ReplaceText ?? string.Empty;
        var regex = BuildFindRegex();
        if (_findRegexInvalid) return;
        List<(int Offset, int Length)> matches;
        var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
        try
        {
            if (regex is null)
                matches = await EditorBackgroundWork.RunAsync(_ => SearchEngine.EnumerateFindMatches(snapshot.Source, query, comparison, wholeWord, null, revisionToken).ToList(), revisionToken);
            else
            {
                var text = await snapshot.GetTextAsync(revisionToken);
                matches = await EditorBackgroundWork.RunAsync(_ => SearchEngine.EnumerateFindMatches(text, query, comparison, wholeWord, regex, revisionToken).ToList(), revisionToken);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (RegexMatchTimeoutException ex)
        {
            KodoDiagnostics.LogDebug("Replace all stopped: the search pattern took too long to evaluate.", ex);
            SearchStatusText = "That pattern is too expensive to search with - try a simpler one.";
            return;
        }
        if (!ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision || !string.Equals(FindText, query, StringComparison.Ordinal)) return;
        if (matches.Count == 0)
        {
            SearchStatusText = "No matches to replace.";
            return;
        }

        var matchWord = matches.Count == 1 ? "match" : "matches";
        var confirmed = await ShowConfirmationDialogAsync(
            "Replace All?",
            $"This will replace {matches.Count} {matchWord} in the current file. This action can be undone with Ctrl+Z.",
            confirmLabel: "Replace All");
        if (!confirmed || !ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision ||
            !string.Equals(FindText, query, StringComparison.Ordinal)) return;

        var updated = await EditorBackgroundWork.RunAsync(_ => SearchEngine.ReplaceAll(snapshot.Source, matches, replacement, revisionToken), revisionToken);

        if (!ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision) return;
        try
        {
            document.Replace(0, snapshot.Source.TextLength, updated);
            SearchStatusText = $"Replaced {matches.Count} match{(matches.Count == 1 ? string.Empty : "es")}.";
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("Search.ReplaceAll: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (ReferenceEquals(EditorTextBox?.Document, document) && EditorSession.Revision == revision)
                        document.Replace(0, snapshot.Source.TextLength, updated);
                }
                catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
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
            {
                var captured = index;
                var capturedItem = displayItem;
                var listBoxRef = SearchResultsListBox;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (listBoxRef is null) return;
                    if (captured < 0 || captured >= SearchDisplayItems.Count) return;
                    if (!ReferenceEquals(listBoxRef.SelectedItem, capturedItem) && SearchDisplayItems.IndexOf(capturedItem) != captured) return;
                    try { listBoxRef.ScrollIntoView(captured); } catch { }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
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
        var next = new System.Collections.ObjectModel.ObservableCollection<SearchDisplayItem>();

        if (_searchMode != SearchMode.ProjectSearch)
        {
            foreach (var item in _searchResults)
                next.Add(new SearchDisplayItem { Result = item });
            ReplaceSearchDisplayItems(next);
            UpdateSearchPanelMinWidth();
            return;
        }

        var previousExpansion = _fileGroups
            .ToDictionary(g => g.FilePath, g => g.IsExpanded, StringComparer.OrdinalIgnoreCase);

        _fileGroups.Clear();

        foreach (var group in _searchResults.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            var searchGroup = new SearchFileGroup
            {
                FilePath = group.Key,
                FileName = Path.GetFileName(group.Key),
                RelativePath = items[0].RelativePath,
                MatchCount = items.Count,
                IsExpanded = previousExpansion.TryGetValue(group.Key, out var wasExpanded) && wasExpanded,
            };

            _fileGroups.Add(searchGroup);
            next.Add(new SearchDisplayItem { IsGroupHeader = true, Group = searchGroup });
            if (!searchGroup.IsExpanded) continue;
            foreach (var item in items)
                next.Add(new SearchDisplayItem { Result = item });
        }

        ReplaceSearchDisplayItems(next);
        UpdateSearchPanelMinWidth();
    }

    private void ReplaceSearchDisplayItems(System.Collections.ObjectModel.ObservableCollection<SearchDisplayItem> next)
    {
        _searchDisplayItems = next;
        OnPropertyChanged(nameof(SearchDisplayItems));
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
                               + (group.MatchCount.ToString().Length + 9) * charWidth11;
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
        using var loading = BeginAppLoading(_searchMode == SearchMode.FileByName ? "Searching files" : "Searching project");

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
                    return SearchEngine.SearchFilesByName(FindText, _currentFolderPath!, matchCase, useRegex, cache.Files, token,
                        progress => loading.Report(progress));
                }, token);
            }
            else
            {
                var (projectResults, wasTruncated) = await Task.Run(() =>
                {
                    var cache = GetOrBuildSearchCache(_currentFolderPath!, includeFilter, excludeFilter);
                    return SearchProjectForText(FindText, _currentFolderPath!, matchCase, wholeWord, useRegex, cache.Files, token,
                        progress => loading.Report(progress));
                }, token);
                results = projectResults;
                truncated = wasTruncated;
            }
        }
        catch (OperationCanceledException)
        {
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
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (_searchFileCache is { } cached &&
            FileSystemPaths.Equals(cached.Rules.RootSnapshot, normalizedRoot) &&
            string.Equals(cached.Rules.IncludeFilterSnapshot, includeFilter ?? "", StringComparison.Ordinal) &&
            string.Equals(cached.Rules.ExcludeFilterSnapshot, excludeFilter ?? "", StringComparison.Ordinal))
        {
            return cached;
        }

        var rules = SearchIgnoreRules.Load(root, includeFilter, excludeFilter);
        rules.RootSnapshot = normalizedRoot;
        rules.IncludeFilterSnapshot = includeFilter ?? "";
        rules.ExcludeFilterSnapshot = excludeFilter ?? "";
        var files = new List<string>();
        SearchEngine.EnumerateProjectFiles(root, files, rules);
        _searchFileCache = (files, rules);
        return _searchFileCache.Value;
    }

    private static (List<SearchResultItem> Results, bool Truncated) SearchProjectForText(string query, string root, bool matchCase, bool wholeWord, bool useRegex, List<string> files, CancellationToken token, Action<double>? reportProgress = null)
    {
        const int maxResults = 2000;
        const long maxFileBytes = 1_500_000;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
            }
            catch
            {
                return (new List<SearchResultItem>(), false);
            }
        }

        var results = new List<SearchResultItem>();
        var truncated = false;
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            token.ThrowIfCancellationRequested();
            if (fileIndex % 16 == 0)
                reportProgress?.Invoke((double)fileIndex / Math.Max(1, files.Count));
            var file = files[fileIndex];
            if (IsImagePreviewFile(file) || IsBinaryContent(file)) continue;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > maxFileBytes) continue;
                if (fi.Length == 0) continue;
            }
            catch { continue; }

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (line.Length > 5000) continue;

                    bool matched;
                    List<int>? matchIndices = null;
                    if (regex is not null)
                    {
                        Match m;
                        try { m = regex.Match(line); }
                        catch (RegexMatchTimeoutException) { continue; }
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
                        if (matched && wholeWord && !SearchEngine.LineContainsWholeWord(line, query, comparison))
                            matched = false;
                    }

                    if (!matched) continue;

                    results.Add(new SearchResultItem
                    {
                        Path = file,
                        DisplayName = Path.GetFileName(file),
                        RelativePath = SearchEngine.GetRelativePathOrName(root, file),
                        LineNumber = lineNumber,
                        PreviewText = $"{lineNumber}: {SearchEngine.TrimSearchPreview(line)}",
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
        reportProgress?.Invoke(1);
        return (results, truncated);
    }

    private void FindNextButton_OnClick(object? sender, RoutedEventArgs e) =>
        FindInEditor(forward: true);

    private void FindPrevButton_OnClick(object? sender, RoutedEventArgs e) =>
        FindInEditor(forward: false);

    private async void ReplaceCurrentMatch()
    {
        if (!IsFindInFileSearchMode || string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null)
            return;

        var document = EditorTextBox.Document;
        var revision = EditorSession.Revision;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var query = FindText;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wholeWord = IsSearchWholeWordEnabled;
        var replacement = ReplaceText ?? string.Empty;
        var regex = BuildFindRegex();
        if (_findRegexInvalid)
        {
            SearchStatusText = "Invalid regular expression.";
            return;
        }
        (int Offset, int Length) match;
        try
        {
            var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
            if (regex is null)
                match = await EditorBackgroundWork.RunAsync(_ => SearchEngine.FindNextMatch(snapshot.Source, query, Math.Max(0, caretOffset - 1), true, comparison, wholeWord, null), CancellationToken.None).ConfigureAwait(false);
            else
            {
                var text = await snapshot.GetTextAsync(EditorSession.RevisionCancellationToken).ConfigureAwait(false);
                match = await EditorBackgroundWork.RunAsync(_ => SearchEngine.FindNextMatch(text, query, Math.Max(0, caretOffset - 1), true, comparison, wholeWord, regex), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            KodoDiagnostics.LogDebug("Replace current match stopped: the search pattern took too long to evaluate.", ex);
            await Dispatcher.UIThread.InvokeAsync(() => SearchStatusText = "That pattern is too expensive to search with - try a simpler one.");
            return;
        }
        if (match.Offset < 0)
            return;

        var applied = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision ||
                EditorTextBox.TextArea.Caret.Offset != caretOffset || !string.Equals(FindText, query, StringComparison.Ordinal)) return;
            try
            {
                document.Replace(match.Offset, match.Length, replacement);
                applied = true;
            }
            catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
            {
                KodoDiagnostics.LogDebug("Search: Visual line race suppressed", ex);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (ReferenceEquals(EditorTextBox?.Document, document)) document.Replace(match.Offset, match.Length, replacement);
                    }
                    catch { }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        });
        if (applied) FindInEditor(forward: true);
    }

    private void EditorFindAllOccurrencesMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (EditorTextBox?.TextArea?.Selection is not { IsEmpty: false } sel) return;
        var selectedText = sel.GetText();
        if (string.IsNullOrEmpty(selectedText)) return;

        FindText = selectedText;
        OpenSearchPanel(SearchMode.FindInFile);
    }

    private async void FindInEditor(bool forward)
    {
        if (string.IsNullOrEmpty(FindText) || EditorTextBox?.Document is null) return;
        CancelSearchOperation(_findNavigationCancellation);
        var document = EditorTextBox.Document;
        var revision = EditorSession.Revision;
        var caretOffset = EditorTextBox.TextArea.Caret.Offset;
        var query = FindText;
        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wholeWord = IsSearchWholeWordEnabled;
        var regex = BuildFindRegex();
        if (_findRegexInvalid)
        {
            SearchStatusText = "Invalid regular expression.";
            return;
        }
        var cancellation = _findNavigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(EditorSession.RevisionCancellationToken);
        var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
        (int Offset, int Length) match = (-1, 0);
        try
        {
            var regexText = regex is null ? null : await snapshot.GetTextAsync(cancellation.Token).ConfigureAwait(false);
            match = await EditorBackgroundWork.RunAsync(_ =>
            {
                var searchSource = regexText;
                (int Offset, int Length) Find(int start, bool isForward) => searchSource is null
                    ? SearchEngine.FindNextMatch(snapshot.Source, query, start, isForward, comparison, wholeWord, null, cancellation.Token)
                    : SearchEngine.FindNextMatch(searchSource, query, start, isForward, comparison, wholeWord, regex, cancellation.Token);
                if (forward)
                {
                    var found = Find(caretOffset + 1, true);
                    return found.Offset < 0
                        ? Find(0, true)
                        : found;
                }
                var previous = Find(Math.Max(0, caretOffset - 1), false);
                return previous.Offset < 0
                    ? Find(snapshot.Source.TextLength - 1, false)
                    : previous;
            }, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_findNavigationCancellation, cancellation)) _findNavigationCancellation = null;
            cancellation.Dispose();
            return;
        }
        catch (RegexMatchTimeoutException ex)
        {
            KodoDiagnostics.LogDebug("Find in editor stopped: the search pattern took too long to evaluate.", ex);
            await Dispatcher.UIThread.InvokeAsync(() => SearchStatusText = "That pattern is too expensive to search with - try a simpler one.");
            if (ReferenceEquals(_findNavigationCancellation, cancellation)) _findNavigationCancellation = null;
            cancellation.Dispose();
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Find in editor failed.", ex);
            if (ReferenceEquals(_findNavigationCancellation, cancellation)) _findNavigationCancellation = null;
            cancellation.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_findNavigationCancellation, cancellation) ||
                !ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision ||
                EditorTextBox.TextArea.Caret.Offset != caretOffset || !string.Equals(FindText, query, StringComparison.Ordinal)) return;
            if (match.Offset < 0) return;
            EditorTextBox.TextArea.Caret.Offset = match.Offset;
            EditorTextBox.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(EditorTextBox.TextArea, match.Offset, match.Offset + match.Length);
            EditorTextBox.ScrollToLine(EditorTextBox.Document.GetLineByOffset(match.Offset).LineNumber);
            if (_findMatchOffsets.Count > 0)
            {
                _currentFindMatchIndex = _findMatchOffsets.BinarySearch(match.Offset);
                if (_currentFindMatchIndex < 0)
                    _currentFindMatchIndex = ~_currentFindMatchIndex - 1;
                if (_currentFindMatchIndex < 0)
                    _currentFindMatchIndex = _findMatchOffsets.Count - 1;
                UpdateFindStatusText();
            }
        });
        if (ReferenceEquals(_findNavigationCancellation, cancellation)) _findNavigationCancellation = null;
        cancellation.Dispose();
    }

    private CancellationTokenSource? _findHighlightCancellation;
    private CancellationTokenSource? _findNavigationCancellation;

    private static void CancelSearchOperation(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void UpdateFindHighlights()
    {
        CancelSearchOperation(_findHighlightCancellation);
        _findHighlightCancellation = null;
        if (EditorTextBox?.Document is not { } document) return;

        var query = FindText;
        var mode = IsFindInFileSearchMode;
        var regex = BuildFindRegex();
        if (_findRegexInvalid)
        {
            ClearFindHighlightState("Invalid regular expression.");
            return;
        }
        if (!mode || string.IsNullOrEmpty(query))
        {
            ClearFindHighlightState(string.Empty);
            return;
        }

        var comparison = IsSearchMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wholeWord = IsSearchWholeWordEnabled;
        var revision = EditorSession.Revision;
        var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
        var cancellation = _findHighlightCancellation = CancellationTokenSource.CreateLinkedTokenSource(EditorSession.RevisionCancellationToken);
        _ = ApplyFindHighlightsAsync(document, snapshot, revision, query, comparison, wholeWord, regex, cancellation);
    }

    private void ClearFindHighlightState(string status)
    {
        _findHighlightRenderer.Clear();
        _findMatchOffsets.Clear();
        _currentFindMatchIndex = -1;
        SearchStatusText = status;
        EditorTextBox?.TextArea?.TextView?.InvalidateLayer(KnownLayer.Background);
    }

    private async Task ApplyFindHighlightsAsync(
        AvaloniaEdit.Document.TextDocument document,
        EditorTextSnapshot snapshot,
        long revision,
        string query,
        StringComparison comparison,
        bool wholeWord,
        Regex? regex,
        CancellationTokenSource cancellation)
    {
        (List<(int Offset, int Length)> Matches, List<int> Offsets) result;
        try
        {
            if (regex is null)
                result = await EditorBackgroundWork.RunAsync(_ => BuildFindHighlightResults(
                    SearchEngine.EnumerateFindMatches(snapshot.Source, query, comparison, wholeWord, null, cancellation.Token), cancellation.Token), cancellation.Token).ConfigureAwait(false);
            else
            {
                var text = await snapshot.GetTextAsync(cancellation.Token).ConfigureAwait(false);
                result = await EditorBackgroundWork.RunAsync(_ => BuildFindHighlightResults(
                    SearchEngine.EnumerateFindMatches(text, query, comparison, wholeWord, regex, cancellation.Token), cancellation.Token), cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_findHighlightCancellation, cancellation)) _findHighlightCancellation = null;
            cancellation.Dispose();
            return;
        }
        catch (RegexMatchTimeoutException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_findHighlightCancellation, cancellation) && FindText == query)
                    ClearFindHighlightState("That pattern is too expensive to search with - try a simpler one.");
            });
            if (ReferenceEquals(_findHighlightCancellation, cancellation)) _findHighlightCancellation = null;
            cancellation.Dispose();
            return;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Find highlight calculation failed.", ex);
            if (ReferenceEquals(_findHighlightCancellation, cancellation)) _findHighlightCancellation = null;
            cancellation.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_findHighlightCancellation, cancellation) ||
                !ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision ||
                !IsFindInFileSearchMode || !string.Equals(FindText, query, StringComparison.Ordinal)) return;

            _findHighlightRenderer.Clear();
            _findMatchOffsets = result.Offsets;
            _findHighlightRenderer.SetMatches(result.Matches);
            _currentFindMatchIndex = _findMatchOffsets.Count == 0
                ? -1
                : _findMatchOffsets.BinarySearch(EditorTextBox.TextArea.Caret.Offset);
            if (_currentFindMatchIndex < 0 && _findMatchOffsets.Count > 0)
                _currentFindMatchIndex = Math.Max(0, ~_currentFindMatchIndex - 1);
            UpdateFindStatusText();
            EditorTextBox.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            _findHighlightCancellation = null;
        });
        if (ReferenceEquals(_findHighlightCancellation, cancellation))
            _findHighlightCancellation = null;
        cancellation.Dispose();
    }

    private static (List<(int Offset, int Length)> Matches, List<int> Offsets) BuildFindHighlightResults(
        IEnumerable<(int Offset, int Length)> sourceMatches,
        CancellationToken cancellationToken)
    {
        var matches = sourceMatches as List<(int Offset, int Length)> ?? sourceMatches.ToList();
        var offsets = new List<int>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            if ((index & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            offsets.Add(matches[index].Offset);
        }
        return (matches, offsets);
    }

    private Regex? _cachedFindRegex;
    private string _cachedFindText = "";
    private bool _cachedFindMatchCase;
    private bool _cachedFindRegexEnabled;
    private bool _cachedFindWholeWord;
    private bool _findRegexInvalid;

    private Regex? BuildFindRegex()
    {
        if (!IsSearchRegexEnabled || string.IsNullOrEmpty(FindText))
        {
            _findRegexInvalid = false;
            return null;
        }
        if (_cachedFindText == FindText && _cachedFindMatchCase == IsSearchMatchCaseEnabled && _cachedFindRegexEnabled == IsSearchRegexEnabled && _cachedFindWholeWord == IsSearchWholeWordEnabled)
        {
            _findRegexInvalid = _cachedFindRegex is null;
            return _cachedFindRegex;
        }
        _cachedFindText = FindText;
        _cachedFindMatchCase = IsSearchMatchCaseEnabled;
        _cachedFindRegexEnabled = IsSearchRegexEnabled;
        _cachedFindWholeWord = IsSearchWholeWordEnabled;
        try
        {
            var options = IsSearchMatchCaseEnabled ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(FindText, options | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
            _cachedFindRegex = regex;
            _findRegexInvalid = false;
            return regex;
        }
        catch
        {
            _cachedFindRegex = null;
            _findRegexInvalid = true;
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
