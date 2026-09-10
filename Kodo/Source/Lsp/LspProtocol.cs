// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Kodo;

/// <summary>Generic JSON-RPC + LSP Content-Length framing. No language-specific logic.</summary>
internal static class LspProtocol
{
    public const string JsonRpcVersion = "2.0";

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
        return int.TryParse(headerLine[(idx + 1)..].Trim(), out length);
    }

    public static JsonElement? ParseMessage(string json, out int? id, out string? method)
    {
        id = null;
        method = null;
        try
        {
            var doc = JsonDocument.Parse(json);
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
