// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{

    private void EditorStateRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _editorStateRefreshTimer.Stop();
        RefreshState(fullRefresh: _pendingFullStateRefresh);
    }

    private void QueueInsightRefresh()
    {
        _InsightRefreshTimer.Stop();
        _InsightRefreshTimer.Start();
    }

    private async void InsightRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _InsightRefreshTimer.Stop();
        await UpdateInsightAsync();
        await UpdateDeadCodeHighlightingAsync();
        await UpdateErrorHighlightingAsync();
    }

    private void WordCountRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _wordCountRefreshTimer.Stop();
        RefreshWordCount();
        OnPropertyChanged(nameof(IsWordCountVisible));
    }

    private void RefreshWordCount()
    {
        if (!HasDocumentOpen || !IsPlainTextFile(_currentFilePath) || EditorTextBox?.Document is null)
        {
            WordCountText = string.Empty;
            return;
        }

        var text = EditorTextBox.Document.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            WordCountText = "0 words";
            return;
        }

        // Count words via Span enumeration - zero allocations vs Split(array+Length)
        var chars = text.AsSpan();
        int wordCount = 0;
        bool inWord = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                wordCount++;
            }
        }
        WordCountText = $"{wordCount} words";
    }

    private string? GetDocumentStatusText()
    {
        if (!HasDocumentOpen) return null;

        if (IsAutoSaveEnabled && HasFileOpen)
        {
            if (!string.IsNullOrWhiteSpace(_autoSaveStatusMessage))
                return _autoSaveStatusMessage;

            if (_isSaving)
                return AutoSaveSavingMessage;

            if (_isDirty || _autoSaveTimer.IsEnabled)
                return "Unsaved";
        }

        return _isDirty ? "Unsaved" : null;
    }

    private string? _hoveredDiagnosticLineText;
    private string? _hoveredDiagnosticMessage;

    private void HideDiagnosticPopup()
    {
        _diagnosticPopupHideTimer.Stop();
        if (DiagnosticPopup.IsOpen)
            DiagnosticPopup.IsOpen = false;
        var textView = EditorTextBox?.TextArea?.TextView;
        if (textView is not null)
            ToolTip.SetTip(textView, null);
    }

    // Show link or diagnostic popup on hover (with X to dismiss)
    private void EditorTextView_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _diagnosticPopupHideTimer.Stop();
        var textView = EditorTextBox.TextArea.TextView;
        var position = e.GetPosition(textView);
        var nowOverLink = IsPointerOverLink(position, textView);
        var errorReason = nowOverLink ? null : GetErrorReasonAt(position, textView);
        var deadCodeReason = nowOverLink ? null : GetDeadCodeReasonAt(position, textView);

        string? diagnosticMessage = BuildDiagnosticMessage(errorReason, deadCodeReason);
        string? primaryReason = errorReason ?? deadCodeReason;

        if (nowOverLink == _isPointerOverEditorLink &&
            (deadCodeReason == _hoveredDeadCodeReason) && (errorReason == _hoveredErrorReason))
            return; // no state change

        _isPointerOverEditorLink = nowOverLink;
        _hoveredDeadCodeReason = deadCodeReason;
        _hoveredErrorReason = errorReason;

        _hoveredDiagnosticLineText = nowOverLink ? null : GetLineTextAt(position);
        _hoveredDiagnosticMessage = diagnosticMessage;

        if (nowOverLink)
        {
            DiagnosticPopup.IsOpen = false;
            ToolTip.SetTip(textView, "Ctrl+click to open link");
            ToolTip.SetShowDelay(textView, 400);
            textView.Cursor = new Cursor(StandardCursorType.Hand);
        }
        else if (diagnosticMessage is not null)
        {
            ToolTip.SetTip(textView, null);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
            DiagnosticPopupText.Text = diagnosticMessage;
            DiagnosticPopup.PlacementTarget = textView;
            try
            {
                var floor = textView.GetPositionFloor(position + textView.ScrollOffset);
                if (floor is not null)
                {
                    var y = textView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(floor.Value.Line, 1), AvaloniaEdit.Rendering.VisualYPosition.LineTop).Y - textView.ScrollOffset.Y;
                    if (double.IsNaN(y) || double.IsInfinity(y)) y = position.Y;
                    DiagnosticPopup.HorizontalOffset = Math.Clamp(position.X, 8, Math.Max(8, textView.Bounds.Width - 430));
                    DiagnosticPopup.VerticalOffset = Math.Clamp(y, 4, Math.Max(4, textView.Bounds.Height - 100));
                }
                else
                {
                    DiagnosticPopup.HorizontalOffset = Math.Clamp(position.X, 8, 300);
                    DiagnosticPopup.VerticalOffset = Math.Clamp(position.Y, 4, 300);
                }
            }
            catch
            {
                DiagnosticPopup.HorizontalOffset = Math.Clamp(position.X, 8, 300);
                DiagnosticPopup.VerticalOffset = Math.Clamp(position.Y, 4, 300);
            }
            DiagnosticPopup.IsOpen = true;
        }
        else
        {
            DiagnosticPopup.IsOpen = false;
            ToolTip.SetTip(textView, null);
            textView.Cursor = new Cursor(StandardCursorType.Ibeam);
        }
    }

    private static string? BuildDiagnosticMessage(string? errorReason, string? deadCodeReason)
    {
        if (errorReason is null && deadCodeReason is null) return null;

        var parts = new List<string>();
        if (errorReason is not null) parts.Add($"Error: {errorReason}");
        if (deadCodeReason is not null) parts.Add($"Dead Code: {deadCodeReason}");

        return parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]}{Environment.NewLine}--------------------{Environment.NewLine}{parts[1]}",
            _ => null
        };
    }

    private string? GetLineTextAt(Point position)
    {
        try
        {
            var textView = EditorTextBox.TextArea.TextView;
            var pos = textView.GetPositionFloor(position + textView.ScrollOffset);
            if (pos is null) return null;
            var line = EditorTextBox.Document.GetLineByNumber(pos.Value.Line);
            return EditorTextBox.Document.GetText(line.Offset, line.Length);
        }
        catch
        {
            return null;
        }
    }

    private async void DiagnosticPopupDismiss_OnClick(object? sender, RoutedEventArgs e)
    {
        var lineText = _hoveredDiagnosticLineText;
        var filePath = _currentFilePath;
        var didDismiss = false;

        if (lineText is not null)
        {
            if (_hoveredErrorReason is not null)
            {
                DismissDiagnostic(filePath, lineText, _hoveredErrorReason);
                didDismiss = true;
            }
            if (_hoveredDeadCodeReason is not null)
            {
                DismissDiagnostic(filePath, lineText, _hoveredDeadCodeReason);
                didDismiss = true;
            }
        }

        if (!didDismiss && lineText is not null && DiagnosticPopupText.Text is not null)
        {
            var parts = DiagnosticPopupText.Text.Split(new[] { "--------------------" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var msg = p.Trim();
                if (string.IsNullOrWhiteSpace(msg)) continue;
                // Strip "Error: " / "Dead Code: " prefixes for signature match
                if (msg.StartsWith("Error: ", StringComparison.Ordinal))
                    msg = msg.Substring("Error: ".Length);
                else if (msg.StartsWith("Dead Code: ", StringComparison.Ordinal))
                    msg = msg.Substring("Dead Code: ".Length);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    DismissDiagnostic(filePath, lineText, msg);
                }
            }
        }

        DiagnosticPopup.IsOpen = false;
        ToolTip.SetTip(EditorTextBox.TextArea.TextView, null);

        // Force immediate unhighlight instead of waiting for debounce timer
        await UpdateErrorHighlightingAsync();
        await UpdateDeadCodeHighlightingAsync();

        _hoveredDiagnosticLineText = null;
        _hoveredDiagnosticMessage = null;
        _hoveredDeadCodeReason = null;
        _hoveredErrorReason = null;
    }

    private string? GetErrorReasonAt(Point pointerPosition, AvaloniaEdit.Rendering.TextView textView)
    {
        if (!IsInsightEnabled || !IsInsightErrorDetectionEnabled)
            return null;

        var pos = textView.GetPositionFloor(pointerPosition + textView.ScrollOffset);
        if (pos is null) return null;

        try
        {
            var doc = EditorTextBox.Document;
            var ln = pos.Value.Line;
            if (ln < 1 || ln > doc.LineCount) return null;
            var line = doc.GetLineByNumber(ln);
            return _errorHighlightRenderer.GetMessageForLine(line.Offset, line.EndOffset);
        }
        catch
        {
            return null;
        }
    }

    private string? GetDeadCodeReasonAt(Point pointerPosition, AvaloniaEdit.Rendering.TextView textView)
    {
        if (!IsInsightEnabled || !IsInsightDeadCodeEnabled)
            return null;

        var pos = textView.GetPositionFloor(pointerPosition + textView.ScrollOffset);
        if (pos is null) return null;

        try
        {
            var doc = EditorTextBox.Document;
            var ln = pos.Value.Line;
            if (ln < 1 || ln > doc.LineCount) return null;
            var line = doc.GetLineByNumber(ln);
            var colOffset = Math.Clamp(pos.Value.Column - 1, 0, line.Length);
            return _deadCodeHighlightRenderer.GetReasonAt(line.Offset + colOffset);
        }
        catch
        {
            // Document may be null or line out of range during rapid edits - treat as no hit.
            return null;
        }
    }

    private void EditorTextBox_OnTextChanged(object? sender, EventArgs e)
    {
        _insightDocVersion++;
        HideDiagnosticPopup();
        // Debounce heavy colorizer snapshot rebuilds: previously every keystroke
        // synchronously cleared caches causing UI-thread allocations (document.Text
        // copies + per-line regex state) that stalled typing in large XAML/HTML.
        // Now stale snapshots are reused for ~40ms while typing; highlight catches
        // up shortly after pause. This is the primary fix for remaining XAML lag.
        _syntaxHighlightDebounceTimer.Stop();
        _syntaxHighlightDebounceTimer.Start();

        if (_suppressDirtyTracking) return;
        ClearAutoSaveStatus();
        _isDirty = true;
        if (ActiveEditorTab is not null)
        {
            ActiveEditorTab.IsDirty = true;
        }
        QueueRefreshState(fullRefresh: true);
        QueueWordCountRefresh();
        RestartAutoSaveTimerIfNeeded();
        QueueInsightRefresh();
        if (IsFindInFileSearchMode && IsSearchPanelVisible)
        {
            _findHighlightDebounceTimer.Stop();
            _findHighlightDebounceTimer.Start();
        }
    }

    private void SyntaxHighlightDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _syntaxHighlightDebounceTimer.Stop();
        _rainbowBracketColorizer.InvalidateCache();
        _markdownColorizer.InvalidateCache();
        _htmlEmbeddedColorizer.InvalidateCache();
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
        // BackgroundRenderer (indent guides) is cheap now (visible-lines only)
        // but still benefits from coalesced invalidation.
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void FindHighlightDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _findHighlightDebounceTimer.Stop();
        if (IsFindInFileSearchMode && IsSearchPanelVisible)
            UpdateFindHighlights();
    }

    private void EditorTextArea_OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!IsSmartSyntaxEnabled()) return;
        if (IsMarkdownFile(_currentFilePath)) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        var ch     = e.Text[0];
        var caret  = EditorTextBox.TextArea.Caret;
        var doc    = EditorTextBox.Document;
        var offset = caret.Offset;
        var selection = EditorTextBox.TextArea.Selection;

        if (!selection.IsEmpty && BracketPairs.TryGetValue(ch, out var selectionClosing))
        {
            var segment = selection.SurroundingSegment;
            if (segment is not null)
            {
                var selectedText = selection.GetText();
                doc.Replace(segment, $"{ch}{selectedText}{selectionClosing}");
                caret.Offset = segment.Offset + selectedText.Length + 2;
                e.Handled = true;
                return;
            }
        }

        // Any character that will trigger an auto-inserted closer in OnTextEntered
        // (i.e. every BracketPairs key that isn't being skipped-over below) needs its
        // default insertion and the closer's insertion merged into one undo group -
        // otherwise a single Undo removes only the closer and leaves the opener behind.
        var isPairOpener = BracketPairs.ContainsKey(ch);

        if (!ClosingChars.Contains(ch))
        {
            if (isPairOpener) BeginAutoCloseUndoGroup(doc);
            return;
        }

        if (ch == '}' && TryAlignClosingDelimiterBeforeInsert(doc, caret, '}'))
            offset = caret.Offset;

        if (offset >= doc.TextLength || doc.GetCharAt(offset) != ch)
        {
            if (isPairOpener) BeginAutoCloseUndoGroup(doc);
            return;
        }

        // Asymmetric pairs are always safe to skip; symmetric pairs only skip mid-pair.
        bool skip = ch is ')' or ']' or '}' or '>';
        if (!skip && (ch == '"' || ch == '\''))
            skip = offset > 0 && doc.GetCharAt(offset - 1) == ch;

        if (skip)
        {
            caret.Offset = offset + 1;
            e.Handled = true;
        }
        else if (isPairOpener)
        {
            BeginAutoCloseUndoGroup(doc);
        }
    }

    private bool _autoCloseUndoGroupOpen;

    private void BeginAutoCloseUndoGroup(AvaloniaEdit.Document.TextDocument doc)
    {
        // Guard against a stray double-open leaving the UndoStack's group counter unbalanced.
        if (_autoCloseUndoGroupOpen) return;
        doc.UndoStack.StartUndoGroup();
        _autoCloseUndoGroupOpen = true;
    }

    private void EndAutoCloseUndoGroupIfOpen(AvaloniaEdit.Document.TextDocument doc)
    {
        if (!_autoCloseUndoGroupOpen) return;
        doc.UndoStack.EndUndoGroup();
        _autoCloseUndoGroupOpen = false;
    }

    private void EditorTextArea_OnTextEntered(object? sender, TextInputEventArgs e)
    {
        // Whatever branch we take below, if OnTextEntering opened an auto-close undo
        // group for this keystroke, it must be closed here so Undo/Redo stay balanced.
        var doc = EditorTextBox.Document;
        try
        {
            if (!IsSmartSyntaxEnabled()) return;
            if (IsMarkdownFile(_currentFilePath)) return;
            if (string.IsNullOrEmpty(e.Text)) return;
            var ch = e.Text[0];

            if (!BracketPairs.TryGetValue(ch, out var closing)) return;

            var caret  = EditorTextBox.TextArea.Caret;
            var offset = caret.Offset;

            if (ch == '"' || ch == '\'' || ch == '`')
            {
                if (offset < doc.TextLength)
                {
                    var next = doc.GetCharAt(offset);
                    if (char.IsLetterOrDigit(next) || next == ch) return;
                }
            }

            doc.Insert(offset, closing.ToString());
            caret.Offset = offset;
        }
        finally
        {
            EndAutoCloseUndoGroupIfOpen(doc);
        }
    }

    private async Task UpdateInsightAsync()
    {
        if (!IsInsightEnabled || !IsInsightCodeSuggestionsEnabled)
        {
            CloseCompletionWindow();
            return;
        }

        if (EditorTextBox?.Document is null || EditorTextBox.TextArea is null)
        {
            CloseCompletionWindow();
            return;
        }

        if (ActiveEditorTab is null || ActiveEditorTab.IsUntitled || IsPlainTextFile(_currentFilePath))
        {
            CloseCompletionWindow();
            return;
        }

        if (IsInsightBlacklisted(_currentFilePath))
        {
            CloseCompletionWindow();
            return;
        }

        var doc = EditorTextBox.Document;
        var offset = Math.Clamp(EditorTextBox.TextArea.Caret.Offset, 0, doc.TextLength);
        var text = doc.Text;

        var wordStart = InsightEngine.FindWordStart(text, offset);
        var prefix = text[wordStart..offset];

        if (prefix.Length == 0)
        {
            CloseCompletionWindow();
            return;
        }

        var fileKey = ActiveEditorTab?.Path ?? "untitled";
        var languageExtension = CurrentLanguageExtension;
        var scanVersion = _insightDocVersion;

        // ScanDocument + GetSuggestions are pure text/regex work with no Avalonia
        // dependency, so run them off the UI thread - this is the part that stalls
        // typing on large files.
        var suggestions = await Task.Run(() =>
        {
            _InsightEngine.ScanDocument(fileKey, text, languageExtension);
            return _InsightEngine.GetSuggestions(prefix, fileKey, languageExtension, text, offset);
        });

        // The document moved on while we were scanning - a fresh scan is already
        // queued for the newer text, so don't apply these stale results.
        if (scanVersion != _insightDocVersion) return;
        if (EditorTextBox?.TextArea is null) return;

        InsightSuggestion.PanelForeground = PrimaryTextBrush;
        InsightSuggestion.MutedForeground = MutedTextBrush;

        if (suggestions.Count == 0)
        {
            CloseCompletionWindow();
            return;
        }

        if (_completionWindow is null)
        {
            _completionWindow = CreateCompletionWindow();
            _completionWindow.StartOffset = wordStart;
            foreach (var suggestion in suggestions)
                _completionWindow.CompletionList.CompletionData.Add(suggestion);
            _completionWindow.Show();
        }
        else
        {
            _completionWindow.StartOffset = wordStart;
            _completionWindow.CompletionList.CompletionData.Clear();
            foreach (var suggestion in suggestions)
                _completionWindow.CompletionList.CompletionData.Add(suggestion);
        }
    }

    private void CloseCompletionWindow()
    {
        _completionWindow?.Close();
        _completionWindow = null;
    }

    private async Task UpdateDeadCodeHighlightingAsync()
    {
        if (!IsInsightEnabled || !IsInsightDeadCodeEnabled ||
            EditorTextBox?.Document is null ||
            ActiveEditorTab is null || ActiveEditorTab.IsUntitled ||
            IsPlainTextFile(_currentFilePath) ||
            HasNoFileExtension(_currentFilePath) ||
            IsInsightBlacklisted(_currentFilePath))
        {
            ClearDeadCodeHighlighting();
            HideDiagnosticPopup();
            return;
        }

        var text = EditorTextBox.Document.Text;
        var languageExtension = CurrentLanguageExtension;
        var folderPath = _currentFolderPath;
        var filePath = _currentFilePath;
        var scanVersion = _insightDocVersion;

        var rawSpans = await Task.Run(() => _InsightEngine.FindDeadCode(text, languageExtension, folderPath, filePath));

        if (scanVersion != _insightDocVersion) return;
        if (EditorTextBox?.Document is null) return;

        var spans = FilterDismissedDeadCodeSpans(rawSpans, EditorTextBox.Document, _currentFilePath);
        _deadCodeHighlightRenderer.SetSpans(spans);
        _deadCodeTextBrightener.SetSpans(spans);
        _errorHighlightRenderer.SetDeadCodeSpans(spans);
        _errorTextDarkener.SetSpans(_errorHighlightRenderer.Spans, spans);
        if (spans.Count != rawSpans.Count)
            HideDiagnosticPopup();
        EditorTextBox.TextArea.TextView.Redraw();
    }

    private List<InsightEngine.DeadCodeSpan> FilterDismissedDeadCodeSpans(List<InsightEngine.DeadCodeSpan> spans, AvaloniaEdit.Document.TextDocument doc, string? filePath)
    {
        if (_dismissedDiagnostics.Count == 0 || spans.Count == 0) return spans;
        var filtered = new List<InsightEngine.DeadCodeSpan>(spans.Count);
        foreach (var span in spans)
        {
            try
            {
                var line = doc.GetLineByOffset(Math.Clamp(span.StartOffset, 0, doc.TextLength));
                var lineText = doc.GetText(line.Offset, line.Length);
                if (IsDiagnosticDismissed(filePath, lineText, span.Reason))
                    continue;
            }
            catch { }
            filtered.Add(span);
        }
        return filtered;
    }

    private CompletionWindow CreateCompletionWindow()
    {
        var window = new CompletionWindow(EditorTextBox.TextArea)
        {
            MaxHeight = InsightRowHeight * InsightVisibleRows
                + InsightListVerticalPadding + InsightBorderThickness,
            MaxWidth = 480,
            Width = 440,
            MinWidth = 360,
            WindowManagerAddShadowHint = true,
        };

        var panelBrush = CardBrush;
        window.CompletionList.Background = panelBrush;
        window.CompletionList.BorderBrush = SurfaceBorderBrush;
        window.CompletionList.BorderThickness = new Thickness(1);
        window.CompletionList.FontFamily = EditorTextBox.FontFamily;
        window.CompletionList.HorizontalAlignment = HorizontalAlignment.Stretch;

        // Match app cards while keeping the panel compact.
        var panelCornerStyle = new Style(x => x.OfType<CompletionList>().Template().OfType<Border>());
        panelCornerStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(10)));
        panelCornerStyle.Setters.Add(new Setter(Border.BackgroundProperty, panelBrush));
        panelCornerStyle.Setters.Add(new Setter(Border.BoxShadowProperty, new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 8, Blur = 28, Spread = 0, Color = Color.FromArgb(38, 0, 0, 0) },
            new BoxShadow[] { new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 10, Spread = 0, Color = Color.FromArgb(20, 0, 0, 0) } })));
        window.Styles.Add(panelCornerStyle);

        if (window.CompletionList.ListBox is { } listBox)
        {
            listBox.Background = panelBrush;
            listBox.BorderThickness = new Thickness(0);
            listBox.Padding = new Thickness(6, 4);
            listBox.Margin = new Thickness(0);
            listBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            listBox.ClipToBounds = true;
        }

        // Avoid row-transition flashes while the list rebuilds.
        var noTransitionStyle = new Style(x => x.OfType<ListBoxItem>());
        noTransitionStyle.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions()));
        window.Styles.Add(noTransitionStyle);

        // Keep the base row transparent; style hover and selection as chips.
        var baseRowStyle = new Style(x => x.OfType<ListBoxItem>());
        baseRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, Brushes.Transparent));
        baseRowStyle.Setters.Add(new Setter(Avalonia.Controls.ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        baseRowStyle.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0, 1)));
        window.Styles.Add(baseRowStyle);

        // Rounded chip for every row - ensures hover/selected backgrounds are pill-shaped.
        var itemCornerStyle = new Style(x => x.OfType<ListBoxItem>().Template().OfType<Border>());
        itemCornerStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(7)));
        window.Styles.Add(itemCornerStyle);

        var accentTint = AccentBrush.ToImmutable() is ISolidColorBrush accentSolid
            ? new SolidColorBrush(accentSolid.Color, 0.16)
            : AccentBrush;

        var selectedRowStyle = new Style(x => x.OfType<ListBoxItem>().Class(":selected"));
        selectedRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, accentTint));
        window.Styles.Add(selectedRowStyle);

        // Keep selected accent even when pointer is over the selected row.
        var selectedHoverStyle = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pointerover"));
        selectedHoverStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, accentTint));
        window.Styles.Add(selectedHoverStyle);

        var hoverRowStyle = new Style(x => x.OfType<ListBoxItem>().Class(":pointerover"));
        hoverRowStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, ButtonHoverBrush));
        window.Styles.Add(hoverRowStyle);

        var rowPaddingStyle = new Style(x => x.OfType<ListBoxItem>());
        rowPaddingStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(10, 7)));
        rowPaddingStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.MinHeightProperty, 32d));
        window.Styles.Add(rowPaddingStyle);

        window.Closed += (_, _) => _completionWindow = null;
        return window;
    }

    private void MainWindow_EditorKeyIntercept_OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Any editor interaction should hide the diagnostic popup - don't require X click
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
            case Key.Enter when IsSmartSyntaxEnabled() && (e.KeyModifiers & KeyModifiers.Shift) != KeyModifiers.Shift:
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

        if (IsSmartSyntaxEnabled() && MatchesKeybind(e, "ToggleLineComment"))
        {
            ToggleLineComment(doc, textArea, textArea.Selection, caret);
            e.Handled = true;
        }
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
            return;
        }

        var adjustedIndent = StartsWithClosingDelimiter(textAfterCaret)
            ? RemoveOneIndentUnit(indent)
            : indent;

        var newLineText = Environment.NewLine + adjustedIndent + extraIndent;
        doc.Insert(offset, newLineText);
        caret.Offset = offset + newLineText.Length;
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

        doc.Remove(offset - 1, 2);
        SetCaretOffsetSafely(caret, doc, offset - 1);
        return true;
    }

    private void HandleIndent(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
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

        var lines = GetSelectedLines(doc, segment.Offset, segment.EndOffset);
        // Replace the selection in one undoable operation.
        var indentedLines = lines.OrderByDescending(l => l.Offset)
            .Select(l => GetIndentUnit() + doc.GetText(l));
        var newText = string.Join(Environment.NewLine, indentedLines);
        
        // Replace the entire selection segment with indented text as one undo unit
        doc.Replace(segment, newText);
        
        SetCaretOffsetSafely(caret, doc, segment.EndOffset + (GetIndentUnit().Length * lines.Count));
    }

    private void HandleOutdent(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        if (selection is null || selection.IsEmpty)
        {
            var line = doc.GetLineByOffset(caret.Offset);
            var lineText = doc.GetText(line);
            var caretColumnInLine = caret.Offset - line.Offset;
            var removable = GetOutdentLength(lineText, caretColumnInLine);
            if (removable <= 0)
                return;

            // Replace the line in one undoable operation.
            var outdentedText = lineText.TrimStart();
            doc.Replace(line.Offset, line.Length, outdentedText);
            
            SetCaretOffsetSafely(caret, doc, caret.Offset - removable);
            return;
        }

        var segment = selection.SurroundingSegment;
        if (segment is null)
            return;

        var lines = GetSelectedLines(doc, segment.Offset, segment.EndOffset);
        // Replace the selected lines in one undoable operation.
        var linesText = lines.OrderByDescending(l => l.Offset)
            .Select(l => doc.GetText(l).TrimStart());
        var replacedText = string.Join(Environment.NewLine, linesText);
        
        // Replace the entire selection segment with outdented text as one undo unit
        doc.Replace(segment, replacedText);
        
        SetCaretOffsetSafely(caret, doc, Math.Max(segment.Offset, segment.EndOffset - lines.Count));
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

        var insertionText = ReindentPastedText(text, doc, caret.Offset);
        var selection = textArea.Selection;
        if (selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null)
        {
            var segment = selection.SurroundingSegment;
            doc.Replace(segment, insertionText);
            SetCaretOffsetSafely(caret, doc, segment.Offset + insertionText.Length);
            return;
        }

        var safeOffset = Math.Clamp(caret.Offset, 0, doc.TextLength);
        doc.Insert(safeOffset, insertionText);
        SetCaretOffsetSafely(caret, doc, safeOffset + insertionText.Length);
    }

    private void ToggleLineComment(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Editing.Selection? selection, AvaloniaEdit.Editing.Caret caret)
    {
        var lineCommentToken = CurrentLanguageExtension?.CommentLine;
        if (string.IsNullOrWhiteSpace(lineCommentToken))
            return;

        var startOffset = selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null
            ? selection.SurroundingSegment.Offset
            : caret.Offset;
        var endOffset = selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null
            ? selection.SurroundingSegment.EndOffset
            : caret.Offset;

        var lines = GetSelectedLines(doc, startOffset, endOffset);
        if (lines.Count == 0)
            return;

        var shouldUncomment = lines
            .Where(line => !string.IsNullOrWhiteSpace(doc.GetText(line)))
            .All(line =>
            {
                var text = doc.GetText(line);
                var indent = GetLeadingWhitespace(text);
                return text[indent.Length..].StartsWith(lineCommentToken, StringComparison.Ordinal);
            });

        var delta = 0;
        // Each line below is its own Insert/Remove call, which would otherwise force
        // one Undo press per line to fully reverse a single comment-toggle action.
        doc.UndoStack.StartUndoGroup();
        try
        {
            foreach (var line in lines.OrderByDescending(l => l.Offset))
            {
                var text = doc.GetText(line);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var indent = GetLeadingWhitespace(text);
                var commentOffset = line.Offset + indent.Length;
                if (shouldUncomment)
                {
                    if (text[indent.Length..].StartsWith(lineCommentToken, StringComparison.Ordinal))
                    {
                        var removedForLine = lineCommentToken.Length;
                        doc.Remove(commentOffset, lineCommentToken.Length);
                        if (text.Length > indent.Length + lineCommentToken.Length && text[indent.Length + lineCommentToken.Length] == ' ')
                        {
                            doc.Remove(commentOffset, 1);
                            removedForLine++;
                        }

                        delta -= removedForLine;
                    }
                }
                else
                {
                    doc.Insert(commentOffset, lineCommentToken + " ");
                    delta += lineCommentToken.Length + 1;
                }
            }
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        if (selection is not null && !selection.IsEmpty && selection.SurroundingSegment is not null)
        {
            var segment = selection.SurroundingSegment;
            var newEnd = Math.Max(segment.Offset, segment.EndOffset + delta);
            textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, segment.Offset, newEnd);
        }
        else
        {
            SetCaretOffsetSafely(caret, doc, caret.Offset + delta);
        }
    }

    private async void AutoSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (!IsAutoSaveEnabled || !HasFileOpen || !_isDirty) return;
        try
        {
            await SaveAsync(allowPromptForPath: false);
        }
        catch (Exception ex)
        {
            _autoSaveStatusMessage = BuildAutoSaveFailureMessage(ex);
            OnPropertyChanged(nameof(FileSummaryText));
            OnPropertyChanged(nameof(AutoSaveStatusText));
            await ShowWarningDialogAsync("Auto-save", ex);
        }
    }

}