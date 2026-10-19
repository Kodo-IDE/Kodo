# Compilers

A compiler entry tells Kodo how to **run** and **build** a language. It is not a `.kox`
extension - compilers are command-line tools registered in a central index file and, when Kodo can
install them itself, downloaded into `%LocalAppData%\Kodo\Compilers\`.

Shipped examples: .NET SDK, Go, Temurin JDK, LLVM/Clang, MinGW-w64, Node.js, Strawberry Perl,
Python, Ruby, Shine, Swift, Zig, PHP, Kotlin, TypeScript, Lua, NASM, HolyC.

---

## Two different things, often confused

| | What it is | Where it lives |
|---|---|---|
| **Compiler entry** | A run/build toolchain, optionally auto-installed by Kodo | `Indexs/CompilerIndex.json` |
| **`externalTools` in an extension** | A command Kodo can run against the active file, associated with a language | Your `.kox` manifest |

Use **`externalTools`** when you are writing a language extension and want to offer a Run action
using a tool the user already has. Use a **compiler entry** when the toolchain is substantial
enough that Kodo should manage its installation and version.

See [`externalTools`](KODO-FILE-FORMAT.md#externaltools) for the extension-side mechanism.

---

## The index file

`Kodo-Extensions/Indexs/CompilerIndex.json` is a single object with a `compilers` array:

```json
{
  "compilers": [
    {
      "id": "mylang-toolchain",
      "name": "MyLang Toolchain",
      "type": "compiler",
      "author": "MyLang",
      "description": "The MyLang compiler and interpreter.",
      "iconUrl": "https://example.invalid/icon.png",
      "fileExtensions": [".myl", ".mylproj"],
      "languageExtensions": ["mylang-kodo-extension"],
      "run": "mylang run {file}",
      "build": "mylang build {folder}",
      "resolver": {
        "kind": "github-release",
        "repo": "mylang/toolchain",
        "assetPattern": "mylang-.*-linux-x64\\.tar\\.gz$",
        "versionPattern": "^v([0-9.]+)$"
      },
      "fallback": {
        "version": "1.0.0",
        "downloadUrl": "https://example.invalid/mylang-1.0.0-linux-x64.tar.gz",
        "fileName": "mylang-1.0.0-linux-x64.tar.gz"
      }
    }
  ]
}
```

### Fields

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Unique kebab-case identifier. |
| `name` | yes | Display name. |
| `type` | yes | `"compiler"`. |
| `author` | marketplace | Shown in the card. |
| `description` | marketplace | One line. |
| `iconUrl` | recommended | Card icon. |
| `fileExtensions` | yes | File extensions this toolchain applies to, with leading dots. |
| `languageExtensions` | recommended | Kodo extension **ids** this pairs with. |
| `run` | recommended | Command to run the active file. |
| `build` | recommended | Command to build. |
| `resolver` | optional | How to find and download the newest release. Omit for a system-only toolchain. |
| `fallback` | optional | A known-good version to fall back to. |

### Command placeholders

`run` and `build` are command strings, split on spaces, with these placeholders substituted:

| Placeholder | Expands to |
|---|---|
| `{file}` | Path to the active file |
| `{fileName}` | File name of the active file |
| `{folder}` | Directory of the active file |
| `{name}` | Base name of the output, for build targets |
| `{args}` | Extra arguments supplied when the user invokes the action |

```json
"run":  "go run {file}",
"build": "go build -o {name}.exe"
```

> **`{project}` is not substituted here.** It exists only in an extension's `externalTools`, where the
> owning project file is resolved from the manifest. A compiler entry that writes `{project}` gets that
> literal text in its command - use `{folder}` if you need the directory, and let the tool discover the
> project itself from there. See
> [`externalTools`](KODO-FILE-FORMAT.md#externaltools) for the extension-side placeholder set.

---

## Resolvers

A resolver answers one question: *what is the newest version, and where do I download it?* It
returns a version, a download URL, and a file name - or nothing, in which case Kodo falls back.

Resolution is throttled and runs off the UI thread with a ten-second timeout, so a slow or dead
URL degrades to the fallback rather than hanging.

### `github-release`

Reads the GitHub Releases API for a repository. Good for anything published as a GitHub release.

| Param | Required | Meaning |
|---|---|---|
| `repo` | yes | `owner/name`. |
| `assetPattern` | yes | Regex matched against asset filenames. |
| `versionPattern` | no | Regex with one capture group, applied to the tag. Without it the tag is used with leading `v` stripped. |
| `includePrerelease` | no | `"true"` to consider prereleases. |

```json
"resolver": {
  "kind": "github-release",
  "repo": "mylang/toolchain",
  "assetPattern": "mylang-.*-linux-x64\\.tar\\.gz$",
  "versionPattern": "^v([0-9.]+)$"
}
```

### `text-pointer`

Fetches a URL whose **body is just a version string**, then builds the download URL from a
template. Use when the vendor publishes a `latest.version` file.

| Param | Required | Meaning |
|---|---|---|
| `url` | yes | URL returning the version as plain text. May contain `{channel}`. |
| `urlTemplate` | yes | Download URL template. |
| `fileNameTemplate` | yes | Resulting filename template. |
| `channel` | no | Value for `{channel}`. |
| `rejectContains` | no | Substring that invalidates a response. Defaults to `<` (catches HTML error pages). |
| `maxLength` | no | Reject responses longer than this. |

```json
"resolver": {
  "kind": "text-pointer",
  "channel": "8.0",
  "url": "https://builds.example.invalid/Sdk/{channel}/latest.version",
  "urlTemplate": "https://builds.example.invalid/Sdk/{version}/toolchain-{version}-linux-x64.tar.gz",
  "fileNameTemplate": "toolchain-{version}-linux-x64.tar.gz"
}
```

### `json-lookup`

Fetches JSON and reads a version out of it.

| Param | Required | Meaning |
|---|---|---|
| `url` | yes | JSON endpoint. May contain placeholders. |
| `path` | no | Base path to start from within the document. |
| `selectKey` | no | Key to read. |

```json
"resolver": { "kind": "json-lookup", "url": "https://api.example.invalid/releases/latest", "selectKey": "version" }
```

### `directory-listing`

Scrapes an HTML or text index page for entries matching a pattern.

| Param | Required | Meaning |
|---|---|---|
| `indexUrl` | yes | The listing page. |
| `entryPattern` | yes | Regex selecting a version from each entry. |
| `downloadUrlTemplate` | yes | How to build the download URL. |
| `fileNameTemplate` | yes | Resulting filename. |

```json
"resolver": {
  "kind": "directory-listing",
  "indexUrl": "https://example.invalid/downloads/",
  "entryPattern": "mylang-([0-9.]+)-linux-x64\\.tar\\.gz",
  "downloadUrlTemplate": "https://example.invalid/downloads/mylang-{version}-linux-x64.tar.gz",
  "fileNameTemplate": "mylang-{version}-linux-x64.tar.gz"
}
```

Use this only when no API exists. Directory markup changes without notice.

### `always-latest`

No network. The version is whatever you declare.

| Param | Required | Meaning |
|---|---|---|
| `downloadUrl` | yes | Direct download URL. |
| `fileName` | yes | Resulting filename. |
| `versionLabel` | no | Version to report. Without it, placeholders in the URL are filled from the other params. |

### `manual`

Returns nothing. Kodo never downloads; the user points at an executable themselves. Use for
toolchains that must be installed by hand.

```json
"resolver": { "kind": "manual" }
```

`manual` also opts the entry out of auto-detection and out of resolution caching, so it always
defers to the user's choice.

---

## `fallback`

Used when resolution fails, is offline, or the resolver returns nothing. Always provide one for a
toolchain Kodo is expected to install:

```json
"fallback": {
  "version": "1.0.0",
  "downloadUrl": "https://example.invalid/mylang-1.0.0-linux-x64.tar.gz",
  "fileName": "mylang-1.0.0-linux-x64.tar.gz"
}
```

---

## `sha256`

Where a resolver or fallback downloads an archive directly, add a checksum so Kodo can verify it.
The field is accepted on the LSP manifest and on fallback-style downloads; without one, Kodo
cannot detect a corrupted or tampered archive.

---

## User-supplied toolchains

A user can always point Kodo at an executable they already have, without any index entry. This is
recorded in a local manual-compiler registry that Kodo persists and reloads.

Users can also dismiss auto-detected compilers, and Kodo auto-detects common toolchains by probing
`PATH` for known executable names. Detection is conservative: a compiler is only offered when
Kodo is confident about the version, and anything ambiguous is left to the user rather than
guessed.

---

## Testing a compiler entry

Compilers are the easiest part of an extension to get wrong and the hardest to test, because
nothing fails loudly. Work through this in order:

1. **Run `run` and `build` verbatim in a terminal**, with `{file}`, `{folder}`, and `{name}`
   substituted by hand. If it does not work there, it will not work in Kodo.
2. Test with **paths containing spaces**.
3. Test **cancellation** - start a long build and stop it.
4. Test a **file in a directory with no project**. There is no `{project}` placeholder, so confirm the
   toolchain still locates its project from `{folder}`, or fails with a clear message.
5. Test with **a missing executable**: the tool should report clearly, not fail silently.
6. Test with **malformed source**, and a **multi-file project**.
7. For a resolver, **test it offline** and confirm the fallback is used.
8. Never publish a developer-specific absolute path.

---

## Adding an entry

1. Add the entry to `Indexs/CompilerIndex.json` in
   [Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions) and open a pull request.
2. `id` must be unique across the array and kebab-case.
3. `fileExtensions` must include the leading dot.
4. `languageExtensions` should list the **ids** of the Kodo language extensions this pairs with,
   so the compiler appears on the right files.
5. Include a `fallback` unless the entry is `manual`.

---

## Related guides

- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) - which kind of extension to build
- [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md#externaltools) - `externalTools` on a language extension
- [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) - the `languageExtensions` a compiler pairs with
