// Licensed under the GNU GPL-v3.0
using AvaloniaEdit.Document;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Kodo.Editor;

internal static class EditorLineTextCache
{
    private const int MaximumCachedLines = 512;
    private static readonly ConditionalWeakTable<TextDocument, DocumentCache> Documents = new();

    public static string GetText(TextDocument document, DocumentLine line) =>
        Documents.GetValue(document, static currentDocument => new DocumentCache(currentDocument)).GetText(document, line);

    private sealed class DocumentCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<DocumentLine, string> _lines = new();
        private readonly List<DocumentLine> _invalidatedLines = new(MaximumCachedLines);
        private ITextSourceVersion? _version;

        public DocumentCache(TextDocument document)
        {
            _version = document.Version;
            document.Changed += OnDocumentChanged;
        }

        private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
        {
            if (sender is not TextDocument document) return;
            var firstOffset = Math.Clamp(e.Offset, 0, document.TextLength);
            var lastOffset = (int)Math.Clamp((long)firstOffset + e.InsertedText.Text.Length, firstOffset, document.TextLength);
            var firstLine = document.GetLineByOffset(firstOffset).LineNumber;
            var lastLine = document.GetLineByOffset(lastOffset).LineNumber;
            var firstAffected = Math.Max(1, firstLine - 1);
            var lastAffected = Math.Min(document.LineCount, lastLine + 1);

            lock (_gate)
            {
                _invalidatedLines.Clear();
                foreach (var line in _lines.Keys)
                    if (line.IsDeleted || line.LineNumber == 0 ||
                        line.LineNumber >= firstAffected && line.LineNumber <= lastAffected)
                        _invalidatedLines.Add(line);
                foreach (var line in _invalidatedLines)
                    _lines.Remove(line);
                _version = document.Version;
            }
        }

        public string GetText(TextDocument document, DocumentLine line)
        {
            lock (_gate)
            {
                var version = document.Version;
                if (!ReferenceEquals(_version, version))
                {
                    _lines.Clear();
                    _invalidatedLines.Clear();
                    _version = version;
                }

                if (_lines.TryGetValue(line, out var text)) return text;
                text = document.GetText(line.Offset, line.Length);
                if (_lines.Count >= MaximumCachedLines)
                {
                    var oldest = _lines.Keys.GetEnumerator();
                    if (oldest.MoveNext()) _lines.Remove(oldest.Current);
                }
                _lines[line] = text;
                return text;
            }
        }
    }
}
