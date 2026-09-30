// Licensed under the GNU GPL-v3.0
#pragma warning disable CA1416
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Kodo.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Kodo;

internal sealed class LspManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<LspClient>> _starting = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LspClient? TryGetClient(string workspaceRoot, LspConfiguration config)
    {
        var key = MakeKey(workspaceRoot, config);
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var c)) return c;
            var root = NormalizeRoot(workspaceRoot);
            foreach (var candidate in _clients.Values)
            {
                if (!candidate.IsInitialized) continue;
                if (!FileSystemPaths.Equals(NormalizeRoot(candidate.ClientWorkspaceRoot), root)) continue;
                try
                {
                    if (string.Equals(candidate.Configuration.EffectiveProviderId, config.EffectiveProviderId, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch { }
            }
            return null;
        }
    }

    private static string NormalizeRoot(string workspaceRoot)
    {
        try { return Path.GetFullPath(workspaceRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return workspaceRoot ?? string.Empty; }
    }

    public Task<LspClient> GetOrStartAsync(string workspaceRoot, LspConfiguration config, CancellationToken ct = default)
    {
        var key = MakeKey(workspaceRoot, config);
        Task<LspClient> taskToAwait;
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LspManager));
            if (_clients.TryGetValue(key, out var existing) && existing.IsStarted)
                return Task.FromResult(existing);
            if (_starting.TryGetValue(key, out var inProgress))
                return inProgress;

            if (existing is not null)
            {
                _clients.Remove(key);
                try { existing.Dispose(); } catch { }
            }

            var newClient = new LspClient(config, workspaceRoot);
            var startTask = StartAndRegisterAsync(newClient, key, config, workspaceRoot, ct);
            _starting[key] = startTask;
            taskToAwait = startTask;
        }
        return taskToAwait;
    }

    private async Task<LspClient> StartAndRegisterAsync(LspClient client, string key, LspConfiguration config, string workspaceRoot, CancellationToken ct)
    {
        client.OnExit += code =>
        {
            lock (_lock) _clients.Remove(key);
            KodoDiagnostics.LogDebug($"LSP manager removed exited client '{config.Command}' ws={workspaceRoot} code={code}");
        };

        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_disposed)
                    _clients[key] = client;
                else
                {
                    try { client.Dispose(); } catch { }
                    _starting.Remove(key);
                    throw new ObjectDisposedException(nameof(LspManager));
                }
                _starting.Remove(key);
            }
            return client;
        }
        catch (Exception ex)
        {
            lock (_lock) _starting.Remove(key);
            try { client.Dispose(); } catch { }
            KodoDiagnostics.LogDebug($"LSP manager failed to start '{config.Command}'", ex);
            throw;
        }
    }

    public async Task ShutdownAsync(string workspaceRoot, LspConfiguration config, string reason = "manager shutdown")
    {
        var key = MakeKey(workspaceRoot, config);
        LspClient? client = null;
        Task<LspClient>? startingTask = null;
        lock (_lock)
        {
            _clients.TryGetValue(key, out client);
            if (client is null) _starting.TryGetValue(key, out startingTask);
        }
        if (startingTask != null)
        {
            try { client = await startingTask.ConfigureAwait(false); } catch { return; }
        }
        if (client is null) return;
        await client.ShutdownAsync(reason).ConfigureAwait(false);
        lock (_lock) _clients.Remove(key);
        client.Dispose();
    }

    public async Task ShutdownAllAsync(string reason = "manager shutdown all")
    {
        List<LspClient> snapshot;
        List<Task<LspClient>> startingSnapshot;
        lock (_lock)
        {
            snapshot = new List<LspClient>(_clients.Values);
            startingSnapshot = new List<Task<LspClient>>(_starting.Values);
            _clients.Clear();
            _starting.Clear();
        }
        foreach (var t in startingSnapshot)
        {
            try
            {
                var c = await t.ConfigureAwait(false);
                if (!snapshot.Contains(c)) snapshot.Add(c);
            }
            catch { }
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
        var expandedArgs = string.Join(" ", config.Arguments.Select(a => a.Replace("{workspace}", root, StringComparison.OrdinalIgnoreCase).Replace("{workspaceFolder}", root, StringComparison.OrdinalIgnoreCase).Replace("{root}", root, StringComparison.OrdinalIgnoreCase)));
        return $"{root}|{config.Command}|{expandedArgs}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                var task = Task.Run(async () => await ShutdownAllAsync("manager dispose").ConfigureAwait(false));
                if (!task.Wait(TimeSpan.FromSeconds(5)))
                    KodoDiagnostics.LogDebug("LSP manager dispose timed out waiting for shutdown");
            }
            else
            {
                ShutdownAllAsync("manager dispose").GetAwaiter().GetResult();
            }
        }
        catch { }
        lock (_lock)
        {
            foreach (var c in _clients.Values) try { c.Dispose(); } catch { }
            _clients.Clear();
            _starting.Clear();
        }
    }
}

internal static class LspProtocol
{
    public const string JsonRpcVersion = "2.0";

    public static string Preview(string? text, int maxLength = 800)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (maxLength <= 0) return "...";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public static string CreateRequest(int id, string method, object? @params)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = id,
            ["method"] = method
        };
        if (@params is not null)
            payload["params"] = @params;
        return JsonSerializer.Serialize(payload);
    }

    public static string CreateNotification(string method, object? @params)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["method"] = method
        };
        if (@params is not null)
            payload["params"] = @params;
        return JsonSerializer.Serialize(payload);
    }

    public static string CreateResponse(int id, object? result)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = id,
            ["result"] = result
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string Frame(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        return $"Content-Length: {bytes}\r\n\r\n{json}";
    }

    public static bool TryParseContentLength(string headerLine, out int length)
    {
        length = 0;
        var idx = headerLine.IndexOf(':', StringComparison.Ordinal);
        if (idx < 0) return false;
        var name = headerLine[..idx].Trim();
        if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) return false;
        var value = headerLine[(idx + 1)..].Trim();
        if (value.Length == 0) return false;
        var semi = value.IndexOf(';');
        if (semi >= 0) value = value[..semi].Trim();
        if (value.Length == 0) return false;
        foreach (var c in value)
            if (!char.IsDigit(c)) return false;
        return int.TryParse(value, out length) && length >= 0;
    }

    public static JsonElement? ParseMessage(string json, out int? id, out string? method)
    {
        id = null;
        method = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
            {
                if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var i)) id = i;
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var si)) id = si;
            }
            if (root.TryGetProperty("method", out var mEl)) method = mEl.GetString();
            return root.Clone();
        }
        catch { return null; }
    }
}

internal static class LspProviderRegistry
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, HashSet<string>> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LspDependencyStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _statusMessage = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _installedVersion = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _executablePath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> _isManaged = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterConsumer(string providerId, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(extensionId)) return;
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_consumers.TryGetValue(pid, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _consumers[pid] = set;
            }
            set.Add(extensionId);
        }
    }

    public static void UnregisterConsumer(string providerId, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (_consumers.TryGetValue(pid, out var set))
            {
                set.Remove(extensionId);
                if (set.Count == 0) _consumers.Remove(pid);
            }
        }
    }

    public static IReadOnlyCollection<string> GetConsumers(string providerId)
    {
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock) return _consumers.TryGetValue(pid, out var set) ? set.ToList().AsReadOnly() : Array.Empty<string>();
    }

    public static bool HasOtherConsumers(string providerId, string excludingExtensionId)
    {
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_consumers.TryGetValue(pid, out var set)) return false;
            return set.Any(id => !id.Equals(excludingExtensionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static void SetStatus(string providerId, LspDependencyStatus status, string? message = null, string? version = null, string? exePath = null, bool isManaged = false)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _status[pid] = status;
            _statusMessage[pid] = message;
            if (version != null) _installedVersion[pid] = version;
            if (exePath != null) _executablePath[pid] = exePath;
            _isManaged[pid] = isManaged;
        }
    }

    public static (LspDependencyStatus status, string? message, string? version, string? exePath, bool isManaged) GetProviderInfo(string providerId)
    {
        var pid = providerId.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _status.TryGetValue(pid, out var st);
            _statusMessage.TryGetValue(pid, out var msg);
            _installedVersion.TryGetValue(pid, out var ver);
            _executablePath.TryGetValue(pid, out var exe);
            _isManaged.TryGetValue(pid, out var managed);
            return (st, msg, ver, exe, managed);
        }
    }

    public static void RefreshFromLoadedExtensions(IEnumerable<LoadedExtension> extensions)
    {
        lock (_lock) _consumers.Clear();
        foreach (var ext in extensions)
        {
            foreach (var cfg in ext.AllLspConfigurations)
                RegisterConsumer(cfg.EffectiveProviderId, ext.Id);
        }
    }
}
