// Licensed under GPL-v3.0

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KodoUpdater;

internal static class Program
{
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
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
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
            try { mutex.ReleaseMutex(); } catch { }
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
            tx = JsonSerializer.Deserialize<UpdateTransaction>(rawJson, JsonOptions);
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
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
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

        // Terminal states never re-apply; unconfirmed states restart Kodo so it
        // can confirm on boot. Transaction + backup are always retained.
        if (!string.Equals(tx.Status, Kodo.HotfixShared.HotfixTransactionStatus.Staged, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(tx.Status, Kodo.HotfixShared.HotfixTransactionStatus.AwaitingConfirmation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tx.Status, Kodo.HotfixShared.HotfixTransactionStatus.Applied, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Hotfix transaction {tx.TransactionId} is {tx.Status} – restarting Kodo for startup confirmation.");
                RestartKodo(tx.KodoExePath, tx.RestartAfterUpdate);
                return 0;
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
        var (liveBase, liveLevel, _) = ReadLiveHotfixState(stateFilePath);
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
            ExpectedPlatformRid = tx.PlatformRid,
        }, Log);

        if (!result.Success)
        {
            Log($"Hotfix application failed: {result.Error} – installation untouched, transaction retained for diagnostics.");
            return 26;
        }

        foreach (var f in result.ReplacedFiles)
            Log($"File replaced: {f}");

        Log("Hotfix application completed");

        // Phase 4: files copied is NOT success yet. Record last-known-good,
        // require startup confirmation, and supervise the restarted Kodo.
        var (_, _, liveLastKnownGood) = ReadLiveHotfixState(stateFilePath);
        var awaitingJson = Kodo.HotfixShared.HotfixShared.MarkAwaitingConfirmation(
            rawJson, DateTime.UtcNow.AddSeconds(ConfirmationTimeoutSeconds), liveLastKnownGood);
        try
        {
            var tmp = transactionPath + ".tmp";
            await File.WriteAllTextAsync(tmp, awaitingJson).ConfigureAwait(false);
            File.Move(tmp, transactionPath, overwrite: true);
            Log("Hotfix transaction marked awaitingConfirmation (retained with backup for rollback).");
        }
        catch (Exception ex)
        {
            Log($"Failed to mark hotfix transaction awaitingConfirmation: {ex.Message}");
            return 26;
        }

        CleanupHotfixPartials(expectedDir);

        if (!tx.RestartAfterUpdate)
        {
            Log("Restart not requested; confirmation will happen on next Kodo start.");
            return 0;
        }

        var kodoTarget = Kodo.HotfixShared.HotfixShared.ResolveRestartTarget(tx.KodoExePath);
        Process? kodoProcess = null;
        if (!string.IsNullOrWhiteSpace(kodoTarget) && File.Exists(kodoTarget))
        {
            Log($"Starting Kodo for confirmation: {kodoTarget}");
            try
            {
                kodoProcess = Process.Start(new ProcessStartInfo { FileName = kodoTarget, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"Failed to start Kodo for confirmation: {ex}");
            }
        }
        else
        {
            Log($"Kodo exe not found for confirmation restart: {tx.KodoExePath}");
        }
        Log("Kodo restarted");

        return await SuperviseHotfixConfirmationAsync(tx, transactionPath, expectedDir, kodoProcess).ConfigureAwait(false);
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
            var tmp = transactionPath + ".tmp";
            await File.WriteAllTextAsync(tmp, rolledBack).ConfigureAwait(false);
            File.Move(tmp, transactionPath, overwrite: true);
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
            var tmp = transactionPath + ".tmp";
            await File.WriteAllTextAsync(tmp, failed).ConfigureAwait(false);
            File.Move(tmp, transactionPath, overwrite: true);
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
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
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
            try { mutex.ReleaseMutex(); } catch { }
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

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false, TypeInfoResolver = UpdateTransactionJsonContext.Default };
}

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
