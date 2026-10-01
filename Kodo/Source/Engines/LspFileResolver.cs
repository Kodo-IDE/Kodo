// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kodo.Models;

namespace Kodo;

/// <summary>
/// Answers "which language server serves this file?" and "what is the workspace
/// root for this file?".
/// </summary>
/// <remarks>
/// Extracted from <c>MainWindow</c>, which previously answered both inline while
/// also owning the results. Resolution is pure with respect to the loaded
/// extension set, so it is memoised per file extension and thrown away wholesale
/// by <see cref="Invalidate"/> whenever extensions change. Everything the
/// resolver cannot infer on its own - the loaded extensions and the folder the
/// window currently has open - is passed in, so this type holds no UI state.
/// </remarks>
internal sealed class LspFileResolver
{
    private sealed class Caches
    {
        public readonly Dictionary<string, LoadedExtension?> ExtensionsByFileExtension = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, LspConfiguration?> ConfigurationsByFileExtension = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> WorkspaceRootByDirectory = new(FileSystemPaths.Comparer);
    }

    private Caches? _caches;

    public void Invalidate() => _caches = null;

    private Caches EnsureCaches() => _caches ??= new Caches();

    /// <summary>
    /// The extension providing the LSP for a file, or null if none does. The
    /// caller is responsible for rejecting files that have no language at all
    /// (plain text, no extension); this only matches on the extension.
    /// </summary>
    public LoadedExtension? ResolveExtensionByFileExtension(string ext, IReadOnlyList<LoadedExtension> loadedExtensions)
    {
        var cache = EnsureCaches();
        if (cache.ExtensionsByFileExtension.TryGetValue(ext, out var cached)) return cached;

        List<LoadedExtension>? candidates = null;
        foreach (var extension in loadedExtensions)
        {
            if (!extension.HasLsp) continue;
            candidates ??= new List<LoadedExtension>();
            candidates.Add(extension);
        }

        LoadedExtension? match = null;
        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (ExtensionDeclaresFileExtension(candidate, ext)) { match = candidate; break; }
            }
            match ??= candidates.FirstOrDefault(candidate =>
                candidate.Extensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase)));
        }

        cache.ExtensionsByFileExtension[ext] = match;
        return match;
    }

    public static bool ExtensionDeclaresFileExtension(LoadedExtension extension, string ext)
    {
        if (extension.Lsps.Count > 0)
        {
            foreach (var configuration in extension.Lsps)
                foreach (var fileExtension in configuration.FileExtensions)
                    if (fileExtension.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (extension.Lsp is { } single)
            foreach (var fileExtension in single.FileExtensions)
                if (fileExtension.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The specific LSP configuration to use for a file. An extension can declare
    /// several servers; the one whose declared file extensions cover this file
    /// wins, otherwise the first configured.
    /// </summary>
    public LspConfiguration? ResolveConfigurationForFile(
        string? filePath,
        Func<string?, LoadedExtension?> resolveExtension)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var ext = resolveExtension(filePath);
        if (ext is null) return null;
        var cache = EnsureCaches();
        var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
        if (cache.ConfigurationsByFileExtension.TryGetValue(fileExt, out var cached)) return cached;

        var configuration = ext.Lsp ?? ext.Lsps.FirstOrDefault();
        foreach (var candidate in ext.Lsps)
        {
            var matches = false;
            foreach (var fileExtension in candidate.FileExtensions)
            {
                if (!fileExtension.Equals(fileExt, StringComparison.OrdinalIgnoreCase)) continue;
                matches = true;
                break;
            }
            if (!matches) continue;
            configuration = candidate;
            break;
        }
        cache.ConfigurationsByFileExtension[fileExt] = configuration;
        return configuration;
    }

    /// <summary>
    /// Workspace root for a file: the nearest ancestor containing one of the
    /// server's declared root markers, else the enclosing open folder, else the
    /// file's own directory.
    /// </summary>
    public string GetWorkspaceRootForFile(
        string? filePath,
        string? currentFolderPath,
        Func<string?, LspConfiguration?> resolveConfiguration,
        Func<string?, LoadedExtension?> resolveExtension,
        Func<string, string, bool> isPathInsideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var cache = EnsureCaches();
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory)) return ResolveWorkspaceRootByWalking(filePath, currentFolderPath, resolveConfiguration, resolveExtension, isPathInsideDirectory);
            if (cache.WorkspaceRootByDirectory.TryGetValue(directory, out var cachedRoot))
                return cachedRoot;
            var resolved = ResolveWorkspaceRootByWalking(filePath, currentFolderPath, resolveConfiguration, resolveExtension, isPathInsideDirectory);
            cache.WorkspaceRootByDirectory[directory] = resolved;
            return resolved;
        }
        if (!string.IsNullOrWhiteSpace(currentFolderPath) && Directory.Exists(currentFolderPath))
            return currentFolderPath;
        return Environment.CurrentDirectory;
    }

    private static string ResolveWorkspaceRootByWalking(
        string filePath,
        string? currentFolderPath,
        Func<string?, LspConfiguration?> resolveConfiguration,
        Func<string?, LoadedExtension?> resolveExtension,
        Func<string, string, bool> isPathInsideDirectory)
    {
        var markers = resolveConfiguration(filePath)?.RootMarkers ?? resolveExtension(filePath)?.Lsp?.RootMarkers;
        if (markers != null && markers.Length > 0)
        {
            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                foreach (var m in markers)
                    if (File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)))
                        return dir;
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        if (!string.IsNullOrWhiteSpace(currentFolderPath) && isPathInsideDirectory(filePath, currentFolderPath))
            return currentFolderPath;
        var fileDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(fileDirectory) && Directory.Exists(fileDirectory))
            return fileDirectory;
        if (!string.IsNullOrWhiteSpace(currentFolderPath) && Directory.Exists(currentFolderPath))
            return currentFolderPath;
        return Environment.CurrentDirectory;
    }
}
