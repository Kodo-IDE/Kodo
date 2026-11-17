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

**Kodo v2.3.0** redesigns the UI around a shared token system, rebuilds the terminal around an always-visible, resizable sidebar, adds project-aware file icons, ships a much more capable engine for extensions, and introduces KodoIcons - our custom-drawn SVG icon set. Announcement banners can now appear right above the editor for real-time posts from Kodo devs.

A fresh Kodo install is **15.4%** of VS Code's initial size (i.e. **84.6%** less disk). In ratio terms, a fresh install of VS Code is **6.48x** bigger. 

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

## What's New in v2.3.0

* **Smart File Icons** - project-aware Explorer icons: .NET, Rust, Go, Node, Python, CMake and other project markers get their own labels, as do Git files, licenses, READMEs, changelogs, lockfiles, and folder roles (source, tests, docs, CI, dependencies). Extensions can declare their own labels via `fileIcon` and `fileIcons`.
* **Extensions, supercharged** - an expanded `ILangRulesProvider` with asynchronous analysis, cancellation, document and workspace context (up to 128 open documents), diagnostic codes, tags, related locations, and diagnostic-aware code actions. Analysis runs in Kodo's isolated Insight worker, and a conformance tool in Kodo-Extension-Template validates your provider before you publish.
* **UI overhaul** - a unified token system across all of Kodo's surfaces and dialogs, micro-animations (toggleable in Accessibility Settings), a reworked editor toolbar with a Refresh Open Editors button, and settings reorganized into subsections you can find by Search.
* **Terminal** - redesigned UI: tabs replaced by a sidebar, making the terminal host itself bigger; the sidebar is resizable, including auto-resize on double-click.
* **The Editor** - the Explorer pane now splits vertically to show the File Explorer and Git Source Control at the same time; the Keyboard Shortcuts dialog is now part of Settings.
* **Smart build/run** - compiler dialogs reworked to pick up exit codes and adjust the UI accordingly.
* **Announcement Banners** - real-time banners from Kodo devs above the editor, with optional action buttons; critical banners stay until the announcement is removed.
* **KodoIcons** - our custom-drawn, standalone SVG icon set now powers the app's UI.
* **Removals** - the Encoding, Line Ending, and Tab Size dialogs are gone; those actions now live in the command palette.

---

## Features

| Feature                             | Description                                                                                             |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------- |
| 🧩 **Extension Marketplace**        | Install languages, themes, and **plugins** via `.kox` files and **compilers** via standalone installers |
| 🔍 **Insight**                      | Code completion, error detection, and dead-code highlighting - configurable per file type               |
| 🔨 **Build / Run**                  | Compile and run projects from within Kodo with custom commands                                          |
| 🧑‍💻 **Integrated Terminal**       | Run code from within Kodo, no extra terminal needed                                                     |
| 🎨 **Themes**                       | Built-in Dark, Light, and System Default modes + custom extension themes                                |
| 🗂️ **Smart File Icons**          | Project markers, repo files, READMEs and folder roles get their own Explorer labels; extensions can declare theirs too |
| 📁 **Folder Support**               | Browse, auto-refresh, and resize the file explorer; rename inline; split vertically with Git Source Control           |
| 🔎 **Search**                       | Find in file, file-name search, and project-wide search (`Ctrl+Shift+F`)                                |
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

| Benchmark | Kodo Result | VS Code Result | Kodo's Advantage |
|---|---:|---:|---|
| Private Memory (Welcome Idle) | **241 MB** | 2,213 MB | ~89% less private memory |
| Large Project Memory (2,501 files / 51 MB) | **~292 MB WS** | ~2.2 GB WS | ~87% less working-set memory |
| Idle CPU | **1.9–2.1%** | 16.5% | ~8× lower measured CPU usage |

These results were gathered from the same machine, opening the same project during our benchmarks.

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
