# Testing and submitting an extension

A package that compiles is not finished. It has to be discoverable, installable, and safe on
input you did not anticipate.

---

## The rule

> **A feature is not ready until the built package — the actual `.kox` a user receives — has been
> installed into a clean Kodo profile and used.**

Almost every extension bug comes from testing the development folder instead of the artifact.

---

## 1. Build checks

- The package opens as a ZIP.
- `manifest.json` is at the **archive root**, not inside a nested folder.
- `manifest.json` parses, and `id`, `version`, `name`, `type`, `author`, `description` are filled in.
- `extensions` entries include the leading dot.
- Every referenced file is present: DLLs, `language.json`, `theme.json`, icons.
- No `bin/`, `obj/`, `.pdb`, secrets, absolute developer paths, or a copy of `Kodo.dll`.
- Manifest and asset filenames are lowercase.

```bash
unzip -l my-extension-1.0.0.kox
```

---

## 2. Test the input, not just the happy path

Users type constantly, and they type broken code. Every one of these is a case you must have tried:

| Input | Why it matters |
|---|---|
| **Empty file** | Zero-length documents hit index and bounds code paths. |
| **Incomplete syntax** | An unclosed bracket, a half-typed keyword, a dangling string. Every provider method is called constantly in this state. |
| **Malformed input** | Binary content, mixed encodings, a stray null byte. |
| **Unicode** | Emoji, CJK, RTL text, combining marks. Offsets are **UTF-16**, so a character outside the BMP occupies two units — get this wrong and ranges land mid-surrogate. |
| **Mixed line endings** | CRLF, LF, and a file with both. A formatter that normalises line endings rewrites the whole file; that is a bug, not a feature. |
| **Boundary edits** | Insert at the very start, at the very end, across a line boundary, deleting a whole line. |
| **Large document** | At least a few MB, to exercise the viewport and size thresholds Kodo uses to stay responsive. |
| **Many results** | Hundreds of diagnostics, completions, or code actions at once. |
| **Rapid repeated input** | Whether a stale result from a previous document can overwrite a current one. |

For plugins and themes, additionally test **startup, reload, clean installation, and shutdown**.

---

## 3. Install-test the real package

1. Create the `.kox` exactly as a user would receive it.
2. Copy it into `%LocalAppData%\Kodo\Extensions\`.
3. Start Kodo **from a clean profile** — not your own, where you have settings and other extensions
   that may be masking a problem.
4. Follow your own guide from start to finish, as a first-time user would.
5. Exercise every feature you declared.
6. Remove the package and confirm nothing is left behind.

---

## 4. Reload testing

The extension folder is watched, so you can iterate quickly — but reload is where most lifecycle
bugs surface.

- [ ] Edit the JSON or DLL, save, and confirm the change appears **without a restart**.
- [ ] Reload the package and confirm **the UI did not duplicate**.
- [ ] Reload several times in a row and watch for growth in memory or background processes.
- [ ] Restart Kodo and confirm nothing is left running.

> LangRules assemblies are the exception: they load into a **non-collectible** context, so
> changing that DLL requires a Kodo restart. Plan for it rather than assuming live reload.

---

## 5. Cross-platform

Test on Windows and Linux unless your extension is explicitly single-platform. On Linux you need
`libicu`, `libssl`, and `fontconfig` present.

- [ ] File paths with spaces, and with non-ASCII characters.
- [ ] Case-sensitive filesystem behaviour.
- [ ] CRLF versus LF line endings.
- [ ] An executable with and without a file extension.
- [ ] If the extension launches a process, that it launches on both platforms.

If you gate an extension by OS, use the marketplace `os` field. Users on an excluded platform see
"Not available" rather than a dead Install button.

---

## 6. Writing the pull request

Explain:

- **The user problem.** What could someone not do before?
- **How you solved it.**
- **Compatibility impact.** Does this change behaviour for existing users? Does it require a
  minimum Kodo version?
- **Exact verification commands.** So a reviewer can reproduce your testing.

Include screenshots for anything visual.

If your change alters a contract or adds a manifest field, update the relevant guide in the same
pull request — see [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md).

---

## 7. Submitting to the marketplace

Open a pull request against
[Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions):

1. Your `.kox` in `Extensions/`.
2. An entry in the matching index file — `Indexs/ExtensionsIndex.json` for languages and themes,
   `Indexs/PluginsIndex.json` for plugins, `Indexs/CompilerIndex.json` for compilers.

Full details, including required fields and the `os`/`arch` gating, are in
[KODO-FILE-FORMAT.md#submitting-to-the-marketplace](KODO-FILE-FORMAT.md#submitting-to-the-marketplace).

---

## Troubleshooting map

| Symptom | Check first | Then check |
|---|---|---|
| Extension not listed at all | valid root `manifest.json` | package location; JSON syntax; `type` |
| Language not selected for your files | `extensions` has the leading dot | another extension already claims the same extension |
| Colors missing | `language.json` valid and beside the manifest | `colorTokens` keys spelled correctly |
| Theme listed, colors unchanged | `theme.json` valid; `type` is `theme` | `themeId` present and unique |
| Plugin listed, no effect | `plugin` DLL name matches exactly | discovery requirements; TFM |
| LangRules features missing | `plugin` names your DLL | `Info.Capabilities`; discovery requirements |
| LSP starts, no features | language id in `languages` | `fileExtensions`; `rootMarkers` |
| Works from source, not as `.kox` | `manifest.json` at the archive root | every referenced file is included |
| UI duplicates on reload | `OnUnload` removes what `OnLoad` created | event handlers unsubscribed |
| Nothing happens on file change | the watcher does not recurse and ignores in-place edits | re-create the file, or restart |
| Works for you, fails for a user | clean profile, fresh install | the user's Kodo version; verbose logs |

---

## Related guides

- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) — the complete documentation index
- [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) — manifest reference and packaging
