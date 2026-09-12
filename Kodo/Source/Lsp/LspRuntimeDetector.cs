// Licensed under GPL-v3.0
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

/// <summary>Detects external runtimes required by LSPs (Node, Java, dotnet, PowerShell).
/// Lightweight – only probes version, does not install runtimes.</summary>
internal static class LspRuntimeDetector
{
    public sealed record RuntimeInfo(bool Found, string? Version, string? RawOutput, string? Error);

    public static async Task<RuntimeInfo> DetectAsync(string runtime, string? minVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return new(true, null, null, null);
        runtime = runtime.Trim().ToLowerInvariant();
        string exe;
        string args;
        switch (runtime)
        {
            case "node": exe = "node"; args = "--version"; break;
            case "npm": exe = "npm"; args = "--version"; break;
            case "java": exe = "java"; args = "--version"; break;
            case "dotnet": exe = "dotnet"; args = "--version"; break;
            case "powershell":
            case "pwsh": exe = "pwsh"; args = "--version"; break;
            case "python": exe = "python"; args = "--version"; break;
            default: exe = runtime; args = "--version"; break;
        }
        var (found, output, error) = await TryRunAsync(exe, args, ct).ConfigureAwait(false);
        if (!found) return new(false, null, output, error ?? $"Runtime '{runtime}' not found on PATH. Install {runtime} to use this language server.");
        var version = ExtractVersion(output ?? "");
        if (!string.IsNullOrWhiteSpace(minVersion) && !string.IsNullOrWhiteSpace(version))
        {
            if (!IsVersionAtLeast(version!, minVersion!))
                return new(true, version, output, $"Runtime '{runtime}' version {version} is below required {minVersion}. Please update {runtime}.");
        }
        return new(true, version, output, null);
    }

    public static bool IsVersionAtLeast(string found, string required)
    {
        try
        {
            var f = ParseVersion(found);
            var r = ParseVersion(required);
            var len = Math.Max(f.Length, r.Length);
            for (int i = 0; i < len; i++)
            {
                var fv = i < f.Length ? f[i] : 0;
                var rv = i < r.Length ? r[i] : 0;
                if (fv > rv) return true;
                if (fv < rv) return false;
            }
            return true;
        }
        catch { return true; }
    }

    private static int[] ParseVersion(string v)
    {
        // strip leading 'v' and trailing non-numeric
        v = v.Trim().TrimStart('v', 'V');
        var parts = new System.Collections.Generic.List<int>();
        var cur = "";
        foreach (var c in v)
        {
            if (char.IsDigit(c)) cur += c;
            else if (c == '.' && cur.Length > 0) { parts.Add(int.Parse(cur)); cur = ""; }
            else if (cur.Length > 0) break;
        }
        if (cur.Length > 0 && int.TryParse(cur, out var last)) parts.Add(last);
        if (parts.Count == 0) return new[] { 0 };
        return parts.ToArray();
    }

    private static string? ExtractVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        // find first version-like token
        var m = System.Text.RegularExpressions.Regex.Match(output, @"v?(\d+\.\d+(\.\d+)?)");
        return m.Success ? m.Groups[1].Value : output.Trim().Split(' ')[0].TrimStart('v');
    }

    private static async Task<(bool found, string? output, string? error)> TryRunAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, $"Failed to start {exe}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            // Even if exit code non-zero, we got output – treat as found
            if (proc.ExitCode == 0 || !string.IsNullOrWhiteSpace(combined))
                return (true, combined.Trim(), null);
            return (false, combined, $"Exit code {proc.ExitCode}");
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is System.ComponentModel.Win32Exception)
        {
            return (false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public static string? FindOnPath(string command)
    {
        try
        {
            var fileName = command.Trim().Trim('"');
            if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return Path.GetFullPath(fileName);
            // Try with extensions on Windows
            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var trimmed = dir.Trim().Trim('"');
                if (!Directory.Exists(trimmed)) continue;
                // direct
                var candidate = Path.Combine(trimmed, fileName);
                if (File.Exists(candidate)) return candidate;
                // try with pathext
                if (Path.HasExtension(fileName)) continue;
                foreach (var ext in pathext.Split(';'))
                {
                    var withExt = candidate + ext;
                    if (File.Exists(withExt)) return withExt;
                }
            }
            return null;
        }
        catch { return null; }
    }
}
