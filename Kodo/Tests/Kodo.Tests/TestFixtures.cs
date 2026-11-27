// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace Kodo.Tests;

internal static class ActionCatalog
{
    public static QuickActionItem Create(string id, string contextKey, double baseRelevance, params string[] features) => new()
    {
        Id = id,
        Title = id,
        ContextKey = contextKey,
        BaseRelevance = baseRelevance,
        LearningFeatures = features,
    };

    public static QuickActionItem ProjectTerminal() =>
        Create("ProjectTerminal", "project", 65, "category:project", "file:unknown", "category-file:project|unknown", "project:True");

    public static IReadOnlyList<QuickActionItem> EditingContext() =>
    [
        Create("MoveLineUp", "editor|.cs", 84, "category:editor", "file:.cs"),
        Create("MoveLineDown", "editor|.cs", 83, "category:editor", "file:.cs"),
        Create("DuplicateLineUp", "editor|.cs", 82, "category:editor", "file:.cs"),
        Create("DeleteLine", "editor|.cs", 80, "category:editor", "file:.cs"),
        Create("Format", "format|.cs", 85, "category:format", "file:.cs"),
        Create("FindInFile", "editor|.cs", 75, "category:editor", "file:.cs"),
        ProjectTerminal(),
    ];

    public static IReadOnlyList<QuickActionItem> WorkspaceClosedContext() =>
    [
        Create("MoveLineUp", "editor|.cs", 84, "category:editor", "file:.cs"),
        Create("Format", "format|.cs", 85, "category:format", "file:.cs"),
        Create("FindInFile", "editor|.cs", 75, "category:editor", "file:.cs"),
    ];
}

internal sealed class TempLearningFile : IDisposable
{
    public TempLearningFile()
    {
        Folder = Path.Combine(Path.GetTempPath(), "kodo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        FilePath = Path.Combine(Folder, "quick-actions-learning.json");
    }

    public string Folder { get; }

    public string FilePath { get; }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch { }
    }
}
