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

        // Smoothness: cache read + JSON parse off UI thread so deferred 3s
        var diskJson = await Task.Run(() => TryReadMarketplaceIndexCache()).ConfigureAwait(false);
        if (diskJson is not null)
            await Task.Run(() => ParseAndApplyMarketplaceIndex(diskJson, marketplaceExtensions, extensionLoadErrors)).ConfigureAwait(false);

        try
        {
            _marketplaceIndexETag ??= TryReadMarketplaceIndexETag();
            var hasLocalDataToReuseOn304 = marketplaceExtensions.Count > 0;
            foreach (var indexUrl in MarketplaceIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (hasLocalDataToReuseOn304 && _marketplaceIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _marketplaceIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Marketplace index fetch",
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
            if (diskJson is not null)
            {
                extensionLoadErrors.Add($"Marketplace index fetch failed (using cached copy): {DescribeFetchFailure(ex)}");
                KodoDiagnostics.LogDebug("Marketplace index fetch failed; using disk cache.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState("Marketplace fetch", ex));
            }
            else
            {
                // No cache at all - propagate so the caller shows the error
                extensionLoadErrors.Add($"Failed to load remote marketplace index: {DescribeFetchFailure(ex)}");
                await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState("Marketplace fetch", ex));
                throw;
            }
        }

        Dictionary<string, string> marketplaceIconMap = [];
        await InvokeExtensionUiAsync(() =>
        {
            var combinedMarketplaceEntries = marketplaceExtensions
                .Where(entry => !string.Equals(entry.Type, "plugin", StringComparison.OrdinalIgnoreCase))
                .Concat(_pluginsIndexEntries)
                .ToList();

            SyncMarketplaceExtensionCollection(MarketplaceExtensions, combinedMarketplaceEntries);
            SyncObservableCollection(
                ExtensionLoadErrors,
                ExtensionLoadErrors.Concat(extensionLoadErrors).Distinct().ToList(),
                error => error);

            SyncMarketplaceInstallStates();
            RaiseMany(nameof(ExtensionLoadErrors), nameof(IsMarketplaceUnavailableVisible), nameof(IsMarketplacePartialErrorVisible), nameof(IsMarketplaceEmptyVisible));
            NotifyExtensionFiltersChanged();

            marketplaceIconMap = MarketplaceExtensions
                .Where(entry => !string.IsNullOrWhiteSpace(entry.IconUrl))
                .ToDictionary(entry => entry.Id, entry => entry.IconUrl, StringComparer.OrdinalIgnoreCase);
        });
        _ = FetchMarketplaceIconsAsync(marketplaceIconMap);
        _ = FetchInstalledExtensionIconsAsync(marketplaceIconMap);
    }

    private async Task LoadPluginsIndexAsync()
    {
        var pluginExtensions = new List<MarketplaceExtension>();
        var pluginLoadErrors = new List<string>();

        await Dispatcher.UIThread.InvokeAsync(() => RefreshMarketplaceConnectivityState(), DispatcherPriority.Background);

        var diskJson = await Task.Run(() => TryReadPluginsIndexCache()).ConfigureAwait(false);
        if (diskJson is not null)
            await Task.Run(() => ParseAndApplyMarketplaceIndex(diskJson, pluginExtensions, pluginLoadErrors)).ConfigureAwait(false);

        try
        {
            _pluginsIndexETag ??= TryReadPluginsIndexETag();
            var hasLocalDataToReuseOn304 = pluginExtensions.Count > 0;
            foreach (var indexUrl in PluginsIndexUrls)
            {
                using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
                indexRequest.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                if (hasLocalDataToReuseOn304 && _pluginsIndexETag is not null)
                    indexRequest.Headers.TryAddWithoutValidation("If-None-Match", _pluginsIndexETag);

                var (statusCode, remoteJson, newETag) = await RunWithGitHubTimeoutAsync(
                    "Plugins index fetch",
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
            if (diskJson is not null)
            {
                pluginLoadErrors.Add($"Plugins index fetch failed (using cached copy): {DescribeFetchFailure(ex)}");
                KodoDiagnostics.LogDebug("Plugins index fetch failed; using disk cache.", ex);
            }
            else
            {
                pluginLoadErrors.Add($"Failed to load remote plugins index: {DescribeFetchFailure(ex)}");
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
                .ToDictionary(entry => entry.Id, entry => entry.IconUrl, StringComparer.OrdinalIgnoreCase);
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
        var tasks = LoadedExtensions
            .Select(ext => (ext, iconUrl: marketplaceIconMap.TryGetValue(ext.Id, out var iconUrl) ? iconUrl : string.Empty))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.iconUrl))
            .Select(async pair =>
            {
                try
                {
                    var icon = await GetCachedIconAsync(pair.iconUrl);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (icon.HasValue)
                        {
                            // Index icon fetched successfully - use it
                            ReplaceLoadedExtensionIcon(pair.ext, icon);
                        }
                    });
                }
                catch (Exception ex)
                {
                    // Network failure for this icon - leave the kox icon
                    KodoDiagnostics.LogDebug($"Icon fetch failed for installed extension '{pair.ext.Id}': {pair.iconUrl}", ex);
                }
            });

        await Task.WhenAll(tasks);
    }

    private async Task FetchMarketplaceIconsAsync(
        IReadOnlyDictionary<string, string> marketplaceIconMap,
        ObservableCollection<MarketplaceExtension>? targetCollection = null)
    {
        var entries = targetCollection ?? MarketplaceExtensions;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var entry in entries)
            {
                if (entry.IconImage is not null || entry.SvgData is not null)
                    continue;

                if (!marketplaceIconMap.TryGetValue(entry.Id, out var cachedUrl))
                    continue;

                if (!_marketplaceIconBytesCache.TryGetValue(cachedUrl, out var cachedBytes))
                    continue;

                var icon = DecodeCachedIconBytes(cachedBytes);
                if (icon.HasValue)
                    ReplaceMarketplaceIcon(entry, icon);
            }
        });

        var iconFailures = 0;
        var iconAttempts = 0;
        Exception? lastIconException = null;

        var tasks = entries
            .Where(entry => entry.IconImage is null && entry.SvgData is null && marketplaceIconMap.TryGetValue(entry.Id, out _))
            .Select(async entry =>
            {
                Interlocked.Increment(ref iconAttempts);
                try
                {
                    var icon = await GetCachedIconAsync(marketplaceIconMap[entry.Id]);
                    if (!icon.HasValue)
                    {
                        KodoDiagnostics.LogDebug($"Icon fetch returned no data for marketplace extension '{entry.Id}': {marketplaceIconMap[entry.Id]}");
                        Interlocked.Increment(ref iconFailures);
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ReplaceMarketplaceIcon(entry, icon);
                    });
                }
                catch (Exception ex)
                {
                    KodoDiagnostics.LogDebug($"Icon fetch failed for marketplace extension '{entry.Id}': {marketplaceIconMap[entry.Id]}", ex);
                    Interlocked.Increment(ref iconFailures);
                    Interlocked.Exchange(ref lastIconException, ex);
                }
            });

        await Task.WhenAll(tasks);

        if (iconAttempts > 0 && iconFailures == iconAttempts && lastIconException is not null)
        {
            KodoDiagnostics.LogDebug(
                $"All {iconAttempts} marketplace icon fetch(es) failed; icons will show abbreviations.",
                lastIconException);
        }
    }

    private async Task<IconResult> GetCachedIconAsync(string iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return default;

        if (_marketplaceIconBytesCache.TryGetValue(iconUrl, out var bytes))
            return DecodeCachedIconBytes(bytes);

        // Cache miss - fetch under semaphore to avoid duplicate requests.
        await _iconFetchSemaphore.WaitAsync();
        try
        {
            if (!_marketplaceIconBytesCache.TryGetValue(iconUrl, out bytes))
            {
                using var cts = new CancellationTokenSource(GitHubOperationTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, iconUrl);
                if (IsGitHubContentsApiUrl(iconUrl))
                    request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");

                using var response = await MarketplaceHttpClient.SendAsync(request, cts.Token);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                _marketplaceIconBytesCache[iconUrl] = bytes;
            }
        }
        finally
        {
            _iconFetchSemaphore.Release();
        }

        var icon = DecodeCachedIconBytes(bytes);
        if (icon.SvgData is not null)
        {
            var svgData = _marketplaceSvgCache.GetOrAdd(iconUrl, icon.SvgData);
            return new IconResult(null, svgData);
        }

        return icon;
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
            return NormalizeGitHubUrl(rawIcon);

        var relativePath = rawIcon.Trim().TrimStart('/');
        return $"https://api.github.com/repos/{KodoExtensionsOwner}/{KodoExtensionsRepo}/contents/{relativePath}";
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
