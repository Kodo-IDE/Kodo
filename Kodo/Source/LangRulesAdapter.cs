using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Kodo;

public sealed class LangRulesAdapter
{
    private readonly LanguageWorker _worker = new();
    private readonly Type _rules;
    private readonly AssemblyLoadContext _loadContext;
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
    private readonly MethodInfo? _positionDefinition;
    private readonly MethodInfo? _positionReferences;
    private readonly MethodInfo? _positionHover;
    private readonly MethodInfo? _positionCompletions;
    private readonly ILangRulesProvider? _typedProvider;

    private LangRulesAdapter(Type rules, AssemblyLoadContext loadContext, ILangRulesProvider? typedProvider = null)
    {
        _rules = rules;
        _loadContext = loadContext;
        _typedProvider = typedProvider;
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
        _positionDefinition = rules.GetMethod("FindDefinitionAt", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(int)]);
        _positionReferences = rules.GetMethod("FindReferencesAt", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(int)]);
        _positionHover = rules.GetMethod("GetHoverInfoAt", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(int)]);
        _positionCompletions = rules.GetMethod("GetCompletionsAt", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(int)]);
    }

    public static LangRulesAdapter? TryLoad(string? folder, string? assemblyFile)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(assemblyFile)) return null;
        var path = Path.Combine(folder, assemblyFile);
        if (!File.Exists(path)) return null;
        try
        {
            var loadContext = new AssemblyLoadContext($"Kodo.LangRules.{Guid.NewGuid():N}", isCollectible: false);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));
            var rules = assembly.GetTypes().FirstOrDefault(t => t.IsAbstract && t.IsSealed && t.Name == "LangRules");
            if (rules is not null) return new LangRulesAdapter(rules, loadContext);
            var providerType = assembly.GetTypes().FirstOrDefault(t => !t.IsAbstract && typeof(ILangRulesProvider).IsAssignableFrom(t));
            if (providerType is null || Activator.CreateInstance(providerType) is not ILangRulesProvider provider) return null;
            return new LangRulesAdapter(providerType, loadContext, provider);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LangRules load failed: path={path}; {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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
    public bool HasPositionNavigation => _positionDefinition is not null;
    public bool HasPositionCompletions => _positionCompletions is not null;
    public IReadOnlyList<string> ValidationWarnings => DiscoverValidationWarnings();

    public LangRulesProviderInfo ProviderInfo => _typedProvider?.Info ?? new(
        _rules.Assembly.GetName().Name ?? _rules.Name,
        _rules.Assembly.GetName().Version?.ToString() ?? "0.0.0",
        (HasTokenizer ? LangRulesCapability.Tokens : LangRulesCapability.None) |
        (HasSymbolAnalyzer ? LangRulesCapability.Symbols : LangRulesCapability.None) |
        (HasDiagnostics ? LangRulesCapability.Diagnostics : LangRulesCapability.None) |
        (HasSemanticAnalyzer ? LangRulesCapability.SemanticAnalysis : LangRulesCapability.None) |
        (HasPositionCompletions || _completions is not null ? LangRulesCapability.Completions : LangRulesCapability.None) |
        (HasPositionNavigation || _definition is not null ? LangRulesCapability.Definition : LangRulesCapability.None) |
        (_positionReferences is not null || _references is not null ? LangRulesCapability.References : LangRulesCapability.None) |
        (HasHoverProvider ? LangRulesCapability.Hover : LangRulesCapability.None) |
        (HasSignatureHelpProvider ? LangRulesCapability.SignatureHelp : LangRulesCapability.None) |
        (HasCodeActionProvider ? LangRulesCapability.CodeActions : LangRulesCapability.None) |
        (HasFormatter ? LangRulesCapability.Formatting : LangRulesCapability.None) |
        (HasEmbeddedRegions ? LangRulesCapability.EmbeddedRegions : LangRulesCapability.None), ValidationWarnings);

    private IReadOnlyList<string> DiscoverValidationWarnings()
    {
        var warnings = new List<string>();
        var signatures = _rules.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var supported = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["Tokenize"] = _tokenize is not null, ["AnalyzeSymbols"] = _symbols is not null,
            ["GetCompletionsForPrefix"] = _completions is not null || _contextCompletions is not null,
            ["GetAllRegexes"] = _regexes is not null, ["GetColorMap"] = _colors is not null,
            ["AnalyzeSyntax"] = _diagnostics is not null, ["AnalyzeSemantics"] = _semantics is not null,
            ["FindDefinition"] = _definition is not null, ["FindReferences"] = _references is not null,
            ["GetHoverInfo"] = _hover is not null, ["GetSignatureHelp"] = _signature is not null,
            ["GetCodeActions"] = _codeActions is not null, ["FormatDocument"] = _formatter is not null,
            ["GetEmbeddedRegions"] = _embeddedRegions is not null, ["FindDefinitionAt"] = _positionDefinition is not null,
            ["FindReferencesAt"] = _positionReferences is not null, ["GetHoverInfoAt"] = _positionHover is not null,
            ["GetCompletionsAt"] = _positionCompletions is not null
        };
        foreach (var pair in supported)
            if (!pair.Value && signatures.ContainsKey(pair.Key))
                warnings.Add($"{pair.Key} was found but its signature is not supported by this Kodo version.");
        return warnings;
    }

    public void OpenDocument(string uri, long version, string text) =>
        _worker.Open(new LanguageDocumentSnapshot(uri, version, text));

    public bool ApplyDocumentChanges(string uri, long version, IReadOnlyList<LanguageTextChange> changes) =>
        _worker.Change(uri, version, changes).Result is LanguageDocumentSnapshot;

    public void CloseDocument(string uri) => _worker.Close(uri);

    public LangRuleLocation? FindDefinitionAt(string code, int offset)
    {
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.FindDefinition(code, offset));
        if (_positionDefinition is null) return null;
        var response = Dispatch("textDocument/definition", code, request => _positionDefinition.Invoke(null, [request.Document.Text, request.Offset]), offset);
        return response.Result is null ? null : ConvertLocation(response.Result);
    }

    public LangRuleHover? GetHoverInfoAt(string code, int offset)
    {
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.GetHover(code, offset));
        if (_positionHover is null) return null;
        var response = Dispatch("textDocument/hover", code, request => _positionHover.Invoke(null, [request.Document.Text, request.Offset]), offset);
        if (response.Result is null) return null;
        return new LangRuleHover(Text(response.Result, "Contents"), i(response.Result, "Start"), i(response.Result, "Length"));
    }

    public IReadOnlyList<LangRuleDiagnostic> AnalyzeSyntax(string code)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.AnalyzeSyntax(code));
        if (_diagnostics is null) return [];
        var response = Dispatch("textDocument/diagnostics", code, request => _diagnostics.Invoke(null, [request.Document.Text]));
        return response.Result is null ? [] : ConvertDiagnostics(response.Result);
    }

    public IReadOnlyList<LangRuleDiagnostic> AnalyzeSemantics(string code)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.AnalyzeSemantics(code));
        if (_semantics is null) return [];
        var response = Dispatch("textDocument/semanticDiagnostics", code, request => _semantics.Invoke(null, [request.Document.Text]));
        return response.Result is null ? [] : ConvertDiagnostics(response.Result);
    }

    public IReadOnlyList<LangRuleToken> Tokenize(string code)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.Tokenize(code));
        if (_tokenize is null) return [];
        try { return ConvertTokens(_tokenize.Invoke(null, [code])); }
        catch { return []; }
    }

    public IReadOnlyList<LangRuleSymbol> AnalyzeSymbols(string code)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.AnalyzeSymbols(code));
        if (_symbols is null) return [];
        var response = Dispatch("textDocument/documentSymbols", code, request => _symbols.Invoke(null, [request.Document.Text]));
        return response.Result is null ? [] : ConvertSymbols(response.Result);
    }

    private static IReadOnlyList<T> SafeTyped<T>(Func<IReadOnlyList<T>> operation)
    {
        try { return operation() ?? Array.Empty<T>(); }
        catch { return Array.Empty<T>(); }
    }

    private LanguageWorkerResponse Dispatch(string method, string code, Func<LanguageWorkerRequest, object?> handler, int offset = 0)
    {
        var request = new LanguageWorkerRequest(method, new("untitled", 0, code), Offset: offset);
        return _worker.Send(request, handler);
    }

    public IEnumerable<string> GetVariableLikeNames(string code)
    {
        foreach (var token in Tokenize(code))
        {
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
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.GetCompletions(code, code.Length, prefix));
        if (_contextCompletions is null) return GetCompletions(prefix);
        try { return ConvertStrings(_contextCompletions.Invoke(null, [prefix, code])); }
        catch { return GetCompletions(prefix); }
    }

    public IReadOnlyList<string> GetCompletionsAt(string code, int offset, string prefix)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.GetCompletions(code, offset, prefix));
        if (_positionCompletions is null) return GetCompletions(prefix, code);
        try { return ConvertStrings(_positionCompletions.Invoke(null, [code, offset])); }
        catch { return GetCompletions(prefix, code); }
    }

    public LangRuleLocation? FindDefinition(string code, string name)
    {
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.FindDefinition(code, Math.Max(0, code.IndexOf(name, StringComparison.Ordinal))));
        if (_definition is null) return null;
        try { return ConvertLocation(_definition.Invoke(null, [code, name])); }
        catch { return null; }
    }

    public IReadOnlyList<LangRuleLocation> FindReferencesAt(string code, int offset)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.FindReferences(code, offset));
        if (_positionReferences is null) return [];
        var response = Dispatch("textDocument/references", code, request => _positionReferences.Invoke(null, [request.Document.Text, request.Offset]), offset);
        return response.Result is null ? [] : ConvertRecords<LangRuleLocation>(response.Result, (x, t) => new(t("Name"), t("Kind"), i(x, "Start"), i(x, "Length"), b(x, "IsDeclaration")));
    }

    public IReadOnlyList<LangRuleLocation> FindReferences(string code, string name)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.FindReferences(code, Math.Max(0, code.IndexOf(name, StringComparison.Ordinal))));
        if (_references is null) return [];
        try { return ConvertRecords<LangRuleLocation>(_references.Invoke(null, [code, name]), (x, t) => new(t("Name"), t("Kind"), i(x, "Start"), i(x, "Length"), b(x, "IsDeclaration"))); }
        catch { return []; }
    }

    public LangRuleHover? GetHoverInfo(string code, string name)
    {
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.GetHover(code, Math.Max(0, code.IndexOf(name, StringComparison.Ordinal))));
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
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.GetSignatureHelp(code, Math.Max(0, code.IndexOf(name, StringComparison.Ordinal))));
        if (_signature is null) return null;
        try
        {
            var value = _signature.Invoke(null, [code, name]);
            if (value is null) return null;
            return new LangRuleSignatureHelp(Text(value, "Label"), Text(value, "Documentation"), i(value, "ActiveParameter"));
        }
        catch { return null; }
    }

    public LangRuleSignatureHelp? GetSignatureHelpAt(string code, int offset)
    {
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.GetSignatureHelp(code, offset));
        return null;
    }

    public IReadOnlyList<LangRuleCodeAction> GetCodeActions(string code)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.GetCodeActions(code, 0));
        if (_codeActions is null) return [];
        try { return ConvertRecords<LangRuleCodeAction>(_codeActions.Invoke(null, [code]), (x, t) => new(t("Title"), t("Kind"), i(x, "Start"), i(x, "Length"), t("NewText"), t("DiagnosticCode"))); }
        catch { return []; }
    }

    public string? FormatDocument(string code)
    {
        if (_typedProvider is not null) return SafeTypedValue(() => _typedProvider.FormatDocument(code));
        if (_formatter is null) return null;
        try { return _formatter.Invoke(null, [code])?.ToString(); }
        catch { return null; }
    }

    public IReadOnlyList<LangRuleEmbeddedRegion> GetEmbeddedRegions(string code)
    {
        if (_typedProvider is not null) return SafeTyped(() => _typedProvider.GetEmbeddedRegions(code));
        if (_embeddedRegions is null) return [];
        try { return ConvertRecords<LangRuleEmbeddedRegion>(_embeddedRegions.Invoke(null, [code]), (x, t) => new(t("LanguageId"), i(x, "Start"), i(x, "Length"))); }
        catch { return []; }
    }

    private static T? SafeTypedValue<T>(Func<T?> operation) where T : class
    {
        try { return operation(); } catch { return null; }
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
