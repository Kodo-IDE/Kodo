// Licensed under GPL-v3.0
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace KodoUpdater;

internal static class Program
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = CreateHttpClient();

    private static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--relaunch")
        {
            HandleRelaunchSentinel();
            return;
        }
        if (args.Length > 1 && args[0] == "--show-progress")
        {
            await ShowProgressAndInstallAsync(args[1]);
            return;
        }

        Mutex? singleInstance = null;
        try
        {
            singleInstance = new Mutex(initiallyOwned: true, "Kodo-KodoUpdater-SingleInstance", out var createdNew);
            if (!createdNew) return;
        }
        catch
        {
        }

        try
        {
            var pipeCts = new CancellationTokenSource();
            _ = Task.Run(() => PipeListenerLoopAsync(pipeCts.Token));

            while (true)
            {
                try
                {
                    await RunOneCycleAsync();
                }
                catch
                {
                    // Never let one bad cycle kill the whole resident process.
                }

                await Task.Delay(PollInterval);
            }
        }
        finally
        {
            singleInstance?.Dispose();
        }
    }

    private const string PipeName = "Kodo-KodoUpdater-InstallCommand";

    private sealed class InstallRequest
    {
        [JsonPropertyName("installerPath")]
        public string InstallerPath { get; set; } = "";

        [JsonPropertyName("reopen")]
        public bool Reopen { get; set; }
    }

    private static async Task PipeListenerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var json = await reader.ReadToEndAsync(ct);
                var request = JsonSerializer.Deserialize<InstallRequest>(json, JsonOptions);
                if (request is not null && !string.IsNullOrEmpty(request.InstallerPath))
                {
                    _ = Task.Run(() => HandleInstallRequestAsync(request));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private static async Task HandleInstallRequestAsync(InstallRequest request)
    {
        try
        {
            if (request.Reopen)
                WriteRelaunchSentinel();
            var helperPath = PrepareProgressHelper();
            if (helperPath is null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName  = request.InstallerPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName        = helperPath,
                Arguments       = $"--show-progress \"{request.InstallerPath}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort - on failure Kodo just doesn't restart this cycle.
        }
        await Task.CompletedTask;
    }

    private static string? PrepareProgressHelper()
    {
        try
        {
            var sourcePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return null;

            var dir = Path.Combine(Path.GetTempPath(), "Kodo-Update");
            Directory.CreateDirectory(dir);
            var destPath = Path.Combine(dir, "KodoUpdaterProgress.exe");

            File.Copy(sourcePath, destPath, overwrite: true);
            return destPath;
        }
        catch
        {
            return null;
        }
    }
    private static async Task ShowProgressAndInstallAsync(string installerPath)
    {
        try
        {
            IClassicDesktopStyleApplicationLifetime? appLifetime = null;
            var dialog = new InstallerProgressDialog();

            var staThread = new Thread(() =>
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(new string[0], lt =>
                {
                    appLifetime = lt;
                    dialog.Show();
                });
                // Blocks here until appLifetime.Shutdown() is called from
            });
            if (OperatingSystem.IsWindows())
            {
                staThread.SetApartmentState(ApartmentState.STA);
            }
            staThread.IsBackground = true;
            staThread.Start();
            var progressFilePath = Path.Combine(Path.GetTempPath(), "Kodo-Update", "install-progress.json");
            var progressCts = new CancellationTokenSource();
            var pollTask = Task.Run(() => PollInstallProgressAsync(dialog, progressFilePath, progressCts.Token));

            // Run the installer on a background thread while the UI pumps
            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName  = installerPath,
                        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                        UseShellExecute = true,
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit();
                }
                catch
                {
                }

                progressCts.Cancel();
                Dispatcher.UIThread.Post(() =>
                {
                    dialog.SetProgress(100);
                    dialog.Close();
                    appLifetime?.Shutdown();
                });
            });

            try { await pollTask; } catch { /* cancelled */ }
            staThread.Join();
        }
        catch
        {
        }
    }

    private static async Task PollInstallProgressAsync(InstallerProgressDialog dialog, string path, CancellationToken ct)
    {
        var lastPercent = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    var record = JsonSerializer.Deserialize<InstallProgressRecord>(json, JsonOptions);
                    if (record is not null && record.Percent != lastPercent)
                    {
                        lastPercent = record.Percent;
                        dialog.SetProgress(record.Percent);
                    }
                }
            }
            catch
            {
            }

            try { await Task.Delay(150, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private sealed class InstallProgressRecord
    {
        [JsonPropertyName("percent")]
        public int Percent { get; set; }
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();


    private static string RelaunchSentinelPath =>
        Path.Combine(Path.GetTempPath(), "Kodo-Update", "relaunch.json");

    private sealed class RelaunchRecord
    {
        public string KodoExePath { get; set; } = "";
        public DateTime WrittenAtUtc { get; set; }
    }

    private static void WriteRelaunchSentinel()
    {
        try
        {
            var dir = Path.GetDirectoryName(RelaunchSentinelPath)!;
            Directory.CreateDirectory(dir);

            var kodoExePath = Environment.ProcessPath ?? "";
            var record = new RelaunchRecord { KodoExePath = kodoExePath, WrittenAtUtc = DateTime.UtcNow };
            File.WriteAllText(RelaunchSentinelPath, JsonSerializer.Serialize(record));
        }
        catch
        {
            // Best-effort - if this fails, Kodo just doesn't restart after
        }
    }

    private static void HandleRelaunchSentinel()
    {
        try
        {
            if (!File.Exists(RelaunchSentinelPath)) return;

            var json = File.ReadAllText(RelaunchSentinelPath);
            var record = JsonSerializer.Deserialize<RelaunchRecord>(json);
            File.Delete(RelaunchSentinelPath);

            if (record is null || string.IsNullOrEmpty(record.KodoExePath)) return;
            if (!File.Exists(record.KodoExePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName        = record.KodoExePath,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }


    private static async Task RunOneCycleAsync()
    {
        var settings = ReadSettings();
        if (!settings.AutoUpdateAppEnabled)
            return;

        var localVersion = ReadInstalledKodoVersion();
        var pending = PendingUpdate.TryRead();
        if (pending is not null && File.Exists(pending.InstallerPath))
        {
            if (!IsNewerVersion(pending.Version, localVersion))
            {
                PendingUpdate.Clear();
                try { File.Delete(pending.InstallerPath); } catch { /* best-effort cleanup */ }
            }
            else
            {
                if (settings.AutoUpdateAppInBackgroundEnabled && !IsKodoRunning())
                    LaunchInstallerSilently(pending.InstallerPath);
                return;
            }
        }

        var update = await CheckForUpdateAsync(localVersion);
        if (update is null)
            return;

        string installerPath;
        try
        {
            installerPath = await DownloadInstallerAsync(update);
        }
        catch
        {
            return; // Network hiccup - try again next cycle.
        }

        if (settings.AutoUpdateAppInBackgroundEnabled && !IsKodoRunning())
        {
            LaunchInstallerSilently(installerPath);
            return;
        }

        PendingUpdate.Write(update.Version, installerPath);
    }

    private static UpdaterSettings ReadSettings()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kodo", "kodosettings.json");

            if (!File.Exists(path)) return new UpdaterSettings();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new UpdaterSettings();

            return JsonSerializer.Deserialize<UpdaterSettings>(json) ?? new UpdaterSettings();
        }
        catch
        {
            return new UpdaterSettings();
        }
    }

    private sealed class UpdaterSettings
    {
        public bool AutoUpdateAppEnabled { get; set; } = true;
        public bool AutoUpdateAppInBackgroundEnabled { get; set; }
    }

    private static string ReadInstalledKodoVersion()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var kodoExePath = Path.Combine(exeDir, "Kodo.exe");
            if (!File.Exists(kodoExePath)) return "v0.0.0";

            var info = FileVersionInfo.GetVersionInfo(kodoExePath);
            var raw = info.ProductVersion ?? info.FileVersion ?? "v0.0.0";
            var plusIndex = raw.IndexOf('+');
            return plusIndex >= 0 ? raw[..plusIndex] : raw;
        }
        catch
        {
            return "v0.0.0";
        }
    }

    private static bool IsKodoRunning()
    {
        try
        {
            return Process.GetProcessesByName("Kodo").Length > 0;
        }
        catch
        {
            return true;
        }
    }


    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kodo-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task<UpdateInfo?> CheckForUpdateAsync(string localVersion)
    {
        try
        {
            using var response = await Http.GetAsync("https://api.github.com/repos/Kodo-IDE/Kodo/releases/latest");
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;
            if (release.Draft || release.Prerelease) return null;
            if (!IsNewerVersion(release.TagName, localVersion)) return null;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            return new UpdateInfo(release.TagName, asset.BrowserDownloadUrl, asset.Name);
        }
        catch
        {
        }

        return null;
    }

    private static bool IsNewerVersion(string remote, string local)
    {
        var r = ParseVersionParts(remote);
        var l = ParseVersionParts(local);
        if (r is null || l is null) return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < Math.Max(r.Length, l.Length); i++)
        {
            var rv = i < r.Length ? r[i] : 0;
            var lv = i < l.Length ? l[i] : 0;
            if (rv != lv) return rv > lv;
        }
        return false;
    }

    private static int[]? ParseVersionParts(string tag)
    {
        var core = tag.Trim();
        if (core.Length > 0 && (core[0] == 'v' || core[0] == 'V')) core = core[1..];
        var dash = core.IndexOf('-'); if (dash >= 0) core = core[..dash];
        var plus = core.IndexOf('+'); if (plus >= 0) core = core[..plus];

        var segments = core.Split('.');
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
            if (!int.TryParse(segments[i], out parts[i])) return null;

        return parts.Length > 0 ? parts : null;
    }

    private static async Task<string> DownloadInstallerAsync(UpdateInfo update)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Kodo-Update");
        Directory.CreateDirectory(dir);
        var destPath = Path.Combine(dir, update.AssetName);

        using var response = await Http.GetAsync(update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await httpStream.CopyToAsync(fileStream);

        return destPath;
    }
    private static void LaunchInstallerSilently(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
            });
            PendingUpdate.Clear();
        }
        catch
        {
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record UpdateInfo(string Version, string AssetDownloadUrl, string AssetName);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public GitHubAsset[]? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}
internal static class PendingUpdate
{
    private static string FilePath => Path.Combine(Path.GetTempPath(), "Kodo-Update", "pending.json");

    public static void Write(string version, string installerPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var payload = JsonSerializer.Serialize(new PendingUpdateRecord(version, installerPath, DateTime.UtcNow));
            File.WriteAllText(FilePath, payload);
        }
        catch
        {
        }
    }

    public static PendingUpdateRecord? TryRead()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<PendingUpdateRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { /* ignore */ }
    }
}

internal sealed record PendingUpdateRecord(string Version, string InstallerPath, DateTime DownloadedAtUtc);