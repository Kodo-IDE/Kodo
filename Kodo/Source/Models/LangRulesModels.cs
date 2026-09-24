// Licensed under GPL v3.0
namespace Kodo;

public sealed record LangRuleToken(string Kind, string Text, int Start, int Length, string Scope, string Color);
public sealed record LangRuleSymbol(string Name, string Kind, int Line, int Start, int Length, bool IsDeclaration);
public sealed record LangRuleDiagnostic(
    int Start,
    int Length,
    string Message,
    string Severity,
    string Code = "",
    string Source = "LangRules");
public sealed record LangRuleLocation(string Name, string Kind, int Start, int Length, bool IsDeclaration);
public sealed record LangRuleHover(string Contents, int Start, int Length);
public sealed record LangRuleSignatureHelp(string Label, string Documentation, int ActiveParameter);
public sealed record LangRuleCodeAction(string Title, string Kind, int Start, int Length, string NewText, string DiagnosticCode);
public sealed record LangRuleEmbeddedRegion(string LanguageId, int Start, int Length);
