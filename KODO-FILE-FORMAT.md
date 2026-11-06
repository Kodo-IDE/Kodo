# The `.kox` file format

A `.kox` file is a **ZIP archive with a different extension**. There is no proprietary container,
no manifest binary format, and no signing. Kodo uses a standard ZIP reader, which means you can
inspect, build, and diff `.kox` files with any ordinary zip tool.

This is the complete reference: what goes inside a package, every field Kodo reads, how to package,
and how to publish.

---

## 1. Package layout

```text
my-extension/
├── manifest.json          required - the only file Kodo cannot start without
├── language.json          language extensions
├── language1.json         optional - additional scoped profiles (…through language5.json)
├── theme.json             theme extensions (single object, or an array of them)
├── icon.png               optional - preferred
├── icon.svg               optional - fallback
├── MyLangRules.dll        language extensions using the LangRules engine
├── MyPlugin.dll           plugin extensions
└── LICENSE.txt            recommended
```

Rules:

- **`manifest.json` should be at the archive root.** Folder sources require it at the root; inside a
  `.kox`, Kodo also finds a nested `manifest.json` by filename (first match wins), but root is the
  only layout you should ship.
- **Keep it flat.** The extension folder watcher does not recurse.
- **Only `manifest.json` is strictly required.** Every other field has a default, so a package
  containing just a manifest loads - it will simply do nothing useful.
- File names are matched case-insensitively inside `.kox` archives only; folder loading is
  case-sensitive (matters on Linux), so use lowercase by convention.

### What Kodo recognizes

| File | Read by | Notes |
|---|---|---|
| `manifest.json` | everything | Required |
| `language.json` | language extensions | Base profile |
| `language1.json` … `language5.json` | language extensions | Up to **five** extra profiles (six total) |
| `theme.json` | theme extensions | Object **or** array of objects |
| `icon.png` | all types | Preferred icon |
| `icon.svg` | all types | Fallback icon |
| `*.dll` | plugin, LangRules | Extracted only when the package names one via `plugin` or `languagePlugin`; DLLs with no such field are never extracted or loaded. On non-Windows, `.so`/`.dylib` are extracted too |
| `languagePlugin` target | plugin | Loaded separately from `plugin` |

> **There is no `icon` field in `manifest.json`.** Kodo finds icons by filename convention only.
> Some published packages include `"icon": "icon.png"` in the manifest out of habit; it is ignored
> and is not required.

If no icon loads, Kodo shows a two-letter abbreviation derived from the extension `name`. A
marketplace `iconUrl`, when set and fetchable, replaces the local icon.

---

## 2. `manifest.json`

### Top-level fields

| Field | Type | Required | Meaning |
|---|---|---|---|
| `id` | string | marketplace | Unique kebab-case identifier. Used for deduplication, update tracking, and dependency resolution. |
| `version` | string | marketplace | See [Versioning](#3-versioning). |
| `name` | string | marketplace | Display name. |
| `type` | string | marketplace | `language`, `theme`, or `plugin`. |
| `author` | string | marketplace | Shown in the marketplace card. |
| `description` | string | marketplace | One line. |
| `extensions` | string[] | language | File extensions this package claims, **with the leading dot**: `[".py", ".pyw"]`. |
| `plugin` | string | LangRules / plugin | **Filename of the assembly to load.** For a LangRules package this is your rules assembly; for a plugin package, your plugin. |
| `languagePlugin` | string | optional | A *second* assembly, loaded with the same `IKodoPlugin` lifecycle. See [`plugin` vs `languagePlugin`](#plugin-vs-languageplugin). |
| `dependencies` | string[] | optional | IDs of packages that must be installed first. **Only honored from the marketplace index entry** - a manifest `dependencies` field is parsed but never consulted during install or load. |
| `enableSemanticDiagnostics` | bool | optional | `true` opts this language into Kodo's semantic diagnostics pass. Leave unset unless you have tested it. |
| `overrides` | object | optional | Turns named Kodo features on or off for this language. See [`overrides`](#overrides). |
| `externalTools` | object[] | optional | Run/build tools associated with this language. See [`externalTools`](#externaltools). |
| `lsp` | object | LSP | One language server. |
| `lsps` | object[] | LSP | Several language servers. |
| `languageServers` | object[] | LSP | Alias for `lsps`. |

Missing fields default to `""`, `[]`, or `null`. A malformed JSON document (or a non-array
`extensions`/`dependencies`) drops the whole package with a per-package load error - individual
LSP entries are the only values skipped-and-logged rather than failing the package.

### `type`

`type` is stored verbatim with no validation. Loading is actually driven by which files are
present, not by `type`, and different engine files can combine on one package:

| Present | Loads |
|---|---|
| `theme.json` | A theme (detected case-insensitively) |
| `plugin` naming an assembly | LangRules (from that assembly) and/or a plugin |
| `lsp`/`lsps` | A language server |

### `plugin` vs `languagePlugin`

Both name an assembly, and both are loaded with the same `IKodoPlugin` lifecycle. They are not
mutually exclusive, and a single assembly can be named by both.

| Field | Use it for |
|---|---|
| `plugin` | Your **LangRules** assembly, and/or your plugin assembly. This is the field Kodo reads to find LangRules. |
| `languagePlugin` | An extra plugin that ships alongside a language, for host UI integration. |

**This trips people up.** To attach a LangRules provider to a language extension, name your
assembly in **`plugin`**:

```json
{ "id": "mylang-kodo-extension", "type": "language", "plugin": "LangRules.dll" }
```

Kodo loads that one assembly and looks inside it for a LangRules provider *and* for any
`IKodoPlugin` implementations. A single `MyLang.dll` can legitimately provide both.

### `overrides`

Turns a named Kodo feature on or off for this language. Values may be booleans or the strings
`"true"` / `"false"`.

```json
"overrides": { "bracketAutoClose": false }
```

| Key | Effect |
|---|---|
| `bracketAutoClose` | `false` disables automatic closing brackets and quotes in this language. |

That is the **only** key Kodo currently reads. Any other key is accepted and stored without
error but has no effect, so a typo fails silently. Do not add overrides speculatively.

### `externalTools`

Declares commands Kodo runs against the active file **to produce diagnostics** (the sole consumer
is the external-tools diagnostics pass). This is not a Run/Build menu - for that, see
[COMPILERS.md](COMPILERS.md).

```json
"externalTools": [
  {
    "id": "mylang-run",
    "command": "mylang",
    "arguments": ["run", "{file}"],
    "format": "compiler",
    "enabled": true,
    "requiresProject": false,
    "projectFiles": ["mylang.project"]
  }
]
```

| Field | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | `command` | Identifier for this tool. |
| `command` | string | - | **Required.** Executable name or path. |
| `arguments` | string[] | `["{file}"]` | Arguments passed to the command. |
| `format` | string | - | Only `"json"` is special-cased (JSON diagnostics output); the `"compiler"` default has no menu effect. |
| `enabled` | bool | `true` | `false` excludes the tool from diagnostics. |
| `requiresProject` | bool | `false` | `true` hides the tool for standalone files. |
| `projectFiles` | string[] | `[]` | Glob patterns used to find the owning project, e.g. `["*.csproj", "*.sln"]`. |

**Placeholders** (substituted case-insensitively before launch):

| Placeholder | Expands to |
|---|---|
| `{file}` | Always a **temporary copy** of the file (never the real path); extension long than 16 chars becomes `.txt` |
| `{fileName}` | File name of the active file |
| `{project}` | Path to the resolved project file, or empty if none was found |
| `{projectName}` | Base name of the resolved project file |

Kodo parses tool output for diagnostics and shows them in the editor. `file(line,col): severity: message`
on stderr or stdout is the convention; `file:line:col`, Python tracebacks, and JSON-format
diagnostics are also recognized.

> For a whole toolchain Kodo should *install* - a compiler, a runtime - see
> [COMPILERS.md](COMPILERS.md). That is a different mechanism with a central index.

### `lsp` / `lsps`

Configures language servers. Use **`lsp`** for one server, **`lsps`** for several.
`languageServers` is a fallback alias used only when neither `lsp` nor `lsps` is present; when both
`lsps` and `lsp` are present, `lsps` wins and `lsp` is ignored. Each entry needs its own
`command`; its language and file-extension lists are scoped independently, and the top-level
`extensions` act as a fallback.

```json
"lsp": {
  "command": "mylang-language-server",
  "args": ["--stdio"],
  "languages": ["mylang"],
  "fileExtensions": [".myl"],
  "providerId": "mylang-ls",
  "installMethod": "npm",
  "packageName": "mylang-language-server"
}
```

#### Core fields

| Field | Type | Meaning |
|---|---|---|
| `command` | string | **Required.** Executable to launch. |
| `args` / `arguments` | string[] | Command-line arguments. |
| `languages` / `languageIds` / `language` | string[] | Language IDs reported to the server via `textDocument/didOpen`. |
| `fileExtensions` / `extensions` | string[] | File extensions this server handles. |
| `env` | object | Environment variables for the server process. |
| `cwd` / `workingDirectory` | string | Working directory for the server process. |
| `initializationOptions` / `initialization_options` | any | Passed through in the LSP `initialize` request. |
| `rootMarkers` / `rootPatterns` | string[] | Files/directories that mark a workspace root, searched by walking up from the file. |
| `providerId` / `provider` | string | Identity used for install tracking and de-duplication. |
| `displayName` | string | Name shown in the UI. |

#### Install and runtime fields

| Field | Type | Meaning |
|---|---|---|
| `version` | string | Pinned version. Omit to always take the newest. |
| `installMethod` / `installationMethod` | string | `npm`, `dotnet`, or `download`. |
| `packageName` / `package` | string | Package name to install (npm/dotnet). |
| `downloadUrl` / `url` / `downloadSource` | string | Direct download URL. |
| `sha256` / `checksum` / `sha256Checksum` | string | Hex checksum of the downloaded file. Verified when present. |
| `runtime` / `requiresRuntime` | string | Required runtime, e.g. `dotnet`, `node`. |
| `runtimeMinVersion` / `runtimeVersion` | string | Minimum acceptable runtime version. |
| `allowAutoInstall` / `autoInstall` | bool | Permit Kodo to install without asking. |
| `allowSystem` / `allowSystemInstall` | bool | Permit using a server already on `PATH`. |
| `versionArgs` / `versionArgument` | string[] or string | Arguments that make the server print its version. |

`runtimeArgs` and `mainClass` are **not** read from `manifest.json` - only `args` (alias
`arguments`) supplies server arguments, and `command` is launched directly. There is no
runtime-launcher mechanism for bundles shipped in a manifest (e.g. a `.jar` server cannot be
launched through `java -jar` this way); publish an executable wrapper instead.

#### Servers that are not directly executable

Not supported via manifest configuration. If your server is a bundle a runtime consumes (a `.jar`,
a Node script), ship a wrapper executable (shell script, `.cmd`, or small binary) and point
`command` at it, or use `installMethod`/`downloadUrl` to install the real server binary.
A single-file download is stored as-is rather than unpacked, so `.jar`, `.exe`, `.dll`, `.msi`,
`.deb`, `.rpm`, `.tgz`, `.war` and `.appimage` all work without special handling; `.zip` and
`.tar.gz` are unpacked (extensionless payloads are sniffed by magic bytes).

#### Cross-platform commands

One `command` value is used on every platform. Prefer an **extensionless** name
(`mylang-language-server`, not `mylang-language-server.exe`). On Linux, Kodo strips a trailing
`.exe`/`.cmd`/`.bat` only as a `PATH` lookup fallback, after trying the exact name first.
`python`→`python3` and `powershell`→`pwsh` mapping belongs to compiler run/build commands and
`externalTools`, not to LSP spawning generally.

If a server genuinely needs different binaries per OS, publish two `.kox` files and gate them
with the marketplace `os` field rather than trying to express conditionals in `command`.

---

## 3. Versioning

`version` accepts a leading `v` or not: `v1.0.0`, `1.0.0`, `v0.8.0-BETA` all parse. Ordering
compares the **numeric parts** (including prerelease digits, so `1.0.0-BETA1` sorts above `1.0.0` - this is not semver prerelease ordering), so `1.10.0` is newer than `1.9.0`.

A version in the **filename** is used when it is higher than the one in `manifest.json`, but only
when the filename contains a `v` followed by a dotted version (e.g. `-v1.1.0.kox`); a bare
`-1.1.0` or `-v1` extracts nothing and the manifest version stands. Re-uploading
`MyLang-Kodo-Extension-v1.1.0.kox` therefore upgrades an installed `v1.0.0`.

Convention is `{Id}-Kodo-Extension-v{version}.kox`, but nothing enforces it - if `fileName` and
`downloadUrl` in the marketplace index disagree, Kodo canonicalizes (rewrites the filename's
version string and the URL's last segment to match) rather than rejecting the entry.

---

## 4. Packaging

### Windows (PowerShell)

Run from the directory **containing** your package folder:

```powershell
Compress-Archive -Path .\my-extension\* -DestinationPath .\my-extension-1.0.0.zip
Rename-Item .\my-extension-1.0.0.zip .\my-extension-1.0.0.kox
```

### Linux / macOS

```bash
(cd my-extension && zip -r ../my-extension-1.0.0.kox . -x "*.pdb" "*.tmp")
unzip -l ../my-extension-1.0.0.kox | head   # confirm manifest.json is at the root
```

The `*` / `.` is the point: it places `manifest.json` at the archive root instead of nesting the
whole folder.

### Packaging checklist

- [ ] The package opens as a ZIP.
- [ ] `manifest.json` is at the archive **root**.
- [ ] `id`, `version`, `name`, `type`, `author`, `description` are all filled in.
- [ ] `extensions` entries include the leading dot.
- [ ] Every referenced file - DLL, JSON, icon - is actually in the package.
- [ ] No `bin/`, `obj/`, `.pdb`, secrets, or absolute developer paths.
- [ ] No stale copy of Kodo's own assembly.
- [ ] The package still works after being extracted somewhere unrelated.

---

## 5. Installing for testing

Copy the `.kox` into `%LocalAppData%\Kodo\Extensions\` and Kodo will pick it up. For a plugin package,
Kodo extracts the DLLs to `%LocalAppData%\Kodo\PluginCache\` and loads them in a collectible
`AssemblyLoadContext` with shadow copying, so the files are never locked and can be replaced while
Kodo is running.

To develop iteratively, work in a folder instead of a `.kox` - Kodo accepts an unpacked directory
with `manifest.json` at its root and picks it up without a restart. In-place edits to the JSON are
**not** watched, so use the Extensions page's refresh button or restart Kodo after changing a file in
place. See [where extensions live](EXTENSION-DEVELOPMENT.md#3-where-extensions-live).

**Never write directly into `PluginCache` or the compilers directory.** Use Kodo's own
install/update flow so versions, cleanup, and permissions stay consistent.

---

## 6. Submitting to the marketplace

The marketplace lives in a separate repository, [Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions).
Open a pull request against it containing:

1. Your `.kox` in `Extensions/`.
2. An entry in the matching index file.

> The folder is named **`Indexs`**, not `Indexes`.

| Index file | For |
|---|---|
| `Indexs/ExtensionsIndex.json` | `language` and `theme` packages |
| `Indexs/PluginsIndex.json` | `plugin` packages |
| `Indexs/CompilerIndex.json` | Compilers - see [COMPILERS.md](COMPILERS.md) |

Each index file is an object with a single array key: `extensions`, `plugins`, or `compilers`.

```json
{
  "extensions": [
    {
      "id": "mylang-kodo-extension",
      "version": "v1.0.0",
      "name": "MyLang Language Support",
      "type": "language",
      "author": "Your Name",
      "description": "Syntax highlighting for MyLang files.",
      "fileName": "MyLang-Kodo-Extension-v1.0.0.kox",
      "downloadUrl": "https://raw.githubusercontent.com/Kodo-IDE/Kodo-Extensions/main/Extensions/MyLang-Kodo-Extension-v1.0.0.kox",
      "iconUrl": "https://raw.githubusercontent.com/Kodo-IDE/Kodo-Extensions/main/Extensions/mylang-icon.png",
      "fileExtensions": [".myl"],
      "os": ["windows", "linux"],
      "arch": ["x64"],
      "dependencies": [],
      "sha256": ""
    }
  ]
}
```

| Field | Required | Notes |
|---|---|---|
| `id` | yes | Must match `manifest.json` exactly. |
| `version` | yes | Must match `manifest.json`. |
| `name`, `author`, `description` | yes | Display metadata. |
| `fileName` | yes | Must be the last segment of `downloadUrl`. |
| `downloadUrl` | yes | Raw URL to the `.kox`. |
| `iconUrl` | recommended | Raw URL to a PNG. |
| `fileExtensions` | recommended | File extensions, for search and association. (A literal `extensions` key is ignored.) |
| `os` / `platforms` | optional | `["windows"]`, `["linux"]`, `["macos"]` (aliases like `win32`/`darwin` also accepted). Honored - incompatible entries show "Not available". Unknown tokens silently pass. |
| `arch` / `architectures` | optional | `["x64"]`, `["arm64"]` (aliases like `x86_64`/`any` also accepted). Unknown tokens silently pass. |
| `dependencies` | optional | IDs that must be installed first. |
| `sha256` | recommended | Hex checksum of the `.kox`. |

`os` and `arch` are **marketplace listing fields, not `manifest.json` fields.** Kodo reads them
from the index, not from inside your package.

---

## Related guides

- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) - which kind of extension to build
- [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) - language engines in full
- [THEME-EXTENSIONS.md](THEME-EXTENSIONS.md) - every theme color key
- [PLUGINS.md](PLUGINS.md) - writing an `IKodoPlugin`
- [COMPILERS.md](COMPILERS.md) - registering a run/build toolchain
