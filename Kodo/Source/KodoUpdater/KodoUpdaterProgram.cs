// Licensed under GPL-v3.0
// One-shot update orchestrator. Lifecycle: Kodo writes a transaction file, launches this exe with the
// transaction path + Kodo PID, then exits. This process waits for that exact PID, runs the staged
// Inno installer, restarts Kodo if requested, cleans up, and exits. No polling, no resident loop,
// no Task Scheduler, no named pipes.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KodoUpdater;

internal static class Program
{
    private const int PidWaitTimeoutSeconds = 60;
    private const int InstallerTimeoutMinutes = 15;
    private const int StaleTransactionHours = 24;

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kodo", "update", "updater.log");

    private static async Task<int> Main(string[] args)
    {
        Log($"KodoUpdater start args=[{string.Join(" ", args)}] pid={Environment.ProcessId}");

        // Self-relocation: if running from {app} (Program Files\Kodo), copy to temp
        // so Inno can overwrite {app}\KodoUpdater.exe during install while we wait.
        try
        {
            var selfPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(selfPath))
            {
                var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var selfDir = Path.GetDirectoryName(Path.GetFullPath(selfPath))?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
                // If self is inside appDir (or Program Files\Kodo) and not already in temp
                var isInApp = selfDir.Equals(appDir, StringComparison.OrdinalIgnoreCase)
                    || selfDir.EndsWith("Kodo", StringComparison.OrdinalIgnoreCase);
                var isInTemp = selfPath.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                    || selfPath.Contains(Path.Combine("LocalAppData", "Kodo", "update"), StringComparison.OrdinalIgnoreCase);
                if (isInApp && !isInTemp && args.Length > 0)
                {
                    var tempCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update", "KodoUpdater-temp.exe");
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(tempCopy)!);
                        // Only copy if not already running from temp or if newer
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
            Log("No transaction path supplied. Usage: KodoUpdater.exe <transaction.json>");
            return 2;
        }

        // Support both quoted and unquoted path (Kodo may pass via ArgumentList)
        var transactionPath = args[0].Trim().Trim('"');
        // If Kodo passed extra args (legacy), join them
        if (args.Length > 1)
            transactionPath = string.Join(" ", args).Trim().Trim('"');

        // Duplicate-instance guard per transaction
        var mutexName = $"Global\\Kodo-Updater-{SanitizeForMutex(Path.GetFileNameWithoutExtension(transactionPath))}";
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
        // 1. Validate transaction
        if (!File.Exists(transactionPath))
        {
            Log($"Transaction not found: {transactionPath}");
            return 4;
        }

        UpdateTransaction? tx;
        try
        {
            var json = await File.ReadAllTextAsync(transactionPath).ConfigureAwait(false);
            tx = JsonSerializer.Deserialize<UpdateTransaction>(json, JsonOptions);
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

        // Stale check
        if (tx.CreatedAtUtc < DateTime.UtcNow.AddHours(-StaleTransactionHours))
        {
            Log($"Stale transaction {tx.TransactionId} created {tx.CreatedAtUtc:o} – discarding.");
            TryDelete(transactionPath);
            return 6;
        }

        // Verify transaction file is inside expected update dir (anti-hijack)
        var expectedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kodo", "update");
        try
        {
            var fullTx = Path.GetFullPath(transactionPath);
            var fullExpected = Path.GetFullPath(expectedDir);
            if (!fullTx.StartsWith(fullExpected, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Transaction outside expected dir: {fullTx} !startsWith {fullExpected}");
                return 7;
            }
        }
        catch (Exception ex)
        {
            Log($"Path validation failed: {ex.Message}");
            return 7;
        }

        Log($"Transaction {tx.TransactionId} pid={tx.KodoPid} installer={tx.InstallerPath} kodo={tx.KodoExePath} restart={tx.RestartAfterUpdate}");

        // 2. Wait for exact Kodo PID to exit
        if (tx.KodoPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(tx.KodoPid);
                // Extra guard: PID reuse – if exe path mismatches, original Kodo is gone so skip wait
                bool pidReused = false;
                try
                {
                    var procPath = proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(procPath) && !string.IsNullOrWhiteSpace(tx.KodoExePath))
                    {
                        var normProc = Path.GetFullPath(procPath).TrimEnd(Path.DirectorySeparatorChar);
                        var normTx = Path.GetFullPath(tx.KodoExePath).TrimEnd(Path.DirectorySeparatorChar);
                        if (!string.Equals(normProc, normTx, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"PID {tx.KodoPid} path mismatch: proc={normProc} tx={normTx} – PID reused, treating Kodo as already exited");
                            pidReused = true;
                        }
                    }
                }
                catch { }

                if (pidReused)
                {
                    Log($"Kodo PID {tx.KodoPid} considered already exited (PID reuse)");
                }
                else if (!proc.HasExited)
                {
                    Log($"Waiting for Kodo PID {tx.KodoPid} to exit (timeout {PidWaitTimeoutSeconds}s)...");
                    // WaitForExit with timeout, polling HasExited to handle PID reuse races
                    var sw = Stopwatch.StartNew();
                    while (!proc.HasExited && sw.Elapsed.TotalSeconds < PidWaitTimeoutSeconds)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                    }

                    if (!proc.HasExited)
                    {
                        Log($"Kodo PID {tx.KodoPid} did not exit within {PidWaitTimeoutSeconds}s – proceeding anyway (Inno /CLOSEAPPLICATIONS will handle)");
                    }
                    else
                    {
                        Log($"Kodo PID {tx.KodoPid} exited after {sw.Elapsed.TotalSeconds:0.0}s");
                    }
                }
                else
                {
                    Log($"Kodo PID {tx.KodoPid} already exited");
                }
            }
            catch (ArgumentException)
            {
                Log($"Kodo PID {tx.KodoPid} not found – already exited");
            }
            catch (Exception ex)
            {
                Log($"Wait for PID {tx.KodoPid} failed: {ex.Message} – continuing");
            }
        }
        else
        {
            Log("No Kodo PID supplied – not waiting");
        }

        // Small settle delay – let OS release file locks, no arbitrary Thread.Sleep elsewhere
        await Task.Delay(800).ConfigureAwait(false);

        // 3. Verify staged installer
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

        // Optional: if transaction carries Sha256, validate (future pipeline may provide it)
        // For now, existence + size is the gate; full hash check would require release pipeline change.

        // 4. Launch Inno installer
        Log($"Launching installer: {tx.InstallerPath}");
        var psi = new ProcessStartInfo
        {
            FileName = tx.InstallerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true, // allow UAC prompt
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

        // 5. Determine success – Inno returns 0 on success
        if (installerProc.ExitCode != 0)
        {
            Log($"Installer failed with exit code {installerProc.ExitCode} – not restarting Kodo, keeping transaction for diagnostics");
            // Keep transaction + installer for manual retry; do not delete
            return installerProc.ExitCode;
        }

        // 6. Cleanup transaction
        TryDelete(transactionPath);
        // Optionally delete installer staging file if inside our staging dir and update succeeded
        TryDeleteIfInStaging(tx.InstallerPath);

        // 7. Restart Kodo if requested
        if (tx.RestartAfterUpdate)
        {
            if (!File.Exists(tx.KodoExePath))
            {
                // Fallback to default install location
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kodo", "Kodo.exe");
                if (File.Exists(fallback)) tx = tx with { KodoExePath = fallback };
            }

            if (File.Exists(tx.KodoExePath))
            {
                Log($"Restarting Kodo: {tx.KodoExePath}");
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = tx.KodoExePath, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log($"Failed to restart Kodo: {ex}");
                }
            }
            else
            {
                Log($"Kodo exe not found for restart: {tx.KodoExePath}");
            }
        }
        else
        {
            Log("Restart not requested");
        }

        Log("Update orchestration complete");
        return 0;
    }

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
            if (fullInst.StartsWith(fullStaging, StringComparison.OrdinalIgnoreCase))
                TryDelete(fullInst);
        }
        catch { }
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

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
}

internal sealed record UpdateTransaction(
    string TransactionId,
    string InstallerPath,
    string KodoExePath,
    int KodoPid,
    bool RestartAfterUpdate,
    DateTime CreatedAtUtc,
    string Version
);
