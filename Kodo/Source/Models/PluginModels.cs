// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

public interface IKodoPlugin
{
    void OnLoad(MainWindow window, LoadedExtension extension);
    void OnUnload();

    void RegisterCommands(KodoPluginCommandRegistry commands) { }
}

public sealed record KodoPluginCommand(string Id, string Title, Func<Task> Execute)
{
    public string Category { get; init; } = "Plugin";
    public string Keywords { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Shortcut { get; init; } = string.Empty;
    public Func<bool>? CanExecute { get; init; }
    public bool KeepPaletteOpen { get; init; }
}

public sealed class KodoPluginCommandRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly string _ownerName;
    private readonly Action<Guid, string, KodoPluginCommand> _register;
    private readonly Action<Guid> _unregister;
    private readonly Dictionary<Guid, string> _registrations = new();
    private readonly HashSet<string> _commandIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal KodoPluginCommandRegistry(
        string ownerName,
        Action<Guid, string, KodoPluginCommand> register,
        Action<Guid> unregister)
    {
        _ownerName = ownerName;
        _register = register;
        _unregister = unregister;
    }

    public IDisposable Register(KodoPluginCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Id)) throw new ArgumentException("A command ID is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Title)) throw new ArgumentException("A command title is required.", nameof(command));
        ArgumentNullException.ThrowIfNull(command.Execute);

        var token = Guid.NewGuid();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_commandIds.Add(command.Id))
                throw new InvalidOperationException($"This plugin already registered a command with ID '{command.Id}'.");
            _registrations.Add(token, command.Id);
        }

        try
        {
            _register(token, _ownerName, command);
        }
        catch
        {
            Release(token);
            throw;
        }
        return new Registration(this, token);
    }

    public IDisposable Register(
        string id,
        string title,
        Func<Task> execute,
        string category = "Plugin",
        string keywords = "",
        string detail = "",
        Func<bool>? canExecute = null,
        string shortcut = "",
        bool keepPaletteOpen = false) =>
        Register(new KodoPluginCommand(id, title, execute)
        {
            Category = category,
            Keywords = keywords,
            Detail = detail,
            CanExecute = canExecute,
            Shortcut = shortcut,
            KeepPaletteOpen = keepPaletteOpen,
        });

    private void Release(Guid token)
    {
        lock (_gate)
        {
            if (!_registrations.Remove(token, out var id)) return;
            _commandIds.Remove(id);
        }
        _unregister(token);
    }

    public void Dispose()
    {
        Guid[] tokens;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            tokens = [.. _registrations.Keys];
            _registrations.Clear();
            _commandIds.Clear();
        }
        foreach (var token in tokens)
            _unregister(token);
    }

    private sealed class Registration(KodoPluginCommandRegistry owner, Guid token) : IDisposable
    {
        private KodoPluginCommandRegistry? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(token);
    }
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
    public List<KodoPluginCommandRegistry> CommandRegistries { get; init; } = [];
}
