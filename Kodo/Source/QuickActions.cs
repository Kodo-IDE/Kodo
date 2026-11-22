// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kodo.Models;
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
    private bool _isQuickActionsLearningEnabled = true;
    private QuickActionLearningStore _quickActionLearningStore = null!;
    private readonly DispatcherTimer _quickActionLearningSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    public ObservableCollection<QuickActionItem> QuickActions { get; } = new();
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
        get => _isQuickActionsLearningEnabled;
        set
        {
            if (_isQuickActionsLearningEnabled == value) return;
            _isQuickActionsLearningEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            RefreshQuickActions();
        }
    }

    public bool HasQuickActionLearningData => _quickActionLearningStore?.HasPreferences == true;

    private void InitializeQuickActionLearning(AppSettings settings)
    {
        _isQuickActionsLearningEnabled = settings.QuickActionsLearningEnabled;
        _quickActionLearningStore = QuickActionLearningStore.Load(KodoPaths.SettingsWritePath("quick-actions-learning.json"));
        if (_quickActionLearningStore.LoadFailed)
            KodoDiagnostics.LogDebug("Quick Actions learning data could not be read; using default recommendations.");
        _quickActionLearningSaveTimer.Tick += QuickActionLearningSaveTimer_OnTick;
    }

    private void QuickActionLearningSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _quickActionLearningSaveTimer.Stop();
        _ = SaveQuickActionLearningAsync();
    }

    private async Task SaveQuickActionLearningAsync()
    {
        try { await Task.Run(_quickActionLearningStore.Save); }
        catch (Exception ex) { KodoDiagnostics.LogWarning("Quick Actions learning data could not be saved.", ex); }
    }

    private async void ResetQuickActionLearningButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await ShowConfirmationDialogAsync("Reset learned preferences", "Quick Actions will return to their default recommendations.", "Reset", "Cancel", isDestructive: true)) return;
        _quickActionLearningStore.Reset();
        _quickActionLearningSaveTimer.Stop();
        OnPropertyChanged(nameof(HasQuickActionLearningData));
        RefreshQuickActions();
        await SaveQuickActionLearningAsync();
    }

    private void RecordQuickActionSelection(string actionId, QuickActionItem? selected = null)
    {
        selected ??= QuickActions.FirstOrDefault(action => action.Id == actionId);
        if (selected is null || !IsQuickActionsLearningEnabled) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsQuickActionsLearningEnabled) return;
            _quickActionLearningStore.RecordSelection(selected.Id, selected.ContextKey, DateTime.UtcNow, learningEnabled: true, selected.LearningFeatures);
            OnPropertyChanged(nameof(HasQuickActionLearningData));
            _quickActionLearningSaveTimer.Stop();
            _quickActionLearningSaveTimer.Start();
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
        {
            AddQuickAction(actions, "ChangeOccurrences", "Change All Occurrences", KodoIcons.ChangeOccurrences, Context("selection"), 105);
            AddQuickAction(actions, "Cut", "Cut Selection", KodoIcons.Cut, Context("selection"), 104);
            AddQuickAction(actions, "Copy", "Copy Selection", KodoIcons.Copy, Context("selection"), 103);
        }

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
        if (document?.UndoStack.CanUndo == true)
            AddQuickAction(actions, "Undo", "Undo", KodoIcons.Undo, Context("editor"), 73);
        if (document?.UndoStack.CanRedo == true)
            AddQuickAction(actions, "Redo", "Redo", KodoIcons.Redo, Context("editor"), 72);
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

        var rankedActions = _quickActionLearningStore.Rank(actions, IsQuickActionsLearningEnabled, DateTime.UtcNow, maximum: 3);

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
        var selectedAction = QuickActions.FirstOrDefault(action => action.Id == id);
        void Record() => RecordQuickActionSelection(id, selectedAction);
        switch (id)
        {
            case "QuickFix": await RunCodeActionsWithFallbackAsync(CodeActionScope.QuickFix, Record); break;
            case "NextProblem": if (NavigateEditorDiagnostic(1)) Record(); break;
            case "PreviousProblem": if (NavigateEditorDiagnostic(-1)) Record(); break;
            case "Definition": GoToDefinitionAtCaret(Record); break;
            case "References": _ = FindReferencesAtCaretAsync(Record); break;
            case "TypeDefinition": _ = NavigateToTypeDefinitionAsync(Record); break;
            case "Implementation": _ = NavigateToImplementationAsync(Record); break;
            case "DocumentSymbols": EditorDocumentSymbolsMenuItem_OnClick(null, new RoutedEventArgs()); Record(); break;
            case "SignatureHelp": _ = ShowSignatureHelpAsync(Record); break;
            case "Rename": _ = RenameSymbolAsync(Record); break;
            case "Format": _ = FormatDocumentAsync(Record); break;
            case "FindInFile": if (EditorTextBox?.Document is not null) { OpenSearchPanel(SearchMode.FindInFile); Record(); } break;
            case "ToggleComment":
                if (EditorTextBox?.Document is { } commentDocument && EditorTextBox.TextArea is { } commentArea)
                {
                    ToggleLineComment(commentDocument, commentArea, commentArea.Selection, commentArea.Caret);
                    Record();
                }
                break;
            case "Undo":
                if (EditorTextBox?.Document is { UndoStack.CanUndo: true } undoDocument) { undoDocument.UndoStack.Undo(); Record(); }
                break;
            case "Redo":
                if (EditorTextBox?.Document is { UndoStack.CanRedo: true } redoDocument) { redoDocument.UndoStack.Redo(); Record(); }
                break;
            case "SelectLine":
                if (EditorTextBox?.Document is { } selectDocument && EditorTextBox.TextArea is { } selectArea)
                {
                    EditorSelectCurrentLine(selectDocument, selectArea);
                    Record();
                }
                break;
            case "MoveLineUp": RunEditorLineAction(-1, duplicate: false, delete: false, insertBelow: null, Record); break;
            case "MoveLineDown": RunEditorLineAction(1, duplicate: false, delete: false, insertBelow: null, Record); break;
            case "DuplicateLineUp": RunEditorLineAction(-1, duplicate: true, delete: false, insertBelow: null, Record); break;
            case "DuplicateLineDown": RunEditorLineAction(1, duplicate: true, delete: false, insertBelow: null, Record); break;
            case "DeleteLine": RunEditorLineAction(0, duplicate: false, delete: true, insertBelow: null, Record); break;
            case "InsertLineAbove": RunEditorLineAction(0, duplicate: false, delete: false, insertBelow: false, Record); break;
            case "InsertLineBelow": RunEditorLineAction(0, duplicate: false, delete: false, insertBelow: true, Record); break;
            case "ToggleBookmark": if (EditorTextBox?.Document is not null && ActiveRelativePath() is not null) { ToggleBookmarkAtCaret(); Record(); } break;
            case "ChangeOccurrences": if (EditorTextBox?.TextArea?.Selection is { IsEmpty: false }) { EditorChangeAllOccurrencesMenuItem_OnClick(null, new RoutedEventArgs()); Record(); } break;
            case "Cut": _ = CutSelectionAsync(Record); break;
            case "Copy": _ = CopySelectionAsync(Record); break;
            case "ProjectTerminal":
                var terminalCount = TerminalSessions.Count;
                CreateTerminalSession(workingDirectoryOverride: _currentFolderPath);
                if (TerminalSessions.Count > terminalCount) Record();
                break;
            case "ProjectSearch": if (IsFolderOpen) { OpenSearchPanel(SearchMode.ProjectSearch); Record(); } break;
            case "CopyProjectPath":
                if (!string.IsNullOrWhiteSpace(_currentFolderPath) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(_currentFolderPath);
                    Record();
                }
                break;
        }
    }

    private void RunEditorLineAction(int direction, bool duplicate, bool delete, bool? insertBelow, Action onSuccess)
    {
        if (EditorTextBox?.Document is not { } document || EditorTextBox.TextArea is not { } area) return;
        if (insertBelow is { } below) EditorInsertLine(document, area, below);
        else if (delete) EditorDeleteLines(document, area);
        else if (duplicate) EditorDuplicateLines(document, area, direction);
        else EditorMoveLines(document, area, direction);
        onSuccess();
    }
}
