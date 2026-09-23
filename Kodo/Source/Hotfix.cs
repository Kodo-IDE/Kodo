// Licensed under GPL-v3.0
//
// Consolidated Kodo-side hotfix system (Phases 1-4): versioning, models,
// validation, state, discovery, packaging, staging, and startup confirmation.
//
// NOTE: HotfixApplyShared.cs intentionally stays a separate file. It is linked
// into the trimmed, single-file KodoUpdater exe and must remain BCL-only with
// no Kodo.* dependencies; merging it here would break the updater build.

using Kodo.HotfixShared;
using Shared = Kodo.HotfixShared.HotfixShared;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Kodo;

// ---------------------------------------------------------------------------
// Part 1: Versioning (was HotfixVersion.cs)
// ---------------------------------------------------------------------------

// Phase 1: keeps full-release versioning separate from hotfix numbering.
// A hotfix update is only valid on the same base version line:
//   Local  2.1.0 HF0  + Remote 2.1.0 HF1 -> update available
//   Local  2.1.0 HF2  + Remote 2.1.0 HF1 -> ignore (remote older)
//   Local  2.1.0 HF2  + Remote 2.2.0 HF0 -> full release, not a hotfix
internal static class HotfixVersion
{
    public const string FallbackBaseVersion = "2.1.0";

    public static string CurrentBaseVersion =>
        NormalizeBaseVersion(KodoDiagnostics.AppVersion) ?? FallbackBaseVersion;

    public static string Format(string baseVersion, int hotfixLevel) =>
        $"{NormalizeBaseVersion(baseVersion) ?? baseVersion?.Trim()} HF{Math.Max(0, hotfixLevel)}";

    public static string Format(HotfixState state) =>
        Format(state.BaseVersion, state.HotfixLevel);

    public static bool IsValidBaseVersion(string? baseVersion) =>
        NormalizeBaseVersion(baseVersion) is not null;

    public static bool AreSameBaseVersion(string? a, string? b)
    {
        var na = NormalizeBaseVersion(a);
        var nb = NormalizeBaseVersion(b);
        if (na is null || nb is null) return false;
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHotfixUpdateAvailable(
        string localBaseVersion, int localHotfixLevel,
        string remoteBaseVersion, int remoteHotfixLevel)
    {
        if (!AreSameBaseVersion(localBaseVersion, remoteBaseVersion)) return false;
        if (remoteHotfixLevel <= 0) return false;
        if (localHotfixLevel < 0) localHotfixLevel = 0;
        return remoteHotfixLevel > localHotfixLevel;
    }

    public static bool IsHotfixUpdateAvailable(HotfixState local, HotfixManifest remote)
    {
        if (local is null || remote is null) return false;
        return IsHotfixUpdateAvailable(local.BaseVersion, local.HotfixLevel, remote.BaseVersion, remote.Hotfix);
    }

    internal static string? NormalizeBaseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var core = raw.Trim();
        if (core.Length > 0 && (core[0] == 'v' || core[0] == 'V')) core = core[1..];
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];
        core = core.Trim();
        if (core.Length == 0) return null;
        var segments = core.Split('.');
        if (segments.Length == 0) return null;
        foreach (var s in segments)
        {
            if (s.Length == 0) return null;
            foreach (var c in s)
                if (!char.IsDigit(c)) return null;
            if (!int.TryParse(s, out _)) return null;
        }
        return core;
    }
}

// ---------------------------------------------------------------------------
// Part 2: Models (was Models/HotfixModels.cs)
// ---------------------------------------------------------------------------

// Phase 1 hotfix foundation: state + manifest models only.
// No download/apply/rollback logic lives here.
internal sealed class HotfixState
{
    [JsonPropertyName("baseVersion")]
    public string BaseVersion { get; set; } = HotfixVersion.FallbackBaseVersion;

    [JsonPropertyName("hotfixLevel")]
    public int HotfixLevel { get; set; }

    // Phase 4: last hotfix level confirmed by a successful startup.
    // hotfixLevel may run ahead while a hotfix is applied-but-unconfirmed;
    // lastKnownGoodHotfix only advances on confirmation and is the rollback target.
    [JsonPropertyName("lastKnownGoodHotfix")]
    public int LastKnownGoodHotfix { get; set; }

    public static HotfixState Default() => new()
    {
        BaseVersion = HotfixVersion.CurrentBaseVersion,
        HotfixLevel = 0,
        LastKnownGoodHotfix = 0,
    };
}

internal sealed class HotfixManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = HotfixValidator.SupportedSchemaVersion;

    [JsonPropertyName("product")]
    public string Product { get; set; } = "Kodo";

    [JsonPropertyName("baseVersion")]
    public string BaseVersion { get; set; } = "";

    [JsonPropertyName("hotfix")]
    public int Hotfix { get; set; }

    [JsonPropertyName("minimumHotfix")]
    public int MinimumHotfix { get; set; }

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("files")]
    public List<HotfixFileEntry> Files { get; set; } = new();
}

internal sealed class HotfixFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

// Phase 2: a discovered hotfix release. Enough info for a future phase to
// download it. Discovery only — nothing here downloads or installs anything.
internal sealed record HotfixCandidate(
    string BaseVersion,
    int HotfixLevel,
    string PlatformRid,
    string TagName,
    string ReleaseNotesUrl,
    string AssetName,
    string AssetDownloadUrl,
    long AssetSizeBytes,
    string? Sha256 = null);

// ---------------------------------------------------------------------------
// Part 3: Manifest validation (was HotfixValidator.cs)
// ---------------------------------------------------------------------------

// Phase 1: inspects a hotfix manifest WITHOUT applying anything.
// No downloads, no file replacement, no rollback, no signatures
// (the existing updater only verifies SHA-256 checksums; there is
// no signing infrastructure to reuse yet).
internal static class HotfixValidator
{
    public const int SupportedSchemaVersion = 1;
    public const string ExpectedProduct = "Kodo";

    // Mirrors RuntimeIdentifiers in Kodo.csproj.
    private static readonly HashSet<string> KnownPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "win-x64",
        "linux-x64",
        "linux-arm64",
    };

    public static bool TryValidate(HotfixManifest? manifest, out string? error)
    {
        var errors = Validate(manifest);
        if (errors.Count == 0)
        {
            error = null;
            return true;
        }
        error = errors[0];
        return false;
    }

    public static IReadOnlyList<string> Validate(HotfixManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            errors.Add("Manifest is missing.");
            return errors;
        }
        if (manifest.SchemaVersion != SupportedSchemaVersion)
            errors.Add($"Unsupported schema version {manifest.SchemaVersion} (expected {SupportedSchemaVersion}).");
        if (!string.Equals(manifest.Product?.Trim(), ExpectedProduct, StringComparison.Ordinal))
            errors.Add($"Unexpected product '{manifest.Product}' (expected '{ExpectedProduct}').");
        if (!HotfixVersion.IsValidBaseVersion(manifest.BaseVersion))
            errors.Add($"Invalid base version '{manifest.BaseVersion}'.");
        if (manifest.Hotfix < 1)
            errors.Add($"Invalid hotfix number {manifest.Hotfix} (must be >= 1).");
        if (manifest.MinimumHotfix < 0)
            errors.Add($"Invalid minimumHotfix {manifest.MinimumHotfix} (must be >= 0).");
        else if (manifest.Hotfix >= 1 && manifest.MinimumHotfix > manifest.Hotfix)
            errors.Add($"minimumHotfix {manifest.MinimumHotfix} exceeds hotfix {manifest.Hotfix}.");
        if (string.IsNullOrWhiteSpace(manifest.Platform) || !KnownPlatforms.Contains(manifest.Platform.Trim()))
            errors.Add($"Unrecognized platform '{manifest.Platform}' (expected one of: win-x64, linux-x64, linux-arm64).");
        if (manifest.Files is null)
        {
            errors.Add("File list is missing.");
            return errors;
        }
        if (manifest.Files.Count == 0)
            errors.Add("File list is empty; a hotfix must update at least one file.");

        var seen = new HashSet<string>(FileSystemPaths.Comparer);
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var entry = manifest.Files[i];
            var prefix = $"files[{i}]";
            if (entry is null)
            {
                errors.Add($"{prefix} entry is missing.");
                continue;
            }
            if (!IsSafeRelativePath(entry.Path))
            {
                errors.Add($"{prefix} has unsafe path '{entry.Path}' (must be a relative path without absolute roots or '..').");
                continue;
            }
            var normalized = NormalizeEntryPath(entry.Path);
            if (!seen.Add(normalized))
                errors.Add($"{prefix} duplicates path '{entry.Path}'.");
            if (!IsValidSha256(entry.Sha256))
                errors.Add($"{prefix} has invalid SHA-256 '{entry.Sha256}' (expected 64 hex characters).");
        }
        return errors;
    }

    internal static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hex = value.Trim();
        if (hex.Length != 64) return false;
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        if (p.Length == 0) return false;
        if (p.Length > 260) return false;

        // No absolute paths (covers C:\, \\server, /etc, \foo on all OSes).
        if (Path.IsPathRooted(p)) return false;
        if (p.StartsWith('/') || p.StartsWith('\\')) return false;
        if (p.Length >= 2 && p[1] == ':') return false;
        if (p.StartsWith("\\\\", StringComparison.Ordinal)) return false;

        // No drive separators, no traversal segments, no empty segments.
        var segments = p.Split('/', '\\');
        foreach (var seg in segments)
        {
            if (seg.Length == 0) return false;
            if (seg == "." || seg == "..") return false;
            if (seg == "~") return false;
            if (seg.EndsWith(':')) return false;
            foreach (var c in Path.GetInvalidPathChars())
                if (seg.Contains(c)) return false;
        }

        // Catch ".." that survives mixed separators or trailing dots/spaces.
        if (p.Contains("..", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string NormalizeEntryPath(string path) =>
        path.Trim().Replace('\\', '/');
}

// ---------------------------------------------------------------------------
// Part 4: State persistence (was HotfixStateStore.cs)
// ---------------------------------------------------------------------------

// Phase 1: persists the installed hotfix level alongside updater data.
// Default location: %LocalAppData%/Kodo/update/hotfix-state.json (UpdateService.UpdateRoot).
// Never throws from load paths: missing file -> HF0, malformed -> rejected, not a crash.
internal static class HotfixStateStore
{
    public const string StateFileName = "hotfix-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string DefaultPath => Path.Combine(UpdateService.UpdateRoot, StateFileName);

    public static bool TryLoad(string? path, out HotfixState? state, out string? error)
    {
        state = null;
        error = null;
        var file = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

        try
        {
            if (!File.Exists(file))
            {
                state = HotfixState.Default();
                return true;
            }

            var json = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Hotfix state file is empty.";
                return false;
            }

            HotfixState? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<HotfixState>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                error = $"Hotfix state is malformed: {ex.GetType().Name}.";
                return false;
            }

            if (parsed is null)
            {
                error = "Hotfix state is malformed: empty document.";
                return false;
            }

            var validationError = ValidateState(parsed);
            if (validationError is not null)
            {
                error = validationError;
                return false;
            }

            parsed.BaseVersion = HotfixVersion.NormalizeBaseVersion(parsed.BaseVersion)!;
            if (!HasLastKnownGood(json))
            {
                // Legacy state file (Phases 1-3): whatever is installed has been
                // running, so treat it as the last known good level.
                parsed.LastKnownGoodHotfix = parsed.HotfixLevel;
            }
            state = parsed;
            return true;
        }
        catch (Exception ex)
        {
            state = null;
            error = $"Could not read hotfix state: {ex.GetType().Name}.";
            return false;
        }
    }

    public static HotfixState LoadOrDefault(string? path = null)
    {
        if (TryLoad(path, out var state, out _) && state is not null)
            return state;
        return HotfixState.Default();
    }

    public static string? ValidateState(HotfixState? state)
    {
        if (state is null) return "Hotfix state is missing.";
        if (!HotfixVersion.IsValidBaseVersion(state.BaseVersion))
            return $"Invalid base version '{state.BaseVersion}'.";
        if (state.HotfixLevel < 0)
            return $"Invalid hotfix level {state.HotfixLevel} (must be >= 0).";
        if (state.LastKnownGoodHotfix < 0 || state.LastKnownGoodHotfix > state.HotfixLevel)
            return $"Invalid lastKnownGoodHotfix {state.LastKnownGoodHotfix} (must be 0..hotfixLevel).";
        return null;
    }

    private static bool HasLastKnownGood(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "lastKnownGoodHotfix", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return true; }
    }

    public static void Save(HotfixState state, string? path = null)
    {
        var validationError = ValidateState(state);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(state));

        var file = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic-ish save: write temp file then move over the target,
        // mirroring UpdateService.CreateUpdateTransaction.
        var payload = JsonSerializer.Serialize(
            new HotfixState
            {
                BaseVersion = state.BaseVersion.Trim(),
                HotfixLevel = state.HotfixLevel,
                LastKnownGoodHotfix = state.LastKnownGoodHotfix,
            });
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, file, overwrite: true);
    }
}

// ---------------------------------------------------------------------------
// Part 5: Discovery (was HotfixDiscovery.cs)
// ---------------------------------------------------------------------------

// Phase 2: hotfix discovery through GitHub Releases.
//
// Hotfix releases use tags like "hotfix/2.1.0/3" (base version + hotfix level).
// Platform is determined per asset: a hotfix package asset must contain both
// "hotfix" and the platform RID in its name, e.g.
// "Kodo-hotfix-2.1.0-3-win-x64.zip".
//
// This class is pure (no network): it selects candidates from release data so
// it can be unit-tested with mocked releases. Network fetching lives in
// UpdateService, which reuses the existing updater HTTP infrastructure.
//
// Discovery only — no downloading, applying, rollback, or health checks.
internal static class HotfixDiscovery
{
    public const string TagPrefix = "hotfix";

    internal const string FallbackReleaseNotesUrl = "https://github.com/Kodo-IDE/Kodo/releases";

    public static bool IsHotfixTag(string? tag) =>
        TryParseHotfixTag(tag, out _, out _);

    public static bool TryParseHotfixTag(string? tag, out string? baseVersion, out int hotfixLevel)
    {
        baseVersion = null;
        hotfixLevel = 0;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var parts = tag.Trim().Split('/');
        if (parts.Length != 3) return false;
        if (!string.Equals(parts[0].Trim(), TagPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var normalizedBase = HotfixVersion.NormalizeBaseVersion(parts[1]);
        if (normalizedBase is null) return false;
        if (!int.TryParse(parts[2].Trim(), out var level) || level < 1) return false;
        baseVersion = normalizedBase;
        hotfixLevel = level;
        return true;
    }

    public static string GetCurrentPlatformRid()
    {
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
        return "win-x64";
    }

    internal static GitHubAsset? PickHotfixAsset(GitHubAsset[]? assets, string platformRid)
    {
        if (assets is null || assets.Length == 0 || string.IsNullOrWhiteSpace(platformRid)) return null;
        var rid = platformRid.Trim();
        foreach (var asset in assets)
        {
            if (asset is null) continue;
            if (string.IsNullOrWhiteSpace(asset.Name)) continue;
            if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)) continue;
            if (asset.Name.Contains("hotfix", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains(rid, StringComparison.OrdinalIgnoreCase))
                return asset;
        }
        return null;
    }

    public static IReadOnlyList<HotfixCandidate> FindCompatibleHotfixes(
        IEnumerable<GitHubRelease>? releases,
        string installedBaseVersion,
        int installedHotfixLevel,
        string? platformRid = null)
    {
        var found = new List<HotfixCandidate>();
        var installedBase = HotfixVersion.NormalizeBaseVersion(installedBaseVersion);
        if (installedBase is null) return found;
        if (installedHotfixLevel < 0) installedHotfixLevel = 0;
        var rid = string.IsNullOrWhiteSpace(platformRid) ? GetCurrentPlatformRid() : platformRid.Trim();
        if (releases is null) return found;

        foreach (var release in releases)
        {
            if (release is null) continue;
            if (string.IsNullOrWhiteSpace(release.TagName)) continue;
            if (release.Draft || release.Prerelease) continue;
            if (!TryParseHotfixTag(release.TagName, out var baseVersion, out var level)) continue;
            if (!HotfixVersion.AreSameBaseVersion(baseVersion, installedBase)) continue;
            if (level <= installedHotfixLevel) continue;
            var asset = PickHotfixAsset(release.Assets, rid);
            if (asset is null) continue;
            found.Add(new HotfixCandidate(
                BaseVersion: baseVersion!,
                HotfixLevel: level,
                PlatformRid: rid,
                TagName: release.TagName.Trim(),
                ReleaseNotesUrl: release.HtmlUrl ?? FallbackReleaseNotesUrl,
                AssetName: asset.Name,
                AssetDownloadUrl: asset.BrowserDownloadUrl,
                AssetSizeBytes: asset.Size,
                Sha256: null));
        }

        found.Sort((a, b) => a.HotfixLevel.CompareTo(b.HotfixLevel));
        return found;
    }

    public static HotfixCandidate? SelectNewestHotfix(
        IEnumerable<GitHubRelease>? releases,
        string installedBaseVersion,
        int installedHotfixLevel,
        string? platformRid = null)
    {
        var compatible = FindCompatibleHotfixes(releases, installedBaseVersion, installedHotfixLevel, platformRid);
        return compatible.Count == 0 ? null : compatible[^1];
    }
}

// ---------------------------------------------------------------------------
// Part 6: Packaging (was HotfixPackaging.cs)
// ---------------------------------------------------------------------------

// Phase 3: hotfix package creation/inspection on the Kodo side.
// A package is a zip containing "manifest.json" at the root plus the payload
// files listed in the manifest. Used by tests to build packages; production
// packages arrive via GitHub Release assets (HotfixDiscovery).
internal static class HotfixPackaging
{
    public const string ManifestEntryName = "manifest.json";

    public static byte[] CreatePackage(HotfixManifest manifest, IReadOnlyDictionary<string, byte[]> payload)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestJson = JsonSerializer.Serialize(manifest);
            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var w = new StreamWriter(manifestEntry.Open()))
                w.Write(manifestJson);
            foreach (var (rel, bytes) in payload)
            {
                var entry = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    public static byte[] CreatePackageRaw(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }
}

// ---------------------------------------------------------------------------
// Part 7: Download, verification, staging (was HotfixStaging.cs)
// ---------------------------------------------------------------------------

// Phase 3 Kodo side: Discover → Download → Verify → Stage → (updater applies).
// Kodo never replaces its own files while running; application happens in
// KodoUpdater after Kodo exits. Nothing is copied into the installation
// until every verification check succeeds.
internal static class HotfixStaging
{
    internal static string HotfixRoot => Path.Combine(UpdateService.UpdateRoot, "hotfix");
    internal static string DownloadsRoot => Path.Combine(HotfixRoot, "downloads");

    private static readonly JsonSerializerOptions TxJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // --- Download ---

    internal static async Task<string> DownloadAsync(
        HttpClient client,
        HotfixCandidate candidate,
        string destPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (string.IsNullOrWhiteSpace(candidate.AssetDownloadUrl))
            throw new InvalidDataException("Hotfix candidate has no download URL.");
        if (string.IsNullOrWhiteSpace(destPath)) throw new ArgumentException("Destination path is missing.", nameof(destPath));

        KodoDiagnostics.LogDebug($"Hotfix download started: {candidate.TagName} ({candidate.AssetName})");
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var partialPath = destPath + ".partial";
        try
        {
            using var response = await client.GetAsync(candidate.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? candidate.AssetSizeBytes;
            await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                readTotal += read;
                if (progress is not null && totalBytes > 0)
                {
                    var fraction = Math.Clamp((double)readTotal / totalBytes, 0, 1);
                    progress.Report(new UpdateDownloadProgress(fraction, $"{FormatBytes(readTotal)} / {FormatBytes(totalBytes)}"));
                }
            }
            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            fileStream.Close();

            var partialInfo = new FileInfo(partialPath);
            if (!partialInfo.Exists || partialInfo.Length == 0)
                throw new InvalidDataException($"Hotfix download produced an empty file: {partialPath}");
            if (candidate.AssetSizeBytes > 0 && partialInfo.Length != candidate.AssetSizeBytes)
                throw new InvalidDataException($"Hotfix download incomplete: expected {candidate.AssetSizeBytes} bytes, got {partialInfo.Length}.");

            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            File.Move(partialPath, destPath);
            KodoDiagnostics.LogDebug($"Hotfix download completed: {destPath} ({FormatBytes(partialInfo.Length)})");
            return destPath;
        }
        catch
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            throw;
        }
    }

    // --- Verification (nothing reaches the install dir until this passes) ---

    internal sealed class VerifyResult
    {
        public bool Ok { get; set; }
        public HotfixManifest? Manifest { get; set; }
        public string? ManifestJson { get; set; }
        public string? Error { get; set; }
    }

    internal static VerifyResult VerifyPackage(
        string packagePath,
        string installedBaseVersion,
        int installedHotfixLevel,
        string? platformRid = null)
    {
        KodoDiagnostics.LogDebug($"Hotfix verification started: {packagePath}");
        ZipArchive? zip = null;
        VerifyResult Fail(string e)
        {
            KodoDiagnostics.LogDebug($"Hotfix verification failed: {e}");
            try { zip?.Dispose(); } catch { }
            zip = null;
            try { if (File.Exists(packagePath)) File.Delete(packagePath); } catch { }
            return new VerifyResult { Ok = false, Error = e };
        }

        try
        {
            var fs = File.OpenRead(packagePath);
            try { zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false); }
            catch { fs.Dispose(); throw; }
        }
        catch (Exception ex)
        {
            return Fail($"Package cannot be opened: {ex.GetType().Name}. Staged package discarded.");
        }

        using (zip!)
        {
            var manifestEntry = zip.Entries.FirstOrDefault(e =>
                string.Equals(NormalizeEntryName(e.FullName), HotfixPackaging.ManifestEntryName, StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
                return Fail("Package manifest is missing (manifest.json not found). Staged package discarded.");

            string manifestJson;
            try
            {
                using var reader = new StreamReader(manifestEntry.Open());
                manifestJson = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                return Fail($"Package manifest cannot be read: {ex.GetType().Name}. Staged package discarded.");
            }

            HotfixManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<HotfixManifest>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                return Fail($"Package manifest is malformed: {ex.GetType().Name}. Staged package discarded.");
            }
            if (manifest is null)
                return Fail("Package manifest is empty. Staged package discarded.");

            if (!HotfixValidator.TryValidate(manifest, out var validationError))
                return Fail($"Manifest validation failed: {validationError} Staged package discarded.");

            var rid = string.IsNullOrWhiteSpace(platformRid) ? HotfixDiscovery.GetCurrentPlatformRid() : platformRid.Trim();
            if (!HotfixVersion.AreSameBaseVersion(manifest.BaseVersion, installedBaseVersion))
                return Fail($"Base version mismatch: package is '{manifest.BaseVersion}', installed is '{installedBaseVersion}'. Staged package discarded.");
            if (manifest.Hotfix <= installedHotfixLevel)
                return Fail($"Hotfix HF{manifest.Hotfix} is not newer than installed HF{installedHotfixLevel}. Staged package discarded.");
            if (!string.Equals(manifest.Platform.Trim(), rid, StringComparison.OrdinalIgnoreCase))
                return Fail($"Platform mismatch: package is '{manifest.Platform}', this installation is '{rid}'. Staged package discarded.");

            var manifestSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
                manifestSet.Add(NormalizeEntryName(f.Path));

            var zipFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // directory entry
                var name = NormalizeEntryName(entry.FullName);
                if (string.Equals(name, HotfixPackaging.ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!HotfixValidator.IsSafeRelativePath(name))
                    return Fail($"Unsafe path in package: '{entry.FullName}'. Staged package discarded.");
                zipFiles[name] = entry;
            }

            foreach (var file in manifest.Files)
            {
                var name = NormalizeEntryName(file.Path);
                if (!zipFiles.TryGetValue(name, out var entry))
                    return Fail($"Payload file missing from package: '{file.Path}'. Staged package discarded.");
                string actual;
                try
                {
                    using var s = entry.Open();
                    actual = ComputeStreamSha256(s);
                }
                catch (Exception ex)
                {
                    return Fail($"Payload file cannot be read: '{file.Path}' ({ex.GetType().Name}). Staged package discarded.");
                }
                if (!string.Equals(actual, file.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    return Fail($"File hash mismatch: '{file.Path}'. Staged package discarded; installation untouched.");
            }

            foreach (var name in zipFiles.Keys)
            {
                if (!manifestSet.Contains(name))
                    return Fail($"Unexpected file in package (not listed in manifest): '{name}'. Staged package discarded.");
            }

            KodoDiagnostics.LogDebug("Hotfix verification succeeded");
            return new VerifyResult { Ok = true, Manifest = manifest, ManifestJson = manifestJson };
        }
    }

    internal static string NormalizeEntryName(string? name) =>
        (name ?? "").Trim().Replace('\\', '/').Trim('/');

    private static string ComputeStreamSha256(Stream stream)
    {
        using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hasher.AppendData(buffer, 0, read);
        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }

    // --- Staging ---

    internal static string StageVerifiedPackage(
        string packagePath,
        string manifestJson,
        HotfixManifest manifest,
        string hotfixRoot,
        string targetDir,
        string kodoExePath,
        int kodoPid,
        bool restartAfterUpdate = true,
        int previousHotfixLevel = 0,
        int previousLastKnownGood = 0)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var stageDir = Path.Combine(hotfixRoot, transactionId);
        var payloadDir = Path.Combine(stageDir, "payload");
        var backupDir = Path.Combine(stageDir, "backup");
        Directory.CreateDirectory(payloadDir);
        Directory.CreateDirectory(backupDir);

        try
        {
            File.Copy(packagePath, Path.Combine(stageDir, "package.zip"), overwrite: true);
            File.WriteAllText(Path.Combine(stageDir, "manifest.json"), manifestJson);
            ExtractPayloadSafe(packagePath, payloadDir, manifest);

            var tx = new HotfixTransaction
            {
                Kind = "hotfix",
                TransactionId = transactionId,
                Status = HotfixTransactionStatus.Staged,
                PackagePath = Path.Combine(stageDir, "package.zip"),
                ManifestPath = Path.Combine(stageDir, "manifest.json"),
                PayloadDir = payloadDir,
                BackupDir = backupDir,
                TargetDir = Path.GetFullPath(targetDir),
                KodoExePath = kodoExePath,
                KodoPid = kodoPid,
                RestartAfterUpdate = restartAfterUpdate,
                CreatedAtUtc = DateTime.UtcNow,
                BaseVersion = HotfixVersion.NormalizeBaseVersion(manifest.BaseVersion) ?? manifest.BaseVersion.Trim(),
                HotfixLevel = manifest.Hotfix,
                PreviousHotfixLevel = Math.Max(0, previousHotfixLevel),
                PreviousLastKnownGood = Math.Max(0, previousLastKnownGood),
                LastKnownGoodHotfix = Math.Max(0, previousLastKnownGood),
                PlatformRid = manifest.Platform.Trim(),
            };
            var txPath = Path.Combine(stageDir, "transaction.json");
            var tmp = txPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(tx, TxJsonOptions));
            File.Move(tmp, txPath, overwrite: true);
            KodoDiagnostics.LogDebug($"Hotfix staged: {stageDir}");
            KodoDiagnostics.LogDebug($"Hotfix transaction created: {txPath}");
            return txPath;
        }
        catch
        {
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); } catch { }
            throw;
        }
    }

    private static void ExtractPayloadSafe(string packagePath, string payloadDir, HotfixManifest manifest)
    {
        var payloadRoot = Path.GetFullPath(payloadDir);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
            allowed.Add(NormalizeEntryName(f.Path));

        using var zip = new ZipArchive(File.OpenRead(packagePath), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var name = NormalizeEntryName(entry.FullName);
            if (string.Equals(name, HotfixPackaging.ManifestEntryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!allowed.Contains(name))
                throw new InvalidDataException($"Unexpected file in package: '{entry.FullName}'.");
            if (!HotfixValidator.IsSafeRelativePath(name))
                throw new InvalidDataException($"Unsafe path in package: '{entry.FullName}'.");
            var dest = Path.GetFullPath(Path.Combine(payloadRoot, name));
            if (!Shared.IsInsideDirectory(dest, payloadRoot))
                throw new InvalidDataException($"Path escapes staging directory: '{entry.FullName}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    // --- Orchestration ---

    internal sealed class PrepareResult
    {
        public bool AlreadyInstalled { get; set; }
        public string? TransactionPath { get; set; }
        public HotfixTransaction? Transaction { get; set; }
    }

    internal static async Task<PrepareResult> PrepareAsync(
        HotfixCandidate candidate,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default,
        HttpClient? httpClient = null,
        string? updateRootOverride = null,
        string? targetDirOverride = null,
        string? installedBaseOverride = null,
        int? installedLevelOverride = null,
        int? kodoPidOverride = null,
        int? installedLastKnownGoodOverride = null)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        var updateRoot = updateRootOverride ?? UpdateService.UpdateRoot;
        var hotfixRoot = Path.Combine(updateRoot, "hotfix");
        var targetDir = targetDirOverride ?? AppContext.BaseDirectory;

        string installedBase;
        int installedLevel;
        int installedLastKnownGood;
        if (installedBaseOverride is not null || installedLevelOverride is not null)
        {
            installedBase = installedBaseOverride ?? HotfixVersion.CurrentBaseVersion;
            installedLevel = installedLevelOverride ?? 0;
            installedLastKnownGood = installedLastKnownGoodOverride ?? installedLevel;
        }
        else
        {
            (installedBase, installedLevel) = UpdateService.ResolveInstalledHotfix();
            installedLastKnownGood = HotfixStateStore.LoadOrDefault().LastKnownGoodHotfix;
        }

        if (!HotfixVersion.IsHotfixUpdateAvailable(installedBase, installedLevel, candidate.BaseVersion, candidate.HotfixLevel))
        {
            KodoDiagnostics.LogDebug($"Hotfix {candidate.TagName} already installed (installed: {HotfixVersion.Format(installedBase, installedLevel)}); skipping download.");
            return new PrepareResult { AlreadyInstalled = true };
        }

        // Loop prevention: a hotfix that repeatedly fails startup is blocked.
        if (Shared.IsBlocked(updateRoot, candidate.BaseVersion, candidate.HotfixLevel))
        {
            var failures = Shared.FailureCount(updateRoot, candidate.BaseVersion, candidate.HotfixLevel);
            KodoDiagnostics.LogDebug($"Hotfix {candidate.TagName} blocked after {failures} failed attempts; staying on last known-good HF{installedLastKnownGood}.");
            throw new InvalidOperationException(
                $"Hotfix {candidate.TagName} failed {failures} times and is blocked. The installation stays on the last known-good hotfix.");
        }

        var downloadsRoot = Path.Combine(hotfixRoot, "downloads");
        var safeTag = candidate.TagName.Trim().Replace('/', '-').Replace('\\', '-');
        foreach (var c in Path.GetInvalidFileNameChars()) safeTag = safeTag.Replace(c, '_');
        var downloadPath = Path.Combine(downloadsRoot, safeTag, "package.zip");

        var client = httpClient ?? UpdateService.SharedHttpClient;
        await DownloadAsync(client, candidate, downloadPath, progress, ct).ConfigureAwait(false);

        var rid = HotfixDiscovery.GetCurrentPlatformRid();
        var verify = VerifyPackage(downloadPath, installedBase, installedLevel, rid);
        if (!verify.Ok || verify.Manifest is null || verify.ManifestJson is null)
            throw new InvalidDataException(verify.Error ?? "Hotfix verification failed.");

        var kodoExeName = OperatingSystem.IsWindows() ? "Kodo.exe" : "Kodo";
        var kodoExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, kodoExeName);
        var txPath = StageVerifiedPackage(
            downloadPath, verify.ManifestJson, verify.Manifest,
            hotfixRoot, targetDir, kodoExePath,
            kodoPidOverride ?? Environment.ProcessId, restartAfterUpdate: true,
            previousHotfixLevel: installedLevel,
            previousLastKnownGood: installedLastKnownGood);

        HotfixTransaction? tx = null;
        try
        {
            if (Shared.TryParseTransaction(await File.ReadAllTextAsync(txPath, ct).ConfigureAwait(false), out tx, out _))
            { /* parsed for caller convenience */ }
        }
        catch { }
        return new PrepareResult { TransactionPath = txPath, Transaction = tx };
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024 * 1024;
        return bytes >= mb ? $"{bytes / mb:0.#} MB" : $"{bytes / 1024.0:0} KB";
    }
}

// ---------------------------------------------------------------------------
// Part 8: Startup confirmation (was HotfixRecovery.cs)
// ---------------------------------------------------------------------------

// Phase 4 Kodo side: startup confirmation and commit.
//
// Flow: updater applies files -> marks awaitingConfirmation -> restarts Kodo.
// Kodo reaches a reliable startup point (App startup, deferred) and calls
// ConfirmStartupAsync: if the installed files still match the hotfix manifest,
// the hotfix is committed (state lastKnownGood advances, backups and staging
// data are cleaned up). If files do not match, Kodo reports the transaction
// path so the caller can hand off to KodoUpdater --rollback and exit.
//
// Never throws: confirmation must never break startup.
internal static class HotfixRecovery
{
    internal sealed class ConfirmResult
    {
        public List<string> Confirmed { get; } = new();
        public List<string> Closed { get; } = new();
        public string? NeedsRollbackTxPath { get; set; }
        public string? Error { get; set; }
    }

    internal static async Task<ConfirmResult> ConfirmStartupAsync(
        string? updateRootOverride = null,
        string? targetDirOverride = null,
        string? currentBaseOverride = null)
    {
        var result = new ConfirmResult();
        try
        {
            var updateRoot = updateRootOverride ?? UpdateService.UpdateRoot;
            var targetDir = targetDirOverride ?? AppContext.BaseDirectory;
            var currentBase = currentBaseOverride ?? HotfixVersion.CurrentBaseVersion;
            var hotfixRoot = Path.Combine(updateRoot, "hotfix");
            if (!Directory.Exists(hotfixRoot)) return result;

            KodoDiagnostics.LogDebug("Hotfix startup confirmation started");
            string[] txFiles;
            try { txFiles = Directory.GetFiles(hotfixRoot, "transaction.json", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            foreach (var txPath in txFiles)
            {
                try { await ConfirmOneAsync(txPath, updateRoot, targetDir, currentBase, result).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Hotfix confirmation error for {txPath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.GetType().Name;
            KodoDiagnostics.LogDebug("Hotfix startup confirmation failed", ex);
        }
        return result;
    }

    private static async Task ConfirmOneAsync(
        string txPath, string updateRoot, string targetDir, string currentBase, ConfirmResult result)
    {
        string rawJson;
        try { rawJson = await File.ReadAllTextAsync(txPath).ConfigureAwait(false); }
        catch { return; }
        if (!Shared.IsHotfixTransactionJson(rawJson)) return;
        if (!Shared.TryParseTransaction(rawJson, out var tx, out _) || tx is null) return;

        var status = tx.Status ?? "";
        var needsConfirmation =
            string.Equals(status, Kodo.HotfixShared.HotfixTransactionStatus.Applied, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, Kodo.HotfixShared.HotfixTransactionStatus.AwaitingConfirmation, StringComparison.OrdinalIgnoreCase);
        if (!needsConfirmation) return;

        // A hotfix line for an older base is obsolete (a full release moved on):
        // close it out without touching state.
        if (!Shared.AreSameBaseVersion(tx.BaseVersion, currentBase))
        {
            await WriteBackAsync(txPath, Shared.MarkFailed(rawJson, $"Base version changed (tx {tx.BaseVersion}, app {currentBase}).")).ConfigureAwait(false);
            DeleteStageDir(txPath, updateRoot);
            result.Closed.Add(tx.TransactionId);
            KodoDiagnostics.LogDebug($"Hotfix transaction {tx.TransactionId} closed: base version changed.");
            return;
        }

        string manifestJson;
        try
        {
            if (string.IsNullOrWhiteSpace(tx.ManifestPath) || !File.Exists(tx.ManifestPath))
            {
                await WriteBackAsync(txPath, Shared.MarkFailed(rawJson, "Manifest missing from staging area.")).ConfigureAwait(false);
                DeleteStageDir(txPath, updateRoot);
                result.Closed.Add(tx.TransactionId);
                KodoDiagnostics.LogDebug($"Hotfix transaction {tx.TransactionId} closed: manifest missing.");
                return;
            }
            manifestJson = await File.ReadAllTextAsync(tx.ManifestPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Hotfix manifest unreadable for {tx.TransactionId}: {ex.Message}");
            return;
        }

        if (!Shared.TryParseManifest(manifestJson, out var manifest, out _) || manifest is null)
        {
            result.NeedsRollbackTxPath ??= txPath;
            KodoDiagnostics.LogDebug("Hotfix confirmation failed: manifest malformed; rollback required.");
            return;
        }

        // The process is alive, but that alone is not confirmation: the files
        // on disk must still match the manifest (catches updater interruption
        // mid-copy and disk corruption).
        var targetRoot = Path.GetFullPath(targetDir);
        foreach (var file in manifest.Files)
        {
            string dest;
            try { dest = Path.GetFullPath(Path.Combine(targetRoot, file.Path.Trim())); }
            catch
            {
                result.NeedsRollbackTxPath ??= txPath;
                KodoDiagnostics.LogDebug("Hotfix confirmation failed: invalid path; rollback required.");
                return;
            }
            if (!Shared.IsInsideDirectory(dest, targetRoot) || !Shared.VerifyFileSha256(dest, file.Sha256))
            {
                result.NeedsRollbackTxPath ??= txPath;
                KodoDiagnostics.LogDebug("Hotfix confirmation failed: installed files do not match manifest; rollback required.");
                return;
            }
        }

        // Commit: advance lastKnownGood, mark confirmed, clear failures, clean up.
        var statePath = Path.Combine(updateRoot, HotfixStateStore.StateFileName);
        try
        {
            var state = HotfixStateStore.LoadOrDefault(statePath);
            if (!HotfixVersion.AreSameBaseVersion(state.BaseVersion, tx.BaseVersion))
                state.BaseVersion = tx.BaseVersion;
            state.HotfixLevel = Math.Max(state.HotfixLevel, tx.HotfixLevel);
            state.LastKnownGoodHotfix = tx.HotfixLevel;
            HotfixStateStore.Save(state, statePath);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Hotfix state commit failed for {tx.TransactionId}: {ex.Message}");
            return;
        }

        Shared.ClearFailures(updateRoot, tx.BaseVersion, tx.HotfixLevel);
        await WriteBackAsync(txPath, Shared.MarkConfirmed(rawJson, DateTime.UtcNow)).ConfigureAwait(false);
        KodoDiagnostics.LogDebug("Hotfix startup confirmed");
        KodoDiagnostics.LogDebug($"Hotfix committed: {HotfixVersion.Format(tx.BaseVersion, tx.HotfixLevel)}");
        DeleteStageDir(txPath, updateRoot);
        result.Confirmed.Add(tx.TransactionId);
    }

    private static async Task WriteBackAsync(string txPath, string json)
    {
        try
        {
            var tmp = txPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, txPath, overwrite: true);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Hotfix transaction write-back failed: {ex.Message}");
        }
    }

    internal static void DeleteStageDir(string txPath, string updateRoot)
    {
        try
        {
            var stageDir = Path.GetFullPath(Path.GetDirectoryName(txPath) ?? "");
            var hotfixRoot = Path.GetFullPath(Path.Combine(updateRoot, "hotfix"));
            if (!Shared.IsInsideDirectory(stageDir, hotfixRoot) || string.Equals(stageDir, hotfixRoot, StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.Exists(stageDir))
            {
                Directory.Delete(stageDir, recursive: true);
                KodoDiagnostics.LogDebug($"Hotfix staging data removed: {stageDir}");
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Hotfix staging cleanup failed: {ex.Message}");
        }
    }
}
