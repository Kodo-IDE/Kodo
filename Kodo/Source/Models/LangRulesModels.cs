// Licensed under the GNU GPL-v3.0
using System.Collections.Generic;

namespace Kodo;

public sealed record LangRulesDocumentContext(
    string Uri,
    string FilePath,
    string LanguageId,
    long Version,
    string Text,
    string? WorkspaceRoot = null,
    IReadOnlyDictionary<string, string>? Configuration = null);

public sealed record LangRulesWorkspaceContext(
    string WorkspaceRoot,
    IReadOnlyList<LangRulesDocumentContext> Documents,
    IReadOnlyDictionary<string, string>? Configuration = null);

public sealed record LangRulesCodeActionContext(
    LangRulesDocumentContext Document,
    int Offset,
    IReadOnlyList<LangRuleDiagnostic> Diagnostics);

public sealed record LangRuleWorkspaceDiagnostic(string Uri, LangRuleDiagnostic Diagnostic);
public sealed record LangRuleRelatedInformation(string Uri, int Start, int Length, string Message);

public enum LangRuleDiagnosticTag
{
    Unnecessary = 1,
    Deprecated = 2
}

public sealed record LangRuleToken(string Kind, string Text, int Start, int Length, string Scope, string Color);
public sealed record LangRuleSymbol(string Name, string Kind, int Line, int Start, int Length, bool IsDeclaration);
public sealed record LangRuleDiagnostic(
    int Start,
    int Length,
    string Message,
    string Severity,
    string Code = "",
    string Source = "LangRules")
{
    public string? CodeDescriptionUri { get; init; }
    public IReadOnlyList<LangRuleDiagnosticTag> Tags { get; init; } = [];
    public IReadOnlyList<LangRuleRelatedInformation> RelatedInformation { get; init; } = [];
    public string? Data { get; init; }
}
public sealed record LangRuleLocation(string Name, string Kind, int Start, int Length, bool IsDeclaration);
public sealed record LangRuleHover(string Contents, int Start, int Length);
public sealed record LangRuleSignatureHelp(string Label, string Documentation, int ActiveParameter);
public sealed record LangRuleCodeAction(string Title, string Kind, int Start, int Length, string NewText, string DiagnosticCode);
public sealed record LangRuleEmbeddedRegion(string LanguageId, int Start, int Length);
