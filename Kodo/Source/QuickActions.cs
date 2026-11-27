// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kodo.Insight;
using Kodo.Models;
using Kodo.QuickActions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Kodo;

public partial class MainWindow
{
    private bool _isQuickActionsEnabled = true;
    private InsightUserIntelligence? _userInsight;
    private InsightUserIntelligence UserInsight =>
        _userInsight ??= InsightUserIntelligence.Load(KodoPaths.SettingsWritePath("quick-actions-learning.json"));
    private readonly DispatcherTimer _insightSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    public ObservableCollection<QuickActionItem> QuickActions { get; } = new();
    private readonly Dictionary<string, DiscoveredQuickAction> _discoveredQuickActions = new(StringComparer.Ordinal);
    private List<PaletteCommand>? _quickActionCommandSource;
    private readonly QuickActionObservationPool _observedQuickActions = new();
    private readonly QuickActionObservationLog _quickActionObservations = new();
    public bool IsQuickActionsEnabled
    {
        get => _isQuickActionsEnabled;
        set
        {
            if (_isQuickActionsEnabled == value) return;
            _isQuickActionsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQuickActions));
            SaveSettings();
            RefreshQuickActions();
        }
    }

    public bool HasQuickActions => IsQuickActionsEnabled && QuickActions.Count > 0;
    public bool IsQuickActionsLearningEnabled
    {
        get => UserInsight.IsQuickActionsLearningEnabled;
        set
        {
            if (UserInsight.IsQuickActionsLearningEnabled == value) return;
            UserInsight.IsQuickActionsLearningEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            RefreshQuickActions();
            RefreshInsightDashboard();
            ShowStatusBarMessage(value ? "Quick Actions personalization enabled." : "Quick Actions personalization disabled.");
        }
    }

    public bool IsPaletteLearningEnabled
    {
        get => UserInsight.IsPaletteLearningEnabled;
        set
        {
            if (UserInsight.IsPaletteLearningEnabled == value) return;
            UserInsight.IsPaletteLearningEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            RefreshOpenCommandPaletteRanking();
            RefreshInsightDashboard();
            ShowStatusBarMessage(value ? "Command Palette personalization enabled." : "Command Palette personalization disabled.");
        }
    }

    public bool HasQuickActionLearningData => _userInsight?.HasQuickActionLearningData == true;

    public bool HasPaletteLearningData => _userInsight?.HasPaletteLearningData == true;

    private void InitializeInsightLearning(AppSettings settings)
    {
        UserInsight.IsQuickActionsLearningEnabled = settings.QuickActionsLearningEnabled;
        UserInsight.IsPaletteLearningEnabled = settings.PaletteLearningEnabled;
        UserInsight.IsLearningEnabled = settings.InsightLearningEnabled;
        if (UserInsight.LoadFailed)
            KodoDiagnostics.LogDebug("Insight learning data could not be read; using defaults.");
        KodoDiagnostics.LogDebug($"Insight learning loaded: file={UserInsight.FilePath} quickActions={UserInsight.HasQuickActionLearningData} palette={UserInsight.HasPaletteLearningData} queries={UserInsight.HasQueryAssociationData} analysisDays={UserInsight.AnalysisCompletedDays}");
        _insightSaveTimer.Tick += InsightSaveTimer_OnTick;
        RefreshInsightBackupState();
    }

    private void InsightSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _insightSaveTimer.Stop();
        _ = SaveInsightAsync();
    }

    private async Task SaveInsightAsync()
    {
        try { await Task.Run(UserInsight.Save); }
        catch (Exception ex) { KodoDiagnostics.LogWarning("Insight learning data could not be saved.", ex); }
    }

    private void ScheduleInsightSave()
    {
        _insightSaveTimer.Stop();
        _insightSaveTimer.Start();
    }

    private void RefreshOpenCommandPaletteRanking()
    {
        if (_commandPaletteOverlay is not { IsVisible: true }) return;
        _commandPaletteCommands = BuildCommandPaletteCommands();
        RefreshCommandPaletteResults();
    }

    private double GetPaletteCommandAdjustment(string commandId) =>
        UserInsight.GetPaletteCommandAdjustment(commandId, DateTime.UtcNow);

    private void RecordPaletteCommandSelection(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        UserInsight.RecordPaletteCommandSelection(commandId, DateTime.UtcNow);
        OnPropertyChanged(nameof(HasPaletteLearningData));
        ScheduleInsightSave();
    }

    private void RecordPaletteQuerySelection(string? query, string commandId)
    {
        if (!UserInsight.RecordPaletteQuerySelection(query, commandId, DateTime.UtcNow)) return;
        OnPropertyChanged(nameof(HasPaletteLearningData));
        ScheduleInsightSave();
    }

    private async void ResetQuickActionLearningButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await ShowConfirmationDialogAsync("Reset Quick Actions learning", "Quick Actions will return to their default order. Command Palette learning is kept.", "Reset", "Cancel", isDestructive: true)) return;
        UserInsight.ResetQuickActionLearning();
        RestartInsightAnalysisAfterClear();
        _insightSaveTimer.Stop();
        OnPropertyChanged(nameof(HasQuickActionLearningData));
        RefreshQuickActions();
        RefreshInsightDashboard();
        ShowStatusBarMessage("Quick Actions learning reset.");
        await SaveInsightAsync();
    }

    private async void ResetPaletteLearningButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await ShowConfirmationDialogAsync("Reset Command Palette learning", "Command Palette results will return to their default order. Quick Actions learning is kept.", "Reset", "Cancel", isDestructive: true)) return;
        UserInsight.ResetPaletteLearning();
        RestartInsightAnalysisAfterClear();
        _insightSaveTimer.Stop();
        OnPropertyChanged(nameof(HasPaletteLearningData));
        RefreshOpenCommandPaletteRanking();
        RefreshInsightDashboard();
        ShowStatusBarMessage("Command Palette learning reset.");
        await SaveInsightAsync();
    }

    private void RecordQuickActionSelection(string actionId, QuickActionItem? selected = null)
    {
        selected ??= QuickActions.FirstOrDefault(action => action.Id == actionId);
        if (selected is null || !IsQuickActionsLearningEnabled) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsQuickActionsLearningEnabled) return;
            UserInsight.RecordQuickActionSelection(selected, DateTime.UtcNow);
            OnPropertyChanged(nameof(HasQuickActionLearningData));
            ScheduleInsightSave();
            RefreshQuickActions();
        });
    }

    private void MainWindow_OnQuickActionsFocusChanged(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused &&
            this.FindControl<StackPanel>("ActivityBarPanel") is { } activityBar &&
            focused.GetSelfAndVisualAncestors().Any(visual => ReferenceEquals(visual, activityBar)))
            return;
        RefreshQuickActions();
    }

    private bool IsQuickActionsEditorFocused()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused || EditorTextBox is null)
            return false;
        return focused.GetSelfAndVisualAncestors().Any(visual =>
            ReferenceEquals(visual, EditorTextBox) || ReferenceEquals(visual, EditorTextBox.TextArea));
    }

    private async void OpenQuickActions()
    {
        if (!IsQuickActionsEnabled) return;
        RefreshQuickActions();
        if (QuickActions.Count > 0) await ExecuteQuickAction(QuickActions[0].Id);
    }

    private void RefreshQuickActions()
    {
        if (!IsQuickActionsEnabled)
        {
            _observedQuickActions.Clear();
            if (QuickActions.Count == 0) return;
            QuickActions.Clear();
            OnPropertyChanged(nameof(HasQuickActions));
            return;
        }

        var actions = new List<QuickActionItem>();
        var hasDocument = IsQuickActionsEditorFocused() && IsTextEditorVisible && EditorTextBox?.Document is not null;
        var word = hasDocument ? GetLanguageWordAtCaret() : null;
        var rules = CurrentLanguageExtension?.LangRules;
        var extension = hasDocument ? Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant() : string.Empty;
        string Context(string category) => category == "project" ? "project" : $"{category}|{extension}";
        var diagnostic = hasDocument ? GetDiagnosticAtCaret() is not null : false;
        var hasProblems = hasDocument && (_errorHighlightRenderer.Spans.Count > 0 || _deadCodeHighlightRenderer.Spans.Count > 0);
        var hasCodeActions = hasDocument && (rules?.HasCodeActionProvider == true || HasQuickActionLspCapability("textDocument/codeAction"));
        var selection = hasDocument && EditorTextBox?.TextArea?.Selection is { IsEmpty: false };
        var document = hasDocument ? EditorTextBox!.Document : null;

        void AddQuickAction(List<QuickActionItem> target, string id, string title, string icon, string contextKey, double baseRelevance)
        {
            var category = contextKey == "project" ? "project" : contextKey.Split('|', 2)[0];
            var fileType = string.IsNullOrWhiteSpace(extension) ? "unknown" : extension;
            target.Add(new QuickActionItem
            {
                Id = id,
                Title = title,
                Icon = icon,
                ContextKey = contextKey,
                BaseRelevance = baseRelevance,
                LearningFeatures =
                [
                    $"category:{category}",
                    $"file:{fileType}",
                    $"category-file:{category}|{fileType}",
                    $"selection:{selection}",
                    $"diagnostic:{diagnostic}",
                    $"symbol:{!string.IsNullOrWhiteSpace(word)}",
                    $"project:{IsFolderOpen}",
                ],
            });
        }

        if (diagnostic && hasCodeActions)
            AddQuickAction(actions, "QuickFix", "Quick Fix", KodoIcons.QuickFix, Context("diagnostic"), 120);
        if (hasProblems)
            AddQuickAction(actions, "NextProblem", "Go to Next Problem", KodoIcons.NextProblem, Context("diagnostic"), 115);
        if (hasProblems && (_errorHighlightRenderer.Spans.Count + _deadCodeHighlightRenderer.Spans.Count) > 1)
            AddQuickAction(actions, "PreviousProblem", "Go to Previous Problem", KodoIcons.PreviousProblem, Context("diagnostic"), 108);

        if (selection)
            AddQuickAction(actions, "ChangeOccurrences", "Change All Occurrences", KodoIcons.ChangeOccurrences, Context("selection"), 105);

        if (!string.IsNullOrWhiteSpace(word))
        {
            if (HasQuickActionLspCapability("textDocument/rename"))
                AddQuickAction(actions, "Rename", "Rename Symbol", KodoIcons.RenameSymbol, Context("symbol"), 100);
            if (rules?.HasDefinitionProvider == true || HasQuickActionLspCapability("textDocument/definition"))
                AddQuickAction(actions, "Definition", "Go to Definition", KodoIcons.GoToDefinition, Context("symbol"), 99);
            if (rules?.HasReferenceProvider == true || HasQuickActionLspCapability("textDocument/references"))
                AddQuickAction(actions, "References", "Find References", KodoIcons.FindReferences, Context("symbol"), 98);
            if (HasQuickActionLspCapability("textDocument/typeDefinition"))
                AddQuickAction(actions, "TypeDefinition", "Go to Type Definition", KodoIcons.TypeDefinition, Context("symbol"), 97);
            if (HasQuickActionLspCapability("textDocument/implementation"))
                AddQuickAction(actions, "Implementation", "Go to Implementation", KodoIcons.Implementation, Context("symbol"), 96);
        }

        if (hasDocument && HasQuickActionLspCapability("textDocument/documentSymbol"))
            AddQuickAction(actions, "DocumentSymbols", "Show Document Symbols", KodoIcons.DocumentSymbols, Context("editor"), 79);
        if (hasDocument && LspSignatureHelpEnabled && HasQuickActionLspCapability("textDocument/signatureHelp"))
            AddQuickAction(actions, "SignatureHelp", "Show Signature Help", KodoIcons.SignatureHelp, Context("symbol"), 95);

        if (document is not null && EditorTextBox?.TextArea is { } lineArea)
        {
            var (firstLine, lastLine) = EditorGetLineOperationRange(document, lineArea);
            if (firstLine > 1)
                AddQuickAction(actions, "MoveLineUp", "Move Line Up", KodoIcons.MoveLineUp, Context("editor"), 84);
            if (lastLine > 0 && lastLine < document.LineCount)
                AddQuickAction(actions, "MoveLineDown", "Move Line Down", KodoIcons.MoveLineDown, Context("editor"), 83);
            AddQuickAction(actions, "DuplicateLineUp", "Duplicate Line Up", KodoIcons.DuplicateLineUp, Context("editor"), 82);
            AddQuickAction(actions, "DuplicateLineDown", "Duplicate Line Down", KodoIcons.DuplicateLineDown, Context("editor"), 81);
            if (document.TextLength > 0)
                AddQuickAction(actions, "DeleteLine", "Delete Line", KodoIcons.DeleteLine, Context("editor"), 80);
            AddQuickAction(actions, "InsertLineAbove", "Insert Line Above", KodoIcons.InsertLineAbove, Context("editor"), 78);
            AddQuickAction(actions, "InsertLineBelow", "Insert Line Below", KodoIcons.InsertLineBelow, Context("editor"), 77);
        }

        if (hasDocument &&
            (rules?.HasFormatter == true || HasQuickActionLspCapability("textDocument/formatting")))
            AddQuickAction(actions, "Format", "Format Document", KodoIcons.FormatDocument, Context("format"), 85);
        if (hasDocument)
            AddQuickAction(actions, "FindInFile", "Find in File", KodoIcons.FindInFile, Context("editor"), 75);
        if (hasDocument && IsSmartSyntaxEnabled())
            AddQuickAction(actions, "ToggleComment", "Toggle Line Comment", KodoIcons.ToggleComment, Context("editor"), 74);
        if (hasDocument)
            AddQuickAction(actions, "SelectLine", "Select Current Line", KodoIcons.SelectLine, Context("editor"), 71);
        if (hasDocument && ActiveRelativePath() is not null)
            AddQuickAction(actions, "ToggleBookmark", "Toggle Bookmark", KodoIcons.ToggleBookmark, Context("editor"), 70);

        if (IsFolderOpen && !string.IsNullOrWhiteSpace(_currentFolderPath))
        {
            if (IsTerminalSupported)
                AddQuickAction(actions, "ProjectTerminal", "Open Project Terminal", KodoIcons.ProjectTerminal, Context("project"), 65);
            AddQuickAction(actions, "ProjectSearch", "Search Across Project", KodoIcons.ProjectSearch, Context("project"), 64);
            AddQuickAction(actions, "CopyProjectPath", "Copy Project Path", KodoIcons.CopyProjectPath, Context("project"), 63);
        }

        IReadOnlyList<DiscoveredQuickAction> discoveredActions = [];
        if (hasDocument || IsFolderOpen)
            discoveredActions = QuickActionDiscovery.Discover(
                BuildQuickActionCommandRegistrations(),
                new QuickActionDiscoveryContext(extension, hasDocument, IsFolderOpen, IsTerminalSupported),
                QuickActionIdentity.CoveredCommandIds,
                ex => KodoDiagnostics.LogWarning("A Quick Action command availability check failed.", ex));

        _discoveredQuickActions.Clear();
        foreach (var discovered in discoveredActions)
        {
            _discoveredQuickActions[discovered.Item.Id] = discovered;
            actions.Add(discovered.Item);
        }

        _observedQuickActions.Update($"{_currentFilePath}|{IsFolderOpen}|{IsTerminalSupported}", actions);

        var rankedActions = UserInsight.RankQuickActions(actions, DateTime.UtcNow, maximum: 3);

        if (QuickActions.Count == rankedActions.Count)
        {
            var unchanged = true;
            for (var i = 0; i < rankedActions.Count; i++)
            {
                if (QuickActions[i].Id == rankedActions[i].Id && QuickActions[i].ContextKey == rankedActions[i].ContextKey) continue;
                unchanged = false;
                break;
            }
            if (unchanged) return;
        }

        QuickActions.Clear();
        foreach (var action in rankedActions) QuickActions.Add(action);
        OnPropertyChanged(nameof(HasQuickActions));
    }

    private bool TryOpenWorkspaceInIntegratedTerminal()
    {
        if (!IsFolderOpen || !IsTerminalSupported || string.IsNullOrWhiteSpace(_currentFolderPath) ||
            !Directory.Exists(_currentFolderPath))
            return false;

        if (!CreateTerminalSession(workingDirectoryOverride: _currentFolderPath)) return false;
        RecordObservedAction("ProjectTerminal");
        return true;
    }

    private async Task ExecuteDiscoveredQuickActionAsync(string id)
    {
        if (!_discoveredQuickActions.TryGetValue(id, out var discovered)) return;

        var result = await QuickActionRunner.RunAsync(
            async () =>
            {
                await discovered.Command.Execute();
                return true;
            },
            () => RecordObservedAction(id));

        if (result.Error is null) return;
        KodoDiagnostics.LogWarning($"Quick Action '{id}' failed.", result.Error);
        ShowStatusBarMessage($"{discovered.Command.Title} failed. See the log for details.");
    }

    private void RecordObservedAction(string actionId) => RecordObservedAction(actionId, Guid.NewGuid());

    private void RecordObservedAction(string actionId, Guid executionId)
    {
        if (string.IsNullOrWhiteSpace(actionId) || !IsQuickActionsLearningEnabled) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RecordObservedAction(actionId, executionId));
            return;
        }

        var resolvedId = QuickActionIdentity.ForCommand(actionId);
        if (resolvedId.Length == 0) return;

        var action = ResolveObservedQuickAction(resolvedId);
        if (action is null)
        {
            RefreshQuickActions();
            action = ResolveObservedQuickAction(resolvedId);
        }
        if (action is null) return;

        if (!_quickActionObservations.TryRecord(action.Id, executionId)) return;
        RecordQuickActionSelection(action.Id, action);
    }

    private QuickActionItem? ResolveObservedQuickAction(string actionId)
    {
        if (_observedQuickActions.Resolve(actionId) is { } observed) return observed;
        if (_discoveredQuickActions.TryGetValue(actionId, out var discovered)) return discovered.Item;
        return QuickActions.FirstOrDefault(action => action.Id == actionId);
    }

    private Action WrapObservedAction(string actionId, Action? onSuccess)
    {
        var executionId = Guid.NewGuid();
        return () =>
        {
            onSuccess?.Invoke();
            RecordObservedAction(actionId, executionId);
        };
    }

    private IEnumerable<QuickActionCommandRegistration> BuildQuickActionCommandRegistrations()
    {
        foreach (var command in QuickActionCommandSource())
        {
            if (command.Id.Length == 0) continue;
            yield return new QuickActionCommandRegistration(
                command.Id, command.Title, command.Category, QuickActionIconFor(command.Category), command.IsAvailable, command.Execute);
        }

        foreach (var entry in _pluginPaletteCommands.Values
                     .OrderBy(item => item.OwnerName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Command.Title, StringComparer.OrdinalIgnoreCase))
            yield return new QuickActionCommandRegistration(
                entry.Command.Id, entry.Command.Title, entry.Command.Category, KodoIcons.PuzzlePiece, entry.Command.CanExecute, entry.Command.Execute);
    }

    private List<PaletteCommand> QuickActionCommandSource()
    {
        if (_quickActionCommandSource is not null) return _quickActionCommandSource;
        try
        {
            return _quickActionCommandSource = BuildCommandPaletteCommands();
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("Quick Action command discovery could not read the command list.", ex);
            return _quickActionCommandSource = [];
        }
    }

    private static string QuickActionIconFor(string category) => category switch
    {
        "Terminal" => KodoIcons.CommandLine,
        "Search" => KodoIcons.Magnifier,
        "Run" or "Build" => KodoIcons.ArrowPath,
        "Editor" => KodoIcons.Pencil,
        "Navigation" => KodoIcons.ChevronRight,
        "View" => KodoIcons.Square,
        "File" => KodoIcons.DocumentText,
        "Appearance" => KodoIcons.Droplet,
        "Git" => KodoIcons.Branch,
        _ => KodoIcons.EllipsisHorizontal,
    };

    private bool HasQuickActionLspCapability(string method)
    {
        if (!LspEnabled || string.IsNullOrWhiteSpace(_currentFilePath) ||
            ResolveLspExtensionForFile(_currentFilePath) is not { HasLsp: true } extension)
            return false;

        var workspace = GetWorkspaceRootForFile(_currentFilePath);
        foreach (var configuration in extension.AllLspConfigurations)
        {
            var client = _lspManager.TryGetClient(workspace, configuration);
            if (client is { IsInitialized: true } && client.Supports(method)) return true;
        }
        return false;
    }

    private async void QuickAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        await ExecuteQuickAction(id);
    }

    private async Task ExecuteQuickAction(string id)
    {
        switch (id)
        {
            case "QuickFix": await RunCodeActionsWithFallbackAsync(CodeActionScope.QuickFix); break;
            case "NextProblem": NavigateEditorDiagnostic(1); break;
            case "PreviousProblem": NavigateEditorDiagnostic(-1); break;
            case "Definition": GoToDefinitionAtCaret(); break;
            case "References": _ = FindReferencesAtCaretAsync(); break;
            case "TypeDefinition": _ = NavigateToTypeDefinitionAsync(); break;
            case "Implementation": _ = NavigateToImplementationAsync(); break;
            case "DocumentSymbols": EditorDocumentSymbolsMenuItem_OnClick(null, new RoutedEventArgs()); break;
            case "SignatureHelp": _ = ShowSignatureHelpAsync(); break;
            case "Rename": _ = RenameSymbolAsync(); break;
            case "Format": _ = FormatDocumentAsync(); break;
            case "FindInFile": if (EditorTextBox?.Document is not null) OpenSearchPanel(SearchMode.FindInFile); break;
            case "ToggleComment":
                if (EditorTextBox?.Document is { } commentDocument && EditorTextBox.TextArea is { } commentArea)
                    ToggleLineComment(commentDocument, commentArea, commentArea.Selection, commentArea.Caret);
                break;
            case "SelectLine":
                if (EditorTextBox?.Document is { } selectDocument && EditorTextBox.TextArea is { } selectArea)
                    EditorSelectCurrentLine(selectDocument, selectArea);
                break;
            case "MoveLineUp": RunEditorLineAction(-1, duplicate: false, delete: false, insertBelow: null); break;
            case "MoveLineDown": RunEditorLineAction(1, duplicate: false, delete: false, insertBelow: null); break;
            case "DuplicateLineUp": RunEditorLineAction(-1, duplicate: true, delete: false, insertBelow: null); break;
            case "DuplicateLineDown": RunEditorLineAction(1, duplicate: true, delete: false, insertBelow: null); break;
            case "DeleteLine": RunEditorLineAction(0, duplicate: false, delete: true, insertBelow: null); break;
            case "InsertLineAbove": RunEditorLineAction(0, duplicate: false, delete: false, insertBelow: false); break;
            case "InsertLineBelow": RunEditorLineAction(0, duplicate: false, delete: false, insertBelow: true); break;
            case "ToggleBookmark": if (ActiveRelativePath() is not null) ToggleBookmarkAtCaret(); break;
            case "ChangeOccurrences": if (EditorTextBox?.TextArea?.Selection is { IsEmpty: false }) EditorChangeAllOccurrencesMenuItem_OnClick(null, new RoutedEventArgs()); break;
            case "ProjectTerminal":
                var terminalResult = await QuickActionRunner.RunAsync(
                    () => Task.FromResult(TryOpenWorkspaceInIntegratedTerminal()));
                if (terminalResult.Error is not null)
                {
                    KodoDiagnostics.LogWarning("Quick Action 'ProjectTerminal' failed.", terminalResult.Error);
                    ShowStatusBarMessage("The workspace terminal could not be opened.");
                }
                break;
            case "ProjectSearch": if (IsFolderOpen) OpenSearchPanel(SearchMode.ProjectSearch); break;
            case "CopyProjectPath":
                if (!string.IsNullOrWhiteSpace(_currentFolderPath) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(_currentFolderPath);
                    RecordObservedAction(id);
                }
                break;
            default:
                await ExecuteDiscoveredQuickActionAsync(id);
                break;
        }
    }

    private void RunEditorLineAction(int direction, bool duplicate, bool delete, bool? insertBelow)
    {
        if (EditorTextBox?.Document is not { } document || EditorTextBox.TextArea is not { } area) return;
        if (insertBelow is { } below) EditorInsertLine(document, area, below);
        else if (delete) EditorDeleteLines(document, area);
        else if (duplicate) EditorDuplicateLines(document, area, direction);
        else EditorMoveLines(document, area, direction);
    }
}
