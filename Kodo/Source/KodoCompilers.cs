using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Win32;
using Kodo.Models;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kodo;

// Persisted record of a locally-installed compiler, keyed by compiler id in installed-compilers.json.
public sealed class InstalledCompilerRecord
{
    public string Version { get; set; } = string.Empty;
    public DateTime InstalledOnUtc { get; set; }
    public string InstalledExePath { get; set; } = string.Empty;
}

// A compiler the user pointed Kodo at directly (or that Kodo found on this machine by itself),
// as opposed to one installed through CompilerIndex.json. These have no installer/updater of
// their own - Kodo is just remembering where they are.
public sealed class ManualCompilerRecord
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime AddedOnUtc { get; set; }
    public bool AutoDetected { get; set; }

    // Matches this record back to an entry in CompilerIndex.json (e.g. "dotnet-sdk", "python")
    // so the Installed tab can show the same icon as the Marketplace/Compilers tab instead of
    // falling back to a text abbreviation. Null when there's no known match (e.g. MSVC's cl.exe,
    // or a manually browsed-to executable Kodo doesn't recognize).
    public string? CanonicalCompilerId { get; set; }
}

// Root object for manual-compilers.json. DismissedAutoDetectIds remembers auto-detected
// compilers the user explicitly removed, so the next auto-detect pass doesn't just add them
// straight back.
public sealed class ManualCompilerRegistryFile
{
    public Dictionary<string, ManualCompilerRecord> Entries { get; set; } = new();
    public List<string> DismissedAutoDetectIds { get; set; } = new();
}

public sealed record CompilerResolverSpec(string Kind, Dictionary<string, string> Params);

public sealed record CompilerResolution(string Version, string DownloadUrl, string FileName);

public partial class MainWindow
{
    // Compilers tab data source: same GitHub-index/disk-cache/ETag pattern as the marketplace,
    // pointed at CompilerIndex.json's "compilers" array instead of ExtensionsIndex.json's "extensions".
    private async Task LoadCompilerExtensionsAsync(bool forceResolve = false)
    {
        // The very first compiler load of an app session always re-checks live versions
        // regardless of the caller's forceResolve, so "reopen the app" behaves like a refresh
        // even though the on-disk cache from the previous run is otherwise fully populated.
        var effectiveForceResolve = forceResolve || !_hasResolvedCompilersThisSession;
        _hasResolvedCompilersThisSession = true;

        var compilerEntries = new List<CompilerIndexEntry>();
        var loadErrors = new List<string>();

        LoadInstalledCompilerRegistry();
        LoadManualCompilerRegistry();
        RefreshManualCompilerExtensions();
        if (!_hasRunCompilerAutoDetect)
        {
            _hasRunCompilerAutoDetect = true;
            _ = AutoDetectDefaultCompilersAsync();
        }

        var diskJson = TryReadCacheFile(CompilerIndexCachePath);
        if (diskJson is not null)
            compilerEntries = ParseCompilerIndexEntries(diskJson, loadErrors);

        try
        {
            _compilerIndexETag ??= TryReadCacheFile(CompilerIndexETagPath)?.Trim();
            var hasLocalDataToReuseOn304 = compilerEntries.Count > 0;

            foreach (var indexUrl in CompilerIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (hasLocalDataToReuseOn304 && _compilerIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _compilerIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Compiler index fetch",
                    async ct =>
                    {
                        using var indexResponse = await MarketplaceHttpClient.SendAsync(indexRequest, ct);
                        if ((int)indexResponse.StatusCode == 304)
                            return (304, (string?)null, (string?)null);
                        if (!indexResponse.IsSuccessStatusCode)
                            return (0, (string?)null, (string?)null);
                        var body = await indexResponse.Content.ReadAsStringAsync(ct);
                        var etag = indexResponse.Headers.ETag?.Tag;
                        return (200, body, etag);
                    });

                if (statusCode == 304)
                {
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
            // Compilers are a secondary, best-effort tab - swallow errors instead of
            // propagating (unlike the marketplace, a stale/missing compiler list shouldn't
            // block the rest of the Extensions page from loading).
            loadErrors.Add($"Compiler index fetch failed: {DescribeFetchFailure(ex)}");
            KodoDiagnostics.LogDebug("Compiler index fetch failed.", ex);
        }

        // Instant, network-free paint using cached/fallback versions - the live lookup
        // (GitHub releases, nodejs.org, go.dev, npm, ...) happens after, in the background.
        var resolutionCache = LoadCompilerResolutionCache();
        var compilerExtensions = BuildCompilerExtensionsFromCacheOrFallback(compilerEntries, resolutionCache);

        Dictionary<string, string> compilerIconMap = [];
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SyncMarketplaceExtensionCollection(CompilerExtensions, compilerExtensions);
            SyncObservableCollection(
                ExtensionLoadErrors,
                ExtensionLoadErrors.Concat(loadErrors).Distinct().ToList(),
                error => error);

            SyncCompilerInstallStates();
            OnPropertyChanged(nameof(ExtensionLoadErrors));
            NotifyExtensionFiltersChanged();

            compilerIconMap = CompilerExtensions
                .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
                .ToDictionary(entry => entry.Id, entry => entry.IconUrl, StringComparer.OrdinalIgnoreCase);

            // CompilerExtensions has real icon URLs now (it didn't yet when
            // RefreshManualCompilerExtensions first ran above) - rebuild the manual/auto-detected
            // entries so they pick up matching icons via CanonicalCompilerId.
            RefreshManualCompilerExtensions();
        });
        _ = FetchMarketplaceIconsAsync(compilerIconMap, CompilerExtensions);
        // SyncMarketplaceExtensionCollection assigns these exact object instances into
        // CompilerExtensions (see its ReferenceEquals check), so mutating compilerExtensions[i]
        // in place from the background resolver is enough to update the live, bound collection.
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
        catch { /* best-effort disk cache - ignore write failures */ }
    }

    private void LoadInstalledCompilerRegistry()
    {
        try
        {
            if (!File.Exists(CompilerInstallRegistryPath))
                return;
            var json = File.ReadAllText(CompilerInstallRegistryPath, System.Text.Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, InstalledCompilerRecord>>(json);
            if (loaded is not null)
                _installedCompilers = new Dictionary<string, InstalledCompilerRecord>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to read installed-compilers.json.", ex);
        }
    }

    private void SaveInstalledCompilerRegistry()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CompilerInstallRegistryPath)!);
            var json = JsonSerializer.Serialize(_installedCompilers, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CompilerInstallRegistryPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to write installed-compilers.json.", ex);
        }
    }

    private void SyncCompilerInstallStates()
    {
        foreach (var entry in CompilerExtensions)
        {
            if (_installedCompilers.TryGetValue(entry.Id, out var record))
            {
                var isUpdateAvailable = CompareExtensionVersions(entry.Version, record.Version) > 0;
                entry.SetCompilerInstalledState(record.Version, record.InstalledOnUtc, isUpdateAvailable);
            }
            else
            {
                entry.SetCompilerInstalledState(null, null, isUpdateAvailable: false);
            }
        }
        NotifyExtensionFiltersChanged();
    }

    private void LoadManualCompilerRegistry()
    {
        try
        {
            if (!File.Exists(ManualCompilersRegistryPath))
                return;

            var json = File.ReadAllText(ManualCompilersRegistryPath, System.Text.Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<ManualCompilerRegistryFile>(json);
            if (loaded is null)
                return;

            _manualCompilers = new Dictionary<string, ManualCompilerRecord>(loaded.Entries, StringComparer.OrdinalIgnoreCase);
            _autoDetectDismissedIds = new HashSet<string>(loaded.DismissedAutoDetectIds, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to read manual-compilers.json.", ex);
        }
    }

    private void SaveManualCompilerRegistry()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManualCompilersRegistryPath)!);
            var payload = new ManualCompilerRegistryFile
            {
                Entries = _manualCompilers,
                DismissedAutoDetectIds = _autoDetectDismissedIds.ToList()
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ManualCompilersRegistryPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to write manual-compilers.json.", ex);
        }
    }

    // Rebuilds ManualCompilerExtensions from _manualCompilers. These entries are always
    // "installed" the moment they're tracked - there's no install/update step for them, Kodo is
    // just remembering where an already-installed compiler lives.
    private void RefreshManualCompilerExtensions()
    {
        // Borrow icons from the official CompilerIndex.json entries where a record has a
        // CanonicalCompilerId match, so e.g. an auto-detected "Python" shows the same icon as
        // the Python entry in the Compilers tab instead of a text abbreviation.
        var canonicalIconMap = CompilerExtensions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
            .ToDictionary(entry => entry.Id, entry => entry.IconUrl, StringComparer.OrdinalIgnoreCase);

        ManualCompilerExtensions.Clear();
        foreach (var pair in _manualCompilers.OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase))
            ManualCompilerExtensions.Add(BuildManualCompilerExtension(pair.Key, pair.Value, canonicalIconMap));

        var manualIconMap = ManualCompilerExtensions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
            .ToDictionary(entry => entry.Id, entry => entry.IconUrl, StringComparer.OrdinalIgnoreCase);
        if (manualIconMap.Count > 0)
            _ = FetchMarketplaceIconsAsync(manualIconMap, ManualCompilerExtensions);
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

    // Clears Kodo's tracking of a manually-added or auto-detected compiler. Unlike
    // ForgetLocalCompilerRecord, this never touches disk beyond our own registry - Kodo never
    // installed these, it was just pointed at (or found) them.
    private void ForgetManualCompilerRecord(MarketplaceExtension compilerExtension)
    {
        if (_manualCompilers.Remove(compilerExtension.Id) &&
            compilerExtension.Id.StartsWith("auto:", StringComparison.Ordinal))
        {
            // Remember it was dismissed so the next auto-detect pass doesn't just re-add it.
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

    // Compilers Kodo will silently look for on this machine - PATH scans only, plus a vswhere
    // lookup for MSVC's cl.exe (which normally isn't on PATH outside a Developer Command
    // Prompt). Every candidate that isn't found is just skipped - see AutoDetectDefaultCompilersAsync.
    // CanonicalCompilerId maps each candidate to its matching entry in CompilerIndex.json (null
    // when there isn't a clean one-to-one match) so the auto-detected entry can borrow that
    // entry's icon instead of falling back to a text abbreviation.
    private static readonly (string Id, string DisplayName, string Author, string[] ExeNames, string? CanonicalCompilerId)[] AutoDetectCompilerCandidates =
    [
        ("dotnet-cli", ".NET SDK (dotnet)", "Microsoft", new[] { "dotnet.exe" }, "dotnet-sdk"),
        ("csc", "C# Compiler (csc.exe)", "Microsoft", new[] { "csc.exe" }, "dotnet-sdk"),
        ("gcc", "GCC", "GNU / MinGW-w64", new[] { "gcc.exe" }, "msys2-mingw"),
        ("gpp", "G++", "GNU / MinGW-w64", new[] { "g++.exe" }, "msys2-mingw"),
        ("clang", "Clang", "LLVM Project", new[] { "clang.exe" }, "llvm-clang"),
        ("python-auto", "Python", "Python Software Foundation", new[] { "python.exe" }, "python"),
        ("node-auto", "Node.js", "OpenJS Foundation", new[] { "node.exe" }, "nodejs"),
        ("javac", "Java (JDK)", "OpenJDK", new[] { "javac.exe" }, "temurin-jdk"),
        ("go-auto", "Go", "Google", new[] { "go.exe" }, "go"),
        ("rustc", "Rust (rustc)", "Rust Foundation", new[] { "rustc.exe" }, "rust-rustup"),
        ("perl-auto", "Perl", "Strawberry Perl", new[] { "perl.exe" }, "strawberry-perl"),
        ("ruby-auto", "Ruby", "RubyInstaller", new[] { "ruby.exe" }, "rubyinstaller"),
        ("swift-auto", "Swift", "Swift.org", new[] { "swift.exe" }, "swift"),
        ("julia-auto", "Julia", "JuliaLang", new[] { "julia.exe" }, "julia")
    ];

    // Silently scans PATH (plus MSVC via vswhere) for compilers already on this machine, so the
    // user doesn't have to hunt down filepaths for tools they already have installed. This must
    // never surface a warning/error dialog - not finding a given compiler is the overwhelmingly
    // common case, so every step here is wrapped and failures are just skipped.
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
                    // Never let detection surface an error.
                }

                foreach (var candidate in AutoDetectCompilerCandidates)
                {
                    try
                    {
                        foreach (var exeName in candidate.ExeNames)
                        {
                            var found = TryFindOnPath(exeName);
                            if (found is null)
                                continue;

                            results.Add((candidate.Id, found, candidate.DisplayName, candidate.Author, candidate.CanonicalCompilerId));
                            break;
                        }
                    }
                    catch
                    {
                        // Never let detection surface an error.
                    }
                }

                return results;
            });

            var addedAny = false;
            foreach (var (id, path, name, author, canonicalCompilerId) in discovered)
            {
                var resolvedId = "auto:" + id;
                if (_manualCompilers.ContainsKey(resolvedId) || _autoDetectDismissedIds.Contains(resolvedId))
                    continue;

                AddOrUpdateManualCompiler(path, autoDetected: true, displayName: name, author: author, id: resolvedId, canonicalCompilerId: canonicalCompilerId);
                addedAny = true;
            }

            if (addedAny)
            {
                RefreshManualCompilerExtensions();
                NotifyExtensionFiltersChanged();
            }
        }
        catch
        {
            // Best-effort background scan - swallow anything unexpected rather than showing a dialog.
        }
    }

    private static string? TryFindOnPath(string exeName)
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Malformed PATH entry - skip it.
                }
            }
        }
        catch
        {
            // No PATH, or otherwise inaccessible - nothing to find.
        }

        return null;
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

    // A parsed CompilerIndex.json entry. Unlike MarketplaceExtension (which every compiler is
    // eventually turned into), this has no Version/DownloadUrl/FileName of its own - only a
    // Resolver describing how to look those up live, plus a Fallback triple to fall back on for
    // entries with no automated resolver at all (see CompilerResolvers.cs).
    private sealed record CompilerIndexEntry(
        string Id, string Name, string Type, string Author, string Description, string IconUrl,
        CompilerResolverSpec? Resolver,
        string FallbackVersion, string FallbackDownloadUrl, string FallbackFileName);

    // Parses CompilerIndex.json's new schema. Deliberately separate from
    // ParseAndApplyMarketplaceIndex - the Marketplace (ExtensionsIndex.json) schema is untouched
    // and still carries hardcoded version/downloadUrl/fileName; only CompilerIndex.json moved to
    // the resolver-based schema.
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

                var fallbackVersion = string.Empty;
                var fallbackDownloadUrl = string.Empty;
                var fallbackFileName = string.Empty;
                if (item.TryGetProperty("fallback", out var fallbackEl) && fallbackEl.ValueKind == JsonValueKind.Object)
                {
                    fallbackVersion = fallbackEl.TryGetProperty("version", out var fvEl) ? fvEl.GetString() ?? string.Empty : string.Empty;
                    fallbackDownloadUrl = fallbackEl.TryGetProperty("downloadUrl", out var fdEl) ? fdEl.GetString() ?? string.Empty : string.Empty;
                    fallbackFileName = fallbackEl.TryGetProperty("fileName", out var ffEl) ? ffEl.GetString() ?? string.Empty : string.Empty;
                }

                result.Add(new CompilerIndexEntry(
                    id, GetStr("name"), GetStr("type"), GetStr("author"), GetStr("description"),
                    NormalizeGitHubUrl(GetStr("iconUrl")), resolver,
                    fallbackVersion, fallbackDownloadUrl, fallbackFileName));
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

    // Disk cache for resolved compiler versions, separate from CompilerIndexCachePath (which
    // only caches the raw index JSON). Avoids re-hitting ~35 different vendor endpoints
    // (GitHub, npm, nodejs.org, go.dev, ...) on every single launch/tab-open.
    private string CompilerResolvedCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "resolved-compiler-versions.json");

    // Once a compiler has a cache entry (real resolution or fallback), it's only re-checked when
    // explicitly asked to be: the Refresh button (forceResolve: true) or the first compiler load
    // of this app session (see _hasResolvedCompilersThisSession below). Everything in between
    // reads straight from disk - no re-hitting ~35 vendor endpoints on every tab open.
    private bool _hasResolvedCompilersThisSession;

    private Dictionary<string, ResolvedCompilerCacheEntry> LoadCompilerResolutionCache()
    {
        try
        {
            if (!File.Exists(CompilerResolvedCachePath))
                return new(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(CompilerResolvedCachePath, System.Text.Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ResolvedCompilerCacheEntry>>(json);
            return loaded is not null
                ? new Dictionary<string, ResolvedCompilerCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to read resolved-compiler-versions.json.", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveCompilerResolutionCache(Dictionary<string, ResolvedCompilerCacheEntry> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CompilerResolvedCachePath)!);
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CompilerResolvedCachePath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to write resolved-compiler-versions.json.", ex);
        }
    }

    // Fast, network-free pass: builds the visible MarketplaceExtension list from whatever's
    // already known (disk cache, however stale, or the CompilerIndex.json "fallback" block for
    // entries with no automated resolver) so the Compilers tab paints instantly. Live values
    // are then patched in by RefreshCompilerResolutionsAsync once resolution completes.
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
                IconUrl = entry.IconUrl
            });
        }
        return result;
    }

    // Background pass: resolves the live version/installer for every non-manual compiler that
    // either has no cache entry yet, or is being force-refreshed (Refresh button, or the first
    // compiler load of this app session), then patches the results into the already-visible
    // MarketplaceExtension objects in place (Version/DownloadUrl/FileName are mutable precisely
    // for this). A single vendor being down/slow/rate-limited only leaves that one entry showing
    // its last-known-good (or fallback) version - it never blocks the rest of the tab. Whatever
    // comes out of this pass - success or fallback - gets written to disk so it sticks until the
    // next forced refresh instead of being re-fetched on every ordinary tab open.
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

    // Switches to the Compilers tab and refreshes the compiler listing (respecting the normal refresh cooldown).
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

    // Compilers are standalone installer executables, not zip'd Kodo extensions - this
    // downloads the exe, saves it under CompilersFolderPath, and launches the installer
    // for the user to run. "Installed" is recorded as soon as the installer is launched
    // (Kodo has no way to know when a third-party installer wizard actually finishes).
    private async Task InstallCompilerExtensionAsync(MarketplaceExtension compilerExtension)
    {
        if (compilerExtension.IsInstalling || (compilerExtension.IsInstalled && !compilerExtension.IsUpdateAvailable))
            return;

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
                        throw new HttpRequestException(
                            $"Unable to download {compilerExtension.Name} installer (HTTP {(int)downloadResponse.StatusCode}).");
                    return await downloadResponse.Content.ReadAsByteArrayAsync(ct);
                });

            await File.WriteAllBytesAsync(outputPath, bytes);

            // Hand off to the OS installer - Kodo doesn't drive the wizard itself.
            Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });

            _installedCompilers[compilerExtension.Id] = new InstalledCompilerRecord
            {
                Version = compilerExtension.Version,
                InstalledOnUtc = DateTime.UtcNow,
                InstalledExePath = outputPath
            };
            SaveInstalledCompilerRegistry();
            SyncCompilerInstallStates();

            ExtensionsStatusText = $"{compilerExtension.Name} installer launched. Finish the setup wizard to complete installation.";
        }
        catch (Exception ex)
        {
            SyncCompilerInstallStates();
            RefreshMarketplaceConnectivityState($"Compiler install - {compilerExtension.Name}", ex);
            ExtensionsStatusText = $"Failed to install {compilerExtension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Compiler install - {compilerExtension.Name}", ex);
        }
        finally
        {
            compilerExtension.IsInstalling = false;
            NotifyExtensionActionStateChanged();
            SyncCompilerInstallStates();
        }
    }

    private void ManualCompilerPathTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = AddManualCompilerFromTextBoxAsync();
    }

    private void AddManualCompilerButton_OnClick(object? sender, RoutedEventArgs e) =>
        _ = AddManualCompilerFromTextBoxAsync();

    // Adds whatever's in ManualCompilerPathText as a tracked compiler. No error dialogs here -
    // a bad path just gets a quiet status-text message, same as everywhere else compiler state
    // is reported.
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

    // Mirrors UninstallExtensionAsync, but compilers aren't Kodo extensions - they're
    // full Windows installs (Program Files, real uninstaller) run via a third-party wizard
    // Kodo doesn't control. Deleting our downloaded copy of the installer under
    // CompilersFolderPath only removes the .exe we downloaded, not the actual install - so
    // this finds the compiler's real uninstaller (registry first, then a Program Files scan
    // for installers that never registered with Windows at all) and runs it silently.
    private async Task UninstallCompilerExtensionAsync(MarketplaceExtension compilerExtension)
    {
        if (IsManuallyTrackedCompilerId(compilerExtension.Id))
        {
            // Kodo never installed these - it was just pointed at (or found) them - so
            // "uninstall" just means forget the tracked reference, not run a real uninstaller.
            ForgetManualCompilerRecord(compilerExtension);
            ExtensionsStatusText = $"Removed {compilerExtension.Name} from Kodo's tracked compilers.";
            return;
        }

        try
        {
            var located = FindCompilerUninstaller(compilerExtension.Name);

            if (located is null)
            {
                // Nothing in the registry and nothing under Program Files either - either
                // it was never a real Windows install, or it's already gone by some other
                // means. Nothing left to launch, so just forget our own record of it.
                ExtensionsStatusText = $"Couldn't find an uninstaller for {compilerExtension.Name} " +
                    "in the registry or Program Files; it may already be removed. Forgetting it in Kodo.";
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
                UseShellExecute = true
            });

            if (uninstallProcess is not null)
                await uninstallProcess.WaitForExitAsync();

            // Inno/NSIS-style uninstallers copy themselves to a temp folder and relaunch,
            // with the original process exiting almost immediately - so the process we
            // awaited exiting doesn't mean removal actually finished. Poll for the install
            // folder to actually disappear (more reliable than the registry: a lot of small
            // installers never register with Windows at all, but they always clean up {app}).
            var removed = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (!Directory.Exists(installFolder) || IsDirectoryEffectivelyEmpty(installFolder))
                {
                    removed = true;
                    break;
                }
                await Task.Delay(750);
            }

            if (removed)
            {
                ForgetLocalCompilerRecord(compilerExtension);
                ExtensionsStatusText = $"{compilerExtension.Name} uninstalled.";
            }
            else
            {
                // Still there - the user likely closed/cancelled the wizard (shouldn't happen
                // with the silent flags below, but a non-Inno installer might ignore them), or
                // it's still running. Leave our record alone so the UI keeps showing it as
                // installed instead of flipping to "Install" and inviting a reinstall.
                ExtensionsStatusText = $"{compilerExtension.Name} still appears installed at {installFolder}. " +
                    "Try Uninstall again, or remove it manually from Program Files.";
            }
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Failed to uninstall {compilerExtension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Compiler uninstall - {compilerExtension.Name}", ex);
        }
        finally
        {
            compilerExtension.IsInstalling = false;
            NotifyExtensionActionStateChanged();
        }
    }

    // True once only harmless leftovers remain (uninstallers commonly can't delete their own
    // exe/log while still running, or leave an empty shell folder behind).
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

    // Clears Kodo's own record of a compiler install: the registry entry in
    // installed-compilers.json and our downloaded copy of the installer under
    // CompilersFolderPath. Called once the real Windows uninstall is confirmed gone
    // (or was never a real Windows install to begin with).
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

    // Locates the compiler's real uninstaller and returns (ExePath, Arguments, InstallFolder).
    // Tries the Windows "Uninstall" registry first (the same place Apps & Features reads
    // from); if nothing is registered there - which small hobby installers often skip - falls
    // back to scanning the two Program Files roots for a folder matching the compiler's name
    // containing an unins###.exe (Inno Setup's standard uninstaller naming).
    private static (string ExePath, string Arguments, string InstallFolder)? FindCompilerUninstaller(string compilerName)
    {
        var registryCommand = FindWindowsUninstallCommand(compilerName);
        if (registryCommand is not null)
        {
            var (exePath, arguments) = ParseUninstallCommand(registryCommand);
            var folder = Path.GetDirectoryName(exePath) ?? string.Empty;
            return (exePath, AppendSilentUninstallFlags(exePath, arguments), folder);
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

    // "Shine Compiler" installs to Program Files\Shine, not Program Files\Shine Compiler - so
    // try the full marketplace name first, then drop a trailing " Compiler"/" compiler" suffix,
    // then just the first word, to match how these installers actually name their folder.
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

    // Forces a fully unattended uninstall so it can't get stuck waiting on a wizard the user
    // never sees complete: Inno Setup uninstallers (unins*.exe, the case for every compiler
    // Kodo currently ships) support /VERYSILENT + /SUPPRESSMSGBOXES + /NORESTART; MSI-based
    // uninstalls (msiexec /X{GUID}) use /quiet + /norestart instead. Leaves anything already
    // silent, or any other installer technology, untouched rather than guessing at its flags.
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

    // Splits a registry UninstallString into (FileName, Arguments) for ProcessStartInfo,
    // instead of handing the raw string to "cmd /c" - wrapping an already-quoted path like
    // `"C:\Program Files\Shine\unins000.exe"` in another layer of quotes for cmd produces
    // a malformed command line that cmd silently no-ops on, which is why launching it that
    // way didn't actually run the uninstaller.
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

        // Unquoted - if it contains a space, we can't reliably tell where the path ends and
        // arguments begin, so treat the whole thing as the path (the common case has no args).
        return (trimmed, string.Empty);
    }

    // Scans the same "Uninstall" registry keys Windows' Apps & Features reads from, looking
    // for an entry whose DisplayName matches (or is contained in / contains) the compiler's
    // marketplace name, and returns its UninstallString. Checks both 64- and 32-bit views plus
    // per-user installs, since we don't know ahead of time how the third-party installer registered.
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
                    catch { /* Unreadable entry - skip it */ }
                }
            }
            catch { /* Registry unavailable */ }
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
        var best = entryPattern.Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderByDescending(v => ParseVersionNumbers(v), VersionNumberSequenceComparer.Instance)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(best)) return null;

        var fileName = fileNameTemplate.Replace("{version}", best);
        var downloadUrl = downloadUrlTemplate.Replace("{version}", best);
        return new CompilerResolution(best, downloadUrl, fileName);
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

}