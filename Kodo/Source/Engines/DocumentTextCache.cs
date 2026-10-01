// Licensed under the GNU GPL-v3.0
using AvaloniaEdit.Document;

namespace Kodo;

/// <summary>
/// Shares a single full-text materialization of the current document across all
/// syntax colorizers. Without this, every colorizer calls
/// <see cref="TextDocument.Text"/> independently, allocating and copying the
/// whole file once per colorizer per redraw.
/// </summary>
/// <remarks>
/// Validity is keyed on document identity plus <see cref="TextDocument.Version"/>
/// identity. AvaloniaEdit version objects are immutable and a new one is created
/// per change, so reference equality is a sound (and O(1)) change detector: the
/// same object means the content is unchanged, anything else forces a rebuild.
/// </remarks>
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

    /// <summary>
    /// Drops the cached text. The version check already invalidates on every edit;
    /// colorizers call this from their own <c>InvalidateCache</c> as a second,
    /// independent guard so highlighting can never go stale.
    /// </summary>
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
