// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Microsoft.Win32;
using Kodo.Models;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kodo;

public sealed class InstalledCompilerRecord
{
    public string Version { get; set; } = string.Empty;
    public DateTime InstalledOnUtc { get; set; }
    public string InstalledExePath { get; set; } = string.Empty;
}

public sealed class ManualCompilerRecord
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime AddedOnUtc { get; set; }
    public bool AutoDetected { get; set; }

    public string? CanonicalCompilerId { get; set; }
}

public sealed class ManualCompilerRegistryFile
{
    public Dictionary<string, ManualCompilerRecord> Entries { get; set; } = new();
    public List<string> DismissedAutoDetectIds { get; set; } = new();
}

public sealed record CompilerResolverSpec(string Kind, Dictionary<string, string> Params);

public sealed record CompilerResolution(string Version, string DownloadUrl, string FileName);

public partial class MainWindow
{
    private async Task LoadCompilerExtensionsAsync(bool forceResolve = false)
    {
        var effectiveForceResolve = forceResolve || !_hasResolvedCompilersThisSession;
        _hasResolvedCompilersThisSession = true;

        var compilerEntries = new List<CompilerIndexEntry>();
        var loadErrors = new List<string>();

        await Task.Run(() =>
        {
            LoadInstalledCompilerRegistry();
            LoadManualCompilerRegistry();
        }).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(RefreshManualCompilerExtensions, DispatcherPriority.Background);
        if (!_hasRunCompilerAutoDetect)
        {
            _hasRunCompilerAutoDetect = true;
            _ = AutoDetectDefaultCompilersAsync();
        }

        _compilerIndexETag ??= TryReadCacheFile(CompilerIndexETagPath)?.Trim();

        try
        {
            foreach (var indexUrl in CompilerIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                if (indexRequest.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                    indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (_compilerIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _compilerIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Compiler index fetch",
                    async ct =>
                    {
                        using var indexResponse = await MarketplaceHttpClient.SendAsync(indexRequest, ct);
                        if ((int)indexResponse.StatusCode == 304)
                            return (304, (string?)null, (string?)null);
                        if (!indexResponse.IsSuccessStatusCode)
                        {
                            if (indexResponse.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                indexResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                                throw new HttpRequestException($"GitHub API rate limit hit ({(int)indexResponse.StatusCode})", null, indexResponse.StatusCode);
                            return ((int)indexResponse.StatusCode, (string?)null, (string?)null);
                        }
                        var body = await indexResponse.Content.ReadAsStringAsync(ct);
                        var etag = indexResponse.Headers.ETag?.Tag;
                        return (200, body, etag);
                    });

                if (statusCode == 304)
                {
                    var cachedJson = await Task.Run(() => TryReadCacheFile(CompilerIndexCachePath)).ConfigureAwait(false);
                    if (cachedJson is not null)
                    {
                        var cachedParsed = await Task.Run(() => ParseCompilerIndexEntries(cachedJson, new List<string>())).ConfigureAwait(false);
                        if (cachedParsed.Count > 0)
                        {
                            compilerEntries = cachedParsed;
                            loadErrors.Clear();
                        }
                    }
                    KodoDiagnostics.LogDebug("Compiler index: 304 Not Modified - reusing cached data.");
                    break;
                }

                if (remoteJson is null)
                    continue;

                var parsedErrors = new List<string>();
                var parsed = ParseCompilerIndexEntries(remoteJson, parsedErrors);

                if (parsed.Count == 0)
                {
                    loadErrors.Add($"Compiler index at {indexUrl} did not contain any compilers.");
                    continue;
                }

                compilerEntries = parsed;
                loadErrors.Clear();
                loadErrors.AddRange(parsedErrors);
                TryWriteCacheFile(CompilerIndexCachePath, remoteJson);
                if (newETag is not null)
                {
                    _compilerIndexETag = newETag;
                    TryWriteCacheFile(CompilerIndexETagPath, newETag);
                }
                break;
            }
        }
        catch (Exception ex)
        {
            if (IsGitHubRateLimitException(ex))
            {
                var diskJson = await Task.Run(() => TryReadCacheFile(CompilerIndexCachePath)).ConfigureAwait(false);
                if (diskJson is not null)
                {
                    var fallback = await Task.Run(() => ParseCompilerIndexEntries(diskJson, new List<string>())).ConfigureAwait(false);
                    if (fallback.Count > 0)
                    {
                        compilerEntries = fallback;
                        loadErrors.Clear();
                        loadErrors.Add($"Compiler index fetch rate-limited; using cached copy: {DescribeFetchFailure(ex)}");
                        KodoDiagnostics.LogDebug("Compiler index fetch rate-limited; using disk cache.", ex);
                    }
                    else
                    {
                        loadErrors.Add($"Compiler index fetch rate-limited and cached copy was empty: {DescribeFetchFailure(ex)}");
                    }
                }
                else
                {
                    loadErrors.Add($"Compiler index fetch rate-limited and no cached copy available: {DescribeFetchFailure(ex)}");
                }
            }
            else if (IsOfflineException(ex))
            {
                var diskJson = await Task.Run(() => TryReadCacheFile(CompilerIndexCachePath)).ConfigureAwait(false);
                if (diskJson is not null)
                {
                    var fallback = await Task.Run(() => ParseCompilerIndexEntries(diskJson, new List<string>())).ConfigureAwait(false);
                    if (fallback.Count > 0)
                    {
                        compilerEntries = fallback;
                        loadErrors.Clear();
                        loadErrors.Add($"Compiler index offline; using cached copy: {DescribeFetchFailure(ex)}");
                        KodoDiagnostics.LogDebug("Compiler index fetch offline; using disk cache.", ex);
                    }
                    else
                    {
                        loadErrors.Add($"Compiler index offline and cached copy was empty: {DescribeFetchFailure(ex)}");
                    }
                }
                else
                {
                    loadErrors.Add($"Compiler index offline and no cached copy available: {DescribeFetchFailure(ex)}");
                }
            }
            else
            {
                loadErrors.Add($"Compiler index fetch failed: {DescribeFetchFailure(ex)}");
                KodoDiagnostics.LogDebug("Compiler index fetch failed (not rate-limited, cache not used).", ex);
            }
        }

        _compilerIndexEntries = compilerEntries;
        var resolutionCache = LoadCompilerResolutionCache();
        var compilerExtensions = BuildCompilerExtensionsFromCacheOrFallback(compilerEntries, resolutionCache);

        Dictionary<string, string> compilerIconMap = [];
        await InvokeExtensionUiAsync(() =>
        {
            var combinedCompilerEntries = CompilerExtensions
                .Where(entry => !entry.IsInstalled)
                .Concat(compilerExtensions)
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            SyncMarketplaceExtensionCollection(CompilerExtensions, combinedCompilerEntries);
            SyncObservableCollection(
                ExtensionLoadErrors,
                ExtensionLoadErrors.Concat(loadErrors).Distinct().ToList(),
                error => error);

            SyncCompilerInstallStates();
            OnPropertyChanged(nameof(ExtensionLoadErrors));
            NotifyExtensionFiltersChanged();

            compilerIconMap = CompilerExtensions
                .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().IconUrl, StringComparer.OrdinalIgnoreCase);

            RefreshManualCompilerExtensions();
        });
        if (_extensionFilterBatchDepth > 0)
        {
            _deferredExtensionUiActions.Add(() =>
            {
                _ = FetchMarketplaceIconsAsync(compilerIconMap, CompilerExtensions);
            });
        }
        else
        {
            _ = FetchMarketplaceIconsAsync(compilerIconMap, CompilerExtensions);
        }
        _ = RefreshCompilerResolutionsAsync(compilerEntries, compilerExtensions, effectiveForceResolve);
    }

    private static string? TryReadCacheFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null; }
        catch { return null; }
    }

    private static void TryWriteCacheFile(string path, string contents)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, System.Text.Encoding.UTF8);
        }
        catch { }
    }

    private static T? ReadJsonCache<T>(string path, string logName) where T : class
    {
        var json = TryReadCacheFile(path);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"Failed to read {logName}.", ex); return null; }
    }

    private static void WriteJsonCache<T>(string path, T value, string logName)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            TryWriteCacheFile(path, json);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"Failed to write {logName}.", ex); }
    }

    private void LoadInstalledCompilerRegistry()
    {
        var loaded = ReadJsonCache<Dictionary<string, InstalledCompilerRecord>>(CompilerInstallRegistryPath, "installed-compilers.json");
        if (loaded is not null) _installedCompilers = new Dictionary<string, InstalledCompilerRecord>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveInstalledCompilerRegistry() => WriteJsonCache(CompilerInstallRegistryPath, _installedCompilers, "installed-compilers.json");

    private void SyncCompilerInstallStates()
    {
        foreach (var entry in CompilerExtensions)
        {
            if (_installedCompilers.TryGetValue(entry.Id, out var record))
            {
                var isUpdateAvailable = CompareExtensionVersions(entry.Version, record.Version) > 0;
                entry.SetCompilerInstalledState(record.Version, record.InstalledOnUtc, isUpdateAvailable);
                continue;
            }

            var manualMatch = _manualCompilers.Values.FirstOrDefault(r =>
                string.Equals(r.CanonicalCompilerId, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (manualMatch is not null)
            {
                var version = string.IsNullOrWhiteSpace(manualMatch.Version) ? "Local" : manualMatch.Version;
                entry.SetCompilerInstalledState(version, manualMatch.AddedOnUtc, isUpdateAvailable: false);
                continue;
            }

            entry.SetCompilerInstalledState(null, null, isUpdateAvailable: false);
        }
        NotifyExtensionFiltersChanged();
    }

    private void LoadManualCompilerRegistry()
    {
        var loaded = ReadJsonCache<ManualCompilerRegistryFile>(ManualCompilersRegistryPath, "manual-compilers.json");
        if (loaded is null) return;
        _manualCompilers = new Dictionary<string, ManualCompilerRecord>(loaded.Entries, StringComparer.OrdinalIgnoreCase);
        _autoDetectDismissedIds = new HashSet<string>(loaded.DismissedAutoDetectIds, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveManualCompilerRegistry() => WriteJsonCache(ManualCompilersRegistryPath, new ManualCompilerRegistryFile { Entries = _manualCompilers, DismissedAutoDetectIds = _autoDetectDismissedIds.ToList() }, "manual-compilers.json");

    private void RefreshManualCompilerExtensions()
    {
        var canonicalIconMap = CompilerExtensions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IconUrl, StringComparer.OrdinalIgnoreCase);

        ManualCompilerExtensions.Clear();
        foreach (var pair in _manualCompilers.OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase))
            ManualCompilerExtensions.Add(BuildManualCompilerExtension(pair.Key, pair.Value, canonicalIconMap));

        var manualIconMap = ManualCompilerExtensions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IconUrl, StringComparer.OrdinalIgnoreCase);
        if (manualIconMap.Count > 0)
            _ = FetchMarketplaceIconsAsync(manualIconMap, ManualCompilerExtensions);
        RefreshRunBuildState();
    }

    private static MarketplaceExtension BuildManualCompilerExtension(
        string id,
        ManualCompilerRecord record,
        IReadOnlyDictionary<string, string> canonicalIconMap)
    {
        var iconUrl = record.CanonicalCompilerId is not null &&
            canonicalIconMap.TryGetValue(record.CanonicalCompilerId, out var matchedIconUrl)
                ? matchedIconUrl
                : string.Empty;

        var extension = new MarketplaceExtension
        {
            Id = id,
            Name = record.Name,
            Type = "compiler",
            Author = record.Author,
            Description = record.Description,
            Version = record.Version,
            DownloadUrl = string.Empty,
            FileName = string.Empty,
            IconUrl = iconUrl
        };
        extension.SetCompilerInstalledState(
            string.IsNullOrWhiteSpace(record.Version) ? "Local" : record.Version,
            record.AddedOnUtc,
            isUpdateAvailable: false);
        return extension;
    }

    private static bool IsManuallyTrackedCompilerId(string id) =>
        id.StartsWith("manual:", StringComparison.Ordinal) || id.StartsWith("auto:", StringComparison.Ordinal);

    private void AddOrUpdateManualCompiler(
        string executablePath,
        bool autoDetected,
        string? displayName = null,
        string? author = null,
        string? id = null,
        string? canonicalCompilerId = null)
    {
        var resolvedId = id ?? "manual:" + ComputeStablePathId(executablePath);
        var version = TryGetFileVersion(executablePath);
        _manualCompilers[resolvedId] = new ManualCompilerRecord
        {
            Name = displayName ?? Path.GetFileNameWithoutExtension(executablePath),
            ExecutablePath = executablePath,
            Author = author ?? "Manually added",
            Description = executablePath,
            Version = string.IsNullOrWhiteSpace(version) ? "Local" : version,
            AddedOnUtc = DateTime.UtcNow,
            AutoDetected = autoDetected,
            CanonicalCompilerId = canonicalCompilerId
        };
        SaveManualCompilerRegistry();
    }

    private void ForgetManualCompilerRecord(MarketplaceExtension compilerExtension)
    {
        if (_manualCompilers.Remove(compilerExtension.Id) &&
            compilerExtension.Id.StartsWith("auto:", StringComparison.Ordinal))
        {
            _autoDetectDismissedIds.Add(compilerExtension.Id);
        }

        SaveManualCompilerRegistry();
        RefreshManualCompilerExtensions();
        NotifyExtensionFiltersChanged();
    }

    private static string ComputeStablePathId(string path)
    {
        var normalized = Path.GetFullPath(path).ToLowerInvariant();
        var hashBytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hashBytes)[..12];
    }

    private static string TryGetFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly (string Id, string DisplayName, string Author, string[] ExeNames, string? CanonicalCompilerId, bool MultiVersion, string? VersionArg)[] AutoDetectCompilerCandidates =
    [
        ("dotnet-cli", ".NET SDK (dotnet)", "Microsoft", new[] { "dotnet.exe" }, "dotnet-sdk", true, "--version"),
        ("csc", "C# Compiler (csc.exe)", "Microsoft", new[] { "csc.exe" }, "dotnet-sdk", false, null),
        ("gcc", "GCC", "GNU / MinGW-w64", new[] { "gcc.exe" }, "msys2-mingw", false, null),
        ("gpp", "G++", "GNU / MinGW-w64", new[] { "g++.exe" }, "msys2-mingw", false, null),
        ("clang", "Clang", "LLVM Project", new[] { "clang.exe" }, "llvm-clang", false, null),
        ("python-auto", "Python", "Python Software Foundation", new[] { "python.exe" }, "python", true, "--version"),
        ("node-auto", "Node.js", "OpenJS Foundation", new[] { "node.exe" }, "nodejs", true, "--version"),
        ("javac", "Java (JDK)", "OpenJDK", new[] { "javac.exe" }, "temurin-jdk", true, "-version"),
        ("go-auto", "Go", "Google", new[] { "go.exe" }, "go", false, null),
        ("rustc", "Rust (rustc)", "Rust Foundation", new[] { "rustc.exe" }, "rust-rustup", false, null),
        ("perl-auto", "Perl", "Strawberry Perl", new[] { "perl.exe" }, "strawberry-perl", false, null),
        ("ruby-auto", "Ruby", "RubyInstaller", new[] { "ruby.exe" }, "rubyinstaller", false, null),
        ("swift-auto", "Swift", "Swift.org", new[] { "swift.exe" }, "swift", false, null),
        ("julia-auto", "Julia", "JuliaLang", new[] { "julia.exe" }, "julia", false, null)
    ];

    private async Task AutoDetectDefaultCompilersAsync()
    {
        try
        {
            var discovered = await Task.Run(() =>
            {
                var results = new List<(string Id, string Path, string Name, string Author, string? CanonicalCompilerId)>();

                try
                {
                    var clPath = TryFindMsvcCl();
                    if (clPath is not null)
                        results.Add(("msvc-cl", clPath, "MSVC (cl.exe)", "Microsoft (Visual Studio)", null));
                }
                catch
                {
                }

                foreach (var candidate in AutoDetectCompilerCandidates)
                {
                    try
                    {
                        foreach (var exeName in candidate.ExeNames)
                        {
                            if (candidate.MultiVersion)
                            {
                                var allFound = FindAllOnPath(exeName);
                                foreach (var found in allFound)
                                {
                                    if (IsKnownShimOrLauncher(found, candidate.Id))
                                        continue;

                                    var version = TryGetVersionFromPath(found);
                                    if (string.IsNullOrWhiteSpace(version) && candidate.VersionArg is { } arg)
                                        version = TryGetVersionFromProcess(found, arg);
                                    var id = string.IsNullOrWhiteSpace(version)
                                        ? candidate.Id
                                        : $"{candidate.Id}-{version}";
                                    var displayName = string.IsNullOrWhiteSpace(version)
                                        ? candidate.DisplayName
                                        : $"{candidate.DisplayName} {version}";
                                    results.Add((id, found, displayName, candidate.Author, candidate.CanonicalCompilerId));
                                }
                            }
                            else
                            {
                                var found = TryFindOnPath(exeName);
                                if (found is null)
                                    continue;

                                results.Add((candidate.Id, found, candidate.DisplayName, candidate.Author, candidate.CanonicalCompilerId));
                            }
                            break;
                        }
                    }
                    catch
                    {
                    }
                }

                return results;
            });

            var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, _, _, _, _) in discovered)
                discoveredIds.Add("auto:" + id);

            foreach (var candidate in AutoDetectCompilerCandidates.Where(c => c.MultiVersion))
            {
                var baseId = "auto:" + candidate.Id;
                var hasVersioned = discoveredIds.Any(d =>
                    d.StartsWith(baseId + "-", StringComparison.OrdinalIgnoreCase));
                if (hasVersioned && _manualCompilers.ContainsKey(baseId))
                {
                    _manualCompilers.Remove(baseId);
                    _autoDetectDismissedIds.Add(baseId);
                }
            }

            var shimKeys = _manualCompilers
                .Where(kvp => kvp.Value.AutoDetected && IsKnownShimOrLauncher(kvp.Value.ExecutablePath, kvp.Key.Contains("python") ? "python-auto" : kvp.Key.Contains("node") ? "node-auto" : ""))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in shimKeys)
            {
                _manualCompilers.Remove(key);
                _autoDetectDismissedIds.Add(key);
            }
            if (shimKeys.Count > 0)
                SaveManualCompilerRegistry();

            var addedAny = false;
            foreach (var (id, path, name, author, canonicalCompilerId) in discovered)
            {
                var resolvedId = "auto:" + id;
                if (_manualCompilers.ContainsKey(resolvedId) || _autoDetectDismissedIds.Contains(resolvedId))
                    continue;

                if (canonicalCompilerId is not null && _installedCompilers.ContainsKey(canonicalCompilerId))
                    continue;

                AddOrUpdateManualCompiler(path, autoDetected: true, displayName: name, author: author, id: resolvedId, canonicalCompilerId: canonicalCompilerId);
                addedAny = true;
            }

            if (addedAny)
            {
                SaveManualCompilerRegistry();
                RefreshManualCompilerExtensions();
                NotifyExtensionFiltersChanged();
                RefreshRunBuildState();
            }
        }
        catch
        {
        }
    }

    private static List<string> FindAllOnPath(string exeName)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), exeName);
                    if (!File.Exists(candidate)) continue;
                    var full = Path.GetFullPath(candidate);
                    if (seen.Add(full)) results.Add(full);
                }
                catch { }
            }
        }
        catch { }
        return results;
    }

    private static string? TryFindOnPath(string exeName) => FindAllOnPath(exeName).FirstOrDefault();

    private static string TryGetVersionFromPath(string exePath)
    {
        try
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(exePath)) ?? string.Empty;

            var match = Regex.Match(dirName, @"(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)");
            if (match.Success)
                return match.Groups[1].Value;

            var fileName = Path.GetFileNameWithoutExtension(exePath);
            match = Regex.Match(fileName, @"(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)");
            if (match.Success)
                return match.Groups[1].Value;

            var info = FileVersionInfo.GetVersionInfo(exePath);
            var fileVer = info.FileVersion?.Trim();
            if (!string.IsNullOrWhiteSpace(fileVer))
                return fileVer;

            var prodVer = info.ProductVersion?.Trim();
            if (!string.IsNullOrWhiteSpace(prodVer))
                return prodVer;
        }
        catch
        {
        }
        return string.Empty;
    }

    private static string TryGetVersionFromProcess(string exePath, string versionArg)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = versionArg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return string.Empty;

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(output))
                output = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(3000);

            var match = Regex.Match(output, @"(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsKnownShimOrLauncher(string exePath, string candidateId)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath) ?? string.Empty;
            var dirName = Path.GetFileName(dir);
            var parentDir = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;

            if (candidateId is "python-auto")
            {
                if (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                    parentDir.Equals("Python", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (dirName.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (candidateId is "node-auto" &&
                dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
        }
        return false;
    }

    private static string? TryFindMsvcCl()
    {
        try
        {
            var vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere))
                return null;

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return null;

            var installPath = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(installPath))
                return null;

            var msvcRoot = Path.Combine(installPath, "VC", "Tools", "MSVC");
            if (!Directory.Exists(msvcRoot))
                return null;

            var versionDir = Directory.GetDirectories(msvcRoot).OrderByDescending(d => d).FirstOrDefault();
            if (versionDir is null)
                return null;

            var clPath = Path.Combine(versionDir, "bin", "Hostx64", "x64", "cl.exe");
            return File.Exists(clPath) ? clPath : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record CompilerIndexEntry(
        string Id, string Name, string Type, string Author, string Description, string IconUrl,
        CompilerResolverSpec? Resolver,
        string FallbackVersion, string FallbackDownloadUrl, string FallbackFileName,
        string[] FileExtensions, string[] LanguageExtensionIds,
        string? RunCommandTemplate, string? BuildCommandTemplate,
        IReadOnlyDictionary<string, CompilerFileCommands>? FileCommands);

    private sealed record CompilerFileCommands(string? Run, string? Build);

    private sealed record BuiltinCompilerFallback(
        string[] FileExtensions, string[] LanguageExtensionIds,
        string? Run, string? Build,
        IReadOnlyDictionary<string, (string? Run, string? Build)>? FileCommands);

    private static readonly Dictionary<string, BuiltinCompilerFallback> BuiltinCompilerFallbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet-sdk"] = new([".cs", ".fs", ".vb"], ["csharp-kodo-extension"], "dotnet run", "dotnet build", null),
            ["go"] = new([".go"], [], "go run {file}", "go build -o {name}.exe", null),
            ["temurin-jdk"] = new([".java"], ["java-kodo-extension"], "java {name}", "javac {file}", null),
            ["llvm-clang"] = new([".c", ".cpp", ".cc", ".cxx", ".h", ".hpp"], ["cpp-kodo-extension"], null, "clang {file} -o {name}.exe", null),
            ["msys2-mingw"] = new([".c", ".cpp", ".cc", ".cxx", ".h", ".hpp"], ["cpp-kodo-extension"], null, "gcc {file} -o {name}.exe", null),
            ["gcc"] = new([".c", ".cpp", ".cc", ".cxx", ".h", ".hpp"], ["c-kodo-extension", "cpp-kodo-extension"], null, null,
                new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase)
                {
                    [".c"] = (null, "gcc {file} -o {name}.exe"),
                    [".cpp"] = (null, "g++ {file} -o {name}.exe"),
                }),
            ["nodejs"] = new([".js", ".jsx", ".mjs", ".cjs"], ["javascript-kodo-extension"], "node {file}", null, null),
            ["strawberry-perl"] = new([".pl", ".pm"], [], "perl {file}", null, null),
            ["python"] = new([".py", ".pyw"], ["python-kodo-extension"], "python {file}", null, null),
            ["rubyinstaller"] = new([".rb"], ["ruby-kodo-extension"], "ruby {file}", null, null),
            ["rust-rustup"] = new([".rs"], ["rust-kodo-extension"], "cargo run", "cargo build", null),
            ["shine-compiler"] = new([".shine"], ["shine-kodo-extension"], "shine {file}", "shinec {file} -o {name}.exe", null),
            ["swift"] = new([".swift"], ["swift-kodo-extension"], "swift {file}", "swiftc {file} -o {name}.exe", null),
            ["zig"] = new([".zig"], ["zig-kodo-extension"], "zig run {file}", "zig build-exe {file} -femit-bin={name}.exe", null),
            ["php"] = new([".php"], ["php-kodo-extension"], "php {file}", null, null),
            ["kotlin"] = new([".kt", ".kts"], ["kotlin-kodo-extension"], "kotlin {name}.jar", "kotlinc {file} -include-runtime -d {name}.jar", null),
            ["typescript"] = new([".ts", ".tsx", ".mts", ".cts"], ["typescript-kodo-extension"], "ts-node {file}", "tsc {file}", null),
            ["lua"] = new([".lua"], ["lua-kodo-extension"], "lua {file}", null, null),
            ["coffeescript"] = new([".coffee"], ["coffeescript-kodo-extension"], "coffee {file}", "coffee -c {file}", null),
            ["nasm"] = new([".asm", ".nasm", ".s"], [], null, "nasm -f win64 {file} -o {name}.obj", null),
            ["holyc"] = new([".hc"], [], null, "holyc {file}", null),
        };

    private static List<CompilerIndexEntry> ParseCompilerIndexEntries(string json, List<string> loadErrors)
    {
        var result = new List<CompilerIndexEntry>();
        var jsonOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };
        using var doc = JsonDocument.Parse(json, jsonOptions);
        if (!doc.RootElement.TryGetProperty("compilers", out var compilersElement) ||
            compilersElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in compilersElement.EnumerateArray())
        {
            string entryId = "?";
            try
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                entryId = string.IsNullOrWhiteSpace(id) ? "?" : id;
                if (string.IsNullOrWhiteSpace(id) || result.Any(e => e.Id == id))
                    continue;

                string GetStr(string prop) =>
                    item.TryGetProperty(prop, out var el) ? el.GetString() ?? string.Empty : string.Empty;

                CompilerResolverSpec? resolver = null;
                if (item.TryGetProperty("resolver", out var resolverEl) && resolverEl.ValueKind == JsonValueKind.Object)
                {
                    var kind = resolverEl.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        var resolverParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in resolverEl.EnumerateObject())
                        {
                            if (prop.NameEquals("kind")) continue;
                            resolverParams[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                ? (prop.Value.GetString() ?? string.Empty)
                                : prop.Value.ToString();
                        }
                        resolver = new CompilerResolverSpec(kind, resolverParams);
                    }
                }

                string? GetOptStr(string prop) =>
                    item.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                        ? el.GetString()
                        : null;

                string[] ParseStringArray(string prop)
                {
                    var list = new List<string>();
                    if (item.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sub in el.EnumerateArray())
                        {
                            if (sub.ValueKind != JsonValueKind.String) continue;
                            var value = sub.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                list.Add(value);
                        }
                    }
                    return list.ToArray();
                }

                Dictionary<string, CompilerFileCommands>? fileCommands = null;
                if (item.TryGetProperty("fileCommands", out var fcEl) && fcEl.ValueKind == JsonValueKind.Object)
                {
                    fileCommands = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in fcEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var runCmd = prop.Value.TryGetProperty("run", out var runEl) && runEl.ValueKind == JsonValueKind.String
                            ? runEl.GetString() : null;
                        var buildCmd = prop.Value.TryGetProperty("build", out var buildEl) && buildEl.ValueKind == JsonValueKind.String
                            ? buildEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(runCmd) && string.IsNullOrWhiteSpace(buildCmd))
                            continue;
                        fileCommands[prop.Name] = new CompilerFileCommands(
                            string.IsNullOrWhiteSpace(runCmd) ? null : runCmd,
                            string.IsNullOrWhiteSpace(buildCmd) ? null : buildCmd);
                    }
                    if (fileCommands.Count == 0)
                        fileCommands = null;
                }

                var fallbackVersion = string.Empty;
                var fallbackDownloadUrl = string.Empty;
                var fallbackFileName = string.Empty;
                if (item.TryGetProperty("fallback", out var fallbackEl) && fallbackEl.ValueKind == JsonValueKind.Object)
                {
                    fallbackVersion = fallbackEl.TryGetProperty("version", out var fvEl) ? fvEl.GetString() ?? string.Empty : string.Empty;
                    fallbackDownloadUrl = fallbackEl.TryGetProperty("downloadUrl", out var fdEl) ? fdEl.GetString() ?? string.Empty : string.Empty;
                    fallbackFileName = fallbackEl.TryGetProperty("fileName", out var ffEl) ? ffEl.GetString() ?? string.Empty : string.Empty;
                }

                var fileExtensions = ParseStringArray("fileExtensions");
                var languageExtensionIds = ParseStringArray("languageExtensions");
                var runTemplate = GetOptStr("run");
                var buildTemplate = GetOptStr("build");

                if (fileExtensions.Length == 0 && BuiltinCompilerFallbacks.TryGetValue(id, out var builtin))
                {
                    fileExtensions = builtin.FileExtensions;
                    if (languageExtensionIds.Length == 0)
                        languageExtensionIds = builtin.LanguageExtensionIds;
                    runTemplate ??= builtin.Run;
                    buildTemplate ??= builtin.Build;
                    if (fileCommands is null && builtin.FileCommands is not null)
                    {
                        fileCommands = builtin.FileCommands
                            .ToDictionary(kv => kv.Key, kv => new CompilerFileCommands(kv.Value.Run, kv.Value.Build),
                                StringComparer.OrdinalIgnoreCase);
                    }
                }

                result.Add(new CompilerIndexEntry(
                    id, GetStr("name"), GetStr("type"), GetStr("author"), GetStr("description"),
                    NormalizeGitHubUrl(GetStr("iconUrl")), resolver,
                    fallbackVersion, fallbackDownloadUrl, fallbackFileName,
                    fileExtensions, languageExtensionIds,
                    runTemplate, buildTemplate,
                    fileCommands));
            }
            catch (Exception itemEx)
            {
                loadErrors.Add($"Skipped malformed compiler entry '{entryId}': {itemEx.Message}");
                KodoDiagnostics.LogDebug($"Skipped malformed compiler entry '{entryId}'", itemEx);
            }
        }

        return result;
    }

    private sealed class ResolvedCompilerCacheEntry
    {
        public string Version { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DateTime ResolvedUtc { get; set; }
    }

    private string CompilerResolvedCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "resolved-compiler-versions.json");

    private bool _hasResolvedCompilersThisSession;

    private Dictionary<string, ResolvedCompilerCacheEntry> LoadCompilerResolutionCache()
    {
        var d = ReadJsonCache<Dictionary<string, ResolvedCompilerCacheEntry>>(CompilerResolvedCachePath, "resolved-compiler-versions.json");
        return d is not null ? new Dictionary<string, ResolvedCompilerCacheEntry>(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCompilerResolutionCache(Dictionary<string, ResolvedCompilerCacheEntry> cache) => WriteJsonCache(CompilerResolvedCachePath, cache, "resolved-compiler-versions.json");

    private void InvalidateCompilerResolution(string compilerId)
    {
        try
        {
            var cache = LoadCompilerResolutionCache();
            if (cache.Remove(compilerId))
                SaveCompilerResolutionCache(cache);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Failed to invalidate resolution cache entry for '{compilerId}'.", ex);
        }
    }

    private static List<MarketplaceExtension> BuildCompilerExtensionsFromCacheOrFallback(
        List<CompilerIndexEntry> entries,
        Dictionary<string, ResolvedCompilerCacheEntry> cache)
    {
        var result = new List<MarketplaceExtension>();
        foreach (var entry in entries)
        {
            string version, downloadUrl, fileName;
            var description = entry.Description;
            var isManual = entry.Resolver is null ||
                string.Equals(entry.Resolver.Kind, "manual", StringComparison.OrdinalIgnoreCase);

            if (isManual)
            {
                version = entry.FallbackVersion;
                downloadUrl = entry.FallbackDownloadUrl;
                fileName = entry.FallbackFileName;
                if (entry.Resolver is not null)
                {
                    var infoUrl = entry.Resolver.Params.TryGetValue("infoUrl", out var iu) ? iu : null;
                    description = string.IsNullOrWhiteSpace(infoUrl)
                        ? $"{description} (No public update API for this one - version is checked manually.)"
                        : $"{description} (No public update API for this one - check {infoUrl} for the latest release.)";
                }
            }
            else if (cache.TryGetValue(entry.Id, out var cached))
            {
                version = cached.Version;
                downloadUrl = cached.DownloadUrl;
                fileName = cached.FileName;
            }
            else
            {
                version = "Checking\u2026";
                downloadUrl = string.Empty;
                fileName = string.Empty;
            }

            result.Add(new MarketplaceExtension
            {
                Id = entry.Id,
                Version = version,
                Name = entry.Name,
                Type = entry.Type,
                Author = entry.Author,
                Description = description,
                DownloadUrl = downloadUrl,
                FileName = fileName,
                IconUrl = entry.IconUrl,
                FileExtensions = entry.FileExtensions,
                LanguageExtensionIds = entry.LanguageExtensionIds,
                RunCommandTemplate = entry.RunCommandTemplate,
                BuildCommandTemplate = entry.BuildCommandTemplate,
                FileCommands = entry.FileCommands is null
                    ? null
                    : entry.FileCommands.ToDictionary(
                        kv => kv.Key,
                        kv => (kv.Value.Run, kv.Value.Build),
                        StringComparer.OrdinalIgnoreCase)
            });
        }
        return result;
    }

    private async Task RefreshCompilerResolutionsAsync(List<CompilerIndexEntry> entries, List<MarketplaceExtension> liveExtensions, bool forceResolve)
    {
        var cache = LoadCompilerResolutionCache();
        var toResolve = entries
            .Where(e => e.Resolver is not null &&
                        !string.Equals(e.Resolver.Kind, "manual", StringComparison.OrdinalIgnoreCase) &&
                        (forceResolve || !cache.ContainsKey(e.Id)))
            .ToList();
        if (toResolve.Count == 0)
            return;

        var extensionById = liveExtensions.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var cacheLock = new object();
        var anyChanged = false;

        var tasks = toResolve.Select(async entry =>
        {
            var resolution = await ResolveCompilerAsync(entry.Id, entry.Resolver!).ConfigureAwait(false);

            var wasCached = cache.TryGetValue(entry.Id, out var previouslyCached);
            var version = resolution?.Version ?? (wasCached ? previouslyCached!.Version : entry.FallbackVersion);
            var downloadUrl = resolution?.DownloadUrl ?? (wasCached ? previouslyCached!.DownloadUrl : entry.FallbackDownloadUrl);
            var fileName = resolution?.FileName ?? (wasCached ? previouslyCached!.FileName : entry.FallbackFileName);

            lock (cacheLock)
            {
                cache[entry.Id] = new ResolvedCompilerCacheEntry
                {
                    Version = version,
                    DownloadUrl = downloadUrl,
                    FileName = fileName,
                    ResolvedUtc = DateTime.UtcNow
                };
            }
            anyChanged = true;

            if (!extensionById.TryGetValue(entry.Id, out var live))
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                live.Version = version;
                live.DownloadUrl = downloadUrl;
                live.FileName = fileName;
            });
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (anyChanged)
            SaveCompilerResolutionCache(cache);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SyncCompilerInstallStates();
            NotifyExtensionFiltersChanged();
        });
    }

    private async Task RefreshSingleCompilerResolutionAsync(string compilerId)
    {
        var entry = _compilerIndexEntries.FirstOrDefault(e =>
            string.Equals(e.Id, compilerId, StringComparison.OrdinalIgnoreCase));
        if (entry?.Resolver is null ||
            string.Equals(entry.Resolver.Kind, "manual", StringComparison.OrdinalIgnoreCase))
            return;

        var resolution = await ResolveCompilerAsync(entry.Id, entry.Resolver).ConfigureAwait(false);
        if (resolution is null)
            return;

        try
        {
            var cache = LoadCompilerResolutionCache();
            cache[entry.Id] = new ResolvedCompilerCacheEntry
            {
                Version = resolution.Version,
                DownloadUrl = resolution.DownloadUrl,
                FileName = resolution.FileName,
                ResolvedUtc = DateTime.UtcNow
            };
            SaveCompilerResolutionCache(cache);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Failed to update resolution cache for '{compilerId}'.", ex);
        }

        var live = CompilerExtensions.FirstOrDefault(e =>
            string.Equals(e.Id, compilerId, StringComparison.OrdinalIgnoreCase));
        if (live is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            live.Version = resolution.Version;
            live.DownloadUrl = resolution.DownloadUrl;
            live.FileName = resolution.FileName;
        });
    }

    private void CompilersTabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        IsCompilersTabSelected = true;
        RefreshMarketplaceConnectivityState();
        _ = RefreshExtensionsDataAsync();
    }
    private async void InstallCompilerExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MarketplaceExtension compilerExtension })
            await InstallCompilerExtensionAsync(compilerExtension);
    }

    private async Task InstallCompilerExtensionAsync(MarketplaceExtension compilerExtension)
    {
        if (compilerExtension.IsInstalling || (compilerExtension.IsInstalled && !compilerExtension.IsUpdateAvailable))
            return;

        var localInstall = DescribeLocalCompilerInstall(compilerExtension);
        if (localInstall is not null)
        {
            var proceed = await ShowConfirmationDialogAsync(
                "Existing compiler detected",
                $"Kodo can see an existing install of {compilerExtension.Name} on this machine:\n\n" +
                $"{localInstall}\n\n" +
                "Continuing will download and run the installer again, which may overwrite or " +
                "conflict with the existing copy. Continue?",
                confirmLabel: "Continue",
                cancelLabel: "Cancel");
            if (!proceed) return;
        }

        RefreshMarketplaceConnectivityState();
        compilerExtension.IsInstalling = true;
        NotifyExtensionActionStateChanged();
        var action = compilerExtension.IsUpdateAvailable ? "Updating" : "Installing";
        compilerExtension.InstallButtonText = $"{action}...";
        ExtensionsStatusText = $"{action} {compilerExtension.Name}...";

        try
        {
            if (string.IsNullOrWhiteSpace(compilerExtension.DownloadUrl))
                throw new InvalidOperationException($"{compilerExtension.Name} has no download URL set.");

            var fileName = string.IsNullOrWhiteSpace(compilerExtension.FileName)
                ? Path.GetFileName(new Uri(compilerExtension.DownloadUrl).LocalPath)
                : compilerExtension.FileName;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"{compilerExtension.Id}-{compilerExtension.Version}.exe";

            var compilerFolder = Path.Combine(CompilersFolderPath, compilerExtension.Id);
            Directory.CreateDirectory(compilerFolder);
            var outputPath = Path.Combine(compilerFolder, fileName);

            var bytes = await RunWithGitHubTimeoutAsync(
                $"Compiler download - {compilerExtension.Name}",
                async ct =>
                {
                    using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, compilerExtension.DownloadUrl);
                    using var downloadResponse = await MarketplaceHttpClient.SendAsync(
                        downloadRequest, HttpCompletionOption.ResponseContentRead, ct);
                    if (!downloadResponse.IsSuccessStatusCode)
                    {
                        InvalidateCompilerResolution(compilerExtension.Id);
                        throw new HttpRequestException(
                            $"Unable to download {compilerExtension.Name} installer (HTTP {(int)downloadResponse.StatusCode}).");
                    }
                    return await downloadResponse.Content.ReadAsByteArrayAsync(ct);
                });

            await File.WriteAllBytesAsync(outputPath, bytes);

            if (compilerExtension.Id.Equals("msys2-mingw", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMsys2MingwInstallAsync(outputPath, compilerExtension);
            }
            else
            {
                Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
            }

            _installedCompilers[compilerExtension.Id] = new InstalledCompilerRecord
            {
                Version = compilerExtension.Version,
                InstalledOnUtc = DateTime.UtcNow,
                InstalledExePath = outputPath
            };
            SaveInstalledCompilerRegistry();
            SyncCompilerInstallStates();

            if (!compilerExtension.Id.Equals("msys2-mingw", StringComparison.OrdinalIgnoreCase))
            {
                await InstallPairedLanguageExtensionsAsync(compilerExtension);
                ExtensionsStatusText = $"{compilerExtension.Name} installer launched. Finish the setup wizard to complete installation.";
            }
        }
        catch (Exception ex)
        {
            SyncCompilerInstallStates();
            RefreshMarketplaceConnectivityState($"Compiler install - {compilerExtension.Name}", ex);
            ExtensionsStatusText = $"Failed to install {compilerExtension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Compiler install - {compilerExtension.Name}", ex);
            if (ex is HttpRequestException)
                _ = RefreshSingleCompilerResolutionAsync(compilerExtension.Id);
        }
        finally
        {
            compilerExtension.IsInstalling = false;
            NotifyExtensionActionStateChanged();
            SyncCompilerInstallStates();
        }
    }

    private async Task HandleMsys2MingwInstallAsync(string installerPath, MarketplaceExtension compilerExtension)
    {
        ExtensionsStatusText = $"{compilerExtension.Name} installer launched. Completing setup will auto-install g++...";
        try
        {
            var msysRoots = new[] { @"C:\msys64", @"C:\msys32", @"C:\tools\msys64", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "msys64") };
            string? bashPath = msysRoots.Select(r => Path.Combine(r, @"usr\bin\bash.exe")).FirstOrDefault(File.Exists);

            if (bashPath is null)
            {
                try
                {
                    using var installerProc = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
                    if (installerProc is not null)
                        await installerProc.WaitForExitAsync();
                    else
                        await Task.Delay(TimeSpan.FromSeconds(3));
                }
                catch { }

                await Task.Delay(TimeSpan.FromSeconds(1));
                bashPath = msysRoots.Select(r => Path.Combine(r, @"usr\bin\bash.exe")).FirstOrDefault(File.Exists);
                if (bashPath is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    bashPath = msysRoots.Select(r => Path.Combine(r, @"usr\bin\bash.exe")).FirstOrDefault(File.Exists);
                }
            }

            if (bashPath is null || !File.Exists(bashPath))
            {
                ExtensionsStatusText = $"{compilerExtension.Name} base installed. Please complete the MSYS2 wizard, then click Install again to finish g++ setup.";
                await ShowWarningDialogAsync($"{compilerExtension.Name} - next step",
                    new InvalidOperationException("MSYS2 base installed but bash.exe not found. Finish the wizard (use default C:\\msys64), then click Install again. Kodo will then auto-run pacman to get g++."));
                return;
            }

            var msysRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bashPath)!, "..", ".."));
            var mingwBin = Path.Combine(msysRoot, @"mingw64\bin");
            var ucrtBin = Path.Combine(msysRoot, @"ucrt64\bin");

            ExtensionsStatusText = $"Installing g++ via pacman (this may take a minute)...";
            var pacmanCmd = "pacman --noconfirm -Sy && pacman --noconfirm -S mingw-w64-x86_64-gcc mingw-w64-x86_64-gdb mingw-w64-x86_64-make";
            using var pacmanProc = Process.Start(new ProcessStartInfo(bashPath, $"-lc \"{pacmanCmd}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (pacmanProc is not null)
            {
                var output = await pacmanProc.StandardOutput.ReadToEndAsync();
                var error = await pacmanProc.StandardError.ReadToEndAsync();
                await pacmanProc.WaitForExitAsync();
                KodoDiagnostics.LogDebug($"pacman g++ install exit {pacmanProc.ExitCode}\n{output}\n{error}");
                if (pacmanProc.ExitCode != 0)
                {
                    ExtensionsStatusText = $"pacman finished with code {pacmanProc.ExitCode}. Check %AppData%\\Kodo\\kodo.log.";
                    await ShowWarningDialogAsync("MSYS2 pacman",
                        new InvalidOperationException($"pacman exit {pacmanProc.ExitCode}. Output:\n{output}\n{error}\n\nTry in MSYS2 shell: {pacmanCmd}"));
                }
            }

            try
            {
                var binToAdd = Directory.Exists(mingwBin) ? mingwBin : Directory.Exists(ucrtBin) ? ucrtBin : null;
                if (binToAdd is not null)
                {
                    var currentUserPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
                    if (!currentUserPath.Split(Path.PathSeparator).Any(p => p.Equals(binToAdd, StringComparison.OrdinalIgnoreCase)))
                    {
                        var newUserPath = string.IsNullOrWhiteSpace(currentUserPath) ? binToAdd : currentUserPath + Path.PathSeparator + binToAdd;
                        Environment.SetEnvironmentVariable("PATH", newUserPath, EnvironmentVariableTarget.User);
                        var procPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
                        if (!procPath.Split(Path.PathSeparator).Any(p => p.Equals(binToAdd, StringComparison.OrdinalIgnoreCase)))
                            Environment.SetEnvironmentVariable("PATH", procPath + Path.PathSeparator + binToAdd, EnvironmentVariableTarget.Process);
                    }
                    var gppPath = Path.Combine(binToAdd, "g++.exe");
                    if (File.Exists(gppPath) && !_installedCompilers.ContainsKey("msys2-mingw"))
                    {
                        AddOrUpdateManualCompiler(gppPath, autoDetected: false, displayName: "G++ (MSYS2)", author: "MSYS2", canonicalCompilerId: "msys2-mingw");
                        RefreshManualCompilerExtensions();
                    }
                }
            }
            catch (Exception ex) { KodoDiagnostics.LogDebug("Failed to update PATH for MSYS2", ex); }

            await InstallPairedLanguageExtensionsAsync(compilerExtension);
            ExtensionsStatusText = $"{compilerExtension.Name} ready - g++ installed. Switched to GCC for .cpp.";
            _compilerOverrides[".cpp"] = compilerExtension.Id;
            _compilerOverrides[".c"] = compilerExtension.Id;
            _compilerOverrides[".cc"] = compilerExtension.Id;
            SaveSettings();
            RefreshRunBuildState();
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("HandleMsys2MingwInstallAsync failed", ex);
            ExtensionsStatusText = $"MSYS2 setup incomplete: {ex.Message}";
            await ShowWarningDialogAsync("MSYS2 setup", ex);
        }
    }

    private async Task InstallPairedLanguageExtensionsAsync(MarketplaceExtension compilerExtension)
    {
        if (compilerExtension.LanguageExtensionIds.Length == 0)
            return;

        var names = new List<string>();
        foreach (var id in compilerExtension.LanguageExtensionIds)
        {
            var friendly = id;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var ext = MarketplaceExtensions.FirstOrDefault(e =>
                    string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                if (ext is not null) friendly = ext.Name;
            });
            names.Add(friendly);
        }

        var installExtension = await ShowConfirmationDialogAsync(
            "Install language extension?",
            $"{compilerExtension.Name} is paired with the following language extension(s):\n\n" +
            $"  {string.Join(", ", names)}\n\n" +
            "Install the language extension(s) as well? The compiler will still install if you skip.",
            confirmLabel: "Install extensions",
            cancelLabel: "Compiler only");
        if (!installExtension) return;

        foreach (var languageExtensionId in compilerExtension.LanguageExtensionIds)
        {
            MarketplaceExtension? languageExtension = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                languageExtension = MarketplaceExtensions.FirstOrDefault(ext =>
                    string.Equals(ext.Id, languageExtensionId, StringComparison.OrdinalIgnoreCase));
            });

            if (languageExtension is null || (languageExtension.IsInstalled && !languageExtension.IsUpdateAvailable))
                continue;

            try
            {
                await InstallMarketplaceExtensionAsync(languageExtension);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug(
                    $"Failed to install paired language extension '{languageExtensionId}' for compiler '{compilerExtension.Name}'.", ex);
            }
        }
    }

    private string? DescribeLocalCompilerInstall(MarketplaceExtension compilerExtension)
    {
        var id = compilerExtension.Id;

        if (_installedCompilers.TryGetValue(id, out var installed))
        {
            var version = string.IsNullOrWhiteSpace(installed.Version) ? "unknown version" : $"v{installed.Version}";
            var location = string.IsNullOrWhiteSpace(installed.InstalledExePath)
                ? string.Empty
                : $"\n  Location: {installed.InstalledExePath}";
            return $"  Tracked install ({version}){location}\n  Installed: {installed.InstalledOnUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }

        ManualCompilerRecord? match = null;
        if (_manualCompilers.TryGetValue(id, out var direct))
            match = direct;
        else
            match = _manualCompilers.Values.FirstOrDefault(r =>
                string.Equals(r.CanonicalCompilerId, id, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            var version = string.IsNullOrWhiteSpace(match.Version) ? "unknown version" : match.Version;
            var kind = match.AutoDetected ? "auto-detected" : "manually tracked";
            return $"  {match.Name} ({version}, {kind})\n  Executable: {match.ExecutablePath}";
        }

        return null;
    }

    private void ManualCompilerPathTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = AddManualCompilerFromTextBoxAsync();
    }

    private void AddManualCompilerButton_OnClick(object? sender, RoutedEventArgs e) =>
        _ = AddManualCompilerFromTextBoxAsync();

    private async Task AddManualCompilerFromTextBoxAsync()
    {
        var path = ManualCompilerPathText?.Trim().Trim('"') ?? string.Empty;
        ManualCompilerPathText = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            ExtensionsStatusText = $"Couldn't find a file at '{path}'.";
            return;
        }

        var displayName = Path.GetFileNameWithoutExtension(path);
        await Task.Run(() => AddOrUpdateManualCompiler(path, autoDetected: false));
        RefreshManualCompilerExtensions();
        NotifyExtensionFiltersChanged();
        ExtensionsStatusText = $"Added {displayName} at {path}.";
    }

    private async void UninstallCompilerExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarketplaceExtension compilerExtension }) return;

        var confirmed = await ShowConfirmationDialogAsync(
            "Uninstall compiler?",
            $"This removes '{compilerExtension.Name}' from disk and forgets its installed state. This can't be undone.",
            confirmLabel: "Uninstall",
            isDestructive: true);

        if (!confirmed) return;

        await UninstallCompilerExtensionAsync(compilerExtension);
    }

    private async Task UninstallCompilerExtensionAsync(MarketplaceExtension compilerExtension)
    {
        if (IsManuallyTrackedCompilerId(compilerExtension.Id))
        {
            ForgetManualCompilerRecord(compilerExtension);
            ExtensionsStatusText = $"Removed {compilerExtension.Name} from Kodo's tracked compilers.";
            return;
        }

        try
        {
            var located = FindCompilerUninstaller(compilerExtension.Name);

            if (located is null)
            {
                ExtensionsStatusText = $"The installation folder for {compilerExtension.Name} could not be found. It may have already been removed or the installation was cancelled. Kodo has removed it from the Installed list.";
                ForgetLocalCompilerRecord(compilerExtension);
                return;
            }

            var (exePath, arguments, installFolder) = located.Value;

            ExtensionsStatusText = $"Uninstalling {compilerExtension.Name}...";
            compilerExtension.IsInstalling = true;
            NotifyExtensionActionStateChanged();

            using var uninstallProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = ResolveValidWorkingDirectory(installFolder)
            });

            if (uninstallProcess is not null)
                await uninstallProcess.WaitForExitAsync();

            var exitSucceeded = uninstallProcess is not null &&
                (uninstallProcess.ExitCode == 0 || uninstallProcess.ExitCode == 3010);
            var removed = false;

            if (string.IsNullOrWhiteSpace(installFolder))
            {
                removed = exitSucceeded;
            }
            else if (!Directory.Exists(installFolder) || IsDirectoryEffectivelyEmpty(installFolder))
            {
                removed = exitSucceeded;
            }
            else
            {
                for (var attempt = 0; attempt < 30 && exitSucceeded; attempt++)
                {
                    if (!Directory.Exists(installFolder) || IsDirectoryEffectivelyEmpty(installFolder))
                    {
                        removed = true;
                        break;
                    }
                    await Task.Delay(750);
                }
            }

            if (removed)
            {
                ForgetLocalCompilerRecord(compilerExtension);
                ExtensionsStatusText = $"{compilerExtension.Name} uninstalled.";
            }
            else
            {
                var at = string.IsNullOrWhiteSpace(installFolder) ? string.Empty : $" at {installFolder}";
                ExtensionsStatusText = $"{compilerExtension.Name} still appears installed{at}. " +
                    "Try Uninstall again, or remove it manually from Program Files.";
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException || (ex is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == 2))
        {
            ExtensionsStatusText = $"The installation folder for {compilerExtension.Name} could not be found. It may have already been removed or the installation was cancelled. Kodo has removed it from the Installed list.";
            ForgetLocalCompilerRecord(compilerExtension);
        }
        catch (Exception ex)
        {
            var folder = FindCompilerUninstaller(compilerExtension.Name)?.InstallFolder;
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
            {
                ExtensionsStatusText = $"The installation folder for {compilerExtension.Name} could not be found at {folder}. It may have already been removed or the installation was cancelled. Kodo has removed it from the Installed list.";
                ForgetLocalCompilerRecord(compilerExtension);
                return;
            }
            if (ex.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("cannot find the path", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("The system cannot find", StringComparison.OrdinalIgnoreCase))
            {
                ExtensionsStatusText = $"The installation folder for {compilerExtension.Name} could not be found. It may have already been removed or the installation was cancelled. Kodo has removed it from the Installed list.";
                ForgetLocalCompilerRecord(compilerExtension);
                return;
            }

            ExtensionsStatusText = $"Failed to uninstall {compilerExtension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Compiler uninstall - {compilerExtension.Name}", ex);
        }
        finally
        {
            compilerExtension.IsInstalling = false;
            NotifyExtensionActionStateChanged();
        }
    }

    private static bool IsDirectoryEffectivelyEmpty(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .All(entry => Path.GetFileName(entry).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static (string ExePath, string Arguments, bool IsMsi) NormalizeUninstallCommand(string exePath, string arguments)
    {
        if (!Path.GetFileName(exePath).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            return (exePath, arguments, false);

        var fullPath = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var normalizedArgs = Regex.Replace(arguments, @"/[Ii](?<code>\{?[0-9A-Fa-f-]{36}\}?)", "/X${code}");
        return (fullPath, normalizedArgs, true);
    }

    private static string ResolveValidWorkingDirectory(string installFolder)
    {
        if (!string.IsNullOrWhiteSpace(installFolder) && Directory.Exists(installFolder))
            return installFolder;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile))
            return profile;

        return Environment.SystemDirectory;
    }

    private void ForgetLocalCompilerRecord(MarketplaceExtension compilerExtension)
    {
        var compilerFolder = Path.Combine(CompilersFolderPath, compilerExtension.Id);
        if (Directory.Exists(compilerFolder))
            Directory.Delete(compilerFolder, recursive: true);

        _installedCompilers.Remove(compilerExtension.Id);
        SaveInstalledCompilerRegistry();
        SyncCompilerInstallStates();
        NotifyExtensionFiltersChanged();
    }

    private static (string ExePath, string Arguments, string InstallFolder)? FindCompilerUninstaller(string compilerName)
    {
        var registryCommand = FindWindowsUninstallCommand(compilerName);
        if (registryCommand is not null)
        {
            var (exePath, arguments) = ParseUninstallCommand(registryCommand);
            var (exe, args, isMsi) = NormalizeUninstallCommand(exePath, arguments);
            var folder = isMsi ? string.Empty : (Path.GetDirectoryName(exe) ?? string.Empty);
            return (exe, AppendSilentUninstallFlags(exe, args), folder);
        }

        foreach (var programFilesRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(programFilesRoot) || !Directory.Exists(programFilesRoot))
                continue;

            foreach (var candidateName in GetCandidateInstallFolderNames(compilerName))
            {
                var candidateFolder = Path.Combine(programFilesRoot, candidateName);
                if (!Directory.Exists(candidateFolder))
                    continue;

                var uninstallerExe = Directory.EnumerateFiles(candidateFolder, "unins*.exe")
                    .FirstOrDefault();
                if (uninstallerExe is null)
                    continue;

                return (uninstallerExe, AppendSilentUninstallFlags(uninstallerExe, string.Empty), candidateFolder);
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateInstallFolderNames(string compilerName)
    {
        var trimmed = compilerName.Trim();
        if (trimmed.Length == 0) yield break;

        yield return trimmed;

        const string suffix = " Compiler";
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            yield return trimmed[..^suffix.Length].Trim();

        var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWord) && !string.Equals(firstWord, trimmed, StringComparison.OrdinalIgnoreCase))
            yield return firstWord;
    }

    private static string AppendSilentUninstallFlags(string exePath, string existingArguments)
    {
        var fileName = Path.GetFileName(exePath);

        if (fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
        {
            if (existingArguments.Contains("/VERYSILENT", StringComparison.OrdinalIgnoreCase) ||
                existingArguments.Contains("/SILENT", StringComparison.OrdinalIgnoreCase))
                return existingArguments;

            return $"{existingArguments} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART".Trim();
        }

        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (existingArguments.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                return existingArguments;

            return $"{existingArguments} /quiet /norestart".Trim();
        }

        return existingArguments;
    }

    private static (string FileName, string Arguments) ParseUninstallCommand(string uninstallCommand)
    {
        var trimmed = uninstallCommand.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 0)
            {
                var path = trimmed[1..closingQuote];
                var rest = trimmed[(closingQuote + 1)..].Trim();
                return (path, rest);
            }
        }

        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace > 0 &&
            trimmed[..firstSpace].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[(firstSpace + 1)..].Trim();
            return (trimmed[..firstSpace], rest);
        }

        return (trimmed, string.Empty);
    }

    private static string? FindWindowsUninstallCommand(string compilerName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrWhiteSpace(compilerName))
            return null;

        (RegistryKey Hive, string SubKey)[] roots =
        [
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        ];

        foreach (var (hive, subKey) in roots)
        {
            try
            {
                using var uninstallRoot = hive.OpenSubKey(subKey);
                if (uninstallRoot is null) continue;

                foreach (var entryName in uninstallRoot.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = uninstallRoot.OpenSubKey(entryName);
                        if (entry?.GetValue("DisplayName") is not string displayName ||
                            entry.GetValue("UninstallString") is not string uninstallString ||
                            string.IsNullOrWhiteSpace(uninstallString))
                            continue;

                        if (displayName.Contains(compilerName, StringComparison.OrdinalIgnoreCase) ||
                            compilerName.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                            return uninstallString;
                    }
                    catch { }
                }
            }
            catch { }
        }

        return null;
    }

    private static readonly TimeSpan CompilerResolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim CompilerResolveConcurrency = new(6, 6);

    private async Task<CompilerResolution?> ResolveCompilerAsync(string id, CompilerResolverSpec spec)
    {
        await CompilerResolveConcurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(CompilerResolveTimeout);
            return spec.Kind switch
            {
                "github-release" => await ResolveGitHubReleaseAsync(spec.Params, cts.Token).ConfigureAwait(false),
                "json-lookup" => await ResolveJsonLookupAsync(spec.Params, cts.Token).ConfigureAwait(false),
                "text-pointer" => await ResolveTextPointerAsync(spec.Params, cts.Token).ConfigureAwait(false),
                "directory-listing" => await ResolveDirectoryListingAsync(spec.Params, cts.Token).ConfigureAwait(false),
                "always-latest" => ResolveAlwaysLatest(spec.Params),
                "manual" => null,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Compiler resolver '{spec.Kind}' failed for '{id}'.", ex);
            return null;
        }
        finally
        {
            CompilerResolveConcurrency.Release();
        }
    }

    private async Task<CompilerResolution?> ResolveGitHubReleaseAsync(Dictionary<string, string> p, CancellationToken ct)
    {
        if (!p.TryGetValue("repo", out var repo) || string.IsNullOrWhiteSpace(repo)) return null;
        if (!p.TryGetValue("assetPattern", out var assetPatternStr) || string.IsNullOrWhiteSpace(assetPatternStr)) return null;

        var assetPattern = new Regex(assetPatternStr, RegexOptions.IgnoreCase);
        var versionPattern = p.TryGetValue("versionPattern", out var vp) && !string.IsNullOrWhiteSpace(vp) ? new Regex(vp) : null;
        var includePrerelease = p.TryGetValue("includePrerelease", out var ip) && string.Equals(ip, "true", StringComparison.OrdinalIgnoreCase);

        CompilerResolution? TryOne(JsonElement release)
        {
            if (!includePrerelease && release.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True)
                return null;
            var tagName = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(tagName)) return null;
            if (!release.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array) return null;

            foreach (var asset in assetsEl.EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(assetName) || !assetPattern.IsMatch(assetName)) continue;
                var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

                var version = versionPattern is not null
                    ? (versionPattern.Match(tagName) is { Success: true } m ? m.Groups[1].Value : tagName)
                    : tagName.TrimStart('v', 'V');
                return new CompilerResolution(version, downloadUrl, assetName);
            }
            return null;
        }

        using var latestRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        latestRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var latestResponse = await MarketplaceHttpClient.SendAsync(latestRequest, ct).ConfigureAwait(false);
        if (latestResponse.IsSuccessStatusCode)
        {
            var body = await latestResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var hit = TryOne(doc.RootElement);
            if (hit is not null) return hit;
        }

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=10");
        listRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var listResponse = await MarketplaceHttpClient.SendAsync(listRequest, ct).ConfigureAwait(false);
        if (!listResponse.IsSuccessStatusCode) return null;
        var listBody = await listResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var listDoc = JsonDocument.Parse(listBody);
        if (listDoc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in listDoc.RootElement.EnumerateArray())
        {
            var hit = TryOne(release);
            if (hit is not null) return hit;
        }
        return null;
    }

    private async Task<CompilerResolution?> ResolveJsonLookupAsync(Dictionary<string, string> p, CancellationToken ct)
    {
        if (!p.TryGetValue("url", out var urlTemplate) || string.IsNullOrWhiteSpace(urlTemplate)) return null;
        var url = SubstituteParams(urlTemplate, p);

        using var response = await MarketplaceHttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        var basePath = p.TryGetValue("path", out var bp) ? bp : string.Empty;
        var baseNode = GetPath(doc.RootElement, basePath) ?? doc.RootElement;

        var candidates = new List<(string? Key, JsonElement Value)>();
        if (p.TryGetValue("selectKey", out var selectKeyParam) && !string.IsNullOrWhiteSpace(selectKeyParam))
        {
            if (!p.TryGetValue(selectKeyParam, out var keyValue) || baseNode.ValueKind != JsonValueKind.Object ||
                !baseNode.TryGetProperty(keyValue, out var single))
                return null;
            candidates.Add((keyValue, single));
        }
        else if (p.TryGetValue("iterateProperties", out var iterProps) && string.Equals(iterProps, "true", StringComparison.OrdinalIgnoreCase))
        {
            if (baseNode.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in baseNode.EnumerateObject())
                candidates.Add((prop.Name, prop.Value));
        }
        else if (baseNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in baseNode.EnumerateArray())
                candidates.Add((null, el));
        }
        else
        {
            candidates.Add((null, baseNode));
        }

        var filters = p.TryGetValue("filters", out var f) ? f : null;
        candidates = candidates.Where(c => MatchesFilters(c.Value, filters)).ToList();

        if (p.TryGetValue("sortDescBy", out var sortBy) && !string.IsNullOrWhiteSpace(sortBy))
        {
            candidates = string.Equals(sortBy, "$key", StringComparison.OrdinalIgnoreCase)
                ? candidates.OrderByDescending(c => ParseVersionNumbers(c.Key ?? string.Empty), VersionNumberSequenceComparer.Instance).ToList()
                : candidates.OrderByDescending(c => ParseVersionNumbers(GetString(c.Value, sortBy)), VersionNumberSequenceComparer.Instance).ToList();
        }

        var versionField = p.TryGetValue("versionField", out var vf) ? vf : null;
        var versionSubField = p.TryGetValue("versionSubField", out var vsf) ? vsf : null;
        var versionStripPrefix = p.TryGetValue("versionStripPrefix", out var vsp) ? vsp : null;

        foreach (var candidate in candidates)
        {
            var rawVersion = string.IsNullOrEmpty(versionField)
                ? string.Empty
                : string.Equals(versionField, "$key", StringComparison.OrdinalIgnoreCase)
                    ? candidate.Key ?? string.Empty
                    : GetString(candidate.Value, versionField);

            var item = candidate.Value;
            if (p.TryGetValue("subPath", out var subPath) && !string.IsNullOrWhiteSpace(subPath))
            {
                var subNode = GetPath(item, subPath);
                if (subNode is null || subNode.Value.ValueKind != JsonValueKind.Array) continue;
                var subFilters = p.TryGetValue("subFilters", out var sf) ? sf : null;
                JsonElement? subHit = null;
                foreach (var s in subNode.Value.EnumerateArray())
                {
                    if (!MatchesFilters(s, subFilters)) continue;
                    subHit = s;
                    break;
                }
                if (subHit is null) continue;
                item = subHit.Value;
            }

            if (string.IsNullOrWhiteSpace(rawVersion) && !string.IsNullOrEmpty(versionSubField))
                rawVersion = GetString(item, versionSubField);
            if (string.IsNullOrWhiteSpace(rawVersion) && candidate.Key is not null)
                rawVersion = candidate.Key;
            if (string.IsNullOrWhiteSpace(rawVersion)) continue;

            if (!string.IsNullOrEmpty(versionStripPrefix) && rawVersion.StartsWith(versionStripPrefix, StringComparison.OrdinalIgnoreCase))
                rawVersion = rawVersion[versionStripPrefix.Length..];

            var downloadUrl = p.TryGetValue("urlField", out var urlField) && !string.IsNullOrWhiteSpace(urlField)
                ? GetString(item, urlField)
                : p.TryGetValue("urlTemplate", out var urlTpl) ? ApplyTemplate(urlTpl, rawVersion, item) : string.Empty;
            if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

            var fileName = p.TryGetValue("fileNameField", out var fileNameField) && !string.IsNullOrWhiteSpace(fileNameField)
                ? GetString(item, fileNameField)
                : p.TryGetValue("fileNameTemplate", out var fileNameTpl)
                    ? ApplyTemplate(fileNameTpl, rawVersion, item)
                    : downloadUrl[(downloadUrl.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            return new CompilerResolution(rawVersion, downloadUrl, fileName);
        }
        return null;
    }

    private async Task<CompilerResolution?> ResolveTextPointerAsync(Dictionary<string, string> p, CancellationToken ct)
    {
        if (!p.TryGetValue("url", out var urlTemplate) || string.IsNullOrWhiteSpace(urlTemplate)) return null;
        if (!p.TryGetValue("urlTemplate", out var downloadUrlTemplate) || !p.TryGetValue("fileNameTemplate", out var fileNameTemplate))
            return null;

        var url = SubstituteParams(urlTemplate, p);
        using var response = await MarketplaceHttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var version = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        var rejectContains = p.TryGetValue("rejectContains", out var rc) ? rc : "<";
        var maxLength = p.TryGetValue("maxLength", out var ml) && int.TryParse(ml, out var mlVal) ? mlVal : 32;
        if (string.IsNullOrWhiteSpace(version) || version.Length > maxLength ||
            (!string.IsNullOrEmpty(rejectContains) && version.Contains(rejectContains)))
            return null;

        var downloadUrl = SubstituteParams(downloadUrlTemplate, p).Replace("{version}", version);
        var fileName = SubstituteParams(fileNameTemplate, p).Replace("{version}", version);
        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(fileName)) return null;
        return new CompilerResolution(version, downloadUrl, fileName);
    }

    private async Task<CompilerResolution?> ResolveDirectoryListingAsync(Dictionary<string, string> p, CancellationToken ct)
    {
        if (!p.TryGetValue("indexUrl", out var indexUrl) ||
            !p.TryGetValue("entryPattern", out var entryPatternStr) ||
            !p.TryGetValue("downloadUrlTemplate", out var downloadUrlTemplate) ||
            !p.TryGetValue("fileNameTemplate", out var fileNameTemplate))
            return null;

        using var response = await MarketplaceHttpClient.GetAsync(indexUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var entryPattern = new Regex(entryPatternStr);
        var candidates = entryPattern.Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderByDescending(v => ParseVersionNumbers(v), VersionNumberSequenceComparer.Instance)
            .ToList();
        if (candidates.Count == 0) return null;

        foreach (var candidate in candidates)
        {
            var fileName = fileNameTemplate.Replace("{version}", candidate);
            var downloadUrl = downloadUrlTemplate.Replace("{version}", candidate);
            if (await HttpUrlExistsAsync(downloadUrl, ct).ConfigureAwait(false))
                return new CompilerResolution(candidate, downloadUrl, fileName);
        }
        return null;
    }

    private static async Task<bool> HttpUrlExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await MarketplaceHttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (headResponse.IsSuccessStatusCode)
                return true;
            if (headResponse.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                using var getResponse = await MarketplaceHttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return getResponse.IsSuccessStatusCode;
            }
            return false;
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Download existence check failed for '{url}'.", ex);
            return false;
        }
    }

    private static CompilerResolution? ResolveAlwaysLatest(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("downloadUrl", out var downloadUrl) || !p.TryGetValue("fileName", out var fileName) ||
            string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(fileName))
            return null;
        var versionLabel = p.TryGetValue("versionLabel", out var vl) && !string.IsNullOrWhiteSpace(vl) ? vl : "rolling";
        return new CompilerResolution(versionLabel, downloadUrl, fileName);
    }

    private static string SubstituteParams(string template, Dictionary<string, string> p)
    {
        return Regex.Replace(template, @"\{([a-zA-Z0-9_]+)\}", m =>
            p.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static string ApplyTemplate(string template, string version, JsonElement item)
    {
        var result = template.Replace("{version}", version);
        return Regex.Replace(result, @"\{field:([^}]+)\}", m => GetString(item, m.Groups[1].Value));
    }

    private static JsonElement? GetPath(JsonElement el, string path)
    {
        if (string.IsNullOrEmpty(path)) return el;
        var cur = el;
        foreach (var seg in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    private static string GetString(JsonElement el, string path)
    {
        var v = GetPath(el, path);
        if (v is null) return string.Empty;
        return v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => v.Value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool MatchesFilters(JsonElement item, string? filters)
    {
        if (string.IsNullOrWhiteSpace(filters)) return true;
        foreach (var clause in filters.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int opIndex;
            if ((opIndex = clause.IndexOf("!=", StringComparison.Ordinal)) >= 0)
            {
                var path = clause[..opIndex];
                var expected = clause[(opIndex + 2)..];
                if (string.Equals(GetString(item, path), expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if ((opIndex = clause.IndexOf("~=", StringComparison.Ordinal)) >= 0)
            {
                var path = clause[..opIndex];
                var expected = clause[(opIndex + 2)..];
                var actual = GetString(item, path);
                if (!string.IsNullOrEmpty(actual) && actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            else if ((opIndex = clause.IndexOf('=')) >= 0)
            {
                var path = clause[..opIndex];
                var expected = clause[(opIndex + 1)..];
                if (!string.Equals(GetString(item, path), expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return true;
    }

    private readonly Dictionary<string, string> _customRunCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _customBuildCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _compilerOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _customBuildScripts = new(StringComparer.OrdinalIgnoreCase);
    private const string CustomBuildScriptId = "custom:build-script";

    private string? _activeRunCommandLine;
    private string? _activeBuildCommandLine;
    private MarketplaceExtension? _activeCompilerExtension;
    private CompilerRunWindow? _runWindow;
    private CompilerRunWindow? _buildWindow;

    public MarketplaceExtension? ActiveCompilerExtension
    {
        get => _activeCompilerExtension;
        private set
        {
            if (ReferenceEquals(_activeCompilerExtension, value)) return;
            _activeCompilerExtension = value;
            OnPropertyChanged();
        }
    }

    public bool IsCompilerIconVisible => ActiveCompilerExtension is not null;

    public string ActiveCompilerDisplayName => ActiveCompilerExtension?.Name ?? "No compiler";

    public bool IsRunButtonEnabled => _currentFilePath is not null && _activeRunCommandLine is not null;
    public bool IsBuildButtonEnabled => _currentFilePath is not null && _activeBuildCommandLine is not null;

    public string? ActiveRunCommandText => _activeRunCommandLine;
    public string? ActiveBuildCommandText => _activeBuildCommandLine;

    public string RunBuildButtonTooltip
    {
        get
        {
            if (_currentFilePath is null)
                return "Open a file to run or build it.";

            var ext = Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant();
            var hasCustomBuild = !string.IsNullOrWhiteSpace(ext) && TryGetCustomBuildScript(ext, out var scriptPath);

            if (ActiveCompilerExtension is null && !hasCustomBuild)
                return "No compiler found for this file type. Install one from the Compilers marketplace, or set a command from the dropdown menu.";

            if (hasCustomBuild)
            {
                var runPart = string.IsNullOrWhiteSpace(_activeRunCommandLine)
                    ? null
                    : $"{ActiveCompilerExtension?.Name ?? "Run"} · {_activeRunCommandLine}";
                var buildPart = $"Custom Build Script · {_activeBuildCommandLine}";
                if (runPart is not null)
                    return $"{runPart}  |  {buildPart}";
                return buildPart;
            }

            var command = _activeRunCommandLine ?? _activeBuildCommandLine;
            return string.IsNullOrWhiteSpace(command)
                ? $"{ActiveCompilerExtension!.Name} - no command configured. Use the dropdown menu to set one."
                : $"{ActiveCompilerExtension!.Name} · {command}";
        }
    }

    private void RefreshRunBuildState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshRunBuildState);
            return;
        }

        ActiveCompilerExtension = ResolveActiveCompiler();
        var ext = Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant();

        _activeRunCommandLine = ActiveCompilerExtension is null
            ? null : BuildCommandLineText(ActiveCompilerExtension, isBuild: false);

        if (!string.IsNullOrWhiteSpace(ext) && TryGetCustomBuildScript(ext, out var scriptPath))
            _activeBuildCommandLine = BuildCustomScriptDisplayCommand(scriptPath, extraArgs: null);
        else
            _activeBuildCommandLine = ActiveCompilerExtension is null
                ? null : BuildCommandLineText(ActiveCompilerExtension, isBuild: true);

        RaiseMany(nameof(IsRunButtonEnabled), nameof(IsBuildButtonEnabled), nameof(IsCompilerIconVisible), nameof(ActiveCompilerDisplayName), nameof(RunBuildButtonTooltip), nameof(ActiveRunCommandText), nameof(ActiveBuildCommandText));
    }

    private MarketplaceExtension? ResolveActiveCompiler()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
            return null;

        var ext = Path.GetExtension(_currentFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            var emptyFolder = ResolveWorkingDirectory();
            if (!string.IsNullOrWhiteSpace(emptyFolder) && TryGetProjectFallbackExtension(emptyFolder, ext, out var emptyFallback))
                return emptyFallback;
            return null;
        }

        if (_compilerOverrides.TryGetValue(ext, out var overrideId))
        {
            var overridden = FindCompilerByIdForOverride(overrideId);
            if (overridden is not null)
                return overridden;
        }

        var candidates = CompilerExtensions
            .Where(c => c.FileExtensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0)
        {
            var folder = ResolveWorkingDirectory();
            if (!string.IsNullOrWhiteSpace(folder) && TryGetProjectFallbackExtension(folder, ext, out var fallback))
                return fallback;
            return null;
        }

        var scored = candidates
            .Select(c => (Compiler: c, Score: ScoreCompilerForExtension(c, ext)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => candidates.IndexOf(x.Compiler))
            .ToList();
        var best = scored.FirstOrDefault();
        if (best.Compiler is null || best.Score <= 0)
        {
            var folder = ResolveWorkingDirectory();
            if (!string.IsNullOrWhiteSpace(folder) && TryGetProjectFallbackExtension(folder, ext, out var fallbackScoreZero))
                return fallbackScoreZero;
            return null;
        }

        if (ResolveCommandTemplate(best.Compiler, isBuild: false, ext) is null &&
            ResolveCommandTemplate(best.Compiler, isBuild: true, ext) is null)
        {
            var folder = ResolveWorkingDirectory();
            if (!string.IsNullOrWhiteSpace(folder) && TryGetProjectFallbackExtension(folder, ext, out var projectFallback))
                return projectFallback;
        }

        return best.Compiler;
    }

    private bool TryGetProjectFallbackExtension(string folder, string forExt, out MarketplaceExtension fallback)
    {
        fallback = null!;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return false;

        var fileFolder = Path.GetDirectoryName(_currentFilePath ?? string.Empty);
        var projectRoot = !string.IsNullOrWhiteSpace(fileFolder) && Directory.Exists(fileFolder)
            ? FindProjectRoot(fileFolder)
            : FindProjectRoot(folder);

        projectRoot ??= FindProjectRoot(folder);
        if (projectRoot is null)
            return false;

        if (HasDotnetProject(projectRoot))
        {
            fallback = BuildProjectFallbackExtension(
                id: "project:dotnet",
                name: ".NET Project",
                author: "Project",
                run: "dotnet run",
                build: "dotnet build",
                iconSourceId: "dotnet-sdk",
                forExt: forExt,
                folder: projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "Cargo.toml")))
        {
            fallback = BuildProjectFallbackExtension("project:cargo", "Cargo Project", "Project", "cargo run", "cargo build", "rust-rustup", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "go.mod")))
        {
            fallback = BuildProjectFallbackExtension("project:go", "Go Module", "Project", "go run .", "go build ./...", "go", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "package.json")))
        {
            fallback = BuildProjectFallbackExtension("project:node", "Node Project", "Project", "npm start", "npm run build", "nodejs", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "pom.xml")))
        {
            fallback = BuildProjectFallbackExtension("project:maven", "Maven Project", "Project", "mvn exec:java", "mvn package", "temurin-jdk", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "build.gradle")) || File.Exists(Path.Combine(projectRoot, "build.gradle.kts")))
        {
            fallback = BuildProjectFallbackExtension("project:gradle", "Gradle Project", "Project", "gradle run", "gradle build", "temurin-jdk", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "CMakeLists.txt")))
        {
            fallback = BuildProjectFallbackExtension("project:cmake", "CMake Project", "Project", null, "cmake --build build", "llvm-clang", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "Makefile")) || File.Exists(Path.Combine(projectRoot, "makefile")) || File.Exists(Path.Combine(projectRoot, "GNUmakefile")))
        {
            fallback = BuildProjectFallbackExtension("project:make", "Make Project", "Project", "make run", "make", "msys2-mingw", forExt, projectRoot);
            return true;
        }

        if (File.Exists(Path.Combine(projectRoot, "pyproject.toml")) || File.Exists(Path.Combine(projectRoot, "requirements.txt")) || File.Exists(Path.Combine(projectRoot, "setup.py")) || File.Exists(Path.Combine(projectRoot, "Pipfile")))
        {
            fallback = BuildProjectFallbackExtension("project:python", "Python Project", "Project", "python {file}", null, "python", forExt, projectRoot);
            return true;
        }

        return false;
    }

    private string? FindProjectRoot(string startFolder)
    {
        try
        {
            var current = Path.GetFullPath(startFolder);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (HasDotnetProject(current) ||
                    File.Exists(Path.Combine(current, "Cargo.toml")) ||
                    File.Exists(Path.Combine(current, "go.mod")) ||
                    File.Exists(Path.Combine(current, "package.json")) ||
                    File.Exists(Path.Combine(current, "pom.xml")) ||
                    File.Exists(Path.Combine(current, "build.gradle")) ||
                    File.Exists(Path.Combine(current, "build.gradle.kts")) ||
                    File.Exists(Path.Combine(current, "CMakeLists.txt")) ||
                    File.Exists(Path.Combine(current, "Makefile")) ||
                    File.Exists(Path.Combine(current, "makefile")) ||
                    File.Exists(Path.Combine(current, "GNUmakefile")) ||
                    File.Exists(Path.Combine(current, "pyproject.toml")) ||
                    File.Exists(Path.Combine(current, "requirements.txt")) ||
                    File.Exists(Path.Combine(current, "setup.py")) ||
                    File.Exists(Path.Combine(current, "Pipfile")))
                {
                    return current;
                }

                var parent = Directory.GetParent(current);
                if (parent is null)
                    break;

                current = parent.FullName;
            }
        }
        catch
        {
        }

        return null;
    }

    private bool HasDotnetProject(string folder)
    {
        try
        {
            if (Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.EnumerateFiles(folder, "*.fsproj", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.EnumerateFiles(folder, "*.vbproj", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.EnumerateFiles(folder, "*.sln", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.EnumerateFiles(folder, "*.slnx", SearchOption.TopDirectoryOnly).Any()) return true;

            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("packages", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Directory.EnumerateFiles(sub, "*.csproj", SearchOption.TopDirectoryOnly).Any()) return true;
                if (Directory.EnumerateFiles(sub, "*.fsproj", SearchOption.TopDirectoryOnly).Any()) return true;
                if (Directory.EnumerateFiles(sub, "*.vbproj", SearchOption.TopDirectoryOnly).Any()) return true;
                if (Directory.EnumerateFiles(sub, "*.sln", SearchOption.TopDirectoryOnly).Any()) return true;
            }
        }
        catch { }
        return false;
    }

    private MarketplaceExtension BuildProjectFallbackExtension(string id, string name, string author, string? run, string? build, string iconSourceId, string forExt, string folder)
    {
        var iconSource = CompilerExtensions.FirstOrDefault(c => c.Id.Equals(iconSourceId, StringComparison.OrdinalIgnoreCase))
            ?? ManualCompilerExtensions.FirstOrDefault(c => c.Id.Equals(iconSourceId, StringComparison.OrdinalIgnoreCase));
        var iconUrl = iconSource?.IconUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            iconUrl = ManualCompilerExtensions.FirstOrDefault(c => c.Id.Equals(iconSourceId, StringComparison.OrdinalIgnoreCase))?.IconUrl ?? string.Empty;
        }

        var ext = new MarketplaceExtension
        {
            Id = id,
            Name = name,
            Type = "compiler",
            Author = author,
            Description = folder,
            Version = "Project",
            DownloadUrl = string.Empty,
            FileName = string.Empty,
            IconUrl = iconUrl,
            FileExtensions = string.IsNullOrWhiteSpace(forExt) ? [] : [forExt],
            LanguageExtensionIds = [],
            RunCommandTemplate = run,
            BuildCommandTemplate = build,
            IconImage = iconSource?.IconImage,
            SvgData = iconSource?.SvgData,
        };
        ext.SetCompilerInstalledState("Project", DateTime.UtcNow, isUpdateAvailable: false);
        return ext;
    }

    private bool TryGetCustomBuildScript(string ext, out string scriptPath)
    {
        if (_customBuildScripts.TryGetValue(ext, out var stored) && !string.IsNullOrWhiteSpace(stored) && File.Exists(stored))
        {
            scriptPath = stored;
            return true;
        }

        scriptPath = string.Empty;
        return false;
    }

    private MarketplaceExtension BuildCustomBuildScriptExtension(string ext, string scriptPath)
    {
        var fileName = Path.GetFileName(scriptPath);
        var extension = new MarketplaceExtension
        {
            Id = CustomBuildScriptId,
            Name = "Custom Build Script",
            Type = "compiler",
            Author = "Local",
            Description = scriptPath,
            Version = string.IsNullOrWhiteSpace(fileName) ? "Local" : fileName,
            DownloadUrl = string.Empty,
            FileName = fileName,
            IconUrl = string.Empty,
            FileExtensions = [ext],
            LanguageExtensionIds = [],
            RunCommandTemplate = QuoteArgument(scriptPath),
            BuildCommandTemplate = QuoteArgument(scriptPath),
        };
        extension.SetCompilerInstalledState("Local", DateTime.UtcNow, isUpdateAvailable: false);
        return extension;
    }

    private string BuildCustomScriptDisplayCommand(string scriptPath, string? extraArgs)
    {
        var baseCmd = QuoteArgument(scriptPath);
        var expanded = ExpandCommandTemplate(baseCmd, extraArgs);
        return expanded;
    }

    private (string Exe, string Args) ResolveCustomScriptExecutable(string scriptPath, string? extraArgs)
    {
        var quotedPath = QuoteArgument(scriptPath);
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        var expandedExtra = ExpandExtraArgsPlaceholders(extraArgs);
        var extra = string.IsNullOrWhiteSpace(expandedExtra) ? string.Empty : $" {expandedExtra.Trim()}";
        var quotedFile = _currentFilePath is not null ? QuoteArgument(_currentFilePath) : string.Empty;
        var alreadyHasFileArg = !string.IsNullOrWhiteSpace(expandedExtra) && !string.IsNullOrWhiteSpace(quotedFile) && expandedExtra.Contains(quotedFile, StringComparison.Ordinal);
        var fileArg = !string.IsNullOrWhiteSpace(quotedFile) && !alreadyHasFileArg ? $" {quotedFile}" : string.Empty;
        if (ext is ".bat" or ".cmd")
            return ("cmd.exe", $"/c {quotedPath}{extra}{fileArg}");
        if (ext is ".ps1")
            return ("powershell.exe", $"-ExecutionPolicy Bypass -File {quotedPath}{extra}{fileArg}");
        if (ext is ".sh")
            return ("bash", $"{quotedPath}{extra}{fileArg}");
        if (ext is ".py" or ".pyw")
            return ("python", $"{quotedPath}{extra}{fileArg}");
        var args = fileArg.Length > 0 ? $"{expandedExtra.Trim()} {quotedFile}".Trim() : expandedExtra.Trim();
        if (string.IsNullOrWhiteSpace(args) && !alreadyHasFileArg)
            args = quotedFile;
        return (scriptPath, args);
    }

    private string ExpandExtraArgsPlaceholders(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs))
            return string.Empty;

        var filePath = _currentFilePath ?? string.Empty;
        var fileName = Path.GetFileName(filePath);
        var name = Path.GetFileNameWithoutExtension(filePath);
        var folder = ResolveWorkingDirectory();

        return extraArgs
            .Replace("{fileName}", QuoteArgument(fileName))
            .Replace("{file}", QuoteArgument(filePath))
            .Replace("{name}", QuoteArgument(name))
            .Replace("{folder}", QuoteArgument(folder))
            .Replace("{args}", string.Empty);
    }

    private MarketplaceExtension? FindCompilerByIdForOverride(string id) =>
        CompilerExtensions.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? CompilerExtensions.Where(e => e.IsInstalled)
            .Concat(ManualCompilerExtensions)
            .FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private int ScoreCompilerForExtension(MarketplaceExtension compiler, string ext)
    {
        var template = ResolveCommandTemplate(compiler, isBuild: false, ext)
                       ?? ResolveCommandTemplate(compiler, isBuild: true, ext);
        if (string.IsNullOrWhiteSpace(template))
            return 0;

        var (exe, _) = SplitCommandLine(template);
        if (string.IsNullOrWhiteSpace(exe))
            return 0;

        if (compiler.IsInstalled)
            return 1;

        var exeName = NormalizeExeName(Path.GetFileName(exe));
        foreach (var record in _manualCompilers.Values)
        {
            if (Path.GetFileName(record.ExecutablePath).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                return 2;
        }

        return ResolveToolExecutable(exe) is not null ? 1 : 0;
    }

    private string? ResolveCommandTemplate(MarketplaceExtension compiler, bool isBuild, string ext)
    {
        if (compiler.Id.Equals(CustomBuildScriptId, StringComparison.OrdinalIgnoreCase))
        {
            if (_customBuildScripts.TryGetValue(ext, out var scriptPath) && !string.IsNullOrWhiteSpace(scriptPath))
                return QuoteArgument(scriptPath);
            return null;
        }

        var custom = isBuild ? _customBuildCommands : _customRunCommands;
        if (custom.TryGetValue(compiler.Id, out var customCommand) && !string.IsNullOrWhiteSpace(customCommand))
            return customCommand;

        if (compiler.FileCommands is not null &&
            compiler.FileCommands.TryGetValue(ext, out var fileCommand))
            return isBuild ? fileCommand.Build : fileCommand.Run;

        var template = isBuild ? compiler.BuildCommandTemplate : compiler.RunCommandTemplate;
        if (!string.IsNullOrWhiteSpace(template))
            return template;

        if (IsManuallyTrackedCompilerId(compiler.Id) &&
            _manualCompilers.TryGetValue(compiler.Id, out var record) &&
            record.CanonicalCompilerId is { } canonicalId)
        {
            var canonical = CompilerExtensions.FirstOrDefault(c =>
                c.Id.Equals(canonicalId, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
                return ResolveCommandTemplate(canonical, isBuild, ext);
        }

        return null;
    }

    private string? BuildCommandLineText(MarketplaceExtension compiler, bool isBuild)
    {
        var ext = Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant();
        if (compiler.Id.Equals(CustomBuildScriptId, StringComparison.OrdinalIgnoreCase))
        {
            if (_customBuildScripts.TryGetValue(ext, out var scriptPath) && !string.IsNullOrWhiteSpace(scriptPath))
                return BuildCustomScriptDisplayCommand(scriptPath, extraArgs: null);
            return null;
        }

        var template = ResolveCommandTemplate(compiler, isBuild, ext);
        return template is null ? null : ExpandCommandTemplate(template, extraArgs: null);
    }

    private string ResolveWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
        {
            var fileFolder = Path.GetDirectoryName(_currentFilePath);
            if (!string.IsNullOrWhiteSpace(fileFolder) && Directory.Exists(fileFolder))
                return fileFolder;
        }

        return _currentFolderPath ?? string.Empty;
    }

    private string ExpandCommandTemplate(string template, string? extraArgs)
    {
        var filePath = _currentFilePath ?? string.Empty;
        var fileName = Path.GetFileName(filePath);
        var name = Path.GetFileNameWithoutExtension(filePath);
        var folder = ResolveWorkingDirectory();

        var expanded = template
            .Replace("{fileName}", QuoteArgument(fileName))
            .Replace("{file}", QuoteArgument(filePath))
            .Replace("{name}", QuoteArgument(name))
            .Replace("{folder}", QuoteArgument(folder))
            .Replace("{args}", (extraArgs ?? string.Empty).Trim());

        if (!string.IsNullOrWhiteSpace(extraArgs) && !template.Contains("{args}", StringComparison.Ordinal))
            expanded = $"{expanded} {extraArgs.Trim()}";

        return expanded;
    }

    private string? ResolveToolExecutable(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return null;

        if (Path.IsPathFullyQualified(toolName) && File.Exists(toolName))
            return toolName;

        var exeName = NormalizeExeName(Path.GetFileName(toolName));

        foreach (var record in _manualCompilers.Values)
        {
            if (Path.GetFileName(record.ExecutablePath).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                return record.ExecutablePath;
        }

        foreach (var record in _manualCompilers.Values)
        {
            var directory = Path.GetDirectoryName(record.ExecutablePath);
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var sibling = Path.Combine(directory, exeName);
            if (File.Exists(sibling))
                return sibling;
        }

        if (exeName.Equals("go.exe", StringComparison.OrdinalIgnoreCase))
        {
            var goPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Go", "bin", "go.exe");
            if (File.Exists(goPath)) return goPath;
            goPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Go", "bin", "go.exe");
            if (File.Exists(goPath)) return goPath;
            var localGo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Go", "bin", "go.exe");
            if (File.Exists(localGo)) return localGo;
        }

        return TryFindOnPath(exeName) ?? TryFindOnPath(toolName);
    }

    private static string NormalizeExeName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return toolName;
        return toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe";
    }

    private static (string Exe, string Arguments) SplitCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 0)
                return (trimmed[1..closingQuote], trimmed[(closingQuote + 1)..].Trim());
        }

        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace < 0
            ? (trimmed, string.Empty)
            : (trimmed[..firstSpace], trimmed[(firstSpace + 1)..].Trim());
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.StartsWith('"') && value.EndsWith('"')) return value;
        return value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
    }

    private async Task EnsureGoModExistsAsync(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return;
        var current = workingDirectory;
        for (var i = 0; i < 4; i++)
        {
            if (File.Exists(Path.Combine(current, "go.mod")))
                return;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
        var moduleName = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(moduleName) || moduleName.Length < 2)
            moduleName = "hello";
        moduleName = System.Text.RegularExpressions.Regex.Replace(moduleName, @"[^a-zA-Z0-9\.\-_]", "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(moduleName))
            moduleName = "hello";
        moduleName = $"example.com/{moduleName}";
        var goExe = ResolveToolExecutable("go") ?? "go";
        try
        {
            using var initProc = Process.Start(new ProcessStartInfo(goExe, $"mod init {moduleName}")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (initProc is not null)
            {
                await initProc.WaitForExitAsync();
                try
                {
                    using var tidyProc = Process.Start(new ProcessStartInfo(goExe, "mod tidy")
                    {
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (tidyProc is not null)
                        await tidyProc.WaitForExitAsync();
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("EnsureGoModExistsAsync failed", ex);
        }
    }

    private async Task ExecuteCurrentCommandAsync(bool isBuild, string? extraArgs)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await ShowWarningDialogAsync("Run / Build",
                new InvalidOperationException("The terminal is only available on Windows."));
            return;
        }

        if (_currentFilePath is null)
            return;

        var ext = Path.GetExtension(_currentFilePath).ToLowerInvariant();

        if (ActiveCompilerExtension is null && !(isBuild && TryGetCustomBuildScript(ext, out _)))
            return;

        var compiler = ActiveCompilerExtension;
        string commandLine;
        string exe;
        string args;
        string toolLabel;

        if (isBuild && TryGetCustomBuildScript(ext, out var buildScriptPath))
        {
            commandLine = BuildCustomScriptDisplayCommand(buildScriptPath, extraArgs);
            var resolved = ResolveCustomScriptExecutable(buildScriptPath, extraArgs);
            exe = resolved.Exe;
            args = resolved.Args;
            toolLabel = Path.GetFileNameWithoutExtension(buildScriptPath);
        }
        else
        {
            if (ActiveCompilerExtension is null)
                return;

            var template = ResolveCommandTemplate(compiler!, isBuild, ext);
            if (template is null)
                return;

            commandLine = ExpandCommandTemplate(template, extraArgs);
            var split = SplitCommandLine(commandLine);
            if (string.IsNullOrWhiteSpace(split.Exe))
                return;

            var resolvedExe = ResolveToolExecutable(split.Exe) ?? split.Exe;
            exe = resolvedExe;
            args = split.Arguments;
            toolLabel = Path.GetFileName(split.Exe);
        }

        var workingDirectory = ResolveWorkingDirectory();

        if (ext.Equals(".go", StringComparison.OrdinalIgnoreCase) &&
            compiler is not null &&
            (compiler.Id.Equals("go", StringComparison.OrdinalIgnoreCase) || compiler.Id.Equals("project:go", StringComparison.OrdinalIgnoreCase)))
        {
            await EnsureGoModExistsAsync(workingDirectory);
        }

        var existing = isBuild ? _buildWindow : _runWindow;
        if (existing is { IsVisible: true })
        {
            existing.Activate();
            return;
        }

        var window = new CompilerRunWindow(
            isBuild ? $"Kodo - Build ({toolLabel})" : $"Kodo - Run ({toolLabel})",
            commandLine,
            exe,
            args,
            workingDirectory)
        {
            TerminalKeybinds = _keybinds,
        };

        if (isBuild) _buildWindow = window; else _runWindow = window;
        window.Closed += (_, _) => { if (isBuild) _buildWindow = null; else _runWindow = null; };
        window.Show();
    }

    private async void RunButton_OnClick(object? sender, RoutedEventArgs e) =>
        await ExecuteCurrentCommandAsync(isBuild: false, extraArgs: null);

    private async void BuildButton_OnClick(object? sender, RoutedEventArgs e) =>
        await ExecuteCurrentCommandAsync(isBuild: true, extraArgs: null);

    private void RunMenuButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            BuildRunBuildMenu(isBuild: false).ShowAt(button);
    }

    private void BuildMenuButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            BuildRunBuildMenu(isBuild: true).ShowAt(button);
    }

    private void CompilerIconButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            BuildCompilerSwitchMenu().ShowAt(button);
    }

    private MenuFlyout BuildRunBuildMenu(bool isBuild)
    {
        var compiler = ActiveCompilerExtension;
        var action = isBuild ? "Build" : "Run";
        var enabled = isBuild ? IsBuildButtonEnabled : IsRunButtonEnabled;

        var menu = new MenuFlyout();

        var primary = new MenuItem { Header = action, IsEnabled = enabled };
        primary.Click += (_, _) => _ = ExecuteCurrentCommandAsync(isBuild, extraArgs: null);
        menu.Items.Add(primary);

        var withArgs = new MenuItem { Header = $"{action} with arguments...", IsEnabled = enabled };
        withArgs.Click += async (_, _) =>
        {
            var args = await ShowTextInputDialogAsync(
                $"{action} with arguments",
                "Extra arguments to append to the command (flags, file paths, etc.):",
                string.Empty);
            if (args is not null)
                await ExecuteCurrentCommandAsync(isBuild, args);
        };
        menu.Items.Add(withArgs);

        menu.Items.Add(new Separator());

        if (compiler is not null || (isBuild && TryGetCustomBuildScript(Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant(), out _)))
        {
            string? commandLine;
            string summary;
            if (isBuild && TryGetCustomBuildScript(Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant(), out var customScriptForSummary))
            {
                commandLine = _activeBuildCommandLine;
                summary = $"Custom Build Script  ·  {commandLine}";
            }
            else
            {
                commandLine = isBuild ? _activeBuildCommandLine : _activeRunCommandLine;
                var label = compiler?.Name ?? "Custom Build Script";
                summary = $"{label}  ·  {commandLine}";
            }
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                var infoItem = new MenuItem
                {
                    Header = BuildCompilerMenuHeader(summary),
                    IsEnabled = false,
                };
                ToolTip.SetTip(infoItem, summary);
                menu.Items.Add(infoItem);
                menu.Items.Add(new Separator());
            }
        }

        if (isBuild)
        {
            var ext = Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant();
            var hasCustomScript = TryGetCustomBuildScript(ext, out var existingScript);
            if (!hasCustomScript)
            {
                var setScript = new MenuItem { Header = "Set Custom Build Script..." };
                setScript.Click += async (_, _) => await PromptForCustomBuildScriptAsync(ext);
                menu.Items.Add(setScript);
            }
            else
            {
                var changeScript = new MenuItem { Header = $"Change Custom Build Script \u00b7 {Path.GetFileName(existingScript)}" };
                ToolTip.SetTip(changeScript, existingScript);
                changeScript.Click += async (_, _) => await PromptForCustomBuildScriptAsync(ext);
                menu.Items.Add(changeScript);

                var removeScript = new MenuItem { Header = "Remove Custom Build Script" };
                removeScript.Click += (_, _) =>
                {
                    _customBuildScripts.Remove(ext);
                    SaveSettings();
                    RefreshRunBuildState();
                };
                menu.Items.Add(removeScript);
            }
            menu.Items.Add(new Separator());
        }

        var custom = isBuild ? _customBuildCommands : _customRunCommands;
        var hasCustom = compiler is not null && custom.ContainsKey(compiler.Id);

        if (!hasCustom)
        {
            var setCustom = new MenuItem
            {
                Header = isBuild ? "Set custom build command..." : "Set custom run command...",
                IsEnabled = compiler is not null,
            };
            setCustom.Click += async (_, _) =>
            {
                if (ActiveCompilerExtension is not { } current)
                    return;

                var ext = Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant();
                var currentTemplate = ResolveCommandTemplate(current, isBuild, ext);
                var entered = await ShowTextInputDialogAsync(
                    isBuild ? "Custom build command" : "Custom run command",
                    "Full command to run. Use {file}, {name}, {folder} and {args} as placeholders.",
                    currentTemplate ?? string.Empty);

                if (string.IsNullOrWhiteSpace(entered))
                    return;

                (isBuild ? _customBuildCommands : _customRunCommands)[current.Id] = entered;
                SaveSettings();
                RefreshRunBuildState();
            };
            menu.Items.Add(setCustom);
        }
        else
        {
            var clearCustom = new MenuItem
            {
                Header = isBuild ? "Clear custom build command" : "Clear custom run command",
            };
            clearCustom.Click += (_, _) =>
            {
                if (ActiveCompilerExtension is not { } current)
                    return;

                (isBuild ? _customBuildCommands : _customRunCommands).Remove(current.Id);
                SaveSettings();
                RefreshRunBuildState();
            };
            menu.Items.Add(clearCustom);
        }

        return menu;
    }

    private MenuFlyout BuildCompilerSwitchMenu()
    {
        var ext = Path.GetExtension(_currentFilePath ?? string.Empty).ToLowerInvariant();
        var menu = new MenuFlyout();

        var automatic = new MenuItem { Header = "Automatic (recommended)" };
        automatic.Click += (_, _) =>
        {
            _compilerOverrides.Remove(ext);
            _customBuildScripts.Remove(ext);
            SaveSettings();
            RefreshRunBuildState();
        };
        menu.Items.Add(automatic);
        menu.Items.Add(new Separator());

        var candidates = CompilerExtensions
            .Where(c => c.FileExtensions.Any(fe => fe.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var installed = FilteredInstalledCompilerExtensions
            .Where(c => candidates.All(existing => !existing.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var anyAdded = false;
        foreach (var compiler in candidates.Concat(installed))
        {
            anyAdded = true;
            var compilerId = compiler.Id;
            var item = new MenuItem
            {
                Header = BuildCompilerMenuHeader(compiler.Name),
                Icon = BuildCompilerMenuIcon(compiler),
                IsChecked = ActiveCompilerExtension is { } active &&
                            active.Id.Equals(compilerId, StringComparison.OrdinalIgnoreCase),
            };
            ToolTip.SetTip(item, compiler.Name);
            item.Click += (_, _) =>
            {
                _compilerOverrides[ext] = compilerId;
                SaveSettings();
                RefreshRunBuildState();
            };
            menu.Items.Add(item);
        }

        {
            var folder = ResolveWorkingDirectory();
            if (!string.IsNullOrWhiteSpace(folder) && TryGetProjectFallbackExtension(folder, ext, out var projectFallback))
            {
                var projItem = new MenuItem
                {
                    Header = BuildCompilerMenuHeader(projectFallback.Name),
                    Icon = BuildCompilerMenuIcon(projectFallback),
                    IsChecked = ActiveCompilerExtension is { } active && active.Id.Equals(projectFallback.Id, StringComparison.OrdinalIgnoreCase),
                };
                ToolTip.SetTip(projItem, projectFallback.Description);
                projItem.Click += (_, _) =>
                {
                    _compilerOverrides.Remove(ext);
                    SaveSettings();
                    RefreshRunBuildState();
                };
                menu.Items.Add(projItem);
                anyAdded = true;
            }
        }

        if (!anyAdded)
            menu.Items.Add(new MenuItem { Header = "No compilers available", IsEnabled = false });

        menu.Items.Add(new Separator());

        var hasCustomScript = TryGetCustomBuildScript(ext, out var existingScript);
        var customLabel = hasCustomScript ? $"Custom Build Script \u00b7 {Path.GetFileName(existingScript)}" : "Custom Build Script...";
        var customItem = new MenuItem
        {
            Header = BuildCompilerMenuHeader(customLabel),
            IsChecked = hasCustomScript,
            IsEnabled = !string.IsNullOrWhiteSpace(ext),
        };
        if (hasCustomScript)
            ToolTip.SetTip(customItem, existingScript);
        else if (string.IsNullOrWhiteSpace(ext))
            ToolTip.SetTip(customItem, "Open a file with an extension to set a custom build script.");
        else
            ToolTip.SetTip(customItem, "Pick a .bat, .cmd, .ps1, .sh or any executable to use for this file type.");
        customItem.Click += async (_, _) => await PromptForCustomBuildScriptAsync(ext);
        menu.Items.Add(customItem);

        if (hasCustomScript)
        {
            var removeItem = new MenuItem { Header = "Remove Custom Build Script" };
            removeItem.Click += (_, _) =>
            {
                _customBuildScripts.Remove(ext);
                SaveSettings();
                RefreshRunBuildState();
            };
            menu.Items.Add(removeItem);
        }

        return menu;
    }

    private async Task PromptForCustomBuildScriptAsync(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = $"Select Custom Build Script for {ext}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Build Scripts")
                {
                    Patterns = ["*.bat", "*.cmd", "*.ps1", "*.sh", "*.py", "*.exe", "*.bin", "*.command"],
                },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files")
                {
                    Patterns = ["*"],
                },
            ],
        };

        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> picked;
        try
        {
            picked = await StorageProvider.OpenFilePickerAsync(options);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Custom build script file picker failed.", ex);
            return;
        }

        var file = picked.Count > 0 ? picked[0] : null;
        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        _customBuildScripts[ext] = path;
        SaveSettings();
        RefreshRunBuildState();
    }

    private static Control BuildCompilerMenuHeader(string text) => new TextBlock
    {
        Text = text,
        MaxWidth = 280,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static Control BuildCompilerMenuIcon(MarketplaceExtension compiler)
    {
        var icon = new StackPanel
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (compiler.IconImage is not null)
        {
            icon.Children.Add(new Image { Source = compiler.IconImage, Width = 18, Height = 18, Stretch = Stretch.Uniform });
            return icon;
        }

        if (!string.IsNullOrWhiteSpace(compiler.SvgData))
        {
            try
            {
                var svg = new Avalonia.Svg.Skia.Svg(new Uri("https://kodo.local/"))
                {
                    Source = compiler.SvgData,
                    Width = 18,
                    Height = 18,
                    Stretch = Avalonia.Media.Stretch.Uniform
                };
                icon.Children.Add(svg);
                return icon;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"BuildCompilerMenuIcon SVG failed for '{compiler.Id}'.", ex);
            }
        }

        return icon;
    }

    private async Task<string?> ShowTextInputDialogAsync(string title, string prompt, string initialValue)
    {
        string? result = null;
        Window? dialog = null;

        var inputBox = new TextBox
        {
            Text = initialValue,
            Background = ButtonBrush,
            Foreground = PrimaryTextBrush,
            BorderBrush = SurfaceBorderBrush,
            Padding = new Thickness(8, 6),
            FontSize = 14,
            CaretBrush = PrimaryTextBrush,
        };

        var confirmButton = CreateDialogButton("OK", AccentBrush, AccentBrush, AccentForegroundBrush, () =>
        {
            result = inputBox.Text;
            dialog!.Close();
        });

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = inputBox.Text; dialog!.Close(); }
            if (e.Key == Key.Escape) { dialog!.Close(); }
        };

        var headerRow2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Border
                {
                    Width = 3,
                    Height = 16,
                    Background = AccentBrush,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var divider2 = new Border
        {
            Height = 1,
            Background = SurfaceBorderBrush,
            Opacity = 0.9,
            Margin = new Thickness(0, 4)
        };

        var inner2 = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                headerRow2,
                new TextBlock
                {
                    Text = prompt,
                    FontSize = 13,
                    Foreground = MutedTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
                inputBox,
                divider2,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        CreateDialogButton("Cancel", ButtonBrush, SurfaceBorderBrush, PrimaryTextBrush, () => dialog!.Close()),
                        confirmButton,
                    },
                },
            },
        };

        dialog = new Window
        {
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MinWidth = 380,
            MaxHeight = 360,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
            Background = WindowBackgroundBrush,
            Content = new Border
            {
                Background = CardBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                Child = inner2
            }
        };

        dialog.Opened += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        await dialog.ShowDialog(this);
        return result;
    }

}

internal sealed class CompilerRunWindow : Window
{
    private readonly ConsoleTerminal _terminal = new();
    private readonly string _exePath;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private readonly string _commandDisplay;
    private readonly Color _accent;
    private readonly Color _accentForeground;
    private TextBlock _statusText = null!;
    private Button _rerunButton = null!;

    public IReadOnlyDictionary<string, KeyGesture>? TerminalKeybinds
    {
        get => _terminal.Keybinds;
        set => _terminal.Keybinds = value;
    }

    public CompilerRunWindow(string title, string commandDisplay, string exePath, string arguments, string workingDirectory)
    {
        _commandDisplay = commandDisplay;
        _exePath = exePath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        (_accent, _accentForeground) = AccentResolver.GetCurrentAccent();

        Title = title;
        Width = 780;
        Height = 500;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(DialogPalette.Surface);
        Content = BuildContent();

        Opened += OnOpened;
        Closed += (_, _) => _terminal.Stop();
    }

    private Control BuildContent()
    {
        _statusText = new TextBlock
        {
            Text = "Starting...",
            FontSize = 12,
            Foreground = new SolidColorBrush(DialogPalette.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _rerunButton = new Button
        {
            Content = "Re-run",
            Padding = new Thickness(14, 6),
            Background = new SolidColorBrush(_accent),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Focusable = false,
        };
        _rerunButton.Click += (_, _) => RunCommand();

        var exitButton = new Button
        {
            Content = "Exit",
            Padding = new Thickness(14, 6),
            Background = new SolidColorBrush(DialogPalette.BadgeBg),
            Foreground = new SolidColorBrush(DialogPalette.TextMuted),
            BorderBrush = new SolidColorBrush(DialogPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Focusable = false,
        };
        exitButton.Click += (_, _) => Close();

        var commandText = new TextBlock
        {
            Text = _commandDisplay,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(DialogPalette.Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(commandText, _commandDisplay);

        var commandBlock = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { commandText, _statusText },
        };

        var header = new Border
        {
            Background = new SolidColorBrush(DialogPalette.SurfaceDeep),
            BorderBrush = new SolidColorBrush(DialogPalette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    new Border
                    {
                        Width = 4,
                        Height = 28,
                        CornerRadius = new CornerRadius(2),
                        Background = new SolidColorBrush(_accent),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    SetGridColumn(commandBlock, 1),
                    SetGridColumn(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _rerunButton, exitButton },
                    }, 2),
                },
            },
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { header, SetGridRow(_terminal, 1) },
        };

        return layout;
    }

    private void OnOpened(object? sender, EventArgs e) => RunCommand();

    private void RunCommand()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _statusText.Text = "Terminal is only available on Windows.";
            return;
        }

        _statusText.Text = "Running...";
        _rerunButton.IsEnabled = false;

        _terminal.Start(_exePath, _arguments, _workingDirectory);

        var watchedHandle = _terminal.CurrentProcessHandle;
        _terminal.SessionExited += OnSessionExited;

        void OnSessionExited(object? s, IntPtr exitedHandle)
        {
            _terminal.SessionExited -= OnSessionExited;
            if (exitedHandle != watchedHandle)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                _statusText.Text = "Finished - the command exited. You can inspect the output below or re-run it.";
                _rerunButton.IsEnabled = true;
            });
        }

        Dispatcher.UIThread.Post(() => _terminal.Focus(), DispatcherPriority.Input);
    }

    private static Control SetGridColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control SetGridRow(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }
}
