// Licensed under the GNU GPL-v3.0
using Avalonia.Threading;
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Kodo;

public partial class MainWindow
{
    private readonly Dictionary<string, LoadedKodoPlugin> _activePlugins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedKodoPlugin> _activeLanguagePlugins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (string OwnerName, KodoPluginCommand Command)> _pluginPaletteCommands = new();

    private KodoPluginCommandRegistry CreatePluginCommandRegistry(string ownerName) =>
        new(ownerName, AddPluginPaletteCommand, RemovePluginPaletteCommand);

    private void AddPluginPaletteCommand(Guid token, string ownerName, KodoPluginCommand command)
    {
        void Add()
        {
            _pluginPaletteCommands[token] = (ownerName, command);
            RefreshOpenCommandPaletteForPluginCommands();
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    private void RemovePluginPaletteCommand(Guid token)
    {
        void Remove()
        {
            if (_pluginPaletteCommands.Remove(token))
                RefreshOpenCommandPaletteForPluginCommands();
        }

        if (Dispatcher.UIThread.CheckAccess()) Remove();
        else Dispatcher.UIThread.Post(Remove);
    }

    private void RefreshOpenCommandPaletteForPluginCommands()
    {
        if (_commandPaletteOverlay is not { IsVisible: true }) return;
        _commandPaletteCommands = BuildCommandPaletteCommands();
        RefreshCommandPaletteResults();
    }

    private string PluginCacheFolderPath => KodoPaths.PluginCacheDir;

    private string ExtractKoxPluginFiles(ZipArchive archive, string id, string version)
    {
        var prefix = $"{SanitizePluginCacheSegment(id)}_{SanitizePluginCacheSegment(version)}_";
        CleanupStalePluginCacheFolders(prefix);

        var pluginFolder = Path.Combine(PluginCacheFolderPath, $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginFolder);
        var fullPluginFolder = Path.GetFullPath(pluginFolder);

        foreach (var entry in archive.Entries)
        {
            var isManaged = entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var isUnixNative = !OperatingSystem.IsWindows() &&
                (entry.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                 entry.Name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase));
            if (!isManaged && !isUnixNative) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destPath = Path.GetFullPath(Path.Combine(pluginFolder, entry.FullName));
            if (!FileSystemPaths.IsPrefixOf(destPath, fullPluginFolder))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var entryStream = entry.Open();
            using var destStream = File.Create(destPath);
            entryStream.CopyTo(destStream);
            if (isUnixNative)
                LspInstallationManager.MakeExecutable(destPath);
        }

        return pluginFolder;
    }

    private static string SanitizePluginCacheSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['*', '?']).ToArray();
        return string.Join("_", value.Split(invalid));
    }

    private void CleanupStalePluginCacheFolders(string prefix)
    {
        if (!Directory.Exists(PluginCacheFolderPath)) return;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var dir in Directory.EnumerateDirectories(PluginCacheFolderPath))
        {
            if (!Path.GetFileName(dir).StartsWith(prefix, comparison))
                continue;
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    private void SyncActivePlugins()
    {
        const string nativeGitIntegrationId = "git-source-control";
        var currentIds = LoadedExtensions
            .Where(e => e.HasPlugin && !string.Equals(e.Id, nativeGitIntegrationId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _activePlugins.Keys.Where(id => !currentIds.Contains(id)).ToList())
            UnloadPlugin(staleId);

        foreach (var ext in LoadedExtensions.Where(e => e.HasPlugin && !string.Equals(e.Id, nativeGitIntegrationId, StringComparison.OrdinalIgnoreCase)))
        {
            if (_activePlugins.TryGetValue(ext.Id, out var existing) && existing.Version == ext.Version)
                continue;

            if (_activePlugins.ContainsKey(ext.Id))
                UnloadPlugin(ext.Id);

            LoadPlugin(ext);
        }
        SyncActiveLanguagePlugins();
    }

    private void SyncActiveLanguagePlugins()
    {
        var currentIds = LoadedExtensions.Where(e => e.HasLanguagePlugin).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _activeLanguagePlugins.Keys.Where(id => !currentIds.Contains(id)).ToList())
            UnloadLanguagePlugin(staleId);

        foreach (var ext in LoadedExtensions.Where(e => e.HasLanguagePlugin))
        {
            if (_activeLanguagePlugins.TryGetValue(ext.Id, out var existing) && existing.Version == ext.Version)
                continue;

            if (_activeLanguagePlugins.ContainsKey(ext.Id))
                UnloadLanguagePlugin(ext.Id);

            LoadLanguagePlugin(ext);
        }
    }

    private void LoadPlugin(LoadedExtension ext)
    {
        if (string.IsNullOrWhiteSpace(ext.PluginFolderPath) || string.IsNullOrWhiteSpace(ext.PluginAssemblyFileName))
            return;
        var assemblyPath = Path.GetFullPath(Path.Combine(ext.PluginFolderPath!, ext.PluginAssemblyFileName!));
        if (!FileSystemPaths.IsPrefixOf(assemblyPath, Path.GetFullPath(ext.PluginFolderPath!)))
            return;
        if (!File.Exists(assemblyPath))
        {
            var fallback = Directory.EnumerateFiles(ext.PluginFolderPath!, Path.GetFileName(ext.PluginAssemblyFileName!), SearchOption.AllDirectories).FirstOrDefault();
            if (fallback is null) return;
            assemblyPath = fallback;
        }
        if (!File.Exists(assemblyPath)) return;

        var loadContext = new KodoPluginLoadContext(ext.Id, ext.PluginFolderPath!);
        var instances = new List<IKodoPlugin>();
        var commandRegistries = new List<KodoPluginCommandRegistry>();

        try
        {
            var assembly = loadContext.LoadMainAssembly(assemblyPath);
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IKodoPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IKodoPlugin instance) continue;

                instances.Add(instance);
                instance.OnLoad(this, ext);
                var commandRegistry = CreatePluginCommandRegistry(ext.Name);
                commandRegistries.Add(commandRegistry);
                instance.RegisterCommands(commandRegistry);
            }

            _activePlugins[ext.Id] = new LoadedKodoPlugin
            {
                Version = ext.Version,
                LoadContext = loadContext,
                Instances = instances,
                CommandRegistries = commandRegistries
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Plugins] Failed to load '{ext.Id}': {ex.Message}");
            ExtensionLoadErrors.Add($"Plugin '{ext.Name}' failed to load: {ex.Message}");
            foreach (var registry in commandRegistries)
            {
                try { registry.Dispose(); } catch { }
            }
            foreach (var instance in instances)
            {
                try { instance.OnUnload(); } catch { }
            }
            loadContext.Unload();
        }
    }

    private void UnloadPlugin(string extensionId)
    {
        if (!_activePlugins.Remove(extensionId, out var plugin)) return;

        foreach (var registry in plugin.CommandRegistries)
        {
            try { registry.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[Plugins] '{extensionId}' command cleanup failed: {ex.Message}"); }
        }
        foreach (var instance in plugin.Instances)
        {
            try { instance.OnUnload(); }
            catch (Exception ex) { Console.WriteLine($"[Plugins] '{extensionId}' threw during unload: {ex.Message}"); }
        }

        plugin.LoadContext?.Unload();
        _ = Task.Run(() =>
        {
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
        });
    }

    private void LoadLanguagePlugin(LoadedExtension ext)
    {
        if (string.IsNullOrWhiteSpace(ext.LanguagePluginFolderPath) || string.IsNullOrWhiteSpace(ext.LanguagePluginAssemblyFileName))
            return;

        // TagAutoClose.dll ships compiled-only inside the HTML/XML/XAML/CSS
        // language .kox packages. Its OnLoad self-posts a full visual-tree
        // walk at Background priority until it finds a TextEditor named
        // "EditorTextBox" — a name that no longer exists in MainWindow.axaml
        // (the editors are "PrimaryEditorTextBox"/"SecondaryEditorTextBox"
        // since the split-editor work). The match never succeeds, so it
        // re-posts forever and burns ~85% of one core at idle with
        // extensions installed (benchmarks/README.md, finding #6).
        // Substitute the built-in, fixed implementation below instead of
        // loading the external DLL, so extension reinstalls or updates
        // cannot reintroduce the spin.
        if (string.Equals(ext.LanguagePluginAssemblyFileName, InternalTagAutoClosePlugin.ExternalFileName, StringComparison.OrdinalIgnoreCase))
        {
            LoadInternalTagAutoClose(ext);
            return;
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(ext.LanguagePluginFolderPath!, ext.LanguagePluginAssemblyFileName!));
        if (!FileSystemPaths.IsPrefixOf(assemblyPath, Path.GetFullPath(ext.LanguagePluginFolderPath!)))
            return;
        if (!File.Exists(assemblyPath))
        {
            var fallback = Directory.EnumerateFiles(ext.LanguagePluginFolderPath!, Path.GetFileName(ext.LanguagePluginAssemblyFileName!), SearchOption.AllDirectories).FirstOrDefault();
            if (fallback is null) return;
            assemblyPath = fallback;
        }
        if (!File.Exists(assemblyPath)) return;

        var loadContext = new KodoPluginLoadContext(ext.Id + "_lp", ext.LanguagePluginFolderPath!);
        var instances = new List<IKodoPlugin>();
        var commandRegistries = new List<KodoPluginCommandRegistry>();

        try
        {
            var assembly = loadContext.LoadMainAssembly(assemblyPath);
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IKodoPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IKodoPlugin instance) continue;

                instances.Add(instance);
                instance.OnLoad(this, ext);
                var commandRegistry = CreatePluginCommandRegistry(ext.Name);
                commandRegistries.Add(commandRegistry);
                instance.RegisterCommands(commandRegistry);
            }

            _activeLanguagePlugins[ext.Id] = new LoadedKodoPlugin
            {
                Version = ext.Version,
                LoadContext = loadContext,
                Instances = instances,
                CommandRegistries = commandRegistries
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LanguagePlugins] Failed to load '{ext.Id}': {ex.Message}");
            ExtensionLoadErrors.Add($"Language plugin '{ext.Name}' failed to load: {ex.Message}");
            foreach (var registry in commandRegistries)
            {
                try { registry.Dispose(); } catch { }
            }
            foreach (var instance in instances)
            {
                try { instance.OnUnload(); } catch { }
            }
            loadContext.Unload();
        }
    }

    private void UnloadLanguagePlugin(string extensionId)
    {
        if (!_activeLanguagePlugins.Remove(extensionId, out var plugin)) return;

        foreach (var registry in plugin.CommandRegistries)
        {
            try { registry.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[LanguagePlugins] '{extensionId}' command cleanup failed: {ex.Message}"); }
        }
        foreach (var instance in plugin.Instances)
        {
            try { instance.OnUnload(); }
            catch (Exception ex) { Console.WriteLine($"[LanguagePlugins] '{extensionId}' threw during unload: {ex.Message}"); }
        }

        plugin.LoadContext?.Unload();
        _ = Task.Run(() =>
        {
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
        });
    }

    private void LoadInternalTagAutoClose(LoadedExtension ext)
    {
        var instances = new List<IKodoPlugin>();
        var commandRegistries = new List<KodoPluginCommandRegistry>();

        try
        {
            IKodoPlugin instance = new InternalTagAutoClosePlugin();
            instances.Add(instance);
            instance.OnLoad(this, ext);
            var commandRegistry = CreatePluginCommandRegistry(ext.Name);
            commandRegistries.Add(commandRegistry);
            instance.RegisterCommands(commandRegistry);

            _activeLanguagePlugins[ext.Id] = new LoadedKodoPlugin
            {
                Version = ext.Version,
                LoadContext = null,
                Instances = instances,
                CommandRegistries = commandRegistries,
            };
            KodoDiagnostics.LogDebug($"Loaded built-in TagAutoClose replacement for '{ext.Id}' (external DLL skipped).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LanguagePlugins] Failed to load built-in TagAutoClose for '{ext.Id}': {ex.Message}");
            ExtensionLoadErrors.Add($"Language plugin '{ext.Name}' (built-in TagAutoClose) failed to load: {ex.Message}");
            foreach (var registry in commandRegistries)
            {
                try { registry.Dispose(); } catch { }
            }
            foreach (var instance in instances)
            {
                try { instance.OnUnload(); } catch { }
            }
        }
    }
}
