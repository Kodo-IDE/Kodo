// Licensed under GPL-v3.0
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
