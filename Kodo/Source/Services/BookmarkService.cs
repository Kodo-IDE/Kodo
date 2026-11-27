// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Kodo;

internal static class BookmarkService
{
    private const int SaveDelayMilliseconds = 750;
    private const int RecoveryWindow = 200;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        MaxDepth = 16,
        WriteIndented = true,
    };

    private static readonly object Gate = new();
    private static readonly List<CodeBookmark> BookmarksInternal = new();
    private static Timer? _saveTimer;
    private static bool _dirty;
    private static string? _currentWorkspaceKey;

    public static string? CurrentWorkspaceRoot { get; private set; }

    public static IReadOnlyList<CodeBookmark> GetBookmarks()
    {
        lock (Gate)
        {
            return BookmarksInternal.ToList();
        }
    }

    public static string BookmarksDirectory
    {
        get
        {
            var dir = Path.Combine(KodoPaths.DataRoot, "Bookmarks");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    public static string WorkspaceKey(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string? TryMakeRelativePath(string? filePath, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        try
        {
            var fullFile = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootWithSeparator = root + Path.DirectorySeparatorChar;

            if (!fullFile.StartsWith(rootWithSeparator, FileSystemPaths.Comparison))
                return null;

            var relative = Path.GetRelativePath(root, fullFile).Replace('\\', '/');
            return IsSafeRelativePath(relative) ? relative : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ToAbsolutePath(string relativePath)
    {
        var root = CurrentWorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !IsSafeRelativePath(relativePath))
            return null;

        try
        {
            return Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }
    }

    public static void LoadForWorkspace(string? workspaceRoot)
    {
        Flush();

        lock (Gate)
        {
            BookmarksInternal.Clear();
            CurrentWorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot;
            _currentWorkspaceKey = CurrentWorkspaceRoot is null ? null : WorkspaceKey(workspaceRoot!);

            var file = CurrentFilePath();
            if (file is null || !File.Exists(file)) return;

            try
            {
                using var stream = File.OpenRead(file);
                var snapshot = JsonSerializer.Deserialize<CodeBookmarkSnapshot>(stream, ReadOptions);
                if (snapshot?.Bookmarks is null) return;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var bookmark in snapshot.Bookmarks)
                {
                    if (bookmark is null || bookmark.Line <= 0) continue;
                    if (!IsSafeRelativePath(bookmark.RelativePath)) continue;

                    bookmark.RelativePath = NormalizeRelative(bookmark.RelativePath);
                    bookmark.Id = string.IsNullOrWhiteSpace(bookmark.Id)
                        ? Guid.NewGuid().ToString()
                        : bookmark.Id;

                    var key = $"{bookmark.RelativePath}:{bookmark.Line}";
                    if (!seen.Add(key)) continue;

                    BookmarksInternal.Add(bookmark);
                }
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Failed to load bookmarks", ex);
            }
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            if (!_dirty) return;
            _dirty = false;
            SaveLocked();
        }
    }

    public static List<CodeBookmark> ForFile(string relativePath)
    {
        lock (Gate)
        {
            return BookmarksInternal
                .Where(b => string.Equals(b.RelativePath, relativePath, FileSystemPaths.Comparison))
                .OrderBy(b => b.Line)
                .ToList();
        }
    }

    public static bool ToggleBookmark(string relativePath, int line, string? lineText)
    {
        if (!IsSafeRelativePath(relativePath) || line <= 0) return false;

        var normalized = NormalizeRelative(relativePath);
        var preview = PreviewOf(lineText);
        var fingerprint = Fingerprint(lineText);

        lock (Gate)
        {
            var existing = BookmarksInternal.FirstOrDefault(b =>
                string.Equals(b.RelativePath, normalized, FileSystemPaths.Comparison) && b.Line == line);
            if (existing is not null)
            {
                BookmarksInternal.Remove(existing);
                ScheduleSaveLocked();
                return false;
            }

            BookmarksInternal.Add(new CodeBookmark
            {
                Id = Guid.NewGuid().ToString(),
                RelativePath = normalized,
                Line = line,
                Preview = preview.Length == 0 ? null : preview,
                LineFingerprint = fingerprint,
            });
            ScheduleSaveLocked();
            return true;
        }
    }

    public static bool RemoveBookmarkById(string id)
    {
        lock (Gate)
        {
            var existing = BookmarksInternal.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));
            if (existing is null) return false;
            BookmarksInternal.Remove(existing);
            ScheduleSaveLocked();
            return true;
        }
    }

    public static void RemoveAll(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        lock (Gate)
        {
            var removed = BookmarksInternal.RemoveAll(b =>
                string.Equals(b.RelativePath, normalized, FileSystemPaths.Comparison));
            if (removed > 0) ScheduleSaveLocked();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            if (BookmarksInternal.Count == 0) return;
            BookmarksInternal.Clear();
            ScheduleSaveLocked();
        }
    }

    public static void SetUnresolved(string id, bool unresolved)
    {
        lock (Gate)
        {
            var bookmark = BookmarksInternal.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));
            if (bookmark is null || bookmark.Unresolved == unresolved) return;
            bookmark.Unresolved = unresolved;
            ScheduleSaveLocked();
        }
    }

    public static bool SetLabel(string id, string? label)
    {
        lock (Gate)
        {
            var bookmark = BookmarksInternal.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));
            if (bookmark is null) return false;

            var normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            if (string.Equals(bookmark.Label, normalized, StringComparison.Ordinal)) return false;

            bookmark.Label = normalized;
            ScheduleSaveLocked();
            return true;
        }
    }

    public static int? NextLine(string relativePath, int currentLine)
    {
        lock (Gate)
        {
            return BookmarksInternal
                .Where(b => string.Equals(b.RelativePath, relativePath, FileSystemPaths.Comparison) && b.Line > currentLine)
                .Select(b => (int?)b.Line)
                .OrderBy(l => l)
                .FirstOrDefault();
        }
    }

    public static int? PreviousLine(string relativePath, int currentLine)
    {
        lock (Gate)
        {
            return BookmarksInternal
                .Where(b => string.Equals(b.RelativePath, relativePath, FileSystemPaths.Comparison) && b.Line < currentLine)
                .Select(b => (int?)b.Line)
                .OrderByDescending(l => l)
                .FirstOrDefault();
        }
    }

    public static bool SyncLines(string relativePath, IReadOnlyList<(string Id, int Line)> updates)
    {
        if (updates.Count == 0) return false;

        lock (Gate)
        {
            var changed = false;
            foreach (var (id, line) in updates)
            {
                if (line <= 0) continue;

                var bookmark = BookmarksInternal.FirstOrDefault(b =>
                    string.Equals(b.Id, id, StringComparison.Ordinal) &&
                    string.Equals(b.RelativePath, relativePath, FileSystemPaths.Comparison));
                if (bookmark is null) continue;

                if (bookmark.Line != line)
                {
                    bookmark.Line = line;
                    changed = true;
                }

                if (bookmark.Unresolved)
                {
                    bookmark.Unresolved = false;
                    changed = true;
                }
            }

            if (changed)
            {
                changed |= DeduplicateLocked(relativePath);
                ScheduleSaveLocked();
            }
            return changed;
        }
    }

    public static bool RefreshFingerprints(string relativePath, Func<int, string?> lineTextAt, IReadOnlyCollection<string>? ids = null)
    {
        lock (Gate)
        {
            var targets = BookmarksInternal
                .Where(b => string.Equals(b.RelativePath, relativePath, FileSystemPaths.Comparison) &&
                            (ids is null || ids.Contains(b.Id)))
                .ToList();
            if (targets.Count == 0) return false;

            var changed = false;
            foreach (var bookmark in targets)
            {
                var text = lineTextAt(bookmark.Line);
                if (text is null) continue;

                var fingerprint = Fingerprint(text);
                if (!string.Equals(bookmark.LineFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    bookmark.LineFingerprint = fingerprint;
                    bookmark.Preview = NullIfEmpty(PreviewOf(text));
                    changed = true;
                }
                else if (string.IsNullOrEmpty(bookmark.Preview))
                {
                    var preview = PreviewOf(text);
                    if (preview.Length > 0)
                    {
                        bookmark.Preview = preview;
                        changed = true;
                    }
                }
            }

            if (changed) ScheduleSaveLocked();
            return changed;
        }
    }

    public static bool RecoverFile(string relativePath, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return false;

        lock (Gate)
        {
            var targets = BookmarksInternal
                .Where(b => string.Equals(b.RelativePath, relativePath, FileSystemPaths.Comparison))
                .ToList();
            if (targets.Count == 0) return false;

            var changed = false;
            foreach (var bookmark in targets)
            {
                var saved = Math.Clamp(bookmark.Line, 1, lines.Count);
                var fingerprint = bookmark.LineFingerprint;

                if (string.IsNullOrEmpty(fingerprint))
                {
                    bookmark.LineFingerprint = Fingerprint(lines[saved - 1]);
                    bookmark.Preview ??= NullIfEmpty(PreviewOf(lines[saved - 1]));
                    if (bookmark.Line != saved)
                    {
                        bookmark.Line = saved;
                        changed = true;
                    }
                    if (bookmark.Unresolved)
                    {
                        bookmark.Unresolved = false;
                        changed = true;
                    }
                    continue;
                }

                if (Fingerprint(lines[saved - 1]) == fingerprint)
                {
                    if (bookmark.Line != saved)
                    {
                        bookmark.Line = saved;
                        changed = true;
                    }
                    if (bookmark.Unresolved)
                    {
                        bookmark.Unresolved = false;
                        changed = true;
                    }
                    bookmark.Preview ??= NullIfEmpty(PreviewOf(lines[saved - 1]));
                    continue;
                }

                var match = FindNearbyMatch(lines, fingerprint, saved);
                if (match > 0)
                {
                    if (bookmark.Line != match)
                    {
                        bookmark.Line = match;
                        changed = true;
                    }
                    if (bookmark.Unresolved)
                    {
                        bookmark.Unresolved = false;
                        changed = true;
                    }
                    bookmark.Preview ??= NullIfEmpty(PreviewOf(lines[match - 1]));
                    continue;
                }

                if (!bookmark.Unresolved)
                {
                    bookmark.Unresolved = true;
                    changed = true;
                }
            }

            changed |= DeduplicateLocked(relativePath);
            if (changed) ScheduleSaveLocked();
            return changed;
        }
    }

    private static int FindNearbyMatch(IReadOnlyList<string> lines, string fingerprint, int anchor)
    {
        var start = Math.Max(1, anchor - RecoveryWindow);
        var end = Math.Min(lines.Count, anchor + RecoveryWindow);

        var best = -1;
        var bestDistance = int.MaxValue;
        for (var line = start; line <= end; line++)
        {
            if (!string.Equals(Fingerprint(lines[line - 1]), fingerprint, StringComparison.Ordinal))
                continue;

            var distance = Math.Abs(line - anchor);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = line;
        }

        if (best > 0) return best;

        var unique = -1;
        var hits = 0;
        for (var line = 1; line <= lines.Count; line++)
        {
            if (!string.Equals(Fingerprint(lines[line - 1]), fingerprint, StringComparison.Ordinal))
                continue;
            hits++;
            unique = line;
            if (hits > 1) return -1;
        }

        return hits == 1 ? unique : -1;
    }

    private static bool DeduplicateLocked(string relativePath)
    {
        var seen = new HashSet<int>();
        var changed = false;
        for (var index = BookmarksInternal.Count - 1; index >= 0; index--)
        {
            var bookmark = BookmarksInternal[index];
            if (!string.Equals(bookmark.RelativePath, relativePath, FileSystemPaths.Comparison))
                continue;
            if (seen.Add(bookmark.Line)) continue;
            BookmarksInternal.RemoveAt(index);
            changed = true;
        }
        return changed;
    }

    private static void ScheduleSaveLocked()
    {
        _dirty = true;
        _saveTimer ??= new Timer(static _ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _saveTimer.Change(SaveDelayMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void SaveLocked()
    {
        var file = CurrentFilePath();
        if (file is null) return;

        try
        {
            var snapshot = new CodeBookmarkSnapshot
            {
                Version = 1,
                Bookmarks = BookmarksInternal.ToList(),
            };

            AtomicWrite(file, JsonSerializer.Serialize(snapshot, WriteOptions));
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to serialize bookmarks", ex);
        }
    }

    private static string? CurrentFilePath()
    {
        if (_currentWorkspaceKey is null) return null;
        return Path.Combine(BookmarksDirectory, _currentWorkspaceKey + ".json");
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains(':')) return false;

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/')) return false;

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }

        return true;
    }

    private static string NormalizeRelative(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    private static string Fingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string PreviewOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var trimmed = text.Trim();
        trimmed = trimmed.Replace('\t', ' ');
        if (trimmed.Length <= 120) return trimmed;
        return trimmed[..120] + "...";
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static void AtomicWrite(string path, string payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return;

        try { Directory.CreateDirectory(directory); } catch { }

        var tempPath = path + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to persist bookmarks", ex);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
