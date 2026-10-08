// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Kodo.Models;

namespace Kodo;

internal static class LspPath
{
    public static string FilePathToUri(string filePath)
    {
        try { return new Uri(Path.GetFullPath(filePath)).AbsoluteUri; }
        catch { try { return new Uri(filePath, UriKind.Absolute).AbsoluteUri; } catch { return filePath; } }
    }

    public static string FileUriToPath(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath)) return uriOrPath;
        if (Path.IsPathRooted(uriOrPath) && !uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Path.GetFullPath(uriOrPath); } catch { return uriOrPath; }
        }
        if (uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var u = new Uri(uriOrPath, UriKind.Absolute);
                if (u.IsFile)
                {
                    var local = u.LocalPath;
                    if (local.Length >= 3 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':')
                        local = local.Substring(1);
                    if (local.StartsWith(":\\", StringComparison.Ordinal) || local.StartsWith(":/", StringComparison.Ordinal) || local.StartsWith(":", StringComparison.Ordinal))
                    {
                        var abs = Uri.UnescapeDataString(u.AbsolutePath);
                        abs = abs.TrimStart('/');
                        abs = abs.Replace('/', Path.DirectorySeparatorChar);
                        if (abs.Length >= 2 && abs[1] == ':')
                            local = abs;
                    }
                    local = local.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(local))
                        return Path.GetFullPath(local);
                    return local;
                }
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"FileUriToPath: failed to parse URI '{uriOrPath}': {ex.Message}");
            }
            try
            {
                var idx = uriOrPath.IndexOf("://", StringComparison.Ordinal);
                var part = idx >= 0 ? uriOrPath[(idx + 3)..] : uriOrPath;
                part = Uri.UnescapeDataString(part);
                part = part.TrimStart('/');
                part = part.Replace('/', Path.DirectorySeparatorChar);
                if (part.Length >= 2 && part[1] == ':')
                    return Path.GetFullPath(part);
                return part;
            }
            catch { }
            return uriOrPath;
        }
        return uriOrPath;
    }

    public static string NormalizeFilePath(string pathOrUri)
    {
        try
        {
            var p = FixCorruptedPath(FileUriToPath(pathOrUri));
            if (Path.IsPathRooted(p))
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return FixCorruptedPath(pathOrUri); }
    }

    public static bool IsSameDocument(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            var na = NormalizeFilePath(a);
            var nb = NormalizeFilePath(b);
            return string.Equals(na, nb, FileSystemPaths.Comparison);
        }
        catch { return string.Equals(a, b, FileSystemPaths.Comparison); }
    }

    public static string FixCorruptedPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return p;
        if (p.Contains(@":\Users\", StringComparison.OrdinalIgnoreCase) && p.Contains(@"Kodo\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = p.IndexOf(@":\Users\", StringComparison.OrdinalIgnoreCase);
            if (idx > 1)
            {
                var drive = p[idx - 1];
                if (char.IsLetter(drive))
                {
                    var correct = string.Concat(drive.ToString(), @":\", p.Substring(idx + 2).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    try { return Path.GetFullPath(correct); } catch { return correct; }
                }
            }
        }
        return p;
    }

    public static string GetLanguageId(LspConfiguration lsp, string? filePath)
    {
        if (lsp.Languages.Length > 0) return lsp.Languages[0];
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ext)) return ext;
        }
        return "plaintext";
    }

    private const int LineIndexCacheCapacity = 8;
    private static readonly object LineIndexCacheLock = new();
    private static readonly (string Text, Lazy<int[]> Starts)[] LineIndexCache = new (string, Lazy<int[]>)[LineIndexCacheCapacity];
    private static int _nextLineIndexSlot;

    public static int[] GetLineStarts(string text)
    {
        Lazy<int[]>? starts = null;
        lock (LineIndexCacheLock)
        {
            foreach (var entry in LineIndexCache)
            {
                if (ReferenceEquals(entry.Text, text))
                {
                    starts = entry.Starts;
                    break;
                }
            }
            if (starts is null)
            {
                starts = new Lazy<int[]>(() =>
                {
                    var offsets = new List<int> { 0 };
                    for (var offset = 0; offset < text.Length; offset++)
                    {
                        if (text[offset] == '\r')
                        {
                            if (offset + 1 < text.Length && text[offset + 1] == '\n') { offsets.Add(offset + 2); offset++; }
                            else offsets.Add(offset + 1);
                        }
                        else if (text[offset] == '\n')
                        {
                            offsets.Add(offset + 1);
                        }
                    }
                    return offsets.ToArray();
                }, LazyThreadSafetyMode.ExecutionAndPublication);
                LineIndexCache[_nextLineIndexSlot] = (text, starts);
                _nextLineIndexSlot = (_nextLineIndexSlot + 1) % LineIndexCacheCapacity;
            }
        }
        return starts.Value;
    }

    public static int OffsetFromLspPosition(string text, int line, int character) =>
        OffsetFromLspPosition(GetLineStarts(text), text, line, character);

    public static int OffsetFromLspPosition(int[] starts, string text, int line, int character)
        => OffsetFromLspPosition(starts, text, line, character, "utf-16");

    public static int OffsetFromLspPosition(string text, int line, int character, string encoding) =>
        OffsetFromLspPosition(GetLineStarts(text), text, line, character, encoding);

    public static int OffsetFromLspPosition(int[] starts, string text, int line, int character, string encoding)
    {
        if (line < 0) line = 0;
        if (character < 0) character = 0;
        if (starts.Length == 0 || line >= starts.Length) return text.Length;
        var offset = starts[line];
        var lineEnd = line + 1 < starts.Length ? starts[line + 1] : text.Length;
        while (lineEnd > offset && text[lineEnd - 1] is '\r' or '\n') lineEnd--;
        var normalizedEncoding = NormalizePositionEncoding(encoding);
        return offset + Utf16UnitsForPosition(text.AsSpan(offset, lineEnd - offset), character, normalizedEncoding);
    }

    public static (int line, int character) OffsetToLspPosition(string text, int offset)
        => OffsetToLspPosition(text, offset, "utf-16");

    public static (int line, int character) OffsetToLspPosition(string text, int offset, string encoding)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var starts = GetLineStarts(text);
        var lo = 0;
        var hi = starts.Length - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) >> 1);
            if (starts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        var normalizedEncoding = NormalizePositionEncoding(encoding);
        var lineStart = starts[lo];
        var lineEnd = offset;
        while (lineEnd > lineStart && text[lineEnd - 1] is '\r' or '\n') lineEnd--;
        var safeOffset = Math.Min(offset, lineEnd);
        return (lo, PositionLength(text.AsSpan(lineStart, safeOffset - lineStart), normalizedEncoding));
    }

    private static string NormalizePositionEncoding(string? encoding) =>
        string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase) ? "utf-8" :
        string.Equals(encoding, "utf-32", StringComparison.OrdinalIgnoreCase) ? "utf-32" : "utf-16";

    private static int PositionWidth(ReadOnlySpan<char> text, int index, string encoding)
    {
        if (encoding == "utf-16") return char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
        var scalar = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            ? char.ConvertToUtf32(text[index], text[index + 1])
            : text[index];
        return encoding == "utf-8" ? scalar <= 0x7f ? 1 : scalar <= 0x7ff ? 2 : scalar <= 0xffff ? 3 : 4 : scalar > 0xffff ? 2 : 1;
    }

    private static int PositionLength(ReadOnlySpan<char> text, string encoding)
    {
        if (encoding == "utf-16") return text.Length;
        var length = 0;
        for (var index = 0; index < text.Length;)
        {
            var scalar = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(text[index], text[index + 1])
                : text[index];
            length += encoding == "utf-8" ? scalar <= 0x7f ? 1 : scalar <= 0x7ff ? 2 : scalar <= 0xffff ? 3 : 4 : scalar > 0xffff ? 2 : 1;
            index += scalar > 0xffff ? 2 : 1;
        }
        return length;
    }

    public static int PositionLength(string text, string? encoding) =>
        PositionLength(text.AsSpan(), NormalizePositionEncoding(encoding));

    private static int Utf16UnitsForPosition(ReadOnlySpan<char> text, int character, string encoding)
    {
        var consumed = 0;
        var units = 0;
        for (var index = 0; index < text.Length;)
        {
            var pair = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            var width = PositionWidth(text, index, encoding);
            if (consumed + width > character) break;
            consumed += width;
            units += pair ? 2 : 1;
            index += pair ? 2 : 1;
        }
        return units;
    }
}
