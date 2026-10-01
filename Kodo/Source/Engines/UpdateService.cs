// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shared = Kodo.HotfixShared.HotfixShared;

namespace Kodo;

internal static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Kodo-IDE/Kodo/releases/latest";
    private const string ReleasesListUrl = "https://api.github.com/repos/Kodo-IDE/Kodo/releases";
    private const string ReleaseNotesUrl = "https://github.com/Kodo-IDE/Kodo/releases";
    private static string UserAgent => $"Kodo/{KodoDiagnostics.AppVersion} (https://github.com/Kodo-IDE/Kodo)";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions TransactionJsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default, bool includeBeta = false)
    {
        LastIncompatibleReason = null;
        var fromLatest = await TryCheckLatestAsync(ct).ConfigureAwait(false);
        if (fromLatest is not null) return fromLatest;
        return await TryCheckReleasesListAsync(ct, includeBeta).ConfigureAwait(false);
    }

    private static async Task<UpdateInfo?> TryCheckLatestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;
            if (release.Draft || release.Prerelease) return null;
            if (HotfixDiscovery.IsHotfixTag(release.TagName)) return null;
            if (!IsNewerVersion(release.TagName, KodoDiagnostics.AppVersion)) return null;
            var asset = PickInstallerAsset(release.Assets);
            if (asset is null) return null;
            var sha256 = await TryResolveAssetChecksumAsync(release.Assets, asset.Name, ct).ConfigureAwait(false);
            return new UpdateInfo(release.TagName, release.HtmlUrl ?? ReleaseNotesUrl, asset.BrowserDownloadUrl, asset.Name, asset.Size, sha256);
        }
        catch { return null; }
    }

    private static async Task<UpdateInfo?> TryCheckReleasesListAsync(CancellationToken ct, bool includeBeta = false)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesListUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<GitHubRelease[]>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (releases is null || releases.Length == 0) return null;
            foreach (var release in releases)
            {
                if (release is null || string.IsNullOrWhiteSpace(release.TagName)) continue;
                if (HotfixDiscovery.IsHotfixTag(release.TagName)) continue;
                var beta = IsBetaVersionTag(release.TagName);
                if (release.Draft || (release.Prerelease && !beta) || (beta && !includeBeta)) continue;
                if (!IsNewerVersion(release.TagName, KodoDiagnostics.AppVersion)) continue;
                var asset = PickInstallerAsset(release.Assets);
                if (asset is null) continue;
                var sha256 = await TryResolveAssetChecksumAsync(release.Assets, asset.Name, ct).ConfigureAwait(false);
                return new UpdateInfo(release.TagName, release.HtmlUrl ?? ReleaseNotesUrl, asset.BrowserDownloadUrl, asset.Name, asset.Size, sha256);
            }
            return null;
        }
        catch { return null; }
    }

    internal static string? LastIncompatibleReason { get; private set; }

    internal static bool IsBetaVersionTag(string? tag) =>
        HotfixVersion.NormalizeBaseVersion(tag)?.EndsWith("-BETA", StringComparison.OrdinalIgnoreCase) == true;

    internal static (string BaseVersion, int HotfixLevel) ResolveInstalledHotfix(
        string? currentBaseOverride = null,
        string? statePathOverride = null,
        string? appBaseDirOverride = null)
    {
        var currentBase = currentBaseOverride ?? HotfixVersion.CurrentBaseVersion;
        var state = HotfixStateStore.LoadOrDefault(statePathOverride, appBaseDirOverride);
        var stateFile = string.IsNullOrWhiteSpace(statePathOverride) ? HotfixStateStore.DefaultPath : statePathOverride;
        string updateRoot;
        try { updateRoot = Path.GetDirectoryName(Path.GetFullPath(stateFile)) ?? UpdateRoot; }
        catch { updateRoot = UpdateRoot; }
        var targetDir = string.IsNullOrWhiteSpace(appBaseDirOverride) ? AppContext.BaseDirectory : appBaseDirOverride;
        if (!HotfixVersion.AreSameBaseVersion(state.BaseVersion, currentBase))
        {
            var shipped = HotfixVersion.GetShippedHotfixLevel(appBaseDirOverride, currentBase);
            state.BaseVersion = currentBase;
            state.HotfixLevel = shipped;
            state.LastKnownGoodHotfix = shipped;
            HotfixFileReconciler.ClearRetained(updateRoot);
            try
            {
                HotfixStateStore.Save(state, statePathOverride);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Could not reset hotfix state for the current base version", ex);
                return ("", 0);
            }
            return (currentBase, shipped);
        }
        var shippedFloor = HotfixVersion.GetShippedHotfixLevel(appBaseDirOverride, currentBase);
        var normalizedBase = HotfixVersion.NormalizeBaseVersion(state.BaseVersion) ?? currentBase;
        var effectiveLevel = Math.Max(Math.Max(0, state.HotfixLevel), shippedFloor);
        var freshSeed = !File.Exists(stateFile);
        if ((shippedFloor > state.HotfixLevel || freshSeed) &&
            Shared.TryGetShippedStamp(targetDir, currentBase, out var stampLevel, out var stampFiles) &&
            stampLevel == shippedFloor && stampFiles is not null && stampFiles.Count > 0)
        {
            HotfixFileReconciler.RetainStampFiles(updateRoot, normalizedBase, shippedFloor, stampFiles);
        }
        if (effectiveLevel > 0 &&
            !HotfixFileReconciler.InstalledFilesMatch(updateRoot, targetDir, normalizedBase, effectiveLevel))
        {
            KodoDiagnostics.LogDebug(
                $"Installed files do not match the retained {HotfixVersion.Format(normalizedBase, effectiveLevel)} manifest; clearing hotfix state so the updater can repair the installation.");
            state.BaseVersion = normalizedBase;
            state.HotfixLevel = 0;
            state.LastKnownGoodHotfix = 0;
            HotfixFileReconciler.ClearRetained(updateRoot);
            try
            {
                HotfixStateStore.Save(state, statePathOverride);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Could not persist reconciled hotfix level", ex);
            }
            return (normalizedBase, 0);
        }
        if (effectiveLevel != state.HotfixLevel || freshSeed)
        {
            state.BaseVersion = normalizedBase;
            state.HotfixLevel = effectiveLevel;
            state.LastKnownGoodHotfix = Math.Max(state.LastKnownGoodHotfix, effectiveLevel);
            try
            {
                HotfixStateStore.Save(state, statePathOverride);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Could not persist merged hotfix level", ex);
            }
        }
        return (normalizedBase, effectiveLevel);
    }

    internal static string InstalledVersionDisplay(
        string? currentBaseOverride = null,
        string? statePathOverride = null,
        string? appBaseDirOverride = null)
    {
        try
        {
            var (baseVersion, level) = ResolveInstalledHotfix(currentBaseOverride, statePathOverride, appBaseDirOverride);
            var display = HotfixVersion.Format(baseVersion, level, HotfixVersion.DisplayChannel);
            return string.IsNullOrEmpty(display) ? KodoDiagnostics.AppVersion : display;
        }
        catch { return KodoDiagnostics.AppVersion; }
    }

    internal static async Task<GitHubRelease[]?> FetchGitHubReleasesAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ReleasesListUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease[]>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    public static async Task<HotfixCandidate?> CheckForHotfixAsync(CancellationToken ct = default, bool includeBeta = false)
    {
        KodoDiagnostics.LogDebug("Hotfix check started");
        var (installedBase, installedLevel) = ResolveInstalledHotfix();
        KodoDiagnostics.LogDebug($"Installed base version: {installedBase}");
        KodoDiagnostics.LogDebug($"Installed version: {HotfixVersion.Format(installedBase, installedLevel)}");

        GitHubRelease[]? releases;
        try
        {
            releases = await FetchGitHubReleasesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Hotfix check failed to fetch releases", ex);
            return null;
        }
        if (releases is null) return null;

        var compatible = HotfixDiscovery.FindCompatibleHotfixes(releases, installedBase, installedLevel,
                installKind: HotfixStaging.GetLinuxInstallKind())
            .Where(candidate => includeBeta || !IsBetaVersionTag(candidate.BaseVersion))
            .ToArray();
        foreach (var c in compatible)
            KodoDiagnostics.LogDebug($"Found compatible hotfix: {HotfixVersion.Format(c.BaseVersion, c.HotfixLevel)} ({c.TagName})");

        HotfixCandidate? selected = null;
        var blockedNewest = false;
        foreach (var c in compatible)
        {
            if (Shared.IsBlocked(UpdateRoot, c.BaseVersion, c.HotfixLevel))
            {
                blockedNewest = true;
                KodoDiagnostics.LogDebug($"Hotfix {c.TagName} blocked after repeated failures; not offered.");
                continue;
            }
            blockedNewest = false;
            selected = c;
        }

        if (selected is null)
        {
            KodoDiagnostics.LogDebug(blockedNewest
                ? "No newer hotfix available (newest compatible hotfix is blocked after failures)"
                : "No newer hotfix available");
            return null;
        }

        KodoDiagnostics.LogDebug($"Selected hotfix: {HotfixVersion.Format(selected.BaseVersion, selected.HotfixLevel)} ({selected.TagName})");
        return selected;
    }

    public static async Task<UpdateCheckResult> CheckForAnyUpdateAsync(CancellationToken ct = default)
    {
        var full = await CheckForUpdateAsync(ct).ConfigureAwait(false);
        if (full is not null)
        {
            KodoDiagnostics.LogDebug($"Full release {full.Version} available; it takes precedence over hotfixes.");
            return UpdateCheckResult.Create(full, null);
        }
        var hotfix = await CheckForHotfixAsync(ct).ConfigureAwait(false);
        return UpdateCheckResult.Create(null, hotfix);
    }

    public static async Task<HotfixCandidate?> CheckAndHandleHotfixAsync(
        bool installInBackground = false,
        CancellationToken ct = default,
        bool includeBeta = false)
    {
        var hotfix = await CheckForHotfixAsync(ct, includeBeta).ConfigureAwait(false);
        if (hotfix is null) return null;
        var isBetaHotfix = HotfixVersion.NormalizeBaseVersion(hotfix.BaseVersion)?
            .EndsWith("-BETA", StringComparison.OrdinalIgnoreCase) == true;
        if (installInBackground && !isBetaHotfix && !hotfix.RequiresManualPackageInstall)
        {
            try
            {
                var prepared = await HotfixStaging.PrepareAsync(hotfix, progress: null, ct).ConfigureAwait(false);
                if (!prepared.AlreadyInstalled && prepared.TransactionPath is not null)
                {
                    LaunchUpdaterAndExit(prepared.TransactionPath);
                    return hotfix;
                }
                if (prepared.AlreadyInstalled) return null;
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Background hotfix staging failed; showing manual dialog", ex);
            }
        }
        UpdateDialog.ShowForHotfix(hotfix);
        return hotfix;
    }

    internal static HttpClient SharedHttpClient => Http;

    public static Task<string> DownloadHotfixPackageAsync(
        HotfixCandidate candidate,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var safeTag = candidate.TagName.Trim().Replace('/', '-').Replace('\\', '-');
        foreach (var c in Path.GetInvalidFileNameChars()) safeTag = safeTag.Replace(c, '_');
        var destPath = Path.Combine(HotfixStaging.DownloadsRoot, safeTag, "package.zip");
        return HotfixStaging.DownloadAsync(Http, candidate, destPath, progress, ct);
    }

    public static async Task<string> PrepareAndLaunchHotfixAsync(
        HotfixCandidate candidate,
        bool restartAfterUpdate = true,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var prepared = await HotfixStaging.PrepareAsync(candidate, progress, ct).ConfigureAwait(false);
        if (prepared.AlreadyInstalled || prepared.TransactionPath is null)
            throw new InvalidOperationException($"Hotfix {candidate.TagName} is already installed.");
        KodoDiagnostics.LogDebug("Kodo shutdown requested for hotfix apply; launching updater.");
        LaunchUpdaterAndExit(prepared.TransactionPath);
        return prepared.TransactionPath;
    }

    private static GitHubAsset? PickInstallerAsset(GitHubAsset[]? assets)
    {
        if (assets is null || assets.Length == 0) return null;
        assets = assets.Where(IsValidReleaseAsset).ToArray();
        if (assets.Length == 0) return null;
        if (OperatingSystem.IsLinux())
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var archTokens = arch == "arm64"
                ? new[] { "arm64", "aarch64" }
                : new[] { "x64", "x86_64", "amd64" };
            GitHubAsset? MatchStrict(Func<GitHubAsset, bool> pred) =>
                assets.FirstOrDefault(a => archTokens.Any(t => a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) && pred(a));
            var tarball = MatchStrict(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && (a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)));
            if (tarball is not null) return tarball;
            var appImage = MatchStrict(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));
            if (appImage is not null) return appImage;
            var deb = MatchStrict(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase));
            if (deb is not null) return deb;
            LastIncompatibleReason = $"No compatible build available (arch={arch}, need linux {string.Join("/", archTokens)} asset).";
            KodoDiagnostics.LogDebug(LastIncompatibleReason);
            return null;
        }
        var preferred = assets.FirstOrDefault(a => a.Name.StartsWith("Kodo-", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith("-Installer.exe", StringComparison.OrdinalIgnoreCase));
        if (preferred is not null) return preferred;
        preferred = assets.FirstOrDefault(a => a.Name.StartsWith("Kodo", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (preferred is not null) return preferred;
        return assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsValidReleaseAsset(GitHubAsset? asset)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.Name) || asset.Size <= 0 ||
            !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> TryResolveAssetChecksumAsync(GitHubAsset[]? assets, string assetName, CancellationToken ct)
    {
        try
        {
            if (assets is null || assets.Length == 0 || string.IsNullOrWhiteSpace(assetName))
                return null;
            var checksumAssets = assets.Where(a =>
                a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals("CHECKSUMS", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => string.Equals(a.Name, assetName + ".sha256", StringComparison.OrdinalIgnoreCase));
            foreach (var checksumAsset in checksumAssets)
            {
                if (!IsValidReleaseAsset(checksumAsset)) continue;
                using var response = await Http.GetAsync(checksumAsset.BrowserDownloadUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > 1024 * 1024) continue;
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith('#')) continue;
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    var hash = parts[0].Trim().TrimStart('*');
                    var file = parts[^1].Trim().TrimStart('*');
                    file = file.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    file = Path.GetFileName(file);
                    if (hash.Length == 64 && hash.All(Uri.IsHexDigit) &&
                        string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase))
                        return hash.ToLowerInvariant();
                }
            }
            return null;
        }
        catch { return null; }
    }

    internal static string? ComputeFileSha256(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                hasher.AppendData(buffer, 0, read);
            return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
        }
        catch { return null; }
    }

    internal static bool VerifyFileSha256(string path, string expectedHex)
    {
        var actual = ComputeFileSha256(path);
        return actual is not null && string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    internal static bool IsCachedInstallerValid(string path, UpdateInfo update)
    {
        try
        {
            if (!File.Exists(path) || string.IsNullOrWhiteSpace(update.Sha256)) return false;
            var length = new FileInfo(path).Length;
            if (length < 1024 * 1024) return false;
            if (update.AssetSizeBytes > 0 && length != update.AssetSizeBytes) return false;
            return VerifyFileSha256(path, update.Sha256);
        }
        catch { return false; }
    }

    internal static bool IsNewerVersion(string remote, string local)
    {
        if (!TryParseComparableVersion(remote, out var remoteParts, out var remoteHotfix))
            return false;
        if (!TryParseComparableVersion(local, out var localParts, out var localHotfix))
            return false;
        for (var i = 0; i < Math.Max(remoteParts.Length, localParts.Length); i++)
        {
            var r = i < remoteParts.Length ? remoteParts[i] : 0;
            var l = i < localParts.Length ? localParts[i] : 0;
            if (r != l) return r > l;
        }
        if (remoteHotfix != localHotfix) return remoteHotfix > localHotfix;
        return !IsBetaVersionTag(remote) && IsBetaVersionTag(local);
    }

    private static bool TryParseComparableVersion(string tag, out int[] parts, out int hotfixLevel)
    {
        parts = Array.Empty<int>();
        hotfixLevel = 0;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (!HotfixVersion.TryParseVersion(tag, out var baseVersion, out hotfixLevel)) return false;
        if (string.IsNullOrEmpty(baseVersion)) return false;
        var core = baseVersion.EndsWith("-BETA", StringComparison.OrdinalIgnoreCase)
            ? baseVersion[..^5]
            : baseVersion;
        var segments = core.Split('.');
        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
            if (!int.TryParse(segments[i], out parsed[i])) return false;
        if (parsed.Length == 0) return false;
        parts = parsed;
        return true;
    }

    private static bool ReadAutoUpdateFlag(Func<AutoUpdateSettings, bool> sel, bool fallback)
    {
        try { return sel(SettingsStore.AutoUpdate); }
        catch { return fallback; }
    }

    public static bool IsAutoUpdateEnabledInSettings() => ReadAutoUpdateFlag(s => s.AutoUpdateAppEnabled, true);
    public static bool IsAutoUpdateInBackgroundEnabledInSettings() => ReadAutoUpdateFlag(s => s.AutoUpdateAppInBackgroundEnabled, false);

    [Obsolete("Resident updater removed – no Task Scheduler registration needed.")]
    public static void EnsureAutostartRegistered() => KodoDiagnostics.LogDebug("EnsureAutostartRegistered no-op (resident updater removed)");
    [Obsolete("Resident updater removed")]
    public static void RemoveAutostartRegistration() => KodoDiagnostics.LogDebug("RemoveAutostartRegistration no-op");

    internal static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
    internal static string StagingRoot => Path.Combine(UpdateRoot, "staging");
    internal static string TransactionDir => Path.Combine(UpdateRoot, "transactions");

    internal static bool IsLinuxNotifyOnly => OperatingSystem.IsLinux() && !IsManagedLinuxInstall();

    internal static bool IsManagedLinuxInstall(string? appBaseDirOverride = null)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var dir = string.IsNullOrWhiteSpace(appBaseDirOverride) ? AppContext.BaseDirectory : appBaseDirOverride;
            if (!File.Exists(Path.Combine(dir, Shared.ManagedInstallMarkerFileName))) return false;
            if (!File.Exists(Path.Combine(dir, "KodoUpdater")) && !File.Exists(Path.Combine(dir, "kodoUpdater")))
                return false;
            return Shared.IsWritableDirectory(dir);
        }
        catch { return false; }
    }

    internal static bool IsLinuxAutoInstallAsset(string? assetName) =>
        !string.IsNullOrWhiteSpace(assetName) &&
        (assetName!.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
         assetName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase));

    internal static void OpenFolderInFileManager(string path)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            return;
        }
        catch { }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex) { KodoDiagnostics.LogDebug("OpenFolderInFileManager xdg-open failed", ex); }
        }
    }

    internal static string LinuxReadyBlurb(string stagedPath) =>
        stagedPath.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
            ? "Download complete. Install it with e.g. sudo dpkg -i <file> (or your software center), then restart Kodo."
            : stagedPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
                ? "Download complete. Make it executable (chmod +x), move it where you keep apps, then restart Kodo from it."
                : "Download complete. Extract the archive over your install folder (keeping the Kodo binary executable with chmod +x), then restart Kodo.";

    internal static void CleanupStaleArtifacts()
    {
        try
        {
            if (Directory.Exists(TransactionDir))
            {
                foreach (var f in Directory.GetFiles(TransactionDir, "*.json"))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(24)) File.Delete(f);
                    }
                    catch { }
                }
            }
            if (Directory.Exists(StagingRoot))
            {
                foreach (var f in Directory.GetFiles(StagingRoot, "*.partial", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(48)) File.Delete(f);
                    }
                    catch { }
                }
            }
            var hotfixDownloads = Path.Combine(UpdateRoot, "hotfix", "downloads");
            if (Directory.Exists(hotfixDownloads))
            {
                foreach (var f in Directory.GetFiles(hotfixDownloads, "*.partial", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(48)) File.Delete(f);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static string SanitizeVersionForPath(string version)
    {
        var s = version.Trim().TrimStart('v', 'V');
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace('/', '_').Replace('\\', '_');
        return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var versionDir = Path.Combine(StagingRoot, SanitizeVersionForPath(update.Version));
        Directory.CreateDirectory(versionDir);

        var safeName = SanitizeFileName(update.AssetName);
        if (!Uri.TryCreate(update.AssetDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update asset URL must be an HTTPS GitHub release URL.");
        if (OperatingSystem.IsWindows() && !safeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) safeName += ".exe";
        var finalPath = Path.Combine(versionDir, safeName);
        var partialPath = finalPath + ".partial";

        if (IsCachedInstallerValid(finalPath, update))
        {
            KodoDiagnostics.LogDebug($"Update installer already staged and verified: {finalPath}");
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            return finalPath;
        }

        using var response = await Http.GetAsync(update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Update download redirected away from HTTPS.");

        var totalBytes = response.Content.Headers.ContentLength ?? update.AssetSizeBytes;
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        using var hasher = string.IsNullOrWhiteSpace(update.Sha256)
            ? null
            : System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            hasher?.AppendData(buffer, 0, read);
            readTotal += read;
            if (progress is not null && totalBytes > 0)
            {
                var fraction = Math.Clamp((double)readTotal / totalBytes, 0, 1);
                var label = $"{FormatBytes(readTotal)} / {FormatBytes(totalBytes)}";
                progress.Report(new UpdateDownloadProgress(fraction, label));
            }
        }

        await fileStream.FlushAsync(ct).ConfigureAwait(false);
        fileStream.Close();

        var partialInfo = new FileInfo(partialPath);
        if (!partialInfo.Exists || partialInfo.Length < 1024 * 1024)
            throw new InvalidDataException($"Download incomplete or too small ({partialInfo.Length} bytes): {partialPath}");
        if (update.AssetSizeBytes > 0 && partialInfo.Length != update.AssetSizeBytes)
        {
            try { File.Delete(partialPath); } catch { }
            throw new InvalidDataException($"Update download size mismatch for {update.AssetName}. Expected {update.AssetSizeBytes} bytes, got {partialInfo.Length}.");
        }

        if (hasher is not null)
        {
            var actual = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
            var expected = update.Sha256!.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                try { File.Delete(partialPath); } catch { }
                throw new InvalidDataException($"Update checksum mismatch for {update.AssetName}. Expected {expected}, got {actual}. The download may be corrupt or tampered with; it was discarded.");
            }
        }

        try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
        File.Move(partialPath, finalPath);

        if (!OperatingSystem.IsWindows() && finalPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var mode = File.GetUnixFileMode(finalPath);
                mode |= System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute;
                File.SetUnixFileMode(finalPath, mode);
            }
            catch (Exception ex) { KodoDiagnostics.LogDebug("AppImage chmod +x failed", ex); }
        }

        KodoDiagnostics.LogDebug($"Update staged: {finalPath} ({FormatBytes(partialInfo.Length)})");
        return finalPath;
    }

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024 * 1024;
        return bytes >= mb ? $"{bytes / mb:0.#} MB" : $"{bytes / 1024.0:0} KB";
    }

    public static string CreateUpdateTransaction(string installerPath, string version, bool restartAfterUpdate = true, string? expectedSha256 = null)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("Staged installer not found", installerPath);
        var fi = new FileInfo(installerPath);
        if (fi.Length < 1024 * 1024) throw new InvalidDataException($"Staged installer too small ({fi.Length} bytes)");
        expectedSha256 ??= ComputeFileSha256(installerPath);

        Directory.CreateDirectory(TransactionDir);
        var transactionId = Guid.NewGuid().ToString("N");
        var kodoExeName = OperatingSystem.IsWindows() ? "Kodo.exe" : "Kodo";
        var kodoExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, kodoExeName);
        var baseDir = AppContext.BaseDirectory;
        var candidateKodo = Path.Combine(baseDir, kodoExeName);
        if (File.Exists(candidateKodo)) kodoExePath = Path.GetFullPath(candidateKodo);

        var tx = new UpdateTransaction(
            TransactionId: transactionId,
            InstallerPath: Path.GetFullPath(installerPath),
            KodoExePath: Path.GetFullPath(kodoExePath),
            KodoPid: Environment.ProcessId,
            RestartAfterUpdate: restartAfterUpdate,
            CreatedAtUtc: DateTime.UtcNow,
            Version: version,
            Sha256: expectedSha256,
            ExpectedSize: fi.Length,
            AssetName: Path.GetFileName(installerPath));

        var txPath = Path.Combine(TransactionDir, $"{transactionId}.json");
        var json = JsonSerializer.Serialize(tx, TransactionJsonOptions);
        Shared.WriteTextAtomically(txPath, json);
        KodoDiagnostics.LogDebug($"Update transaction created {txPath} pid={tx.KodoPid}");
        return txPath;
    }

    public static void LaunchUpdaterAndExit(string transactionPath)
    {
        var updaterPath = ResolveUpdaterPath();
        var exeDir = AppContext.BaseDirectory;

        try
        {
            if (!Shared.EnsureExecutable(updaterPath))
                KodoDiagnostics.LogDebug($"Could not ensure executable permission on {updaterPath}");
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"EnsureExecutable failed for {updaterPath}", ex); }

        var psi = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = exeDir,
        };
        psi.ArgumentList.Add(transactionPath);

        try
        {
            Process.Start(psi);
        }
        catch
        {
            var fallback = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"\"{transactionPath}\"",
                UseShellExecute = true,
                WorkingDirectory = exeDir,
            };
            try { Process.Start(fallback); }
            catch (Exception ex) { KodoDiagnostics.LogDebug($"KodoUpdater shell fallback failed: {ex.Message}"); }
        }

        KodoDiagnostics.LogDebug($"KodoUpdater launched for {transactionPath} – exiting Kodo PID {Environment.ProcessId}");
        Thread.Sleep(400);
        Environment.Exit(0);
    }

    internal static string ResolveUpdaterPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var updaterFileName = OperatingSystem.IsWindows() ? "KodoUpdater.exe" : "KodoUpdater";
        var updaterPath = Path.Combine(exeDir, updaterFileName);
        if (!File.Exists(updaterPath))
        {
            updaterPath = Path.Combine(exeDir, "KodoUpdater", updaterFileName);
            if (!File.Exists(updaterPath))
                throw new FileNotFoundException($"{updaterFileName} not found", updaterPath);
        }
        return updaterPath;
    }

    public static void LaunchUpdaterForRollback(string transactionPath)
    {
        var updaterPath = ResolveUpdaterPath();
        var exeDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = exeDir,
        };
        psi.ArgumentList.Add("--rollback");
        psi.ArgumentList.Add(transactionPath);

        try
        {
            Process.Start(psi);
        }
        catch
        {
            var fallback = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--rollback \"{transactionPath}\"",
                UseShellExecute = true,
                WorkingDirectory = exeDir,
            };
            try { Process.Start(fallback); }
            catch (Exception ex) { KodoDiagnostics.LogDebug($"KodoUpdater rollback shell fallback failed: {ex.Message}"); }
        }

        KodoDiagnostics.LogDebug($"KodoUpdater launched for rollback of {transactionPath} – exiting Kodo PID {Environment.ProcessId}");
        Thread.Sleep(400);
        Environment.Exit(0);
    }

    public static void PrepareAndLaunchUpdate(string installerPath, string version, bool restartAfterUpdate = true, string? expectedSha256 = null)
    {
        var txPath = CreateUpdateTransaction(installerPath, version, restartAfterUpdate, expectedSha256);
        LaunchUpdaterAndExit(txPath);
    }

    public static async Task<UpdateInfo?> CheckAndHandleUpdateAsync(
        bool installInBackground,
        Action<UpdateInfo>? onUpdateFound = null,
        CancellationToken ct = default,
        bool includeBeta = false)
    {
        CleanupStaleArtifacts();
        var update = await CheckForUpdateAsync(ct, includeBeta).ConfigureAwait(false);
        if (update is null) return null;
        onUpdateFound?.Invoke(update);

        var isBeta = IsBetaVersionTag(update.Version);
        if (installInBackground && !isBeta)
        {
            try
            {
                var staged = await DownloadInstallerAsync(update, progress: null, ct).ConfigureAwait(false);
                UpdateDialog.ShowFor(update, staged);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Background update download failed", ex);
                UpdateDialog.ShowFor(update);
            }
        }
        else
        {
            UpdateDialog.ShowFor(update);
        }
        return update;
    }

}
