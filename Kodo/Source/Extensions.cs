// Licensed under GPL-v3.0
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using Microsoft.Win32;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Animation;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using DiscordAssetsModel = DiscordRPC.Assets;
using DiscordRpcClient = DiscordRPC.DiscordRpcClient;
using DiscordRichPresenceModel = DiscordRPC.RichPresence;
using Kodo.Models;

namespace Kodo;

public partial class MainWindow
{

    private void EnsureExtensionsFolder()
    {
        if (!Directory.Exists(ExtensionsFolderPath))
            Directory.CreateDirectory(ExtensionsFolderPath);
    }

    private void SetupExtensionFolderWatchers()
    {
        DisposeExtensionFolderWatchers();
        _extensionsFolderWatcher = CreateExtensionFolderWatcher(ExtensionsFolderPath);

        if (Directory.Exists(ProjectExtensionsFolderPath) &&
            !string.Equals(ProjectExtensionsFolderPath, ExtensionsFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            _projectExtensionsFolderWatcher = CreateExtensionFolderWatcher(ProjectExtensionsFolderPath);
        }
    }

    private FileSystemWatcher CreateExtensionFolderWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
        };

        watcher.Created += ExtensionFolderWatcher_OnChanged;
        watcher.Deleted += ExtensionFolderWatcher_OnChanged;
        watcher.Renamed += ExtensionFolderWatcher_OnRenamed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void DisposeExtensionFolderWatchers()
    {
        DisposeExtensionFolderWatcher(_extensionsFolderWatcher);
        DisposeExtensionFolderWatcher(_projectExtensionsFolderWatcher);
        _extensionsFolderWatcher = null;
        _projectExtensionsFolderWatcher = null;
    }

    private void ExtensionFolderWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsExtensionFilePath(e.FullPath))
            return;

        QueueExtensionsRefresh();
    }

    private void QueueExtensionsRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _extensionsRefreshDebounceTimer.Stop();
            _extensionsRefreshDebounceTimer.Start();
        });
    }

    private async Task RefreshExtensionsDataAsync(bool force = false, bool suppressWatchdog = false)
    {
        if (_isRefreshingExtensions)
            return;

        if (!force && DateTime.UtcNow - _lastExtensionsRefreshUtc < ExtensionsRefreshCooldown)
            return;

        IsRefreshingExtensions = true;
        ExtensionsStatusText = "Refreshing extensions...";

        using var watchdogCts = new CancellationTokenSource();
        var watchdogToken = watchdogCts.Token;
        if (!suppressWatchdog)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(GitHubOperationTimeout, watchdogToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var timeoutEx = new TimeoutException(
                    $"Marketplace refresh did not complete within " +
                    $"{GitHubOperationTimeout.TotalSeconds:0} seconds. " +
                    "This may indicate a slow or stalled network connection, " +
                    "a slow disk scan, or a hung extension operation.");

                KodoDiagnostics.LogWarning(
                    source: "MainWindow.RefreshExtensionsDataAsync.Watchdog",
                    exception: timeoutEx,
                    operation: "Marketplace refresh watchdog");

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // Update status bar so the panel itself reflects the stall.
                    ExtensionsStatusText = "Marketplace refresh is taking too long. Check your connection.";
                    await ShowWarningDialogAsync("Marketplace refresh", timeoutEx);
                });
            }, watchdogToken);
        }

        try
        {
            var extensionScan = await Task.Run(ScanInstalledExtensions);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyLoadedExtensionsResult(extensionScan));
            await LoadMarketplaceExtensionsAsync();
            await LoadPluginsIndexAsync();
            await LoadCompilerExtensionsAsync(forceResolve: force);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(CurrentThemeName, _requestedThemeName, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(_requestedThemeName, "Light", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(_requestedThemeName, "Dark", StringComparison.OrdinalIgnoreCase) ||
                     ThemeExtensions.Any(t => string.Equals(t.ThemeDefinition!.ThemeId, _requestedThemeName, StringComparison.OrdinalIgnoreCase))))
                {
                    ApplyTheme(_requestedThemeName);
                }
                var updateCount = MarketplaceExtensions.Count(e => e.IsUpdateAvailable);
                var installedCount = VisibleLoadedExtensions.Count();
                var marketplaceCount = MarketplaceExtensions.Count;
                var installedWord = installedCount == 1 ? "extension" : "extensions";
                var marketplaceWord = marketplaceCount == 1 ? "extension" : "extensions";
                var updateWord = updateCount == 1 ? "update" : "updates";
                ExtensionsStatusText = updateCount > 0
                    ? $"Found {installedCount} installed {installedWord} and {marketplaceCount} in the marketplace. {updateCount} {updateWord} available."
                    : $"Found {installedCount} installed {installedWord} and {marketplaceCount} in the marketplace.";
                _lastExtensionsRefreshUtc = DateTime.UtcNow;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ExtensionsStatusText = "Couldn't refresh extensions. Check your connection and try again.");
            await Dispatcher.UIThread.InvokeAsync(async () => await ShowWarningDialogAsync("Marketplace fetch", ex));
        }
        finally
        {
            await watchdogCts.CancelAsync();
            await Dispatcher.UIThread.InvokeAsync(() => IsRefreshingExtensions = false);
        }
    }

    private void LoadExtensions()
    {
        ApplyLoadedExtensionsResult(ScanInstalledExtensions());
    }

    private async Task LoadExtensionsAsync()
    {
        var scan = await Task.Run(() => ScanInstalledExtensions()).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyLoadedExtensionsResult(scan), DispatcherPriority.Background);
        // Give renderer a chance to pump one frame after extensions + theme refresh
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private ExtensionScanResult ScanInstalledExtensions()
    {
        var loadedExtensions = new List<LoadedExtension>();
        var extensionLoadErrors = new List<string>();
        var searchPaths = GetExtensionSearchPaths().ToList();

        var anyFolderFound = false;
        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath)) continue;
            anyFolderFound = true;

            foreach (var koxFile in Directory.GetFiles(searchPath, "*.kox"))
            {
                try
                {
                    foreach (var ext in LoadExtensionsFromKox(koxFile))
                    {
                        AddOrReplaceLoadedExtension(loadedExtensions, ext);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Extensions] Failed to load '{Path.GetFileName(koxFile)}': {ex.Message}");
                    extensionLoadErrors.Add($"Failed to load '{Path.GetFileName(koxFile)}': {ex.Message}");
                }
            }

            foreach (var dir in Directory.GetDirectories(searchPath))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, "manifest.json")))
                    {
                        foreach (var ext in LoadExtensionsFromFolder(dir))
                        {
                            AddOrReplaceLoadedExtension(loadedExtensions, ext);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Extensions] Failed to load folder extension '{Path.GetFileName(dir)}': {ex.Message}");
                    extensionLoadErrors.Add($"Failed to load folder extension '{Path.GetFileName(dir)}': {ex.Message}");
                }
            }
        }

        if (!anyFolderFound)
            extensionLoadErrors.Add($"Extensions folder not found. Expected: {ExtensionsFolderPath}");

        return new ExtensionScanResult(loadedExtensions, extensionLoadErrors);
    }

    private void ApplyLoadedExtensionsResult(ExtensionScanResult result)
    {
        _highlightingCache.Clear();
        _compiledSyntaxProfileCache.Clear();
        _contentSniffCache.Clear();
        SyncObservableCollection(LoadedExtensions, result.Extensions, ext => ext.Id);
        SyncObservableCollection(ExtensionLoadErrors, result.LoadErrors, error => error);

        // Smoothness: decode icon bitmaps off UI thread in staggered chunks, yielding between batches.
        // SVGs are tiny (string decode) so handle inline; PNGs are decoded on pool then assigned at Background.
        var pngExtensions = new List<LoadedExtension>();
        foreach (var ext in LoadedExtensions)
        {
            if (ext.IconImage is null && ext.SvgData is null && ext.IconBytes is not null)
            {
                if (IsSvgContent(ext.IconBytes))
                {
                    try { ext.SvgData = System.Text.Encoding.UTF8.GetString(ext.IconBytes); }
                    catch { /* malformed SVG - leave icon absent */ }
                    ext.IconBytes = null;
                    ext.NotifyIconChanged();
                }
                else
                {
                    pngExtensions.Add(ext);
                }
            }
        }

        if (pngExtensions.Count > 0)
        {
            // Offload PNG Skia decode off UI thread, assign at Background priority with yields
            _ = Task.Run(async () =>
            {
                const int batchSize = 4;
                for (var i = 0; i < pngExtensions.Count; i += batchSize)
                {
                    var batch = pngExtensions.Skip(i).Take(batchSize).ToList();
                    var decoded = new List<(LoadedExtension ext, Bitmap? bmp)>();
                    foreach (var ext in batch)
                    {
                        var bytes = ext.IconBytes;
                        if (bytes is null) continue;
                        try
                        {
                            using var ms = new MemoryStream(bytes);
                            var bmp = new Bitmap(ms);
                            if (bmp.PixelSize.Width != bmp.PixelSize.Height)
                            {
                                bmp.Dispose();
                                bmp = null;
                            }
                            decoded.Add((ext, bmp));
                        }
                        catch { decoded.Add((ext, null)); }
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var (ext, bmp) in decoded)
                        {
                            ext.IconImage = bmp;
                            ext.IconBytes = null;
                            ext.NotifyIconChanged();
                        }
                    }, DispatcherPriority.Background);

                    // Yield to renderer between batches
                    await Task.Delay(16);
                }
            });
        }

        foreach (var ext in ThemeExtensions)
            ext.IsActiveTheme = string.Equals(ext.ThemeCardThemeId, _currentThemeName, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(ExtensionLoadErrors));
        OnPropertyChanged(nameof(VisibleLoadedExtensions));
        NotifyExtensionFiltersChanged();
        OnPropertyChanged(nameof(IsNoExtensionsVisible));
        OnPropertyChanged(nameof(ThemeExtensions));
        OnPropertyChanged(nameof(HasThemeExtensions));
        OnPropertyChanged(nameof(GroupedThemeExtensions));
        OnPropertyChanged(nameof(HasGroupedThemeExtensions));
        RefreshExtensionTheme();
        SyncMarketplaceInstallStates();
        SyncActivePlugins();
    }

    private IEnumerable<LoadedExtension> LoadExtensionsFromFolder(string folderPath)
    {
        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(manifestPath)) yield break;

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var baseExt = ParseManifest(manifestDoc.RootElement);
        baseExt = baseExt with { Version = GetBestKnownExtensionVersion(baseExt.Version, folderPath) };
        baseExt.SourcePath = folderPath;
        baseExt.IsDirectorySource = true;
        baseExt.InstalledOnUtc = GetExtensionSourceActivityUtc(folderPath, isDirectory: true);
        if (baseExt.PluginAssemblyFileName is not null &&
            File.Exists(Path.Combine(folderPath, baseExt.PluginAssemblyFileName)))
            baseExt.PluginFolderPath = folderPath;

        foreach (var languageFileName in EnumerateLanguageProfileNames())
        {
            var langPath = Path.Combine(folderPath, languageFileName);
            if (!File.Exists(langPath)) continue;

            using var langDoc = JsonDocument.Parse(File.ReadAllText(langPath));
            ParseLanguage(langDoc.RootElement, baseExt);
        }

        var iconPath = Path.Combine(folderPath, "icon.png");
        if (File.Exists(iconPath))
        {
            using var iconStream = File.OpenRead(iconPath);
            baseExt.IconBytes = ReadIconBytesFromStream(iconStream);
        }
        else
        {
            var svgIconPath = Path.Combine(folderPath, "icon.svg");
            if (File.Exists(svgIconPath))
            {
                using var iconStream = File.OpenRead(svgIconPath);
                baseExt.IconBytes = ReadIconBytesFromStream(iconStream);
            }
        }

        var themePath = Path.Combine(folderPath, "theme.json");
        if (!File.Exists(themePath))
        {
            // No theme file - yield the extension as-is (language extension, etc.)
            yield return baseExt;
            yield break;
        }

        using var themeDoc = JsonDocument.Parse(File.ReadAllText(themePath));
        var root = themeDoc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var themeElement in root.EnumerateArray())
            {
                var def = ParseTheme(themeElement, baseExt);
                var entry = CloneBaseExtension(baseExt);
                entry.ThemeDefinition = def;
                if (index > 0)
                    entry = entry with { Id = $"{baseExt.Id}_{def.ThemeId}", IsThemeSubEntry = true };
                yield return entry;
                index++;
            }
        }
        else
        {
            baseExt.ThemeDefinition = ParseTheme(root, baseExt);
            yield return baseExt;
        }
    }

    private IEnumerable<LoadedExtension> LoadExtensionsFromKox(string koxPath)
    {
        using var archive = ZipFile.OpenRead(koxPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is null) yield break;

        using var manifestStream = manifestEntry.Open();
        using var manifestDoc = JsonDocument.Parse(manifestStream);
        var baseExt = ParseManifest(manifestDoc.RootElement);
        baseExt = baseExt with { Version = GetBestKnownExtensionVersion(baseExt.Version, koxPath) };
        baseExt.SourcePath = koxPath;
        baseExt.IsDirectorySource = false;
        baseExt.InstalledOnUtc = GetExtensionSourceActivityUtc(koxPath, isDirectory: false);
        if (baseExt.PluginAssemblyFileName is not null &&
            archive.GetEntry(baseExt.PluginAssemblyFileName) is not null)
            baseExt.PluginFolderPath = ExtractKoxPluginFiles(archive, baseExt.Id, baseExt.Version);

        foreach (var languageFileName in EnumerateLanguageProfileNames())
        {
            var langEntry = archive.GetEntry(languageFileName);
            if (langEntry is null) continue;

            using var langStream = langEntry.Open();
            using var langDoc = JsonDocument.Parse(langStream);
            ParseLanguage(langDoc.RootElement, baseExt);
        }

        var iconEntry = archive.GetEntry("icon.png") ?? archive.GetEntry("icon.svg");
        if (iconEntry is not null)
        {
            using var iconStream = iconEntry.Open();
            baseExt.IconBytes = ReadIconBytesFromStream(iconStream);
        }

        var themeEntry = archive.GetEntry("theme.json");
        if (themeEntry is null)
        {
            yield return baseExt;
            yield break;
        }

        using var themeStream = themeEntry.Open();
        using var ms = new MemoryStream();
        themeStream.CopyTo(ms);
        ms.Position = 0;
        using var themeDoc = JsonDocument.Parse(ms);
        var root = themeDoc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var themeElement in root.EnumerateArray())
            {
                var def = ParseTheme(themeElement, baseExt);
                var entry = CloneBaseExtension(baseExt);
                entry.ThemeDefinition = def;
                if (index > 0)
                    entry = entry with { Id = $"{baseExt.Id}_{def.ThemeId}", IsThemeSubEntry = true };
                yield return entry;
                index++;
            }
        }
        else
        {
            baseExt.ThemeDefinition = ParseTheme(root, baseExt);
            yield return baseExt;
        }
    }

    private static LoadedExtension ParseManifest(JsonElement manifest) => new()
    {
        Id          = manifest.TryGetProperty("id",          out var id)   ? id.GetString()   ?? "" : "",
        Version     = manifest.TryGetProperty("version",     out var ver)  ? ver.GetString()  ?? "" : "",
        Name        = manifest.TryGetProperty("name",        out var name) ? name.GetString() ?? "" : "",
        Type        = manifest.TryGetProperty("type",        out var type) ? type.GetString() ?? "" : "",
        Author      = manifest.TryGetProperty("author",      out var auth) ? auth.GetString() ?? "" : "",
        Description = manifest.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
        Extensions  = manifest.TryGetProperty("extensions",  out var exts)
            ? exts.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [],
        PluginAssemblyFileName = manifest.TryGetProperty("plugin", out var plugin) ? plugin.GetString() : null
    };

    private static IEnumerable<string> EnumerateLanguageProfileNames()
    {
        yield return "language.json";
        yield return "language1.json";
        yield return "language2.json";
        yield return "language3.json";
        yield return "language4.json";
        yield return "language5.json";
    }

    private static void ParseLanguage(JsonElement lang, LoadedExtension ext)
    {
        var profile = ParseLanguageProfile(lang);
        if (profile.Extensions.Length > 0)
        {
            ext.SyntaxProfiles.Add(profile);
            return;
        }

        ApplyLanguageProfile(ext, profile);
    }

    private static LanguageSyntaxProfile ParseLanguageProfile(JsonElement lang)
    {
        var profile = new LanguageSyntaxProfile
        {
            Extensions = ReadStringArray(lang, "extensions"),
            Keywords = ReadStringArray(lang, "keywords"),
            Types = ReadStringArray(lang, "types"),
            Functions = ReadStringArray(lang, "functions"),
            Properties = ReadStringArray(lang, "properties"),
            Namespaces = ReadStringArray(lang, "namespaces"),
            Blacklist = ReadStringArray(lang, "blacklist"),
            DeadCodeIgnore = ReadStringArray(lang, "deadCodeIgnore"),
            DeadCodeEntryPoints = ReadStringArray(lang, "deadCodeEntryPoints"),
            CommentLine = lang.TryGetProperty("commentLine", out var cl) ? NormalizeSyntaxToken(cl.GetString()) : null,
            CommentBlockStart = lang.TryGetProperty("commentBlockStart", out var cbs) ? NormalizeSyntaxToken(cbs.GetString()) : null,
            CommentBlockEnd = lang.TryGetProperty("commentBlockEnd", out var cbe) ? NormalizeSyntaxToken(cbe.GetString()) : null,
            StringDelimiters = lang.TryGetProperty("stringDelimiters", out var sd) ? ReadStringArray(sd) : null,
            MultiLineStringDelimiters = lang.TryGetProperty("multiLineStringDelimiters", out var msd) ? ReadStringArray(msd) : null,
            DisableSingleQuoteStrings = lang.TryGetProperty("disableSingleQuoteStrings", out var dsqs) && dsqs.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? dsqs.GetBoolean()
                : null,
            ColorTokens = ReadColorTokens(lang)
        };

        return profile;
    }

    private static ExtensionThemeDefinition ParseTheme(JsonElement theme, LoadedExtension ext) => new()
    {
        ThemeId = theme.TryGetProperty("themeId", out var themeId) ? themeId.GetString() ?? ext.Id : ext.Id,
        DisplayName = theme.TryGetProperty("displayName", out var displayName) ? displayName.GetString() ?? ext.Name : ext.Name,
        BaseTheme = theme.TryGetProperty("baseTheme", out var baseTheme) ? baseTheme.GetString() ?? "Dark" : "Dark",
        WindowBackground = GetThemeColor(theme, "windowBackground", "#000000"),
        TopBar = GetThemeColor(theme, "topBar", "#0E0E0E"),
        Sidebar = GetThemeColor(theme, "sidebar", "#0E0E0E"),
        Button = GetThemeColor(theme, "button", "#242424"),
        ButtonHover = GetThemeColor(theme, "buttonHover", "#343434"),
        EditorBackground = GetThemeColor(theme, "editorBackground", "#000000"),
        Card = GetThemeColor(theme, "card", "#121212"),
        PrimaryText = GetThemeColor(theme, "primaryText", "#FFFFFF"),
        MutedText = GetThemeColor(theme, "mutedText", "#BDBDBD"),
        SurfaceBorder = GetThemeColor(theme, "surfaceBorder", "#4A4A4A"),
        Accent = GetThemeColor(theme, "accent", "#8C00FF"),
        PreviewBackground = GetThemeColor(theme, "previewBackground", GetThemeColor(theme, "editorBackground", "#000000")),
        PreviewBorder = GetThemeColor(theme, "previewBorder", GetThemeColor(theme, "surfaceBorder", "#4A4A4A"))
    };

    private void RefreshExtensionTheme()
    {
        foreach (var ext in LoadedExtensions)
        {
            ext.AccentBrush        = AccentBrush;
            ext.CardBrush          = CardBrush;
            ext.PrimaryTextBrush   = PrimaryTextBrush;
            ext.SurfaceBorderBrush = SurfaceBorderBrush;
            ext.MutedTextBrush     = MutedTextBrush;
            ext.NotifyAllBrushesChanged();
        }

        foreach (var ext in ThemeExtensions)
            ext.IsActiveTheme = string.Equals(ext.ThemeCardThemeId, _currentThemeName, StringComparison.OrdinalIgnoreCase);
    }

    private LoadedExtension? GetLanguageExtension(string filePath)
    {
        if (IsPlainTextFile(filePath))
            return null;

        var fileExt = Path.GetExtension(filePath).ToLowerInvariant();
        var extension = LoadedExtensions.FirstOrDefault(e =>
            e.Type == "language" &&
            e.Extensions.Any(ex => ex.Equals(fileExt, StringComparison.OrdinalIgnoreCase)));

        if (extension is null)
        {
            if (!_contentSniffCache.TryGetValue(filePath, out var sniffed))
            {
                sniffed = TryDetectLanguageFromContent(filePath);
                _contentSniffCache[filePath] = sniffed;
            }
            if (sniffed is null)
                return null;

            // Content-sniffed match: use the base extension as-is (no profile narrowing).
            return sniffed;
        }

        var matchingProfiles = extension.SyntaxProfiles
            .Where(profile => profile.Extensions.Any(ex => ex.Equals(fileExt, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matchingProfiles.Count == 0)
            return extension;

        var effectiveExtension = CloneBaseExtension(extension);
        if (matchingProfiles.Any(profile => profile.Keywords.Length > 0))
            effectiveExtension.Keywords = [];
        if (matchingProfiles.Any(profile => profile.Types.Length > 0))
            effectiveExtension.Types = [];
        if (matchingProfiles.Any(profile => profile.Functions.Length > 0))
            effectiveExtension.Functions = [];
        if (matchingProfiles.Any(profile => profile.Properties.Length > 0))
            effectiveExtension.Properties = [];
        if (matchingProfiles.Any(profile => profile.Namespaces.Length > 0))
            effectiveExtension.Namespaces = [];

        foreach (var profile in matchingProfiles)
            ApplyLanguageProfile(effectiveExtension, profile);

        return effectiveExtension;
    }

    private static JsonElement ReadManifestFromFolder(string folderPath)
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folderPath, "manifest.json")));
        return manifestDoc.RootElement.Clone();
    }

    private static bool ExtensionSourceMatchesId(string path, string extensionId, bool isDirectory)
    {
        try
        {
            var manifest = isDirectory
                ? ReadManifestFromFolder(path)
                : ReadManifestFromKox(path);

            return manifest.TryGetProperty("id", out var id) &&
                   string.Equals(id.GetString(), extensionId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeGitHubUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segments.Length >= 5 &&
                segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) &&
                IsKodoExtensionsRepo(segments[0], segments[1]))
            {
                var owner = segments[0];
                var repo  = segments[1];
                var path  = string.Join("/", segments, 4, segments.Length - 4);
                return $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
            }
            return url; // non-blob or third-party github.com URL - leave alone
        }

        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segments.Length >= 4 && IsKodoExtensionsRepo(segments[0], segments[1]))
            {
                var owner = segments[0];
                var repo  = segments[1];
                // segments[2] is the branch - omitted from the Contents API path
                var path  = string.Join("/", segments, 3, segments.Length - 3);
                return $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
            }
            return url; // third-party raw.githubusercontent.com URL - already serves raw bytes, leave alone
        }

        // Already a Contents API URL or a non-GitHub third-party URL - leave unchanged.
        return url;
    }

    private static int CompareExtensionVersions(string left, string right)
    {
        var leftParts = ParseVersionNumbers(left);
        var rightParts = ParseVersionNumbers(right);
        return VersionNumberSequenceComparer.Instance.Compare(leftParts, rightParts);
    }

    private static string GetHighestKnownExtensionVersion(string declaredVersion, params string[] fileNameCandidates)
    {
        var bestVersion = declaredVersion ?? string.Empty;

        foreach (var candidate in fileNameCandidates)
        {
            var extractedVersion = ExtractVersionFromName(candidate);
            if (string.IsNullOrWhiteSpace(extractedVersion))
                continue;

            if (CompareExtensionVersions(extractedVersion, bestVersion) > 0)
                bestVersion = extractedVersion;
        }

        return bestVersion;
    }

    private static string GetBestKnownExtensionVersion(string manifestVersion, string sourcePath)
    {
        var inferredVersion = ExtractVersionFromName(Path.GetFileName(sourcePath));
        return CompareExtensionVersions(inferredVersion, manifestVersion) > 0
            ? inferredVersion
            : manifestVersion;
    }

    private static DateTime? GetExtensionSourceActivityUtc(string path, bool isDirectory)
    {
        try
        {
            var createdUtc = isDirectory ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            var modifiedUtc = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            var activityUtc = createdUtc > modifiedUtc ? createdUtc : modifiedUtc;
            return activityUtc == DateTime.MinValue ? null : activityUtc;
        }
        catch
        {
            return null;
        }
    }

    private void AddOrReplaceLoadedExtension(IList<LoadedExtension> extensions, LoadedExtension extension)
    {
        var existingIndex = extensions
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => x.item.Id.Equals(extension.Id, StringComparison.OrdinalIgnoreCase));

        if (existingIndex is null)
        {
            extensions.Add(extension);
            return;
        }

        if (ShouldReplaceLoadedExtension(existingIndex.item, extension))
            extensions[existingIndex.index] = extension;
    }

    private void DeleteInstalledExtensionSources(string extensionId, string? pathToKeep = null)
    {
        foreach (var source in EnumerateInstalledExtensionSources(extensionId))
        {
            var resolvedPath = Path.GetFullPath(source.Path);
            if (!string.IsNullOrWhiteSpace(pathToKeep) &&
                string.Equals(resolvedPath, Path.GetFullPath(pathToKeep), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsPathInsideDirectory(resolvedPath, ExtensionsFolderPath) &&
                !IsPathInsideDirectory(resolvedPath, ProjectExtensionsFolderPath))
            {
                continue;
            }

            if (source.IsDirectory)
            {
                if (Directory.Exists(resolvedPath))
                    Directory.Delete(resolvedPath, recursive: true);
            }
            else
            {
                if (File.Exists(resolvedPath))
                    File.Delete(resolvedPath);
            }
        }
    }

    private string ResolveExtensionInstallPath(MarketplaceExtension marketplaceExtension, LoadedExtension? installedExtension)
    {
        if (installedExtension is not null &&
            !installedExtension.IsDirectorySource &&
            !string.IsNullOrWhiteSpace(installedExtension.SourcePath))
        {
            var sourcePath = Path.GetFullPath(installedExtension.SourcePath);
            if (IsPathInsideDirectory(sourcePath, ExtensionsFolderPath) ||
                IsPathInsideDirectory(sourcePath, ProjectExtensionsFolderPath))
            {
                return sourcePath;
            }
        }

        var fileName = string.IsNullOrWhiteSpace(marketplaceExtension.FileName)
            ? TryGetFileNameFromUrl(marketplaceExtension.DownloadUrl)
            : marketplaceExtension.FileName;

        return Path.Combine(ExtensionsFolderPath, fileName);
    }


    private void DisposeExtensionFolderWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
            return;

        watcher.EnableRaisingEvents = false;
        watcher.Created -= ExtensionFolderWatcher_OnChanged;
        watcher.Deleted -= ExtensionFolderWatcher_OnChanged;
        watcher.Renamed -= ExtensionFolderWatcher_OnRenamed;
        watcher.Dispose();
    }

    private static bool IsExtensionFilePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".kox", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(extension);
    }

    private void ExtensionFolderWatcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsExtensionFilePath(e.OldFullPath) && !IsExtensionFilePath(e.FullPath))
            return;

        QueueExtensionsRefresh();
    }

    private async void ExtensionsRefreshDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _extensionsRefreshDebounceTimer.Stop();
        await RefreshExtensionsDataAsync();
    }

    private static void ReplaceLoadedExtensionIcon(LoadedExtension extension, IconResult icon)
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
        extension.NotifyIconChanged();
    }

    private LoadedExtension? GetPreferredLoadedExtension(string extensionId) =>
        LoadedExtensions
            .Where(ext => ext.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetLoadedExtensionSourcePriority)
            .ThenByDescending(ext => ParseVersionNumbers(ext.Version), VersionNumberSequenceComparer.Instance)
            .FirstOrDefault();

    private bool ShouldReplaceLoadedExtension(LoadedExtension current, LoadedExtension candidate)
    {
        var currentPriority = GetLoadedExtensionSourcePriority(current);
        var candidatePriority = GetLoadedExtensionSourcePriority(candidate);
        if (candidatePriority != currentPriority)
            return candidatePriority > currentPriority;

        return CompareExtensionVersions(candidate.Version, current.Version) > 0;
    }

    private int GetLoadedExtensionSourcePriority(LoadedExtension extension)
    {
        if (!string.IsNullOrWhiteSpace(extension.SourcePath))
        {
            var sourcePath = Path.GetFullPath(extension.SourcePath);
            if (IsPathInsideDirectory(sourcePath, ExtensionsFolderPath))
                return 2;

            if (IsPathInsideDirectory(sourcePath, ProjectExtensionsFolderPath))
                return 1;
        }

        return 0;
    }

    private static string PrefixExtensionPath(string folderName, string suffix)
    {
        suffix = suffix.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(suffix) ? folderName : $"{folderName}/{suffix}";
    }

    private IEnumerable<string> GetExtensionSearchPaths()
    {
        yield return ExtensionsFolderPath;

        // Also search the project source tree when running from the build output directory
        var projectRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..");
        var srcPath = Path.GetFullPath(Path.Combine(projectRoot, "Extensions"));
        if (!string.Equals(srcPath, ExtensionsFolderPath, StringComparison.OrdinalIgnoreCase))
            yield return srcPath;
    }

    private IEnumerable<(string Path, bool IsDirectory)> EnumerateInstalledExtensionSources(string extensionId)
    {
        foreach (var searchPath in GetExtensionSearchPaths())
        {
            if (!Directory.Exists(searchPath))
                continue;

            foreach (var koxFile in Directory.GetFiles(searchPath, "*.kox"))
            {
                if (ExtensionSourceMatchesId(koxFile, extensionId, isDirectory: false))
                    yield return (koxFile, false);
            }

            foreach (var dir in Directory.GetDirectories(searchPath))
            {
                if (ExtensionSourceMatchesId(dir, extensionId, isDirectory: true))
                    yield return (dir, true);
            }
        }
    }

    private static LoadedExtension CloneBaseExtension(LoadedExtension src) => new()
    {
        Id                = src.Id,
        Version           = src.Version,
        Name              = src.Name,
        Type              = src.Type,
        Author            = src.Author,
        Description       = src.Description,
        Extensions        = src.Extensions,
        Keywords          = src.Keywords,
        Types             = src.Types,
        Functions         = src.Functions,
        Properties        = src.Properties,
        Namespaces        = src.Namespaces,
        Blacklist         = src.Blacklist,
        DeadCodeIgnore    = src.DeadCodeIgnore,
        DeadCodeEntryPoints = src.DeadCodeEntryPoints,
        CommentLine       = src.CommentLine,
        CommentBlockStart = src.CommentBlockStart,
        CommentBlockEnd   = src.CommentBlockEnd,
        StringDelimiters  = src.StringDelimiters.ToArray(),
        MultiLineStringDelimiters = src.MultiLineStringDelimiters.ToArray(),
        DisableSingleQuoteStrings = src.DisableSingleQuoteStrings,
        ColorTokens       = new Dictionary<string, string>(src.ColorTokens),
        SourcePath        = src.SourcePath,
        IsDirectorySource = src.IsDirectorySource,
        InstalledOnUtc    = src.InstalledOnUtc,
        PluginAssemblyFileName = src.PluginAssemblyFileName,
        PluginFolderPath  = src.PluginFolderPath,
        IconImage         = src.IconImage,
        IconBytes         = src.IconBytes,
    };

    private static void ApplyLanguageProfile(LoadedExtension ext, LanguageSyntaxProfile profile)
    {
        ext.Keywords = ext.Keywords.Union(profile.Keywords).ToArray();
        ext.Types = ext.Types.Union(profile.Types).ToArray();
        ext.Functions = ext.Functions.Union(profile.Functions).ToArray();
        ext.Properties = ext.Properties.Union(profile.Properties).ToArray();
        ext.Namespaces = ext.Namespaces.Union(profile.Namespaces).ToArray();
        ext.Blacklist = ext.Blacklist.Union(profile.Blacklist).ToArray();
        ext.DeadCodeIgnore = ext.DeadCodeIgnore.Union(profile.DeadCodeIgnore).ToArray();
        ext.DeadCodeEntryPoints = ext.DeadCodeEntryPoints.Union(profile.DeadCodeEntryPoints).ToArray();

        if (profile.CommentLine is not null)
            ext.CommentLine = profile.CommentLine;
        if (profile.CommentBlockStart is not null)
            ext.CommentBlockStart = profile.CommentBlockStart;
        if (profile.CommentBlockEnd is not null)
            ext.CommentBlockEnd = profile.CommentBlockEnd;
        if (profile.StringDelimiters is not null)
            ext.StringDelimiters = profile.StringDelimiters.ToArray();
        if (profile.MultiLineStringDelimiters is not null)
            ext.MultiLineStringDelimiters = profile.MultiLineStringDelimiters.ToArray();
        if (profile.DisableSingleQuoteStrings.HasValue)
            ext.DisableSingleQuoteStrings = profile.DisableSingleQuoteStrings.Value;

        foreach (var (key, value) in profile.ColorTokens)
            ext.ColorTokens[key] = value;
    }

    private LoadedExtension? TryDetectLanguageFromContent(string filePath)
    {
        try
        {
            string? firstLine = null;
            using (var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        firstLine = trimmed;
                        break;
                    }
                }
            }

            if (firstLine is null)
                return null;

            string? syntheticExt = null;

            if (firstLine.StartsWith("<Project", StringComparison.OrdinalIgnoreCase))
                syntheticExt = ".csproj";
            else if (firstLine.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                     firstLine.StartsWith("<", StringComparison.OrdinalIgnoreCase))
                syntheticExt = ".xml";

            if (syntheticExt is null)
                return null;

            return LoadedExtensions.FirstOrDefault(e =>
                e.Type == "language" &&
                e.Extensions.Any(ex => ex.Equals(syntheticExt, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return null;
        }
    }

    private void ApplySyntaxHighlighting(LoadedExtension ext)
    {
        if (EditorTextBox is null) return;
        var syntaxProfile = ResolveCompiledSyntaxProfile(ext);
        if (!_highlightingCache.TryGetValue(ext, out var definition))
        {
            definition = new KodoHighlightingDefinition(ext, syntaxProfile);
            _highlightingCache[ext] = definition;
        }
        EditorTextBox.SyntaxHighlighting = definition;
        ConfigureRainbowBrackets(ext);
        ConfigureInterpolatedStrings(syntaxProfile);
        ConfigureHtmlEmbeddedHighlighting(ext);
        ConfigureMarkdownHighlighting(ext);
    }

    private CompiledSyntaxProfile ResolveCompiledSyntaxProfile(LoadedExtension extension)
    {
        if (_compiledSyntaxProfileCache.TryGetValue(extension, out var cached))
            return cached;

        var profile = CompiledSyntaxProfile.Create(extension);
        _compiledSyntaxProfileCache[extension] = profile;
        return profile;
    }

    private void ConfigureHtmlEmbeddedHighlighting(LoadedExtension? extension)
    {
        _htmlEmbeddedColorizer.UpdateSyntax(extension, ResolveHtmlEmbeddedSyntaxProfile);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private void ConfigureMarkdownHighlighting(LoadedExtension? extension)
    {
        _markdownColorizer.UpdateSyntax(extension, ResolveFenceLanguageSyntaxProfile, ResolveInlineCodeLanguageExtension);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private CompiledSyntaxProfile? FindLanguageSyntaxProfileForFileExtension(string extension)
    {
        var loadedExtension = LoadedExtensions.FirstOrDefault(loadedExtension =>
            string.Equals(loadedExtension.Type, "language", StringComparison.OrdinalIgnoreCase) &&
            loadedExtension.Extensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)));

        return loadedExtension is null ? null : ResolveCompiledSyntaxProfile(loadedExtension);
    }

    private LoadedExtension? ResolveFenceLanguageExtension(string fenceLanguage)
    {
        if (string.IsNullOrWhiteSpace(fenceLanguage))
            return null;

        var token = fenceLanguage.Trim();
        if (token.StartsWith("{", StringComparison.Ordinal) && token.EndsWith("}", StringComparison.Ordinal) && token.Length > 2)
            token = token[1..^1];

        token = token.Split([' ', '\t', ',', ';', ':'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? token;
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim().TrimStart('.').ToLowerInvariant();
        if (FenceLanguageAliases.TryGetValue(normalized, out var alias))
        {
            if (string.IsNullOrEmpty(alias))
                return null; // explicit plain-text marker - no syntax profile
            normalized = alias;
        }

        var bestMatch = LoadedExtensions
            .Where(extension =>
                extension.Type == "language" &&
                !KodoExtensionIds.IsMarkdown(extension.Id))
            .Select(extension => new
            {
                Extension = extension,
                Score = ScoreFenceLanguageMatch(extension, normalized, token)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Extension.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return bestMatch?.Extension;
    }

    private static int ScoreFenceLanguageMatch(LoadedExtension extension, string normalizedFenceLanguage, string rawFenceLanguage)
    {
        if (string.IsNullOrWhiteSpace(normalizedFenceLanguage))
            return 0;

        var score = 0;
        var normalizedCompact = normalizedFenceLanguage.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        var rawCompact = rawFenceLanguage.Trim().TrimStart('.').Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (extension.Extensions.Any(ext =>
                ext.TrimStart('.').Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 100);
        }

        var normalizedId = extension.Id
            .Replace("-kodo-extension", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-language-support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalizedId.Equals(normalizedCompact, StringComparison.OrdinalIgnoreCase) ||
            normalizedId.Equals(rawCompact, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 90);
        }

        var compactName = extension.Name
            .Replace("Language Support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Support", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (compactName.Equals(normalizedCompact, StringComparison.OrdinalIgnoreCase) ||
            compactName.Equals(rawCompact, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 80);
        }

        if (extension.SyntaxProfiles.Any(profile =>
                profile.Extensions.Any(ext =>
                    ext.TrimStart('.').Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase))))
        {
            score = Math.Max(score, 70);
        }

        if (extension.Types.Any(type =>
                type.Equals(rawFenceLanguage, StringComparison.OrdinalIgnoreCase) ||
                type.Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 60);
        }

        if (extension.Keywords.Any(keyword =>
                keyword.Equals(rawFenceLanguage, StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals(normalizedFenceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 40);
        }

        return score;
    }

    private LoadedExtension? ResolveInlineCodeLanguageExtension(string codeSnippet) =>
        InlineCodeLanguageDetector.Resolve(LoadedExtensions, codeSnippet);

    private void ConfigureRainbowBrackets(LoadedExtension? ext)
    {
        var isMarkdown = KodoExtensionIds.IsMarkdown(ext?.Id);
        _rainbowBracketColorizer.UpdateSyntax(isMarkdown ? null : ext);
        EditorTextBox?.TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private void SetSelectedExtensionsTab(string tab)
    {
        if (string.Equals(_selectedExtensionsTab, tab, StringComparison.Ordinal)) return;
        _selectedExtensionsTab = tab;
        OnPropertyChanged(nameof(IsInstalledTabSelected));
        OnPropertyChanged(nameof(IsLanguagesTabSelected));
        OnPropertyChanged(nameof(IsThemesTabSelected));
        OnPropertyChanged(nameof(IsPluginsTabSelected));
        OnPropertyChanged(nameof(IsCompilersTabSelected));
        OnPropertyChanged(nameof(IsMarketplaceSectionTabSelected));
        OnPropertyChanged(nameof(SelectedExtensionSort));
        NotifyExtensionFiltersChanged();
    }

}
