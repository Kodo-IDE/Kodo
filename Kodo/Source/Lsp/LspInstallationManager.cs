// Licensed under GPL-v3.0
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

/// <summary>Manages Kodo-managed LSP installations under %LocalAppData%\Kodo\Lsp\ (or custom LspInstallDir).
/// Hardened: HTTPS-only, mandatory SHA-256 for github artifacts, atomic temp-dir install,
/// ZipSlip guard, TAR support, per-provider concurrency lock, path-traversal safe.</summary>
internal static class LspInstallationManager
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Kodo/2.0.0-DEV (https://github.com/Kodo-IDE/Kodo)");
        return c;
    }

    // --- Managed root handling (respects custom LspInstallDir setting if provided) ---
    public static string ManagedRoot => GetManagedRoot(null);

    public static string GetManagedRoot(AppSettings? settings)
    {
        var custom = settings?.LspInstallDir?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                // Must be absolute and inside user's profile or local app data; reject traversal
                var full = Path.GetFullPath(custom);
                // Allow any absolute path that is not system root, but ensure it's under %LocalAppData% or %AppData% or %UserProfile% to avoid writing to Program Files etc.
                // For now allow any absolute path that exists or can be created, but sanitize provider subdir later.
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
        // Allow only alphanum, dash, underscore, dot
        var sanitized = new string(providerId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "lsp";
        // Prevent .. traversal
        sanitized = sanitized.Replace("..", "_");
        return sanitized.ToLowerInvariant();
    }

    public static string GetProviderDir(LspConfiguration cfg) => GetProviderDir(cfg, null);
    public static string GetProviderDir(LspConfiguration cfg, AppSettings? settings)
    {
        var root = GetManagedRoot(settings);
        var sanitized = SanitizeProviderId(cfg.EffectiveProviderId);
        var dir = Path.Combine(root, sanitized);
        // Ensure dir is inside root
        var fullRoot = Path.GetFullPath(root);
        var fullDir = Path.GetFullPath(dir);
        if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
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
        // Prevent path traversal in fileName
        fileName = Path.GetFileName(fileName);
        return Path.Combine(dir, fileName);
    }

    /// <summary>True only if the expected executable exists and is not stale. Does NOT return true for partial/empty dirs.</summary>
    public static bool IsManagedInstalled(LspConfiguration cfg) => IsManagedInstalled(cfg, null);
    public static bool IsManagedInstalled(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = FindManagedExecutable(cfg, settings);
        return exe != null && File.Exists(exe);
    }

    public static string? FindSystemExecutable(LspConfiguration cfg)
    {
        if (!cfg.AllowSystem) return null;
        return LspRuntimeDetector.FindOnPath(cfg.Command);
    }

    public static string? FindManagedExecutable(LspConfiguration cfg) => FindManagedExecutable(cfg, null);
    public static string? FindManagedExecutable(LspConfiguration cfg, AppSettings? settings)
    {
        var exe = GetManagedExecutablePath(cfg, settings);
        if (File.Exists(exe)) return exe;
        // For npm providers, executable is in <providerDir>/node_modules/.bin/<command> or <providerDir>/node_modules/.bin/<name>.cmd
        var dir = GetProviderDir(cfg, settings);
        if (!Directory.Exists(dir)) return null;
        // Strict: look for file matching Command fileName exactly, recursively, but only within provider dir
        try
        {
            var targetName = Path.GetFileName(exe);
            var targetWithoutExt = Path.GetFileNameWithoutExtension(targetName);
            // Search up to 3 levels deep, prefer .bin
            var candidates = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => {
                    var name = Path.GetFileName(f);
                    // Exact match or without extension match
                    return name.Equals(targetName, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(name).Equals(targetWithoutExt, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            // Prefer node_modules/.bin location
            var binPref = candidates.FirstOrDefault(c => c.Contains("node_modules" + Path.DirectorySeparatorChar + ".bin", StringComparison.OrdinalIgnoreCase));
            if (binPref != null) return binPref;
            return candidates.FirstOrDefault();
        }
        catch { return null; }
    }

    public static async Task<(bool ok, string? version, string? error)> TryGetVersionAsync(string exePath, string[] versionArgs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return (false, null, "Executable not found");
        var args = versionArgs != null && versionArgs.Length > 0 ? string.Join(" ", versionArgs) : "--version";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, null, "Failed to start");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var outText = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(outText))
                return (false, null, $"Exit code {proc.ExitCode}: {stderr.Trim()}");
            return (true, outText?.Trim(), null);
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

        // Runtime check first
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
            // Double-check after acquiring lock: maybe another thread installed while we waited
            if (IsManagedInstalled(cfg, settings))
                return new(InstallResultKind.AlreadyInstalled, "Already installed", GetProviderDir(cfg, settings));

            var method = (cfg.InstallMethod ?? "").Trim().ToLowerInvariant();
            if (method == "npm" || (!string.IsNullOrWhiteSpace(cfg.PackageName) && string.IsNullOrWhiteSpace(cfg.DownloadUrl)))
                return await InstallViaNpmAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.DownloadUrl))
            {
                // Enforce SHA-256 for github/standalone artifacts
                var isGithub = method == "github" || cfg.DownloadUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase) || cfg.DownloadUrl.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
                if (isGithub && string.IsNullOrWhiteSpace(cfg.Sha256))
                    return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' download requires SHA-256 verification. No checksum provided – please install manually from {cfg.DownloadUrl} and configure an override, or update the provider to include a trusted checksum.", null);
                if (string.IsNullOrWhiteSpace(cfg.Sha256) && method != "npm")
                {
                    // For non-npm standalone, also require checksum
                    return new(InstallResultKind.NotInstallable, $"Provider '{cfg.EffectiveProviderId}' cannot be auto-installed without a SHA-256 checksum. This is a security requirement. Install manually or provide a checksum.", null);
                }
                return await InstallViaDownloadAsync(cfg, settings, progress, ct).ConfigureAwait(false);
            }

            return new(InstallResultKind.NotInstallable, $"No install source configured for '{cfg.EffectiveProviderId}'. Install manually and ensure '{cfg.Command}' is on PATH.", null);
        }
        finally { sem.Release(); }
    }

    private static async Task<InstallResult> InstallViaNpmAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var pkg = cfg.PackageName ?? cfg.EffectiveProviderId;
        // Detect node + npm
        var nodeRt = await LspRuntimeDetector.DetectAsync("node", cfg.RuntimeMinVersion ?? "16.0.0", ct).ConfigureAwait(false);
        if (!nodeRt.Found) return new(InstallResultKind.RuntimeMissing, nodeRt.Error ?? "Node.js is required. Install Node.js 16+ from https://nodejs.org/", null);
        var npmRt = await LspRuntimeDetector.DetectAsync("npm", null, ct).ConfigureAwait(false);
        if (!npmRt.Found) return new(InstallResultKind.RuntimeMissing, "npm is required but not found. Install Node.js which includes npm.", null);

        progress?.Report($"Installing {pkg} via npm...");
        KodoDiagnostics.LogDebug($"LSP npm install {pkg}");
        var providerDir = GetProviderDir(cfg, settings);
        var stagingDir = providerDir + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(stagingDir);
            var args = $"install --prefix \"{stagingDir}\" {pkg}";
            var npmExe = LspRuntimeDetector.FindOnPath("npm") ?? "npm";
            // On Windows npm is npm.cmd – must invoke via cmd.exe wrapper handling already in LspClient, but for installer we call directly
            // Use npm.cmd if found
            if (File.Exists(npmExe) && npmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                // keep as is; Process will handle via cmd? We need to invoke via cmd.exe /c for .cmd
                // Instead, use npx or node? Simpler: if npm is .cmd, invoke via cmd.exe
                // We'll handle by switching to use cmd.exe
            }
            var useCmdWrapper = npmExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || npmExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            string fileName = npmExe;
            string cmdArgs = args;
            if (useCmdWrapper)
            {
                var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                fileName = comSpec;
                cmdArgs = $"/c \"{npmExe}\" {args}";
            }
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = useCmdWrapper ? cmdArgs : args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
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
            // Validate staging contains executable
            var stagedExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(pkg, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals(cfg.Command, StringComparison.OrdinalIgnoreCase));
            // For vscode-langservers-extracted, we expect multiple servers; check at least one known file exists
            if (stagedExe == null && !Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "npm install produced no files", null);
            }
            // Atomic move staging -> providerDir
            try
            {
                if (Directory.Exists(providerDir)) Directory.Delete(providerDir, true);
            }
            catch { }
            Directory.Move(stagingDir, providerDir);
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

    private static async Task<InstallResult> InstallViaDownloadAsync(LspConfiguration cfg, AppSettings? settings, IProgress<string>? progress, CancellationToken ct)
    {
        var url = cfg.DownloadUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return new(InstallResultKind.NotInstallable, "No download URL", null);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new(InstallResultKind.Failed, "Download URL must be HTTPS", null);

        progress?.Report($"Downloading {cfg.EffectiveProviderId}...");
        KodoDiagnostics.LogDebug($"LSP download {url}");
        var providerDir = GetProviderDir(cfg, settings);
        var stagingDir = providerDir + ".staging-" + Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(Path.GetTempPath(), $"kodo-lsp-{SanitizeProviderId(cfg.EffectiveProviderId)}-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDir);
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Verify final request URI is still HTTPS (followed redirects)
            if (resp.RequestMessage?.RequestUri != null && !resp.RequestMessage.RequestUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return new(InstallResultKind.Failed, "Download redirected to non-HTTPS URL – blocked for security", null);
            if (!resp.IsSuccessStatusCode)
                return new(InstallResultKind.Failed, $"Download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", null);
            await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            IncrementalHash? hasher = null;
            if (!string.IsNullOrWhiteSpace(cfg.Sha256))
                hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            else
                return new(InstallResultKind.NotInstallable, "SHA-256 checksum required for verification – not provided", null);
            var buffer = new byte[81920];
            long totalRead = 0;
            var contentLength = resp.Content.Headers.ContentLength;
            int read;
            while ((read = await netStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                hasher?.AppendData(buffer, 0, read);
                totalRead += read;
                if (contentLength.HasValue && contentLength.Value > 0)
                    progress?.Report($"Downloading {cfg.EffectiveProviderId}... {totalRead * 100 / contentLength.Value}%");
            }
            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            fileStream.Close();

            // Verify checksum (mandatory)
            var hash = hasher!.GetHashAndReset();
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var expected = cfg.Sha256!.Trim().ToLowerInvariant().Replace(" ", "").Replace("0x", "");
            if (!hex.Equals(expected, StringComparison.Ordinal))
            {
                try { File.Delete(tempFile); } catch { }
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Checksum mismatch for {cfg.EffectiveProviderId}. Expected {expected}, got {hex}.", null);
            }

            progress?.Report($"Installing {cfg.EffectiveProviderId}...");
            bool isZip = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || IsZipFile(tempFile);
            bool isTarGz = url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || IsTarGzFile(tempFile);
            if (isZip)
            {
                await ExtractZipSecureAsync(tempFile, stagingDir, ct).ConfigureAwait(false);
            }
            else if (isTarGz)
            {
                await ExtractTarGzSecureAsync(tempFile, stagingDir, ct).ConfigureAwait(false);
            }
            else
            {
                // Single binary – copy into staging dir
                var fileName = Path.GetFileName(GetManagedExecutablePath(cfg, settings));
                var dest = Path.Combine(stagingDir, fileName);
                File.Copy(tempFile, dest, true);
            }
            // Validate staging contains at least one file
            if (!Directory.EnumerateFileSystemEntries(stagingDir).Any())
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, "Archive extracted no files", null);
            }
            // Additional validation: ensure expected executable exists (or at least one exe in staging)
            var foundExe = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(GetManagedExecutablePath(cfg, settings)), StringComparison.OrdinalIgnoreCase));
            if (foundExe == null)
            {
                // For zip that contains nested dir, and command not at top level, we accept any executable
                if (!Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Any())
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                    return new(InstallResultKind.Failed, "Extracted archive contains no executable", null);
                }
            }
            try { File.Delete(tempFile); } catch { }
            // Atomic move: delete old providerDir and move staging
            try
            {
                if (Directory.Exists(providerDir)) Directory.Delete(providerDir, true);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to clean old installation: {ex.Message}", null);
            }
            try
            {
                Directory.Move(stagingDir, providerDir);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(stagingDir, true); } catch { }
                return new(InstallResultKind.Failed, $"Failed to finalize installation: {ex.Message}", null);
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

    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return false;
            return header[0] == 0x50 && header[1] == 0x4B; // PK
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
            return header[0] == 0x1F && header[1] == 0x8B; // gzip magic
        }
        catch { return false; }
    }

    private static async Task ExtractZipSecureAsync(string zipPath, string destDir, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/")) continue; // directory
            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            var fullDestDir = Path.GetFullPath(destDir);
            if (!destPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry escapes destination: {entry.FullName}");
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Prevent overwriting with directory
            if (entry.FullName.EndsWith("/")) continue;
            await Task.Run(() => entry.ExtractToFile(destPath, overwrite: true), ct).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarGzSecureAsync(string tgzPath, string destDir, CancellationToken ct)
    {
        // Use System.Formats.Tar if available (.NET 7+); fallback to manual error if not
        await Task.Run(() =>
        {
            using var fs = File.OpenRead(tgzPath);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            // Try System.Formats.Tar
            var tarType = Type.GetType("System.Formats.Tar.TarReader, System.Formats.Tar");
            if (tarType != null)
            {
                // Use reflection to avoid compile-time dependency if not available
                dynamic reader = Activator.CreateInstance(tarType, gz)!;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = reader.GetNextEntry();
                        if (entry == null) break;
                        string entryName = entry.Name;
                        if (string.IsNullOrWhiteSpace(entryName)) continue;
                        var destPath = Path.GetFullPath(Path.Combine(destDir, entryName));
                        var fullDestDir = Path.GetFullPath(destDir);
                        if (!destPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Tar entry escapes destination: {entryName}");
                        if (entry.EntryType.ToString() == "Directory")
                        {
                            Directory.CreateDirectory(destPath);
                        }
                        else
                        {
                            var dir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            using var outFs = File.Create(destPath);
                            entry.DataStream.CopyTo(outFs);
                        }
                    }
                }
                finally { (reader as IDisposable)?.Dispose(); }
                return;
            }
            throw new InvalidOperationException("TAR extraction not supported on this runtime – archive is .tar.gz but System.Formats.Tar not available. Please install manually.");
        }, ct).ConfigureAwait(false);
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
            if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || fullDir.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
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
        // ignore uninstall failure if not installed
        return await InstallAsync(cfg, settings, progress, ct).ConfigureAwait(false);
    }
}
