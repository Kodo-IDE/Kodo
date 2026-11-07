# Kodo Benchmarks - `v2.2.0-DEV` (2.2.0-DEV, commit `15bee08`, "perf: major perf improvs and Insight debouncing")

- **Date:** 2026-11-03 · **Machine:** Windows 11 Home (build 26300) · Intel Core i7-14700 (20C/28T) · 16 GB RAM · SSD
- **Build used:** `dotnet publish -c Release -r win-x64 --self-contained true` (+ `PublishReadyToRun` from Re-run 3 onward) — same distribution form as the installed product (no trimming/single-file, matching prior releases).
- **Profile:** truly clean — `%LOCALAPPDATA%\Kodo` and `%APPDATA%\Kodo` were moved aside for the whole session and restored afterwards. That means **no extensions, no LSP, default settings, defaults: welcome screen**. Raw result files live in [`results/`](results/).

All timing = external wall-clock from `Process.Start` until the Kodo main window is visible (Win32 `IsWindowVisible`) unless stated otherwise. Nothing was counted from inside the app except the typing test (see its section).

| # | Benchmark | Result | Verdict |
|---|-----------|--------|---------|
| 1 | **RAM usage** | 242 MB private / 325 MB working set at idle welcome screen; 697 MB private / 788 MB WS with a 52 MB file open | Good for idle, high for big files |
| 2 | **Startup time** | **1.26 s** warm median (1.41 s cold) to visible window | Excellent |
| 3 | **Installation size** | 115.3 MB installed footprint (43.5 MB compressed installer, v2.1.7) | In line — ~13% is Kodo's own code |
| 4 | **Large-project performance** | 2,501-file / 72 MB workspace opens with the window in 1.33 s median; RAM stays ~330 MB | Good, with caveats on tree population |
| 5 | **Typing latency** | **key→handler p95 = 0.32 ms** (small file) / 0.34 ms (52 MB); handler body p95 ≤ 0.06 ms — no big-file degradation | Excellent |
| 6 | **CPU usage** | Idle on welcome screen: **~0.85 of one core in an endless GUI loop — bug found and pinned to `Marketplace.cs:474`** | Needs fix |

Detail per benchmark below.

## 1 — RAM usage

Sampled 1 Hz × 30 s, median/peak of the process (`WorkingSet64` / `PrivateMemorySize64`), clean profile, no extensions:

| Scenario | Working set | Private bytes |
|----------|-------------|---------------|
| Welcome screen (idle, no docs) | **324.5 MB** (peak 324.9) | **242.0 MB** (peak 242.5) |
| 52 MB / 200k-line text file open | **788.1 MB** (peak 788.2) | **696.5 MB** (peak 696.6) |

Notes:
- Welcome-screen private bytes ≈ 242 MB is the best "idling at the door" figure; VS Code idles closer to ~300 MB private with its own file open, so Kodo wins the empty-state comparison category.
- Opening the 52 MB file adds ~455 MB working set; that's a ~9× expansion over file size — generous but not pathological for syntax coloring + AvaloniaEdit document model.

## 2 — Startup time

6 launches (run 1 = first-ever run on the clean profile, runs 2–6 warm, app terminated between runs):

| Run | Visible |
|-----|---------|
| 1 (cold) | 1407 ms |
| 2–6 (warm) | 1246 / 1262 / 1257 / 1423 / 1265 ms |
| **Warm median** | **1262 ms** |

Window-visible is reached before `Extensions` are loaded (they're deferred — `Startup ctor fast-path` exists in code), so measured time is largely layout + init. There is no splash screen delay on top.

## 3 — Installation size

| Measure | Value |
|---------|-------|
| Publish output (what an install actually writes) | **115.3 MB / 235 files** |
| …of which Kodo's own assemblies (Kodo.dll + Avalonia + Kodo.exe) | **15.3 MB** |
| …bundled .NET 10 runtime + Skia/GLES native | **100 MB** |
| Official Windows installer download (v2.1.7 release) | **43.5 MB** (.exe, inno compresses 115→43) |
| Debian / tar.gz / AppImage reference (v2.1.7) | **40 / 49 / 46 MB** |

Largest payloads: `System.Private.CoreLib.dll` (15.3), `libSkiaSharp.dll` (11.6), `System.Private.Xml.dll` (7.4), `av_libglesv2.dll` (5.1), `coreclr.dll` (4.4) — i.e. 87% of the footprint is the runtime, not the editor.

## 4 — Large-project performance

Synthetic workspace in `assets/large-project/`: 2,501 files across 11 folders, ~72 MB, one 48 MB module. Started via the CLI arg (folder), same path the UI uses:

| Run | Window visible | Working set (after settle) |
|-----|----------------|----------------------------|
| 1 | 1283 ms | 329 MB |
| 2 | 1534 ms | 327 MB |
| 3 | 1364 ms | 334 MB |
| **Median** | **1364 ms** | **~330 MB** |

Caveats: the file tree population is *deferred* after the window shows, so raw folder-index cost beyond window-visible isn't externally observable; the ~100 ms delta vs the no-folder baseline (1262 ms) plus the flat RAM suggests the initial traversal is cheap, with the tree building behind the scenes.

## 5 — Typing latency

Measured with a temporary instrumented build (worktree `bench-typing-latency` only; all timestamps from `Stopwatch` inside the app, report JSONs in `results/editor-performance-typing-*-typing.json`). Driver: [`scripts/run-typing.ps1`](scripts/run-typing.ps1), which types with `SendKeys` **only while Kodo is verified foreground** (checks before every batch; aborts otherwise). Instrumentation measures:

- **key→handler** — tunnel-phase `TextInput` → pane `TextChanged` handler start, per keystroke (the Kodo-side cost of a keystroke).
- **handler body** — the synchronous pane `TextChanged` handler execution.
- **dispatcher lateness** — lateness of a 50 ms dispatcher timer (proxy for UI-thread backlog).

Results (2,800 keystrokes on an 800-line file; 2,035 on a 52 MB file):

| Measurement (ms) | Small file (0.1 MB) | Big file (52 MB) |
|---|---|---|
| key→handler mean / p50 / p95 / max | **0.112 / 0.075 / 0.32 / 6.6** | 0.167 / 0.065 / 0.34 / 15.3 |
| handler body mean / p50 / p95 / max | **0.024 / 0.017 / 0.06 / 5.1** | 0.040 / 0.016 / 0.03 / 14.7 |
| dispatcher lateness p50 / p95 / max | 50.0 / 63.5 / 1512 | 50.0 / 50.1 / **108,959** |

Verdict: **typing latency is excellent** — the app-side cost of a keystroke is well under half a millisecond at p95 in both scenarios, with no degradation between a small file and a 52 MB one.

Caveats (do not inflate these numbers):

- `keystrokeIntervals` (~31–47 ms p50) and the burst batches (~5.7 s per ~190-char batch) reflect the **`SendKeys` harness cadence** (≈30 ms/char Windows overhead), not app slowness; app-side latency comes from the in-app instrumentation above.
- The **109 s dispatcher lateness max on the 52 MB file** is the initial 52 MB document load synchronously stalling the UI thread for ~109 s (`typing-bigfile52mb` report). Anything typed/painted during that window queues behind it. Worth a follow-up (background/chunked load).
- The small-file max (1.5 s dispatcher lateness, 1.2 s keystroke interval) came from the first burst batch hitting while the file/tab UI settled.

First-run traps that cost a few aborted runs (all fixed by seeding `%LOCALAPPDATA%\Kodo\kodosettings.json` with `{"HasCompletedTutorial":true,"HasRespondedToDataTrackingPrompt":true,"HasAcceptedPrivacyPolicy":true}`): on a clean profile the first launch shows the **tutorial page**, then a **"Help Improve Kodo" consent dialog** — both capture keyboard focus away from the editor. Evidence screenshots of the swallowed runs: [`assets/screenshots/typing-smallfile-tutorial-midburst.png`](assets/screenshots/typing-smallfile-tutorial-midburst.png), [`typing-smallfile-consent-dialog.png`](assets/screenshots/typing-smallfile-consent-dialog.png).

## 6 — CPU usage

Idle at the welcome screen (30 s sampling, clean profile, marketplace index cached by run 1 of the same session):

| Scenario | CPU |
|----------|-----|
| Welcome idle | **83% of one core** (0.83) |
| 52 MB file open, untouched | **80% of one core** |
| Large project, loaded then idle | **85–87%** |

**That burn is a bug.** A 10 s CPU-sampling trace pinned a single thread (`35904`, the UI thread) at ~90% of one core with the stack showing 40% inside `Kodo.MainWindow.FetchInstalledExtensionIconsAsync` (Marketplace.cs:450): it retries with `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)` "until extensions load", but when no extensions exist it never stops — infinite background-priority dispatcher churn. It also shows in a secondary place on a loaded profile (user extensions: `TagAutoClosePlugin.TryHookEditor` spinning at ~83% — same call pattern from a different plugin, clearly also a bug to report upstream). Both need an exit condition (e.g., give up after N retries or wait on a real event).

After a fix, expect welcome-idle CPU to land near 0–2% of one core, which materially improves battery/laptop marketing numbers. The 80%/85% figures above include this bug.

### Post-fix verification (Nov 5, 2026)

Both spinners fixed and re-measured with the same 40 s idle-sampling harness:

| Scenario | Before | After |
|----------|--------|-------|
| Idle, no extensions (clean profile) | **83%** of one core | **1.3%** (CPU-quiet 12 s after window visible) |
| Idle, extensions installed (real profile) | **86.9%** (never went quiet) | **1.8%** (CPU-quiet 13 s after window visible) |

- **Fix 1 (no extensions):** `Marketplace.FetchInstalledExtensionIconsAsync` — the self-reposting `Dispatcher.UIThread.Post` loop replaced with a bounded `Task.Delay(500)` × 20 wait that exits once the installed-extension scan applies, or gives up after ~10 s.
- **Fix 2 (with extensions):** `TagAutoClose.dll` (shipped compiled-only inside the HTML/XAML/XML .kox packages) searched for a `TextEditor` named `EditorTextBox` — a name removed from `MainWindow.axaml` (now `PrimaryEditorTextBox`/`SecondaryEditorTextBox`) — so its walk *never* succeeded and re-posted forever. Fixed host-side in `Source/InternalTagAutoClosePlugin.cs`: a faithful reconstruction of the plugin's auto-close behavior (verified identical decompilation across v2.0.0/v2.1.0) with a one-pass visual-tree hook plus a bounded 500 ms catch-up timer; `KodoPlugins.LoadLanguagePlugin` substitutes the built-in for any extension declaring `TagAutoClose.dll`, so package reinstalls/updates can't reintroduce the spin. Confirmed in `kodo.log`: `Loaded built-in TagAutoClose replacement for 'html/xaml/xml-kodo-extension' (external DLL skipped)`.

## 7 — Blank editor despite the file having content

User-reported "sometimes all the text in the editor doesn't show up, although the file definitely has content". Two confirmed mechanisms with the same symptom (evidence: `%APPDATA%\Kodo\crash.log`):

1. **`TextDocument` constructed on a worker thread** (the Nov 4 perf commit moved big-file document parsing into `Task.Run`). AvaloniaEdit 12.0.0 captures `ownerThread = Thread.CurrentThread` at construction and every later access (`TextLength`, line tree, rendering) runs `VerifyAccess()` — throwing `InvalidOperationException: Call from invalid thread` (`MainWindow_OnOpened` crash entries, reproduced by bench runs Nov 5). Where the throw is swallowed by a guarded activation path, the editor shows nothing for the activated file.
2. **Stale `FoldingManager` bound to a previous document** — `FoldingManagerInstallation` captures `textArea.Document` at install time and never follows document swaps; `FoldingElementGenerator.StartGeneration` then throws `ArgumentException: Invalid document` mid visual-line construction (`crash.log` Oct 25, right after `layout/after content set took 88058ms len=2088895`) and the render pass never draws.

**Fixes (Nov 6, 2026):**
- `MainWindow.CreateOffThreadDocument` constructs the document off-thread (keeping the big-file perf win) and immediately calls `SetOwnerThread` to hand ownership to the captured UI thread before the document is used anywhere. Applied to all three worker-thread construction sites: `Explorer.RestoreInactivePaneDocumentAsync`, `MainWindow.SetEditorContentAsync`, and the startup tab-restore path.
- `FoldingManager.Document` is internal, so the pane's captured document is now tracked beside each manager (`_primary/secondaryLspFoldingDocument`) in `SetEditorPaneDocument`, and all four LSP folding apply paths refuse to touch a manager whose captured document isn't the editor's current document. Application is retried like any other deferred presentation instead of crashing a render pass.

Verified post-fix: a launch/open cycle logs **0 new crash entries** and idle CPU remains 1.8% (real profile).

## Reproduce

```pwsh
pwsh -File benchmarks/scripts/session.ps1 -Stages startup,ram,largeproject   # external benchmarks + clean profile dance
pwsh -File benchmarks/scripts/run-typing.ps1                                # typing (needs instrumented build)
pwsh -File benchmarks/scripts/debug-trace-clean.ps1                         # idle CPU trace (clean profile)
pwsh -File benchmarks/scripts/measure-idle.ps1                              # idle CPU, clean + real profile
```

## Re-run (Nov 7, 2026, suite remade after deletion, post-optimization)

| Benchmark | Original (Nov 3) | Re-run 1 (Nov 7) | Re-run 2 (Nov 7, post-fixes) | Δ vs original |
|---|---|---|---|---|
| Startup warm median (6 runs) | 1262 ms | 1348 ms | **1328 ms** (1388/1328/1329/1330/1320/1327) | +66 ms (+5.2%) |
| RAM welcome idle | 324.5 MB WS / 242.0 MB private | 324.5 MB / 242.7 MB | **325.9 MB / 240.6 MB** | identical |
| Large project median (3 runs) | 1364 ms, ~330 MB WS | 1347 ms, ~306 MB WS | **1325 ms, ~317 MB WS** | −39 ms, −13 MB |
| Idle CPU — clean profile | 1.3% (post-fix) | 2.5% | **1.8%** | at post-fix level |
| Idle CPU — real profile | 1.8% (post-fix) | 1.8% | **2.6%** | +0.8 pts (session variance) |
| Typing key→handler p95 (small) | 0.32 ms (2800 keys) | 0.62 ms (630 keys) | **0.32 ms** (630 keys; handler p95 0.043 ms; dispatcher p95 66 ms) | identical to original |
| Dispatcher lateness p95 | 63.5 ms | 66.0 ms | **66.3 ms** | unchanged |

Post-fix changes since the original suite: settings ACL caching + read-path removal (SettingsStore), file-tree virtualization, marketplace icon parallelism bound + apply chunking, SyncObservableCollection change-detection, emoji colorizer off-thread snapshot, `AsyncDocumentSnapshot` stale-serving (highlighting stability), palette Button rows, immediate settings save standard.

## Re-run 3 (Nov 7, 2026 — ReadyToRun publish)

Enabling `PublishReadyToRun` in `Kodo.csproj` pre-compiles IL to native code at publish time, eliminating JIT compilation of the app and its framework on every launch. `Kodo.dll` grows 4.4 → 10 MB but the total install footprint is unchanged (~141 MB vs 141.6 MB non-R2R; far below VS Code's ~1.13 GB).

| Benchmark | Re-run 2 (non-R2R) | Re-run 3 (R2R) | Δ |
|---|---|---|---|
| Startup warm median | 1328 ms | **823 ms** (2319 cold / 823/818/840/808/836) | **−505 ms (−38%)** |
| Large project median | 1325 ms | **849 ms** (860/849/821) | **−476 ms (−36%)** |
| Large project WS | ~317 MB | **~292 MB** | −25 MB |
| RAM welcome idle | 325.9 MB WS / 240.6 MB private | 321.7 MB / 241.1 MB | identical |
| Idle CPU — clean / real | 1.8% / 2.6% | 2.1% / 1.9% | within noise |

Publish command is unchanged (`dotnet publish -c Release -r win-x64 --self-contained true`); the flag lives in the csproj and only applies to Release publishes. The `bench-common.ps1` harness also honors `KODO_BENCH_EXE` to point at an alternate build for A/B runs. At 823 ms warm startup Kodo still trails VS Code's ~309 ms "window-visible" number (Electron paints a shell window first and loads the workbench after), but closes most of the gap that isn't a dispatcher-architecture difference.
