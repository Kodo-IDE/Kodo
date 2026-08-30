# Contributing to Kodo

Thanks for your interest in contributing! Kodo is a small project built by two people, so every contribution genuinely matters. This document covers everything you need to know to get started.

---

## Packages

Kodo uses these NuGet packages:
- `Avalonia` 12.1.1
- `Avalonia.Desktop` 12.1.1
- `Avalonia.AvaloniaEdit` 12.0.0
- `Avalonia.Themes.Fluent` 12.1.1
- `Avalonia.Fonts.Inter` 12.1.1
- `AvaloniaUI.DiagnosticsSupport` 2.2.3
- `DiscordRichPresence` 1.6.1.70
- `Svg.Controls.Skia.Avalonia` 12.0.0.16

---

## Getting Set Up

To run Kodo locally, you'll need:

1. [.NET](https://dotnet.microsoft.com/en-us/download) minimum version 10
2. Run `dotnet new install Avalonia.Templates`
3. Change your directory to Kodo's source folder: `cd Kodo\Source` (from the repository root — e.g. `cd path\to\Kodo\Kodo\Source` if you cloned to `path\to\Kodo`)
4. For best results, run `dotnet build Kodo.csproj` to catch any errors. This is optional.
5. Run `dotnet run` - it'll take a few seconds then open up

That's it. No complicated build pipeline, no extra tools.

> **Plugin authors:** build `Kodo.csproj` once first so `Kodo-Extension-Template/TemplatePlugin` can find `Kodo\Source\bin\Debug\net10.0\Kodo.dll` (`Source\bin\Debug\net10.0\Kodo.dll` from the repo root; `..\..\Kodo\Kodo\Source\bin\Debug\net10.0\Kodo.dll` from `TemplatePlugin` — `HintPath` with `Private=false`).

---

## Rules

1. **AI Integration**
   - **a)** No AI integration in native/default Kodo, full stop.
   - **b)** Extensions may add or use AI, but only where the effect is minimal and scoped. For example: enhancing an existing feature like CodePredict with AI is allowed; adding a general-purpose AI chat interface is not. Acceptance of AI-related extensions is at the maintainer's discretion during PR review, even if a submission technically fits these guidelines.

2. **Code Quality**
   - PRs to the main Kodo app (not extensions) must be high-quality and add clear, meaningful value.
   - Bloat, redundant functionality, or low-value features will be rejected, regardless of code quality. If we need more information about your PR's contents, we'll make an attempt to contact you.

---

## How to Contribute

### Reporting Bugs

If something's broken, open an [Issue](https://github.com/Kodo-IDE/Kodo/issues) and describe:
- What you were doing when it happened
- What you expected to happen
- What actually happened
- Your OS and .NET version if relevant

If you have Aptabase data tracking enabled, slimmer crash logs are sent automatically, with them being sent to us without revealing any personal information. If you'd like to describe the error better, please open an Issue! (Tip: if an error dialog appears, use the Report on GitHub button - this will auto-fill your Issue with all the information we need.)

### Submitting Changes

1. Fork the repo
2. Make your changes on a new branch
3. Open a Pull Request with a clear description of what you changed and why

Please keep PRs focused, one thing per PR makes it much easier to review. If you're planning something large, open an Issue first so we can discuss it before you put in the work.

### Code Style

Look at the existing code and match it. A few things to keep consistent:

- Instead of a per-file changelog comment, make a good PR - a clear title and description of what you changed and why goes a long way for review.
- Keep comments descriptive but not excessive - explain *why*, not just *what*
- Don't leave dead code or commented-out blocks behind

---

## Making Extensions

Extensions are the best way to contribute without touching the core app. Kodo supports three extension types, declared by `type` in `manifest.json`:

| `type` | Purpose | Key files |
|---|---|---|
| `language` | Syntax highlighting, CodePredict, and Insight analysis | `language.json` (+ `language1.json` … `language5.json`) |
| `theme` | Window, editor, and accent colors | `theme.json` (single object or array) |
| `plugin` | Managed code that runs inside Kodo via `IKodoPlugin` | DLL + `manifest.json:plugin` |

Compilers (`.NET SDK`, `Go`, `Shine`, etc.) are not `.kox` extensions - they are standalone installers listed in `Indexs/CompilerIndex.json` and downloaded to `%LocalAppData%\Kodo\Compilers\`.

A `.kox` file is a renamed `.zip`. An unpacked folder with `manifest.json` at its root also works. Both are scanned from `%AppData%\Kodo\Extensions\` (priority) and the repo's `Extensions\` folder used by local/dev builds (live reload via `FileSystemWatcher`, 250 ms debounce). Only `manifest.json` is required for loading - if it's missing, the package is skipped.

```
manifest.json                          ← required
language.json                          ← for language extensions
language1.json ... language5.json      ← optional, additional profiles (scoped by their own extensions array)
theme.json                             ← for theme extensions (single object or array)
icon.png / icon.svg                    ← optional
*.dll                                  ← for plugin extensions (every .dll is extracted)
```

A single `.kox` can bundle up to six language profiles (`language.json` plus `language1.json` through `language5.json`). Each file is parsed independently: if a profile's own `extensions` array is non-empty, it's kept scoped to just those file extensions; otherwise it's merged into the extension's base profile. This lets one package support several related file types (e.g. `.cs` + `.csproj`) without separate extensions.

Icons: `icon.png` is preferred, `icon.svg` is the fallback. If neither local icon loads, the card shows the two-letter abbreviation. If a marketplace `iconUrl` exists it replaces the local icon when fetched.

Versions are compared numerically - `v1.4.0` with a `v` prefix is the convention but `1.0.0` also works; the version in the filename may override `manifest.json:version` if higher.

### manifest.json

Required for all extensions:

```json
{
  "id": "mylang-kodo-extension",
  "version": "v1.0.0",
  "name": "MyLang Language Support",
  "type": "language",
  "author": "Your Name",
  "description": "Syntax highlighting for MyLang files.",
  "extensions": [".myl"]
}
```

The loader defaults missing fields to `""` / `[]` / `null`, so the only hard requirement is valid JSON plus a `manifest.json` file. For marketplace submissions, though, fill in every field that applies to your extension.

| Field | Required | Notes |
|---|---|---|
| `id` | marketplace yes | Unique kebab-case, used for dedup and install tracking. Must match the index on submit. |
| `version` | marketplace yes | Display + update version. `v1.0.0`, `1.4.0`, `v0.8.0-BETA` all work; numeric parts drive ordering. |
| `name` | marketplace yes | Display name. |
| `type` | marketplace yes | `language`, `theme`, or `plugin`. |
| `author` | marketplace yes | |
| `description` | marketplace yes | One-line description. |
| `extensions` | language only | File extensions this package claims (e.g. `[".py", ".pyw"]`). Language profiles can also declare their own `extensions` for scoping. |
| `plugin` | plugin only | Filename of the DLL inside the package (e.g. `MyPlugin.dll`). Without this field a `type: plugin` package loads inertly - no code runs. |

### language.json

All fields are optional unless noted. Defaults: `commentLine="//"`, `commentBlockStart="/*"`, `commentBlockEnd="*/"`, `stringDelimiters=["\"", "'"]`, `multiLineStringDelimiters=[]`, `disableSingleQuoteStrings=false`, empty token lists.

```json
{
  "extensions": [".myl"],
  "keywords": ["if", "else", "return", "while"],
  "types": ["int", "string", "bool", "MyClass"],
  "functions": [],
  "properties": [],
  "namespaces": [],
  "blacklist": [],
  "deadCodeIgnore": [],
  "deadCodeEntryPoints": [],
  "commentLine": "//",
  "commentBlockStart": "/*",
  "commentBlockEnd": "*/",
  "stringDelimiters": ["\"", "'"],
  "multiLineStringDelimiters": ["\"\"\""],
  "disableSingleQuoteStrings": false,
  "colorTokens": {
    "keyword":      "#569CD6",
    "type":         "#4EC9B0",
    "string":       "#CE9178",
    "comment":      "#6A9955",
    "number":       "#B5CEA8",
    "operator":     "#D4D4D4",
    "punctuation":  "#D4D4D4",
    "function":     "#DCDCAA",
    "property":     "#9CDCFE",
    "namespace":    "#4FC1FF",
    "attribute":    "#C586C0",
    "preprocessor": "#C586C0",
    "variable":     "#A0DBFD",
    "charLiteral":  "#CE9178"
  }
}
```

**Token lists**

| Field | Description |
|---|---|
| `keywords` | Reserved words colored with `keyword` color (e.g. `if`, `return`, `class`) |
| `types` | Type names colored with `type` color (e.g. `int`, `string`, built-in classes) |
| `functions` | Function/method names colored with `function` color |
| `properties` | Property/field names colored with `property` color |
| `namespaces` | Namespace/module names colored with `namespace` color |

All five accept a JSON array of strings. Word-boundary matching is applied automatically, so `"int"` won't match inside `"integer"`.

**Other fields**

| Field | Description |
|---|---|
| `extensions` | If non-empty, this profile is scoped to just those file extensions; otherwise it merges into the base profile. |
| `blacklist` | Function/keyword names to suppress from autocomplete inside a call to that same name. Case-insensitive. |
| `deadCodeIgnore` | Names Insight's dead-code scanner should never flag (e.g. framework hooks like `__init__`, `self`). |
| `deadCodeEntryPoints` | Extra implicit entry-points beyond `main`/`WinMain` etc. |
| `commentLine` | Line comment prefix (e.g. `"//"`, `"#"`) |
| `commentBlockStart` / `commentBlockEnd` | Block comment delimiters (e.g. `"/*"` / `"*/"`, `"<!--"` / `"-->"`) |
| `stringDelimiters` | Single-line delimiters (e.g. `["\"", "'"]`). Span ends at closing delimiter or end of line. |
| `multiLineStringDelimiters` | Multi-line delimiters (e.g. `["\"\"\"", "'''"]`). Span continues until closing delimiter. List longer delimiters first. |
| `disableSingleQuoteStrings` | `true` replaces `'…'` string span with a precise char-literal regex - use for C# where `'` appears outside strings. |
| `colorTokens` | Overrides any highlighting color. Keys: `keyword`, `type`, `string`, `comment`, `number`, `operator`, `punctuation`, `function`, `property`, `namespace`, `attribute`, `preprocessor`, `variable`, `charLiteral`. Omit a key to keep the default. |

### theme.json

Supports: `themeId`, `displayName`, `baseTheme` (`"Dark"` or `"Light"`), and color keys for `windowBackground`, `topBar`, `sidebar`, `button`, `buttonHover`, `editorBackground`, `card`, `primaryText`, `mutedText`, `surfaceBorder`, `accent`, `previewBackground`, `previewBorder`.

`theme.json` can be either a single theme object or a JSON array of theme objects. Use an array to ship several related themes in one `.kox` (e.g. a "Dark Themes" pack) - each entry becomes its own installable theme sharing the same `manifest.json`, grouped together in the UI.

### Plugin extensions

Plugins are .NET class libraries that implement `IKodoPlugin` (`Kodo/Kodo/Source/KodoPlugins.cs:13`):

```csharp
public interface IKodoPlugin
{
    void OnLoad(MainWindow window, LoadedExtension extension);
    void OnUnload();
}
```

`OnLoad` runs on the UI thread with the live `MainWindow` - use `window.FindControl<StackPanel>("ActivityBarPanel")` to inject UI (add `Classes.Add("activity")` for styling). `OnUnload` must remove everything you added (controls, handlers, timers).

**Minimal example** - `Kodo-Extension-Template/TemplatePlugin/TemplatePlugin.cs`:

```csharp
public sealed class TemplatePlugin : IKodoPlugin
{
    public void OnLoad(MainWindow window, LoadedExtension extension) { /* add controls */ }
    public void OnUnload() { /* remove controls */ }
}
```

**Full examples:** `Kodo-Extension-Template/HelloWorld/HelloWorldPlugin.cs` (activity-bar button).

**Project setup** - `TemplatePlugin.csproj` must set:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <Reference Include="Kodo">
    <HintPath>..\..\Kodo\Kodo\Source\bin\Debug\net10.0\Kodo.dll</HintPath> <!-- Relative from TemplatePlugin to the Kodo build output (Kodo\Source\bin\Debug\net10.0\Kodo.dll from repo root); adjust if your checkout layout differs -->
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

`TargetFramework` must be `net10.0` and `Private=false` (otherwise you ship a stale `Kodo.dll`). Build `Kodo.csproj` first so the DLL exists, then:

```
dotnet build TemplatePlugin.csproj -c Release
# → bin/Release/net10.0/TemplatePlugin.dll
# ZIP your plugin DLL (+ any dependencies) and manifest.json, then rename .zip → .kox
```

`manifest.json` needs `"type": "plugin"` and a `"plugin"` value containing the exact filename of the plugin DLL (for example, `"plugin": "TemplatePlugin.dll"`; the name is not required to be `TemplatePlugin.dll`). Every `*.dll` in the `.kox` is extracted to `%AppData%\Kodo\PluginCache\`; folder installs use the folder directly. Loading uses a collectible `AssemblyLoadContext` with shadow copy so files are never locked - updates call `OnUnload` then `Unload()`.

---

## Submitting an Extension/Plugin

If you want your extension in the official marketplace, open a PR against [Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions):

1. Add your `.kox` to `Extensions/` (e.g. `Extensions/MyLang-Kodo-Extension-v1.0.0.kox`)
2. Add an entry to `Indexs/ExtensionsIndex.json` or `Indexs/PluginIndex.json` depending on your extension's type (note the folder is `Indexs`, not `Indexes`):

```json
{
  "id": "mylang-kodo-extension",
  "version": "v1.0.0",
  "name": "MyLang Language Support",
  "type": "language",
  "author": "Your Name",
  "description": "Syntax highlighting for MyLang files.",
  "fileName": "MyLang-Kodo-Extension-v1.0.0.kox",
  "downloadUrl": "REPLACE_WITH_RAW_GITHUB_DOWNLOAD_URL",
  "iconUrl": "REPLACE_WITH_RAW_GITHUB_ICON_URL"
}
```

`fileName` and the last segment of `downloadUrl` must match. Rule 1B applies when submitting extensions.

For reference, the current app uses the following extension locations:

- `%AppData%\Kodo\Extensions\` for installed extensions
- `%AppData%\Kodo\PluginCache\` for extracted plugin DLLs from `.kox` packages
- `%LocalAppData%\Kodo\Compilers\` for compiler installs

---

## License

By contributing, you agree that your contributions are licensed under [GPL v3.0](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE).

---

## Questions?

Jump into the [Discord](https://discord.gg/cUQ6C88Z9C), we're always here to help.
