// Licensed under the GNU GPL-v3.0
// Built-in replacement for the "TagAutoClose" language plugin.
//
// The HTML, XML, XAML, and CSS language .kox packages ship a compiled
// languagePlugin "TagAutoClose.dll" whose OnLoad self-posts a full
// visual-tree walk back onto the dispatcher at Background priority until it
// finds a TextEditor named "EditorTextBox". That editor name no longer
// exists (the editors are "PrimaryEditorTextBox"/"SecondaryEditorTextBox"
// since the split-editor work), so the match never succeeds, the posting
// never stops, and with extensions installed the UI thread burns ~85% of a
// core while idling (benchmarks/README.md, finding #6).
//
// KodoPlugins.LoadLanguagePlugin substitutes this built-in implementation
// whenever an extension declares TagAutoClose.dll, so the buggy external
// DLL is never loaded and upstream package refreshes cannot reintroduce
// the spin. The auto-close behavior below is a faithful reconstruction of
// the external plugin (decompiled and verified identical across v2.0.0 and
// v2.1.0) with the hooking replaced by a single visual-tree pass plus a
// short, spaced, bounded catch-up timer - never a self-reposting walk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia.VisualTree;
using Kodo;
using Kodo.Models;

namespace Kodo;

public sealed class InternalTagAutoClosePlugin : IKodoPlugin
{
    /// <summary>Language-plugin file name whose load triggers this built-in.</summary>
    public const string ExternalFileName = "TagAutoClose.dll";

    private const string PrimaryEditorName = "PrimaryEditorTextBox";
    private const string SecondaryEditorName = "SecondaryEditorTextBox";
    private const int HookCatchUpTicks = 60;   // 60 × 500 ms = min(30 s) of catch-up, then give up

    private static readonly HashSet<string> HtmlVoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr", "!doctype", "!doctype"
    };

    private MainWindow? _window;
    private LoadedExtension? _extension;
    private TextEditor? _primary;
    private TextEditor? _secondary;
    private bool _isHandling;
    private DispatcherTimer? _catchUpTimer;
    private int _catchUpTicks;

    public void OnLoad(MainWindow window, LoadedExtension extension)
    {
        _window = window;
        _extension = extension;
        HookEditors();
    }

    public void OnUnload()
    {
        StopCatchUpTimer();
        UnhookEditor(ref _primary);
        UnhookEditor(ref _secondary);
        _window = null;
        _extension = null;
    }

    // ── Hooking ──────────────────────────────────────────────────────────

    private void HookEditors()
    {
        if (_window is null) return;

        // The editor controls are declared statically in MainWindow.axaml, so
        // one tree walk normally suffices. If content has not been attached
        // yet (the extension scan can run before the window lays out), a
        // bounded 500 ms catch-up timer finishes the job and stops.
        _primary = FindEditor(_window, PrimaryEditorName);
        _secondary = FindEditor(_window, SecondaryEditorName);
        if (_primary is not null || _secondary is not null)
        {
            Attach(_primary);
            Attach(_secondary);
            StopCatchUpTimer();
            return;
        }

        if (_catchUpTimer is not null) return;
        _catchUpTicks = 0;
        _catchUpTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _catchUpTimer.Tick += CatchUpTimer_OnTick;
        _catchUpTimer.Start();
    }

    private void CatchUpTimer_OnTick(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            StopCatchUpTimer();
            return;
        }

        _primary = FindEditor(_window, PrimaryEditorName);
        _secondary = FindEditor(_window, SecondaryEditorName);
        if (_primary is not null || _secondary is not null)
        {
            Attach(_primary);
            Attach(_secondary);
            StopCatchUpTimer();
            return;
        }

        _catchUpTicks++;
        if (_catchUpTicks >= HookCatchUpTicks)
        {
            KodoDiagnostics.LogDebug(
                $"TagAutoClose: no editor found after {HookCatchUpTicks} catch-up ticks; stopping hook attempts.");
            StopCatchUpTimer();
        }
    }

    private void StopCatchUpTimer()
    {
        _catchUpTimer?.Stop();
        _catchUpTimer = null;
        _catchUpTicks = 0;
    }

    private void Attach(TextEditor? editor)
    {
        if (editor is null || editor.TextArea is null) return;
        editor.TextArea.TextEntered -= OnTextEntered;   // idempotent by design
        editor.TextArea.TextEntered += OnTextEntered;
    }

    private void UnhookEditor(ref TextEditor? editor)
    {
        if (editor is null) return;
        try
        {
            editor.TextArea.TextEntered -= OnTextEntered;
        }
        catch { }
        editor = null;
    }

    private static TextEditor? FindEditor(Visual root, string editorName)
    {
        try
        {
            foreach (var descendant in root.GetVisualDescendants())
            {
                if (descendant is TextEditor editor &&
                    string.Equals(editor.Name, editorName, StringComparison.Ordinal))
                    return editor;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ── Auto-close behavior (faithful to the external plugin) ────────────

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_isHandling || string.IsNullOrEmpty(e.Text) || e.Text.Length != 1 || e.Text[0] != '>')
            return;

        var window = _window;
        if (window is null) return;
        if (sender is not TextArea textArea || textArea.Document is null) return;
        var document = textArea.Document;

        var extension = Path.GetExtension(window.ActiveEditorTab?.Path ?? string.Empty);
        if (!string.IsNullOrEmpty(extension))
        {
            var insightBlacklist = (window.InsightBlacklistExtensions ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.StartsWith(".") ? s : "." + s)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (insightBlacklist.Contains(extension))
                return;
        }

        var current = window.CurrentLanguageExtension;
        if (current is null)
            return;

        var currentId = current.Id ?? string.Empty;
        if (!currentId.Equals("HTML-kodo-extension", StringComparison.OrdinalIgnoreCase) &&
            !currentId.Equals("xaml-kodo-extension", StringComparison.OrdinalIgnoreCase) &&
            !currentId.Equals("xml-kodo-extension", StringComparison.OrdinalIgnoreCase) &&
            !current.Extensions.Any(ex => ex.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                                          ex.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
                                          ex.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
                                          ex.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ||
                                          ex.Equals(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var caret = textArea.Caret;
        var offset = caret.Offset;
        if (offset == 0 || offset > document.TextLength || offset - 1 < 0 ||
            offset - 1 >= document.TextLength || document.GetCharAt(offset - 1) != '>' ||
            (offset < document.TextLength && document.TextLength - offset >= 2 &&
             document.GetText(offset, Math.Min(2, document.TextLength - offset)).StartsWith("</", StringComparison.Ordinal)))
        {
            return;
        }

        var tagName = ExtractTagNameBefore(document, offset - 1);
        if (string.IsNullOrEmpty(tagName) ||
            ((currentId.Equals("HTML-kodo-extension", StringComparison.OrdinalIgnoreCase) ||
              current.Extensions.Any(ex => ex.Equals(".html", StringComparison.OrdinalIgnoreCase))) &&
             HtmlVoidElements.Contains(tagName)))
        {
            return;
        }

        if (_extension?.Blacklist is { Length: > 0 } pluginBlacklist &&
            pluginBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            return;

        if (current.Blacklist is { Length: > 0 } languageBlacklist &&
            languageBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            return;

        if (IsSelfClosingTag(document, offset - 1) || IsClosingTag(document, offset - 1))
            return;

        var closingTag = "</" + tagName + ">";
        try
        {
            _isHandling = true;
            var insertOffset = caret.Offset;
            document.Insert(insertOffset, closingTag);
            caret.Offset = insertOffset;
            caret.BringCaretToView();
        }
        finally
        {
            _isHandling = false;
        }
    }

    private static string? ExtractTagNameBefore(TextDocument document, int greaterThanOffset)
    {
        var tagOpenIndex = -1;
        for (var scan = greaterThanOffset; scan >= 0; scan--)
        {
            var ch = document.GetCharAt(scan);
            if (ch == '<') { tagOpenIndex = scan; break; }
            if (ch == '>') break;
        }
        if (tagOpenIndex < 0) return null;

        var tagText = document.GetText(tagOpenIndex, greaterThanOffset - tagOpenIndex + 1);
        if (tagText.Contains("!--", StringComparison.Ordinal) ||
            tagText.StartsWith("<?", StringComparison.Ordinal) ||
            tagText.StartsWith("<!", StringComparison.Ordinal))
            return null;

        int i;
        for (i = tagOpenIndex + 1; i <= greaterThanOffset && char.IsWhiteSpace(document.GetCharAt(i)); i++) { }
        if (i <= greaterThanOffset && document.GetCharAt(i) == '/') return null;

        var nameStart = i;
        for (; i <= greaterThanOffset && (char.IsLetterOrDigit(document.GetCharAt(i)) ||
                                          document.GetCharAt(i) == '-' || document.GetCharAt(i) == '_' ||
                                          document.GetCharAt(i) == ':' || document.GetCharAt(i) == '.'); i++) { }

        if (i <= nameStart) return null;

        var tagName = document.GetText(nameStart, i - nameStart);
        return string.IsNullOrWhiteSpace(tagName) ? null : tagName;
    }

    private static bool IsSelfClosingTag(TextDocument document, int greaterThanOffset)
    {
        var i = greaterThanOffset - 1;
        while (i >= 0 && char.IsWhiteSpace(document.GetCharAt(i)))
            i--;
        return i >= 0 && document.GetCharAt(i) == '/';
    }

    private static bool IsClosingTag(TextDocument document, int greaterThanOffset)
    {
        var tagOpenIndex = -1;
        for (var scan = greaterThanOffset; scan >= 0; scan--)
        {
            var ch = document.GetCharAt(scan);
            if (ch == '<') { tagOpenIndex = scan; break; }
            if (ch == '>') break;
        }
        if (tagOpenIndex < 0) return false;

        int i;
        for (i = tagOpenIndex + 1; i <= greaterThanOffset && char.IsWhiteSpace(document.GetCharAt(i)); i++) { }
        return i <= greaterThanOffset && document.GetCharAt(i) == '/';
    }
}
