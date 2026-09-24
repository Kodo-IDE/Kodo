// Licensed under GPL v3.0
using System;

namespace Kodo;

public enum KodoSeverity { Critical, Warning, Debug }

public sealed record ExternalToolDiagnostic(int Start, int Length, string Message, string Severity, string Code, string Source);

public sealed class DeadCodeSpan
{
    public int StartOffset { get; }
    public int Length { get; }
    public string Reason { get; }

    public DeadCodeSpan(int startOffset, int length, string reason)
    {
        StartOffset = startOffset;
        Length = length;
        Reason = reason;
    }
}

public sealed class ErrorSpan
{
    public int StartOffset { get; }
    public int Length { get; }
    public string Message { get; }
    public string Severity { get; }
    public string Code { get; }
    public string Source { get; }

    public ErrorSpan(int startOffset, int length, string message, string severity = "error", string code = "", string source = "Kodo")
    {
        StartOffset = startOffset;
        Length = Math.Max(1, length);
        Message = message;
        Severity = NormalizeSeverity(severity);
        Code = code ?? string.Empty;
        Source = string.IsNullOrWhiteSpace(source) ? "Kodo" : source;
    }

    private static string NormalizeSeverity(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "fatal" or "critical" or "error" => "error",
        "warn" or "warning" => "warning",
        "information" or "info" => "info",
        "hint" => "hint",
        _ => "error",
    };
}
