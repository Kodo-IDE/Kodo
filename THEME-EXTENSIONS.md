# Theme extensions

A theme extension changes how Kodo looks: window chrome, editor background, text colors, accents.
It is the simplest extension type - pure JSON, no build step, no code.

Built-in themes are **Dark**, **Light**, and **System Default**. Custom themes come from extension packages (see below); there are no shipped theme packs in the repo.

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

`type` must be `theme` (case-insensitive). There is no `extensions` field for a theme, and no engine. Other manifest fields are parsed for any package and simply unused by the theme picker.

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

Drop the folder into `%LocalAppData%\Kodo\Extensions\` and it appears in the theme list -
the folder is watched for created/deleted/renamed packages, so dropping in a new package
reloads live. Editing `theme.json` inside an existing folder will not trigger a reload
(the watcher does not fire on in-place edits); re-adding the package or restarting Kodo picks it up.

---

## Color keys

Every key below is read and applied. Values are parsed as Avalonia brushes
(`#RRGGBB`, `#AARRGGBB`, and named colors all work); an unparsable value silently
falls back to the Kodo accent color rather than failing.

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
| `accent` | Accent color - selections, focus, links, highlights |
| `button` | Button backgrounds |
| `buttonHover` | Button hover state |
| `previewBackground` | Theme-card preview backgrounds (defaults to `editorBackground`) |
| `previewBorder` | Theme-card preview borders (defaults to `surfaceBorder`) |

### Identifying fields

| Field | Meaning |
|---|---|
| `themeId` | Identifies the theme within the package. Optional - defaults to the manifest `id`. Keep it unique within the package if you ship more than one theme. |
| `displayName` | Shown in the theme picker. Optional - defaults to the manifest `name`. |
| `baseTheme` | `"Light"` (case-insensitive) selects the light Avalonia variant; anything else (including the default `"Dark"`) selects dark. It does **not** supply colors for omitted keys. |

Omitted color keys fall back to Kodo's built-in dark constants (`#000000`, `#0E0E0E`, `#242424`, …), never to the Light palette. A `baseTheme: "Light"` theme that omits `windowBackground` gets `#000000`, not the Light `#F3F3F3` - specify every key you care about.

`accent` drives selection highlights, focus rings, and other chrome. It does not drive the caret
(that follows `primaryText`) or link underlines (hardcoded blue), so don't expect those to follow it.

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

A single object also works. The array form is what lets one package ship a coordinated family.

Rules:
- Keep `themeId` unique **within the package**. A duplicate is not dropped, but the first entry
  with that id wins - the duplicate is unreachable in the picker.
- Omitted keys fall back to Kodo's built-in dark constants (see above), not to the `baseTheme`
  palette, so a partial theme is valid but its omitted areas will be dark regardless of `baseTheme`.
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

`manifest.json` should sit at the archive root (Kodo also tolerates a nested copy). See
[KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md#4-packaging).

---

## Checking a theme properly

A theme that looks fine on an empty file can still be unreadable in practice. Before publishing,
open a file that exercises everything and check:

- [ ] Code containing **every** token category - comments, strings, numbers, keywords, types
- [ ] A **diagnostic** - is the squiggle visible against the editor background?
- [ ] A **selection** - is the selected text distinguishable from unselected?
- [ ] A **hover popup** - readable text, visible border
- [ ] **Menus** and dropdowns
- [ ] **Inactive** controls - disabled buttons, unfocused tabs
- [ ] The **terminal panel** if your theme leaves it dark
- [ ] Light *and* dark surroundings if `baseTheme` allows following the OS

The most common failure is a `mutedText` too close to its background, which makes secondary text
illegible while everything else looks correct.

---

## Troubleshooting

| Symptom | Check |
|---|---|
| Theme does not appear | `manifest.json` at the package root; `type` is `theme`; `theme.json` is valid JSON |
| Theme appears but colors do not change | the keys are spelled exactly as above; an unparsable color silently falls back to the Kodo accent instead of failing |
| Only some colors change | omitted keys fall back to Kodo's built-in dark constants, not the `baseTheme` palette - specify every key you care about |
| Multi-theme pack shows one entry | `theme.json` is an object, not an array |
| An entry is missing from a pack | duplicate `themeId` (first entry with that id wins) |
| Editor text is hard to read | `mutedText` contrast against `editorBackground` |
| Changes are not picked up | the folder watcher only fires on created/deleted/renamed packages, not in-place edits - re-add the package, or restart |

---

## Related guides

- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) - which kind of extension to build
- [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) - manifest fields and packaging
- [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) - syntax colors come from the language, not the theme
