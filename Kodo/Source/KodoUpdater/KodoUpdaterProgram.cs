// Licensed under GPL v3.0

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KodoUpdater;

internal static class Program
{
    private static string? _trustedInstallRoot;
    private const int PidWaitTimeoutSeconds = 60;
    private const int InstallerTimeoutMinutes = 15;
    private const int StaleTransactionHours = 24;
    private const int ConfirmationTimeoutSeconds = 180;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string UpdaterTempFileName =>
        OperatingSystem.IsWindows() ? "KodoUpdater-temp.exe" : "KodoUpdater-temp";

    private static string UpdaterUsageName =>
        OperatingSystem.IsWindows() ? "KodoUpdater.exe" : "KodoUpdater";

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kodo", "update", "updater.log");

    private static async Task<int> Main(string[] args)
    {
        var forwardedInstallRoot = Array.FindIndex(args, a => string.Equals(a, "--install-root", StringComparison.OrdinalIgnoreCase));
        if (forwardedInstallRoot >= 0 && forwardedInstallRoot + 1 < args.Length)
        {
            _trustedInstallRoot = Path.GetFullPath(args[forwardedInstallRoot + 1]);
            args = args.Where((_, i) => i != forwardedInstallRoot && i != forwardedInstallRoot + 1).ToArray();
        }
        else
        {
            var localUpdate = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update"));
            var self = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(self) || !Kodo.HotfixShared.HotfixShared.IsInsideDirectory(Path.GetFullPath(self), localUpdate))
                _trustedInstallRoot = Path.GetFullPath(AppContext.BaseDirectory);
        }
        Log($"KodoUpdater start args=[{string.Join(" ", args)}] pid={Environment.ProcessId}");

        try
        {
            var selfPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(selfPath))
            {
                var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var selfDir = Path.GetDirectoryName(Path.GetFullPath(selfPath))?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
                var isInApp = selfDir.Equals(appDir, PathComparison)
                    || selfDir.EndsWith("Kodo", PathComparison);
                var updateTempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
                var isInTemp = selfPath.StartsWith(Path.GetTempPath(), PathComparison)
                    || selfDir.StartsWith(updateTempDir, PathComparison);
                if (isInApp && !isInTemp && args.Length > 0)
                {
                    var tempCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update", UpdaterTempFileName);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(tempCopy)!);
                        File.Copy(selfPath, tempCopy, overwrite: true);
                        Log($"Relocating self to temp: {selfPath} -> {tempCopy}");
                        var psi = new ProcessStartInfo { FileName = tempCopy, UseShellExecute = false, CreateNoWindow = true };
                        psi.ArgumentList.Add("--install-root");
                        psi.ArgumentList.Add(appDir);
                        foreach (var a in args) psi.ArgumentList.Add(a);
                        var p = Process.Start(psi);
                        Log($"Relaunched from temp PID {p?.Id}, exiting original");
                        return 0;
                    }
                    catch (Exception ex) { Log($"Self-relocation failed, continuing in-place: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log($"Self-relocation check failed: {ex.Message}"); }

        if (args.Length == 0)
        {
            Log($"No transaction path supplied. Usage: {UpdaterUsageName} <transaction.json> | --rollback <transaction-id-or-path>");
            return 2;
        }

        // Phase 4 manual recovery: roll back a failed hotfix without touching DLLs by hand.
        if (string.Equals(args[0].Trim(), "--rollback", StringComparison.OrdinalIgnoreCase))
        {
            var idOrPath = args.Length > 1 ? string.Join(" ", args.Skip(1)).Trim().Trim('"') : "";
            if (string.IsNullOrWhiteSpace(idOrPath))
            {
                Log($"Usage: {UpdaterUsageName} --rollback <transaction-id-or-path>");
                return 2;
            }
            try
            {
                return await RunManualRollbackAsync(idOrPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Fatal: {ex}");
                return 1;
            }
            finally
            {
                Log("KodoUpdater exit");
            }
        }

        var transactionPath = args[0].Trim().Trim('"');
        if (args.Length > 1)
            transactionPath = string.Join(" ", args).Trim().Trim('"');

        var mutexName = OperatingSystem.IsWindows()
            ? $"Global\\Kodo-Updater-{SanitizeForMutex(Path.GetFileNameWithoutExtension(transactionPath))}"
            : $"Kodo-Updater-{SanitizeForMutex(Path.GetFileNameWithoutExtension(transactionPath))}";
        using var guard = UpdaterMutex.TryAcquire(mutexName);
        if (guard is null)
        {
            Log($"Another updater already running for {transactionPath} (mutex {mutexName}). Exiting.");
            return 3;
        }

        try
        {
            return await RunTransactionAsync(transactionPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
            return 1;
        }
        finally
        {
            Log("KodoUpdater exit");
        }
    }

    private static async Task<int> RunTransactionAsync(string transactionPath)
    {
        if (!File.Exists(transactionPath))
        {
            Log($"Transaction not found: {transactionPath}");
            return 4;
        }

        UpdateTransaction? tx;
        string rawJson;
        try
        {
            rawJson = await File.ReadAllTextAsync(transactionPath).ConfigureAwait(false);
            if (Kodo.HotfixShared.HotfixShared.IsHotfixTransactionJson(rawJson))
                return await RunHotfixTransactionAsync(transactionPath, rawJson).ConfigureAwait(false);
            tx = JsonSerializer.Deserialize(rawJson, UpdateTransactionJsonContext.Default.UpdateTransaction);
        }
        catch (Exception ex)
        {
            Log($"Malformed transaction {transactionPath}: {ex.Message}");
            TryDelete(transactionPath);
            return 5;
        }

        if (tx is null || string.IsNullOrWhiteSpace(tx.InstallerPath) || string.IsNullOrWhiteSpace(tx.KodoExePath))
        {
            Log($"Invalid transaction content: {tx}");
            TryDelete(transactionPath);
            return 5;
        }

        if (tx.CreatedAtUtc < DateTime.UtcNow.AddHours(-StaleTransactionHours))
        {
            Log($"Stale transaction {tx.TransactionId} created {tx.CreatedAtUtc:o} – discarding.");
            TryDelete(transactionPath);
            return 6;
        }

        var expectedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
        try
        {
            var fullTx = Path.GetFullPath(transactionPath);
            var fullExpected = Path.GetFullPath(expectedDir);
            if (!IsInsideDirectory(fullTx, fullExpected))
            {
                Log($"Transaction outside expected dir: {fullTx} !inside {fullExpected}");
                return 7;
            }
        }
        catch (Exception ex)
        {
            Log($"Path validation failed: {ex.Message}");
            return 7;
        }

        var installRoot = Path.GetDirectoryName(Path.GetFullPath(tx.KodoExePath));
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            Log("Update transaction does not identify a valid installation directory.");
            return 7;
        }
        using var installGuard = InstallUpdateGuard.TryAcquire(installRoot);
        if (installGuard is null)
        {
            Log($"Another updater operation is already modifying installation '{installRoot}'. Exiting.");
            return 3;
        }

        Log($"Transaction {tx.TransactionId} pid={tx.KodoPid} installer={tx.InstallerPath} kodo={tx.KodoExePath} restart={tx.RestartAfterUpdate}");

        await WaitForKodoExitAsync(tx.KodoPid, tx.KodoExePath).ConfigureAwait(false);

        await Task.Delay(800).ConfigureAwait(false);

        if (!File.Exists(tx.InstallerPath))
        {
            Log($"Installer not found: {tx.InstallerPath}");
            TryDelete(transactionPath);
            return 8;
        }

        var fi = new FileInfo(tx.InstallerPath);
        if (fi.Length < 1024 * 1024)
        {
            Log($"Installer too small ({fi.Length} bytes), likely incomplete: {tx.InstallerPath}");
            TryDelete(transactionPath);
            return 9;
        }

        if (tx.ExpectedSize > 0 && fi.Length != tx.ExpectedSize)
        {
            Log($"Installer size mismatch: expected {tx.ExpectedSize} bytes, found {fi.Length}: {tx.InstallerPath}");
            TryDelete(transactionPath);
            return 9;
        }

        if (!string.IsNullOrWhiteSpace(tx.Sha256) && !VerifyFileSha256(tx.InstallerPath, tx.Sha256))
        {
            Log($"Installer checksum mismatch for {tx.InstallerPath} – not launching, keeping files for diagnostics");
            return 9;
        }

        if (OperatingSystem.IsLinux())
        {
            // Managed archive installs update automatically (tarball swap);
            // anything else (AppImage, .deb, foreign layouts) stays manual.
            if (IsManagedTarballTransaction(tx))
                return await RunLinuxTarballTransactionAsync(tx, transactionPath).ConfigureAwait(false);
            Log($"Linux manual update: staged={tx.InstallerPath}. " + LinuxManualBlurb(tx.InstallerPath));
            TryDelete(transactionPath);
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Log($"Unsupported updater platform for automatic install: {tx.InstallerPath}. Manual installation required.");
            TryDelete(transactionPath);
            return 0;
        }

        Log($"Launching installer: {tx.InstallerPath}");
        var psi = new ProcessStartInfo
        {
            FileName = tx.InstallerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(tx.InstallerPath) ?? Path.GetTempPath(),
        };

        Process? installerProc;
        try
        {
            installerProc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"Failed to start installer: {ex}");
            TryDelete(transactionPath);
            return 10;
        }

        if (installerProc is null)
        {
            Log("Process.Start returned null for installer");
            TryDelete(transactionPath);
            return 10;
        }

        Log($"Installer PID {installerProc.Id} started, waiting (timeout {InstallerTimeoutMinutes}m)...");
        try
        {
            var exited = installerProc.WaitForExit(TimeSpan.FromMinutes(InstallerTimeoutMinutes));
            if (!exited)
            {
                Log($"Installer timed out after {InstallerTimeoutMinutes}m – killing");
                try { installerProc.Kill(entireProcessTree: true); } catch { }
                TryDelete(transactionPath);
                return 11;
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForExit failed: {ex}");
        }

        Log($"Installer exited with code {installerProc.ExitCode}");

        if (installerProc.ExitCode != 0)
        {
            Log($"Installer failed with exit code {installerProc.ExitCode} – not restarting Kodo, keeping transaction for diagnostics");
            return installerProc.ExitCode;
        }

        TryDelete(transactionPath);
        TryDeleteIfInStaging(tx.InstallerPath);

        RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);

        Log("Update orchestration complete");
        return 0;
    }

    // --- Linux managed-tarball full updates (parity with the Windows installer flow) ---

    private static bool IsManagedTarballTransaction(UpdateTransaction tx)
    {
        try
        {
            if (tx is null || string.IsNullOrWhiteSpace(tx.InstallerPath) || string.IsNullOrWhiteSpace(tx.KodoExePath))
                return false;
            var asset = tx.AssetName ?? Path.GetFileName(tx.InstallerPath) ?? "";
            if (!asset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) &&
                !asset.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                return false;
            var installRoot = Path.GetDirectoryName(Path.GetFullPath(tx.KodoExePath));
            if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot)) return false;
            if (!File.Exists(Path.Combine(installRoot, Kodo.HotfixShared.HotfixShared.ManagedInstallMarkerFileName)))
                return false;
            // The tarball always ships both binaries; at least the updater
            // must be present for a managed install.
            var updaterPresent = File.Exists(Path.Combine(installRoot, "KodoUpdater")) ||
                File.Exists(Path.Combine(installRoot, "kodoUpdater"));
            if (!updaterPresent) return false;
            return Kodo.HotfixShared.HotfixShared.IsWritableDirectory(installRoot);
        }
        catch (Exception ex)
        {
            Log($"Managed-install check failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<int> RunLinuxTarballTransactionAsync(UpdateTransaction tx, string transactionPath)
    {
        var installRoot = Path.GetDirectoryName(Path.GetFullPath(tx.KodoExePath))!;
        using var installGuard = InstallUpdateGuard.TryAcquire(installRoot);
        if (installGuard is null)
        {
            Log($"Another updater operation is already modifying installation '{installRoot}'. Exiting.");
            return 3;
        }

        var updateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
        CleanupOrphanedLinuxWorkDirs(Path.Combine(updateDir, "linux-full"), tx.TransactionId);

        Log($"Linux tarball transaction {tx.TransactionId} installer={tx.InstallerPath} kodo={tx.KodoExePath} restart={tx.RestartAfterUpdate}");

        await WaitForKodoExitAsync(tx.KodoPid, tx.KodoExePath).ConfigureAwait(false);
        await Task.Delay(800).ConfigureAwait(false);

        var result = Kodo.HotfixShared.HotfixShared.InstallTarballUpdate(new Kodo.HotfixShared.TarballInstallRequest
        {
            TarballPath = tx.InstallerPath,
            TargetDir = installRoot,
            // Per-transaction work root: staging/backup dir names inside are
            // deterministic, so concurrent updaters for different installs
            // must not share a root.
            WorkRoot = Path.Combine(updateDir, "linux-full", tx.TransactionId),
            KodoExeName = Path.GetFileName(tx.KodoExePath),
            KodoUpdaterName = "KodoUpdater",
        }, Log);

        if (!result.Success)
        {
            Log($"Linux tarball installation failed: {result.Error} – transaction retained for diagnostics.");
            await MarkTxFailedAsync(transactionPath, result.Error ?? "tarball installation failed").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.RetainedBackupDir))
                Log($"Previous install retained at {result.RetainedBackupDir}; re-extract the tarball manually to recover.");
            return 40;
        }

        TryDelete(transactionPath);
        TryDeleteIfInStaging(tx.InstallerPath);

        RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);

        Log("Linux tarball update orchestration complete");
        return 0;
    }

    private static void CleanupOrphanedLinuxWorkDirs(string linuxFullRoot, string currentTransactionId)
    {
        // Per-transaction work dirs orphaned by a killed updater (or a restore
        // failure whose transaction was later discarded) would otherwise
        // accumulate. Only removes dirs older than 48h and never the current
        // transaction's dir: anything that old cannot belong to a live
        // operation (full-release transactions themselves go stale at 24h).
        try
        {
            if (!Directory.Exists(linuxFullRoot)) return;
            foreach (var dir in Directory.GetDirectories(linuxFullRoot))
            {
                try
                {
                    if (string.Equals(Path.GetFileName(dir), currentTransactionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var info = new DirectoryInfo(dir);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(48))
                    {
                        Directory.Delete(dir, recursive: true);
                        Log($"Removed orphaned tarball work dir: {dir}");
                    }
                }
                catch (Exception ex) { Log($"Orphaned work dir cleanup failed for {dir}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log($"Orphaned work dir scan failed: {ex.Message}"); }
    }

    private static async Task WaitForKodoExitAsync(int kodoPid, string kodoExePath)
    {
        if (kodoPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(kodoPid);
                bool pidReused = false;
                try
                {
                    var procPath = proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(procPath) && !string.IsNullOrWhiteSpace(kodoExePath))
                    {
                        var normProc = Path.GetFullPath(procPath).TrimEnd(Path.DirectorySeparatorChar);
                        var normTx = Path.GetFullPath(kodoExePath).TrimEnd(Path.DirectorySeparatorChar);
                        if (!string.Equals(normProc, normTx, PathComparison))
                        {
                            Log($"PID {kodoPid} path mismatch: proc={normProc} tx={normTx} – PID reused, treating Kodo as already exited");
                            pidReused = true;
                        }
                    }
                }
                catch { }

                if (pidReused)
                {
                    Log($"Kodo PID {kodoPid} considered already exited (PID reuse)");
                }
                else if (!proc.HasExited)
                {
                    Log($"Waiting for Kodo PID {kodoPid} to exit (timeout {PidWaitTimeoutSeconds}s)...");
                    var sw = Stopwatch.StartNew();
                    while (!proc.HasExited && sw.Elapsed.TotalSeconds < PidWaitTimeoutSeconds)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                    }

                    if (!proc.HasExited)
                    {
                        Log($"Kodo PID {kodoPid} did not exit within {PidWaitTimeoutSeconds}s – proceeding anyway (Inno /CLOSEAPPLICATIONS will handle)");
                    }
                    else
                    {
                        Log($"Kodo PID {kodoPid} exited after {sw.Elapsed.TotalSeconds:0.0}s");
                    }
                }
                else
                {
                    Log($"Kodo PID {kodoPid} already exited");
                }
            }
            catch (ArgumentException)
            {
                Log($"Kodo PID {kodoPid} not found – already exited");
            }
            catch (Exception ex)
            {
                Log($"Wait for PID {kodoPid} failed: {ex.Message} – continuing");
            }
        }
        else
        {
            Log("No Kodo PID supplied – not waiting");
        }
    }

    private static void RestartKodo(string kodoExePath, bool restartAfterUpdate)
    {
        if (!restartAfterUpdate)
        {
            Log("Restart not requested");
            return;
        }
        var target = Kodo.HotfixShared.HotfixShared.ResolveRestartTarget(kodoExePath);
        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            Log($"Restarting Kodo: {target}");
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                }
                else
                {
                    // On Unix launch the binary directly (no shell/xdg-open
                    // involvement) with the install dir as working directory.
                    // Best effort: ensure it is executable first so a hotfix
                    // that replaced the binary can never leave it unstartable.
                    if (!Kodo.HotfixShared.HotfixShared.EnsureExecutable(target))
                        Log($"Warning: could not ensure executable permission on {target}; attempting restart anyway.");
                    var installDir = Path.GetDirectoryName(Path.GetFullPath(target));
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = false,
                        WorkingDirectory = string.IsNullOrWhiteSpace(installDir) ? AppContext.BaseDirectory : installDir,
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to restart Kodo: {ex}");
            }
        }
        else
        {
            Log($"Kodo exe not found for restart: {kodoExePath}");
        }
    }

    // --- Phase 3: hotfix application (file copy only; never executes package files) ---

    private static async Task<int> RunHotfixTransactionAsync(string transactionPath, string rawJson)
    {
        Log($"Hotfix application started: {transactionPath}");
        if (!Kodo.HotfixShared.HotfixShared.TryParseTransaction(rawJson, out var tx, out var parseError) || tx is null)
        {
            Log($"Malformed hotfix transaction: {parseError}");
            TryDelete(transactionPath);
            return 21;
        }

        if (tx.CreatedAtUtc != DateTime.MinValue && tx.CreatedAtUtc < DateTime.UtcNow.AddHours(-StaleTransactionHours))
        {
            Log($"Stale hotfix transaction {tx.TransactionId} created {tx.CreatedAtUtc:o} – discarding.");
            TryDelete(transactionPath);
            return 22;
        }

        var expectedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
        try
        {
            var fullTx = Path.GetFullPath(transactionPath);
            var fullExpected = Path.GetFullPath(expectedDir);
            if (!IsInsideDirectory(fullTx, fullExpected))
            {
                Log($"Hotfix transaction outside expected dir: {fullTx} !inside {fullExpected}");
                return 23;
            }
        }
        catch (Exception ex)
        {
            Log($"Hotfix path validation failed: {ex.Message}");
            return 23;
        }

        string? txPathError = null;
        if (string.IsNullOrWhiteSpace(_trustedInstallRoot) ||
            !Kodo.HotfixShared.HotfixShared.ValidateTransactionPaths(tx, transactionPath, expectedDir, _trustedInstallRoot!, out txPathError))
        {
            Log($"Hotfix transaction path validation failed: {txPathError ?? "install root is unknown"}");
            return 23;
        }

        using var installGuard = InstallUpdateGuard.TryAcquire(tx.TargetDir);
        if (installGuard is null)
        {
            Log($"Another updater operation is already modifying installation '{tx.TargetDir}'. Exiting.");
            return 3;
        }

        // Terminal states never re-apply. If an earlier updater died after
        // applying files, supervise the confirmation retry too so a failed
        // startup still rolls back automatically.
        if (!string.Equals(tx.Status, Kodo.HotfixShared.HotfixTransactionStatus.Staged, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(tx.Status, Kodo.HotfixShared.HotfixTransactionStatus.AwaitingConfirmation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tx.Status, Kodo.HotfixShared.HotfixTransactionStatus.Applied, StringComparison.OrdinalIgnoreCase))
            {
                if (!tx.RestartAfterUpdate)
                {
                    Log($"Hotfix transaction {tx.TransactionId} is {tx.Status}; restart was disabled, leaving startup confirmation to the user.");
                    return 0;
                }
                Log($"Hotfix transaction {tx.TransactionId} is {tx.Status} – supervising startup confirmation retry.");
                var existingProcess = FindRunningKodoProcess(tx.KodoExePath);
                var confirmationProcess = existingProcess ?? StartKodoForConfirmation(tx);
                return await SuperviseHotfixConfirmationAsync(tx, transactionPath, expectedDir, confirmationProcess).ConfigureAwait(false);
            }
            Log($"Hotfix transaction {tx.TransactionId} already {tx.Status} – nothing to do.");
            return 0;
        }

        Log($"Hotfix transaction {tx.TransactionId} base={tx.BaseVersion} HF{tx.HotfixLevel} platform={tx.PlatformRid} kodo={tx.KodoExePath} restart={tx.RestartAfterUpdate}");

        await WaitForKodoExitAsync(tx.KodoPid, tx.KodoExePath).ConfigureAwait(false);
        await Task.Delay(800).ConfigureAwait(false);

        string manifestJson;
        try
        {
            if (!File.Exists(tx.ManifestPath))
            {
                Log($"Hotfix manifest not found: {tx.ManifestPath}");
                return 24;
            }
            manifestJson = await File.ReadAllTextAsync(tx.ManifestPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Hotfix manifest unreadable: {ex.Message}");
            return 24;
        }

        var stateFilePath = Path.Combine(expectedDir, "hotfix-state.json");

        // Guard before touching anything: a retried/stale transaction must not
        // downgrade an already-newer install.
        var (liveBase, liveLevel, liveLastKnownGood) = ReadLiveHotfixState(stateFilePath);
        // Cumulative installs bake hotfixes into the files on disk. Treat the
        // shipped stamp as a floor so a stale staged transaction can never
        // downgrade a cumulative install (e.g. HF2 staged, then the user
        // reinstalls cumulative HF3, then the updater runs).
        if (Kodo.HotfixShared.HotfixShared.TryGetShippedHotfixLevel(tx.TargetDir, tx.BaseVersion, out var shippedLevel) &&
            (string.IsNullOrWhiteSpace(liveBase) ||
             Kodo.HotfixShared.HotfixShared.AreSameBaseVersion(liveBase, tx.BaseVersion)) &&
            shippedLevel > liveLevel)
        {
            liveBase = tx.BaseVersion;
            liveLevel = shippedLevel;
            liveLastKnownGood = Math.Max(liveLastKnownGood, shippedLevel);
            Log($"Shipped hotfix floor is HF{shippedLevel}; stale state ignored.");
        }
        if (!string.IsNullOrWhiteSpace(liveBase) &&
            !Kodo.HotfixShared.HotfixShared.AreSameBaseVersion(liveBase, tx.BaseVersion))
        {
            Log($"Hotfix base {tx.BaseVersion} no longer matches installed base {liveBase}; transaction discarded.");
            await MarkTxFailedAsync(transactionPath, "Installed base version changed before apply.").ConfigureAwait(false);
            return 25;
        }
        if (Kodo.HotfixShared.HotfixShared.AreSameBaseVersion(liveBase, tx.BaseVersion) && tx.HotfixLevel <= liveLevel)
        {
            Log($"Hotfix HF{tx.HotfixLevel} not newer than installed HF{liveLevel} – discarding without changes.");
            return 25;
        }

        var result = Kodo.HotfixShared.HotfixShared.Apply(new Kodo.HotfixShared.HotfixApplyRequest
        {
            ManifestJson = manifestJson,
            PayloadDir = tx.PayloadDir,
            BackupDir = tx.BackupDir,
            TargetDir = tx.TargetDir,
            StateFilePath = stateFilePath,
            ExpectedBaseVersion = tx.BaseVersion,
            InstalledHotfixLevel = Kodo.HotfixShared.HotfixShared.AreSameBaseVersion(liveBase, tx.BaseVersion) ? liveLevel : -1,
            PreviousLastKnownGood = Kodo.HotfixShared.HotfixShared.AreSameBaseVersion(liveBase, tx.BaseVersion) ? Math.Max(0, liveLastKnownGood) : 0,
            ExpectedHotfixLevel = tx.HotfixLevel,
            ExpectedPlatformRid = tx.PlatformRid,
            // This callback runs only after every original file and the complete
            // backup index are durable, immediately before the first replace.
            BeforeReplace = () =>
            {
                var awaitingJson = Kodo.HotfixShared.HotfixShared.MarkAwaitingConfirmation(
                    rawJson, DateTime.UtcNow.AddSeconds(ConfirmationTimeoutSeconds), Math.Max(0, liveLastKnownGood));
                Kodo.HotfixShared.HotfixShared.WriteTextAtomically(transactionPath, awaitingJson);
                rawJson = awaitingJson;
            },
        }, Log);

        if (!result.Success)
        {
            Log($"Hotfix application failed: {result.Error} – attempting recovery from the staged backup.");
            return await RollbackAndRestartAsync(tx, transactionPath, expectedDir, "apply failed", 26).ConfigureAwait(false);
        }

        foreach (var f in result.ReplacedFiles)
            Log($"File replaced: {f}");

        Log("Hotfix application completed");

        // Files copied is NOT success yet; Kodo must start and confirm hashes.
        Log("Hotfix transaction marked awaitingConfirmation (retained with backup for rollback).");

        CleanupHotfixPartials(expectedDir);

        if (!tx.RestartAfterUpdate)
        {
            Log("Restart not requested; confirmation will happen on next Kodo start.");
            return 0;
        }

        var kodoProcess = StartKodoForConfirmation(tx);
        Log("Kodo restarted");

        return await SuperviseHotfixConfirmationAsync(tx, transactionPath, expectedDir, kodoProcess).ConfigureAwait(false);
    }

    private static Process? StartKodoForConfirmation(Kodo.HotfixShared.HotfixTransaction tx)
    {
        var target = Kodo.HotfixShared.HotfixShared.ResolveRestartTarget(tx.KodoExePath);
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            Log($"Kodo exe not found for confirmation restart: {tx.KodoExePath}");
            return null;
        }
        Log($"Starting Kodo for confirmation: {target}");
        try { return Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log($"Failed to start Kodo for confirmation: {ex}");
            return null;
        }
    }

    private static Process? FindRunningKodoProcess(string kodoExePath)
    {
        var target = Kodo.HotfixShared.HotfixShared.ResolveRestartTarget(kodoExePath);
        if (string.IsNullOrWhiteSpace(target)) return null;
        string fullTarget;
        try { fullTarget = Path.GetFullPath(target); }
        catch { return null; }
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited) { process.Dispose(); continue; }
                var processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath) &&
                    string.Equals(Path.GetFullPath(processPath), fullTarget, PathComparison))
                    return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    private static string ReadTxStatus(string transactionPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(transactionPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "status", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString() ?? "";
            }
            return "";
        }
        catch { return ""; }
    }

    private static async Task<int> SuperviseHotfixConfirmationAsync(
        Kodo.HotfixShared.HotfixTransaction tx,
        string transactionPath,
        string expectedDir,
        Process? kodoProcess)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ConfirmationTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(ReadTxStatus(transactionPath),
                Kodo.HotfixShared.HotfixTransactionStatus.Confirmed, StringComparison.OrdinalIgnoreCase))
            {
                Log("Hotfix committed (startup confirmed by Kodo).");
                CleanupHotfixPartials(expectedDir);
                return 0;
            }
            if (kodoProcess is null)
            {
                Log("Hotfix confirmation failed: Kodo could not be started.");
                return await RollbackAndRestartAsync(tx, transactionPath, expectedDir, "Kodo failed to start after hotfix", 30).ConfigureAwait(false);
            }
            try
            {
                if (kodoProcess.HasExited)
                {
                    int code;
                    try { code = kodoProcess.ExitCode; } catch { code = -1; }
                    Log($"Hotfix confirmation failed: Kodo exited (code {code}) before confirming startup.");
                    return await RollbackAndRestartAsync(tx, transactionPath, expectedDir, "Kodo exited before startup confirmation", 30).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"Hotfix supervision error: {ex.Message}");
                return await RollbackAndRestartAsync(tx, transactionPath, expectedDir, "supervision error", 30).ConfigureAwait(false);
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }

        try
        {
            if (kodoProcess is not null && !kodoProcess.HasExited)
            {
                Log("Hotfix confirmation timed out with Kodo still running; stopping Kodo for rollback.");
                try { kodoProcess.Kill(entireProcessTree: true); } catch (Exception ex) { Log($"Kill failed: {ex.Message}"); }
                await Task.Delay(2000).ConfigureAwait(false);
            }
            else
            {
                Log("Hotfix confirmation timed out.");
            }
        }
        catch (Exception ex)
        {
            Log($"Hotfix timeout handling error: {ex.Message}");
        }
        return await RollbackAndRestartAsync(tx, transactionPath, expectedDir, "startup confirmation timed out", 31).ConfigureAwait(false);
    }

    private static async Task<int> RollbackAndRestartAsync(
        Kodo.HotfixShared.HotfixTransaction tx,
        string transactionPath,
        string expectedDir,
        string reason,
        int rollbackExitCode)
    {
        Log("Rollback started");
        string manifestJson;
        try
        {
            if (string.IsNullOrWhiteSpace(tx.ManifestPath) || !File.Exists(tx.ManifestPath))
            {
                Log($"Rollback failed: manifest not found: {tx.ManifestPath}");
                await MarkTxFailedAsync(transactionPath, "manifest missing for rollback").ConfigureAwait(false);
                Kodo.HotfixShared.HotfixShared.RecordFailure(expectedDir, tx.BaseVersion, tx.HotfixLevel);
                Log("Hotfix marked failed");
                RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);
                return 32;
            }
            manifestJson = await File.ReadAllTextAsync(tx.ManifestPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Rollback failed: manifest unreadable: {ex.Message}");
            await MarkTxFailedAsync(transactionPath, "manifest unreadable for rollback").ConfigureAwait(false);
            Kodo.HotfixShared.HotfixShared.RecordFailure(expectedDir, tx.BaseVersion, tx.HotfixLevel);
            Log("Hotfix marked failed");
            RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);
            return 32;
        }

        // Restore target: live last-known-good when it belongs to this base
        // line, otherwise the snapshot recorded at stage time.
        var stateFilePath = Path.Combine(expectedDir, "hotfix-state.json");
        var (liveBase, _, liveLastKnownGood) = ReadLiveHotfixState(stateFilePath);
        var restoreLevel = Kodo.HotfixShared.HotfixShared.AreSameBaseVersion(liveBase, tx.BaseVersion) && liveLastKnownGood >= 0
            ? liveLastKnownGood
            : tx.PreviousHotfixLevel;

        var rollback = Kodo.HotfixShared.HotfixShared.Rollback(new Kodo.HotfixShared.HotfixRollbackRequest
        {
            ManifestJson = manifestJson,
            BackupDir = tx.BackupDir,
            TargetDir = tx.TargetDir,
            StateFilePath = stateFilePath,
            BaseVersion = tx.BaseVersion,
            RestoreHotfixLevel = Math.Max(0, restoreLevel),
            RestoreLastKnownGood = Math.Max(0, restoreLevel),
        }, Log);

        if (!rollback.Success)
        {
            Log($"Rollback failed: {rollback.Error} – transaction retained for diagnostics.");
            await MarkTxFailedAsync(transactionPath, rollback.Error ?? "rollback failed").ConfigureAwait(false);
            Kodo.HotfixShared.HotfixShared.RecordFailure(expectedDir, tx.BaseVersion, tx.HotfixLevel);
            Log("Hotfix marked failed");
            RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);
            return 32;
        }

        foreach (var f in rollback.RestoredFiles)
            Log($"File restored: {f}");
        foreach (var f in rollback.RemovedFiles)
            Log($"Added file removed: {f}");
        Log("Hotfix state restored");

        try
        {
            var rolledBack = Kodo.HotfixShared.HotfixShared.MarkRolledBack(
                await File.ReadAllTextAsync(transactionPath).ConfigureAwait(false), DateTime.UtcNow);
            Kodo.HotfixShared.HotfixShared.WriteTextAtomically(transactionPath, rolledBack);
        }
        catch (Exception ex)
        {
            Log($"Failed to mark transaction rolledBack: {ex.Message}");
        }

        var failures = Kodo.HotfixShared.HotfixShared.RecordFailure(expectedDir, tx.BaseVersion, tx.HotfixLevel);
        if (failures >= Kodo.HotfixShared.HotfixFailurePolicy.MaxAttempts)
            Log($"Hotfix marked failed: HF{tx.HotfixLevel} failed {failures} times and will no longer be offered.");
        Log("Rollback completed");

        // Exit after restarting the known-good install; do NOT supervise again
        // (that would risk an update loop — see failure tracker).
        RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);
        Log("Kodo restarted");
        return rollbackExitCode;
    }

    private static async Task MarkTxFailedAsync(string transactionPath, string reason)
    {
        try
        {
            var failed = Kodo.HotfixShared.HotfixShared.MarkFailed(
                await File.ReadAllTextAsync(transactionPath).ConfigureAwait(false), reason);
            Kodo.HotfixShared.HotfixShared.WriteTextAtomically(transactionPath, failed);
        }
        catch (Exception ex)
        {
            Log($"Failed to mark transaction failed: {ex.Message}");
        }
    }

    // --- Phase 4 manual recovery: KodoUpdater --rollback <transaction-id-or-path> ---

    private static async Task<int> RunManualRollbackAsync(string idOrPath)
    {
        var resolved = ResolveRollbackTransaction(idOrPath);
        if (resolved is null)
        {
            Log($"Rollback target not found: {idOrPath}. Expected a transaction.json path or a transaction id under <LocalAppData>/Kodo/update/hotfix.");
            return 33;
        }

        var mutexName = OperatingSystem.IsWindows()
            ? $"Global\\Kodo-Updater-rollback-{SanitizeForMutex(Path.GetFileNameWithoutExtension(resolved) + Path.GetFileName(Path.GetDirectoryName(resolved) ?? ""))}"
            : $"Kodo-Updater-rollback-{SanitizeForMutex(Path.GetFileNameWithoutExtension(resolved) + Path.GetFileName(Path.GetDirectoryName(resolved) ?? ""))}";
        using var guard = UpdaterMutex.TryAcquire(mutexName);
        if (guard is null)
        {
            Log($"Another updater operation is running for {resolved}. Exiting.");
            return 3;
        }

        try
        {
            return await RunManualRollbackCoreAsync(resolved).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
            return 1;
        }
        finally
        {
            Log("KodoUpdater exit");
        }
    }

    private static string? ResolveRollbackTransaction(string idOrPath)
    {
        try
        {
            if (File.Exists(idOrPath)) return Path.GetFullPath(idOrPath);
            var hotfixRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update", "hotfix");
            if (!Directory.Exists(hotfixRoot)) return null;
            foreach (var txPath in Directory.GetFiles(hotfixRoot, "transaction.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (Kodo.HotfixShared.HotfixShared.TryParseTransaction(
                        File.ReadAllText(txPath), out var tx, out _) && tx is not null &&
                        string.Equals(tx.TransactionId, idOrPath.Trim(), StringComparison.OrdinalIgnoreCase))
                        return Path.GetFullPath(txPath);
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    private static async Task<int> RunManualRollbackCoreAsync(string transactionPath)
    {
        string rawJson;
        try { rawJson = await File.ReadAllTextAsync(transactionPath).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log($"Rollback target unreadable: {ex.Message}");
            return 33;
        }
        if (!Kodo.HotfixShared.HotfixShared.TryParseTransaction(rawJson, out var tx, out var parseError) || tx is null)
        {
            Log($"Malformed hotfix transaction: {parseError}");
            return 33;
        }

        var expectedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
        string? txPathError = null;
        if (string.IsNullOrWhiteSpace(_trustedInstallRoot) ||
            !Kodo.HotfixShared.HotfixShared.ValidateTransactionPaths(tx, transactionPath, expectedDir, _trustedInstallRoot!, out txPathError))
        {
            Log($"Rollback transaction path validation failed: {txPathError ?? "install root is unknown"}");
            return 33;
        }
        try
        {
            if (!IsInsideDirectory(Path.GetFullPath(transactionPath), Path.GetFullPath(expectedDir)))
            {
                Log($"Rollback target outside expected dir: {transactionPath}");
                return 33;
            }
        }
        catch (Exception ex)
        {
            Log($"Rollback path validation failed: {ex.Message}");
            return 33;
        }

        using var installGuard = InstallUpdateGuard.TryAcquire(tx.TargetDir);
        if (installGuard is null)
        {
            Log($"Another updater operation is already modifying installation '{tx.TargetDir}'. Exiting.");
            return 3;
        }

        var status = tx.Status ?? "";
        var rollbackable =
            string.Equals(status, Kodo.HotfixShared.HotfixTransactionStatus.Applied, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, Kodo.HotfixShared.HotfixTransactionStatus.AwaitingConfirmation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, Kodo.HotfixShared.HotfixTransactionStatus.Failed, StringComparison.OrdinalIgnoreCase);
        if (!rollbackable)
        {
            Log($"Transaction {tx.TransactionId} is '{status}': only applied/awaitingConfirmation/failed hotfixes can be rolled back.");
            return 33;
        }

        Log($"Manual rollback of hotfix transaction {tx.TransactionId} base={tx.BaseVersion} HF{tx.HotfixLevel}");
        await WaitForKodoExitAsync(tx.KodoPid, tx.KodoExePath).ConfigureAwait(false);
        if (IsKodoRunning(tx.KodoExePath))
        {
            Log("Kodo is still running; close it before manual rollback and retry.");
            return 33;
        }
        var code = await RollbackAndRestartAsync(tx, transactionPath, expectedDir, "manual rollback", 0).ConfigureAwait(false);
        return code == 0 ? 0 : code;
    }

    private static bool IsKodoRunning(string kodoExePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(kodoExePath)) return false;
            var fullExe = Path.GetFullPath(kodoExePath);
            var self = Environment.ProcessId;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == self) continue;
                    var procPath = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(procPath)) continue;
                    if (string.Equals(Path.GetFullPath(procPath), fullExe, PathComparison))
                        return true;
                }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    private static (string BaseVersion, int Level, int LastKnownGood) ReadLiveHotfixState(string stateFilePath)
    {
        try
        {
            if (!File.Exists(stateFilePath)) return ("", -1, -1);
            var json = File.ReadAllText(stateFilePath);
            using var doc = JsonDocument.Parse(json);
            var level = -1;
            var lkg = -1;
            var baseVersion = "";
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "hotfixLevel", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var v))
                    level = v;
                if (string.Equals(prop.Name, "lastKnownGoodHotfix", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var g))
                    lkg = g;
                if (string.Equals(prop.Name, "baseVersion", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    baseVersion = prop.Value.GetString() ?? "";
            }
            return (baseVersion, level, lkg);
        }
        catch { return ("", -1, -1); }
    }

    private static void CleanupHotfixPartials(string updateDir)
    {
        try
        {
            var downloads = Path.Combine(updateDir, "hotfix", "downloads");
            if (!Directory.Exists(downloads)) return;
            foreach (var f in Directory.GetFiles(downloads, "*.partial", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch (Exception ex) { Log($"Hotfix partial cleanup failed {f}: {ex.Message}"); }
            }
        }
        catch { }
    }

    private static string LinuxManualBlurb(string stagedPath) =>
        stagedPath.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
            ? "Install with e.g. sudo dpkg -i <file> (or software center), then restart Kodo."
            : stagedPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
                ? "Make executable (chmod +x), move where you keep apps, then restart Kodo from it."
                : "Extract the archive over the install folder (keep Kodo binary executable with chmod +x), then restart Kodo.";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log($"Cleanup delete failed {path}: {ex.Message}"); }
    }

    private static void TryDeleteIfInStaging(string installerPath)
    {
        try
        {
            var stagingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update", "staging");
            var fullInst = Path.GetFullPath(installerPath);
            var fullStaging = Path.GetFullPath(stagingRoot);
            if (IsInsideDirectory(fullInst, fullStaging))
                TryDelete(fullInst);
        }
        catch { }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
            return false;
        var sep = Path.DirectorySeparatorChar.ToString();
        return string.Equals(path, directory, PathComparison)
            || path.StartsWith(directory + sep, PathComparison);
    }

    private static bool VerifyFileSha256(string path, string expectedHex)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                hasher.AppendData(buffer, 0, read);
            var actual = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
            return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Log($"Checksum validation failed: {ex.Message}");
            return false;
        }
    }

    private static string SanitizeForMutex(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "default";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {message}";
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch { }
        try { Console.WriteLine(line); } catch { }
        try { Debug.WriteLine(line); } catch { }
    }

    // Single-instance guard that is safe to dispose after awaits.
    //
    // Named mutexes are thread-affine: ReleaseMutex must run on the thread that
    // acquired ownership, and Main awaits with ConfigureAwait(false), so the
    // finally routinely runs on a different threadpool thread. Releasing there
    // throws ApplicationException ("unsynchronized block of code"), which in the
    // trimmed single-file build surfaced as a fatal crash instead of the
    // intended exit code. This guard only releases on the acquiring thread and
    // otherwise just disposes; process exit drops the rest. The guard only needs
    // to live as long as this one-shot process.
    private sealed class UpdaterMutex : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly int _owningThreadId;
        private bool _disposed;

        private UpdaterMutex(Mutex mutex)
        {
            _mutex = mutex;
            _owningThreadId = Environment.CurrentManagedThreadId;
        }

        public static UpdaterMutex? TryAcquire(string name)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name, out _);
            }
            catch
            {
                return null;
            }

            bool owns;
            try
            {
                owns = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died without releasing; we now own it.
                owns = true;
            }
            catch
            {
                owns = false;
            }

            if (!owns)
            {
                try { mutex.Dispose(); } catch { }
                return null;
            }
            return new UpdaterMutex(mutex);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Environment.CurrentManagedThreadId == _owningThreadId)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            try { _mutex.Dispose(); } catch { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(UpdateTransaction))]
internal sealed partial class UpdateTransactionJsonContext : JsonSerializerContext
{
}

internal sealed record UpdateTransaction(
    string TransactionId,
    string InstallerPath,
    string KodoExePath,
    int KodoPid,
    bool RestartAfterUpdate,
    DateTime CreatedAtUtc,
    string Version,
    string? Sha256 = null,
    long ExpectedSize = 0,
    string? AssetName = null
);
