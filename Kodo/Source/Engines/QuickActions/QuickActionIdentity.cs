// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo.QuickActions;

public static class QuickActionIdentity
{
    private static readonly Dictionary<string, string> CommandAliases = new(StringComparer.Ordinal)
    {
        ["cmd.quick-fix"] = "QuickFix",
        ["cmd.next-problem"] = "NextProblem",
        ["cmd.previous-problem"] = "PreviousProblem",
        ["cmd.change-all-occurrences"] = "ChangeOccurrences",
        ["cmd.rename-symbol"] = "Rename",
        ["cmd.go-to-definition"] = "Definition",
        ["cmd.find-all-references"] = "References",
        ["cmd.go-to-type-definition"] = "TypeDefinition",
        ["cmd.go-to-implementation"] = "Implementation",
        ["cmd.show-document-symbols"] = "DocumentSymbols",
        ["cmd.show-signature-help"] = "SignatureHelp",
        ["cmd.format-document"] = "Format",
        ["cmd.find-in-file"] = "FindInFile",
        ["cmd.toggle-line-comment"] = "ToggleComment",
        ["cmd.select-current-line"] = "SelectLine",
        ["cmd.move-line-up"] = "MoveLineUp",
        ["cmd.move-line-down"] = "MoveLineDown",
        ["cmd.duplicate-line-up"] = "DuplicateLineUp",
        ["cmd.duplicate-line-down"] = "DuplicateLineDown",
        ["cmd.delete-line"] = "DeleteLine",
        ["cmd.insert-line-above"] = "InsertLineAbove",
        ["cmd.insert-line-below"] = "InsertLineBelow",
        ["cmd.toggle-bookmark"] = "ToggleBookmark",
        ["cmd.find-in-project"] = "ProjectSearch",
        ["cmd.open-workspace-in-terminal"] = "ProjectTerminal",
        ["ToggleLineComment"] = "ToggleComment",
        ["ChangeAllOccurrences"] = "ChangeOccurrences",
        ["SelectCurrentLine"] = "SelectLine",
        ["GoToDefinition"] = "Definition",
        ["FindInProject"] = "ProjectSearch",
        ["NewTerminalSession"] = "cmd.new-terminal",
    };

    public static IReadOnlySet<string> CoveredCommandIds { get; } =
        new HashSet<string>(CommandAliases.Keys, StringComparer.Ordinal);

    public static bool IsAliased(string? commandId) =>
        !string.IsNullOrWhiteSpace(commandId) && CommandAliases.ContainsKey(commandId.Trim());

    public static string ForCommand(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return string.Empty;
        var trimmed = commandId.Trim();
        return CommandAliases.TryGetValue(trimmed, out var actionId) ? actionId : trimmed;
    }
}
