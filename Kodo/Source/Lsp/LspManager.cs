// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

/// <summary>Manages <see cref="LspClient"/> lifetimes – one per (workspace, command).
/// Generic, no language-specific logic.</summary>
internal sealed class LspManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LspClient? TryGetClient(string workspaceRoot, LspConfiguration config)
    {
        var key = MakeKey(workspaceRoot, config);
        lock (_lock) return _clients.TryGetValue(key, out var c) ? c : null;
    }

    public async Task<LspClient> GetOrStartAsync(string workspaceRoot, LspConfiguration config, CancellationToken ct = default)
    {
        var key = MakeKey(workspaceRoot, config);
        LspClient? client;
        bool needsStart = false;

        lock (_lock)
        {
            if (_clients.TryGetValue(key, out client) && client.IsStarted)
                return client;

            // stale entry
            if (client is not null)
            {
                _clients.Remove(key);
                try { client.Dispose(); } catch { }
            }

            client = new LspClient(config, workspaceRoot);
            _clients[key] = client;
            needsStart = true;
        }

        if (needsStart)
        {
            client.OnExit += code =>
            {
                lock (_lock) _clients.Remove(key);
                KodoDiagnostics.LogDebug($"LSP manager removed exited client '{config.Command}' ws={workspaceRoot} code={code}");
            };

            try
            {
                await client.StartAsync(ct).ConfigureAwait(false);
                // initialize is caller's responsibility (Phase 4) – but we can lazy-initialize here if needed
            }
            catch (Exception ex)
            {
                lock (_lock) _clients.Remove(key);
                try { client.Dispose(); } catch { }
                // Surface useful message without crashing – caller shows dialog
                KodoDiagnostics.LogDebug($"LSP manager failed to start '{config.Command}'", ex);
                throw;
            }
        }

        return client;
    }

    public async Task ShutdownAsync(string workspaceRoot, LspConfiguration config, string reason = "manager shutdown")
    {
        var key = MakeKey(workspaceRoot, config);
        LspClient? client;
        lock (_lock) _clients.TryGetValue(key, out client);
        if (client is null) return;
        await client.ShutdownAsync(reason).ConfigureAwait(false);
        lock (_lock) _clients.Remove(key);
        client.Dispose();
    }

    public async Task ShutdownAllAsync(string reason = "manager shutdown all")
    {
        List<LspClient> snapshot;
        lock (_lock)
        {
            snapshot = new List<LspClient>(_clients.Values);
            _clients.Clear();
        }
        foreach (var c in snapshot)
        {
            try { await c.ShutdownAsync(reason).ConfigureAwait(false); } catch { }
            try { c.Dispose(); } catch { }
        }
    }

    public IReadOnlyCollection<LspClient> AllClients
    {
        get { lock (_lock) return new List<LspClient>(_clients.Values).AsReadOnly(); }
    }

    private static string MakeKey(string workspaceRoot, LspConfiguration config)
    {
        var root = Path.GetFullPath(workspaceRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"{root}|{config.Command}|{string.Join(" ", config.Arguments)}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ShutdownAllAsync("manager dispose").GetAwaiter().GetResult(); } catch { }
    }
}
