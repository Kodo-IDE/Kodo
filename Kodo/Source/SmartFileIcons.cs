// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kodo;

internal static class SmartFileIcons
{
    private sealed record ProjectMarker(string FileName, string Family, string Icon);

    private sealed record ProjectFolderInfo(IReadOnlyList<ProjectMarker> Markers)
    {
        public string Icon => Markers.Select(marker => marker.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? FamilyIcon(Markers[0].Family)
            : "PRJ";
    }

    private static readonly ProjectMarker[] NamedMarkers =
    [
        new("Cargo.toml", "rust", "CARGO"),
        new("go.mod", "go", "GO"),
        new("package.json", "node", "NPM"),
        new("deno.json", "deno", "DENO"),
        new("deno.jsonc", "deno", "DENO"),
        new("pom.xml", "maven", "MAVEN"),
        new("build.gradle", "gradle", "GRADLE"),
        new("build.gradle.kts", "gradle", "GRADLE"),
        new("CMakeLists.txt", "cmake", "CMAKE"),
        new("Makefile", "make", "MAKE"),
        new("makefile", "make", "MAKE"),
        new("GNUmakefile", "make", "MAKE"),
        new("pyproject.toml", "python", "PY"),
        new("requirements.txt", "python", "PY"),
        new("setup.py", "python", "PY"),
        new("Pipfile", "python", "PY"),
        new("Package.swift", "swift", "SWIFT"),
        new("pubspec.yaml", "dart", "DART"),
        new("composer.json", "php", "PHP"),
        new("Gemfile", "ruby", "RUBY"),
        new("mix.exs", "elixir", "ELIXIR"),
        new("stack.yaml", "haskell", "HS"),
    ];

    private static readonly Dictionary<string, ProjectMarker> DotnetMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csproj"] = new("", "dotnet", "C#"),
        [".fsproj"] = new("", "dotnet", "F#"),
        [".vbproj"] = new("", "dotnet", "VB"),
        [".sln"] = new("", "dotnet", "SLN"),
        [".slnx"] = new("", "dotnet", "SLN"),
    };

    private static readonly ConcurrentDictionary<string, ProjectFolderInfo> ProjectFolders = new(FileSystemPaths.Comparer);

    public static Dictionary<string, string> BuildExtensionIcons(IEnumerable<LoadedExtension> extensions)
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions.Where(extension => extension.Type.Equals("language", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var profile in extension.SyntaxProfiles)
            {
                var profileExtensions = profile.Extensions.Length > 0 ? profile.Extensions : extension.Extensions;
                foreach (var profileExtension in profileExtensions)
                {
                    var normalizedExtension = NormalizeExtension(profileExtension);
                    if (normalizedExtension.Length == 0)
                        continue;

                    var icon = profile.FileIcons.TryGetValue(normalizedExtension, out var perExtensionIcon)
                        ? perExtensionIcon
                        : profile.FileIcons.TryGetValue(profileExtension, out perExtensionIcon)
                            ? perExtensionIcon
                            : profile.FileIcon;
                    if (TryNormalizeIcon(icon, out var normalizedIcon))
                        icons[normalizedExtension] = normalizedIcon;
                }
            }
        }

        return icons;
    }

    public static string ResolveIcon(
        string name,
        string fullPath,
        bool isDirectory,
        string workspaceRoot,
        IReadOnlyDictionary<string, string> extensionIcons)
    {
        if (isDirectory)
            return ResolveDirectoryIcon(name, fullPath);

        if (ResolveProjectFileIcon(name) is { Length: > 0 } projectFileIcon)
            return projectFileIcon;

        if (FileTreeItem.GetNamedFileIcon(name) is { Length: > 0 } namedIcon)
            return namedIcon;

        var extension = NormalizeExtension(Path.GetExtension(name));
        if (extension.Length > 0 && extensionIcons.TryGetValue(extension, out var extensionIcon))
            return extensionIcon;

        var standardIcon = FileTreeItem.GetFileIcon(name);
        if (standardIcon != "..")
            return standardIcon;

        return FindNearestProjectFolder(Path.GetDirectoryName(fullPath), workspaceRoot)?.Icon ?? standardIcon;
    }

    public static void InvalidatePath(string path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        try
        {
            var workspace = Path.GetFullPath(workspaceRoot);
            var normalizedPath = Path.GetFullPath(path);
            var current = Directory.Exists(normalizedPath) ? normalizedPath : Path.GetDirectoryName(normalizedPath);
            while (!string.IsNullOrWhiteSpace(current) && IsWithinWorkspace(current, workspace))
            {
                ProjectFolders.TryRemove(current, out _);
                if (FileSystemPaths.Equals(current, workspace))
                    break;
                current = Path.GetDirectoryName(current);
            }
        }
        catch
        {
            ProjectFolders.Clear();
        }
    }

    public static void InvalidateWorkspace(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;
        try
        {
            var workspace = Path.GetFullPath(workspaceRoot);
            foreach (var path in ProjectFolders.Keys.Where(path => IsWithinWorkspace(path, workspace)).ToArray())
                ProjectFolders.TryRemove(path, out _);
        }
        catch
        {
            ProjectFolders.Clear();
        }
    }

    private static string ResolveDirectoryIcon(string name, string fullPath)
    {
        var folderProject = GetProjectFolder(fullPath);
        if (folderProject.Markers.Count > 0)
            return folderProject.Icon;

        return name.Trim().ToLowerInvariant() switch
        {
            "src" or "source" or "sources" => "SRC",
            "test" or "tests" or "testing" or "__tests__" => "TEST",
            "doc" or "docs" or "documentation" => "DOC",
            "asset" or "assets" or "public" or "static" => "ASSET",
            "config" or "configs" or "configuration" => "CFG",
            "build" or "out" or "dist" or "release" or "artifacts" => "BUILD",
            "bin" => "BIN",
            "obj" => "OBJ",
            "target" => "TARGET",
            "generated" or "gen" => "GEN",
            "temp" or "tmp" => "TMP",
            "node_modules" or "vendor" or ".pnpm-store" => "DEPS",
            ".venv" or "venv" => "ENV",
            "coverage" or "__pycache__" or ".cache" or "cache" or ".gradle" or ".next" or ".turbo" or ".mypy_cache" or ".pytest_cache" or ".ruff_cache" => "CACHE",
            ".git" => "GIT",
            ".github" or ".gitlab" or ".azuredevops" => "CI",
            _ => string.Empty,
        };
    }

    private static string? ResolveProjectFileIcon(string name)
    {
        var fileName = name.Trim();
        var namedMarker = NamedMarkers.FirstOrDefault(marker => marker.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (namedMarker is not null)
            return namedMarker.Icon;

        var extension = Path.GetExtension(fileName);
        if (DotnetMarkers.TryGetValue(extension, out var dotnetMarker))
            return dotnetMarker.Icon;
        if (extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase))
            return "C++";
        if (extension.Equals(".cabal", StringComparison.OrdinalIgnoreCase))
            return "HS";
        if (extension.Equals(".xcodeproj", StringComparison.OrdinalIgnoreCase))
            return "XCODE";

        return null;
    }

    private static ProjectFolderInfo? FindNearestProjectFolder(string? directory, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        try
        {
            var workspace = Path.GetFullPath(workspaceRoot);
            var current = Path.GetFullPath(directory);
            while (IsWithinWorkspace(current, workspace))
            {
                var projectFolder = GetProjectFolder(current);
                if (projectFolder.Markers.Count > 0)
                    return projectFolder;
                if (FileSystemPaths.Equals(current, workspace))
                    break;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
        }
        catch
        {
        }

        return null;
    }

    private static ProjectFolderInfo GetProjectFolder(string directory) =>
        ProjectFolders.GetOrAdd(Path.GetFullPath(directory), ScanProjectFolder);

    private static ProjectFolderInfo ScanProjectFolder(string directory)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            var markers = new List<ProjectMarker>();
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var marker = NamedMarkers.FirstOrDefault(candidate => candidate.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (marker is not null)
                {
                    markers.Add(marker);
                    continue;
                }

                var extension = Path.GetExtension(name);
                if (DotnetMarkers.TryGetValue(extension, out var dotnetMarker))
                    markers.Add(dotnetMarker);
                else if (extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase))
                    markers.Add(new ProjectMarker(name, "cpp", "C++"));
                else if (extension.Equals(".cabal", StringComparison.OrdinalIgnoreCase))
                    markers.Add(new ProjectMarker(name, "haskell", "HS"));
                else if (Directory.Exists(entry) && extension.Equals(".xcodeproj", StringComparison.OrdinalIgnoreCase))
                    markers.Add(new ProjectMarker(name, "xcode", "XCODE"));
            }

            return new ProjectFolderInfo(markers);
        }
        catch
        {
            return new ProjectFolderInfo(Array.Empty<ProjectMarker>());
        }
    }

    private static string FamilyIcon(string family) => family.ToLowerInvariant() switch
    {
        "dotnet" => "NET",
        "rust" => "RUST",
        "go" => "GO",
        "node" => "NODE",
        "deno" => "DENO",
        "maven" => "JAVA",
        "gradle" => "GRADLE",
        "cmake" => "CMAKE",
        "make" => "MAKE",
        "python" => "PY",
        "swift" => "SWIFT",
        "dart" => "DART",
        "php" => "PHP",
        "ruby" => "RUBY",
        "elixir" => "ELIXIR",
        "haskell" => "HS",
        "cpp" or "xcode" => "C++",
        _ => "PRJ",
    };

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;
        var normalized = extension.Trim().ToLowerInvariant();
        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static bool TryNormalizeIcon(string? icon, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(icon))
            return false;
        var value = icon.Trim();
        if (value.Length is > 10 or 0 || value.Any(character => !char.IsLetterOrDigit(character) && character is not '+' and not '#' and not '-'))
            return false;
        normalized = value.ToUpperInvariant();
        return true;
    }

    private static bool IsWithinWorkspace(string path, string workspaceRoot) =>
        FileSystemPaths.IsPrefixOf(Path.GetFullPath(path), Path.GetFullPath(workspaceRoot));
}
