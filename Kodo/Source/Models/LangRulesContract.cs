// Licensed under GPL v3.0
using System;
using System.Collections.Generic;

namespace Kodo;

[Flags]
public enum LangRulesCapability
{
    None = 0,
    Tokens = 1 << 0,
    Symbols = 1 << 1,
    Diagnostics = 1 << 2,
    SemanticAnalysis = 1 << 3,
    Completions = 1 << 4,
    Definition = 1 << 5,
    References = 1 << 6,
    Hover = 1 << 7,
    SignatureHelp = 1 << 8,
    CodeActions = 1 << 9,
    Formatting = 1 << 10,
    EmbeddedRegions = 1 << 11
}

public sealed record LangRulesProviderInfo(string Name, string Version, LangRulesCapability Capabilities, IReadOnlyList<string>? ValidationWarnings = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = ValidationWarnings ?? Array.Empty<string>();
    public bool Provides(LangRulesCapability capability) => (Capabilities & capability) == capability;
}

public interface ILangRulesProvider
{
    LangRulesProviderInfo Info { get; }
    IReadOnlyList<LangRuleToken> Tokenize(string code) => Array.Empty<LangRuleToken>();
    IReadOnlyList<LangRuleSymbol> AnalyzeSymbols(string code) => Array.Empty<LangRuleSymbol>();
    IReadOnlyList<LangRuleDiagnostic> AnalyzeSyntax(string code) => Array.Empty<LangRuleDiagnostic>();
    IReadOnlyList<LangRuleDiagnostic> AnalyzeSemantics(string code) => Array.Empty<LangRuleDiagnostic>();
    IReadOnlyList<string> GetCompletions(string code, int offset, string prefix) => Array.Empty<string>();
    LangRuleLocation? FindDefinition(string code, int offset) => null;
    IReadOnlyList<LangRuleLocation> FindReferences(string code, int offset) => Array.Empty<LangRuleLocation>();
    LangRuleHover? GetHover(string code, int offset) => null;
    LangRuleSignatureHelp? GetSignatureHelp(string code, int offset) => null;
    IReadOnlyList<LangRuleCodeAction> GetCodeActions(string code, int offset) => Array.Empty<LangRuleCodeAction>();
    string? FormatDocument(string code) => null;
    IReadOnlyList<LangRuleEmbeddedRegion> GetEmbeddedRegions(string code) => Array.Empty<LangRuleEmbeddedRegion>();
}
