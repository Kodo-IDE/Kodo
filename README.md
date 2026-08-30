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

**v2.0.0 is here** - Insight, Build/Run, plugin extensions, and a compiler marketplace, all while staying around **~70% lighter** than VSCode with no extensions installed.

Released under the [GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE).

**[🌐 Website](https://kodo-ide.github.io/)  ·  [💬 Join The Discord](https://discord.gg/cUQ6C88Z9C)**

---

## What's New in v2.0.0

- **Insight** - rebranded CodePredict with error detection, dead/unused code highlighting, per-extension `deadCodeIgnore`/`deadCodeEntryPoints`, and a dedicated settings panel (with `.md`/`.txt` blacklist)
- **Build / Run** - one-click Build/Run for projects with custom commands, Batch support, and dropdown editing
- **Plugin Extensions** - extensions can now add UI and C# code via `IKodoPlugin` (`type: plugin` + `plugin: MyPlugin.dll`, collectible `AssemblyLoadContext`)
- **Editor Experience** - overhauled Search system, improved smart syntax, and improved syntax highlighting, as well as new features.
- **Compiler Marketplace** - downloadable compilers (21 toolchains) in their own Marketplace tab, plus "add installed compiler via path"
- **UI Overhaul** - Reworked dialogs, UI elements and screens.
- **Optimizations** - Significant improvements to code and performance optimization, as well as improved startup times and lag reduction.

---

## Features

| Feature | Description |
|---|---|
| 🧩 **Extension Marketplace** | Install languages, themes, and **plugins** via `.kox` files and **compilers** via standalone installers |
| 🔍 **Insight** | Code completion, error detection, and dead-code highlighting - configurable per file type |
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

**Coming soon:** real-time collaborative editing · Linux and macOS support · experimental code optimization · improved .kox language packages · and more!

---

## Getting Started

Releases are available through installers on the [Releases page](https://github.com/KerbalMissile/Kodo/releases) - download, install, and run.

**Prerequisites (app):** Windows 10 (1809+) or Windows 11. Linux and macOS are currently not supported.

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

© 2026 KerbalMissile and SS-YYC. Licensed under the [GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE).
