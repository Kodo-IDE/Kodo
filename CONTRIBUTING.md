# Contributing to Kodo

Thanks for your interest in contributing! Kodo is a small project built by two people, so every contribution genuinely matters. This document covers everything you need to know to get started.

Detailed information regarding individual types of contributions can be found in sibling files to this document. Please refer to this as a quick-start guide, and find detailed information in this document's sibling documents.

Examples for all types of extensions can be found in [Kodo-Extension-Template](https://github.com/Kodo-IDE/Kodo-Extension-Template).

---

## Packages

Kodo uses these NuGet packages:
- `Avalonia` 12.1.3
- `Avalonia.Desktop` 12.1.3
- `Avalonia.AvaloniaEdit` 12.0.0
- `Avalonia.Themes.Fluent` 12.1.3
- `Avalonia.Fonts.Inter` 12.1.3
- `AvaloniaUI.DiagnosticsSupport` 2.2.3
- `DiscordRichPresence` 1.6.1.70
- `Svg.Controls.Skia.Avalonia` 12.0.0.17

---

## Getting Set Up

To run Kodo locally, you'll need:

1. [.NET](https://dotnet.microsoft.com/en-us/download) minimum version 10
2. Change your directory to Kodo's source folder: `cd Kodo\Source` (from the repository root - e.g. `cd path\to\Kodo\Kodo\Source` if you cloned to `path\to\Kodo`)
3. For best results, run `dotnet build Kodo.csproj` to catch any errors. This is optional.
4. Run `dotnet run` - it'll take a few seconds then open up

That's it. No complicated build pipeline, no extra tools.

> **Plugin authors:** build `Kodo.csproj` once first so that your plugin can find `Kodo\Source\bin\Debug\net10.0\Kodo.dll` (`Source\bin\Debug\net10.0\Kodo.dll` from the repo root; `..\..\Kodo\Kodo\Source\bin\Debug\net10.0\Kodo.dll` from `TemplatePlugin` - `HintPath` with `Private=false`).

---

## Rules

1. **AI Integration**
   - **a)** No AI integration in native/default Kodo, full stop.
   - **b)** Extensions may add or use AI, but only where the effect is minimal and scoped. For example: enhancing an existing feature like CodePredict with AI is allowed; adding a general-purpose AI chat interface is not. Acceptance of AI-related extensions is at the maintainer's discretion during PR review, even if a submission technically fits these guidelines.

2. **Code Quality**
   - PRs to the main Kodo app (not extensions) must be high-quality and add clear, meaningful value.
   - Bloat, redundant functionality, or low-value features will be rejected, regardless of code quality. If we need more information about your PR's contents, we'll make an attempt to contact you.

---

## How to Contribute

### Reporting Bugs

If something's broken, open an [Issue](https://github.com/Kodo-IDE/Kodo/issues) and describe:
- What you were doing when it happened
- What you expected to happen
- What actually happened
- Your OS and .NET version if relevant

If you have Aptabase data tracking enabled, slimmer crash logs are sent automatically, with them being sent to us without revealing any personal information. If you'd like to describe the error better, please open an Issue! (Tip: if an error dialog appears, use the Report on GitHub button - this will auto-fill your Issue with all the information we need.)

### Submitting Changes

1. Fork the repo
2. Make your changes on a new branch
3. Open a Pull Request with a clear description of what you changed and why

Please keep PRs focused, one thing per PR makes it much easier to review. If you're planning something large, open an Issue first so we can discuss it before you put in the work.

### Code Style

Look at the existing code and match it. A few things to keep consistent:

- Instead of a per-file changelog comment, make a good PR - a clear title and description of what you changed and why goes a long way for review.
- Keep comments descriptive but not excessive - explain *why*, not just *what*
- Don't leave dead code or commented-out blocks behind

---

## License

By contributing, you agree that your contributions to Kodo's codebase are licensed under [GPL v3.0](https://github.com/Kodo-IDE/Kodo/blob/main/LICENSE), and that your extensions are licensed under the [EXTENSIONS_LICENSE](EXTENSIONS_LICENSE).

---

## Questions?

Jump into the [Discord](https://discord.gg/cUQ6C88Z9C), we're always here to help.
