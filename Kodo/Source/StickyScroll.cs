// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Kodo;

internal sealed class StickyScope
{
    public StickyScope(int startLine0, int endLine0)
    {
        StartLine0 = startLine0;
        EndLine0 = endLine0;
    }

    public int StartLine0 { get; }
    public int EndLine0 { get; }
}

public partial class MainWindow
{
    private static readonly HashSet<int> StickyScopeLspKinds = new() { 2, 3, 4, 5, 6, 9, 10, 11, 12, 23 };
    private const int StickyScrollMaxLinesLimit = 5;
    private const int StickyScrollMaxSyntaxAnalyzerBytes = 4 * 1024 * 1024;
    private const int StickyScrollMaxPrefixCloneBytes = 2 * 1024 * 1024;
    private const double StickyScrollFallbackLineHeight = 22;

    private readonly DispatcherTimer _stickyScopeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _isStickyScrollEnabled = true;
    private int _stickyScrollMaxLines = StickyScrollMaxLinesLimit;
    private StickyScrollPane? _primaryStickyPane;
    private StickyScrollPane? _secondaryStickyPane;

    private sealed class StickyScrollPane
    {
        public StickyScrollPane(TextEditor editor, IBrush background, IBrush borderBrush)
        {
            Editor = editor;
            Lines = new StackPanel();
            Root = new Border
            {
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsVisible = false,
                ClipToBounds = true,
                Child = Lines,
                Focusable = false,
            };
        }

        public TextEditor Editor { get; }
        public Border Root { get; }
        public StackPanel Lines { get; }
        public List<StickyScope> Scopes { get; set; } = new();
        public int ReloadVersion { get; set; }
        public bool Stale { get; set; } = true;
        public string RowSignature { get; set; } = string.Empty;
        public ScrollViewer? EditorScrollViewer { get; set; }
        internal List<StickyRowHandle> RowHandles { get; } = new();

        internal sealed class StickyRowHandle
        {
            public ScrollViewer? ScrollViewer { get; set; }
        }
    }

    private StickyScrollPane? GetStickyScrollPane(TextEditor editor) =>
        ReferenceEquals(editor, SecondaryEditorTextBox) ? _secondaryStickyPane : _primaryStickyPane;

    private void SetupStickyScrollPanes()
    {
        _stickyScopeRefreshTimer.Tick += (_, _) =>
        {
            _stickyScopeRefreshTimer.Stop();
            _ = ReloadStickyScopesAsync();
        };
        if (PrimaryEditorTextBox is not null)
            SetupStickyScrollPane(PrimaryEditorPane, PrimaryEditorTextBox, secondary: false);
        if (SecondaryEditorTextBox is not null)
            SetupStickyScrollPane(null, SecondaryEditorTextBox, secondary: true);
    }

    private void SetupStickyScrollPane(Control? directHost, TextEditor editor, bool secondary)
    {
        var sticky = new StickyScrollPane(editor, EditorBackgroundBrush, SurfaceBorderBrush);
        Grid? host = null;
        if (directHost is Grid typedHost)
            host = typedHost;
        else
        {
            foreach (var ancestor in editor.GetLogicalAncestors())
            {
                if (ancestor is Grid grid && grid.Children.Contains(editor))
                {
                    host = grid;
                    break;
                }
            }
        }
        if (host is null) return;
        Grid.SetRow(sticky.Root, Grid.GetRow(editor));
        Grid.SetColumn(sticky.Root, Grid.GetColumn(editor));
        sticky.Root.ZIndex = 20;
        host.Children.Add(sticky.Root);
        if (secondary) _secondaryStickyPane = sticky;
        else _primaryStickyPane = sticky;
    }

    private double StickyScrollLineHeight(TextView textView)
    {
        try
        {
            var height = textView.DefaultLineHeight;
            if (height > 4 && height < 200) return height;
        }
        catch
        {
        }
        return StickyScrollFallbackLineHeight;
    }

    private static int CompareStickyScopes(StickyScope left, StickyScope right)
    {
        var byStart = left.StartLine0.CompareTo(right.StartLine0);
        return byStart != 0 ? byStart : (right.EndLine0 - right.StartLine0).CompareTo(left.EndLine0 - left.StartLine0);
    }

    private void UpdateStickyScrollForPane(StickyScrollPane? sticky)
    {
        if (sticky is null) return;
        var editor = sticky.Editor;
        var document = editor.Document;
        var textView = editor.TextArea.TextView;
        if (!_isStickyScrollEnabled || !editor.IsVisible || document is null || !textView.VisualLinesValid ||
            textView.VisualLines.Count == 0)
        {
            sticky.Root.IsVisible = false;
            sticky.RowSignature = string.Empty;
            return;
        }

        var topLine0 = textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
        var candidates = new List<StickyScope>();
        foreach (var scope in sticky.Scopes)
        {
            if (scope.StartLine0 >= topLine0 || scope.EndLine0 < topLine0) continue;
            candidates.Add(scope);
        }
        if (topLine0 <= 0 || candidates.Count == 0)
        {
            sticky.Root.IsVisible = false;
            sticky.RowSignature = string.Empty;
            return;
        }

        candidates.Sort(CompareStickyScopes);
        var kept = new List<StickyScope>();
        var seen = new HashSet<int>();
        foreach (var scope in candidates)
        {
            if (!seen.Add(scope.StartLine0)) continue;
            kept.Add(scope);
            if (kept.Count >= _stickyScrollMaxLines) break;
        }

        var lineHeight = StickyScrollLineHeight(textView);
        sticky.Root.MaxHeight = lineHeight * Math.Max(1, _stickyScrollMaxLines);
        var keptTexts = new string[kept.Count];
        for (var i = 0; i < kept.Count; i++)
        {
            var line = document.GetLineByNumber(kept[i].StartLine0 + 1);
            keptTexts[i] = document.GetText(line).Trim();
        }
        var pane = ReferenceEquals(editor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane;
        var pathText = pane.ActiveTab?.Path;
        var useSyntaxPipeline = document.TextLength <= StickyScrollMaxPrefixCloneBytes;
        var editorBackgroundFingerprint = ((EditorBackgroundBrush as ISolidColorBrush)?.Color ?? Color.Parse(KodoDesignTokens.EditorBackground)).ToString();
        var signature = topLine0 + "|" + string.Join(",", kept.Select((scope, i) => $"{scope.StartLine0}:{keptTexts[i]}"))
            + "|" + (useSyntaxPipeline ? 1 : 0) + "|" + (string.IsNullOrWhiteSpace(pathText) ? "" : pathText)
            + "|" + editorBackgroundFingerprint;
        if (string.Equals(sticky.RowSignature, signature, StringComparison.Ordinal))
        {
            ApplyStickyRowOffsets(sticky);
            sticky.Root.IsVisible = sticky.Lines.Children.Count > 0;
            return;
        }
        sticky.RowSignature = signature;
        sticky.Lines.Children.Clear();
        sticky.RowHandles.Clear();
        var gutterWidth = ComputeStickyGutterWidth(editor);
        var extension = ResolveStickySyntaxExtension(pane);
        var isMarkdown = KodoExtensionIds.IsMarkdown(extension?.Id);
        for (var depth = 0; depth < kept.Count; depth++)
        {
            var scope = kept[depth];
            var target = scope.StartLine0;
            var row = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Height = lineHeight,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false,
            };
            ToolTip.SetTip(row, $"Line {target + 1}");
            if (useSyntaxPipeline)
                sticky.RowHandles.Add(BuildSyntaxStickyRow(row, editor, document, extension, isMarkdown,
                    depth, keptTexts[depth], target, gutterWidth, lineHeight));
            else
            {
                BuildPlainStickyRow(row, editor, keptTexts[depth], depth, target, lineHeight);
                sticky.RowHandles.Add(new StickyScrollPane.StickyRowHandle());
            }
            sticky.Lines.Children.Add(row);
        }
        sticky.Root.IsVisible = sticky.Lines.Children.Count > 0;
    }

    private LoadedExtension? ResolveStickySyntaxExtension(EditorPaneState pane)
    {
        var path = pane.ActiveTab?.Path;
        if (string.IsNullOrWhiteSpace(path))
            return CurrentLanguageExtension;
        return GetLanguageExtension(path);
    }

    private static double ComputeStickyGutterWidth(TextEditor editor)
    {
        var total = 0d;
        foreach (var margin in editor.TextArea.LeftMargins)
            total += margin.Bounds.Width;
        return total;
    }

    private Control CreateStickyLineNumberMargin(TextEditor mainEditor, int targetLine0, double width)
    {
        return new Border
        {
            Width = width,
            Background = Brushes.Transparent,
            Focusable = false,
            Child = new TextBlock
            {
                Text = (targetLine0 + 1).ToString(),
                FontFamily = mainEditor.FontFamily,
                FontSize = mainEditor.FontSize,
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
                IsHitTestVisible = false,
            },
        };
    }

    private StickyScrollPane.StickyRowHandle BuildSyntaxStickyRow(
        Border row,
        TextEditor mainEditor,
        TextDocument document,
        LoadedExtension? extension,
        bool isMarkdown,
        int depth,
        string text,
        int targetLine0,
        double gutterWidth,
        double lineHeight)
    {
        var line = document.GetLineByNumber(targetLine0 + 1);
        var end = Math.Min(document.TextLength, line.EndOffset);
        var prefixText = document.GetText(0, end);
        var rowScrollViewer = default(ScrollViewer);
        var handle = new StickyScrollPane.StickyRowHandle();
        var clone = new TextEditor
        {
            Text = prefixText,
            IsReadOnly = true,
            WordWrap = false,
            ShowLineNumbers = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            Background = new SolidColorBrush((EditorBackgroundBrush as ISolidColorBrush)?.Color ?? Color.Parse(KodoDesignTokens.EditorBackground)),
            Padding = new Thickness(8, 0, 18, 0),
            Focusable = false,
            SyntaxHighlighting = mainEditor.SyntaxHighlighting,
            Height = lineHeight,
        };
        clone.FontFamily = mainEditor.FontFamily;
        clone.FontSize = mainEditor.FontSize;
        clone.Foreground = mainEditor.Foreground;
        clone.Cursor = new Cursor(StandardCursorType.Hand);
        clone.TextArea.Cursor = new Cursor(StandardCursorType.Hand);
        clone.ContextMenu = null;
        clone.TextArea.TextView.Options.AllowScrollBelowDocument = false;
        clone.TextArea.TextView.CurrentLineBackground = Brushes.Transparent;
        clone.TextArea.TextView.CurrentLineBorder = null;
        clone.TextArea.Caret.Hide();
        if (gutterWidth > 0)
            clone.TextArea.LeftMargins.Add(CreateStickyLineNumberMargin(mainEditor, targetLine0, gutterWidth));
        ConfigureStickyRowHighlighting(clone, extension, isMarkdown);

        var positionPending = true;
        clone.LayoutUpdated += (_, _) =>
        {
            if (!positionPending) return;
            var view = clone.TextArea.TextView;
            var totalHeight = view.DocumentHeight;
            if (totalHeight <= 0) return;
            if (rowScrollViewer is null)
                foreach (var descendant in clone.GetVisualDescendants())
                    if (descendant is ScrollViewer found) { rowScrollViewer = found; handle.ScrollViewer = found; break; }
            if (rowScrollViewer is null) return;
            var horizontalOffset = GetEditorScrollViewer(mainEditor)?.Offset.X ?? 0d;
            var verticalOffset = Math.Max(0, totalHeight - lineHeight);
            positionPending = false;
            var target = new Vector(horizontalOffset, verticalOffset);
            if (rowScrollViewer.Offset != target)
                rowScrollViewer.Offset = target;
        };

        row.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            e.Handled = true;
            NavigateStickyScrollLine(mainEditor, targetLine0);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        row.PointerEntered += (_, _) => clone.Background = ButtonBrush;
        row.PointerExited += (_, _) =>
            clone.Background = new SolidColorBrush((EditorBackgroundBrush as ISolidColorBrush)?.Color ?? Color.Parse(KodoDesignTokens.EditorBackground));
        row.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            EditorTextView_OnPointerWheelChanged(mainEditor.TextArea.TextView, e);
        };
        row.Child = clone;
        return handle;
    }

    private void BuildPlainStickyRow(
        Border row,
        TextEditor mainEditor,
        string text,
        int depth,
        int targetLine0,
        double lineHeight)
    {
        if (text.Length > 240) text = text[..240] + "…";
        var number = new TextBlock
        {
            Text = (targetLine0 + 1).ToString(),
            FontFamily = mainEditor.FontFamily,
            FontSize = mainEditor.FontSize,
            Foreground = MutedTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false,
        };
        var label = new TextBlock
        {
            Text = text,
            FontFamily = KodoFonts.MonoFamily,
            FontSize = KodoDesignTokens.BodyLargeFontSize,
            Foreground = PrimaryTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(depth * 14, 0, 0, 0),
            IsHitTestVisible = false,
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { number, label },
        };
        row.Child = panel;
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            NavigateStickyScrollLine(mainEditor, targetLine0);
        };
        row.PointerEntered += (_, _) => row.Background = ButtonBrush;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
    }

    private void ConfigureStickyRowHighlighting(TextEditor clone, LoadedExtension? extension, bool isMarkdown)
    {
        var rainbow = new RainbowBracketColorizer();
        rainbow.UpdateSyntax(isMarkdown ? null : extension);
        clone.TextArea.TextView.LineTransformers.Add(rainbow);
        var interpolated = new InterpolatedStringColorizer();
        interpolated.UpdateSyntax(extension is null ? null : ResolveCompiledSyntaxProfile(extension));
        clone.TextArea.TextView.LineTransformers.Add(interpolated);
        var htmlEmbedded = new HtmlEmbeddedColorizer();
        htmlEmbedded.UpdateSyntax(extension, ResolveHtmlEmbeddedSyntaxProfile);
        clone.TextArea.TextView.LineTransformers.Add(htmlEmbedded);
        var markdown = new MarkdownColorizer();
        markdown.UpdateSyntax(extension, ResolveFenceLanguageSyntaxProfile, ResolveInlineCodeLanguageExtension);
        clone.TextArea.TextView.LineTransformers.Add(markdown);
        clone.TextArea.TextView.LineTransformers.Add(new EmojiTypefaceColorizer());
        clone.TextArea.TextView.LineTransformers.Add(new DeadCodeTextBrightener());
        clone.TextArea.TextView.LineTransformers.Add(new ErrorTextDarkener());
    }

    private ScrollViewer? GetEditorScrollViewer(TextEditor editor)
    {
        var pane = ReferenceEquals(editor, SecondaryEditorTextBox) ? _secondaryStickyPane : _primaryStickyPane;
        if (pane?.EditorScrollViewer is { } cached) return cached;
        foreach (var descendant in editor.GetVisualDescendants())
        {
            if (descendant is ScrollViewer found)
            {
                if (pane is not null) pane.EditorScrollViewer = found;
                return found;
            }
        }
        return null;
    }

    private void ApplyStickyRowOffsets(StickyScrollPane sticky)
    {
        var horizontalOffset = GetEditorScrollViewer(sticky.Editor)?.Offset.X ?? 0d;
        foreach (var handle in sticky.RowHandles)
            if (handle.ScrollViewer is { } sv && sv.Offset.X != horizontalOffset)
                sv.Offset = new Vector(horizontalOffset, sv.Offset.Y);
    }

    private void NavigateStickyScrollLine(TextEditor editor, int line0)
    {
        var document = editor.Document;
        if (document is null) return;
        var line = document.GetLineByNumber(Math.Clamp(line0 + 1, 1, document.LineCount));
        var textArea = editor.TextArea;
        textArea.ClearSelection();
        var offset = line.Offset;
        while (offset < line.EndOffset && offset < document.TextLength)
        {
            var ch = document.GetCharAt(offset);
            if (ch is not (' ' or '\t')) break;
            offset++;
        }
        SetCaretOffsetSafely(textArea.Caret, document, offset);
        editor.Focus();
    }

    private void InvalidateStickyScopes()
    {
        if (_primaryStickyPane is not null) _primaryStickyPane.Stale = true;
        if (_secondaryStickyPane is not null) _secondaryStickyPane.Stale = true;
        _stickyScopeRefreshTimer.Stop();
        _stickyScopeRefreshTimer.Start();
    }

    private async Task ReloadStickyScopesAsync()
    {
        await ReloadStickyScopeAsync(_primaryStickyPane);
        await ReloadStickyScopeAsync(_secondaryStickyPane);
    }

    private async Task ReloadStickyScopeAsync(StickyScrollPane? sticky)
    {
        if (sticky is null) return;
        sticky.ReloadVersion++;
        var version = sticky.ReloadVersion;
        var editor = sticky.Editor;
        var pane = ReferenceEquals(editor, SecondaryEditorTextBox) ? _secondaryEditorPane : _editorPane;
        var tab = pane.ActiveTab;
        var document = editor.Document;
        if (!_isStickyScrollEnabled || tab is null || document is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != sticky.ReloadVersion) return;
                sticky.Scopes = new();
                UpdateStickyScrollForPane(sticky);
            });
            return;
        }

        string text;
        try { text = document.Text; } catch { return; }
        var path = tab.Path;
        List<StickyScope>? scopes = null;
        LangRulesAdapter? rules = null;
        if (!string.IsNullOrWhiteSpace(path)) rules = GetLanguageExtension(path)?.LangRules;
        else if (CurrentLanguageExtension is not null) rules = CurrentLanguageExtension.LangRules;
        try
        {
            if (rules is { HasSymbolAnalyzer: true } && text.Length <= StickyScrollMaxSyntaxAnalyzerBytes)
            {
                scopes = await Task.Run(() => ComputeSyntaxStickyScopes(text, rules)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Sticky scroll syntax analysis failed.", ex);
        }
        if (scopes is null && !tab.IsUntitled && !string.IsNullOrWhiteSpace(path) && LspEnabled)
            scopes = await LoadLspStickyScopesAsync(path, text, document.LineCount).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != sticky.ReloadVersion) return;
            sticky.Scopes = scopes ?? new List<StickyScope>();
            sticky.Stale = false;
            UpdateStickyScrollForPane(sticky);
        });
    }

    private static List<StickyScope> ComputeSyntaxStickyScopes(string text, LangRulesAdapter rules)
    {
        var lineStarts = BuildStickyLineStarts(text);
        var lineCount = lineStarts.Length;
        var masked = MaskStickyScrollText(text);
        var scopes = new List<StickyScope>();
        foreach (var symbol in rules.AnalyzeSymbolsUncapped(text))
        {
            if (symbol is null || !symbol.IsDeclaration || string.IsNullOrWhiteSpace(symbol.Name)) continue;
            if (!IsStickyScopeKind(symbol.Kind)) continue;
            var offset = Math.Clamp(symbol.Start, 0, text.Length);
            var startLine0 = FindStickyLineIndex(lineStarts, offset);
            var startLineEnd = startLine0 + 1 < lineStarts.Length ? lineStarts[startLine0 + 1] : text.Length;
            var windowEnd = Math.Min(masked.Length, Math.Max(startLineEnd, offset + 1) + 500);
            int? endLine0 = null;
            var openBrace = offset < masked.Length ? masked.IndexOf('{', offset, Math.Min(windowEnd, masked.Length) - offset) : -1;
            if (openBrace >= 0) endLine0 = FindScopeBraceEndLine(masked, openBrace, lineStarts);
            endLine0 ??= FindIndentedScopeEndLine(text, lineStarts, startLine0);
            if (endLine0 < startLine0 || endLine0 > lineCount - 1) endLine0 = startLine0;
            scopes.Add(new StickyScope(startLine0, endLine0.Value));
        }
        return DedupeStickyScopes(scopes);
    }

    private static List<StickyScope> DedupeStickyScopes(IEnumerable<StickyScope> scopes) => scopes
        .GroupBy(scope => scope.StartLine0)
        .Select(group => group.OrderByDescending(scope => scope.EndLine0 - scope.StartLine0).First())
        .OrderBy(scope => scope.StartLine0)
        .ThenByDescending(scope => scope.EndLine0 - scope.StartLine0)
        .ToList();

    private static bool IsStickyScopeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return true;
        foreach (var token in new[] { "class", "struct", "record", "interface", "enum", "namespace", "module",
                                       "function", "method", "constructor", "trait", "impl", "procedure",
                                       "routine", "rule", "expando", "protocol" })
            if (kind.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<List<StickyScope>?> LoadLspStickyScopesAsync(string path, string text, int lineCount)
    {
        JsonElement? result;
        try { result = await GetLspDocumentSymbolsAsync(path).ConfigureAwait(false); }
        catch { return null; }
        if (result is not { ValueKind: JsonValueKind.Array } array) return null;
        var scopes = new List<StickyScope>();
        CollectLspStickyScopes(array, lineCount, scopes);
        return DedupeStickyScopes(scopes);
    }

    private void CollectLspStickyScopes(JsonElement items, int lineCount, List<StickyScope> scopes)
    {
        foreach (var symbol in items.EnumerateArray())
        {
            if (symbol.ValueKind != JsonValueKind.Object) continue;
            var kind = symbol.TryGetProperty("kind", out var kindElement) &&
                       kindElement.ValueKind == JsonValueKind.Number &&
                       kindElement.TryGetInt32(out var value) ? value : 0;
            if (symbol.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Object &&
                range.TryGetProperty("start", out var rangeStart) && rangeStart.ValueKind == JsonValueKind.Object &&
                range.TryGetProperty("end", out var rangeEnd) && rangeEnd.ValueKind == JsonValueKind.Object &&
                TryReadLspPosition(rangeStart, out var rangeStartLine, out _) &&
                TryReadLspPosition(rangeEnd, out var rangeEndLine, out _) &&
                StickyScopeLspKinds.Contains(kind))
            {
                var displayLine0 = rangeStartLine;
                if (symbol.TryGetProperty("selectionRange", out var selection) && selection.ValueKind == JsonValueKind.Object &&
                    selection.TryGetProperty("start", out var selectionStart) && selectionStart.ValueKind == JsonValueKind.Object &&
                    TryReadLspPosition(selectionStart, out var selectionLine, out _))
                    displayLine0 = selectionLine;
                var safeEnd = Math.Clamp(rangeEndLine, 0, Math.Max(0, lineCount - 1));
                var safeStart = Math.Clamp(displayLine0, 0, Math.Max(0, lineCount - 1));
                if (safeEnd >= safeStart)
                    scopes.Add(new StickyScope(safeStart, safeEnd));
            }
            if (symbol.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                CollectLspStickyScopes(children, lineCount, scopes);
        }
    }

    private static int[] BuildStickyLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    private static int FindStickyLineIndex(int[] lineStarts, int offset)
    {
        if (offset <= 0 || lineStarts.Length == 0) return 0;
        var low = 0;
        var high = lineStarts.Length - 1;
        var answer = 0;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (lineStarts[mid] <= offset)
            {
                answer = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return answer;
    }

    private static string LineStickyText(string text, int[] lineStarts, int line0)
    {
        var start = lineStarts[line0];
        var end = line0 + 1 < lineStarts.Length ? lineStarts[line0 + 1] : text.Length;
        return text[start..end];
    }

    private static int GetStickyLineIndent(string line)
    {
        var indent = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') indent++;
            else if (ch == '\t') indent += 4;
            else break;
        }
        return indent;
    }

    private static int FindIndentedScopeEndLine(string text, int[] lineStarts, int startLine0)
    {
        var lineCount = lineStarts.Length;
        var startLine = LineStickyText(text, lineStarts, startLine0);
        var startIndent = GetStickyLineIndent(startLine);
        var last = startLine0;
        for (var i = startLine0 + 1; i < lineCount; i++)
        {
            var raw = LineStickyText(text, lineStarts, i);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (GetStickyLineIndent(raw) > startIndent) last = i;
            else break;
        }
        return last;
    }

    private static int? FindScopeBraceEndLine(string masked, int openBraceOffset, int[] lineStarts)
    {
        var depth = 0;
        for (var i = openBraceOffset; i < masked.Length; i++)
        {
            var ch = masked[i];
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return FindStickyLineIndex(lineStarts, i);
            }
        }
        return null;
    }

    private static string MaskStickyScrollText(string text)
    {
        var chars = text.ToCharArray();
        var length = chars.Length;
        var i = 0;
        while (i < length)
        {
            var current = chars[i];
            if (current == '/' && i + 1 < length && chars[i + 1] == '/')
            {
                var end = i;
                while (end < length && chars[end] != '\n') end++;
                for (var k = i; k < end; k++) chars[k] = ' ';
                i = end;
                continue;
            }
            if (current == '/' && i + 1 < length && chars[i + 1] == '*')
            {
                var end = i + 2;
                var depth = 1;
                while (end < length && depth > 0)
                {
                    if (end + 1 < length && chars[end] == '*' && chars[end + 1] == '/') { depth--; end += 2; continue; }
                    end++;
                }
                for (var k = i; k < end; k++) chars[k] = ' ';
                i = end;
                continue;
            }
            if (current == '#')
            {
                var end = i;
                while (end < length && chars[end] != '\n') end++;
                for (var k = i; k < end; k++) chars[k] = ' ';
                i = end;
                continue;
            }
            if (current == '"' || current == '\'' || current == '`')
            {
                var quote = current;
                var end = i + 1;
                while (end < length)
                {
                    var ch = chars[end];
                    if (ch == '\\' && end + 1 < length) { end += 2; continue; }
                    if (ch == quote) { end++; break; }
                    if (ch == '\n' && quote != '`') { break; }
                    end++;
                }
                for (var k = i; k < Math.Min(end, length); k++)
                    if (chars[k] != '\n') chars[k] = ' ';
                i = Math.Min(end, length);
                continue;
            }
            i++;
        }
        return new string(chars);
    }
}





