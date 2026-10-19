# Kodo extension development

Kodo is extended with `.kox` packages. A `.kox` file is just a ZIP archive with a different
extension - there is no proprietary format, no signing, and no installer.

This page tells you **which kind of extension to build** and gets you started. Each guide below
then covers its area in full.

---

## 1. What are you building?

| You want to add… | Build a… | Guide |
|---|---|---|
| Colors, editor appearance, light/dark themes | **Theme extension** | [THEME-EXTENSIONS.md](THEME-EXTENSIONS.md) |
| Syntax highlighting and editor intelligence for a language | **Language extension** | [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) |
| Panels, commands, menus, previews, or any host integration | **Plugin** | [PLUGINS.md](PLUGINS.md) |
| A toolchain Kodo can run or build with | **Compiler entry** | [COMPILERS.md](COMPILERS.md) |
| Understanding what goes inside the package | - | [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) |

Everything is declared with a `type` field in `manifest.json`:

| `type` | What Kodo loads |
|---|---|
| `language` | A language association plus an engine (see below) |
| `theme` | `theme.json` - colors |
| `plugin` | A .NET assembly implementing `IKodoPlugin` |

Compilers are **not** `.kox` extensions. They are separate command-line tools registered in a
central index. See [COMPILERS.md](COMPILERS.md).

---

## 2. Language extensions: pick one engine

A language extension always does two things:

1. **Claims file extensions** - so Kodo knows that `.myl` is a MyLang file.
2. **Provides an engine** - so Kodo knows what to do with it.

Pick exactly one engine:

| Engine | Use it when | You write | Server needed? |
|---|---|---|---|
| **Legacy** | You only need syntax highlighting. Smallest possible effort. | `language.json` only | No |
| **LangRules** | It is a community or smaller language and no language server exists for it. | A .NET assembly implementing `ILangRulesProvider` | No |
| **LSP** | It is an established language with a real language server. | Manifest JSON only | Yes |

Guidance on choosing:

- **Start with Legacy.** If `language.json` gets you highlighting you are happy with, stop there.
  It is pure data, has no build step, and cannot crash the editor.
- **Use LangRules** for a language nobody has written a server for. You own the parsing, so you own
  the bugs - see the [honest trade-offs](LANGUAGE-EXTENSIONS.md#choosing-an-engine) before
  committing to it.
- **Use LSP** whenever a server exists. You write configuration, not a parser, and the server is
  maintained by the language's community.

> The three engines are alternatives, not layers. Build one. If you are following an existing
> package that does more than one, that is a historical artifact of how that language grew up, not
> a pattern to copy.

Full detail, including every engine's API and worked examples, is in
[LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md).

---

## 3. Where extensions live

Kodo scans these locations:

| Location | Purpose |
|---|---|
| `%LocalAppData%\Kodo\Extensions\` | Installed extensions (Windows). The normal install target. |
| `Extensions/` next to the Kodo source | Local development. Live-reloads while you work. |
| `%LocalAppData%\Kodo\PluginCache\` | Where plugin DLLs from `.kox` packages are extracted. Do not write here. |
| `%LocalAppData%\Kodo\Compilers\` | Where managed compilers are installed. Do not write here. |

> **Note:** Kodo used to keep extensions in `%AppData%\Kodo\Extensions\`. That path is still read as a
> **fallback**, but only when `%LocalAppData%\Kodo\Extensions\` does not exist - and Kodo creates that
> folder on first launch, so in practice the old path is ignored. If you installed extensions before
> the move, copy them across once: `%LocalAppData%\Kodo\Extensions\` is the path that counts.

Both extension folders are watched with a `FileSystemWatcher` for **created, deleted, and renamed**
files, so dropping in a new `.kox` picks it up without restarting Kodo. Two consequences:

- The watcher does **not** recurse (`IncludeSubdirectories = false`). Keep packages flat.
- The watcher does **not** fire on in-place content changes. If your editor rewrites a file rather
  than creating a new one, delete and re-add it, or restart Kodo.

**Develop against `%LocalAppData%\Kodo\Extensions\`, not the repo folder,** unless you are working on
Kodo itself. The repo folder is for testing changes to Kodo.

---

## 4. Your first extension in five minutes

This is a complete, installable theme. No build step, no compiler.

Create a folder and add `manifest.json`:

```json
{
  "id": "my-first-theme",
  "version": "1.0.0",
  "name": "My First Theme",
  "type": "theme",
  "author": "Your Name",
  "description": "A simple dark theme."
}
```

Add `theme.json` beside it:

```json
{
  "themeId": "my-first",
  "displayName": "My First Theme",
  "baseTheme": "Dark",
  "windowBackground": "#10131A",
  "topBar": "#1C2330",
  "sidebar": "#121722",
  "card": "#202938",
  "primaryText": "#F2F5FA",
  "mutedText": "#A8B2C2",
  "surfaceBorder": "#354154",
  "accent": "#6EA8FE",
  "button": "#29364B",
  "buttonHover": "#38506F",
  "editorBackground": "#151A24"
}
```

Drop the folder into `%LocalAppData%\Kodo\Extensions\`, and it appears in Kodo's theme list
immediately. That is a working extension. [THEME-EXTENSIONS.md](THEME-EXTENSIONS.md) covers every
supported color key.

A first language extension is nearly as quick - see
[the Legacy walkthrough](LANGUAGE-EXTENSIONS.md#legacy-walkthrough).

---

## 5. Package it into a `.kox`

An unpacked folder works for development. To distribute, zip it and rename:

```powershell
# Windows, from the folder containing your package folder
Compress-Archive -Path .\my-theme\* -DestinationPath .\my-theme-1.0.0.zip
Rename-Item .\my-theme-1.0.0.zip .\my-theme-1.0.0.kox
```

```bash
# Linux / macOS
(cd my-theme && zip -r ../my-theme-1.0.0.kox . -x "*.pdb" "*.tmp")
unzip -l ../my-theme-1.0.0.kox | head   # manifest.json must be at the root
```

The `*` / `.` is the important part: it puts `manifest.json` at the archive root rather than
nesting it inside a folder. Kodo does not search subdirectories.

Full packaging rules and the complete manifest reference: [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md).

---

## 6. Worked examples

Real, complete packages live in two sibling repositories:

- **[Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions)** - the published marketplace.
  Contains 29 shipped `.kox` files covering every type: theme packs, Legacy-only packages (Shine,
  HolyC), LangRules packages (Python, Rust, TOML…), LSP packages, and the marketplace index files.
- **[Kodo-Extension-Template](https://github.com/Kodo-IDE/Kodo-Extension-Template)** - small,
  deliberately minimal examples of each type, plus two example plugins. Start here.

Reading a real package is the fastest way to learn. Any `.kox` is a ZIP:

```bash
unzip -o Python-Kodo-Extension-v2.1.0.kox -d /tmp/python-ext
```

---

## 7. Before you publish

Read [TESTING-AND-PULL-REQUESTS.md](TESTING-AND-PULL-REQUESTS.md). The short version: install your
own package into a clean Kodo profile and use it. A package that only works from your development
folder is not finished.

To submit to the official marketplace, open a pull request against
[Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions) adding your `.kox` to `Extensions/`
and an entry to the matching index file. See
[KODO-FILE-FORMAT.md#submitting-to-the-marketplace](KODO-FILE-FORMAT.md#submitting-to-the-marketplace).

---

## All guides

| Guide | Covers |
|---|---|
| [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) | `.kox` anatomy, every `manifest.json` field, packaging, marketplace submission |
| [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) | Legacy, LangRules, and LSP engines in full |
| [THEME-EXTENSIONS.md](THEME-EXTENSIONS.md) | Every theme color key, multi-theme packages |
| [PLUGINS.md](PLUGINS.md) | `IKodoPlugin`, project setup, lifecycle, debugging |
| [COMPILERS.md](COMPILERS.md) | Registering a run/build toolchain and its resolver |
| [TESTING-AND-PULL-REQUESTS.md](TESTING-AND-PULL-REQUESTS.md) | checklists, common failures, submitting |

For building Kodo itself, see [CONTRIBUTING.md](CONTRIBUTING.md).
