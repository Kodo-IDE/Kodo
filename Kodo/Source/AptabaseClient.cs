// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Kodo;

internal sealed record AptabaseSystemProps(
    [property: JsonPropertyName("isDebug")]    bool   IsDebug,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("sdkVersion")] string SdkVersion,
    [property: JsonPropertyName("osName")]     string OsName);

internal sealed record AptabaseEvent(
    [property: JsonPropertyName("timestamp")]   string               Timestamp,
    [property: JsonPropertyName("sessionId")]   string               SessionId,
    [property: JsonPropertyName("eventName")]   string               EventName,
    [property: JsonPropertyName("systemProps")] AptabaseSystemProps  SystemProps,
    [property: JsonPropertyName("props")] Dictionary<string, string>? Props);

internal static class AptabaseClient
{
    private static readonly HttpClient _client = new();
    private static string? _appKey;
    private static string? _sessionId;
    private static readonly Queue<(string eventName, string? message)> _eventQueue = new();

    private static AptabaseSystemProps? _systemProps;

    private static bool _isEnabled;

    private static bool _isDevBuild;

    public static bool IsEnabled => _isEnabled;
    public static bool IsDevBuild => _isDevBuild;

    public static void SetEnabled(bool enabled)
    {
        if (enabled && (_isDevBuild || string.IsNullOrWhiteSpace(_appKey)))
        {
            _isEnabled = false;
            return;
        }

        if (_isEnabled == enabled) return;

        _isEnabled = enabled;

        Console.WriteLine(
            $"[Aptabase] Data tracking {(enabled ? "enabled" : "disabled")}");

        if (!enabled)
        {
            _eventQueue.Clear();
            return;
        }

        // CERTAINLY deferrable: connectivity probe not needed before first paint
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await TestConnectivityAsync();
            }
            catch { }
        });
    }

    // Strips user-identifying substrings from exception messages
    private static string? SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var sanitized = message;

        sanitized = Regex.Replace(
            sanitized,
            @"([A-Za-z]:\\Users\\)[^\\]+",
            "$1<redacted>",
            RegexOptions.IgnoreCase);

        sanitized = Regex.Replace(
            sanitized,
            @"(\\\\[^\\]+\\Users\\)[^\\]+",
            "$1<redacted>",
            RegexOptions.IgnoreCase);

        sanitized = Regex.Replace(
            sanitized,
            @"(/(?:home|Users)/)[^/\s]+",
            "$1<redacted>",
            RegexOptions.IgnoreCase);

        sanitized = Regex.Replace(
            sanitized,
            @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
            "<redacted-email>");

        return sanitized;
    }

    private static string GetWindowsVersion()
    {
        var ver = Environment.OSVersion.Version;
        if (OperatingSystem.IsWindows())
        {
            if (ver.Build >= 22000) return $"Windows 11 (Build {ver.Build})";
            if (ver.Build >= 10240) return $"Windows 10 (Build {ver.Build})";
        }
        return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    }

    public static void Initialize()
    {
        var informationalVersion = System.Reflection.Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var appVersion = !string.IsNullOrEmpty(informationalVersion)
            ? informationalVersion
            : System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        var readableVersion = appVersion.Split('+', 2)[0]; //Exclude metadatas

        _isDevBuild = readableVersion.EndsWith("-DEV", StringComparison.OrdinalIgnoreCase);

        _sessionId   = Guid.NewGuid().ToString();
        _systemProps = new AptabaseSystemProps(
            IsDebug:    System.Diagnostics.Debugger.IsAttached,
            AppVersion: readableVersion,
            SdkVersion: "kodo-aptabase@1.0.0",
            OsName:     GetWindowsVersion());

        if (_isDevBuild)
        {
            Console.WriteLine($"[Aptabase] Dev build detected ({appVersion})");
            return;
        }

        _appKey = GetAptabaseKey();

        Console.WriteLine($"[Aptabase] Initialized with session: {_sessionId}");
        Console.WriteLine($"[Aptabase] App Key: {_appKey}");

    }

    private static string? GetAptabaseKey()
    {
        var key = KEYS.AptabaseKey;
        
        // Check and warn if the KEYS.cs file is missing or if the key is
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("[Aptabase] WARNING: No key found, check if the file is there or if the key is set correctly.");
            KodoDiagnostics.LogWarning(
                "AptabaseClient.GetAptabaseKey",
                new InvalidOperationException("KEYS.AptabaseKey was null/empty at startup"),
                "resolving app key");
            return null;
        }

        // Check if placeholder key is there and print if it is.
        if (key == "PLACEHOLDER")
        {
            Console.WriteLine("[Aptabase] Placeholder Key found.");
            return null;
        }

        return key;
    }

    private static async Task TestConnectivityAsync()
    {
        try
        {
            Console.WriteLine("[Aptabase] Testing connectivity to us.aptabase.com...");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _client.GetAsync("https://us.aptabase.com", cts.Token);
            Console.WriteLine($"[Aptabase] ✓ Connectivity test response: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Aptabase] CONNECTIVITY TEST FAILED: {ex.GetType().Name}: {ex.Message}");
            KodoDiagnostics.LogWarning("AptabaseClient.TestConnectivityAsync", ex, "Aptabase connectivity check");
        }
    }

    public static void TrackEvent(string eventName, string? message = null)
    {
        try
        {
            if (!_isEnabled)
            {
                Console.WriteLine($"[Aptabase] Data tracking disabled, skipping event: {eventName}");
                return;
            }

            if (string.IsNullOrEmpty(_sessionId))
            {
                Console.WriteLine($"[Aptabase] Session not initialized, skipping event: {eventName}");
                return;
            }

            _eventQueue.Enqueue((eventName, SanitizeMessage(message)));
            Console.WriteLine($"[Aptabase] Queued event: {eventName}");

            _ = FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Aptabase] Error in TrackEvent: {ex.Message}");
            KodoDiagnostics.LogWarning("AptabaseClient.TrackEvent", ex, $"event={eventName}");
        }
    }

    public static async Task FlushAsync()
    {
        try
        {
            if (!_isEnabled)
            {
                _eventQueue.Clear();
                return;
            }

            if (_eventQueue.Count == 0) return;

            Console.WriteLine($"[Aptabase] Flushing {_eventQueue.Count} queued event(s)");

            var batch = new List<AptabaseEvent>();
            while (_eventQueue.TryDequeue(out var item))
            {
                batch.Add(new AptabaseEvent(
                    Timestamp:   DateTime.UtcNow.ToString("O"),
                    SessionId:   _sessionId!,
                    EventName:   item.eventName,
                    SystemProps: _systemProps!,
                    Props:       string.IsNullOrEmpty(item.message)
                                     ? null
                                     : new Dictionary<string, string> { ["message"] = item.message }
                ));
            }

            await SendBatchAsync(batch);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Aptabase] Error in FlushAsync: {ex.Message}");
            KodoDiagnostics.LogWarning("AptabaseClient.FlushAsync", ex, "flushing queued events");
        }
    }

    private static async Task SendBatchAsync(List<AptabaseEvent> events)
    {
        try
        {
            if (string.IsNullOrEmpty(_sessionId) || events.Count == 0)
                return;

            // Body is a bare JSON array and NOT wrapped in { "events": [___] }
            var json = JsonSerializer.Serialize(events);
            Console.WriteLine($"[Aptabase] Payload: {json}");

            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var url = "https://us.aptabase.com/api/v0/events";

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("App-Key", _appKey);

            var response = await _client.SendAsync(request, cts.Token);
            Console.WriteLine($"[Aptabase] ✓ Response status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Aptabase] Response body: {body}");
                KodoDiagnostics.LogWarning(
                    "AptabaseClient.SendBatchAsync",
                    new InvalidOperationException($"Aptabase returned {(int)response.StatusCode} {response.StatusCode}: {body}"),
                    $"eventCount={events.Count}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Aptabase] Error: {ex.GetType().Name}: {ex.Message}");
            KodoDiagnostics.LogWarning("AptabaseClient.SendBatchAsync", ex, $"eventCount={events.Count}");
        }
    }
}