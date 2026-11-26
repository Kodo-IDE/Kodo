<h1 align="center">Kodo</h1>

<h3 align="center">Code fast. Stay light.</h3>

<p align="center">
  <img width="160" height="160" alt="Kodo Logo" src="https://github.com/user-attachments/assets/e044cdac-5434-41d7-b08f-20897a1ba771" />
</p>

<p align="center">
  <strong>A fast, lightweight code editor. No accounts. No ads. Free forever.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/github/downloads/Kodo-IDE/Kodo/total?style=for-the-badge&label=Downloads&color=8C00FF" alt="Downloads" />
  <img src="https://img.shields.io/github/v/release/Kodo-IDE/Kodo?style=for-the-badge&label=Latest%20Release&color=8C00FF" alt="Latest Release" />
  <img src="https://img.shields.io/github/commit-activity/t/Kodo-IDE/Kodo/main?style=for-the-badge&label=Commits&color=8C00FF" alt="Commits on main" />
  <img src="https://img.shields.io/github/commit-activity/w/Kodo-IDE/Kodo/main?style=for-the-badge&label=Weekly%20Commits&color=8C00FF" alt="Weekly Commit Activity on main" />
  <img src="https://img.shields.io/github/last-commit/Kodo-IDE/Kodo/main?style=for-the-badge&label=Last%20Commit&color=8C00FF" alt="Last Commit on main" />
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-8C00FF?style=for-the-badge" alt="Windows and Linux"/>
  <img src="https://img.shields.io/badge/License-GPL--3.0-8C00FF?style=for-the-badge" alt="GPL-3.0 License"/>
</p>

---

Kodo is built by [KerbalMissile](https://github.com/KerbalMissile) and [SS-YYC](https://github.com/SS-YYC) around a few simple ideas: your editor should stay out of your way. Quick setup, syntax highlighting via extensions, and zero friction from launch to coding. We want to make coding human again; no bloat, but still a good user experience.

**Kodo v2.4.0** adds Sticky Scroll, bookmarks with their own panel, Quick Actions in the Activity Bar, Signature Help, and a personalization layer built on local, on-device learning. The Command Palette and Quick Open now reuse their overlay and keep result rows between modes, making them **3.5× faster** than v2.3.0, and workspace search reads each file once for a **38% faster** scan.

A fresh Kodo install is **14.9%** of VS Code's installed size (i.e. **85.1%** less disk). In ratio terms, a fresh install of VS Code is **6.7×** bigger.

Released under the [GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE).

<p align="center">
  <a href="https://kodo-ide.github.io/">
    <img src="https://img.shields.io/badge/Website-Visit-8C00FF?style=for-the-badge&logo=googlechrome&logoColor=white" alt="Website"/>
  </a>
  <a href="https://discord.gg/cUQ6C88Z9C">
    <img src="https://img.shields.io/badge/Discord-Join%20the%20Community-8C00FF?style=for-the-badge&logo=discord&logoColor=white" alt="Join Discord"/>
  </a>
</p>

---

## What's New in v2.4.0

* **Sticky Scroll** - nested classes and other relevant structures stay pinned at the top of the editor with their original syntax highlighting; click a sticky line to jump to it, and turn the feature off in Editor Settings.
* **Bookmarks** - bookmark a line from its line number or with `Ctrl + Alt + B`, open the Bookmarks panel with `Ctrl + Shift + B`, label and manage bookmarks across files, and see them marked on the minimap.
* **Quick Actions** - an Activity Bar section with up to three context-aware actions, with `Ctrl + .` to run the highest-ranked one. Actions adapt to editor focus, selection, diagnostics, language-server capabilities and project state, and unavailable ones are omitted.
* **Insight** - split into Code Intelligence (LSP, diagnostics, navigation, Signature Help) and Context Intelligence (learning-driven Quick Actions and palette ordering), with a Personalization Dashboard to inspect, disable or reset what has been learned locally.
* **Personalized search** - the Command Palette learns from your queries and selections to rank results over time, and can be turned off in Settings.
* **Faster palette and search** - the Command Palette and Quick Open reuse their overlay and keep result rows between modes (3.5× faster opens), and project-wide search now reads each file once instead of twice (38% faster warm scans).
* **Editor and LSP** - a clipboard combo box that adapts its primary action, undo/redo in the toolbar, a Signature Help popup, and language-server messages in the status bar.
* **Fixes** - palette results ignoring clicks, palette input going dead after reopening, incorrect truncation counts, borders rendering beneath their contents, `Ctrl + F` and `Ctrl + Shift + F` when focus is outside the editor, and editor startup stutter.

---

## Features

| Feature                             | Description                                                                                             |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------- |
| 🧩 **Extension Marketplace**        | Install languages, themes, and **plugins** via `.kox` files and **compilers** via standalone installers |
| 🔍 **Insight**                      | Code completion, error detection and dead-code highlighting, plus on-device learning for Quick Actions and Command Palette ordering, reviewable in Settings → Insight - configurable per file type |
| 🔨 **Build / Run**                  | Compile and run projects from within Kodo with custom commands                                          |
| 🧑‍💻 **Integrated Terminal**       | Run code from within Kodo, no extra terminal needed                                                     |
| 🎨 **Themes**                       | Built-in Dark, Light, and System Default modes + custom extension themes                                |
| 🗂️ **Smart File Icons**          | Project markers, repo files, READMEs and folder roles get their own Explorer labels; extensions can declare theirs too |
| 📁 **Folder Support**               | Browse, auto-refresh, and resize the file explorer; rename inline; split vertically with Git Source Control           |
| 🔎 **Search**                       | Find in file, file-name search, and project-wide search (`Ctrl+Shift+F`)                                |
| 📌 **Bookmarks**                    | Bookmark lines, label them, jump across files, and track them on the minimap                            |
| 🧷 **Sticky Scroll**                | Nested classes and structures stay pinned at the top while you scroll                                    |
| ⚡ **Quick Actions**                 | Context-aware actions in the Activity Bar — run the top one with `Ctrl + .`                              |
| ⌘ **Command Palette**                | Open files, run commands, navigate to lines and symbols, and run plugin commands                        |
| ↔ **Split Editor**                   | Edit two files side by side with independent editor groups                                                |
| 🌿 **Git Source Control**            | Review repository changes and use Git tools from the native Integrations UI                              |
| 🗺️ **Editor Minimap**               | See and scroll through a compact map of the current file                                                   |
| 💾 **Autosave**                     | Configurable autosave so you never lose work                                                            |
| 🔤 **Syntax Highlighting**          | Language support delivered through the extension system                                                 |
| 🎨 **Color Picker & Smart Editing** | Built-in color picker, change-all-occurrences, auto-closing brackets, auto-indent                       |
| 🖼️ **Image Preview**               | View image files directly in the editor, with zoom                                                      |
| 🕓 **Recent Files**                 | Jump back into recent files from the home screen                                                        |
| 🎮 **Discord Rich Presence**        | Show what you're working on in Discord (optional, toggle in Settings)                                   |
| ⚡ **Performance Mode**              | Disable live GitHub panels and debounce search for large projects                                       |
| 🔄 **Background Auto-Updates**      | Kodo checks GitHub releases and keeps the app and extensions up to date (with progress bar)             |
| 🚀 **Guided Tutorial**              | Short built-in walkthrough for first-time setup, revisitable from Settings                              |

**Coming soon:** real-time collaborative editing · macOS support · experimental code optimization · and more!

---

## Benchmarks

Measured on the same Windows machine with clean profiles and the same project, 26 November 2026. Kodo numbers come from the v2.4.0 release build.

| Benchmark | Kodo v2.4.0 | VS Code | Result |
|---|---:|---:|---|
| Fresh install on disk | **176 MB** | 1,186 MB | **6.7× smaller** |
| Private memory, welcome screen | **223 MB** | 1,035 MB | **78% less** |
| Working set, 12,000-file project | **314 MB** | 1,127 MB | **72% less** |
| Idle CPU, no project open | 1.2–1.8% of one core | **0.46%** | VS Code lower |
| Command Palette open | 54.5 ms | **51.1 ms** | within 7% |
| Quick Open | 54.6 ms | **50.6 ms** | within 8% |

Palette figures are medians of 12 alternating trials per case; memory figures are medians of 3 trials; idle CPU comes from 150–300 second process-tree CPU deltas. Install sizes exclude runtime caches created on first launch.

Project-wide search (3,000 files) returns results in **183 ms** warm and **238 ms** on the first run, measured with Kodo's internal timing marks. VS Code's search was measured with a different detector, so the two numbers are not directly comparable and no comparison is claimed.

Kodo's palettes were **190 ms** (Command Palette) and **211 ms** (Quick Open) in v2.3.0, measured with the same harness, so v2.4.0 is **3.5× faster** and now within a few milliseconds of VS Code.

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

* **Bug reports** - open an Issue with steps to reproduce
* **Pull Requests** - keep them focused; one change or fix per PR
* **Extensions** - build a `.kox` (language / theme / plugin) or add a compiler and submit it to the marketplace

See https://github.com/Kodo-IDE/Kodo/blob/main/CONTRIBUTING.md for full details and rules, including how to build and submit extensions.

---

## License

© 2026 Kodo-IDE (KerbalMissile and SS-YYC). Kodo's code is licensed under the [GNU GPL-v3.0 license](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE). Extension developers, there is an exception to this. Please see `EXTENSIONS_LICENSE`.
