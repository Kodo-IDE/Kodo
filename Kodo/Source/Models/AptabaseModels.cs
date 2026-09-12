// Licensed under GPL-v3.0
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kodo;

internal sealed record AptabaseSystemProps(
    [property: JsonPropertyName("isDebug")] bool IsDebug,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("sdkVersion")] string SdkVersion,
    [property: JsonPropertyName("osName")] string OsName);

internal sealed record AptabaseEvent(
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("eventName")] string EventName,
    [property: JsonPropertyName("systemProps")] AptabaseSystemProps SystemProps,
    [property: JsonPropertyName("props")] Dictionary<string, string>? Props);
