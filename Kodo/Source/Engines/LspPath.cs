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
    {
        if (line < 0) line = 0;
        if (character < 0) character = 0;
        if (starts.Length == 0 || line >= starts.Length) return text.Length;
        var offset = starts[line];
        var lineEnd = line + 1 < starts.Length ? starts[line + 1] - 1 : text.Length;
        var lineLen = lineEnd - offset;
        if (lineLen > 0 && lineEnd > offset && text[lineEnd - 1] == '\r')
            lineLen--;
        var col = Math.Min(character, Math.Max(0, lineLen));
        if (col > 0 && col < lineLen && offset + col < text.Length && char.IsHighSurrogate(text[offset + col - 1]) && char.IsLowSurrogate(text[offset + col]))
            col++;
        return Math.Clamp(offset + col, 0, Math.Max(0, text.Length));
    }

    public static (int line, int character) OffsetToLspPosition(string text, int offset)
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
        return (lo, offset - starts[lo]);
    }
}
