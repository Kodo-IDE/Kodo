// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{

    private async Task LoadMarketplaceExtensionsAsync()
    {
        var marketplaceExtensions = new List<MarketplaceExtension>();
        var extensionLoadErrors = new List<string>();

        await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState(), DispatcherPriority.Background);

        // Avoid pulling from disk cache unless rate limits have certainly been hit.
        // Prefer fresh GitHub pull; ETag still sent if available (304 handling does not count as cache pull).
        _marketplaceIndexETag ??= TryReadMarketplaceIndexETag();
        // Keep ETag for conditional request even without pre-loading JSON.
        // If server returns 304 we will re-read cache then (still rate-limit safe).
        var rateLimitEncountered = false;

        try
        {
            foreach (var indexUrl in MarketplaceIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                if (indexRequest.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                    indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (_marketplaceIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _marketplaceIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Marketplace index fetch",
                    async ct =>
                    {
                        using var indexResponse = await MarketplaceHttpClient.SendAsync(indexRequest, ct);
                        if ((int)indexResponse.StatusCode == 304)
                            return (304, (string?)null, (string?)null);
                        if (!indexResponse.IsSuccessStatusCode)
                        {
                            // Propagate rate limits as exception so cache fallback can be triggered.
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
                    // 304 means server confirms cache is fresh - now and only now pull from disk cache.
                    var cachedJson = await Task.Run(() => TryReadMarketplaceIndexCache()).ConfigureAwait(false);
                    if (cachedJson is not null)
                    {
                        var cachedParsed = new List<MarketplaceExtension>();
                        var cachedErrors = new List<string>();
                        await Task.Run(() => ParseAndApplyMarketplaceIndex(cachedJson, cachedParsed, cachedErrors)).ConfigureAwait(false);
                        marketplaceExtensions.Clear();
                        marketplaceExtensions.AddRange(cachedParsed);
                        extensionLoadErrors.Clear();
                        extensionLoadErrors.AddRange(cachedErrors);
                    }
                    KodoDiagnostics.LogDebug("Marketplace index: 304 Not Modified - reusing cached data.");
                    break;
                }

                if (remoteJson is null)
                    continue;

                var parsedExtensions = new List<MarketplaceExtension>();
                var parsedErrors = new List<string>();
                ParseAndApplyMarketplaceIndex(remoteJson, parsedExtensions, parsedErrors);

                if (parsedExtensions.Count == 0)
                {
                    extensionLoadErrors.Add($"Marketplace index at {indexUrl} did not contain any extensions.");
                    continue;
                }

                marketplaceExtensions.Clear();
                extensionLoadErrors.Clear();
                marketplaceExtensions.AddRange(parsedExtensions);
                extensionLoadErrors.AddRange(parsedErrors);
                TryWriteMarketplaceIndexCache(remoteJson);
                if (newETag is not null)
                {
                    _marketplaceIndexETag = newETag;
                    TryWriteMarketplaceIndexETag(newETag);
                }
                break;
            }
        }
        catch (Exception ex)
        {
            if (IsGitHubRateLimitException(ex))
            {
                rateLimitEncountered = true;
                var diskJson = await Task.Run(() => TryReadMarketplaceIndexCache()).ConfigureAwait(false);
                if (diskJson is not null)
                {
                    var fallbackExtensions = new List<MarketplaceExtension>();
                    var fallbackErrors = new List<string>();
                    await Task.Run(() => ParseAndApplyMarketplaceIndex(diskJson, fallbackExtensions, fallbackErrors)).ConfigureAwait(false);
                    // Only use fallback if it actually contains data.
                    if (fallbackExtensions.Count > 0)
                    {
                        marketplaceExtensions.Clear();
                        marketplaceExtensions.AddRange(fallbackExtensions);
                        extensionLoadErrors.Clear();
                        extensionLoadErrors.AddRange(fallbackErrors);
                        extensionLoadErrors.Add($"Marketplace index fetch rate-limited; using cached copy: {DescribeFetchFailure(ex)}");
                        KodoDiagnostics.LogDebug("Marketplace index fetch rate-limited; using disk cache.", ex);
                    }
                    else
                    {
                        extensionLoadErrors.Add($"Marketplace index fetch rate-limited and cached copy was empty: {DescribeFetchFailure(ex)}");
                    }
                }
                else
                {
                    extensionLoadErrors.Add($"Marketplace index fetch rate-limited and no cached copy available: {DescribeFetchFailure(ex)}");
                }
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState("Marketplace fetch", ex));
                // For rate limit we do NOT rethrow - allow UI to show cached data with fallback icons (lettered/embedded handled elsewhere).
                if (marketplaceExtensions.Count == 0 && rateLimitEncountered)
                {
                    // No usable data even from cache - propagate to trigger empty state; connectivity warning will explain.
                    KodoDiagnostics.LogDebug("Rate limit hit with no usable marketplace cache; marketplace will appear empty with lettered fallback.");
                }
            }
            else
            {
                // Not a rate limit - do NOT pull from cache per new policy. Fallback to embedded/lettered icon logic elsewhere will apply.
                extensionLoadErrors.Add($"Failed to load remote marketplace index: {DescribeFetchFailure(ex)}");
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState("Marketplace fetch", ex));
                // Offline / other failures should not use stale cache; marketplace will show lettered fallback, installed keeps embedded.
                if (marketplaceExtensions.Count == 0)
                    throw;
            }
        }

        Dictionary<string, string> marketplaceIconMap = [];
        var combinedForIconMap = marketplaceExtensions
            .Where(entry => !string.Equals(entry.Type, "plugin", StringComparison.OrdinalIgnoreCase))
            .Concat(_pluginsIndexEntries)
            .ToList();
        // Build icon map from combined entries directly to avoid stale MarketplaceExtensions snapshot when batch is deferred
        // Use GroupBy to avoid duplicate-key exceptions if same Id appears in both indexes (stability).
        marketplaceIconMap = combinedForIconMap
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IconUrl, StringComparer.OrdinalIgnoreCase);
        await InvokeExtensionUiAsync(() =>
        {
            SyncMarketplaceExtensionCollection(MarketplaceExtensions, combinedForIconMap);
            SyncObservableCollection(
                ExtensionLoadErrors,
                ExtensionLoadErrors.Concat(extensionLoadErrors).Distinct().ToList(),
                error => error);

            SyncMarketplaceInstallStates();
            RaiseMany(nameof(ExtensionLoadErrors), nameof(IsMarketplaceUnavailableVisible), nameof(IsMarketplacePartialErrorVisible), nameof(IsMarketplaceEmptyVisible));
            NotifyExtensionFiltersChanged();
        });
        _ = FetchMarketplaceIconsAsync(marketplaceIconMap);
        _ = FetchInstalledExtensionIconsAsync(marketplaceIconMap);
    }

    private async Task LoadPluginsIndexAsync()
    {
        var pluginExtensions = new List<MarketplaceExtension>();
        var pluginLoadErrors = new List<string>();

        await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState(), DispatcherPriority.Background);

        // Avoid disk cache unless rate limits have certainly been hit.
        _pluginsIndexETag ??= TryReadPluginsIndexETag();

        try
        {
            foreach (var indexUrl in PluginsIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                if (indexRequest.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                    indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (_pluginsIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _pluginsIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Plugins index fetch",
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
                    var cachedJson = await Task.Run(() => TryReadPluginsIndexCache()).ConfigureAwait(false);
                    if (cachedJson is not null)
                    {
                        var cachedParsed = new List<MarketplaceExtension>();
                        var cachedErrors = new List<string>();
                        await Task.Run(() => ParseAndApplyMarketplaceIndex(cachedJson, cachedParsed, cachedErrors)).ConfigureAwait(false);
                        pluginExtensions.Clear();
                        pluginExtensions.AddRange(cachedParsed);
                        pluginLoadErrors.Clear();
                        pluginLoadErrors.AddRange(cachedErrors);
                    }
                    KodoDiagnostics.LogDebug("Plugins index: 304 Not Modified - reusing cached data.");
                    break;
                }

                if (remoteJson is null)
                    continue;

                var parsedErrors = new List<string>();
                ParseAndApplyMarketplaceIndex(remoteJson, pluginExtensions, parsedErrors);

                if (pluginExtensions.Count == 0)
                {
                    pluginLoadErrors.Add($"Plugins index at {indexUrl} did not contain any plugins.");
                    continue;
                }

                TryWritePluginsIndexCache(remoteJson);
                if (newETag is not null)
                {
                    _pluginsIndexETag = newETag;
                    TryWritePluginsIndexETag(newETag);
                }
                break;
            }
        }
        catch (Exception ex)
        {
            if (IsGitHubRateLimitException(ex))
            {
                var diskJson = await Task.Run(() => TryReadPluginsIndexCache()).ConfigureAwait(false);
                if (diskJson is not null)
                {
                    var fallback = new List<MarketplaceExtension>();
                    var fallbackErrors = new List<string>();
                    await Task.Run(() => ParseAndApplyMarketplaceIndex(diskJson, fallback, fallbackErrors)).ConfigureAwait(false);
                    if (fallback.Count > 0)
                    {
                        pluginExtensions.Clear();
                        pluginExtensions.AddRange(fallback);
                        pluginLoadErrors.Clear();
                        pluginLoadErrors.AddRange(fallbackErrors);
                        pluginLoadErrors.Add($"Plugins index fetch rate-limited; using cached copy: {DescribeFetchFailure(ex)}");
                        KodoDiagnostics.LogDebug("Plugins index fetch rate-limited; using disk cache.", ex);
                    }
                    else
                    {
                        pluginLoadErrors.Add($"Plugins index fetch rate-limited and cached copy was empty: {DescribeFetchFailure(ex)}");
                    }
                }
                else
                {
                    pluginLoadErrors.Add($"Plugins index fetch rate-limited and no cached copy available: {DescribeFetchFailure(ex)}");
                }
            }
            else
            {
                // Not rate limited - do not pull from cache.
                pluginLoadErrors.Add($"Failed to load remote plugins index: {DescribeFetchFailure(ex)}");
                KodoDiagnostics.LogDebug("Plugins index fetch failed (not rate-limited, cache not used).", ex);
            }
        }

        Dictionary<string, string> pluginIconMap = [];
        await InvokeExtensionUiAsync(() =>
        {
            _pluginsIndexEntries = pluginExtensions;
            var combinedMarketplaceEntries = MarketplaceExtensions
                .Where(entry => !string.Equals(entry.Type, "plugin", StringComparison.OrdinalIgnoreCase))
                .Concat(_pluginsIndexEntries)
                .ToList();

            SyncMarketplaceExtensionCollection(MarketplaceExtensions, combinedMarketplaceEntries);
            SyncObservableCollection(
                ExtensionLoadErrors,
                ExtensionLoadErrors.Concat(pluginLoadErrors).Distinct().ToList(),
                error => error);

            SyncMarketplaceInstallStates();
            RaiseMany(nameof(ExtensionLoadErrors), nameof(IsMarketplaceUnavailableVisible), nameof(IsMarketplacePartialErrorVisible), nameof(IsMarketplaceEmptyVisible));
            NotifyExtensionFiltersChanged();

            pluginIconMap = MarketplaceExtensions
                .Where(entry => string.Equals(entry.Type, "plugin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.IconUrl))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().IconUrl, StringComparer.OrdinalIgnoreCase);
        });

        _ = FetchMarketplaceIconsAsync(pluginIconMap);
        _ = FetchInstalledExtensionIconsAsync(pluginIconMap);
    }

    private string? TryReadPluginsIndexCache()
    {
        try { return File.Exists(PluginsIndexCachePath) ? File.ReadAllText(PluginsIndexCachePath, System.Text.Encoding.UTF8) : null; }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not read plugins index cache.", ex); return null; }
    }

    private string? TryReadPluginsIndexETag()
    {
        try { return File.Exists(PluginsIndexETagPath) ? File.ReadAllText(PluginsIndexETagPath).Trim() : null; }
        catch { return null; }
    }

    private void TryWritePluginsIndexCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PluginsIndexCachePath)!);
            File.WriteAllText(PluginsIndexCachePath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not write plugins index cache.", ex); }
    }

    private void TryWritePluginsIndexETag(string etag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PluginsIndexETagPath)!);
            File.WriteAllText(PluginsIndexETagPath, etag);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not write plugins index ETag.", ex); }
    }

    private async Task FetchInstalledExtensionIconsAsync(IReadOnlyDictionary<string, string> marketplaceIconMap)
    {
        // Policy: All panels prefer GitHub Index icons where possible.
        // Installed panel: try GitHub icon first; on rate-limit / offline fallback to embedded .kox icon.
        // If embedded is also missing, the view will show the lettered abbreviation (HasIcon == false).
        // Snapshot on UI thread to avoid ObservableCollection cross-thread race (stability).
        List<(LoadedExtension ext, string iconUrl)> pending;
        try
        {
            pending = await Dispatcher.UIThread.InvokeAsync(() =>
                LoadedExtensions
                    .Select(ext => (ext, iconUrl: marketplaceIconMap.TryGetValue(ext.Id, out var iconUrl) ? iconUrl : string.Empty))
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.iconUrl))
                    .ToList(), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to snapshot installed extensions for icon fetch.", ex);
            return;
        }

        if (pending.Count == 0)
            return;

        var successful = new System.Collections.Concurrent.ConcurrentBag<(LoadedExtension ext, IconResult icon)>();
        var needsEmbeddedFallback = new System.Collections.Concurrent.ConcurrentBag<LoadedExtension>();

        var tasks = pending.Select(async pair =>
            {
                try
                {
                    var icon = await GetCachedIconAsync(pair.iconUrl).ConfigureAwait(false);

                    if (icon.HasValue)
                    {
                        // Prefer GitHub Index icon over embedded - collect for batched UI update.
                        successful.Add((pair.ext, icon));
                    }
                    else
                    {
                        KodoDiagnostics.LogDebug($"Icon fetch returned no data for installed extension '{pair.ext.Id}': {pair.iconUrl} - using embedded icon.");
                        needsEmbeddedFallback.Add(pair.ext);
                    }
                }
                catch (OperationCanceledException)
                {
                    needsEmbeddedFallback.Add(pair.ext);
                }
                catch (Exception ex)
                {
                    var isRateLimit = IsGitHubRateLimitException(ex);
                    var reason = isRateLimit ? "rate limit" : !HasActiveInternetConnection() ? "offline" : "network failure";
                    KodoDiagnostics.LogDebug($"Icon fetch failed for installed extension '{pair.ext.Id}' ({reason}): {pair.iconUrl} - falling back to embedded icon.", ex);
                    try { RefreshMarketplaceConnectivityState($"Icon fetch - {pair.ext.Id}", ex); } catch { }
                    needsEmbeddedFallback.Add(pair.ext);
                }
            });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Unexpected error during installed icon batch.", ex); }

        // Batch apply GitHub icons first (higher priority), then ensure embedded for failures.
        if (successful.Count > 0)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var (ext, icon) in successful)
                    {
                        try { ReplaceLoadedExtensionIcon(ext, icon); }
                        catch (Exception ex) { KodoDiagnostics.LogDebug($"Failed to apply installed icon for '{ext.Id}'.", ex); }
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex) { KodoDiagnostics.LogDebug("Failed to batch-apply installed GitHub icons.", ex); }
        }

        if (needsEmbeddedFallback.Count > 0)
        {
            var fallbackTasks = needsEmbeddedFallback.Select(ext => EnsureInstalledEmbeddedIconAsync(ext));
            try { await Task.WhenAll(fallbackTasks).ConfigureAwait(false); } catch { }
        }
    }

    private async Task EnsureInstalledEmbeddedIconAsync(LoadedExtension extension)
    {
        // Fallback helper: if GitHub icon unavailable, make sure embedded .kox icon is materialized.
        // Installed decoding already runs in ApplyLoadedExtensionsResult, but if it hasn't completed
        // or bytes are still pending, decode here so the panel does not stay on lettered fallback.
        if (extension.HasIcon)
            return;
        if (extension.IconBytes is null)
            return;

        var bytes = extension.IconBytes;
        // Decode synchronously for fallback so UI updates promptly.
        if (IsSvgContent(bytes))
        {
            try
            {
                var svg = System.Text.Encoding.UTF8.GetString(bytes);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!extension.HasIcon)
                    {
                        extension.SvgData = svg;
                        extension.IconImage?.Dispose();
                        extension.IconImage = null;
                        extension.IconBytes = null;
                        extension.NotifyIconChanged();
                    }
                });
            }
            catch { }
        }
        else
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                if (bmp.PixelSize.Width != bmp.PixelSize.Height)
                {
                    bmp.Dispose();
                    bmp = null;
                }
                if (bmp is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!extension.HasIcon)
                        {
                            var old = extension.IconImage;
                            extension.IconImage = bmp;
                            old?.Dispose();
                            extension.SvgData = null;
                            extension.IconBytes = null;
                            extension.NotifyIconChanged();
                        }
                        else
                        {
                            bmp.Dispose();
                        }
                    });
                }
            }
            catch { }
        }
    }

    private async Task FetchMarketplaceIconsAsync(
        IReadOnlyDictionary<string, string> marketplaceIconMap,
        ObservableCollection<MarketplaceExtension>? targetCollection = null)
    {
        var entries = targetCollection ?? MarketplaceExtensions;

        // Avoid in-memory cache unless rate limited - prefer fresh GitHub pull per new policy.
        // Do not pre-populate from _marketplaceIconBytesCache here; GetCachedIconAsync will handle
        // cache fallback only when a rate limit is certainly encountered.

        // Policy: All panels prefer GitHub Index icons where possible.
        // Marketplace panel: try GitHub icon first; on rate-limit / offline fallback to lettered abbreviation (HasIcon == false).
        // Snapshot on UI thread to avoid ObservableCollection cross-thread race (stability - fixes inconsistent loads).
        List<MarketplaceExtension> snapshot;
        try
        {
            snapshot = await Dispatcher.UIThread.InvokeAsync(() => entries.ToList(), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Failed to snapshot marketplace entries for icon fetch.", ex);
            return;
        }

        var pendingEntries = snapshot
            .Where(entry => entry.IconImage is null && entry.SvgData is null && marketplaceIconMap.TryGetValue(entry.Id, out _))
            .ToList();

        if (pendingEntries.Count == 0)
            return;

        var iconFailures = 0;
        var iconAttempts = 0;
        Exception? lastIconException = null;
        var rateLimited = 0;
        var successful = new System.Collections.Concurrent.ConcurrentBag<(MarketplaceExtension entry, IconResult icon)>();

        var tasks = pendingEntries.Select(async entry =>
            {
                Interlocked.Increment(ref iconAttempts);
                try
                {
                    // Guard against disposed collection / missing map entry.
                    if (!marketplaceIconMap.TryGetValue(entry.Id, out var url) || string.IsNullOrWhiteSpace(url))
                    {
                        Interlocked.Increment(ref iconFailures);
                        return;
                    }

                    var icon = await GetCachedIconAsync(url).ConfigureAwait(false);
                    if (!icon.HasValue)
                    {
                        KodoDiagnostics.LogDebug($"Icon fetch returned no data for marketplace extension '{entry.Id}': {url} - using lettered fallback.");
                        Interlocked.Increment(ref iconFailures);
                        return;
                    }

                    successful.Add((entry, icon));
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref iconFailures);
                }
                catch (Exception ex)
                {
                    if (IsGitHubRateLimitException(ex))
                        Interlocked.Increment(ref rateLimited);
                    KodoDiagnostics.LogDebug($"Icon fetch failed for marketplace extension '{entry.Id}' (will use lettered fallback): {marketplaceIconMap.GetValueOrDefault(entry.Id, string.Empty)}", ex);
                    Interlocked.Increment(ref iconFailures);
                    Interlocked.Exchange(ref lastIconException, ex);
                }
            });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Unexpected error during marketplace icon batch.", ex);
        }

        // Batch UI updates to reduce dispatcher pressure and improve stability.
        if (successful.Count > 0)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var (entry, icon) in successful)
                    {
                        try { ReplaceMarketplaceIcon(entry, icon); }
                        catch (Exception ex) { KodoDiagnostics.LogDebug($"Failed to apply icon for '{entry.Id}'.", ex); }
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Failed to batch-apply marketplace icons.", ex);
            }
        }

        if (iconAttempts > 0 && iconFailures == iconAttempts && lastIconException is not null)
        {
            var reason = rateLimited > 0 ? "rate limit" : !HasActiveInternetConnection() ? "offline" : "network failure";
            KodoDiagnostics.LogDebug(
                $"All {iconAttempts} marketplace icon fetch(es) failed due to {reason}; icons will show lettered abbreviations.",
                lastIconException);
            try { RefreshMarketplaceConnectivityState("Marketplace icon fetch", lastIconException); } catch { }
        }
        else if (iconFailures > 0)
        {
            // Partial failure - surface connectivity state but still show lettered fallback for failed entries.
            if (lastIconException is not null && (rateLimited > 0 || !HasActiveInternetConnection()))
                try { RefreshMarketplaceConnectivityState("Marketplace icon fetch", lastIconException); } catch { }
        }
    }

    private async Task<IconResult> GetCachedIconAsync(string iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return default;

        // Disk cache with TTL avoids hitting GitHub at all for recent icons (major rate-limit + stability saver).
        if (TryReadIconFromDiskCache(iconUrl, out var cachedDiskBytes))
        {
            var diskIcon = DecodeCachedIconBytes(cachedDiskBytes);
            if (diskIcon.HasValue)
            {
                _marketplaceIconBytesCache[iconUrl] = cachedDiskBytes;
                KodoDiagnostics.LogDebug($"Icon cache hit (disk TTL) for '{iconUrl}' - avoiding network.");
                return diskIcon;
            }
        }

        // Rate-limit avoidance: if we are in backoff or remaining is low, prefer cached data without hitting network.
        if (ShouldDeferDueToRateLimit())
        {
            if (_marketplaceIconBytesCache.TryGetValue(iconUrl, out var memBytes))
                return DecodeCachedIconBytes(memBytes);
            if (TryReadIconFromDiskCache(iconUrl, out var diskBytes))
            {
                _marketplaceIconBytesCache[iconUrl] = diskBytes;
                return DecodeCachedIconBytes(diskBytes);
            }
            throw new HttpRequestException("GitHub rate limit backoff active - deferring icon fetch", null, System.Net.HttpStatusCode.TooManyRequests);
        }

        if (!HasActiveInternetConnection())
        {
            // Flaky NetworkInterface check caused inconsistent loads - attempt network anyway and let
            // HttpClient failure trigger per-icon fallback (embedded/lettered) instead of whole-batch skip.
            KodoDiagnostics.LogDebug($"HasActiveInternetConnection false for '{iconUrl}' - attempting fetch anyway (fallback on failure).");
        }

        // Deduplicate concurrent fetches for same URL to improve stability and avoid thundering herd.
        // Use GetOrAdd to avoid race where two callers both miss in-flight and create duplicate tasks.
        var fetchTask = _iconFetchInFlight.GetOrAdd(iconUrl, _ => FetchIconWithRetryAsync(iconUrl));
        try
        {
            return await fetchTask.ConfigureAwait(false);
        }
        finally
        {
            // Only remove if still our task (avoid removing a newer task added while we were awaiting).
            _iconFetchInFlight.TryRemove(new KeyValuePair<string, Task<IconResult>>(iconUrl, fetchTask));
        }
    }

    private async Task<IconResult> FetchIconWithRetryAsync(string iconUrl)
    {
        // Stagger start to avoid burst hitting rate limit (jitter 20-80ms per fetch).
        try { await Task.Delay(Random.Shared.Next(20, 80)).ConfigureAwait(false); } catch { }

        await _iconFetchSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // Pre-check deferral before even attempting network.
            if (ShouldDeferDueToRateLimit())
            {
                if (_marketplaceIconBytesCache.TryGetValue(iconUrl, out var deferMem))
                    return DecodeCachedIconBytes(deferMem);
                if (TryReadIconFromDiskCache(iconUrl, out var deferDisk))
                {
                    _marketplaceIconBytesCache[iconUrl] = deferDisk;
                    return DecodeCachedIconBytes(deferDisk);
                }
                throw new HttpRequestException("Rate limit deferral active", null, System.Net.HttpStatusCode.TooManyRequests);
            }

            // Loop over raw -> api fallback candidates (raw primary to avoid api.github.com rate limit).
            Exception? lastRateLimitException = null;
            foreach (var candidateUrl in BuildIconUrlCandidates(iconUrl))
            {
                const int maxAttempts = 3;
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(GitHubOperationTimeout);
                        using var request = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                        if (IsGitHubContentsApiUrl(candidateUrl))
                            request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");

                        using var response = await MarketplaceHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                        // Track rate limit headers on every response to proactively avoid future hits.
                        UpdateRateLimitStateFromHeaders(response.Headers);
                        if (response.Headers.TryGetValues("Retry-After", out _) || response.Headers.Contains("X-RateLimit-Remaining"))
                            SetRateLimitBackoffFromHeaders(response.Headers);

                        // Rate limit - log and fall through to next candidate, don't abort whole fetch.
                        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            SetRateLimitBackoffFromHeaders(response.Headers);
                            var rlEx = new HttpRequestException($"GitHub API rate limit hit ({(int)response.StatusCode})", null, response.StatusCode);
                            KodoDiagnostics.LogDebug($"Icon fetch rate-limited ({(int)response.StatusCode}) for '{candidateUrl}' - trying next candidate if any.", rlEx);
                            lastRateLimitException = rlEx;
                            break; // break inner retry, continue to next candidate
                        }

                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            KodoDiagnostics.LogDebug($"Icon not found (404) for '{candidateUrl}' - trying next candidate if any.");
                            break; // break inner retry loop, continue to next candidate in outer foreach
                        }

                    if (!response.IsSuccessStatusCode)
                    {
                        // 5xx and 408 are transient - throw to trigger retry.
                        throw new HttpRequestException($"Icon fetch failed with {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);

                    // Validate size to avoid OOM from unexpectedly large responses.
                    if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
                    {
                        KodoDiagnostics.LogDebug($"Icon for '{candidateUrl}' has invalid size {bytes.Length} - discarding.");
                        break;
                    }

                    // Handle GitHub Contents API JSON wrapper when raw accept was not honored.
                    if (TryExtractIconFromGitHubJsonWrapper(bytes, out var unwrapped))
                        bytes = unwrapped;

                    var icon = DecodeCachedIconBytes(bytes);
                    if (!icon.HasValue)
                    {
                        KodoDiagnostics.LogDebug($"Decoded icon has no value for '{candidateUrl}' (corrupted or unsupported) - not caching.");
                        break;
                    }

                    // Only cache validated, decodable bytes - keep cache keyed by original iconUrl (stable identity).
                    _marketplaceIconBytesCache[iconUrl] = bytes;
                    TryWriteIconToDiskCache(iconUrl, bytes);
                    if (icon.SvgData is not null)
                    {
                        var svgData = _marketplaceSvgCache.GetOrAdd(iconUrl, icon.SvgData);
                        return new IconResult(null, svgData);
                    }
                    return icon;
                }
                catch (Exception ex) when (IsGitHubRateLimitException(ex))
                {
                    KodoDiagnostics.LogDebug($"Icon fetch rate-limited for '{candidateUrl}' - trying next candidate if any.", ex);
                    lastRateLimitException = ex;
                    break;
                }
                catch (Exception ex) when (IsTransientIconFailure(ex) && HasActiveInternetConnection() && attempt < maxAttempts - 1)
                {
                    var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(0, 150));
                    KodoDiagnostics.LogDebug($"Transient icon fetch failure for '{candidateUrl}' (attempt {attempt + 1}/{maxAttempts}): {ex.Message} - retrying in {delay.TotalMilliseconds:F0}ms");
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Icon fetch failed for '{candidateUrl}' (will try next candidate if any): {ex.Message}");
                    break;
                }
            }
            // Candidate exhausted - continue to next candidate in BuildIconUrlCandidates
            }
            // All candidates exhausted - if any candidate hit rate limit, fallback to cache per earlier policy (point b)
            if (lastRateLimitException != null)
            {
                if (_marketplaceIconBytesCache.TryGetValue(iconUrl, out var cachedBytes))
                {
                    KodoDiagnostics.LogDebug($"All icon candidates rate-limited for '{iconUrl}'; serving from in-memory cache.");
                    return DecodeCachedIconBytes(cachedBytes);
                }
                if (TryReadIconFromDiskCache(iconUrl, out var diskBytes))
                {
                    _marketplaceIconBytesCache[iconUrl] = diskBytes;
                    KodoDiagnostics.LogDebug($"All icon candidates rate-limited for '{iconUrl}'; serving from disk cache.");
                    return DecodeCachedIconBytes(diskBytes);
                }
                throw lastRateLimitException;
            }
            return default;
        }
        finally
        {
            _iconFetchSemaphore.Release();
        }
    }

    private static bool IsTransientIconFailure(Exception ex) =>
        ex is TimeoutException ||
        ex is TaskCanceledException ||
        ex is IOException ||
        (ex is HttpRequestException hre &&
         (hre.StatusCode is null ||
          hre.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout)) ||
        (ex is InvalidDataException);

    private static void UpdateRateLimitStateFromHeaders(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        try
        {
            if (headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
            {
                lock (_rateLimitLock) _gitHubRateLimitRemaining = remaining;
                if (remaining < 10)
                    KodoDiagnostics.LogDebug($"GitHub rate limit low: {remaining} remaining.");
            }
            if (headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
            {
                var resetUtc = DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime;
                lock (_rateLimitLock) _gitHubRateLimitResetUtc = resetUtc;
            }
        }
        catch { }
    }

    private static bool ShouldDeferDueToRateLimit()
    {
        lock (_rateLimitLock)
        {
            if (_gitHubRateLimitBackoffUntilUtc > DateTime.UtcNow)
                return true;
            if (_gitHubRateLimitRemaining < 5 && _gitHubRateLimitResetUtc > DateTime.UtcNow)
                return true;
            return false;
        }
    }

    private static void SetRateLimitBackoffFromHeaders(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        try
        {
            if (headers.TryGetValues("Retry-After", out var retryValues) &&
                int.TryParse(retryValues.FirstOrDefault(), out var seconds))
            {
                lock (_rateLimitLock) _gitHubRateLimitBackoffUntilUtc = DateTime.UtcNow.AddSeconds(seconds + 1);
                KodoDiagnostics.LogDebug($"GitHub Retry-After {seconds}s - backing off icon fetches.");
                return;
            }
            // Fallback to X-RateLimit-Reset if present
            if (headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
            {
                var resetUtc = DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime;
                lock (_rateLimitLock) _gitHubRateLimitBackoffUntilUtc = resetUtc;
            }
        }
        catch { }
    }

    private static string GetIconDiskCachePath(string iconUrl)
    {
        try
        {
            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(iconUrl));
            var hex = Convert.ToHexString(hash);
            Directory.CreateDirectory(IconDiskCacheDir);
            return Path.Combine(IconDiskCacheDir, hex + ".bin");
        }
        catch { return Path.Combine(IconDiskCacheDir, "fallback.bin"); }
    }

    private static bool TryReadIconFromDiskCache(string iconUrl, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var path = GetIconDiskCachePath(iconUrl);
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            // TTL check - avoid hitting network for fresh cache
            if (DateTime.UtcNow - info.LastWriteTimeUtc > IconDiskCacheTtl)
                return false;
            bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return false;
            return true;
        }
        catch { return false; }
    }

    private static void TryWriteIconToDiskCache(string iconUrl, byte[] bytes)
    {
        try
        {
            var path = GetIconDiskCachePath(iconUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug($"Failed to write icon disk cache for '{iconUrl}'.", ex); }
    }

    private static bool TryExtractIconFromGitHubJsonWrapper(byte[] bytes, out byte[] unwrapped)
    {
        unwrapped = bytes;
        // GitHub Contents API without raw accept returns JSON with base64 "content".
        // Detect quickly to avoid parsing every binary.
        if (bytes.Length < 2 || bytes[0] != (byte)'{')
            return false;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("content", out var contentProp) ||
                contentProp.ValueKind != JsonValueKind.String)
                return false;
            var b64 = contentProp.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                return false;
            // Remove whitespace/newlines GitHub inserts.
            b64 = b64.Replace("\n", string.Empty).Replace("\r", string.Empty).Replace(" ", string.Empty);
            var decoded = Convert.FromBase64String(b64);
            if (decoded.Length > 0 && decoded.Length <= 2 * 1024 * 1024)
            {
                unwrapped = decoded;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static IconResult DecodeCachedIconBytes(byte[] bytes)
    {
        if (IsSvgContent(bytes))
        {
            try
            {
                return new IconResult(null, System.Text.Encoding.UTF8.GetString(bytes));
            }
            catch { return default; }
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            return new IconResult(new Bitmap(ms), null);
        }
        catch { return default; }
    }

    private static bool IsSvgContent(byte[] bytes)
    {
        var header = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return header.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || header.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static void ParseAndApplyMarketplaceIndex(
        string json,
        List<MarketplaceExtension> marketplaceExtensions,
        List<string> extensionLoadErrors,
        string rootPropertyName = "extensions")
    {
        var jsonOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };
        using var doc = JsonDocument.Parse(json, jsonOptions);

        JsonElement? extensionsElement = null;
        var triedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        do
        {
            if (triedProperties.Add(rootPropertyName) &&
                doc.RootElement.TryGetProperty(rootPropertyName, out var element) &&
                element.ValueKind == JsonValueKind.Array)
            {
                extensionsElement = element;
                break;
            }
            rootPropertyName = rootPropertyName == "extensions" ? "plugins" : "extensions";
        } while (true);

        if (extensionsElement is null)
            return;

        foreach (var item in extensionsElement.Value.EnumerateArray())
        {
            try
            {
                var entry = ParseMarketplaceExtension(item);
                if (string.IsNullOrWhiteSpace(entry.Id) || marketplaceExtensions.Any(e => e.Id == entry.Id))
                    continue;
                marketplaceExtensions.Add(entry);
            }
            catch (Exception itemEx)
            {
                var entryId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "?" : "?";
                extensionLoadErrors.Add($"Skipped malformed marketplace entry '{entryId}': {itemEx.Message}");
                KodoDiagnostics.LogDebug($"Skipped malformed marketplace entry '{entryId}'", itemEx);
            }
        }
    }

    private string? TryReadMarketplaceIndexCache()
    {
        try { return File.Exists(MarketplaceIndexCachePath) ? File.ReadAllText(MarketplaceIndexCachePath, System.Text.Encoding.UTF8) : null; }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not read marketplace index cache.", ex); return null; }
    }

    private string? TryReadMarketplaceIndexETag()
    {
        try { return File.Exists(MarketplaceIndexETagPath) ? File.ReadAllText(MarketplaceIndexETagPath).Trim() : null; }
        catch { return null; }
    }

    private void TryWriteMarketplaceIndexCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarketplaceIndexCachePath)!);
            File.WriteAllText(MarketplaceIndexCachePath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not write marketplace index cache.", ex); }
    }

    private static MarketplaceExtension ParseMarketplaceExtension(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        var declaredVersion = item.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
        var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        var author = item.TryGetProperty("author", out var authorElement) ? authorElement.GetString() ?? string.Empty : string.Empty;
        var description = item.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty;
        var rawDownloadUrl = NormalizeGitHubBlobViewerUrl(
            item.TryGetProperty("downloadUrl", out var downloadUrlElement) ? downloadUrlElement.GetString() ?? string.Empty : string.Empty);
        var declaredFileName = item.TryGetProperty("fileName", out var fileNameElement) ? fileNameElement.GetString() ?? string.Empty : string.Empty;
        var iconUrl = NormalizeMarketplaceIconUrl(item);
        var urlFileName = TryGetFileNameFromUrl(rawDownloadUrl);
        var bestKnownVersion = GetHighestKnownExtensionVersion(declaredVersion, declaredFileName, urlFileName);
        var canonicalFileName = GetCanonicalMarketplaceFileName(declaredFileName, urlFileName, bestKnownVersion);
        var canonicalDownloadUrl = NormalizeMarketplaceDownloadUrl(rawDownloadUrl, canonicalFileName);

        return new MarketplaceExtension
        {
            Id = id,
            Version = bestKnownVersion,
            Name = name,
            Type = type,
            Author = author,
            Description = description,
            DownloadUrl = canonicalDownloadUrl,
            FileName = canonicalFileName,
            IconUrl = iconUrl
        };
    }

    private static string NormalizeMarketplaceIconUrl(JsonElement item)
    {
        var rawIcon = string.Empty;
        foreach (var propertyName in new[] { "iconUrl", "icon", "iconPath" })
        {
            if (item.TryGetProperty(propertyName, out var iconElement) &&
                iconElement.ValueKind == JsonValueKind.String)
            {
                rawIcon = iconElement.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rawIcon))
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(rawIcon))
            return string.Empty;

        if (Uri.TryCreate(rawIcon, UriKind.Absolute, out _))
        {
            // Prefer raw.githubusercontent.com for Kodo-Extensions icons to avoid api.github.com rate limits.
            // Raw fetches do not count against the core API rate limit and have much higher quota.
            var normalized = NormalizeGitHubUrl(rawIcon);
            return TryConvertKodoApiUrlToRaw(normalized, out var raw) ? raw : normalized;
        }

        var relativePath = rawIcon.Trim().TrimStart('/');
        // Use raw for first-party repo to avoid API rate limit; api fallback is handled via conversion helper if needed.
        return $"https://raw.githubusercontent.com/{KodoExtensionsOwner}/{KodoExtensionsRepo}/main/{relativePath}";
    }

    private static bool TryConvertKodoApiUrlToRaw(string apiUrl, out string rawUrl)
    {
        rawUrl = apiUrl;
        if (!TryParseGitHubContentsUrl(apiUrl, out var owner, out var repo, out var path))
            return false;
        if (!IsKodoExtensionsRepo(owner, repo))
            return false;
        // Use main branch for raw; GitHub will redirect if needed.
        rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/main/{path}";
        return true;
    }

    private static bool TryGetAlternativeIconUrl(string url, out string altUrl)
    {
        altUrl = string.Empty;
        // raw -> api fallback (when raw branch 404, api is canonical)
        if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = url.Substring("https://raw.githubusercontent.com/".Length);
            var parts = remainder.Split('/', 4);
            if (parts.Length == 4)
            {
                var owner = parts[0];
                var repo = parts[1];
                // parts[2] is branch, parts[3] is path
                var path = parts[3];
                // Try opposite branch first, then api
                var currentBranch = parts[2];
                var altBranch = currentBranch.Equals("main", StringComparison.OrdinalIgnoreCase) ? "master" : "main";
                // Prefer api as most reliable alternative
                altUrl = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
                return true;
            }
            return false;
        }
        // api -> raw fallback
        if (TryParseGitHubContentsUrl(url, out var owner2, out var repo2, out var path2))
        {
            // Prefer raw main; raw master will be tried via next 404 loop if needed
            altUrl = $"https://raw.githubusercontent.com/{owner2}/{repo2}/main/{path2}";
            return true;
        }
        return false;
    }

    private void SyncMarketplaceInstallStates()
    {
        foreach (var installedExtension in LoadedExtensions)
            installedExtension.IsUpdateAvailable = false;

        foreach (var entry in MarketplaceExtensions)
        {
            var localExt = GetPreferredLoadedExtension(entry.Id);
            var isUpdateAvailable = localExt is not null && CompareExtensionVersions(entry.Version, localExt.Version) > 0;

            entry.SetInstalledState(localExt, isUpdateAvailable);
            if (localExt is not null)
                localExt.IsUpdateAvailable = isUpdateAvailable;
        }

        RaiseMany(nameof(AvailableExtensionUpdatesCount), nameof(IsExtensionUpdateBannerVisible), nameof(ExtensionUpdatesBannerText), nameof(AutoUpdateExtensionsStatusText));
        NotifyExtensionActionStateChanged();
        NotifyExtensionFiltersChanged();
    }

    private void NotifyExtensionFiltersChanged()
    {
        if (_extensionFilterBatchDepth > 0)
        {
            _pendingExtensionFilterNotify = true;
            return;
        }
        NotifyExtensionFiltersChangedCore();
    }

    private static void SyncMarketplaceExtensionCollection(ObservableCollection<MarketplaceExtension> target, IList<MarketplaceExtension> source)
        => SyncObservableCollection(target, source, e => e.Id, (existing, incoming) =>
        {
            if (existing.IconImage is not null && incoming.IconImage is null) incoming.IconImage = existing.IconImage; else existing.IconImage?.Dispose();
            if (existing.SvgData is not null && incoming.SvgData is null) incoming.SvgData = existing.SvgData;
        });

    private async Task InstallMarketplaceExtensionAsync(MarketplaceExtension marketplaceExtension)
    {
        if (marketplaceExtension.IsInstalling || (marketplaceExtension.IsInstalled && !marketplaceExtension.IsUpdateAvailable))
            return;

        RefreshMarketplaceConnectivityState();
        marketplaceExtension.IsInstalling = true;
        NotifyExtensionActionStateChanged();
        var action = marketplaceExtension.IsUpdateAvailable ? "Updating" : "Installing";
        marketplaceExtension.InstallButtonText = $"{action}...";
        ExtensionsStatusText = $"{action} {marketplaceExtension.Name}...";

        try
        {
            EnsureExtensionsFolder();
            var wasUpdate = marketplaceExtension.IsUpdateAvailable;
            var installedExtension = GetPreferredLoadedExtension(marketplaceExtension.Id);
            var outputPath = ResolveExtensionInstallPath(marketplaceExtension, installedExtension);

            var bytes = await RunWithGitHubTimeoutAsync(
                $"Extension download - {marketplaceExtension.Name}",
                async ct =>
                {
                    var downloadUrls = BuildExtensionDownloadUrlCandidates(marketplaceExtension.DownloadUrl);
                    foreach (var downloadUrl in downloadUrls)
                    {

                        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                        if (IsGitHubContentsApiUrl(downloadUrl))
                            downloadRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");

                        using var downloadResponse = await MarketplaceHttpClient.SendAsync(
                            downloadRequest, HttpCompletionOption.ResponseContentRead, ct);
                        if (!downloadResponse.IsSuccessStatusCode)
                            continue;

                        return await downloadResponse.Content.ReadAsByteArrayAsync(ct);
                    }

                    throw new HttpRequestException($"Unable to download extension package from any known location for {marketplaceExtension.Name}.");
                });

            ValidateDownloadedExtensionPackage(marketplaceExtension, bytes);
            DeleteInstalledExtensionSources(marketplaceExtension.Id, outputPath);
            await File.WriteAllBytesAsync(outputPath, bytes);
            NormalizeKoxManifestVersion(outputPath);

            // suppressWatchdog=true: the download already has its own
            await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
            ExtensionsStatusText = $"{marketplaceExtension.Name} {(wasUpdate ? "updated" : "installed")}.";
        }
        catch (Exception ex)
        {
            marketplaceExtension.SetInstalledState(
                GetPreferredLoadedExtension(marketplaceExtension.Id),
                marketplaceExtension.IsUpdateAvailable);
            RefreshMarketplaceConnectivityState($"Extension install - {marketplaceExtension.Name}", ex);
            ExtensionsStatusText = $"Failed to install {marketplaceExtension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Extension install - {marketplaceExtension.Name}", ex);
        }
        finally
        {
            marketplaceExtension.IsInstalling = false;
            NotifyExtensionActionStateChanged();
            SyncMarketplaceInstallStates();
        }
    }

    private static void ValidateDownloadedExtensionPackage(MarketplaceExtension marketplaceExtension, byte[] packageBytes)
    {
        using var ms = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException($"Downloaded package for {marketplaceExtension.Name} is missing manifest.json.");

        using var manifestStream = manifestEntry.Open();
        using var manifestDoc = JsonDocument.Parse(manifestStream);
        var manifest = manifestDoc.RootElement;
        var manifestId = manifest.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        var manifestVersion = manifest.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;

        if (!string.Equals(manifestId, marketplaceExtension.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded package id '{manifestId}' does not match expected id '{marketplaceExtension.Id}'.");
        }

        if (CompareExtensionVersions(manifestVersion, marketplaceExtension.Version) < 0)
        {
            throw new InvalidDataException(
                $"Downloaded package version '{manifestVersion}' is older than the marketplace version '{marketplaceExtension.Version}'.");
        }
    }

    private static IEnumerable<string> BuildExtensionDownloadUrlCandidates(string downloadUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                candidates.Add(url);
        }

        Add(downloadUrl);

        if (TryParseGitHubContentsUrl(downloadUrl, out _, out _, out var path))
        {
            var suffix = GetExtensionPackageRelativePath(path);
            Add(BuildGitHubContentsUrl("Kodo-IDE", "Kodo-Extensions", PrefixExtensionPath("Extensions", suffix)));
        }
        else if (TryParseGitHubRawUrl(downloadUrl, out _, out _, out var rawPath))
        {
            var suffix = GetExtensionPackageRelativePath(rawPath);
            Add(BuildGitHubContentsUrl("Kodo-IDE", "Kodo-Extensions", PrefixExtensionPath("Extensions", suffix)));
        }

        return candidates;
    }

    private static IEnumerable<string> BuildIconUrlCandidates(string iconUrl)
    {
        yield return iconUrl;
        if (TryParseGitHubRawUrl(iconUrl, out var owner, out var repo, out var path))
            yield return BuildGitHubContentsUrl(owner, repo, path);
    }

    private static bool TryParseGitHubContentsUrl(string url, out string owner, out string repo, out string path)
    {
        owner = string.Empty;
        repo = string.Empty;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        if (segments.Length < 6 ||
            !segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase) ||
            !segments[3].Equals("contents", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        owner = segments[1];
        repo = segments[2];
        path = string.Join("/", segments, 4, segments.Length - 4);
        return true;
    }

    private static bool TryParseGitHubRawUrl(string url, out string owner, out string repo, out string path)
    {
        owner = string.Empty;
        repo = string.Empty;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        if (segments.Length < 3)
            return false;

        owner = segments[0];
        repo = segments[1];
        path = string.Join("/", segments, 2, segments.Length - 2);
        return true;
    }

    private static string GetExtensionPackageRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', 2);
        if (segments.Length == 2 &&
            (segments[0].Equals("Extensions", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("Official_Extensions", StringComparison.OrdinalIgnoreCase)))
        {
            return segments[1];
        }

        return normalized;
    }

    private async Task UninstallExtensionAsync(LoadedExtension extension)
    {
        if (string.IsNullOrWhiteSpace(extension.SourcePath))
        {
            ExtensionsStatusText = $"Cannot uninstall {extension.Name}: missing source path.";
            return;
        }

        try
        {
            var resolvedPath = Path.GetFullPath(extension.SourcePath);
            if (!IsPathInsideDirectory(resolvedPath, ExtensionsFolderPath) &&
                !IsPathInsideDirectory(resolvedPath, ProjectExtensionsFolderPath))
            {
                ExtensionsStatusText = $"Cannot uninstall {extension.Name}: source is outside the Extensions folders.";
                return;
            }

            if (extension.IsDirectorySource)
            {
                if (Directory.Exists(resolvedPath))
                    Directory.Delete(resolvedPath, recursive: true);
            }
            else
            {
                if (File.Exists(resolvedPath))
                    File.Delete(resolvedPath);
            }

            await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
            ExtensionsStatusText = $"{extension.Name} uninstalled.";
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Failed to uninstall {extension.Name}: {ex.Message}";
            await ShowWarningDialogAsync($"Extension uninstall - {extension.Name}", ex);
        }
    }

    private async void InstallMarketplaceExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MarketplaceExtension marketplaceExtension })
            await InstallMarketplaceExtensionAsync(marketplaceExtension);
    }

    private async void UninstallExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedExtension extension }) return;

        var confirmed = await ShowConfirmationDialogAsync(
            "Uninstall extension?",
            $"This removes '{extension.Name}' from disk and disables it immediately. This can't be undone.",
            confirmLabel: "Uninstall",
            isDestructive: true);

        if (!confirmed) return;

        await UninstallExtensionAsync(extension);
    }

    private IEnumerable<LoadedExtension> SortInstalledExtensions(IEnumerable<LoadedExtension> source) =>
        SelectedExtensionSort switch
        {
            ExtensionSortModes.ReverseAlphabetical => source.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.RecentlyInstalled => source
                .OrderByDescending(e => e.InstalledOnUtc ?? DateTime.MinValue)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.UpdatesAvailable => source
                .OrderByDescending(e => e.IsUpdateAvailable)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };

    private IEnumerable<MarketplaceExtension> SortMarketplaceExtensions(IEnumerable<MarketplaceExtension> source) =>
        SelectedExtensionSort switch
        {
            ExtensionSortModes.ReverseAlphabetical => source.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.RecentlyInstalled => source
                .OrderByDescending(e => e.InstalledOnUtc.HasValue)
                .ThenByDescending(e => e.InstalledOnUtc ?? DateTime.MinValue)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ExtensionSortModes.UpdatesAvailable => source
                .OrderBy(GetMarketplaceUpdatePriority)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };

    private static int GetMarketplaceUpdatePriority(MarketplaceExtension extension)
    {
        if (extension.IsUpdateAvailable)
            return 0;
        if (!extension.IsInstalled)
            return 1;
        return 2;
    }


    private static void ReplaceMarketplaceIcon(MarketplaceExtension extension, IconResult icon)
    {
        if (icon.Bitmap is not null)
        {
            if (ReferenceEquals(extension.IconImage, icon.Bitmap)) return;
            extension.IconImage?.Dispose();
            extension.IconImage = icon.Bitmap;
            extension.SvgData = null;
        }
        else if (icon.SvgData is not null)
        {
            extension.IconImage?.Dispose();
            extension.IconImage = null;
            extension.SvgData = icon.SvgData;
        }
    }

    private void TryWriteMarketplaceIndexETag(string etag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarketplaceIndexETagPath)!);
            File.WriteAllText(MarketplaceIndexETagPath, etag);
        }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Could not write marketplace index ETag.", ex); }
    }

    private void NotifyExtensionActionStateChanged()
    {
        OnPropertyChanged(nameof(CanUpdateAllExtensions));
        OnPropertyChanged(nameof(UpdateAllExtensionsButtonText));
    }

    private MarketplaceExtension? GetMarketplaceExtensionForInstalled(LoadedExtension extension) =>
        MarketplaceExtensions.FirstOrDefault(entry =>
            entry.Id.Equals(extension.Id, StringComparison.OrdinalIgnoreCase));

    private static string GetCanonicalMarketplaceFileName(string declaredFileName, string urlFileName, string bestKnownVersion)
    {
        var baseFileName = !string.IsNullOrWhiteSpace(declaredFileName)
            ? declaredFileName
            : !string.IsNullOrWhiteSpace(urlFileName) && !string.Equals(urlFileName, "extension.kox", StringComparison.OrdinalIgnoreCase)
                ? urlFileName
                : string.Empty;

        if (string.IsNullOrWhiteSpace(baseFileName))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(bestKnownVersion))
            return baseFileName;

        var fileVersion = ExtractVersionFromName(baseFileName);
        if (string.IsNullOrWhiteSpace(fileVersion))
            return baseFileName;

        return string.Equals(fileVersion, bestKnownVersion, StringComparison.OrdinalIgnoreCase)
            ? baseFileName
            : ReplaceVersionInValue(baseFileName, fileVersion, bestKnownVersion);
    }

    private static string NormalizeMarketplaceDownloadUrl(string rawDownloadUrl, string canonicalFileName)
    {
        if (string.IsNullOrWhiteSpace(rawDownloadUrl) || string.IsNullOrWhiteSpace(canonicalFileName))
            return rawDownloadUrl;

        if (!Uri.TryCreate(rawDownloadUrl, UriKind.Absolute, out var uri))
            return rawDownloadUrl;

        var absolutePath = uri.AbsolutePath;
        var lastSlashIndex = absolutePath.LastIndexOf('/');
        if (lastSlashIndex < 0)
            return rawDownloadUrl;

        var pathPrefix = absolutePath[..(lastSlashIndex + 1)];
        var normalizedPath = pathPrefix + Uri.EscapeDataString(canonicalFileName);
        var builder = new UriBuilder(uri) { Path = normalizedPath };
        return builder.Uri.ToString();
    }

    private static bool IsKodoExtensionsRepo(string owner, string repo) =>
        owner.Equals(KodoExtensionsOwner, StringComparison.OrdinalIgnoreCase) &&
        repo.Equals(KodoExtensionsRepo, StringComparison.OrdinalIgnoreCase);

    private static void SyncObservableCollection<T, TKey>(
        ObservableCollection<T> target,
        IList<T> source,
        Func<T, TKey> keySelector,
        Action<T, T>? merge = null)
        where TKey : notnull
    {
        var dedupedSource = source
            .GroupBy(keySelector)
            .Select(group => group.First())
            .ToList();
        var sourceByKey = dedupedSource.ToDictionary(keySelector);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            var key = keySelector(target[i]);
            if (!sourceByKey.ContainsKey(key))
            {
                if (target[i] is MarketplaceExtension mex) mex.IconImage?.Dispose();
                target.RemoveAt(i);
            }
        }

        var targetIndexByKey = new Dictionary<TKey, int>();
        for (var i = 0; i < target.Count; i++)
            targetIndexByKey[keySelector(target[i])] = i;

        for (var i = 0; i < dedupedSource.Count; i++)
        {
            var item = dedupedSource[i];
            var key = keySelector(item);
            var existingIndex = targetIndexByKey.TryGetValue(key, out var foundIndex) ? foundIndex : -1;

            if (existingIndex == -1)
            {
                target.Insert(Math.Min(i, target.Count), item);
                for (var j = i; j < target.Count; j++)
                    targetIndexByKey[keySelector(target[j])] = j;
                continue;
            }

            if (existingIndex != i)
            {
                target.Move(existingIndex, i);
                var start = Math.Min(existingIndex, i);
                for (var j = start; j < target.Count; j++)
                    targetIndexByKey[keySelector(target[j])] = j;
            }

            if (!ReferenceEquals(target[i], item))
            {
                merge?.Invoke(target[i], item);
                target[i] = item;
                targetIndexByKey[key] = i;
            }
        }
    }

    private void ExtensionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenExtensionsPage(showMarketplaceTab: false, forceRefresh: false);
    }

    private async void RefreshExtensionsButton_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshExtensionsDataAsync(force: true);

    private void OpenMarketplaceTab(string tab)
    {
        SetSelectedExtensionsTab(tab);
        RefreshMarketplaceConnectivityState();
        _ = RefreshExtensionsDataAsync();
    }

    private void OpenMarketplaceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenExtensionsPage(showMarketplaceTab: true, forceRefresh: true);
    }

    private void DismissExtensionUpdateBanner_OnClick(object? sender, RoutedEventArgs e)
    {
        _extensionUpdateBannerDismissed = true;
        OnPropertyChanged(nameof(IsExtensionUpdateBannerVisible));
    }

    private void OpenMarketplaceFromBannerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _extensionUpdateBannerDismissed = true;
        OnPropertyChanged(nameof(IsExtensionUpdateBannerVisible));
        OpenExtensionsPage(showMarketplaceTab: true, forceRefresh: false);
    }

    private void OpenExtensionsPage(bool showMarketplaceTab, bool forceRefresh)
    {
        NavigateTo(AppPage.Extensions);
        if (showMarketplaceTab) IsLanguagesTabSelected = true;
        RefreshMarketplaceConnectivityState();
        _ = RefreshExtensionsDataAsync(force: forceRefresh);
    }

    private async void OpenExtensionsFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(ExtensionsFolderPath))
                Directory.CreateDirectory(ExtensionsFolderPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = ExtensionsFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ExtensionsStatusText = $"Could not open extensions folder: {ex.Message}";
            await ShowWarningDialogAsync("Open extensions folder", ex);
        }
    }

    private async void UpdateAllMarketplaceExtensionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsUpdatingAllExtensions || _isAutoUpdatingExtensions)
            return;

        var pendingUpdateIds = MarketplaceExtensions
            .Where(extension => extension.IsUpdateAvailable && extension.IsInstallEnabled)
            .Select(extension => extension.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingUpdateIds.Count == 0)
        {
            ExtensionsStatusText = "All extensions are up to date.";
            return;
        }

        IsUpdatingAllExtensions = true;
        var successfulUpdates = 0;
        var failedUpdates = 0;

        try
        {
            for (var index = 0; index < pendingUpdateIds.Count; index++)
            {
                var extensionId = pendingUpdateIds[index];
                var marketplaceExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (marketplaceExtension is null || !marketplaceExtension.IsUpdateAvailable)
                    continue;

                ExtensionsStatusText = $"Updating {index + 1} of {pendingUpdateIds.Count}: {marketplaceExtension.Name}...";
                await InstallMarketplaceExtensionAsync(marketplaceExtension);

                var refreshedExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (refreshedExtension is not null && refreshedExtension.IsInstalled && !refreshedExtension.IsUpdateAvailable)
                    successfulUpdates++;
                else
                    failedUpdates++;
            }

            ExtensionsStatusText = failedUpdates == 0
                ? $"Updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}."
                : $"Updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}. {failedUpdates} couldn't be updated.";
        }
        finally
        {
            IsUpdatingAllExtensions = false;
        }
    }

    private void UpdateExtensionAutoUpdateLifecycle()
    {
        _extensionAutoUpdateTimer.Stop();
        if (IsAutoUpdateExtensionsEnabled)
            _extensionAutoUpdateTimer.Start();
    }

    private async void ExtensionAutoUpdateTimer_OnTick(object? sender, EventArgs e)
    {
        if (!IsAutoUpdateExtensionsEnabled)
            return;

        await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
        await AutoUpdateExtensionsIfEnabledAsync();
    }

    private async void MarketplaceRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        await RefreshExtensionsDataAsync(force: true, suppressWatchdog: true);
    }

    private async Task RefreshExtensionsAndAutoUpdateAsync()
    {
        await RefreshExtensionsDataAsync();
        await AutoUpdateExtensionsIfEnabledAsync();
    }

    private async Task AutoUpdateExtensionsIfEnabledAsync()
    {
        if (!IsAutoUpdateExtensionsEnabled || _isAutoUpdatingExtensions || IsUpdatingAllExtensions)
            return;

        var pendingUpdateIds = MarketplaceExtensions
            .Where(extension => extension.IsUpdateAvailable && extension.IsInstallEnabled)
            .Select(extension => extension.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingUpdateIds.Count == 0)
            return;

        _isAutoUpdatingExtensions = true;
        var successfulUpdates = 0;
        var failedUpdates = 0;

        try
        {
            for (var index = 0; index < pendingUpdateIds.Count; index++)
            {
                var extensionId = pendingUpdateIds[index];
                var marketplaceExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (marketplaceExtension is null || !marketplaceExtension.IsUpdateAvailable || marketplaceExtension.IsInstalling)
                    continue;

                if (!IsAutoUpdateExtensionsInBackgroundEnabled)
                    ExtensionsStatusText = $"Auto-updating {index + 1} of {pendingUpdateIds.Count}: {marketplaceExtension.Name}...";
                await InstallMarketplaceExtensionAsync(marketplaceExtension);

                var refreshedExtension = MarketplaceExtensions.FirstOrDefault(entry =>
                    entry.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                if (refreshedExtension is not null && refreshedExtension.IsInstalled && !refreshedExtension.IsUpdateAvailable)
                    successfulUpdates++;
                else
                    failedUpdates++;
            }

            if ((successfulUpdates > 0 || failedUpdates > 0) && !IsAutoUpdateExtensionsInBackgroundEnabled)
            {
                ExtensionsStatusText = failedUpdates == 0
                    ? $"Automatically updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}."
                    : $"Automatically updated {successfulUpdates} extension{(successfulUpdates == 1 ? string.Empty : "s")}. {failedUpdates} couldn't be updated.";
            }
        }
        finally
        {
            _isAutoUpdatingExtensions = false;
        }
    }

    private async void UpdateInstalledExtensionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LoadedExtension extension })
            return;

        var marketplaceExtension = GetMarketplaceExtensionForInstalled(extension);
        if (marketplaceExtension is null)
        {
            ExtensionsStatusText = $"Couldn't find an update source for {extension.Name}.";
            return;
        }

        await InstallMarketplaceExtensionAsync(marketplaceExtension);
    }

    private void RefreshMarketplaceConnectivityState(string? operation = null, Exception? exception = null)
    {
        var hasWirelessConnection = HasActiveWirelessConnection();
        var hasInternetConnection = HasActiveInternetConnection();

        string message = string.Empty;

        if (!hasInternetConnection)
        {
            message = hasWirelessConnection
                ? "No internet connection. Marketplace installs and updates won't work until you're back online."
                : "No Wi-Fi or internet detected. Marketplace installs and updates won't work until you're back online.";
        }
        else if (exception is not null)
        {
            // Shows a message for any exception when online, not just
            message = IsGitHubRateLimitException(exception)
                ? "GitHub's API rate limit was hit. Marketplace refreshes will resume once it resets."
                : hasWirelessConnection
                    ? "Couldn't reach the marketplace. Your connection may be unstable - try again in a moment."
                    : "No Wi-Fi detected. If you're expecting a connection, reconnect first. Marketplace downloads may fail while offline.";
        }

        if (!string.IsNullOrWhiteSpace(operation) && !string.IsNullOrWhiteSpace(message))
            message = $"{message} (Last issue: {operation})";

        MarketplaceConnectivityMessage = message;
        IsMarketplaceConnectivityWarningVisible = !string.IsNullOrWhiteSpace(message);
    }

}
