<h1 align="center">Kodo</h1>

<h3 align="center">Code fast. Stay light.</h3>

<p align="center">
  <img width="160" height="160" alt="Kodo Logo" src="https://github.com/user-attachments/assets/e044cdac-5434-41d7-b08f-20897a1ba771" />
</p>

<p align="center">
  <strong>A fast, lightweight code editor. No accounts. No ads. Free forever.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/github/downloads/Kodo-IDE/Kodo/total" alt="Downloads" />
  <img src="https://img.shields.io/github/commit-activity/t/Kodo-IDE/Kodo" alt="Commits" />
  <img src="https://img.shields.io/github/v/tag/Kodo-IDE/Kodo?label=latest%20version" alt="Latest Version" />
</p>

---

Kodo is built by [KerbalMissile](https://github.com/KerbalMissile) and [SS-YYC](https://github.com/SS-YYC) around a few simple ideas: your editor should stay out of your way. Quick setup, syntax highlighting via extensions, and zero friction from launch to coding. We want to make coding human again; no bloat, but still a good user experience.

**v2.1.0 is here** - Native Linux support (x64 and ARM64), independent hotfixes with verification and rollback, a completely rewritten cross-platform updater, and language-server support - still around **~87% lighter** than VSCode with no extensions installed.

Released under the [GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE).

**[🌐 Website](https://kodo-ide.github.io/)  ·  [💬 Join The Discord](https://discord.gg/cUQ6C88Z9C)**

---

## What's New in v2.1.0

- **Linux Support** - Kodo now runs natively on Linux (x64 and ARM64), with a proper integrated terminal, cross-platform updates, and a rewritten updater that works on both Windows and Linux
- **Hotfixes** - Critical fixes delivered independently of full releases, verified before being applied, with rollback support if something goes wrong
- **Extensions v2.0.0** - A complete overhaul of the `.kox` framework: language extensions can define their own Insight rules, use `IKodoPlugin` to add UI and C# code, and override certain Kodo behaviours
- **Language Server Support** - Added code completion, diagnostics, hover information, and navigation where supported by extensions
- **Rewritten Updater** - Updates are now downloaded and prepared before installation, with integrity verification, and support both Windows and Linux
- **Editor & UI** - Reworked status bar with diagnostic info, new editor tab right-click options, and Shift + Scroll for horizontal scrolling
- **Keyboard Shortcuts** - Individual shortcuts can be disabled and re-enabled directly from the Keyboard Shortcuts window
- **Performance** - Significantly improved responsiveness, large-file handling, and startup times

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

**Coming soon:** real-time collaborative editing · macOS support · experimental code optimization · and more!

---

## Getting Started

Download the latest release from the [Releases page](https://github.com/Kodo-IDE/Kodo/releases), choose the appropriate package for your platform, install Kodo, and launch it. Available package formats include `.exe`, `.deb`, `.tar.gz`, and `.AppImage`.

**Prerequisites (running the app):**

* **Windows:** Windows 10 (version 1809 or later) or Windows 11 (x64).
* **Linux:** x64 or ARM64.
* **macOS:** Not currently supported.

Kodo is distributed as a self-contained application, so a separate .NET runtime installation is not required.

---

### Building from Source

To build Kodo from source, refer to the [contribution guidelines](https://github.com/Kodo-IDE/Kodo/blob/main/CONTRIBUTING.md) for the required tools and packages.

You're welcome to clone the repository, explore the code, and make modifications. All contributions must comply with GPL-3.0 and the guidelines outlined in `CONTRIBUTING.md`.

Feedback, pull requests, and discussions are always welcome!


---

## Contributing

Contributions are welcome. The best ways to help:

- **Bug reports** - open an Issue with steps to reproduce
- **Pull Requests** - keep them focused; one change or fix per PR
- **Extensions** - build a `.kox` (language / theme / plugin) or add a compiler and submit it to the marketplace

See [CONTRIBUTING.md](https://github.com/Kodo-IDE/Kodo/blob/main/CONTRIBUTING.md) for full details and rules, including how to build and submit extensions.

---

## License

© 2026 Kodo-IDE (KerbalMissile and SS-YYC). Kodo's code is licensed under the [GNU GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE). Extension developers, there is an exception to this. Please see [EXTENSIONS_LICENSE](EXTENSIONS_LICENSE).
