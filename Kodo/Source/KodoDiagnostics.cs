// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Kodo;

public enum KodoSeverity { Critical, Warning, Debug }

internal static class KodoDiagnostics
{
    public static string AppVersion { get; } = ResolveAppVersion();

    public static string OSDescription { get; } = ResolveOSDescription();

    public static bool VerboseLoggingEnabled { get; set; }


    private const int BreadcrumbCapacity = 50;
    private static readonly Queue<string> _breadcrumbs = new();
    private static readonly object _breadcrumbLock = new();

    private static readonly object _sessionInitLock = new();
    private static bool _kodoLogSessionInitialized;
    private static bool _crashLogSessionInitialized;

    private static void EnsureSessionLog(string path, ref bool initialized)
    {
        if (initialized) return;

        lock (_sessionInitLock)
        {
            if (initialized) return;

            if (!VerboseLoggingEnabled)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectoryPath);
                    File.WriteAllText(path, string.Empty);
                }
                catch { /* best effort - fall through to normal append/create-on-write */ }
            }

            initialized = true;
        }
    }

    private static void PushBreadcrumb(string line)
    {
        lock (_breadcrumbLock)
        {
            if (_breadcrumbs.Count >= BreadcrumbCapacity)
                _breadcrumbs.Dequeue();
            _breadcrumbs.Enqueue(line);
        }
    }

    private static IReadOnlyList<string> DrainBreadcrumbs()
    {
        lock (_breadcrumbLock)
        {
            return new List<string>(_breadcrumbs);
        }
    }


    private static string ResolveAppVersion()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "v0.0.0";

        var plusIndex = raw.IndexOf('+');
        return plusIndex >= 0 ? raw[..plusIndex] : raw;
    }

    private static string ResolveOSDescription()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.OSDescription;

        return TryGetWindowsProductName() ?? RuntimeInformation.OSDescription;
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetWindowsProductName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;

            var productName = key.GetValue("ProductName")        as string ?? string.Empty;
            var buildStr    = key.GetValue("CurrentBuildNumber") as string ?? string.Empty;
            var displayVer  = key.GetValue("DisplayVersion")     as string ?? string.Empty;

            if (!int.TryParse(buildStr, out var build)) return null;

            var edition = productName
                .Replace("Windows 10", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Windows 11", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            var winLabel = build >= 22000 ? "Windows 11" : "Windows 10";
            var fullName = string.IsNullOrWhiteSpace(edition) ? winLabel : $"{winLabel} {edition}";

            // e.g. "Windows 11 Pro 24H2 (build 26200)"
            return string.IsNullOrWhiteSpace(displayVer)
                ? $"{fullName} (build {build})"
                : $"{fullName} {displayVer} (build {build})";
        }
        catch
        {
            return null;
        }
    }


    public static string LogDirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kodo");

    public static string MainLogFilePath => Path.Combine(LogDirectoryPath, "kodo.log");

    public static string CrashLogFilePath => Path.Combine(LogDirectoryPath, "crash.log");

    public static string LogFilePath => MainLogFilePath;

    public static DateTime UtcNow() => DateTime.UtcNow;


    public static string BuildDiagnosticPayload(
        string source,
        Exception exception,
        bool isTerminating,
        KodoSeverity severity,
        string? operation = null,
        bool redactPaths = false)
    {
        var timestamp = UtcNow();
        var sb = new StringBuilder();
        sb.Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC]")
          .Append(' ').Append(SeverityLabel(severity));

        if (!string.IsNullOrWhiteSpace(operation))
            sb.Append(" (").Append(operation).Append(')');

        sb.AppendLine();
        sb.Append("Source: ").AppendLine(source);
        sb.Append("Terminating: ").AppendLine(isTerminating ? "Yes" : "No");
        sb.Append("Version: ").AppendLine(AppVersion);
        sb.Append("OS: ").AppendLine(OSDescription);
        sb.Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription);
        sb.Append("Architecture: ").Append(RuntimeInformation.ProcessArchitecture)
          .Append(" / ").AppendLine(Environment.Is64BitProcess ? "64-bit" : "32-bit");
        sb.Append("Log: ").AppendLine(redactPaths ? RedactPath(MainLogFilePath) : MainLogFilePath);
        sb.AppendLine();
        sb.AppendLine(redactPaths ? RedactExceptionText(exception.ToString()) : exception.ToString());
        return sb.ToString();
    }

    public static string BuildDiagnosticSummary(
        string source,
        bool isTerminating,
        string? operation = null,
        bool redactPaths = false)
    {
        var timestamp = UtcNow().ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        var summary = new StringBuilder();
        summary.Append("Time: ").Append(timestamp)
               .Append("  |  Source: ").Append(source)
               .Append("  |  Version: ").Append(AppVersion);

        if (!string.IsNullOrWhiteSpace(operation))
            summary.Append("  |  Operation: ").Append(operation);

        summary.Append("  |  ").Append(isTerminating ? "Terminating" : "Recoverable");
        return redactPaths ? RedactExceptionText(summary.ToString()) : summary.ToString();
    }


    public static void LogCritical(
        string source,
        Exception exception,
        bool isTerminating,
        string? operation = null)
    {
        WriteToLog(source, exception, isTerminating, KodoSeverity.Critical, operation);
        WriteCrashLog(source, exception, isTerminating, operation);
    }

    public static void LogWarning(
        string source,
        Exception exception,
        string? operation = null) =>
        WriteToLog(source, exception, isTerminating: false, KodoSeverity.Warning, operation);

    // Emits a Debug trace; only reaches kodo.log while Debug Logging is enabled
    public static void LogDebug(string message, Exception? exception = null)
    {
        try
        {
            Debug.WriteLine(exception is null
                ? $"[Kodo] {message}"
                : $"[Kodo] {message}{Environment.NewLine}{exception}");

            if (!VerboseLoggingEnabled) return;

            if (exception is not null)
                WriteToLog("KodoDiagnostics.Debug", exception, false, KodoSeverity.Debug, message);
            else
                WriteVerboseTrace(message);
        }
        catch { /* never throw from a debug trace */ }
    }

    private static void WriteVerboseTrace(string message)
    {
        var timestamp = UtcNow().ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        var line = $"[{timestamp}] VERBOSE  {message}";
        EnsureSessionLog(MainLogFilePath, ref _kodoLogSessionInitialized);
        PushBreadcrumb(line);
        WritePayloadToDisk(line, MainLogFilePath);
    }


    public static void WriteDiagnosticLog(
        string source,
        Exception exception,
        bool isTerminating,
        string severity,
        string? operation = null) =>
        WriteToLog(source, exception, isTerminating, ParseSeverity(severity), operation);


    private static void WriteToLog(
        string source,
        Exception exception,
        bool isTerminating,
        KodoSeverity severity,
        string? operation)
    {
        var payload = string.Empty;
        try
        {
            payload = BuildDiagnosticPayload(source, exception, isTerminating, severity, operation);
        }
        catch
        {
            payload = $"[Kodo] Log payload build failed. Source={source} " +
                      $"Exception={exception?.GetType()}:{exception?.Message}";
        }

        EnsureSessionLog(MainLogFilePath, ref _kodoLogSessionInitialized);
        PushBreadcrumb(payload);
        WritePayloadToDisk(payload, MainLogFilePath);
    }

    private static void WriteCrashLog(
        string source,
        Exception exception,
        bool isTerminating,
        string? operation)
    {
        try
        {
            EnsureSessionLog(CrashLogFilePath, ref _crashLogSessionInitialized);

            var sb = new StringBuilder();
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.Append('[').Append(UtcNow().ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" UTC] CRASH REPORT");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine();

            var crumbs = DrainBreadcrumbs();
            if (crumbs.Count > 0)
            {
                sb.AppendLine("── Recent activity ──────────────────────────────────────────");
                foreach (var crumb in crumbs)
                    sb.AppendLine(crumb);
                sb.AppendLine();
            }

            sb.AppendLine("── Crash ────────────────────────────────────────────────────");
            sb.AppendLine(BuildDiagnosticPayload(source, exception, isTerminating, KodoSeverity.Critical, operation));

            WritePayloadToDisk(sb.ToString(), CrashLogFilePath);
        }
        catch { /* crash log generation must never itself crash */ }
    }

    private static string RedactExceptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var result = text;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

        if (!string.IsNullOrWhiteSpace(appData))
            result = result.Replace(appData, @"%AppData%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(localAppData))
            result = result.Replace(localAppData, @"%LocalAppData%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(repoRoot))
            result = result.Replace(repoRoot, @"<repo>", StringComparison.OrdinalIgnoreCase);

        return Regex.Replace(
            result,
            @"\b[A-Za-z]:\\[^\r\n\t]+",
            _ => "<path>",
            RegexOptions.Compiled);
    }

    private static string RedactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

        if (!string.IsNullOrWhiteSpace(repoRoot) &&
            path.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return path.Replace(repoRoot, "<repo>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(appData) &&
            path.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
            return path.Replace(appData, "%AppData%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(localAppData) &&
            path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
            return path.Replace(localAppData, "%LocalAppData%", StringComparison.OrdinalIgnoreCase);

        return path;
    }

    private static void WritePayloadToDisk(string payload, string primaryPath)
    {
        try
        {
            Directory.CreateDirectory(LogDirectoryPath);
            File.AppendAllText(primaryPath, payload + Environment.NewLine);
            return;
        }
        catch { /* fall through */ }

        try
        {
            var tempLog = Path.Combine(Path.GetTempPath(), Path.GetFileName(primaryPath));
            File.AppendAllText(tempLog, payload + Environment.NewLine);
            return;
        }
        catch { /* fall through */ }

        try
        {
            var desktopLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.GetFileName(primaryPath));
            File.AppendAllText(desktopLog, payload + Environment.NewLine);
        }
        catch { /* all attempts exhausted */ }
    }


    private static string SeverityLabel(KodoSeverity severity) => severity switch
    {
        KodoSeverity.Critical => "CRITICAL",
        KodoSeverity.Warning  => "WARNING",
        KodoSeverity.Debug    => "DEBUG",
        _                     => "INFO",
    };

    private static KodoSeverity ParseSeverity(string label) => label.ToUpperInvariant() switch
    {
        "CRASH" or "CRITICAL" or "FATAL" => KodoSeverity.Critical,
        "DEBUG"                           => KodoSeverity.Debug,
        _                                 => KodoSeverity.Warning,
    };
}

internal static class WindowsThemeHelper
{
    public static string KodoDataPath(string file) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", file);
    public static string? GetWindowsAccentHex()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int raw)
            {
                var r = raw & 0xFF; var g = (raw >> 8) & 0xFF; var b = (raw >> 16) & 0xFF;
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { }
        return null;
    }
    public static bool? GetIsLightTheme()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return null;
    }
    public static Avalonia.Media.IBrush GetReadableForeground(Avalonia.Media.Color bg)
    {
        static double Lum(Avalonia.Media.Color c) { double To(double ch){ ch/=255; return ch<=0.03928?ch/12.92:Math.Pow((ch+0.055)/1.055,2.4);} return 0.2126*To(c.R)+0.7152*To(c.G)+0.0722*To(c.B); }
        var l = Lum(bg);
        return (1.05/(l+0.05)) >= ((l+0.05)/0.05) ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.Black;
    }
    public static Avalonia.Media.Color Lighten(Avalonia.Media.Color c, double a) { byte Adj(byte ch) => (byte)Math.Clamp(ch+(255-ch)*a,0,255); return Avalonia.Media.Color.FromArgb(c.A, Adj(c.R), Adj(c.G), Adj(c.B)); }
    public static Avalonia.Media.Color Darken(Avalonia.Media.Color c, double a) { byte Adj(byte ch) => (byte)Math.Clamp(ch*(1-a),0,255); return Avalonia.Media.Color.FromArgb(c.A, Adj(c.R), Adj(c.G), Adj(c.B)); }
}