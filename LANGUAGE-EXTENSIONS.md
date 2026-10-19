# Language extensions

A language extension teaches Kodo about a file type. It always does two things:

1. **Claims file extensions**, so Kodo knows a `.myl` file is a MyLang file.
2. **Provides one engine**, so Kodo knows what to do with it.

Pick exactly one engine. This guide covers all three in full.

| Engine | Use when | You write | Needs a server? |
|---|---|---|---|
| [Legacy](#1-legacy) | You only need syntax highlighting | `language.json` | No |
| [LangRules](#2-langrules) | A community or smaller language that has no server | A .NET assembly | No |
| [LSP](#3-lsp) | An established language that has a real server | Manifest JSON | Yes |

Every language package needs a `manifest.json`:

```json
{
  "id": "mylang-kodo-extension",
  "version": "1.0.0",
  "name": "MyLang",
  "type": "language",
  "author": "Your Name",
  "description": "Support for MyLang files.",
  "extensions": [".myl"]
}
```

`extensions` entries **must include the leading dot**. Every other `manifest.json` field is
covered in [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md).

---

## Choosing an engine

**Start with Legacy.** If `language.json` gives you the highlighting you want, you are done. It is
pure data, has no build step, cannot crash the editor, and updates instantly when you edit the
file.

**Move to LangRules** when highlighting is not enough - you want completions, hover, or navigation
- and no language server exists for your language. Be honest about the cost before you start:

- You are writing a parser. Every edge case in the language is your problem.
- Your code runs in Kodo's process. An infinite loop or runaway allocation in a provider will hang
  or crash the editor for the user.
- You must handle incomplete input gracefully. Users type half a token constantly; every method
  will be called on broken code, on every keystroke.
- Nothing validates your output. A range outside the document, or a start/length pair that
  overflows, produces visibly wrong behaviour.

**Move to LSP** whenever a server exists. You write configuration, not a parser. The server is
maintained by the language's community, and you get correctness for free.

If a language has a server, use LSP. Do not reimplement a parser that already exists.

---

## 1. Legacy

Legacy packages contain no code at all - just `language.json`. Kodo's built-in tokenizers read it
and color your file. This is the whole engine.

Shipped examples of this shape: **Shine**, HolyC, KSP-Module-Manager-Config.

### Legacy walkthrough

Create a folder with `manifest.json` (above) and `language.json`:

```json
{
  "extensions": [".myl"],
  "keywords": ["if", "else", "while", "for", "return", "func", "var", "const"],
  "types": ["int", "float", "string", "bool", "MyClass"],
  "functions": ["print", "len", "push"],
  "properties": [],
  "namespaces": [],
  "commentLine": "#",
  "stringDelimiters": ["\""],
  "colorTokens": {
    "keyword": "#569CD6",
    "type": "#4EC9B0",
    "function": "#DCDCAA",
    "string": "#CE9178",
    "comment": "#6A9955"
  }
}
```

Drop the folder into `%LocalAppData%\Kodo\Extensions\`. Kodo picks up a new folder or a new `.kox`
without a restart, but **editing `language.json` in place does not reload it** - the folder watcher
only reacts to created, deleted, and renamed files, and it ignores `language.json` by name. Use the
Extensions page's refresh button, or restart Kodo, to see colour edits.

### `language.json` reference

Every field is optional.

**Token lists** - each is an array of strings, matched on word boundaries, so `"int"` will not
match inside `"integer"`.

| Field | Colors as | Notes |
|---|---|---|
| `keywords` | `keyword` | Reserved words |
| `types` | `type` | Built-in and user type names |
| `functions` | `function` | Function and method names |
| `properties` | `property` | Property and field names |
| `namespaces` | `namespace` | Namespace and module names |

**Lexical structure**

| Field | Default | Meaning |
|---|---|---|
| `commentLine` | `"//"` | Line comment prefix, e.g. `"#"` |
| `commentBlockStart` | `"/*"` | Block comment opener |
| `commentBlockEnd` | `"*/"` | Block comment closer |
| `stringDelimiters` | `["\"", "'"]` | Single-line string delimiters. A span ends at the closing delimiter or end of line. |
| `multiLineStringDelimiters` | `[]` | Delimiters that span lines, e.g. `["\"\"\""]`. **List longer delimiters first.** |
| `disableSingleQuoteStrings` | `false` | `true` stops `'…'` being treated as a string. Use for C#, where `'` also appears as a character literal and in generics. |

**Insight tuning**

| Field | Meaning |
|---|---|
| `blacklist` | Names suppressed from autocomplete inside a call to that same name. Case-insensitive. |
| `deadCodeIgnore` | Names the dead-code scanner must never flag, e.g. `__init__`, `self`. |
| `deadCodeEntryPoints` | Extra implicit entry points beyond the built-in `main` / `WinMain`. |

**`colorTokens`** overrides highlighting colors. Omit a key to keep the default.

| Key | Typical use |
|---|---|
| `keyword` | Reserved words |
| `type` | Type names |
| `string` | String literals |
| `charLiteral` | Character literals |
| `comment` | Comments |
| `number` | Numeric literals |
| `operator` | Operators |
| `punctuation` | Punctuation |
| `function` | Function names |
| `property` | Property names |
| `namespace` | Namespaces |
| `attribute` | Attributes/annotations |
| `preprocessor` | Preprocessor directives |
| `variable` | Variables |

```json
"colorTokens": {
  "keyword": "#569CD6",      "type": "#4EC9B0",
  "string": "#CE9178",       "comment": "#6A9955",
  "number": "#B5CEA8",       "operator": "#D4D4D4",
  "punctuation": "#D4D4D4",  "function": "#DCDCAA",
  "property": "#9CDCFE",     "namespace": "#4FC1FF",
  "attribute": "#C586C0",    "preprocessor": "#C586C0",
  "variable": "#A0DBFD",     "charLiteral": "#CE9178"
}
```

### Several file profiles in one package

A package can carry up to **six** profiles: `language.json` plus `language1.json` through
`language5.json`. Each is parsed independently.

- If a profile's own `extensions` array is **non-empty**, that profile is **scoped** to just those
  file extensions.
- If it is **absent or empty**, the profile is **merged** into the base language.

This is how one package covers several related file types. A real example is the C# package: its
`language.json` handles `.cs`/`.csx`, and its `language2.json` scopes `.csproj`, `.props`,
`.targets` and friends with completely different comment delimiters (`<!-- -->`) and its own color
map.

```json
// language2.json - scoped to project files only
{
  "extensions": [".csproj", ".props", ".targets"],
  "keywords": ["Project", "PropertyGroup", "ItemGroup", "Target", "PackageReference"],
  "commentBlockStart": "<!--",
  "commentBlockEnd": "-->"
}
```

---

## 2. LangRules

LangRules is for a language that has **no language server** - typically a community or
smaller language. You write a .NET assembly; Kodo loads it and calls into it.

Shipped examples: Python, Rust, TOML, SQL, Bash, Batch, JSON, Markdown, PowerShell, and others.

### How the assembly is found

Kodo reads the **`plugin`** field of your `manifest.json` to find the assembly:

```json
{
  "id": "mylang-kodo-extension",
  "version": "1.0.0",
  "name": "MyLang",
  "type": "language",
  "author": "Your Name",
  "description": "Support for MyLang files.",
  "extensions": [".myl"],
  "plugin": "LangRules.dll"
}
```

This trips people up, so state it plainly: **`plugin` is the field that binds LangRules**, not
`languagePlugin`. Every shipped LangRules package uses `plugin`. See
[`plugin` vs `languagePlugin`](KODO-FILE-FORMAT.md#plugin-vs-languageplugin).

Kodo loads that one assembly and looks for a type implementing `ILangRulesProvider`. The same
assembly is also scanned for `IKodoPlugin` implementations, so a single DLL can provide both a
LangRules engine and host UI integration.

> **Development note:** the LangRules assembly is loaded into a **non-collectible**
> `AssemblyLoadContext`. Replacing or rebuilding your DLL requires restarting Kodo - live reload
> does not apply to it. Plan your edit/build/restart loop accordingly.

### The contract

Kodo exposes the contract in the main assembly. Reference it from your project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>LangRules</AssemblyName>
    <RootNamespace>MyLangKodoExtension</RootNamespace>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Kodo">
      <HintPath>..\..\Kodo\Kodo\Source\bin\Debug\net10.0\Kodo.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

`TargetFramework` **must** be `net10.0`, matching Kodo. `Private=false` stops a stale copy of
`Kodo.dll` being shipped inside your package. Build Kodo once (`dotnet build
Kodo/Source/Kodo.csproj`) so the reference resolves.

### Discovery requirements

Kodo finds your provider by reflection. It must be:

- `public`
- not `abstract`
- implement `Kodo.ILangRulesProvider`
- have a **public parameterless constructor**

If any of these fail, the assembly loads and the provider is silently ignored.

### Declaring capabilities

`LangRulesCapability` is a `[Flags]` enum. Declare exactly what you implement - a flag you claim
but do not honour produces an empty or misleading command.

```csharp
public LangRulesProviderInfo Info { get; } = new(
    "MyLang", "1.0.0",
    LangRulesCapability.Tokens |
    LangRulesCapability.Diagnostics |
    LangRulesCapability.Completions);
```

> **The flags are descriptive.** Kodo derives its own capability set from the methods your assembly
> actually exposes, and uses that to decide which menu entries to offer. Declaring a flag you did not
> implement will not turn a feature on, and omitting one you did implement will not turn it off. Keep
> them accurate because they are reported to the UI, not because they gate anything.

| Flag | Enables | You implement |
|---|---|---|
| `Tokens` | Syntax coloring | `Tokenize` |
| `Symbols` | Declaration discovery (not an outline) | `AnalyzeSymbols` |
| `Diagnostics` | Syntax errors | `AnalyzeSyntax` |
| `SemanticAnalysis` | Semantic errors | `AnalyzeSemantics` |
| `Completions` | Suggestions | `GetCompletions` |
| `Definition` | Go to definition | `FindDefinition` |
| `References` | Find references | `FindReferences` |
| `Hover` | Information tooltip | `GetHover` |
| `SignatureHelp` | Parameter help | `GetSignatureHelp` |
| `CodeActions` | Quick fixes | `GetCodeActions` |
| `Formatting` | Document formatting | `FormatDocument` |
| `EmbeddedRegions` | Nested languages | `GetEmbeddedRegions` |

Every method on the interface has a **default implementation** that returns empty or `null`, so Kodo
offers a feature only when your type actually overrides the matching method - an unoverridden member
is never advertised.

Every method on the interface has a **default implementation** that returns empty or `null`. You
only override what you claim. `None` means no optional feature.

Add one flag at a time, and only after the matching method works on incomplete input.

### Positions and ranges

- All offsets are **zero-based UTF-16** indices into the complete `code` string you are given.
- Ranges are a `Start` plus a `Length`. `Start + Length` must stay within the string.
- `Line` is zero-based.
- Out-of-range values are filtered rather than crashing, but they make diagnostics silently
  disappear - clamp defensively.

### The result types

```csharp
new LangRuleToken(Kind, Text, Start, Length, Scope, Color)
new LangRuleSymbol(Name, Kind, Line, Start, Length, IsDeclaration)
new LangRuleDiagnostic(Start, Length, Message, Severity, Code, Source)
new LangRuleLocation(Name, Kind, Start, Length, IsDeclaration)
new LangRuleHover(Contents, Start, Length)
new LangRuleSignatureHelp(Label, Documentation, ActiveParameter)
new LangRuleCodeAction(Title, Kind, Start, Length, NewText, DiagnosticCode)
new LangRuleEmbeddedRegion(LanguageId, Start, Length)
```

| Type | Field notes |
|---|---|
| `LangRuleToken` | `Kind` is a token class such as `keyword`; `Color` is a hex string. |
| `LangRuleSymbol` | `IsDeclaration` marks the defining occurrence. |
| `LangRuleDiagnostic` | `Severity` is user-facing (`"error"`, `"warning"`, `"info"`, `"hint"`); `Code` is a stable machine-readable id a code action can match; `Source` identifies your provider. |
| `LangRuleLocation` | `IsDeclaration` distinguishes the definition from uses. |
| `LangRuleHover` | Put the signature before longer prose. |
| `LangRuleSignatureHelp` | `ActiveParameter` is a zero-based index. |
| `LangRuleCodeAction` | `NewText` replaces the returned range. `DiagnosticCode` links the action to a diagnostic. |
| `LangRuleEmbeddedRegion` | Return only the **inner** region belonging to the embedded language. |

### A complete minimal provider

```csharp
using System;
using System.Collections.Generic;
using Kodo;

namespace MyLangKodoExtension;

public sealed class MyLangProvider : ILangRulesProvider
{
    public LangRulesProviderInfo Info { get; } = new(
        "MyLang", "1.0.0",
        LangRulesCapability.Tokens |
        LangRulesCapability.Diagnostics |
        LangRulesCapability.Completions);

    public IReadOnlyList<LangRuleToken> Tokenize(string code) => Array.Empty<LangRuleToken>();

    public IReadOnlyList<LangRuleDiagnostic> AnalyzeSyntax(string code)
    {
        var result = new List<LangRuleDiagnostic>();
        var open = 0;
        foreach (var c in code) { if (c == '(') open++; else if (c == ')') open--; }

        if (open > 0)
            result.Add(new LangRuleDiagnostic(
                Math.Max(0, code.Length - 1), 1,
                "A closing parenthesis is missing.", "error", "MY001", "MyLang"));

        return result;
    }

    public IReadOnlyList<string> GetCompletions(string code, int offset, string prefix)
    {
        var words = new[] { "if", "else", "return", "print", "func" };
        var result = new List<string>();
        foreach (var w in words)
            if (w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(w);
        return result;
    }
}
```

`prefix` is what the user has already typed. Do not include it again in your results, and keep
the list short.

### Worked examples

**Identifier scanning helper** - most providers need this:

```csharp
private static string WordAt(string code, int offset)
{
    offset = Math.Clamp(offset, 0, code.Length);
    var start = offset;
    while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) start--;
    var end = offset;
    while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
    return code[start..end];
}
```

**Definition and references:**

```csharp
public LangRuleLocation? FindDefinition(string code, int offset)
{
    if (WordAt(code, offset) != "print") return null;
    var start = code.IndexOf("print", StringComparison.Ordinal);
    return new LangRuleLocation("print", "function", start, 5, true);
}

public IReadOnlyList<LangRuleLocation> FindReferences(string code, int offset)
{
    var result = new List<LangRuleLocation>();
    for (var start = code.IndexOf("print", StringComparison.Ordinal);
         start >= 0;
         start = code.IndexOf("print", start + 1, StringComparison.Ordinal))
        result.Add(new LangRuleLocation("print", "function", start, 5, start == 0));
    return result;
}
```

**Hover:**

```csharp
public LangRuleHover? GetHover(string code, int offset)
{
    if (WordAt(code, offset) != "print") return null;
    var start = code.LastIndexOf("print", Math.Clamp(offset, 0, code.Length), StringComparison.Ordinal);
    return new LangRuleHover("`print(value)` writes a value to the output.", start, 5);
}
```

**Signature help:**

```csharp
public LangRuleSignatureHelp? GetSignatureHelp(string code, int offset)
{
    var upto = code[..Math.Clamp(offset, 0, code.Length)].TrimEnd();
    if (!upto.EndsWith("print(", StringComparison.Ordinal)) return null;
    return new LangRuleSignatureHelp("print(value)", "Writes value to the output.", 0);
}
```

For multiple parameters, scan backwards tracking parenthesis depth and count only top-level
commas to compute `ActiveParameter`.

**Code action:**

```csharp
public IReadOnlyList<LangRuleCodeAction> GetCodeActions(string code, int offset)
{
    if (!code.Contains("pritn", StringComparison.Ordinal)) return Array.Empty<LangRuleCodeAction>();
    var start = code.IndexOf("pritn", StringComparison.Ordinal);
    return new[] { new LangRuleCodeAction(
        "Change 'pritn' to 'print'", "quickfix", start, 5, "print", "MY001") };
}
```

Return only fixes that are safe and local. The `Title` is what the user sees.

**Formatting - read this before you write it:**

```csharp
public string? FormatDocument(string code)
{
    // Preserve the document's existing line endings. Normalising CRLF to LF
    // here rewrites every line in the file and produces an enormous diff.
    var newline = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    return string.Join(newline, Array.ConvertAll(lines, l => l.TrimEnd()));
}
```

Formatting must be **idempotent** - running it twice should not keep changing the file. Real
formatters need a parser: preserve comments and string contents, and never reflow inside a
literal.

**Embedded regions:**

```csharp
public IReadOnlyList<LangRuleEmbeddedRegion> GetEmbeddedRegions(string code)
{
    var start = code.IndexOf("<style>", StringComparison.Ordinal);
    var end = code.IndexOf("</style>", StringComparison.Ordinal);
    if (start < 0 || end <= start) return Array.Empty<LangRuleEmbeddedRegion>();
    start += "<style>".Length;
    return new[] { new LangRuleEmbeddedRegion("css", start, end - start) };
}
```

Return only the inner region. Never overlap regions or return an unterminated one.

### The `RecoveryAnalyzer` convention

Shipped language packages commonly include a `RecoveryAnalyzer` class alongside the provider. It
is not part of Kodo's API - it is a convention for the *syntax recovery* pass: cheap, dependency-free
checks that keep the editor useful while code is half-written.

A recovery analyzer reports structural problems like unbalanced braces or brackets, unterminated
strings, a missing semicolon after a directive, and unreachable code after a terminator.

```csharp
internal static class RecoveryAnalyzer
{
    public static IReadOnlyList<LangRuleDiagnostic> Analyze(string code)
    {
        var result = new List<LangRuleDiagnostic>();
        var depth = 0;
        var offset = 0;
        foreach (var c in code)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
            offset++;
        }
        if (depth > 0)
            result.Add(new LangRuleDiagnostic(
                Math.Max(0, code.Length - 1), 1,
                "Unclosed brace.", "error", "MY002", "MyLang"));
        return result;
    }
}
```

Call it from your `AnalyzeSyntax`. Keep it string- and comment-aware, or it will report errors
inside string literals.

### Maintaining a pre-2.1.0 extension

Before the typed contract existed, providers were a `public static class LangRules` discovered by
name, with methods bound by reflection. Kodo still supports this, and several shipped packages use
it. If you are maintaining one, the recognised method names are:

`Tokenize`, `AnalyzeSymbols`, `GetCompletionsForPrefix`, `GetAllRegexes`, `GetColorMap`,
`AnalyzeSyntax`, `AnalyzeSemantics`, `FindDefinition`, `FindReferences`, `GetHoverInfo`,
`GetSignatureHelp`, `GetCodeActions`, `FormatDocument`, `GetEmbeddedRegions`, `FindDefinitionAt`,
`FindReferencesAt`, `GetHoverInfoAt`, `GetCompletionsAt`.

Note the naming difference from the typed interface: `GetHoverInfo` / `GetHoverInfoAt` rather
than `GetHover`. `GetAllRegexes` and `GetColorMap` have **no typed equivalent**.

**Kodo checks for a static `LangRules` class first and only falls back to
`ILangRulesProvider`.** If your assembly contains both, the static class wins and your typed
provider is never called. For new work, ship only `ILangRulesProvider`.

Capabilities are **derived automatically** for the static-class form from which methods were
found, so there is nothing to declare. With `ILangRulesProvider` you declare them yourself.

### Packaging a LangRules extension

```text
mylang-kodo-extension/
├── manifest.json
├── language.json          ← still useful for the base profile
├── LangRules.cs           ← ship your source alongside the DLL
├── MyLangProvider.cs
├── RecoveryAnalyzer.cs
├── LangRules.dll
└── icon.png
```

Shipping the `.cs` files next to the DLL is the convention in published packages, and it makes
the package reviewable. It costs nothing.

```powershell
Compress-Archive -Path .\mylang-kodo-extension\* -DestinationPath .\mylang-1.0.0.zip
Rename-Item .\mylang-1.0.0.zip .\mylang-1.0.0.kox
```

Never include `Kodo.dll` in the package. Kodo provides it.
---

## 3. LSP

LSP is for established languages that have a real language server. You write configuration; the
server does the parsing. This is by far the least code and the most correct option when it is
available.

All fields live in `manifest.json`. The complete field and alias reference is in
[KODO-FILE-FORMAT.md#lsp--lsps](KODO-FILE-FORMAT.md#lsp--lsps); this section covers choosing and
testing one.

### Minimal configuration

```json
{
  "id": "mylang-lsp",
  "version": "1.0.0",
  "name": "MyLang",
  "type": "language",
  "author": "Your Name",
  "description": "MyLang support powered by a language server.",
  "extensions": [".myl"],
  "lsp": {
    "command": "mylang-language-server",
    "args": ["--stdio"],
    "languages": ["mylang"],
    "fileExtensions": [".myl"],
    "providerId": "mylang-ls",
    "displayName": "MyLang Language Server"
  }
}
```

That is enough for a server already on `PATH`. To let Kodo install it, add install metadata:

```json
"lsp": {
  "command": "mylang-language-server",
  "languages": ["mylang"],
  "providerId": "mylang-ls",
  "installMethod": "npm",
  "packageName": "mylang-language-server",
  "allowSystem": true,
  "allowAutoInstall": true
}
```

| `installMethod` | Kodo runs | Use for |
|---|---|---|
| `npm` | `npm install --prefix <staging> <packageName>` with `--ignore-scripts`, then promoted into Kodo's Lsp folder | Most JS/TS servers |
| `dotnet` | `dotnet tool install --global <packageName>` | .NET servers |
| `download` | fetches `downloadUrl` directly, unpacking `.zip`/`.tar.gz` | Self-contained artifacts |
| `github` | like `download`, but resolves GitHub asset/release URLs | Servers hosted on GitHub releases |
| `manual` | nothing; Kodo never installs it | Toolchains the user installs by hand |
| *(omit)* | nothing; expects the server on `PATH` | System-provided servers |

npm installs are **not global** - Kodo stages them into a private prefix and promotes the result into
`%LocalAppData%\Kodo\Lsp`, so nothing is written to a machine-wide npm root.

`allowSystem: true` lets Kodo use a server already installed. `allowAutoInstall: true` lets Kodo
install without prompting. Add `sha256` to verify a download install.

> **`version` does not pin the install.** It is not read by the npm or dotnet installers; it is only
> used when checking whether an already-installed server satisfies a minimum. To pin an npm or dotnet
> server, put the version in `packageName` (for example `typescript-language-server@5.0.0`) or gate it
> with `allowAutoInstall: false` and ship your own wrapper.

### Cross-platform commands

One `command` value is used on **every** platform:

- Prefer an **extensionless** name: `mylang-language-server`, not `mylang-language-server.exe`.
- `.cmd` and `.bat` suffixes are stripped when spawning on Linux. `.exe` is stripped during the
  `PATH` lookup, so an extensionless binary next to a `.exe` of the same name resolves correctly.
- Kodo does **not** rewrite `python` → `python3` or `powershell` → `pwsh` for language servers. That
  mapping applies to a compiler's run/build commands and to `externalTools`, but a server's `command`
  is launched as written. If your server is a script, name the interpreter explicitly
  (`command: "python3"`, and use `runtimeArgs` if it needs them).

If a server genuinely needs different binaries per OS, publish **two `.kox` files** and gate them
with the marketplace `os` field (`["windows"]` and `["linux"]`). Do not try to encode conditionals
in `command`.

### Several servers, or several file families

Use `lsps` when one package covers languages with different servers, or one language whose file
types are handled by different servers:

```json
"lsps": [
  {
    "command": "html-language-server",
    "languages": ["html"],
    "fileExtensions": [".html", ".htm"]
  },
  {
    "command": "css-language-server",
    "languages": ["css"],
    "fileExtensions": [".css", ".scss"]
  }
]
```

Each entry needs its own `command`. Top-level `extensions` act as a fallback when no entry claims
a file. `languageServers` is accepted as an alias for `lsps`; `lsp` (singular) is the older
single-server form and both are supported.

### Workspace roots

Servers expect a workspace root, not a bare file path. Give Kodo markers to search for:

```json
"rootMarkers": ["package.json", "tsconfig.json", ".git"]
```

Kodo walks up from the file looking for the first directory containing one. If none is found it
falls back to the enclosing open folder, then the file's own directory.

### Feature mapping

Advertise each capability during `initialize` and implement the matching request. Kodo maps them
as follows:

| Kodo feature | LSP request / notification |
|---|---|
| Completion | `textDocument/completion` |
| Hover | `textDocument/hover` |
| Diagnostics | `textDocument/publishDiagnostics` (after `didOpen` / `didChange`) |
| Go to definition | `textDocument/definition` |
| Type definition | `textDocument/typeDefinition` |
| Implementations | `textDocument/implementation` |
| Find references | `textDocument/references` |
| Rename | `textDocument/rename` |
| Formatting | `textDocument/formatting` |
| Code actions | `textDocument/codeAction` |
| Signature help | `textDocument/signatureHelp` |
| Document outline | `textDocument/documentSymbol` |
| Folding | `textDocument/foldingRange` |
| Semantic tokens | `textDocument/semanticTokens/full` |
| Inlay hints | `textDocument/inlayHint` |
| Document highlights | `textDocument/documentHighlight` |

### Rules your server must follow

These are not stylistic. Kodo enforces them.

- **Use zero-based UTF-16 line/character positions.** UTF-16, not bytes and not code points.
- **Clear diagnostics by publishing an empty array.** Stale errors linger otherwise.
- **Never return an edit computed from a stale document version.** Kodo rejects formatting and
  rename results if the document changed while the request was in flight, and the user sees nothing
  happen.
- **Honour cancellation.** Kodo sends `$/cancelRequest`; ignoring it causes flicker.
- **Return `null` or an empty result when a feature does not apply.** Do not invent a plausible
  answer.
- **Format defensively.** Return the smallest safe edit; do not reflow the whole document.

### Testing an LSP extension

1. **Run the command manually first.** `mylang-language-server --stdio`, then send it a
   `Content-Length` framed `initialize` request. If it does not start here, it will not start in
   Kodo.
2. Install the package and open a file of the target type.
3. Confirm Kodo reports the server as running, and that the **language id** sent in `didOpen`
   matches what your server expects. A mismatch is the most common cause of "starts but has no
   features".
4. Check diagnostics appear, then completion, then hover.
5. Only then add advanced features.

If the server starts but nothing works, the language id and workspace root are the first two things
to check. Kodo traces the whole exchange to its own log when verbose logging is enabled; the
tracing lives in Kodo's internal `KodoDiagnostics.LogDebug`, which is `internal` and so is **not**
callable from your extension. You get the effect by turning verbose logging on and reading Kodo's
log file.

---

## Troubleshooting language extensions

| Symptom | Check first | Then check |
|---|---|---|
| Language never appears in the list | `manifest.json` is at the package root | `type` is `language`; `extensions` includes the leading dot |
| Colors are wrong or absent | `language.json` is valid JSON and beside the manifest | `colorTokens` keys are spelled correctly; Kodo is not falling back to defaults |
| One file type is colored, another is not | the profile's `extensions` array | a non-empty array scopes the profile; empty merges it |
| LangRules features missing | `plugin` names your DLL, **not** `languagePlugin` | the type is non-abstract and has a public parameterless constructor (`public` on the type is not required); the method is actually overridden rather than left as the interface default |
| A feature is offered but returns nothing | the method is implemented, not just declared | ranges are within the document |
| LangRules changes have no effect | restart Kodo | the LangRules assembly is loaded non-collectibly and is never hot-reloaded |
| Signature help never appears | - | `GetSignatureHelp` has no UI entry point in Kodo; signature help is LSP-only |
| Document outline is empty | - | the outline is LSP-only; `AnalyzeSymbols` feeds declaration discovery, not an outline |
| `language.json` edits do not appear | use the Extensions page refresh, or restart | the folder watcher ignores in-place edits and ignores `language.json` by name |
| LSP starts but has no features | the language id in `languages` | `fileExtensions`; workspace root via `rootMarkers` |
| LSP never starts | run the command manually | `allowSystem`; the server is installed under `%LocalAppData%` for managed installs |
| The wrong theme colors apply to dialogs | not applicable | theme and accent are read from settings, not from your package |
| Plugin listed but does nothing | `plugin` DLL name matches exactly | target framework is `net10.0`; the DLL loads as a plugin too and may need `IKodoPlugin` |

---

## Related guides

- [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) - every `manifest.json` field, packaging, marketplace
- [THEME-EXTENSIONS.md](THEME-EXTENSIONS.md) - syntax colors can be overridden per theme
- [PLUGINS.md](PLUGINS.md) - add host UI to a language package
- [COMPILERS.md](COMPILERS.md) - run and build the language
- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) - which kind of extension to build
