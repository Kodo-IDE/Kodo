<h1 align="center">Kodo</h1>
<h3 align="center">Code fast. Stay light.</h3>

<p align="center">
  <img width="160" height="160" alt="Kodo Logo" src="https://github.com/user-attachments/assets/e044cdac-5434-41d7-b08f-20897a1ba771" />
</p>

<p align="center">
  <strong>A fast, lightweight code editor. No accounts. No ads. Free forever.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/github/downloads/KerbalMissile/Kodo/total" alt="Downloads" />
  <img src="https://img.shields.io/github/commit-activity/t/KerbalMissile/Kodo" alt="Commits" />
  <img src="https://img.shields.io/github/v/tag/KerbalMissile/Kodo?label=latest%20version" alt="Latest Version" />
</p>

---

Kodo is built by [KerbalMissile](https://github.com/KerbalMissile) and [SS-YYC](https://github.com/SS-YYC) around a few simple ideas: your editor should stay out of your way. Quick setup, syntax highlighting via extensions, and zero friction from launch to coding. We want to make coding human again; no bloat, but still a good user experience.

**v2.1.0 is here** - Linux support (x64 and ARM64), verified hotfixes, Extensions v2.0.0 with language-server support, and a cross-platform updater, all while staying around **~70% lighter** than VSCode with no extensions installed.

Released under the [GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE).

**[🌐 Website](https://kodo-ide.github.io/)  ·  [💬 Join The Discord](https://discord.gg/cUQ6C88Z9C)**

---

## What's New in v2.1.0

- **Linux support** - Kodo now runs on Linux for both **x64 and ARM64**, with native Unix PTYs for a proper terminal, plus adapted compiler/build tooling, a cross-platform updater, and an OS indicator
- **Hotfixes** - small, verified updates that ship independently of full releases, with integrity checks and rollback if a hotfix causes problems
- **Extensions v2.0.0** - `.kox` language extensions can now define their own rules for Kodo's language intelligence via `LangRules.cs`, making syntax highlighting and variable detection smarter and far less dependent on hardcoded rules
- **Language Intelligence** - language-server support through extensions, improving completion, diagnostics, hover, navigation, quick fixes, and more where supported
- **Editor & UI** - reworked editor status bar with diagnostics and UI chips, file explorer tree, and editor/terminal tabs; new tab right-click actions for LSP features and diagnostics
- **Performance** - major gains for large files, scrolling, rendering, and startup; `KodoUpdater` rewritten and trimmed to roughly **15 MB**

---

## Features

| Feature | Description |
|---|---|
| 🧩 **Extension Marketplace** | Install languages, themes, and **plugins** via `.kox` files and **compilers** via standalone installers |
| 🔍 **Insight** | Code completion, error detection, and dead-code highlighting - configurable per file type |
| 🧠 **Language Servers** | Optional LSP support through extensions - completion, diagnostics, hover, navigation, quick fixes, and rename |
| 🔨 **Build / Run** | Compile and run projects from within Kodo with custom commands |
| 🧑‍💻 **Integrated Terminal** | Run code from within Kodo, no extra terminal needed |
| 🎨 **Themes** | Built-in Dark, Light, and System Default modes + custom extension themes |
| 📁 **Folder Support** | Browse, auto-refresh, and resize the file explorer; rename inline |
| 🔎 **Search** | Find in file, file-name search, and project-wide search (`Ctrl+Shift+F`) |
| 💾 **Autosave** | Configurable autosave so you never lose work |
| 🔤 **Syntax Highlighting** | Language support delivered through the extension system |
| 🎨 **Color Picker & Smart Editing** | Built-in color picker, change-all-occurrences, auto-closing brackets, auto-indent |
| 🖼️ **Image Preview** | View image files directly in the editor, with zoom |
| 🕓 **Recent Files** | Jump back into recent files from the home screen |
| 🎮 **Discord Rich Presence** | Show what you're working on in Discord (optional, toggle in Settings) |
| ⚡ **Performance Mode** | Disable live GitHub panels and debounce search for large projects |
| 🔄 **Background Auto-Updates** | Kodo checks GitHub releases and keeps the app and extensions up to date (with progress bar) |
| 🚀 **Guided Tutorial** | Short built-in walkthrough for first-time setup, revisitable from Settings |

**Coming soon:** real-time collaborative editing · macOS support · experimental code optimization · and more!

---

## Getting Started

Releases are available on the [Releases page](https://github.com/KerbalMissile/Kodo/releases) - download the build for your platform, install, and run.

**Prerequisites (app):** Windows 10 (1809+) or Windows 11. Linux is supported on **x64 and ARM64** - grab the `.tar.gz`, `.AppImage`, or `.deb` package for your architecture. macOS is not currently supported.

**Packages:** Windows ships an `.exe` installer. Linux ships a writable `.tar.gz` archive (extract and run the included `Kodo`), an `.AppImage` (`chmod +x` then run), and a `.deb` for Debian/Ubuntu (`sudo apt install ./Kodo-*.deb`).

Stable hotfixes are applied by the updater on Windows and on writable `.tar.gz` installs. AppImage and `.deb` installs are not modified in place - download the rebuilt hotfix package instead.

Source users, please refer to [CONTRIBUTING.md](https://github.com/Kodo-IDE/Kodo/blob/main/CONTRIBUTING.md) for required packages.

If you're running from source, feel free to clone the repository and modify code. Make sure all submissions comply with GPL v3.0 and the rules in [CONTRIBUTING.md](https://github.com/Kodo-IDE/Kodo/blob/main/CONTRIBUTING.md). Feedback, PRs and discussions are always and will always be welcome!

---

## Contributing

Contributions are welcome. The best ways to help:

- **Bug reports** - open an Issue with steps to reproduce
- **Pull Requests** - keep them focused; one change or fix per PR
- **Extensions** - build a `.kox` (language / theme / plugin) or add a compiler and submit it to the marketplace

See [CONTRIBUTING.md](https://github.com/Kodo-IDE/Kodo/blob/main/CONTRIBUTING.md) for full details and rules, including how to build and submit extensions.

---

## License

© 2026 KerbalMissile and SS-YYC. Licensed under the [GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE). There is an exception to this, see [EXTENSIONS_LICENSE](EXTENSIONS_LICENSE).
