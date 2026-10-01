// Licensed under the GNU GPL-v3.0
using AvaloniaEdit.Document;

namespace Kodo;

internal static class DocumentTextCache
{
    private static readonly object Gate = new();
    private static TextDocument? _document;
    private static ITextSourceVersion? _version;
    private static string _text = string.Empty;

    public static string Get(TextDocument document)
    {
        lock (Gate)
        {
            var version = document.Version;
            if (ReferenceEquals(_document, document) && ReferenceEquals(_version, version))
                return _text;

            var text = document.Text ?? string.Empty;
            _document = document;
            _version = version;
            _text = text;
            return text;
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _document = null;
            _version = null;
            _text = string.Empty;
        }
    }
}
