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

internal static class LspInstallationManager
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Kodo/{KodoDiagnostics.AppVersion} (https://github.com/Kodo-IDE/Kodo)");
        return c;
    }

    public static string ManagedRoot => GetManagedRoot(null);

    public static string GetManagedRoot(AppSettings? settings)
    {
        var custom = settings?.LspInstallDir?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                var full = Path.GetFullPath(custom);
                if (Path.IsPathRooted(full)) return full;
            }
            catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "Lsp");
    }

    public static string GetManagedRoot() => ManagedRoot;

    private static string SanitizeProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("ProviderId required");
        var sanitized = new string(providerId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "lsp";
        sanitized = sanitized.Replace("..", "_");
        return sanitized.ToLowerInvariant();
    }

    public static string GetProviderDir(LspConfiguration cfg) => GetProviderDir(cfg, null);
    public static string GetProviderDir(LspConfiguration cfg, AppSettings? settings)
    {
        var root = GetManagedRoot(settings);
        var sanitized = SanitizeProviderId(cfg.EffectiveProviderId);
        var dir = Path.Combine(root, sanitized);
        var fullRoot = Path.GetFullPath(root);
        var fullDir = Path.GetFullPath(dir);
        if (!FileSystemPaths.IsPrefixOf(fullDir, fullRoot))
            throw new InvalidOperationException($"Provider directory escapes managed root: {fullDir}");
        return fullDir;
    }

    public static string GetManagedExecutablePath(LspConfiguration cfg) => GetManagedExecutablePath(cfg, null);
    public static string GetManagedExecutablePath(LspConfiguration cfg, AppSettings? settings)
    {
        var dir = GetProviderDir(cfg, settings);
        var cmd = cfg.Command.Trim().Trim('"');
        var fileName = Path.GetFileName(cmd);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = SanitizeProviderId(cfg.EffectiveProviderId);
        fileName = Path.GetFileName(fileName);
        return Path.Combine(dir, fileName);
    }

    public static string GetManagedPackagePath(LspConfiguration cfg, AppSettings? settings) =>
        Path.Combine(GetProviderDir(cfg, settings), ExpectedArtifactFileName(cfg, settings));

    internal static string GetDownloadFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var withoutQuery = url.Trim();
        var cut = withoutQuery.IndexOfAny(['?', '#']);
        if (cut >= 0) withoutQuery = withoutQuery[..cut];

        var lastSlash = withoutQuery.LastIndexOf('/');
        if (lastSlash >= 0) withoutQuery = withoutQuery[(lastSlash + 1)..];
        withoutQuery = Uri.UnescapeDataString(withoutQuery);

        if (withoutQuery.Length == 0) return string.Empty;
        if (withoutQuery.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return string.Empty;
        if (withoutQuery is "." or "..") return string.Empty;
        return withoutQuery;
    }

    public static string ExpectedArtifactFileName(LspConfiguration cfg, AppSettings? settings)
    {
        if (UsesRuntimeLauncher(cfg) && GetDownloadFileName(cfg.DownloadUrl) is { Length: > 0 } downloadName)
            return downloadName;
        return Path.GetFileName(GetManagedExecutablePath(cfg, settings));
    }

    public static bool UsesRuntimeLauncher(LspConfiguration cfg) =>
        !string.IsNullOrWhiteSpace(cfg.Runtime) &&
        (cfg.RuntimeArgs.Length > 0 || !string.IsNullOrWhiteSpace(cfg.MainClass));

    internal readonly record struct LspLaunchPlan(string FileName, IReadOnlyList<string> PrefixArgs);

    internal static LspLaunchPlan ResolveLaunchPlan(LspConfiguration cfg, AppSettings? settings, string? artifact = null)
    {
        if (!UsesRuntimeLauncher(cfg))
            return new(cfg.Command.Trim().Trim('"'), Array.Empty<string>());

        var runtime = cfg.Runtime!.Trim();
        var resolvedRuntime = LspRuntimeDetector.FindOnPath(runtime) ?? runtime;
        var package = artifact ?? GetManagedPackagePath(cfg, settings);

        var prefix = new List<string>(cfg.RuntimeArgs.Length + 1);
        foreach (var arg in cfg.RuntimeArgs)
        {
            prefix.Add(arg.Replace("{package}", package, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(cfg.MainClass)) prefix.Add(cfg.MainClass!.Trim());
        return new(resolvedRuntime, prefix);
    }

    public static bool IsManagedInstalled(LspConfiguration cfg) => IsManagedInstalled(cfg, null);
    public static bool IsManagedInstalled(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = FindManagedExecutable(cfg, settings);
        return exe != null && File.Exists(exe);
    }

    public static string? FindSystemExecutable(LspConfiguration cfg)
    {
        if (!cfg.AllowSystem) return null;
        var found = LspRuntimeDetector.FindOnPath(cfg.Command);
        if (found != null) return found;
        if (cfg.InstallMethod?.Equals("dotnet", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var toolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
                var candidates = new[] { Path.Combine(toolsDir, cfg.Command), Path.Combine(toolsDir, cfg.Command + ".exe"), Path.Combine(toolsDir, cfg.EffectiveProviderId), Path.Combine(toolsDir, cfg.EffectiveProviderId + ".exe") };
                foreach (var c in candidates) if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    public static string? FindManagedExecutable(LspConfiguration cfg) => FindManagedExecutable(cfg, null);
    public static string? FindManagedExecutable(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = UsesRuntimeLauncher(cfg)
            ? GetManagedPackagePath(cfg, settings)
            : GetManagedExecutablePath(cfg, settings);
        if (File.Exists(exe)) return exe;
        var dir = GetProviderDir(cfg, settings);
        return FindExecutableInDirectory(dir, Path.GetFileName(exe));
    }

    internal static string? FindExecutableInDirectory(string dir, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || string.IsNullOrWhiteSpace(expectedFileName)) return null;
        try
        {
            var targetName = Path.GetFileName(expectedFileName);
            var targetWithoutExt = Path.GetFileNameWithoutExtension(targetName);
            var candidates = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => {
                    var name = Path.GetFileName(f);
                    return name.Equals(targetName, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(name).Equals(targetWithoutExt, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            var binPref = candidates.FirstOrDefault(c => c.Contains("node_modules" + Path.DirectorySeparatorChar + ".bin", StringComparison.OrdinalIgnoreCase));
            if (binPref != null) return binPref;
            return candidates.FirstOrDefault();
        }
        catch { return null; }
    }

    internal static async Task PromoteStagedDirectoryAsync(string stagingDir, string providerDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(stagingDir))
            throw new DirectoryNotFoundException($"Staged language-server directory was not found: {stagingDir}");

        var parent = Path.GetDirectoryName(Path.GetFullPath(providerDir));
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("Managed language-server install directory has no parent.");
        Directory.CreateDirectory(parent);

        var backupDir = providerDir + ".previous-" + Guid.NewGuid().ToString("N");
        var movedPrevious = false;
        if (Directory.Exists(providerDir))
        {
            const int maxAttempts = 5;
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(providerDir, backupDir);
                    movedPrevious = true;
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts - 1)
                {
                    await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }

        try
        {
            const int maxPromotionAttempts = 5;
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(stagingDir, providerDir);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxPromotionAttempts - 1)
                {
                    await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception promotionError)
        {
            if (movedPrevious)
            {
                try { Directory.Move(backupDir, providerDir); }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"Could not activate the new language server or restore the previous one. The previous installation is preserved at '{backupDir}'.",
                        new AggregateException(promotionError, restoreError));
                }
            }
            throw;
        }

        if (movedPrevious)
        {
            try { Directory.Delete(backupDir, recursive: true); }
            catch (Exception ex) { KodoDiagnostics.LogDebug($"Old LSP installation retained at {backupDir}", ex); }
        }
    }

    private static readonly ConcurrentDictionary<string, (bool ok, string? version, string? error)> VersionProbeCache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<(bool ok, string? version, string? error)> TryGetVersionAsync(string exePath, string[] versionArgs, CancellationToken ct = default)
        => TryGetVersionAsync(exePath, versionArgs, [], ct);

    public static async Task<(bool ok, string? version, string? error)> TryGetVersionAsync(
        string exePath, string[] versionArgs, IReadOnlyList<string> prefixArgs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return (false, null, "Executable not found");
        var args = versionArgs != null && versionArgs.Length > 0 ? string.Join(" ", versionArgs) : "--version";
        var cacheKey = exePath + "\0" + args;
        if (VersionProbeCache.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            bool isCmdScript = exePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            string fileName = exePath;

            var allArgs = new List<string>(prefixArgs.Count + 1);
            if (isCmdScript && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                fileName = comSpec;
                allArgs.Add("/c");
                allArgs.Add(exePath);
            }
            allArgs.AddRange(prefixArgs);
            allArgs.Add(args);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in allArgs) psi.ArgumentList.Add(arg);
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, "Failed to start");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var outText = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(outText))
                return (false, null, $"Exit code {proc.ExitCode}: {stderr.Trim()}");
            var result = (true, outText?.Trim(), (string?)null);
            VersionProbeCache[cacheKey] = result;
            return result;
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public enum InstallResultKind { Success, AlreadyInstalled, Failed, Cancelled, Offline, RuntimeMissing, NotInstallable }

    public sealed record InstallResult(InstallResultKind Kind, string? Message, string? InstalledPath);

    public static bool IsOffline(Exception ex)
    {
        return ex is HttpRequestException || ex is TaskCanceledException || ex is IOException && ex.Message.Contains("offline", StringComparison.OrdinalIgnoreCase);
    }

    private static SemaphoreSlim GetLock(string providerId) => Locks.GetOrAdd(SanitizeProviderId(providerId), _ => new SemaphoreSlim(1, 1));

    public static async Task<InstallResult> InstallAsync(LspConfiguration cfg, IProgress<string>? progress, CancellationToken ct = default)
        => await InstallAsync(cfg, null, progress, ct).ConfigureAwait(false);

    public static async Task<InstallResult> InstallAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (cfg == null) return new(InstallResultKind.Failed, "Provider not configured", null);
        if (cfg.InstallMethod != null && cfg.InstallMethod.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' requires manual installation.", null);
        if (!cfg.AllowAutoInstall)
            return new(InstallResultKind.NotInstallable, $"Automatic installation disabled for '{cfg.EffectiveProviderId}'.", null);

        if (!string.IsNullOrWhiteSpace(cfg.Runtime))
        {
            var rt = await LspRuntimeDetector.DetectAsync(cfg.Runtime!, cfg.RuntimeMinVersion, ct).ConfigureAwait(false);
            if (!rt.Found || !string.IsNullOrWhiteSpace(rt.Error))
                return new(InstallResultKind.RuntimeMissing, rt.Error ?? $"Runtime '{cfg.Runtime}' required.", null);
        }

        var providerId = SanitizeProviderId(cfg.EffectiveProviderId);
        var sem = GetLock(providerId);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsManagedInstalled(cfg, settings))
                return new(InstallResultKind.AlreadyInstalled, "Already installed", GetProviderDir(cfg, settings));

            var method = (cfg.InstallMethod ?? "").Trim().ToLowerInvariant();
            if (method == "dotnet")
                return await InstallViaDotnetAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            if (method == "npm" || (!string.IsNullOrWhiteSpace(cfg.PackageName) && string.IsNullOrWhiteSpace(cfg.DownloadUrl)))
                return await InstallViaNpmAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.DownloadUrl))
            {
                var isGithub = method == "github" || cfg.DownloadUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) || cfg.DownloadUrl.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(cfg.Sha256))
                    return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' download requires SHA-256 verification. No checksum provided – please install manually from {cfg.DownloadUrl} and configure an override, or update the provider to include a trusted checksum.", null);
                return await InstallViaDownloadAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            }

            return new(InstallResultKind.NotInstallable, $"No install source configured for '{cfg.EffectiveProviderId}'. Install manually and ensure '{cfg.Command}' is on PATH.", null);
        }
        finally { sem.Release(); }
    }

    private static readonly System.Text.RegularExpressions.Regex NpmPackageNameRegex = new(@"^(@[a-z0-9-~][a-z0-9-._~]*\/)?[a-z0-9-~][a-z0-9-._~]*(@[a-z0-9-._~][a-z0-9-._~.-]*)?$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsValidNpmPackageName(string pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg)) return false;
        if (pkg.IndexOfAny(new[] { ';', '&', '|', '`', '$', '(', ')', '<', '>', '"', '\'', '\\', ' ', '\n', '\r', '\t' }) >= 0) return false;
        if (pkg.Contains("..", StringComparison.Ordinal)) return false;
        return NpmPackageNameRegex.IsMatch(pkg.Trim());
    }

    private static ProcessStartInfo BuildNpmProcessStartInfo(string npmExe, string[] npmArgs, string workingDirectory)
    {
        var isCmdScript = npmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || npmExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (isCmdScript && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            static string EscapeArg(string a)
            {
                if (string.IsNullOrEmpty(a)) return "\"\"";
                if (!a.Contains(' ') && !a.Contains('"') && !a.Contains('\t')) return a;
                return "\"" + a.Replace("\"", "\"\"") + "\"";
            }
            var inner = $"\"{npmExe}\" {string.Join(" ", npmArgs.Select(EscapeArg))}";
            return new ProcessStartInfo
            {
                FileName = comSpec,
                Arguments = $"/d /s /c \"{inner}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = npmExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
            foreach (var a in npmArgs) psi.ArgumentList.Add(a);
            return psi;
        }
    }

    private static async Task<InstallResult> InstallViaNpmAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var pkg = cfg.PackageName ?? cfg.EffectiveProviderId;
        if (!IsValidNpmPackageName(pkg))
            return new(InstallResultKind.Failed, $"Invalid npm package name '{pkg}'. Package name contains illegal characters or pattern.", null);
        var nodeRt = await LspRuntimeDetector.DetectAsync("node", cfg.RuntimeMinVersion ?? "16.0.0", ct).ConfigureAwait(false);
        if (!nodeRt.Found) return new(InstallResultKind.RuntimeMissing, nodeRt.Error ?? "Node.js is required. Install Node.js 16+ from https://nodejs.org/", null);
        var npmRt = await LspRuntimeDetector.DetectAsync("npm", null, ct).ConfigureAwait(false);
        if (!npmRt.Found) return new(InstallResultKind.RuntimeMissing, "npm is required but not found. Install Node.js which includes npm.", null);

        progress?.Report($"Installing {pkg} via npm...");
        KodoDiagnostics.LogDebug($"LSP npm install {pkg}");
        var providerDir = GetProviderDir(cfg, settings);
        var stagingDir = providerDir + ".staging-" + Guid.NewGuid().ToString("N");
        var expectedFileName = ExpectedArtifactFileName(cfg, settings);
        try
        {
            Directory.CreateDirectory(stagingDir);
            var npmExe = LspRuntimeDetector.FindExecutable("npm") ?? LspRuntimeDetector.FindOnPath("npm") ?? "npm";
            var npmArgs = new[] { "install", "--prefix", stagingDir, "--ignore-scripts", "--no-audit", "--no-fund", "--progress=false", "--loglevel=error", pkg };
            var psi = BuildNpmProcessStartInfo(npmExe, npmArgs, Path.GetTempPath());
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return new(InstallResultKind.Failed, "Failed to start npm", null);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"npm install stdout: {stdout}\nstderr: {stderr}");
            if (proc.ExitCode != 0)
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"npm install failed (exit {proc.ExitCode}): {stderr.Trim()}", null);
            }
            var stagedExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => FileSystemPaths.Equals(Path.GetFileName(f), expectedFileName)
                    || Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(expectedFileName), StringComparison.OrdinalIgnoreCase));
            if (stagedExe == null)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "npm install did not produce the configured language-server executable", null);
            }
            try
            {
                await PromoteStagedDirectoryAsync(stagingDir, providerDir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to finalize installation; the previous provider was preserved where possible: {ex.Message}", null);
            }
            progress?.Report($"Installed {pkg}");
            return new(InstallResultKind.Success, $"Installed {pkg} via npm", providerDir);
        }
        catch (Exception ex) when (IsOffline(ex))
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            return new(InstallResultKind.Offline, $"Offline – could not download {pkg}: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            KodoDiagnostics.LogDebug($"LSP npm install failed for {pkg}", ex);
            return new(InstallResultKind.Failed, $"Installation failed: {ex.Message}", null);
        }
    }

    private static async Task<InstallResult> InstallViaDotnetAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var pkg = cfg.PackageName ?? cfg.EffectiveProviderId;
        var dotnetRt = await LspRuntimeDetector.DetectAsync("dotnet", cfg.RuntimeMinVersion ?? "6.0.0", ct).ConfigureAwait(false);
        if (!dotnetRt.Found) return new(InstallResultKind.RuntimeMissing, dotnetRt.Error ?? "dotnet SDK is required. Install from https://dotnet.microsoft.com/download", null);
        progress?.Report($"Installing {pkg} via dotnet...");
        KodoDiagnostics.LogDebug($"LSP dotnet tool install {pkg}");
        try
        {
            var listPsi = new ProcessStartInfo { FileName = "dotnet", Arguments = "tool list --global", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var listProc = new Process { StartInfo = listPsi };
            if (listProc.Start())
            {
                var outTxt = await listProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await listProc.WaitForExitAsync(ct).ConfigureAwait(false);
                if (outTxt.Contains(pkg, StringComparison.OrdinalIgnoreCase))
                {
                    var updPsi = new ProcessStartInfo { FileName = "dotnet", Arguments = $"tool update --global {pkg}", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using var updProc = new Process { StartInfo = updPsi };
                    if (updProc.Start())
                    {
                        var uo = await updProc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                        var ue = await updProc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                        await updProc.WaitForExitAsync(ct).ConfigureAwait(false);
                        KodoDiagnostics.LogDebug($"dotnet tool update stdout: {uo} stderr: {ue} exit:{updProc.ExitCode}");
                        if (updProc.ExitCode == 0) return new(InstallResultKind.Success, $"Updated {pkg} via dotnet tool", null);
                    }
                }
            }
            var psi = new ProcessStartInfo { FileName = "dotnet", Arguments = $"tool install --global {pkg}", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return new(InstallResultKind.Failed, "Failed to start dotnet", null);
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            KodoDiagnostics.LogDebug($"dotnet tool install stdout: {stdout} stderr: {stderr} exit:{proc.ExitCode}");
            if (proc.ExitCode != 0)
                return new(InstallResultKind.Failed, $"dotnet tool install failed (exit {proc.ExitCode}): {stderr.Trim()}", null);
            return new(InstallResultKind.Success, $"Installed {pkg} via dotnet tool", null);
        }
        catch (Exception ex) when (IsOffline(ex))
        {
            return new(InstallResultKind.Offline, $"Offline – could not download {pkg}: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"LSP dotnet install failed for {pkg}", ex);
            return new(InstallResultKind.Failed, $"Installation failed: {ex.Message}", null);
        }
    }

    private static async Task<InstallResult> InstallViaDownloadAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var url = cfg.DownloadUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return new(InstallResultKind.NotInstallable, "No download URL", null);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new(InstallResultKind.Failed, "Download URL must be HTTPS", null);
        if (string.IsNullOrWhiteSpace(cfg.Sha256))
            return new(InstallResultKind.NotInstallable, "SHA-256 checksum required for verification – not provided", null);

        progress?.Report($"Downloading {cfg.EffectiveProviderId}...");
        KodoDiagnostics.LogDebug($"LSP download {url}");
        var providerDir = GetProviderDir(cfg, settings);
        var stagingDir = providerDir + ".staging-" + Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(Path.GetTempPath(), $"kodo-lsp-{SanitizeProviderId(cfg.EffectiveProviderId)}-{Guid.NewGuid():N}.tmp");
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.RequestMessage?.RequestUri != null && !resp.RequestMessage.RequestUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return new(InstallResultKind.Failed, "Download redirected to non-HTTPS URL – blocked for security", null);
            if (!resp.IsSuccessStatusCode)
                return new(InstallResultKind.Failed, $"Download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", null);
            await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long totalRead = 0;
            var contentLength = resp.Content.Headers.ContentLength;
            int read;
            while ((read = await netStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                totalRead += read;
                if (contentLength.HasValue && contentLength.Value > 0)
                    progress?.Report($"Downloading {cfg.EffectiveProviderId}... {totalRead * 100 / contentLength.Value}%");
            }
            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            fileStream.Close();

            var hash = hasher.GetHashAndReset();
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var expected = cfg.Sha256!.Trim().ToLowerInvariant().Replace(" ", "").Replace("0x", "");
            if (!hex.Equals(expected, StringComparison.Ordinal))
            {
                try { File.Delete(tempFile); } catch { }
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Checksum mismatch for {cfg.EffectiveProviderId}. Expected {expected}, got {hex}.", null);
            }

            progress?.Report($"Installing {cfg.EffectiveProviderId}...");
            Directory.CreateDirectory(stagingDir);
            var expectedFileName = ExpectedArtifactFileName(cfg, settings);
            var shape = ClassifyDownload(url, tempFile);
            if (shape == DownloadShape.Zip)
            {
                await ExtractZipSecureAsync(tempFile, stagingDir, ct).ConfigureAwait(false);
            }
            else if (shape == DownloadShape.TarGz)
            {
                await ExtractTarGzSecureAsync(tempFile, stagingDir, ct).ConfigureAwait(false);
            }
            else
            {
                File.Copy(tempFile, Path.Combine(stagingDir, expectedFileName), true);
                MakeExecutable(Path.Combine(stagingDir, expectedFileName));
            }
            if (!Directory.EnumerateFileSystemEntries(stagingDir).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "Archive extracted no files", null);
            }
            var foundExe = FindExecutableInDirectory(stagingDir, expectedFileName);
            if (foundExe == null)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                var what = shape == DownloadShape.SingleFile ? "Download" : "Extracted archive";
                var listing = string.Join(", ", Directory.Exists(stagingDir)
                    ? Directory.EnumerateFileSystemEntries(stagingDir, "*", SearchOption.AllDirectories)
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(8)
                    : Array.Empty<string>());
                return new(InstallResultKind.Failed,
                    $"{what} does not contain the configured language-server executable '{expectedFileName}'" +
                    (listing.Length > 0 ? $". Found instead: {listing}" : string.Empty), null);
            }
            try { File.Delete(tempFile); } catch { }
            try
            {
                await PromoteStagedDirectoryAsync(stagingDir, providerDir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to finalize installation; the previous provider was preserved where possible: {ex.Message}", null);
            }
            progress?.Report($"Installed {cfg.EffectiveProviderId}");
            KodoDiagnostics.LogDebug($"LSP installed {cfg.EffectiveProviderId} to {providerDir}");
            return new(InstallResultKind.Success, $"Installed {cfg.EffectiveProviderId}", providerDir);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            return new(InstallResultKind.Cancelled, "Installation cancelled", null);
        }
        catch (Exception ex) when (IsOffline(ex))
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            return new(InstallResultKind.Offline, $"Offline – could not download {cfg.EffectiveProviderId}: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            KodoDiagnostics.LogDebug($"LSP download/install failed for {cfg.EffectiveProviderId}", ex);
            return new(InstallResultKind.Failed, $"Installation failed: {ex.Message}", null);
        }
    }

    private enum DownloadShape { SingleFile, Zip, TarGz }

    private static DownloadShape ClassifyDownload(string url, string downloadedFile)
    {
        var withoutQuery = url;
        var cut = withoutQuery.IndexOfAny(['?', '#']);
        if (cut >= 0) withoutQuery = withoutQuery[..cut];

        var name = withoutQuery;
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return DownloadShape.TarGz;
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return DownloadShape.Zip;
        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".war", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase))
            return DownloadShape.SingleFile;

        if (IsZipFile(downloadedFile)) return DownloadShape.Zip;
        if (IsTarGzFile(downloadedFile)) return DownloadShape.TarGz;
        return DownloadShape.SingleFile;
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return false;
            return header[0] == 0x50 && header[1] == 0x4B;
        }
        catch { return false; }
    }

    private static bool IsTarGzFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = new byte[2];
            if (fs.Read(header, 0, 2) < 2) return false;
            return header[0] == 0x1F && header[1] == 0x8B;
        }
        catch { return false; }
    }

    private static async Task ExtractZipSecureAsync(string zipPath, string destDir, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue;
            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            var fullDestDir = Path.GetFullPath(destDir);
            if (!FileSystemPaths.IsPrefixOf(destPath, fullDestDir))
                throw new InvalidDataException($"Zip entry escapes destination: {entry.FullName}");
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (entry.FullName.EndsWith("/")) continue;
            await Task.Run(() => entry.ExtractToFile(destPath, overwrite: true), ct).ConfigureAwait(false);
        }
        RestoreUnixExecBit(destDir);
    }

    private static async Task ExtractTarGzSecureAsync(string tgzPath, string destDir, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var fs = File.OpenRead(tgzPath);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);

            using var reader = new System.Formats.Tar.TarReader(gz, leaveOpen: false);

            var fullDestDir = Path.GetFullPath(destDir);
            while (reader.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();

                var entryName = entry.Name;
                if (string.IsNullOrWhiteSpace(entryName)) continue;

                var destPath = Path.GetFullPath(Path.Combine(destDir, entryName));
                if (!FileSystemPaths.IsPrefixOf(destPath, fullDestDir))
                    throw new InvalidDataException($"Tar entry escapes destination: {entryName}");

                if (entry.EntryType is System.Formats.Tar.TarEntryType.Directory)
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile
                    or System.Formats.Tar.TarEntryType.V7RegularFile
                    or System.Formats.Tar.TarEntryType.ContiguousFile))
                {
                    continue;
                }

                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var outFs = File.Create(destPath);
                if (entry.DataStream is not null) entry.DataStream.CopyTo(outFs);
            }
        }, ct).ConfigureAwait(false);
        RestoreUnixExecBit(destDir);
    }

    private static void RestoreUnixExecBit(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is ".md" or ".txt" or ".json" or ".png" or ".svg" or ".xml" or ".pdb" or ".dll")
                        continue;
                    var mode = File.GetUnixFileMode(file);
                    mode |= System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute;
                    File.SetUnixFileMode(file, mode);
                }
                catch { }
            }
        }
        catch { }
    }

    internal static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch { }
    }

    private static bool HasInternetConnection()
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; }
    }

    public static InstallResult Uninstall(LspConfiguration cfg) => Uninstall(cfg, null);
    public static InstallResult Uninstall(LspConfiguration cfg, AppSettings? settings)
    {
        try
        {
            var dir = GetProviderDir(cfg, settings);
            var fullRoot = Path.GetFullPath(GetManagedRoot(settings));
            var fullDir = Path.GetFullPath(dir);
            if (!FileSystemPaths.IsPrefixOf(fullDir, fullRoot) || FileSystemPaths.Equals(fullDir, fullRoot))
                return new(InstallResultKind.Failed, "Uninstall blocked: invalid provider directory", null);
            if (!Directory.Exists(dir)) return new(InstallResultKind.Failed, "Not installed", null);
            Directory.Delete(dir, true);
            return new(InstallResultKind.Success, "Uninstalled", null);
        }
        catch (Exception ex) { return new(InstallResultKind.Failed, ex.Message, null); }
    }

    public static async Task<InstallResult> UpdateAsync(LspConfiguration cfg, IProgress<string>? progress, CancellationToken ct = default)
        => await UpdateAsync(cfg, null, progress, ct).ConfigureAwait(false);

    public static async Task<InstallResult> UpdateAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct = default)
    {
        var uninstall = Uninstall(cfg, settings);
        return await InstallAsync(cfg, settings, progress, ct).ConfigureAwait(false);
    }
}

internal static class LspRuntimeDetector
{
    public sealed record RuntimeInfo(bool Found, string? Version, string? RawOutput, string? Error);

    private static readonly TimeSpan DetectCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (DateTime stamp, RuntimeInfo info)> DetectCache = new(StringComparer.Ordinal);

    public static void InvalidateCache() => DetectCache.Clear();

    public static async Task<RuntimeInfo> DetectAsync(string runtime, string? minVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return new(true, null, null, null);
        runtime = runtime.Trim().ToLowerInvariant();
        var cacheKey = runtime + "\0" + (minVersion ?? string.Empty);
        var now = DateTime.UtcNow;
        if (DetectCache.TryGetValue(cacheKey, out var cached) && now - cached.stamp < DetectCacheTtl)
            return cached.info;
        var info = await DetectCoreAsync(runtime, minVersion, ct).ConfigureAwait(false);
        DetectCache[cacheKey] = (now, info);
        return info;
    }

    private static async Task<RuntimeInfo> DetectCoreAsync(string runtime, string? minVersion, CancellationToken ct)
    {
        string[] exes;
        string args;
        switch (runtime)
        {
            case "node": exes = ["node"]; args = "--version"; break;
            case "npm": exes = ["npm"]; args = "--version"; break;
            case "java": exes = ["java"]; args = "--version"; break;
            case "dotnet": exes = ["dotnet"]; args = "--version"; break;
            case "powershell":
            case "pwsh": exes = ["pwsh"]; args = "--version"; break;
            case "python": exes = OperatingSystem.IsWindows() ? ["python"] : ["python3", "python"]; args = "--version"; break;
            default: exes = [runtime]; args = "--version"; break;
        }
        string? output = null;
        string? error = null;
        var found = false;
        foreach (var exe in exes)
        {
            (found, output, error) = await TryRunAsync(exe, args, ct).ConfigureAwait(false);
            if (found) break;
        }
        if (!found) return new(false, null, output, error ?? DescribeRuntimeMissing(runtime));
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
        var m = System.Text.RegularExpressions.Regex.Match(output, @"v?(\d+\.\d+(?:\.\d+)?(?:[.-]\w+)*)");
        if (m.Success) return m.Groups[1].Value;
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var mm = System.Text.RegularExpressions.Regex.Match(p, @"\d+\.\d+.*");
            if (mm.Success) return mm.Value.TrimStart('v');
        }
        return output.Trim().Split(' ')[0].TrimStart('v');
    }

    private static async Task<(bool found, string? output, string? error)> TryRunAsync(string exe, string args, CancellationToken ct)
    {
        string? resolvedExe;
        try
        {
            resolvedExe = FindExecutable(exe);
        }
        catch (Exception ex)
        {
            return (false, null, $"Could not look up '{exe}': {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(resolvedExe))
            return (false, null, DescribeRuntimeMissing(exe));

        try
        {
            var isCmdScript = resolvedExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolvedExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            ProcessStartInfo psi;
            if (isCmdScript && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                psi = new ProcessStartInfo
                {
                    FileName = comSpec,
                    Arguments = $"/d /s /c \"\"{resolvedExe}\" {args}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = resolvedExe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
            }
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, DescribeRuntimeLaunchFailure(exe, resolvedExe, null));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (proc.ExitCode == 0 || !string.IsNullOrWhiteSpace(combined))
                return (true, combined.Trim(), null);
            return (false, combined, $"Exit code {proc.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return (false, null, ct.IsCancellationRequested
                ? $"Probing {exe} was cancelled."
                : $"Timed out probing {exe}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return (false, null, DescribeRuntimeLaunchFailure(exe, resolvedExe, ex));
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string DescribeRuntimeMissing(string runtime) => runtime switch
    {
        "node" => "Node.js was not found on your PATH. Install Node.js 16 or newer from https://nodejs.org, then restart Kodo.",
        "npm" => "npm was not found on your PATH. npm ships with Node.js - install Node.js from https://nodejs.org, then restart Kodo.",
        "npx" => "npx was not found on your PATH. npx ships with Node.js - install Node.js from https://nodejs.org, then restart Kodo.",
        "python" or "python3" => "Python was not found on your PATH. Install Python 3, then restart Kodo.",
        "java" => "Java was not found on your PATH. Install a JDK, then restart Kodo.",
        "dotnet" => "The .NET SDK was not found on your PATH. Install it from https://dotnet.microsoft.com/download, then restart Kodo.",
        "pwsh" or "powershell" => "PowerShell was not found on your PATH. Install it, then restart Kodo.",
        _ => $"'{runtime}' was not found on your PATH. Install it, then restart Kodo."
    };

    private static string DescribeRuntimeLaunchFailure(string runtime, string resolvedPath, Exception? ex)
    {
        var reason = ex is null ? "the process could not be started" : DescribeStartFailureReason(ex);
        return $"'{resolvedPath}' was found but could not be run ({reason}). " +
               $"Reinstall {runtime} and make sure it is on your PATH, then restart Kodo.";
    }

    private static string DescribeStartFailureReason(Exception ex)
    {
        if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: not 0 } win32)
            return new System.ComponentModel.Win32Exception(win32.NativeErrorCode).Message;

        var message = ex.Message;
        const string noisePrefix = "An error occurred trying to start process";
        if (message.StartsWith(noisePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var lastStop = message.LastIndexOf(". ", StringComparison.Ordinal);
            if (lastStop >= 0 && lastStop + 2 < message.Length)
                message = message[(lastStop + 2)..];
        }

        message = message.Trim();
        if (message.EndsWith('.')) message = message[..^1];
        return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message;
    }

    public static string? FindExecutable(string command)
    {
        var viaPath = FindOnPath(command);
        if (viaPath != null) return viaPath;
        var viaKnown = FindInKnownLocations(command);
        if (viaKnown != null) return viaKnown;
        var viaWhere = FindViaWhere(command);
        if (viaWhere != null) return viaWhere;
        return null;
    }

    private static string? FindInKnownLocations(string command)
    {
        try
        {
            var lower = command.Trim().ToLowerInvariant();
            if (lower != "node" && lower != "npm" && lower != "npx") return null;
            var isWindows = OperatingSystem.IsWindows();
            var target = lower switch
            {
                "node" => isWindows ? "node.exe" : "node",
                "npm" => isWindows ? "npm.cmd" : "npm",
                "npx" => isWindows ? "npx.cmd" : "npx",
                _ => lower
            };
            var candidates = new List<string>();
            if (isWindows)
            {
                var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
                if (!string.IsNullOrWhiteSpace(nvmHome)) { candidates.Add(Path.Combine(nvmHome, "node.exe")); candidates.Add(Path.Combine(nvmHome, "npm.cmd")); }
                var nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
                if (!string.IsNullOrWhiteSpace(nvmSymlink)) { candidates.Add(Path.Combine(nvmSymlink, "node.exe")); candidates.Add(Path.Combine(nvmSymlink, "npm.cmd")); }
                try { var voltaBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta", "bin"); candidates.Add(Path.Combine(voltaBin, "node.exe")); candidates.Add(Path.Combine(voltaBin, "npm.cmd")); } catch { }
                var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fnm");
                if (!string.IsNullOrWhiteSpace(fnmDir) && Directory.Exists(fnmDir))
                    try { var fnmNode = Directory.EnumerateFiles(fnmDir, "node.exe", SearchOption.AllDirectories).FirstOrDefault(); if (fnmNode != null) candidates.Add(fnmNode); } catch { }
                try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")); candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd")); } catch { }
                try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe")); candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "npm.cmd")); } catch { }
                foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
                {
                    try
                    {
                        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                        using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + (lower == "node" ? "node.exe" : "npm.cmd"));
                        var pathVal = appPaths?.GetValue(null) as string ?? appPaths?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(pathVal) && File.Exists(pathVal)) candidates.Add(pathVal);
                    }
                    catch { }
                    try
                    {
                        using var baseKey32 = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry32);
                        using var appPaths32 = baseKey32.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + (lower == "node" ? "node.exe" : "npm.cmd"));
                        var pathVal32 = appPaths32?.GetValue(null) as string ?? appPaths32?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(pathVal32) && File.Exists(pathVal32)) candidates.Add(pathVal32);
                    }
                    catch { }
                }
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var nvmDir = Environment.GetEnvironmentVariable("NVM_DIR");
                if (string.IsNullOrWhiteSpace(nvmDir)) nvmDir = Path.Combine(home, ".nvm");
                if (!string.IsNullOrWhiteSpace(nvmDir) && Directory.Exists(nvmDir))
                {
                    try
                    {
                        var nvmNode = Directory.EnumerateFiles(nvmDir, "node", SearchOption.AllDirectories)
                            .FirstOrDefault(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}node", StringComparison.Ordinal));
                        if (nvmNode != null) candidates.Add(nvmNode);
                    }
                    catch { }
                }
                var voltaHome = Environment.GetEnvironmentVariable("VOLTA_HOME");
                var voltaBinCandidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(voltaHome)) voltaBinCandidates.Add(Path.Combine(voltaHome, "bin"));
                voltaBinCandidates.Add(Path.Combine(home, ".volta", "bin"));
                try { voltaBinCandidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta", "bin")); } catch { }
                foreach (var vb in voltaBinCandidates.Distinct(StringComparer.Ordinal))
                    candidates.Add(Path.Combine(vb, target));
                var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR");
                if (string.IsNullOrWhiteSpace(fnmDir))
                    fnmDir = Path.Combine(home, ".local", "share", "fnm");
                if (!string.IsNullOrWhiteSpace(fnmDir) && Directory.Exists(fnmDir))
                {
                    try { var fnmNode = Directory.EnumerateFiles(fnmDir, "node", SearchOption.AllDirectories).FirstOrDefault(); if (fnmNode != null) candidates.Add(fnmNode); } catch { }
                    try { var fnmMultis = Directory.GetDirectories(fnmDir, "*", SearchOption.TopDirectoryOnly); foreach (var d in fnmMultis) candidates.Add(Path.Combine(d, "installation", "bin", target)); } catch { }
                }
                foreach (var p in new[] { "/usr/local/bin", "/usr/bin", "/opt/nodejs/bin", "/snap/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".fnm", "current", "bin") })
                    candidates.Add(Path.Combine(p, target));
            }
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var file = Path.GetFileName(c);
                if (!file.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsExecutableFile(c)) return Path.GetFullPath(c);
            }
            return null;
        }
        catch { return null; }
    }

    private static string? FindViaWhere(string command)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var bin = isWindows
                ? (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"))
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe") : "where")
                : "which";
            var psi = new ProcessStartInfo { FileName = bin, Arguments = command, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            if (proc.ExitCode != 0) return null;
            var first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return Path.GetFullPath(first);
            return null;
        }
        catch { return null; }
    }

    public static string? FindOnPath(string command)
    {
        try
        {
            var fileName = command.Trim().Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return Path.GetFullPath(fileName);
            var isWindows = OperatingSystem.IsWindows();
            var rawPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] pathexts = [];
            if (isWindows)
            {
                var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
                pathexts = pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            foreach (var rawDir in rawPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(rawDir)) continue;
                var expanded = Environment.ExpandEnvironmentVariables(rawDir.Trim().Trim('"').Trim());
                if (string.IsNullOrWhiteSpace(expanded)) continue;
                string dir;
                try { dir = Path.GetFullPath(expanded); } catch { dir = expanded; }
                if (!Directory.Exists(dir)) continue;
                var candidate = Path.Combine(dir, fileName);
                if (isWindows && !Path.HasExtension(fileName))
                {
                    foreach (var ext in pathexts)
                    {
                        var withExt = candidate + (ext.StartsWith(".") ? ext : "." + ext);
                        if (IsExecutableFile(withExt)) return Path.GetFullPath(withExt);
                    }
                    continue;
                }
                if (IsExecutableFile(candidate)) return Path.GetFullPath(candidate);
                if (!isWindows) continue;
            }
            if (!isWindows && (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                var stripped = fileName[..^4];
                foreach (var rawDir in rawPath.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(rawDir)) continue;
                    var expanded = Environment.ExpandEnvironmentVariables(rawDir.Trim().Trim('"').Trim());
                    if (string.IsNullOrWhiteSpace(expanded)) continue;
                    string dir;
                    try { dir = Path.GetFullPath(expanded); } catch { dir = expanded; }
                    if (!Directory.Exists(dir)) continue;
                    var candidate = Path.Combine(dir, stripped);
                    if (IsExecutableFile(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return null;
        }
        catch { return null; }
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;

            if (Path.HasExtension(path) && WindowsOnlyExtensions.Contains(Path.GetExtension(path)))
                return false;

            var mode = new FileInfo(path).UnixFileMode;
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & executeBits) == 0) return false;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Span<byte> magic = stackalloc byte[2];
                if (fs.Read(magic) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z')
                    return false;
            }

            return true;
        }
        catch { return false; }
    }

    private static readonly HashSet<string> WindowsOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".cmd", ".bat", ".ps1", ".msi"
    };
}

internal static class LspServerResolver
{
    public static Task<LspResolution> ResolveAsync(
        LspConfiguration cfg,
        AppSettings? settings = null,
        string extensionId = "",
        CancellationToken ct = default)
        => ResolveInternalAsync(cfg, extensionId, settings, ct);

    public static async Task<LspResolution> ResolveAsync(
        LoadedExtension extension,
        AppSettings? settings = null,
        CancellationToken ct = default)
    {
        var cfg = extension.Lsp;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Command))
        {
            if (extension.Lsps.Count > 0) cfg = extension.Lsps[0];
        }
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Command))
            return new(LspServerSource.Missing, null, cfg ?? new LspConfiguration(), null, "No LSP configured", false);
        return await ResolveInternalAsync(cfg, extension.Id, settings, ct).ConfigureAwait(false);
    }

    private static async Task<LspResolution> ResolveInternalAsync(
        LspConfiguration cfg,
        string extensionId,
        AppSettings? settings,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(extensionId) && settings != null && settings.LspDisabledLanguages.TryGetValue(extensionId, out var disabled) && disabled)
            return new(LspServerSource.Disabled, null, cfg, null, "LSP disabled for this language", false);
        if (settings != null && !settings.LspEnabled)
            return new(LspServerSource.Disabled, null, cfg, null, "LSP globally disabled", false);

        if (!string.IsNullOrWhiteSpace(cfg.Runtime))
        {
            var rt = await LspRuntimeDetector.DetectAsync(cfg.Runtime!, cfg.RuntimeMinVersion, ct).ConfigureAwait(false);
            if (!rt.Found || !string.IsNullOrWhiteSpace(rt.Error))
            {
                return new(LspServerSource.RuntimeMissing, null, cfg, null, rt.Error, false);
            }
        }

        string? overrideKey = null;
        string? ov = null;
        if (settings != null)
        {
            if (!string.IsNullOrWhiteSpace(extensionId) && settings.LspExecutableOverrides.TryGetValue(extensionId, out var ov1) && !string.IsNullOrWhiteSpace(ov1))
            { ov = ov1; overrideKey = extensionId; }
            else if (settings.LspExecutableOverrides.TryGetValue(cfg.EffectiveProviderId, out var ov2) && !string.IsNullOrWhiteSpace(ov2))
            { ov = ov2; overrideKey = cfg.EffectiveProviderId; }
        }
        if (!string.IsNullOrWhiteSpace(ov))
        {
            var trimmed = ov!.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                var resolved = CloneWithCommand(cfg, trimmed);
                var ver = await ProbeVersionAsync(trimmed, cfg.VersionArgs, ct).ConfigureAwait(false);
                if (!IsVersionCompatible(ver, cfg.Version))
                    return new(LspServerSource.Incompatible, trimmed, resolved, ver, $"Language server '{cfg.EffectiveProviderId}' version {ver ?? "unknown"} is incompatible with required {cfg.Version}.", cfg.AllowAutoInstall);
                return new(LspServerSource.UserOverride, trimmed, resolved, ver, null, false);
            }
            var found = LspRuntimeDetector.FindOnPath(trimmed);
            if (found != null)
            {
                var resolved = CloneWithCommand(cfg, found);
                var ver = await ProbeVersionAsync(found, cfg.VersionArgs, ct).ConfigureAwait(false);
                if (!IsVersionCompatible(ver, cfg.Version))
                    return new(LspServerSource.Incompatible, found, resolved, ver, $"Language server '{cfg.EffectiveProviderId}' version {ver ?? "unknown"} is incompatible with required {cfg.Version}.", cfg.AllowAutoInstall);
                return new(LspServerSource.UserOverride, found, resolved, ver, null, false);
            }
            KodoDiagnostics.LogDebug($"LSP user override for {overrideKey} not found: {trimmed}");
        }

        bool preferManaged = settings?.LspPreferManaged ?? true;
        bool preferSystem = settings?.LspPreferSystem ?? true;
        var managedExe = LspInstallationManager.FindManagedExecutable(cfg, settings);
        bool managedExists = managedExe != null && File.Exists(managedExe);
        string? systemExe = null;
        if (cfg.AllowSystem && preferSystem)
            systemExe = LspInstallationManager.FindSystemExecutable(cfg);
        if (systemExe == null && cfg.AllowSystem && preferSystem && cfg.Command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var without = cfg.Command[..^4];
            systemExe = LspRuntimeDetector.FindOnPath(without);
        }
        bool systemExists = systemExe != null && File.Exists(systemExe);

        async Task<LspResolution?> TryResolveManagedOrSystemAsync(string exe, LspServerSource source)
        {
            var resolved = CloneWithCommand(cfg, exe);
            var ver = await ProbeVersionViaLaunchPlanAsync(cfg, settings, exe, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.Version) && !string.IsNullOrWhiteSpace(ver) && !IsVersionCompatible(ver, cfg.Version))
            {
                KodoDiagnostics.LogDebug($"LSP {source} {cfg.EffectiveProviderId} version {ver} incompatible with required {cfg.Version}");
                return new(LspServerSource.Incompatible, exe, resolved, ver, $"Language server '{cfg.EffectiveProviderId}' version {ver} is incompatible with required {cfg.Version}.", cfg.AllowAutoInstall);
            }
            if (!string.IsNullOrWhiteSpace(cfg.Version) && string.IsNullOrWhiteSpace(ver))
                KodoDiagnostics.LogDebug($"LSP {source} {cfg.EffectiveProviderId} version unknown; treating as compatible with required {cfg.Version}");
            return new(source, exe, resolved, ver, null, false);
        }

        if (preferManaged && preferSystem)
        {
            if (managedExists)
            {
                var r = await TryResolveManagedOrSystemAsync(managedExe!, LspServerSource.Managed).ConfigureAwait(false);
                return r!;
            }
            if (systemExists)
            {
                var r = await TryResolveManagedOrSystemAsync(systemExe!, LspServerSource.System).ConfigureAwait(false);
                return r!;
            }
        }
        else if (preferManaged && !preferSystem)
        {
            if (managedExists)
            {
                var r = await TryResolveManagedOrSystemAsync(managedExe!, LspServerSource.Managed).ConfigureAwait(false);
                return r!;
            }
        }
        else if (!preferManaged && preferSystem)
        {
            if (systemExists)
            {
                var r = await TryResolveManagedOrSystemAsync(systemExe!, LspServerSource.System).ConfigureAwait(false);
                return r!;
            }
            if (managedExists)
            {
                var r = await TryResolveManagedOrSystemAsync(managedExe!, LspServerSource.Managed).ConfigureAwait(false);
                return r!;
            }
        }

        bool isGithub = (cfg.InstallMethod?.Equals("github", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cfg.DownloadUrl?.Contains("github.com", StringComparison.OrdinalIgnoreCase) ?? false);
        bool hasSha = !string.IsNullOrWhiteSpace(cfg.Sha256);
        bool canInstall = cfg.AllowAutoInstall && !string.Equals(cfg.InstallMethod, "manual", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(cfg.DownloadUrl) || !string.IsNullOrWhiteSpace(cfg.PackageName) || cfg.InstallMethod == "npm" || cfg.InstallMethod == "github" || cfg.InstallMethod == "dotnet");
        if (canInstall && isGithub && !hasSha)
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' download requires SHA-256 verification (no checksum provided). Please install manually from {cfg.DownloadUrl} or update provider metadata.", false);
        }
        if (canInstall && !isGithub && !hasSha && !string.IsNullOrWhiteSpace(cfg.DownloadUrl) && cfg.InstallMethod != "npm" && cfg.InstallMethod != "dotnet")
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' cannot be auto-installed without SHA-256. Install manually.", false);
        }
        if (canInstall)
            return new(LspServerSource.Installable, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' not installed. Can be installed on demand.", true);
        if (cfg.InstallMethod != null && cfg.InstallMethod.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' requires manual installation. Ensure '{cfg.Command}' is on PATH.", false);
        return new(LspServerSource.Missing, null, cfg, null, $"Language server '{cfg.Command}' not found on PATH and no install source configured.", false);
    }

    private static LspConfiguration CloneWithCommand(LspConfiguration cfg, string newCommand) => new()
    {
        Command = newCommand,
        Arguments = cfg.Arguments,
        Languages = cfg.Languages,
        FileExtensions = cfg.FileExtensions,
        Env = new Dictionary<string, string>(cfg.Env, StringComparer.OrdinalIgnoreCase),
        WorkingDirectory = cfg.WorkingDirectory,
        InitializationOptions = cfg.InitializationOptions,
        RootMarkers = cfg.RootMarkers,
        ProviderId = cfg.ProviderId,
        DisplayName = cfg.DisplayName,
        Version = cfg.Version,
        InstallMethod = cfg.InstallMethod,
        PackageName = cfg.PackageName,
        DownloadUrl = cfg.DownloadUrl,
        Sha256 = cfg.Sha256,
        Runtime = cfg.Runtime,
        RuntimeMinVersion = cfg.RuntimeMinVersion,
        RuntimeArgs = cfg.RuntimeArgs,
        MainClass = cfg.MainClass,
        AllowAutoInstall = cfg.AllowAutoInstall,
        AllowSystem = cfg.AllowSystem,
        VersionArgs = cfg.VersionArgs
    };

    private static async Task<string?> ProbeVersionAsync(string exe, string[] versionArgs, CancellationToken ct)
    {
        try
        {
            var (ok, ver, err) = await LspInstallationManager.TryGetVersionAsync(exe, versionArgs, [], ct).ConfigureAwait(false);
            if (ok && !string.IsNullOrWhiteSpace(ver))
            {
                var first = ver.Split('\n')[0].Trim();
                return first.Length > 120 ? first[..120] : first;
            }
            return null;
        }
        catch { return null; }
    }

    private static async Task<string?> ProbeVersionViaLaunchPlanAsync(
        LspConfiguration cfg, AppSettings? settings, string exe, CancellationToken ct)
    {
        if (!LspInstallationManager.UsesRuntimeLauncher(cfg)) return await ProbeVersionAsync(exe, cfg.VersionArgs, ct).ConfigureAwait(false);

        var plan = LspInstallationManager.ResolveLaunchPlan(cfg, settings, exe);
        try
        {
            var (ok, ver, _) = await LspInstallationManager.TryGetVersionAsync(plan.FileName, cfg.VersionArgs, plan.PrefixArgs, ct).ConfigureAwait(false);
            if (ok && !string.IsNullOrWhiteSpace(ver))
            {
                var first = ver.Split('\n')[0].Trim();
                return first.Length > 120 ? first[..120] : first;
            }
            return null;
        }
        catch { return null; }
    }

    public static string GetStatusText(LspResolution r)
    {
        return r.Source switch
        {
            LspServerSource.Managed => $"Managed ({r.Version ?? "installed"})",
            LspServerSource.System => $"System ({r.Version ?? "found"})",
            LspServerSource.UserOverride => $"Custom ({r.Version ?? "override"})",
            LspServerSource.Installable => "Not installed – can install",
            LspServerSource.Incompatible => $"Incompatible ({r.Version ?? "unknown"} vs required {r.ResolvedConfiguration.Version})",
            LspServerSource.RuntimeMissing => $"Runtime missing: {r.Error}",
            LspServerSource.ManualRequired => "Manual install required",
            LspServerSource.Disabled => "Disabled",
            _ => "Not installed"
        };
    }

    internal static bool IsVersionCompatible(string? installedRaw, string? required)
    {
        if (string.IsNullOrWhiteSpace(required)) return true;
        if (string.IsNullOrWhiteSpace(installedRaw)) return true;
        try
        {
            string Extract(string s)
            {
                var m = System.Text.RegularExpressions.Regex.Match(s, @"v?(\d+\.\d+(?:\.\d+)?)");
                if (m.Success) return m.Groups[1].Value;
                return s.Trim().TrimStart('v','V').Split(' ')[0];
            }
            var inst = Extract(installedRaw);
            var req = Extract(required);
            var f = ParseVersionNumbers(inst);
            var r = ParseVersionNumbers(req);
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

    private static int[] ParseVersionNumbers(string v)
    {
        var list = new List<int>();
        var cur = "";
        foreach (var c in v)
        {
            if (char.IsDigit(c)) cur += c;
            else if (c == '.' && cur.Length > 0) { if (int.TryParse(cur, out var n)) list.Add(n); cur = ""; }
            else if (cur.Length > 0) break;
        }
        if (cur.Length > 0 && int.TryParse(cur, out var last)) list.Add(last);
        if (list.Count == 0) return new[] { 0 };
        return list.ToArray();
    }
}
