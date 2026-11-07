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
  <img src="https://img.shields.io/github/commit-activity/t/Kodo-IDE/Kodo/dev?style=for-the-badge&label=Commits&color=8C00FF" alt="Commits on dev" />
  <img src="https://img.shields.io/github/last-commit/Kodo-IDE/Kodo/dev?style=for-the-badge&label=Last%20Commit&color=8C00FF" alt="Last Commit on dev" />
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-8C00FF?style=for-the-badge" alt="Windows and Linux"/>
  <img src="https://img.shields.io/badge/License-GPL--3.0-8C00FF?style=for-the-badge" alt="GPL-3.0 License"/>
</p>

---

Kodo is built by [KerbalMissile](https://github.com/KerbalMissile) and [SS-YYC](https://github.com/SS-YYC) around a few simple ideas: your editor should stay out of your way. Quick setup, syntax highlighting via extensions, and zero friction from launch to coding. We want to make coding human again; no bloat, but still a good user experience.

**Kodo v2.2.0** adds a command palette, side-by-side editing, native Git integration, and an editor minimap. It also brings faster startup, smoother large-file editing, and broad editor and language-server fixes.

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

## What's New in v2.2.0

* **Command Palette** - Search and open files, run editor commands, navigate to lines and symbols, and invoke commands registered by plugins.
* **Split Editor** - Work in two editor groups side by side, with pane-aware tabs and language-server documents.
* **Native Git** - Use Git Source Control from Kodo and toggle it in Integrations Settings.
* **Minimap and Navigation** - Scroll through the editor minimap, navigate between problems and code locations, and use per-pane breadcrumbs.
* **Editor and Settings** - Refreshed Settings, integrated encoding, line-ending, and indentation choices, and more commands for common editor actions.
* **Performance and Reliability** - Faster startup, less lag when closing folders, improved large-file handling, and fixes across editor rendering, split tabs, completions, and language-server startup.

---

## Features

| Feature                             | Description                                                                                             |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------- |
| 🧩 **Extension Marketplace**        | Install languages, themes, and **plugins** via `.kox` files and **compilers** via standalone installers |
| 🔍 **Insight**                      | Code completion, error detection, and dead-code highlighting - configurable per file type               |
| 🔨 **Build / Run**                  | Compile and run projects from within Kodo with custom commands                                          |
| 🧑‍💻 **Integrated Terminal**       | Run code from within Kodo, no extra terminal needed                                                     |
| 🎨 **Themes**                       | Built-in Dark, Light, and System Default modes + custom extension themes                                |
| 📁 **Folder Support**               | Browse, auto-refresh, and resize the file explorer; rename inline                                       |
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

These results were gathered from the same machine, opening the same projects during our benchmarks.

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
