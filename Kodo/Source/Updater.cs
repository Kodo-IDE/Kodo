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

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        LastIncompatibleReason = null;
        var fromLatest = await TryCheckLatestAsync(ct).ConfigureAwait(false);
        if (fromLatest is not null) return fromLatest;
        return await TryCheckReleasesListAsync(ct).ConfigureAwait(false);
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
            // Hotfix tags (hotfix/2.1.0/1) are handled by hotfix discovery, never as full releases.
            if (HotfixDiscovery.IsHotfixTag(release.TagName)) return null;
            if (!IsNewerVersion(release.TagName, KodoDiagnostics.AppVersion)) return null;
            var asset = PickInstallerAsset(release.Assets);
            if (asset is null) return null;
            var sha256 = await TryResolveAssetChecksumAsync(release.Assets, asset.Name, ct).ConfigureAwait(false);
            return new UpdateInfo(release.TagName, release.HtmlUrl ?? ReleaseNotesUrl, asset.BrowserDownloadUrl, asset.Name, asset.Size, sha256);
        }
        catch { return null; }
    }

    private static async Task<UpdateInfo?> TryCheckReleasesListAsync(CancellationToken ct)
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
                if (release.Draft || release.Prerelease) continue;
                // Hotfix tags (hotfix/2.1.0/1) are handled by hotfix discovery, never as full releases.
                if (HotfixDiscovery.IsHotfixTag(release.TagName)) continue;
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

    // --- Phase 2: hotfix discovery (identify only; no download/apply) ---

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
            // Hotfix state lives under the per-user update directory, which a
            // full release does not replace. Rebase it before staging a hotfix
            // for the new app version so KodoUpdater does not mistake the old
            // release's state for a base-version mismatch.
            //
            // A cumulative installer already contains hotfixes for the new base,
            // so the rebased level starts at the shipped floor, not HF0.
            // Retained manifests belong to the old base and are discarded.
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
        // Cumulative packages bake hotfixes into the installed files. The
        // effective level is the max of the persisted state and the shipped
        // stamp: a fresh cumulative install has no state file yet (floor
        // applies), and reinstalling a cumulative package over an older
        // hotfixed install must not report a stale lower level.
        var shippedFloor = HotfixVersion.GetShippedHotfixLevel(appBaseDirOverride, currentBase);
        var normalizedBase = HotfixVersion.NormalizeBaseVersion(state.BaseVersion) ?? currentBase;
        var effectiveLevel = Math.Max(Math.Max(0, state.HotfixLevel), shippedFloor);
        var freshSeed = !File.Exists(stateFile);
        // When the shipped floor dominates (fresh cumulative seed or adopt on
        // reinstall-newer), retain the stamp's file list so later drift has
        // something trustworthy to check against.
        if ((shippedFloor > state.HotfixLevel || freshSeed) &&
            Shared.TryGetShippedStamp(targetDir, currentBase, out var stampLevel, out var stampFiles) &&
            stampLevel == shippedFloor && stampFiles is not null && stampFiles.Count > 0)
        {
            HotfixFileReconciler.RetainStampFiles(updateRoot, normalizedBase, shippedFloor, stampFiles);
        }
        // Reconcile the claimed level against the files on disk. A reinstall
        // of an older package leaves persisted state (and the install-dir
        // stamp) behind; without this check Kodo would silently claim a level
        // whose files are gone, and the updater would never offer it again.
        if (effectiveLevel > 0 &&
            !HotfixFileReconciler.InstalledFilesMatch(updateRoot, targetDir, normalizedBase, effectiveLevel))
        {
            KodoDiagnostics.LogDebug(
                $"Installed files do not match the retained HF{effectiveLevel} manifest; clamping hotfix state to HF0 so the updater can repair the installation.");
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

    // Display string for logs, diagnostic reports, and Settings/About, e.g.
    // "Hotfix 3" (or "Hotfix 0" when nothing is applied). Shares
    // ResolveInstalledHotfix's side-effect profile (idempotent state merge);
    // never throws — falls back to "Hotfix 0". Overrides exist for tests so
    // they never touch the real per-user state.
    internal static string InstalledHotfixDisplay(
        string? currentBaseOverride = null,
        string? statePathOverride = null,
        string? appBaseDirOverride = null)
    {
        try
        {
            var (_, level) = ResolveInstalledHotfix(currentBaseOverride, statePathOverride, appBaseDirOverride);
            return $"Hotfix {Math.Max(0, level)}";
        }
        catch { return "Hotfix 0"; }
    }

    internal static async Task<GitHubRelease[]?> FetchGitHubReleasesAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ReleasesListUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease[]>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    public static async Task<HotfixCandidate?> CheckForHotfixAsync(CancellationToken ct = default)
    {
        KodoDiagnostics.LogDebug("Hotfix check started");
        var (installedBase, installedLevel) = ResolveInstalledHotfix();
        KodoDiagnostics.LogDebug($"Installed base version: {installedBase}");
        KodoDiagnostics.LogDebug($"Installed hotfix: HF{installedLevel}");

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

        var compatible = HotfixDiscovery.FindCompatibleHotfixes(releases, installedBase, installedLevel);
        foreach (var c in compatible)
            KodoDiagnostics.LogDebug($"Found compatible hotfix: HF{c.HotfixLevel} ({c.TagName})");

        // Loop prevention: drop hotfixes blocked after repeated startup failures.
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
        CancellationToken ct = default)
    {
        var hotfix = await CheckForHotfixAsync(ct).ConfigureAwait(false);
        if (hotfix is null) return null;
        // When background installation is enabled, download/verify/stage up
        // front so the dialog opens ready ("Restart & Update"), mirroring the
        // full-release background flow. The user still chooses when to apply:
        // nothing is installed silently.
        if (installInBackground)
        {
            try
            {
                var prepared = await HotfixStaging.PrepareAsync(hotfix, progress: null, ct).ConfigureAwait(false);
                if (!prepared.AlreadyInstalled && prepared.TransactionPath is not null)
                {
                    UpdateDialog.ShowForHotfix(hotfix, prepared.TransactionPath);
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

    // --- Phase 3: hotfix application (download/verify/stage here; KodoUpdater applies) ---

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
            var checksumAsset = assets.FirstOrDefault(a =>
                a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals("CHECKSUMS", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
            if (checksumAsset is null || !IsValidReleaseAsset(checksumAsset))
                return null;
            using var response = await Http.GetAsync(checksumAsset.BrowserDownloadUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 1024 * 1024) return null;
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
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
                if (string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase))
                    return hash.ToLowerInvariant();
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
        var remoteParts = ParseVersionParts(remote);
        var localParts = ParseVersionParts(local);
        if (remoteParts is null || localParts is null)
            return false;
        for (var i = 0; i < Math.Max(remoteParts.Length, localParts.Length); i++)
        {
            var r = i < remoteParts.Length ? remoteParts[i] : 0;
            var l = i < localParts.Length ? localParts[i] : 0;
            if (r != l) return r > l;
        }
        return false;
    }

    private static int[]? ParseVersionParts(string tag)
    {
        var core = tag.Trim();
        if (core.Length > 0 && (core[0] == 'v' || core[0] == 'V')) core = core[1..];
        var dashIndex = core.IndexOf('-');
        if (dashIndex >= 0) core = core[..dashIndex];
        var plusIndex = core.IndexOf('+');
        if (plusIndex >= 0) core = core[..plusIndex];
        var segments = core.Split('.');
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
            if (!int.TryParse(segments[i], out parts[i])) return null;
        return parts.Length > 0 ? parts : null;
    }

    private static bool ReadAutoUpdateFlag(Func<AutoUpdateSettings, bool> sel, bool fallback)
    {
        try
        {
            var path = KodoPaths.SettingsFilePath("kodosettings.json");
            if (!File.Exists(path)) return fallback;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            var settings = JsonSerializer.Deserialize<AutoUpdateSettings>(json);
            return settings is null ? fallback : sel(settings);
        }
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

    // Managed Linux archive installs (our tarball: marker present, updater
    // beside the app, user-writable) update automatically like Windows.
    // AppImage mounts, root-owned .deb installs, and foreign layouts stay on
    // the download-notify path. The override exists for tests.
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

    // Only tarballs can be applied automatically; a managed install offered
    // a .deb/AppImage still goes through the manual dialog.
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
            // Hotfix downloads use .partial files too; transaction/backup dirs are
            // retained for Phase 4 rollback and must not be deleted here.
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
            Process.Start(fallback);
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

    // Phase 4: hand a failed/unverifiable hotfix to the updater for rollback,
    // then exit so files are not locked. Never returns.
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
            Process.Start(fallback);
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
        CancellationToken ct = default)
    {
        CleanupStaleArtifacts();
        var update = await CheckForUpdateAsync(ct).ConfigureAwait(false);
        if (update is null) return null;
        onUpdateFound?.Invoke(update);

        if (installInBackground)
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

internal sealed class UpdateDialog : Window
{
    private readonly DialogThemePalette _palette;
    private readonly Color _accentColor;
    private readonly Color _accentForeground;
    private readonly UpdateInfo? _update;
    private string? _stagedInstallerPath;
    private readonly HotfixCandidate? _hotfix;
    private string? _stagedHotfixTxPath;
    private readonly bool _isHotfixMode;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly Button _primaryButton;
    private readonly Button _laterButton;
    private bool _canClose = true;
    private bool _isDownloading;
    private bool _isReady;

    // Full-release dialog flow: automatic ("Restart & Update") except on
    // Linux installs that cannot self-apply (unmanaged install, or a
    // non-tarball asset), which stay on the manual ("Show in Folder") flow.
    private readonly bool _linuxManualOnly;

    public UpdateDialog(UpdateInfo update, string? stagedInstallerPath = null)
    {
        _update = update;
        _stagedInstallerPath = stagedInstallerPath;
        _linuxManualOnly = UpdateService.IsLinuxNotifyOnly ||
            (OperatingSystem.IsLinux() && !UpdateService.IsLinuxAutoInstallAsset(update.AssetName));
        _palette = ThemeResolver.GetCurrentPalette();
        (_accentColor, _accentForeground) = AccentResolver.GetCurrentAccent();

        Title = "Kodo - Update Available";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = new SolidColorBrush(_palette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var iconBadge = new Border
        {
            Background = new SolidColorBrush(_accentColor),
            CornerRadius = new CornerRadius(8),
            Width = 40, Height = 40,
            Child = new TextBlock { Text = "↑", FontSize = 20, Foreground = new SolidColorBrush(_accentForeground), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var titleText = new TextBlock
        {
            Text = $"Kodo {update.Version} is available",
            FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { iconBadge, titleText } };

        _statusText = new TextBlock
        {
            Text = stagedInstallerPath is not null && File.Exists(stagedInstallerPath)
                ? (_linuxManualOnly
                    ? UpdateService.LinuxReadyBlurb(stagedInstallerPath)
                    : "Update downloaded and ready to install. Choose Restart & Update when you're ready.")
                : "A new version of Kodo has been published. Update now to get the latest fixes and features.",
            FontSize = 13, Foreground = new SolidColorBrush(_palette.TextMuted), TextWrapping = TextWrapping.Wrap,
        };
        var notesLink = new TextBlock { Text = "View release notes", FontSize = 12, Foreground = new SolidColorBrush(_accentColor), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        notesLink.PointerPressed += (_, _) => OpenUrl(update.ReleaseNotesUrl);

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 8, IsVisible = false, Foreground = new SolidColorBrush(_accentColor), Background = new SolidColorBrush(_palette.BadgeBg), CornerRadius = new CornerRadius(4) };

        _laterButton = new Button { Content = "Later", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8), Background = new SolidColorBrush(_palette.BadgeBg), Foreground = new SolidColorBrush(_palette.TextMuted), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        _laterButton.Click += (_, _) => Close();

        _primaryButton = new Button
        {
            Content = stagedInstallerPath is not null
                ? (_linuxManualOnly ? "Show in Folder" : "Restart & Update")
                : "Download Update",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(_accentColor),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        };
        _primaryButton.Click += async (_, _) => await OnPrimaryClickAsync();

        if (stagedInstallerPath is not null && File.Exists(stagedInstallerPath)) _isReady = true;

        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(_laterButton);
        Grid.SetColumn(_primaryButton, 1);
        buttonRow.Children.Add(_primaryButton);

        var headerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };
        var footerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };

        var content = new StackPanel { Spacing = 12, Children = { headerRow, headerDivider, _statusText, notesLink, _progressBar, footerDivider, buttonRow } };
        Content = new Border { Background = new SolidColorBrush(_palette.SurfaceDeep), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(16), Child = content };
    }

    protected override void OnClosing(WindowClosingEventArgs e) { if (!_canClose) e.Cancel = true; base.OnClosing(e); }

    public static void ShowFor(UpdateInfo update, string? stagedPath = null)
    {
        Dispatcher.UIThread.Post(() => ShowDialog(new UpdateDialog(update, stagedPath)));
    }

    public static void ShowForHotfix(HotfixCandidate hotfix, string? stagedTxPath = null)
    {
        Dispatcher.UIThread.Post(() => ShowDialog(new UpdateDialog(hotfix, stagedTxPath)));
    }

    private static void ShowDialog(UpdateDialog dialog)
    {
        Window? owner = null;
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = desktop.MainWindow;
            if (main is { IsVisible: true }) owner = main;
        }
        if (owner is not null) dialog.Show(owner); else dialog.Show();
    }

    public UpdateDialog(HotfixCandidate hotfix, string? stagedTxPath = null)
    {
        _hotfix = hotfix;
        _isHotfixMode = true;
        _stagedHotfixTxPath = stagedTxPath;
        _palette = ThemeResolver.GetCurrentPalette();
        (_accentColor, _accentForeground) = AccentResolver.GetCurrentAccent();
        var display = HotfixVersion.Format(hotfix.BaseVersion, hotfix.HotfixLevel);

        Title = "Kodo - Hotfix Available";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = new SolidColorBrush(_palette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var iconBadge = new Border
        {
            Background = new SolidColorBrush(_accentColor),
            CornerRadius = new CornerRadius(8),
            Width = 40, Height = 40,
            Child = new TextBlock { Text = "↑", FontSize = 20, Foreground = new SolidColorBrush(_accentForeground), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var titleText = new TextBlock
        {
            Text = $"Kodo {display} hotfix is available",
            FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { iconBadge, titleText } };

        _statusText = new TextBlock
        {
            Text = stagedTxPath is not null && File.Exists(stagedTxPath)
                ? "Hotfix downloaded and verified. Choose Restart & Update when you're ready."
                : $"A hotfix for Kodo {hotfix.BaseVersion} is available (HF{hotfix.HotfixLevel}). Download now to get the latest fixes.",
            FontSize = 13, Foreground = new SolidColorBrush(_palette.TextMuted), TextWrapping = TextWrapping.Wrap,
        };
        var notesLink = new TextBlock { Text = "View release notes", FontSize = 12, Foreground = new SolidColorBrush(_accentColor), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        notesLink.PointerPressed += (_, _) => OpenUrl(hotfix.ReleaseNotesUrl);

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 8, IsVisible = false, Foreground = new SolidColorBrush(_accentColor), Background = new SolidColorBrush(_palette.BadgeBg), CornerRadius = new CornerRadius(4) };

        _laterButton = new Button { Content = "Later", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8), Background = new SolidColorBrush(_palette.BadgeBg), Foreground = new SolidColorBrush(_palette.TextMuted), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        _laterButton.Click += (_, _) => Close();

        _primaryButton = new Button
        {
            Content = stagedTxPath is not null ? "Restart & Update" : "Download Hotfix",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(_accentColor),
            Foreground = new SolidColorBrush(_accentForeground),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        };
        _primaryButton.Click += async (_, _) => await OnPrimaryClickAsync();

        if (stagedTxPath is not null && File.Exists(stagedTxPath)) _isReady = true;

        var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonRow.Children.Add(_laterButton);
        Grid.SetColumn(_primaryButton, 1);
        buttonRow.Children.Add(_primaryButton);

        var headerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };
        var footerDivider = new Border { Height = 1, Background = new SolidColorBrush(_palette.Border), Opacity = 0.9, Margin = new Thickness(0, 4) };

        var content = new StackPanel { Spacing = 12, Children = { headerRow, headerDivider, _statusText, notesLink, _progressBar, footerDivider, buttonRow } };
        Content = new Border { Background = new SolidColorBrush(_palette.SurfaceDeep), BorderBrush = new SolidColorBrush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(16), Child = content };
    }

    private async Task OnPrimaryClickAsync()
    {
        if (_isHotfixMode)
        {
            await OnHotfixPrimaryClickAsync();
            return;
        }
        if (_isReady && _stagedInstallerPath is not null && File.Exists(_stagedInstallerPath))
        {
            if (_linuxManualOnly)
            {
                UpdateService.OpenFolderInFileManager(_stagedInstallerPath);
                _statusText.Text = UpdateService.LinuxReadyBlurb(_stagedInstallerPath);
                return;
            }
            _canClose = false;
            _primaryButton.IsEnabled = false;
            _laterButton.IsEnabled = false;
            _statusText.Text = "Launching updater… Kodo will restart shortly.";
            _progressBar.IsVisible = true;
            _progressBar.IsIndeterminate = true;
            await Task.Delay(300);
            try { UpdateService.PrepareAndLaunchUpdate(_stagedInstallerPath, _update!.Version, restartAfterUpdate: true, expectedSha256: _update!.Sha256); }
            catch (Exception ex)
            {
                _statusText.Text = $"Couldn't start updater: {ex.Message}";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
            }
            return;
        }
        await BeginDownloadAsync();
    }

    private async Task BeginDownloadAsync()
    {
        if (_isDownloading) return;
        _isDownloading = true;
        _canClose = false;
        _primaryButton.IsEnabled = false;
        _laterButton.IsEnabled = false;
        _primaryButton.Content = "Downloading…";
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _statusText.Text = "Downloading the update…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            _progressBar.Value = p.Fraction;
            _statusText.Text = $"Downloading… {p.Label}";
        });

        try
        {
            _stagedInstallerPath = await UpdateService.DownloadInstallerAsync(_update!, progress);
            _isDownloading = false;
            _isReady = true;
            _progressBar.IsVisible = false;
            if (_linuxManualOnly)
            {
                _statusText.Text = UpdateService.LinuxReadyBlurb(_stagedInstallerPath);
                _primaryButton.Content = "Show in Folder";
            }
            else
            {
                _statusText.Text = "Download complete. Ready to install – choose Restart & Update when you're ready.";
                _primaryButton.Content = "Restart & Update";
            }
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _laterButton.Content = "Later";
            _canClose = true;
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            _statusText.Text = ex is InvalidDataException && ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase)
                ? "The update failed integrity verification and was discarded. Try again; if it persists, download manually from the releases page."
                : "The update couldn't be downloaded. Check your connection and try again.";
            _primaryButton.Content = "Retry";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _progressBar.IsVisible = false;
            _canClose = true;
            KodoDiagnostics.WriteDiagnosticLog("UpdateDialog.BeginDownloadAsync", ex, false, "Warning", "AutoUpdate");
        }
    }

    private async Task OnHotfixPrimaryClickAsync()
    {
        if (_hotfix is null) return;
        if (_isReady && _stagedHotfixTxPath is not null && File.Exists(_stagedHotfixTxPath))
        {
            _canClose = false;
            _primaryButton.IsEnabled = false;
            _laterButton.IsEnabled = false;
            _statusText.Text = "Launching updater… Kodo will restart shortly.";
            _progressBar.IsVisible = true;
            _progressBar.IsIndeterminate = true;
            await Task.Delay(300);
            try { UpdateService.LaunchUpdaterAndExit(_stagedHotfixTxPath); }
            catch (Exception ex)
            {
                _statusText.Text = $"Couldn't start updater: {ex.Message}";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
            }
            return;
        }
        await BeginHotfixDownloadAsync();
    }

    private async Task BeginHotfixDownloadAsync()
    {
        if (_hotfix is null) return;
        if (_isDownloading) return;
        _isDownloading = true;
        _canClose = false;
        _primaryButton.IsEnabled = false;
        _laterButton.IsEnabled = false;
        _primaryButton.Content = "Downloading…";
        _progressBar.IsVisible = true;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _statusText.Text = "Downloading the hotfix…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            _progressBar.Value = p.Fraction;
            _statusText.Text = $"Downloading… {p.Label}";
        });

        try
        {
            var prepared = await HotfixStaging.PrepareAsync(_hotfix, progress);
            if (prepared.AlreadyInstalled || prepared.TransactionPath is null)
            {
                _statusText.Text = "This hotfix is already installed.";
                _primaryButton.Content = "Close";
                _primaryButton.IsEnabled = true;
                _laterButton.IsEnabled = true;
                _canClose = true;
                _isDownloading = false;
                return;
            }
            _stagedHotfixTxPath = prepared.TransactionPath;
            _isDownloading = false;
            _isReady = true;
            _progressBar.IsVisible = false;
            _statusText.Text = "Hotfix downloaded and verified. Ready to apply – choose Restart & Update when you're ready.";
            _primaryButton.Content = "Restart & Update";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _laterButton.Content = "Later";
            _canClose = true;
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            _statusText.Text = ex is UnauthorizedAccessException
                ? ex.Message
                : ex is InvalidDataException
                    ? "The hotfix failed verification and was discarded. The current installation is untouched."
                    : "The hotfix couldn't be downloaded. Check your connection and try again.";
            _primaryButton.Content = "Retry";
            _primaryButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            _progressBar.IsVisible = false;
            _canClose = true;
            KodoDiagnostics.WriteDiagnosticLog("UpdateDialog.BeginHotfixDownloadAsync", ex, false, "Warning", "HotfixUpdate");
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }
}

internal static class DialogPalette
{
    public static readonly Color Surface = Color.Parse("#1E1E1E");
    public static readonly Color SurfaceDeep = Color.Parse("#1A1A1A");
    public static readonly Color Border = Color.Parse("#3A3A3A");
    public static readonly Color BadgeBg = Color.Parse("#2B2B2B");
    public static readonly Color Text = Color.Parse("#F4F4F4");
    public static readonly Color TextMuted = Color.Parse("#A0A0A0");
    public static readonly Color TextDim = Color.Parse("#606060");
    public static readonly Color TokenBlue = Color.Parse("#9CDCFE");
    public static readonly Color TokenOrange = Color.Parse("#CE9178");
}

internal static class AccentResolver
{
    private const string DefaultAccentHex = "#8C00FF";
    private const string SettingsFileName = "kodosettings.json";
    public static (Color Accent, Color Foreground) GetCurrentAccent()
    {
        var hex = ResolveAccentHex();
        Color accent;
        try { accent = Color.Parse(hex); } catch { accent = Color.Parse(DefaultAccentHex); }
        return (accent, GetAccentForeground(accent));
    }
    private static string ResolveAccentHex()
    {
        var settings = LoadAccentSettings();
        return settings.AccentColorMode switch
        {
            "theme" => string.IsNullOrWhiteSpace(settings.CachedThemeAccentHex) ? DefaultAccentHex : settings.CachedThemeAccentHex,
            "windows" => GetWindowsAccentColor() ?? "#0078D4",
            "custom" => string.IsNullOrWhiteSpace(settings.CustomAccentHex) ? DefaultAccentHex : settings.CustomAccentHex,
            _ => DefaultAccentHex,
        };
    }
    private static AccentSettings LoadAccentSettings()
    {
        try
        {
            var path = KodoPaths.SettingsFilePath(SettingsFileName);
            if (!File.Exists(path)) return new AccentSettings();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AccentSettings();
            return JsonSerializer.Deserialize<AccentSettings>(json) ?? new AccentSettings();
        }
        catch { return new AccentSettings(); }
    }
    [SupportedOSPlatform("windows")]
    private static string? GetWindowsAccentColorWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int raw)
            {
                var r = raw & 0xFF; var g = (raw >> 8) & 0xFF; var b = (raw >> 16) & 0xFF;
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { } return null;
    }
    private static string? GetWindowsAccentColor() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GetWindowsAccentColorWindows() : null;
    private static Color GetAccentForeground(Color accent)
    {
        static double Lin(byte ch) { var s = ch / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Lin(accent.R) + 0.7152 * Lin(accent.G) + 0.0722 * Lin(accent.B);
        var whiteContrast = 1.05 / (luminance + 0.05);
        var blackContrast = (luminance + 0.05) / 0.05;
        return whiteContrast >= blackContrast ? Colors.White : Colors.Black;
    }
}

internal static class ThemeResolver
{
    private const string SettingsFileName = "kodosettings.json";
    private static readonly DialogThemePalette DarkPalette = new(Color.Parse("#1E1E1E"), Color.Parse("#1A1A1A"), Color.Parse("#3A3A3A"), Color.Parse("#2B2B2B"), Color.Parse("#F4F4F4"), Color.Parse("#A0A0A0"), Color.Parse("#606060"));
    private static readonly DialogThemePalette LightPalette = new(Color.Parse("#F3F3F3"), Color.Parse("#FFFFFF"), Color.Parse("#D7DCE5"), Color.Parse("#E3E8F1"), Color.Parse("#202124"), Color.Parse("#5F6B7A"), Color.Parse("#8A8A8A"));
    public static DialogThemePalette GetCurrentPalette()
    {
        var settings = LoadThemeSettings();
        return settings.ThemeName switch
        {
            "Light" => LightPalette, "Dark" => DarkPalette,
#pragma warning disable CA1416
            "System" => IsWindowsLightTheme() ? LightPalette : DarkPalette,
#pragma warning restore CA1416
            _ => ResolveExtensionPalette(settings),
        };
    }
    private static DialogThemePalette ResolveExtensionPalette(ThemeSettings settings)
    {
        var bgHex = settings.CachedThemeWindowBackgroundHex;
        if (string.IsNullOrWhiteSpace(bgHex)) return DarkPalette;
        Color bg; try { bg = Color.Parse(bgHex); } catch { return DarkPalette; }
        var isLight = IsLightColor(bg);
        return isLight ? new DialogThemePalette(bg, Lighten(bg, 0.04), Blend(Color.Parse("#D7DCE5"), bg, 0.5), Blend(Color.Parse("#E3E8F1"), bg, 0.4), Color.Parse("#202124"), Color.Parse("#5F6B7A"), Color.Parse("#8A8A8A"))
                       : new DialogThemePalette(bg, Darken(bg, 0.04), Blend(Color.Parse("#3A3A3A"), bg, 0.5), Blend(Color.Parse("#2B2B2B"), bg, 0.4), Color.Parse("#F4F4F4"), Color.Parse("#A0A0A0"), Color.Parse("#606060"));
    }
    private static bool IsLightColor(Color c) { static double Lin(byte ch) { var s = ch / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } var luminance = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B); return luminance > 0.4; }
    private static Color Lighten(Color c, double a) { var r = (byte)Math.Clamp(c.R + (255 - c.R) * a, 0, 255); var g = (byte)Math.Clamp(c.G + (255 - c.G) * a, 0, 255); var b = (byte)Math.Clamp(c.B + (255 - c.B) * a, 0, 255); return Color.Parse($"#{r:X2}{g:X2}{b:X2}"); }
    private static Color Darken(Color c, double a) { var r = (byte)Math.Clamp(c.R * (1 - a), 0, 255); var g = (byte)Math.Clamp(c.G * (1 - a), 0, 255); var b = (byte)Math.Clamp(c.B * (1 - a), 0, 255); return Color.Parse($"#{r:X2}{g:X2}{b:X2}"); }
    private static Color Blend(Color a, Color b, double t) { var r = (byte)(a.R + (b.R - a.R) * t); var g = (byte)(a.G + (b.G - a.G) * t); var bl = (byte)(a.B + (b.B - a.B) * t); return Color.Parse($"#{r:X2}{g:X2}{bl:X2}"); }
    [SupportedOSPlatform("windows")] private static bool IsWindowsLightTheme() { try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); if (key?.GetValue("AppsUseLightTheme") is int raw) return raw != 0; } catch { } return false; }
    private static ThemeSettings LoadThemeSettings()
    {
        try
        {
            var path = KodoPaths.SettingsFilePath(SettingsFileName);
            if (!File.Exists(path)) return new ThemeSettings();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new ThemeSettings();
            return JsonSerializer.Deserialize<ThemeSettings>(json) ?? new ThemeSettings();
        }
        catch { return new ThemeSettings(); }
    }

}

internal sealed class AppUpdateScheduler
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isManualCheckInProgress;
    private readonly Func<bool> _installInBackground;
    public AppUpdateScheduler(Func<bool> isEnabled, Func<bool> isManualCheckInProgress, Func<bool> installInBackground) { _isEnabled = isEnabled; _isManualCheckInProgress = isManualCheckInProgress; _installInBackground = installInBackground; _timer.Tick += async (_, _) => await OnTickAsync().ConfigureAwait(true); }
    public void UpdateLifecycle() { _timer.Stop(); if (_isEnabled()) _timer.Start(); }
    public void Stop() => _timer.Stop();
    private async Task OnTickAsync()
    {
        if (!_isEnabled() || _isManualCheckInProgress()) return;
        try
        {
            // Full releases take precedence; only check the hotfix channel when
            // no full release was offered, mirroring the startup background
            // check. Hotfixes were previously startup-only and never rechecked
            // on this timer.
            var full = await UpdateService.CheckAndHandleUpdateAsync(installInBackground: _installInBackground()).ConfigureAwait(true);
            if (full is null)
                await UpdateService.CheckAndHandleHotfixAsync(installInBackground: _installInBackground()).ConfigureAwait(true);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Periodic update check failed", ex); }
    }
}
