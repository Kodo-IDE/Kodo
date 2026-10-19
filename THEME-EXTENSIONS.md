# Theme extensions

A theme extension changes how Kodo looks: window chrome, editor background, text colors, accents.
It is the simplest extension type — pure JSON, no build step, no code.

Shipped examples: **Dark Themes**, Light Themes, High-Contrast Themes.

---

## How a theme is put together

A theme package is two files:

```text
my-theme/
├── manifest.json     type must be "theme"
├── theme.json        the colors
└── icon.png          optional
```

### `manifest.json`

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

`type` must be exactly `theme`. There is no `extensions` field for a theme, and no engine.

> Do not add an `icon` field. Kodo finds `icon.png` (preferred) or `icon.svg` (fallback) by
> filename convention. Some published packages include `"icon": "icon.png"` out of habit; it is
> ignored.

### `theme.json`

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

Drop the folder into `%LocalAppData%\Kodo\Extensions\` and it appears in the theme list immediately —
the folder is watched, so later edits reload live.

---

## Color keys

Every key below is read and applied. Values are hex colors, `#RRGGBB` or `#AARRGGBB`.

| Key | Applies to |
|---|---|
| `windowBackground` | The main window background |
| `editorBackground` | The text editor background |
| `topBar` | Title bar / top strip |
| `sidebar` | Explorer and side panels |
| `card` | Card and grouped-panel surfaces |
| `primaryText` | Main body text |
| `mutedText` | Secondary and inactive text |
| `surfaceBorder` | Borders and dividers |
| `accent` | Accent color — selections, focus, links, highlights |
| `button` | Button backgrounds |
| `buttonHover` | Button hover state |
| `previewBackground` | Hover/preview popup backgrounds |
| `previewBorder` | Hover/preview popup borders |

### Identifying fields

| Field | Meaning |
|---|---|
| `themeId` | Unique within the package. Required — it is how Kodo tells your themes apart. |
| `displayName` | Shown in the theme picker |
| `baseTheme` | `"Dark"` or `"Light"`. Decides which built-in palette unused areas fall back to. |

`accent` is the most consequential value. It drives selection highlights, the caret, focus rings,
and link underlines, so a poor choice is far more noticeable than a background tweak.

**Syntax colors are not themed here.** Editor token colors come from the *language* extension's
`colorTokens` or from the server. See
[LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md). A theme sets the editor's background and text,
not the syntax palette.

---

## Shipping several themes in one package

Make `theme.json` an **array** of theme objects. Each becomes its own selectable entry, grouped
together in the picker under the package name.

```json
[
  {
    "themeId": "midnight",
    "displayName": "Midnight",
    "baseTheme": "Dark",
    "windowBackground": "#0D0D0D",
    "editorBackground": "#0A0A0A",
    "primaryText": "#E8E8E8",
    "mutedText": "#888888",
    "accent": "#3B82F6"
  },
  {
    "themeId": "slate",
    "displayName": "Slate",
    "baseTheme": "Dark",
    "windowBackground": "#1B1D21",
    "editorBackground": "#21242A",
    "primaryText": "#E4E6EB",
    "mutedText": "#9098A8",
    "accent": "#7C9CF0"
  }
]
```

A single object also works. The array form is what lets one package ship a coordinated family —
the shipped Dark Themes pack contains 28 entries this way.

Rules:
- Every `themeId` must be unique **within the package**.
- Omitted keys fall back to the `baseTheme` built-in palette, so a partial theme is valid.
- The `manifest.json` `name` is the group label; `displayName` is the individual entry.

---

## Packaging

```powershell
Compress-Archive -Path .\my-theme\* -DestinationPath .\my-theme-1.0.0.zip
Rename-Item .\my-theme-1.0.0.zip .\my-theme-1.0.0.kox
```

```bash
(cd my-theme && zip -r ../my-theme-1.0.0.kox . -x "*.pdb" "*.tmp")
```

`manifest.json` must land at the archive root. See
[KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md#4-packaging).

---

## Checking a theme properly

A theme that looks fine on an empty file can still be unreadable in practice. Before publishing,
open a file that exercises everything and check:

- [ ] Code containing **every** token category — comments, strings, numbers, keywords, types
- [ ] A **diagnostic** — is the squiggle visible against the editor background?
- [ ] A **selection** — is the selected text distinguishable from unselected?
- [ ] A **hover popup** — readable text, visible border
- [ ] **Menus** and dropdowns
- [ ] **Inactive** controls — disabled buttons, unfocused tabs
- [ ] The **terminal panel** if your theme leaves it dark
- [ ] Light *and* dark surroundings if `baseTheme` allows following the OS

The most common failure is a `mutedText` too close to its background, which makes secondary text
illegible while everything else looks correct.

---

## Troubleshooting

| Symptom | Check |
|---|---|
| Theme does not appear | `manifest.json` at the package root; `type` is exactly `theme`; `theme.json` is valid JSON |
| Theme appears but colors do not change | the keys are spelled exactly as above; `themeId` is present and unique |
| Only some colors change | omitted keys fall back to `baseTheme` — that is expected, not a bug |
| Multi-theme pack shows one entry | `theme.json` is an object, not an array |
| An entry is missing from a pack | duplicate `themeId` |
| Editor text is hard to read | `mutedText` contrast against `editorBackground` |
| Changes are not picked up | the folder watcher does not recurse and does not fire on in-place edits — re-save, or restart |

---

## Related guides

- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) — which kind of extension to build
- [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) — manifest fields and packaging
- [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) — syntax colors come from the language, not the theme
