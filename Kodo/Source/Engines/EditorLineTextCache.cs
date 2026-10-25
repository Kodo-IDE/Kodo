// Licensed under the GNU GPL-v3.0
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AvaloniaEdit.Document;

namespace Kodo.Editor;

internal static class EditorLineTextCache
{
    private const int MaximumCachedLines = 512;
    private static readonly ConditionalWeakTable<TextDocument, DocumentCache> Documents = new();

    public static string GetText(TextDocument document, DocumentLine line) =>
        Documents.GetValue(document, static _ => new DocumentCache()).GetText(document, line);

    private sealed class DocumentCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<DocumentLine, string> _lines = new();
        private ITextSourceVersion? _version;

        public string GetText(TextDocument document, DocumentLine line)
        {
            lock (_gate)
            {
                var version = document.Version;
                if (!ReferenceEquals(_version, version))
                {
                    _lines.Clear();
                    _version = version;
                }

                if (_lines.TryGetValue(line, out var text)) return text;
                text = document.GetText(line.Offset, line.Length);
                if (_lines.Count >= MaximumCachedLines) _lines.Clear();
                _lines[line] = text;
                return text;
            }
        }
    }
}
