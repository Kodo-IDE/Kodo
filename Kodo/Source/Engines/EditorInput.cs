// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Animation;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Avalonia.Threading;
using Kodo.Editor;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{
    private bool TryHandleEditorHorizontalScrollKey(KeyEventArgs e)
    {
        if (MatchesKeybind(e, "ScrollEditorLeft") && ScrollEditorHorizontally(-EditorHorizontalScrollStep))
            return ConsumeKey(e);

        if (MatchesKeybind(e, "ScrollEditorRight") && ScrollEditorHorizontally(EditorHorizontalScrollStep))
            return ConsumeKey(e);

        if (MatchesKeybind(e, "ScrollEditorPageLeft") && ScrollEditorHorizontallyByPage(-1))
            return ConsumeKey(e);

        if (MatchesKeybind(e, "ScrollEditorPageRight") && ScrollEditorHorizontallyByPage(1))
            return ConsumeKey(e);

        return false;
    }

    private static bool ConsumeKey(KeyEventArgs e)
    {
        e.Handled = true;
        return true;
    }

    private void MainWindow_EditorKeyIntercept_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DiagnosticPopup.IsOpen && IsEditorKeyEvent(e))
            HideDiagnosticPopup();

        if (_completionWindow is not null && e.Key == Key.Tab)
        {
            var firstSuggestion = _completionWindow.CompletionList.CompletionData
                .OfType<InsightSuggestion>()
                .FirstOrDefault();
            if (firstSuggestion is not null && EditorTextBox?.Document is not null)
            {
                var segment = new TextSegment
                {
                    StartOffset = _completionWindow.StartOffset,
                    EndOffset = EditorTextBox.TextArea.Caret.Offset,
                };
                firstSuggestion.Complete(EditorTextBox.TextArea, segment, EventArgs.Empty);
            }
            CloseCompletionWindow();
            e.Handled = true;
            return;
        }

        if (_completionWindow is not null && e.Key is Key.Escape or Key.Enter
            or Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            return;
        }

        if (IsTerminalVisible && ActiveTerminalSession is not null)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            var isTerminalFocused = focused is not null &&
                (ReferenceEquals(focused, TerminalHostControl) ||
                 focused.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, TerminalHostControl)));

            if (isTerminalFocused)
                return;
        }

        if (!IsEditorKeyEvent(e))
            return;

        if (TryHandleEditorHorizontalScrollKey(e))
            return;

        if (EditorTextBox?.Document is null)
            return;

        var textArea = EditorTextBox.TextArea;
        var caret = textArea.Caret;
        var doc = EditorTextBox.Document;
        if (MatchesKeybind(e, "FindInProject"))
        {
            OpenSearchPanel(SearchMode.ProjectSearch);
            e.Handled = true;
            return;
        }
        if (MatchesKeybind(e, "FindInFile"))
        {
            OpenSearchPanel(SearchMode.FindInFile);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.None:
                HandleSmartEnter(doc, caret);
                e.Handled = true;
                return;

            case Key.Tab when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.Shift:
                HandleTabKey(() => HandleOutdent(doc, textArea.Selection, caret), doc, caret);
                e.Handled = true;
                return;

            case Key.Tab when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.None:
                HandleTabKey(() => HandleIndent(doc, textArea.Selection, caret), doc, caret);
                e.Handled = true;
                return;

            case Key.Back when IsSmartSyntaxEnabled():
                if (HandleSmartBackspace(doc, caret))
                {
                    e.Handled = true;
                    return;
                }
                break;

            case Key.V when IsSmartSyntaxEnabled() && e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                _ = HandleSmartPasteAsync(doc, textArea, caret);
                return;

        }

        if (TryHandleEditorLineAndNavigationKeys(e))
            return;

        if (IsSmartSyntaxEnabled() && MatchesKeybind(e, "ToggleLineComment"))
        {
            ToggleLineComment(doc, textArea, textArea.Selection, caret);
            e.Handled = true;
        }
    }

    private bool TryHandleEditorLineAndNavigationKeys(KeyEventArgs e)
    {
        var textArea = EditorTextBox?.TextArea;
        var doc = EditorTextBox?.Document;
        if (textArea is null || doc is null) return false;

        if (MatchesKeybind(e, "EditorGoBack")) { _ = NavigateEditorHistoryAsync(true); e.Handled = true; return true; }
        if (MatchesKeybind(e, "EditorGoForward")) { _ = NavigateEditorHistoryAsync(false); e.Handled = true; return true; }
        if (MatchesKeybind(e, "NextProblem")) { NavigateEditorDiagnostic(1); e.Handled = true; return true; }
        if (MatchesKeybind(e, "PreviousProblem")) { NavigateEditorDiagnostic(-1); e.Handled = true; return true; }
        if (MatchesKeybind(e, "MoveLineUp")) { EditorMoveLines(doc, textArea, -1); e.Handled = true; return true; }
        if (MatchesKeybind(e, "MoveLineDown")) { EditorMoveLines(doc, textArea, 1); e.Handled = true; return true; }
        if (MatchesKeybind(e, "DuplicateLineUp")) { EditorDuplicateLines(doc, textArea, -1); e.Handled = true; return true; }
        if (MatchesKeybind(e, "DuplicateLineDown")) { EditorDuplicateLines(doc, textArea, 1); e.Handled = true; return true; }
        if (MatchesKeybind(e, "DeleteLine")) { EditorDeleteLines(doc, textArea); e.Handled = true; return true; }
        if (MatchesKeybind(e, "InsertLineAbove")) { EditorInsertLine(doc, textArea, false); e.Handled = true; return true; }
        if (MatchesKeybind(e, "InsertLineBelow")) { EditorInsertLine(doc, textArea, true); e.Handled = true; return true; }
        if (MatchesKeybind(e, "ChangeAllOccurrences")) { EditorChangeAllOccurrencesMenuItem_OnClick(null, null!); e.Handled = true; return true; }
        if (MatchesKeybind(e, "GoToLine")) { _ = EditorGoToLineAsync(doc, textArea); e.Handled = true; return true; }
        if (MatchesKeybind(e, "SelectCurrentLine")) { EditorSelectCurrentLine(doc, textArea); e.Handled = true; return true; }
        if (MatchesKeybind(e, "ToggleHighlightCurrentLine")) { EditorToggleHighlightCurrentLine(); e.Handled = true; return true; }
        return false;
    }

    private void EditorInvalidateEditorLayers()
    {
        var textView = EditorTextBox?.TextArea?.TextView;
        if (textView is null) return;
        textView.InvalidateLayer(KnownLayer.Background);
        textView.InvalidateLayer(KnownLayer.Text);
    }

    private (int First, int Last) EditorGetLineOperationRange(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea)
    {
        var selection = textArea.Selection;
        if (selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null)
        {
            var first = doc.GetLineByOffset(selection.SurroundingSegment.Offset).LineNumber;
            var last = doc.GetLineByOffset(selection.SurroundingSegment.EndOffset).LineNumber;
            return (first, last);
        }

        var caret = textArea.Caret;
        if (caret is null) return (-1, -1);
        var line = doc.GetLineByOffset(Math.Clamp(caret.Offset, 0, doc.TextLength));
        return (line.LineNumber, line.LineNumber);
    }

    private void EditorRestoreLineSelection(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, int firstLine, int lastLine)
    {
        var clampedFirst = Math.Clamp(firstLine, 1, doc.LineCount);
        var clampedLast = Math.Clamp(lastLine, clampedFirst, doc.LineCount);
        var startLine = doc.GetLineByNumber(clampedFirst);
        var endLine = doc.GetLineByNumber(clampedLast);
        var start = startLine.Offset;
        var end = clampedLast >= doc.LineCount ? endLine.EndOffset : endLine.Offset;
        SetCaretOffsetSafely(textArea.Caret, doc, start);
        textArea.Caret.Offset = Math.Clamp(end, start, doc.TextLength);
    }

    private void EditorMoveLines(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, int direction)
    {
        var (first, last) = EditorGetLineOperationRange(doc, textArea);
        if (first < 0) return;
        if (direction < 0 && first == 1) return;
        if (direction > 0 && last >= doc.LineCount) return;

        doc.UndoStack.StartUndoGroup();
        try
        {
            if (direction > 0)
            {
                var block = doc.GetLineByNumber(last + 1);
                var text = doc.GetText(block);
                doc.Remove(block.Offset, block.TotalLength);
                doc.Insert(doc.GetLineByNumber(first).Offset, text);
            }
            else
            {
                var block = doc.GetLineByNumber(first);
                var text = doc.GetText(block);
                doc.Remove(block.Offset, block.TotalLength);
                doc.Insert(doc.GetLineByNumber(last).Offset + doc.GetLineByNumber(last).Length, text);
            }
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        EditorRestoreLineSelection(doc, textArea, first + direction, last + direction);
        EditorInvalidateEditorLayers();
    }

    private void EditorDuplicateLines(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, int direction)
    {
        var (first, last) = EditorGetLineOperationRange(doc, textArea);
        if (first < 0) return;
        var span = last - first + 1;
        var startOffset = doc.GetLineByNumber(first).Offset;
        var endOffset = doc.GetLineByNumber(last).EndOffset;
        var text = doc.GetText(startOffset, endOffset - startOffset);
        var insertOffset = direction > 0
            ? doc.GetLineByNumber(last).EndOffset
            : doc.GetLineByNumber(first).Offset;

        doc.UndoStack.StartUndoGroup();
        try
        {
            doc.Insert(insertOffset, text);
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        EditorRestoreLineSelection(doc, textArea,
            direction > 0 ? last + 1 : first,
            direction > 0 ? last + span : first);
        EditorInvalidateEditorLayers();
    }

    private void EditorDeleteLines(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea)
    {
        var (first, last) = EditorGetLineOperationRange(doc, textArea);
        if (first < 0) return;

        doc.UndoStack.StartUndoGroup();
        try
        {
            if (first == 1 && last >= doc.LineCount)
            {
                doc.Text = string.Empty;
            }
            else if (last >= doc.LineCount)
            {
                doc.Remove(doc.GetLineByNumber(first).Offset, doc.TextLength - doc.GetLineByNumber(first).Offset);
            }
            else
            {
                var start = doc.GetLineByNumber(first).Offset;
                var end = doc.GetLineByNumber(last + 1).Offset;
                doc.Remove(start, end - start);
            }
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        if (doc.TextLength == 0)
        {
            textArea.ClearSelection();
            SetCaretOffsetSafely(textArea.Caret, doc, 0);
        }
        else
        {
            var target = Math.Min(first, doc.LineCount);
            var line = doc.GetLineByNumber(target);
            SetCaretOffsetSafely(textArea.Caret, doc, Math.Clamp(line.Offset, 0, doc.TextLength));
        }

        EditorInvalidateEditorLayers();
    }

    private void EditorInsertLine(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, bool below)
    {
        var caret = textArea.Caret;
        if (caret is null) return;
        var line = doc.GetLineByOffset(Math.Clamp(caret.Offset, 0, doc.TextLength));
        var offset = below ? line.EndOffset : line.Offset;

        doc.UndoStack.StartUndoGroup();
        try
        {
            doc.Insert(offset, Environment.NewLine);
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        SetCaretOffsetSafely(caret, doc, offset + Environment.NewLine.Length);
        EditorInvalidateEditorLayers();
    }

    private async Task EditorGoToLineAsync(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea)
    {
        var answer = await ShowTextInputDialogAsync("Go to line", $"Line number (1-{doc.LineCount}):", string.Empty).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(answer)) return;
        if (!int.TryParse(answer.Trim(), out var requested)) return;
        var lineNumber = Math.Clamp(requested, 1, doc.LineCount);
        var line = doc.GetLineByNumber(lineNumber);
        textArea.ClearSelection();
        SetCaretOffsetSafely(textArea.Caret, doc, line.Offset);
        EditorTextBox?.Focus();
        EditorInvalidateEditorLayers();
    }

    private void EditorSelectCurrentLine(AvaloniaEdit.Document.TextDocument document, AvaloniaEdit.Editing.TextArea textArea)
    {
        var line = document.GetLineByOffset(Math.Clamp(textArea.Caret.Offset, 0, document.TextLength));
        EditorTextBox.Select(line.Offset, line.TotalLength);
    }

    private bool _isHighlightCurrentLineEnabled = true;

    public bool IsHighlightCurrentLineEnabled => _isHighlightCurrentLineEnabled;

    private void EditorToggleHighlightCurrentLine()
    {
        _isHighlightCurrentLineEnabled = !_isHighlightCurrentLineEnabled;
        var textView = EditorTextBox?.TextArea?.TextView;
        if (textView is not null)
        {
            textView.CurrentLineBackground = _isHighlightCurrentLineEnabled
                ? EditorBuildCurrentLineBrush()
                : null;
        }
        OnPropertyChanged(nameof(IsHighlightCurrentLineEnabled));
        EditorInvalidateEditorLayers();
    }

    private Avalonia.Media.IBrush EditorBuildCurrentLineBrush()
    {
        var baseColor = GetTerminalThemeColor(EditorBackgroundBrush, Color.Parse("#121118"));
        var overlay = Color.FromArgb(
            (byte)0x16,
            (byte)Math.Min(baseColor.R + 0x18, 255),
            (byte)Math.Min(baseColor.G + 0x18, 255),
            (byte)Math.Min(baseColor.B + 0x18, 255));
        return new SolidColorBrush(overlay);
    }

    private bool IsEditorKeyEvent(KeyEventArgs e)
    {
        if (EditorTextBox is null || e.Source is not Visual visual)
            return false;

        if (ReferenceEquals(visual, EditorTextBox) || ReferenceEquals(visual, EditorTextBox.TextArea))
            return true;

        return visual.GetSelfAndVisualAncestors().Any(v =>
            ReferenceEquals(v, EditorTextBox) || ReferenceEquals(v, EditorTextBox.TextArea));
    }

    private void HandleTabKey(Action tabAction, AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret)
    {
        try
        {
            tabAction();
        }
        catch
        {
            var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
            doc.Insert(safeOffset, GetIndentUnit());
            SetCaretOffsetSafely(caret, doc, safeOffset + GetIndentUnit().Length);
        }
    }

    private void HandleSmartEnter(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret)
    {
        var offset = caret.Offset;
        var line = doc.GetLineByOffset(offset);
        var lineText = doc.GetText(line);
        var caretColumnInLine = offset - line.Offset;
        var textBeforeCaret = lineText[..Math.Min(caretColumnInLine, lineText.Length)];
        var textAfterCaret = lineText[Math.Min(caretColumnInLine, lineText.Length)..];
        var indent = GetLeadingWhitespace(textBeforeCaret);
        var trimmedBeforeCaret = textBeforeCaret.TrimEnd();
        var extraIndent = ShouldIncreaseIndentAfter(trimmedBeforeCaret) ? GetIndentUnit() : string.Empty;

        if (ShouldInsertStructuredBlock(trimmedBeforeCaret, textAfterCaret))
        {
            var blockText = Environment.NewLine + indent + extraIndent + Environment.NewLine + indent;
            doc.Insert(offset, blockText);
            caret.Offset = offset + Environment.NewLine.Length + indent.Length + extraIndent.Length;
            try { caret.BringCaretToView(); } catch { }
            return;
        }

        var adjustedIndent = StartsWithClosingDelimiter(textAfterCaret)
            ? RemoveOneIndentUnit(indent)
            : indent;

        var newLineText = Environment.NewLine + adjustedIndent + extraIndent;
        doc.Insert(offset, newLineText);
        caret.Offset = offset + newLineText.Length;
        try { caret.BringCaretToView(); } catch { }
    }

    private bool HandleSmartBackspace(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Caret caret)
    {
        var selection = EditorTextBox.TextArea.Selection;
        if (selection is not null && !selection.IsEmpty)
            return false;

        var offset = caret.Offset;
        if (offset <= 0 || offset >= doc.TextLength)
            return false;

        var opening = doc.GetCharAt(offset - 1);
        if (!BracketPairs.TryGetValue(opening, out var closing))
            return false;

        if (doc.GetCharAt(offset) != closing)
            return false;

        try
        {
            doc.Remove(offset - 1, 2);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("HandleSmartBackspace: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try { doc.Remove(offset - 1, 2); SetCaretOffsetSafely(caret, doc, offset - 1); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
            return true;
        }
        SetCaretOffsetSafely(caret, doc, offset - 1);
        return true;
    }

    private void HandleIndent(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        var textArea = EditorTextBox?.TextArea;
        if (textArea is null) return;
        try
        {
            if (selection is null || selection.IsEmpty)
            {
                var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
                doc.Insert(safeOffset, GetIndentUnit());
                SetCaretOffsetSafely(caret, doc, safeOffset + GetIndentUnit().Length);
                return;
            }

            var segment = selection.SurroundingSegment;
            if (segment is null)
            {
                var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
                doc.Insert(safeOffset, GetIndentUnit());
                SetCaretOffsetSafely(caret, doc, safeOffset + GetIndentUnit().Length);
                return;
            }

            var (first, last) = EditorGetLineOperationRange(doc, textArea);
            if (first < 0) return;
            var firstLine = doc.GetLineByNumber(first);
            var lastLine = doc.GetLineByNumber(last);
            var startOffset = firstLine.Offset;
            var sourceLength = lastLine.EndOffset - startOffset;
            var sourceText = doc.GetText(startOffset, sourceLength);
            var indent = GetIndentUnit();
            var newText = new StringBuilder(sourceText.Length + (indent.Length * (last - first + 1)));
            newText.Append(indent);
            for (var index = 0; index < sourceText.Length; index++)
            {
                var current = sourceText[index];
                newText.Append(current);
                if (current == '\r')
                {
                    if (index + 1 < sourceText.Length && sourceText[index + 1] == '\n')
                        newText.Append(sourceText[++index]);
                    if (index + 1 < sourceText.Length) newText.Append(indent);
                }
                else if (current == '\n' && index + 1 < sourceText.Length)
                {
                    newText.Append(indent);
                }
            }

            doc.Replace(startOffset, sourceLength, newText.ToString());
            textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, startOffset, startOffset + newText.Length);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("HandleIndent: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try { HandleIndent(doc, selection, caret); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void HandleOutdent(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        var textArea = EditorTextBox?.TextArea;
        if (textArea is null) return;
        try
        {
            doc.UndoStack.StartUndoGroup();
            if (selection is null || selection.IsEmpty)
            {
                var line = doc.GetLineByOffset(caret.Offset);
                var lineText = doc.GetText(line);
                var caretColumnInLine = caret.Offset - line.Offset;
                var removable = GetOutdentLength(lineText, caretColumnInLine);
                if (removable <= 0)
                    return;

                doc.Remove(line.Offset, removable);
                SetCaretOffsetSafely(caret, doc, line.Offset + Math.Max(0, caretColumnInLine - removable));
                return;
            }

            var segment = selection.SurroundingSegment;
            if (segment is null)
                return;

            var (first, last) = EditorGetLineOperationRange(doc, textArea);
            if (first < 0) return;
            var firstLine = doc.GetLineByNumber(first);
            var lastLine = doc.GetLineByNumber(last);
            var startOffset = firstLine.Offset;
            var sourceLength = lastLine.EndOffset - startOffset;
            var sourceText = doc.GetText(startOffset, sourceLength);
            var newText = new StringBuilder(sourceText.Length);
            var changed = false;
            var lineStart = 0;
            while (lineStart < sourceText.Length)
            {
                var lineEnd = lineStart;
                while (lineEnd < sourceText.Length && sourceText[lineEnd] is not '\r' and not '\n') lineEnd++;
                var line = sourceText.AsSpan(lineStart, lineEnd - lineStart);
                var removable = GetOutdentLength(line, line.Length);
                newText.Append(line[removable..]);
                changed |= removable > 0;
                if (lineEnd < sourceText.Length)
                {
                    var delimiterLength = sourceText[lineEnd] == '\r' && lineEnd + 1 < sourceText.Length && sourceText[lineEnd + 1] == '\n' ? 2 : 1;
                    newText.Append(sourceText, lineEnd, delimiterLength);
                    lineStart = lineEnd + delimiterLength;
                }
                else
                {
                    lineStart = sourceText.Length;
                }
            }

            if (!changed) return;
            doc.Replace(startOffset, sourceLength, newText.ToString());
            textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, startOffset, startOffset + newText.Length);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
        {
            KodoDiagnostics.LogDebug("HandleOutdent: Visual line race suppressed", ex);
            Dispatcher.UIThread.Post(() =>
            {
                try { HandleOutdent(doc, selection, caret); } catch { }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        finally
        {
            try { doc.UndoStack.EndUndoGroup(); } catch { }
        }
    }

    private static string GetLeadingWhitespace(string text)
    {
        var length = 0;
        while (length < text.Length && char.IsWhiteSpace(text[length]) && text[length] != '\r' && text[length] != '\n')
            length++;

        return text[..length];
    }

    private static bool ShouldIncreaseIndentAfter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.EndsWith(":", StringComparison.Ordinal) ||
               text.EndsWith("{", StringComparison.Ordinal) ||
               text.EndsWith("[", StringComparison.Ordinal) ||
               text.EndsWith("(", StringComparison.Ordinal) ||
               text.EndsWith("=>", StringComparison.Ordinal) ||
               text.EndsWith(" then", StringComparison.OrdinalIgnoreCase) ||
               text.EndsWith(" do", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldInsertStructuredBlock(string textBeforeCaret, string textAfterCaret)
    {
        var trimmedAfter = textAfterCaret.TrimStart();
        if (string.IsNullOrEmpty(trimmedAfter))
            return false;

        if (!BracketPairs.TryGetValue(textBeforeCaret.LastOrDefault(), out var closing))
            return false;

        return trimmedAfter.Length > 0 && trimmedAfter[0] == closing && closing is ')' or ']' or '}';
    }

    private string ReindentPastedText(string text, AvaloniaEdit.Document.TextDocument doc, int offset)
    {
        var normalized = NormalizeLineEndings(text);
        if (!normalized.Contains('\n'))
            return text;

        var line = doc.GetLineByOffset(Math.Clamp(offset, 0, doc.TextLength));
        var lineText = doc.GetText(line);
        var caretColumnInLine = Math.Clamp(offset - line.Offset, 0, lineText.Length);
        var textBeforeCaret = lineText[..caretColumnInLine];
        var baseIndent = GetLeadingWhitespace(textBeforeCaret);
        var pasteLines = normalized.Split('\n');

        if (pasteLines.Length <= 1)
            return text;

        var firstNonEmptyIndex = Array.FindIndex(pasteLines, static l => !string.IsNullOrWhiteSpace(l));
        if (firstNonEmptyIndex < 0)
            return text;

        var commonIndent = GetLeadingWhitespace(pasteLines[firstNonEmptyIndex]);
        for (var i = firstNonEmptyIndex + 1; i < pasteLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(pasteLines[i]))
                continue;

            commonIndent = GetSharedIndent(commonIndent, GetLeadingWhitespace(pasteLines[i]));
            if (commonIndent.Length == 0)
                break;
        }

        for (var i = 1; i < pasteLines.Length; i++)
        {
            if (pasteLines[i].Length == 0)
                continue;

            var trimmedLine = pasteLines[i];
            if (commonIndent.Length > 0 && trimmedLine.StartsWith(commonIndent, StringComparison.Ordinal))
                trimmedLine = trimmedLine[commonIndent.Length..];

            pasteLines[i] = baseIndent + trimmedLine;
        }

        return string.Join(Environment.NewLine, pasteLines);
    }

    private async Task HandleSmartPasteAsync(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Editing.Caret caret)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(EditorTextBox?.Document, doc) ||
                !ReferenceEquals(EditorTextBox.TextArea, textArea) ||
                !ReferenceEquals(textArea.Caret, caret))
                return;

            var insertionOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
            var insertionText = ReindentPastedText(text, doc, insertionOffset);
            var safeOffset = insertionOffset;

            var selection = textArea.Selection;
            if (selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null)
            {
                var segment = selection.SurroundingSegment;
                try
                {
                    doc.Replace(segment, insertionText);
                }
                catch (ArgumentException ex) when (ex.Message.Contains("visual line", StringComparison.OrdinalIgnoreCase))
                {
                    KodoDiagnostics.LogDebug("HandleSmartPasteAsync: Visual line race suppressed", ex);
                    var activationVersion = _activeTabVersion;
                    var documentVersion = doc.Version;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_activeTabVersion != activationVersion || !ReferenceEquals(EditorTextBox?.Document, doc) ||
                            !ReferenceEquals(doc.Version, documentVersion)) return;
                        try
                        {
                            doc.Replace(segment, insertionText);
                            SetCaretOffsetSafely(caret, doc, segment.Offset + insertionText.Length);
                        }
                        catch { }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                    return;
                }
                SetCaretOffsetSafely(caret, doc, segment.Offset + insertionText.Length);
                return;
            }

            doc.Insert(safeOffset, insertionText);
            SetCaretOffsetSafely(caret, doc, safeOffset + insertionText.Length);
        });
    }

    private void ToggleLineComment(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        var lineCommentToken = CurrentLanguageExtension?.CommentLine;
        if (string.IsNullOrWhiteSpace(lineCommentToken))
            return;

        var selected = selection is not null && !selection.IsEmpty;
        var (first, last) = EditorGetLineOperationRange(doc, textArea);
        if (first < 0) return;
        var firstLine = doc.GetLineByNumber(first);
        var lastLine = doc.GetLineByNumber(last);
        var rangeStart = firstLine.Offset;
        var sourceLength = lastLine.EndOffset - rangeStart;
        var sourceText = doc.GetText(rangeStart, sourceLength);
        var shouldUncomment = true;
        var hasNonWhitespaceLine = false;
        var lineStart = 0;
        while (lineStart < sourceText.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < sourceText.Length && sourceText[lineEnd] is not '\r' and not '\n') lineEnd++;
            var line = sourceText.AsSpan(lineStart, lineEnd - lineStart);
            if (!line.Trim().IsEmpty)
            {
                hasNonWhitespaceLine = true;
                var indentLength = 0;
                while (indentLength < line.Length && char.IsWhiteSpace(line[indentLength])) indentLength++;
                if (!line[indentLength..].StartsWith(lineCommentToken, StringComparison.Ordinal))
                    shouldUncomment = false;
            }
            if (lineEnd == sourceText.Length) break;
            lineStart = lineEnd + (sourceText[lineEnd] == '\r' && lineEnd + 1 < sourceText.Length && sourceText[lineEnd + 1] == '\n' ? 2 : 1);
        }

        if (!hasNonWhitespaceLine) return;
        var prefixLength = shouldUncomment ? -lineCommentToken.Length : lineCommentToken.Length + 1;
        var newText = new StringBuilder(Math.Max(0, sourceText.Length + (prefixLength * (last - first + 1))));
        var originalCaretOffset = caret.Offset;
        var mappedCaretOffset = originalCaretOffset;
        lineStart = 0;
        while (lineStart < sourceText.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < sourceText.Length && sourceText[lineEnd] is not '\r' and not '\n') lineEnd++;
            var line = sourceText.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Trim().IsEmpty)
            {
                newText.Append(line);
            }
            else
            {
                var indentLength = 0;
                while (indentLength < line.Length && char.IsWhiteSpace(line[indentLength])) indentLength++;
                if (shouldUncomment)
                {
                    var content = line[indentLength..];
                    var removed = content.StartsWith(lineCommentToken, StringComparison.Ordinal) ? lineCommentToken.Length : 0;
                    if (removed > 0 && removed < content.Length && content[removed] == ' ') removed++;
                    newText.Append(line[..indentLength]).Append(content[removed..]);
                    if (!selected && lineStart == 0 && removed > 0)
                    {
                        var removalStart = rangeStart + indentLength;
                        var removalEnd = removalStart + removed;
                        mappedCaretOffset = originalCaretOffset >= removalEnd
                            ? originalCaretOffset - removed
                            : originalCaretOffset > removalStart ? removalStart : originalCaretOffset;
                    }
                }
                else
                {
                    newText.Append(line[..indentLength]).Append(lineCommentToken).Append(' ').Append(line[indentLength..]);
                    if (!selected && lineStart == 0 && originalCaretOffset >= rangeStart + indentLength)
                        mappedCaretOffset = originalCaretOffset + lineCommentToken.Length + 1;
                }
            }

            if (lineEnd == sourceText.Length) break;
            var delimiterLength = sourceText[lineEnd] == '\r' && lineEnd + 1 < sourceText.Length && sourceText[lineEnd + 1] == '\n' ? 2 : 1;
            newText.Append(sourceText, lineEnd, delimiterLength);
            lineStart = lineEnd + delimiterLength;
        }

        doc.Replace(rangeStart, sourceLength, newText.ToString());
        if (selected)
        {
            textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, rangeStart, rangeStart + newText.Length);
        }
        else
        {
            SetCaretOffsetSafely(caret, doc, mappedCaretOffset);
        }
    }

}
