// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

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

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var candidatePath = Path.Combine(_pluginFolder, assemblyName.Name + ".dll");
        return File.Exists(candidatePath) ? LoadFromAssemblyPath(candidatePath) : null;
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

    private string PluginCacheFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kodo", "PluginCache");

    private string ExtractKoxPluginFiles(ZipArchive archive, string id, string version)
    {
        var safeId = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
        var pluginFolder = Path.Combine(PluginCacheFolderPath, $"{safeId}_{version}");
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
    }

    private void LoadPlugin(LoadedExtension ext)
    {
        var assemblyPath = Path.Combine(ext.PluginFolderPath!, ext.PluginAssemblyFileName!);
        if (!File.Exists(assemblyPath)) return;

        var loadContext = new KodoPluginLoadContext(ext.Id, ext.PluginFolderPath!);
        var instances = new List<IKodoPlugin>();

        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
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
                try { instance.OnUnload(); } catch { /* best effort */ }
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
    }
}