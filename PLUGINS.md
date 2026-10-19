# Plugins

A plugin runs managed code inside Kodo. Use it for anything that is *not* language intelligence:
panels, commands, menus, previews, status indicators, integrations with external tools.

Shipped examples: **HelloWorld** and **TemplatePlugin** in
[Kodo-Extension-Template](https://github.com/Kodo-IDE/Kodo-Extension-Template).

> If you are adding syntax, completions, diagnostics, or navigation, you want a
> [language extension](LANGUAGE-EXTENSIONS.md), not a plugin. Keep parsing out of plugins.

---

## The contract

```csharp
public interface IKodoPlugin
{
    void OnLoad(MainWindow window, LoadedExtension extension);
    void OnUnload();
}
```

| Member | When it runs | Notes |
|---|---|---|
| `OnLoad` | Once per plugin class, after Kodo loads the assembly | Runs on the UI thread. `window` is the live `MainWindow`. |
| `OnUnload` | Before the plugin is removed, replaced, or reloaded | No arguments. Must be safe to call even if `OnLoad` failed partway. |

### Discovery requirements

Kodo finds your plugin by reflection. The class must be:

- not `abstract`
- implement `Kodo.IKodoPlugin`
- have a **public parameterless constructor**

The type itself does **not** need to be `public` — Kodo filters on `IsAbstract` and the interface
only, so an `internal` plugin class works fine. (The constructor does need to be public, because Kodo
instantiates via `Activator.CreateInstance`.)

Every matching class in the assembly is instantiated. One DLL may contain several plugins.

`OnUnload` receives no arguments because the plugin is expected to have kept hold of whatever it
created. Store your controls, handlers, timers, and subscriptions in fields.

---

## Setting up the project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>MyPlugin</AssemblyName>
    <RootNamespace>MyPlugin</RootNamespace>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!--
      Match Kodo's Avalonia version exactly (check Kodo/Source/Kodo.csproj), and use
      ExcludeAssets="runtime" so Avalonia is not copied into your output. Kodo already has
      Avalonia loaded, so a second copy inside the .kox is a version conflict.
    -->
    <PackageReference Include="Avalonia" Version="12.1.3" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Kodo">
      <HintPath>..\..\Kodo\Kodo\Source\bin\Debug\net10.0\Kodo.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

| Setting | Why it matters |
|---|---|
| `net10.0` | Must match Kodo exactly. A different TFM will not load. |
| `Private=false` | Stops a stale copy of `Kodo.dll` being copied into your output and shipped in the package. |
| `EnableDynamicLoading` | Correct SDK defaults for a library loaded at runtime. |
| `Avalonia` version | Must match the version in `Kodo/Source/Kodo.csproj`. Kodo upgrades this, and an older Avalonia can fail to load. |
| `ExcludeAssets="runtime"` | Keeps Avalonia out of your output. Without it, `dotnet build` copies ~4.5 MB of Avalonia DLLs into the folder you are about to zip, and Kodo ends up loading a second, conflicting copy. |

Without `ExcludeAssets="runtime"`, a `PackageReference` to Avalonia copies the whole Avalonia set
into your build output. The output looks correct, so the mistake is easy to miss, and the packaged
plugin then carries a duplicate UI framework. Verified: with it set, the output is just your
assembly plus its `.deps.json`.

Build Kodo first so the reference resolves:

```bash
dotnet build Kodo/Source/Kodo.csproj
```

Adjust `HintPath` to your checkout layout. The path is relative to your `.csproj`.

Ship any **dependencies** of your plugin alongside it. Kodo loads your main assembly and resolves
sibling assemblies from the plugin folder, so extra DLLs in the package are found automatically.

---

## A complete plugin

```csharp
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kodo;
using Kodo.Models;

namespace MyPlugin;

public sealed class HelloPlugin : IKodoPlugin
{
    private Button? _button;

    public void OnLoad(MainWindow window, LoadedExtension extension)
    {
        _button = new Button { Content = "Hello from MyPlugin" };
        _button.Click += OnClick;

        // Add a class alongside Content for styling hooks.
        _button.Classes.Add("activity");

        window.FindControl<Panel>("ActivityBarPanel")?.Children.Add(_button);
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        // Do work here. Keep it fast; this is the UI thread.
    }

    public void OnUnload()
    {
        if (_button is null) return;
        _button.Click -= OnClick;
        if (_button.Parent is Panel panel) panel.Children.Remove(_button);
        _button = null;
    }
}
```

### Manifest

```json
{
  "id": "hello-plugin",
  "version": "1.0.0",
  "name": "Hello Plugin",
  "type": "plugin",
  "author": "Your Name",
  "description": "Adds a small greeting command.",
  "plugin": "MyPlugin.dll"
}
```

`plugin` must be the **exact filename** of your assembly inside the package. If it is missing or
wrong, a `type: plugin` package loads inertly — no error, no code runs.

---

## How loading works

1. Kodo extracts every `*.dll` from the `.kox` into `%LocalAppData%\Kodo\PluginCache\`. On non-Windows it
   also extracts `*.so` and `*.dylib` and marks them executable, so a plugin can carry a native
   dependency.
2. It creates an `AssemblyLoadContext` with `isCollectible: true`.
3. The main assembly is loaded from a **shadow copy** — the bytes are read into memory and loaded
   from a stream, so the file on disk is never locked and can be replaced while Kodo runs.
4. Sibling assemblies are resolved from the plugin folder, also shadow-copied.
5. Every `IKodoPlugin` implementation in the assembly is constructed and `OnLoad` is called.
   Kodo filters on `IsAbstract` and the interface only — it does **not** require the type to be
   `public`, so an `internal` plugin class works. There is no "pick one entry point" mechanism, so
   every implementation in the assembly runs.
6. On unload, Kodo calls `OnUnload` on each instance and then unloads the context.

Consequences worth designing around:

- Because files are shadow-copied, you can rebuild and reinstall without fighting file locks.
  Because the context is collectible, a clean unload is possible — **if your `OnUnload` releases
  your references.** A plugin that roots itself through a static event or a running timer will not
  actually be collected.
- An unpacked plugin folder is used in place rather than extracted.

### Reloading: bump the version

Kodo decides whether to reload a plugin by comparing the **manifest `version`** of the currently
loaded plugin against the one on disk. If they match, the plugin is left alone.

So rebuilding your DLL is not enough. To see a change, bump `version` in `manifest.json`. Without
that, the new build sits on disk ignored and the old code keeps running, which reads exactly like
"my change had no effect".

A collectible context also means this does **not** need a restart, unlike a LangRules assembly.

### Logging: there is no public API

Plugins cannot write to Kodo's log. `KodoDiagnostics` declares `public` methods, but the class
itself is `internal`, so it is unreachable from a plugin assembly. What you have:

| Option | Reaches |
|---|---|
| `Console.WriteLine` | The terminal Kodo was launched from. What Kodo's own plugin loader uses. |
| `Debug.WriteLine` | An attached debugger only. |

Console output is invisible when Kodo is launched from Explorer, a file manager, or an IDE without a
terminal. Plan for a deployed plugin being close to undebuggable, and prefer surfacing problems in
your own UI over relying on diagnostics.

---

## Plugins that ship with a language

A language package can carry a plugin for host integration — extra buttons, panels, or commands —
without giving up the language engine. Name the assembly in `languagePlugin`:

```json
{
  "id": "mylang-kodo-extension",
  "type": "language",
  "extensions": [".myl"],
  "plugin": "LangRules.dll",
  "languagePlugin": "MyLangUi.dll"
}
```

`languagePlugin` uses the identical `IKodoPlugin` lifecycle. The distinction is organizational:
`plugin` is the assembly Kodo treats as the package's primary code, `languagePlugin` is an extra
plugin shipped alongside a language. See
[`plugin` vs `languagePlugin`](KODO-FILE-FORMAT.md#plugin-vs-languageplugin).

---

## Rules for plugin authors

**Do not block the UI thread.** `OnLoad` and every event handler you own run on it. Use
`async`/`await` for I/O and `Task.Run` for CPU work, and marshal back with
`Dispatcher.UIThread`. A plugin that blocks the thread freezes the whole editor.

**Always pair creation with teardown.** If `OnLoad` adds a control, subscribes a handler, or starts a
timer, `OnUnload` must undo all of it. Otherwise reloading the extension duplicates UI or leaks a
running background task.

**Be defensive at the boundaries.** Find controls with `FindControl<T>(...)` and handle `null` —
the UI may change, or the control may not exist in the page you expected. Never assume a control
named in this guide is present forever.

**Do not silently overwrite user data.** If your plugin writes files, write somewhere the user
chose, tell them what you are doing, and never clobber unrelated content.

**Validate external input.** File paths, command-line output, and network responses are all
untrusted.

**Do not put language intelligence in a plugin.** Parsing belongs in a language engine so it is
testable and does not fight the editor's own analysis.

---

## Packaging

```text
hello-plugin/
├── manifest.json
├── MyPlugin.dll
├── MyPlugin.deps.json      if produced
├── AnyDependency.dll       ship what you need
├── icon.png                optional
└── LICENSE.txt             recommended
```

```powershell
Compress-Archive -Path .\hello-plugin\* -DestinationPath .\hello-plugin-1.0.0.zip
Rename-Item .\hello-plugin-1.0.0.zip .\hello-plugin-1.0.0.kox
```

Never include `Kodo.dll` in the package. Kodo provides it, and shipping a copy causes assembly
conflicts.

To submit to the marketplace, add the `.kox` to `Extensions/` and an entry to
`Indexs/PluginsIndex.json` in [Kodo-Extensions](https://github.com/Kodo-IDE/Kodo-Extensions). See
[KODO-FILE-FORMAT.md#submitting-to-the-marketplace](KODO-FILE-FORMAT.md#submitting-to-the-marketplace).

---

## Testing

1. Build Kodo, then build your plugin in `Release`.
2. Confirm the output DLL name — the manifest must match it exactly.
3. Copy the folder (or `.kox`) into `%LocalAppData%\Kodo\Extensions\`.
4. Start Kodo and confirm the plugin loaded.
5. Exercise the feature.
6. **Reload the extension and confirm the UI did not duplicate.** This is the single most common
   bug, and it only shows up here.
7. Restart Kodo and confirm nothing is left running — no orphaned processes, no growing memory.

Turn on verbose logging in Kodo's settings if you need to see plugin load failures; load errors
are logged rather than shown as dialogs.

---

## Troubleshooting

| Symptom | Check |
|---|---|
| Plugin is listed but does nothing | `type` is `plugin`; `plugin` matches the DLL filename exactly; the assembly is at the package root |
| Nothing loads at all | the type is non-abstract, implements `IKodoPlugin`, and has a public parameterless constructor (`public` on the type is not required) |
| `TypeLoadException` or missing-type errors | `TargetFramework` is not `net10.0`; a dependency DLL is missing from the package |
| Reloading duplicates UI | `OnUnload` did not remove the controls or unsubscribe the handlers it added |
| Memory grows on reload | something is rooted — a static event subscription, a running timer, or an unobserved task |
| The editor freezes | blocking work on the UI thread in `OnLoad` or a handler |
| `FindControl` returns null | the control name changed, or the page is not currently realised |

---

## Related guides

- [EXTENSION-DEVELOPMENT.md](EXTENSION-DEVELOPMENT.md) — which kind of extension to build
- [KODO-FILE-FORMAT.md](KODO-FILE-FORMAT.md) — manifest fields, packaging, marketplace
- [LANGUAGE-EXTENSIONS.md](LANGUAGE-EXTENSIONS.md) — the `languagePlugin` field in context
