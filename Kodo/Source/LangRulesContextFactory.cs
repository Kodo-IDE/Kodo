// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{
    private static LangRulesDocumentContext CreateLangRulesDocumentContext(
        string filePath,
        string text,
        long version,
        string? workspaceRoot,
        LoadedExtension? languageExtension) => new(
            ToLangRulesUri(filePath),
            filePath,
            languageExtension?.Id ?? Path.GetExtension(filePath),
            version,
            text,
            workspaceRoot,
            languageExtension?.LangRulesOptions);

    private (LangRulesDocumentContext Document, LangRulesWorkspaceContext Workspace) CreateLangRulesContexts(
        string filePath,
        string text,
        long version,
        LoadedExtension? languageExtension)
    {
        var fullPath = GetFullPathOrEmpty(filePath);
        var root = string.IsNullOrWhiteSpace(_currentFolderPath)
            ? (fullPath.Length == 0 ? string.Empty : Path.GetDirectoryName(fullPath) ?? string.Empty)
            : GetFullPathOrEmpty(_currentFolderPath);
        var current = CreateLangRulesDocumentContext(fullPath, text, version, root, languageExtension);
        var documents = new List<LangRulesDocumentContext> { current };
        var seen = new HashSet<string>(FileSystemPaths.Comparer);
        if (fullPath.Length > 0) seen.Add(fullPath);
        var totalTextLength = text.Length;
        foreach (var tab in OpenTabs.Where(tab => !tab.IsUntitled && !string.IsNullOrWhiteSpace(tab.Path)))
        {
            var tabPath = GetFullPathOrEmpty(tab.Path);
            if (tabPath.Length == 0) continue;
            if (tabPath.Length == 0 || !FileSystemPaths.IsPrefixOf(tabPath, root) || !seen.Add(tabPath))
                continue;
            var tabText = tab.Document?.Text ?? tab.Content;
            if (tabText.Length > 250_000 || totalTextLength + tabText.Length > 2_000_000 || documents.Count >= 128)
                continue;
            var tabExtension = GetLanguageExtension(tabPath);
            documents.Add(new LangRulesDocumentContext(
                ToLangRulesUri(tabPath),
                tabPath,
                tabExtension?.Id ?? Path.GetExtension(tabPath),
                tab.Document?.Version.GetHashCode() ?? 0,
                tabText,
                root,
                tabExtension?.LangRulesOptions));
            totalTextLength += tabText.Length;
        }

        return (current, new LangRulesWorkspaceContext(root, documents, languageExtension?.LangRulesOptions));
    }

    private static string ToLangRulesUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "untitled:";
        try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; }
        catch { return path; }
    }

    private static string GetFullPathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return string.Empty; }
    }
}
