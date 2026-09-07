using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Kodo;

/// Adapter for the generated LangRules.dll contract shipped by language extensions.
/// The core deliberately talks to this through reflection: each language package owns
/// its scanner and may use a different generated namespace.
public sealed class LangRulesAdapter
{
    private readonly Type _rules;
    private readonly MethodInfo? _tokenize;
    private readonly MethodInfo? _symbols;
    private readonly MethodInfo? _completions;
    private readonly MethodInfo? _regexes;
    private readonly MethodInfo? _colors;
    private readonly MethodInfo? _diagnostics;
    private readonly MethodInfo? _semantics;
    private readonly MethodInfo? _contextCompletions;
    private readonly MethodInfo? _definition;
    private readonly MethodInfo? _references;
    private readonly MethodInfo? _hover;
    private readonly MethodInfo? _signature;
    private readonly MethodInfo? _codeActions;
    private readonly MethodInfo? _formatter;
    private readonly MethodInfo? _embeddedRegions;

    private LangRulesAdapter(Type rules)
    {
        _rules = rules;
        _tokenize = rules.GetMethod("Tokenize", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _symbols = rules.GetMethod("AnalyzeSymbols", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _completions = rules.GetMethod("GetCompletionsForPrefix", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _regexes = rules.GetMethod("GetAllRegexes", BindingFlags.Public | BindingFlags.Static);
        _colors = rules.GetMethod("GetColorMap", BindingFlags.Public | BindingFlags.Static);
        _diagnostics = rules.GetMethod("AnalyzeSyntax", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _semantics = rules.GetMethod("AnalyzeSemantics", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _contextCompletions = rules.GetMethod("GetCompletionsForPrefix", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)]);
        _definition = rules.GetMethod("FindDefinition", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)]);
        _references = rules.GetMethod("FindReferences", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)]);
        _hover = rules.GetMethod("GetHoverInfo", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)]);
        _signature = rules.GetMethod("GetSignatureHelp", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)]);
        _codeActions = rules.GetMethod("GetCodeActions", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _formatter = rules.GetMethod("FormatDocument", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        _embeddedRegions = rules.GetMethod("GetEmbeddedRegions", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
    }

    public static LangRulesAdapter? TryLoad(string? folder, string? assemblyFile)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(assemblyFile)) return null;
        var path = Path.Combine(folder, assemblyFile);
        if (!File.Exists(path)) return null;
        try
        {
            var assembly = Assembly.LoadFrom(path);
            var rules = assembly.GetTypes().FirstOrDefault(t => t.IsAbstract && t.IsSealed && t.Name == "LangRules");
            return rules is null ? null : new LangRulesAdapter(rules);
        }
        catch { return null; }
    }

    public bool HasTokenizer => _tokenize is not null;
    public bool HasSymbolAnalyzer => _symbols is not null;
    public bool HasDiagnostics => _diagnostics is not null;
    public bool HasSemanticAnalyzer => _semantics is not null;
    public bool HasDefinitionProvider => _definition is not null;
    public bool HasReferenceProvider => _references is not null;
    public bool HasHoverProvider => _hover is not null;
    public bool HasSignatureHelpProvider => _signature is not null;
    public bool HasCodeActionProvider => _codeActions is not null;
    public bool HasFormatter => _formatter is not null;
    public bool HasEmbeddedRegions => _embeddedRegions is not null;

    public IReadOnlyList<LangRuleDiagnostic> AnalyzeSyntax(string code)
    {
        if (_diagnostics is null) return [];
        try { return ConvertDiagnostics(_diagnostics.Invoke(null, [code])); }
        catch { return []; }
    }

    public IReadOnlyList<LangRuleDiagnostic> AnalyzeSemantics(string code)
    {
        if (_semantics is null) return [];
        try { return ConvertDiagnostics(_semantics.Invoke(null, [code])); }
        catch { return []; }
    }

    public IReadOnlyList<LangRuleToken> Tokenize(string code)
    {
        if (_tokenize is null) return [];
        try { return ConvertTokens(_tokenize.Invoke(null, [code])); }
        catch { return []; }
    }

    public IReadOnlyList<LangRuleSymbol> AnalyzeSymbols(string code)
    {
        if (_symbols is null) return [];
        try { return ConvertSymbols(_symbols.Invoke(null, [code])); }
        catch { return []; }
    }

    public IEnumerable<string> GetVariableLikeNames(string code)
    {
        foreach (var token in Tokenize(code))
        {
            // TokenKind is owned by the extension. Keep this contract name-based
            // so the core does not depend on a language's generated enum type.
            if (token.Kind.Equals("Variable", StringComparison.OrdinalIgnoreCase) ||
                token.Kind.Equals("Parameter", StringComparison.OrdinalIgnoreCase) ||
                token.Kind.Equals("Field", StringComparison.OrdinalIgnoreCase) ||
                token.Kind.Equals("Property", StringComparison.OrdinalIgnoreCase))
            {
                var name = token.Text.TrimStart('$', '@');
                if (!string.IsNullOrWhiteSpace(name)) yield return name;
            }
        }
    }

    public IReadOnlyList<string> GetCompletions(string prefix)
    {
        if (_completions is null) return [];
        try { return (_completions.Invoke(null, [prefix]) as System.Collections.IEnumerable)?.Cast<object>().Select(Convert.ToString).Where(x => x is not null).Cast<string>().ToArray() ?? []; }
        catch { return []; }
    }

    public IReadOnlyList<string> GetCompletions(string prefix, string code)
    {
        if (_contextCompletions is null) return GetCompletions(prefix);
        try { return ConvertStrings(_contextCompletions.Invoke(null, [prefix, code])); }
        catch { return GetCompletions(prefix); }
    }

    public LangRuleLocation? FindDefinition(string code, string name)
    {
        if (_definition is null) return null;
        try { return ConvertLocation(_definition.Invoke(null, [code, name])); }
        catch { return null; }
    }

    public IReadOnlyList<LangRuleLocation> FindReferences(string code, string name)
    {
        if (_references is null) return [];
        try { return ConvertRecords<LangRuleLocation>(_references.Invoke(null, [code, name]), (x, t) => new(t("Name"), t("Kind"), i(x, "Start"), i(x, "Length"), b(x, "IsDeclaration"))); }
        catch { return []; }
    }

    public LangRuleHover? GetHoverInfo(string code, string name)
    {
        if (_hover is null) return null;
        try
        {
            var value = _hover.Invoke(null, [code, name]);
            if (value is null) return null;
            return new LangRuleHover(Text(value, "Contents"), i(value, "Start"), i(value, "Length"));
        }
        catch { return null; }
    }

    public LangRuleSignatureHelp? GetSignatureHelp(string code, string name)
    {
        if (_signature is null) return null;
        try
        {
            var value = _signature.Invoke(null, [code, name]);
            if (value is null) return null;
            return new LangRuleSignatureHelp(Text(value, "Label"), Text(value, "Documentation"), i(value, "ActiveParameter"));
        }
        catch { return null; }
    }

    public IReadOnlyList<LangRuleCodeAction> GetCodeActions(string code)
    {
        if (_codeActions is null) return [];
        try { return ConvertRecords<LangRuleCodeAction>(_codeActions.Invoke(null, [code]), (x, t) => new(t("Title"), t("Kind"), i(x, "Start"), i(x, "Length"), t("NewText"), t("DiagnosticCode"))); }
        catch { return []; }
    }

    public string? FormatDocument(string code)
    {
        if (_formatter is null) return null;
        try { return _formatter.Invoke(null, [code])?.ToString(); }
        catch { return null; }
    }

    public IReadOnlyList<LangRuleEmbeddedRegion> GetEmbeddedRegions(string code)
    {
        if (_embeddedRegions is null) return [];
        try { return ConvertRecords<LangRuleEmbeddedRegion>(_embeddedRegions.Invoke(null, [code]), (x, t) => new(t("LanguageId"), i(x, "Start"), i(x, "Length"))); }
        catch { return []; }
    }

    public IReadOnlyDictionary<string, Regex> GetRegexes()
    {
        if (_regexes is null) return new Dictionary<string, Regex>();
        try { return ((_regexes.Invoke(null, null) as System.Collections.IDictionary)?.Cast<System.Collections.DictionaryEntry>().Where(e => e.Value is Regex).ToDictionary(e => Convert.ToString(e.Key)!, e => (Regex)e.Value!) ?? new()); }
        catch { return new Dictionary<string, Regex>(); }
    }

    public IReadOnlyDictionary<string, string> GetColors()
    {
        if (_colors is null) return new Dictionary<string, string>();
        try { return ((_colors.Invoke(null, null) as System.Collections.IDictionary)?.Cast<System.Collections.DictionaryEntry>().Where(e => e.Key is not null && e.Value is not null).ToDictionary(e => e.Key!.ToString()!, e => e.Value!.ToString()!) ?? new()); }
        catch { return new Dictionary<string, string>(); }
    }

    private static IReadOnlyList<LangRuleToken> ConvertTokens(object? value) => ConvertRecords<LangRuleToken>(value, (x, t) => new(t("Kind"), t("Text"), i(x, "Start"), i(x, "Length"), t("Scope"), t("Color")));
    private static IReadOnlyList<LangRuleSymbol> ConvertSymbols(object? value) => ConvertRecords<LangRuleSymbol>(value, (x, t) => new(t("Name"), t("Kind"), i(x, "Line"), i(x, "Start"), i(x, "Length"), b(x, "IsDeclaration")));
    private static IReadOnlyList<LangRuleDiagnostic> ConvertDiagnostics(object? value) => ConvertRecords<LangRuleDiagnostic>(value, (x, t) => new(
        i(x, "Start"),
        i(x, "Length"),
        t("Message"),
        string.IsNullOrWhiteSpace(t("Severity")) ? "error" : t("Severity"),
        t("Code"),
        t("Source")));
    private static IReadOnlyList<T> ConvertRecords<T>(object? value, Func<object, Func<string, string>, T> factory)
    {
        if (value is not System.Collections.IEnumerable items) return [];
        var result = new List<T>();
        foreach (var item in items)
        {
            if (item is null) continue;
            string Text(string name) => item.GetType().GetProperty(name)?.GetValue(item)?.ToString() ?? "";
            result.Add(factory(item, Text));
        }
        return result;
    }
    private static IReadOnlyList<string> ConvertStrings(object? value) => value is System.Collections.IEnumerable items
        ? items.Cast<object>().Select(Convert.ToString).Where(x => x is not null).Cast<string>().ToArray()
        : [];
    private static LangRuleLocation? ConvertLocation(object? value) => value is null ? null : new(Text(value, "Name"), Text(value, "Kind"), i(value, "Start"), i(value, "Length"), b(value, "IsDeclaration"));
    private static string Text(object x, string n) => x.GetType().GetProperty(n)?.GetValue(x)?.ToString() ?? "";
    private static int i(object x, string n) => Convert.ToInt32(x.GetType().GetProperty(n)?.GetValue(x) ?? 0);
    private static bool b(object x, string n) => Convert.ToBoolean(x.GetType().GetProperty(n)?.GetValue(x) ?? false);
}

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
