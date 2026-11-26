// Licensed under the GNU GPL-v3.0
using System;
using System.Diagnostics;

namespace Kodo;

internal static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("KODO_STARTUP_TRACE") == "1";

    private static readonly bool UiTraceEnabled =
        Environment.GetEnvironmentVariable("KODO_UI_TRACE") == "1";

    public static bool IsEnabled => Enabled;

    public static bool IsUiTraceEnabled => UiTraceEnabled;

    public static void Interaction(string scope, long elapsedMs, string stage)
    {
        if (!UiTraceEnabled) return;

        try
        {
            KodoDiagnostics.ReportRuntimeMetric("ui", $"{elapsedMs}ms", $"scope={scope} stage={stage}");
        }
        catch
        {
        }
    }

    public static void Mark(string stage, string? detail = null)
    {
        if (!Enabled) return;

        try
        {
            KodoDiagnostics.ReportRuntimeMetric(
                "startup",
                $"{Clock.Elapsed.TotalMilliseconds:F1}ms",
                string.IsNullOrWhiteSpace(detail) ? $"stage={stage}" : $"stage={stage} {detail}");
        }
        catch
        {
        }
    }
}
