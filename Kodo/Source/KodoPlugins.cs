// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

public interface IKodoPlugin
{
    void OnLoad(MainWindow window, LoadedExtension extension);
    void OnUnload();
}

public sealed class KodoPluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginFolder;

    public KodoPluginLoadContext(string id, string pluginFolder) : base(name: id, isCollectible: true)
    {
        _pluginFolder = pluginFolder;
    }

    public Assembly LoadMainAssembly(string assemblyPath) => LoadShadowCopy(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var candidatePath = Path.Combine(_pluginFolder, assemblyName.Name + ".dll");
        return File.Exists(candidatePath) ? LoadShadowCopy(candidatePath) : null;
    }

    private Assembly LoadShadowCopy(string assemblyPath)
    {
        var bytes = File.ReadAllBytes(assemblyPath);
        using var stream = new MemoryStream(bytes, writable: false);
        return LoadFromStream(stream);
    }
}

public sealed class LoadedKodoPlugin
{
    public required string Version { get; init; }
    public required KodoPluginLoadContext LoadContext { get; init; }
    public required List<IKodoPlugin> Instances { get; init; }
}

public partial class MainWindow
{
    private readonly Dictionary<string, LoadedKodoPlugin> _activePlugins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedKodoPlugin> _activeLanguagePlugins =
        new(StringComparer.OrdinalIgnoreCase);

    private string PluginCacheFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kodo", "PluginCache");

    private string ExtractKoxPluginFiles(ZipArchive archive, string id, string version)
    {
        var safeId = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
        var prefix = $"{safeId}_{version}_";
        CleanupStalePluginCacheFolders(prefix);

        var pluginFolder = Path.Combine(PluginCacheFolderPath, $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginFolder);

        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            var destPath = Path.Combine(pluginFolder, entry.Name);
            using var entryStream = entry.Open();
            using var destStream = File.Create(destPath);
            entryStream.CopyTo(destStream);
        }

        return pluginFolder;
    }

    private void CleanupStalePluginCacheFolders(string prefix)
    {
        if (!Directory.Exists(PluginCacheFolderPath)) return;

        foreach (var dir in Directory.EnumerateDirectories(PluginCacheFolderPath, $"{prefix}*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    private void SyncActivePlugins()
    {
        var currentIds = LoadedExtensions.Where(e => e.HasPlugin).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _activePlugins.Keys.Where(id => !currentIds.Contains(id)).ToList())
            UnloadPlugin(staleId);

        foreach (var ext in LoadedExtensions.Where(e => e.HasPlugin))
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
        var assemblyPath = Path.Combine(ext.PluginFolderPath!, ext.PluginAssemblyFileName!);
        if (!File.Exists(assemblyPath)) return;

        var loadContext = new KodoPluginLoadContext(ext.Id, ext.PluginFolderPath!);
        var instances = new List<IKodoPlugin>();

        try
        {
            var assembly = loadContext.LoadMainAssembly(assemblyPath);
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IKodoPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IKodoPlugin instance) continue;

                instance.OnLoad(this, ext);
                instances.Add(instance);
            }

            _activePlugins[ext.Id] = new LoadedKodoPlugin
            {
                Version = ext.Version,
                LoadContext = loadContext,
                Instances = instances
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Plugins] Failed to load '{ext.Id}': {ex.Message}");
            ExtensionLoadErrors.Add($"Plugin '{ext.Name}' failed to load: {ex.Message}");
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

        foreach (var instance in plugin.Instances)
        {
            try { instance.OnUnload(); }
            catch (Exception ex) { Console.WriteLine($"[Plugins] '{extensionId}' threw during unload: {ex.Message}"); }
        }

        plugin.LoadContext.Unload();
        _ = Task.Run(() =>
        {
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
        });
    }

    private void LoadLanguagePlugin(LoadedExtension ext)
    {
        var assemblyPath = Path.Combine(ext.LanguagePluginFolderPath!, ext.LanguagePluginAssemblyFileName!);
        if (!File.Exists(assemblyPath)) return;

        var loadContext = new KodoPluginLoadContext(ext.Id + "_lp", ext.LanguagePluginFolderPath!);
        var instances = new List<IKodoPlugin>();

        try
        {
            var assembly = loadContext.LoadMainAssembly(assemblyPath);
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IKodoPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IKodoPlugin instance) continue;

                instance.OnLoad(this, ext);
                instances.Add(instance);
            }

            _activeLanguagePlugins[ext.Id] = new LoadedKodoPlugin
            {
                Version = ext.Version,
                LoadContext = loadContext,
                Instances = instances
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LanguagePlugins] Failed to load '{ext.Id}': {ex.Message}");
            ExtensionLoadErrors.Add($"Language plugin '{ext.Name}' failed to load: {ex.Message}");
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

        foreach (var instance in plugin.Instances)
        {
            try { instance.OnUnload(); }
            catch (Exception ex) { Console.WriteLine($"[LanguagePlugins] '{extensionId}' threw during unload: {ex.Message}"); }
        }

        plugin.LoadContext.Unload();
        _ = Task.Run(() =>
        {
            GC.Collect(0, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
        });
    }
}