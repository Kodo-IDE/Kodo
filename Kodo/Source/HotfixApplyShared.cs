// Licensed under GPL v3.0
//
// Shared hotfix-apply core, compiled into BOTH the Kodo app and KodoUpdater.
//
// Constraints for this file (it ships in the trimmed, single-file updater):
//   - BCL only. No Kodo.*, Avalonia, or other project references.
//   - No reflection-based JSON (System.Text.Json source-gen is per-assembly).
//     All JSON I/O here uses JsonDocument / Utf8JsonWriter (trim-safe).
//   - No environment assumptions: every path is passed in by the caller.
//
// Kodo-side staging (HotfixStaging) uses the Phase 1/2 models + validator for
// rich pre-stage verification. The updater re-verifies with this file's
// self-contained checks before touching the installation, then calls Apply.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Kodo.HotfixShared;

internal static class HotfixTransactionStatus
{
    public const string Staged = "staged";
    public const string Applied = "applied";
    public const string AwaitingConfirmation = "awaitingConfirmation";
    public const string Confirmed = "confirmed";
    public const string RolledBack = "rolledBack";
    public const string Failed = "failed";

    public static bool IsPending(string? status) =>
        string.Equals(status, Staged, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Applied, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, AwaitingConfirmation, StringComparison.OrdinalIgnoreCase);
}

internal static class HotfixFailurePolicy
{
    public const int MaxAttempts = 3;
    public const string FailuresFileName = "hotfix-failures.json";
}

internal sealed class HotfixTransaction
{
    public string Kind { get; set; } = "hotfix";
    public string TransactionId { get; set; } = "";
    public string Status { get; set; } = HotfixTransactionStatus.Staged;
    public string PackagePath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string PayloadDir { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string KodoExePath { get; set; } = "";
    public int KodoPid { get; set; }
    public bool RestartAfterUpdate { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAtUtc { get; set; }
    public DateTime? ConfirmationDeadlineUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? RolledBackAtUtc { get; set; }
    public string FailureReason { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public int HotfixLevel { get; set; }
    public int PreviousHotfixLevel { get; set; }
    public int PreviousLastKnownGood { get; set; }
    public int LastKnownGoodHotfix { get; set; }
    public string PlatformRid { get; set; } = "";
}

internal sealed class HotfixPackageFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";

    // Optional. When true, Apply/Rollback ensure the file is executable on
    // Unix after writing it (Linux hotfix payloads replacing native binaries
    // such as Kodo/KodoUpdater). Absent/false preserves historical behavior.
    // Ignored on Windows. Old packages omit it; old code ignores it.
    public bool Executable { get; set; }
}

internal sealed class HotfixPackageManifest
{
    public int SchemaVersion { get; set; }
    public string Product { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public int Hotfix { get; set; }
    public int MinimumHotfix { get; set; }
    public string Platform { get; set; } = "";
    public List<HotfixPackageFile> Files { get; set; } = new();
}

internal sealed class HotfixApplyRequest
{
    public string ManifestJson { get; set; } = "";
    public string PayloadDir { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string StateFilePath { get; set; } = "";
    public string ExpectedBaseVersion { get; set; } = "";
    public int InstalledHotfixLevel { get; set; }
    public int PreviousLastKnownGood { get; set; }
    public int ExpectedHotfixLevel { get; set; }
    public string ExpectedPlatformRid { get; set; } = "";
    public Action? BeforeReplace { get; set; }
}

internal sealed class HotfixApplyResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<string> ReplacedFiles { get; } = new();
    public List<string> BackedUpFiles { get; } = new();
    public List<string> CreatedFiles { get; } = new();

    public static HotfixApplyResult Fail(string error) => new() { Success = false, Error = error };
}

internal sealed class HotfixRollbackRequest
{
    public string ManifestJson { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string StateFilePath { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public int RestoreHotfixLevel { get; set; }
    public int RestoreLastKnownGood { get; set; }
}

internal sealed class HotfixRollbackResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<string> RestoredFiles { get; } = new();
    public List<string> RemovedFiles { get; } = new();

    public static HotfixRollbackResult Fail(string error) => new() { Success = false, Error = error };
}

internal sealed class TarballInstallRequest
{
    public string TarballPath { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string WorkRoot { get; set; } = "";
    public string KodoExeName { get; set; } = "Kodo";
    public string KodoUpdaterName { get; set; } = "KodoUpdater";
}

internal sealed class TarballInstallResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    // Set ONLY when the swap failed AND the restore failed: the previous
    // install retained here for manual recovery. Null in every other
    // outcome (success cleans up; restored failures need nothing kept).
    public string? RetainedBackupDir { get; set; }

    public static TarballInstallResult Fail(string error, string? retainedBackupDir = null) =>
        new() { Success = false, Error = error, RetainedBackupDir = retainedBackupDir };
}

internal static class HotfixShared
{
    public static readonly string[] KnownPlatforms = { "win-x64", "linux-x64", "linux-arm64" };

    public static void WriteTextAtomically(string path, string contents) =>
        WriteBytesAtomically(path, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    public static void WriteBytesAtomically(string path, byte[] contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Atomic file path has no directory.");
        Directory.CreateDirectory(directory);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.WriteThrough))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static bool ValidateTransactionPaths(HotfixTransaction tx, string transactionPath, string updateRoot, string installRoot, out string? error)
    {
        error = null;
        try
        {
            var update = Path.GetFullPath(updateRoot);
            var install = Path.GetFullPath(installRoot);
            var hotfix = Path.Combine(update, "hotfix");
            var stage = Path.GetFullPath(Path.GetDirectoryName(transactionPath) ?? "");
            var expectedTx = Path.Combine(stage, "transaction.json");
            var stageParent = Directory.GetParent(stage)?.FullName ?? "";
            bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            if (!Same(transactionPath, expectedTx) || !Same(stageParent, hotfix) ||
                !string.Equals(tx.TransactionId, Path.GetFileName(stage), StringComparison.OrdinalIgnoreCase) ||
                !IsPathWithinDirectoryWithoutReparsePoints(stage, hotfix) ||
                !IsPathWithinDirectoryWithoutReparsePoints(transactionPath, stage))
                error = "Transaction must be directly staged under the hotfix update directory.";
            else if (!Same(tx.ManifestPath, Path.Combine(stage, "manifest.json")) ||
                     !Same(tx.PackagePath, Path.Combine(stage, "package.zip")) ||
                     !Same(tx.PayloadDir, Path.Combine(stage, "payload")) ||
                     !Same(tx.BackupDir, Path.Combine(stage, "backup")))
                error = "Transaction staging paths do not match the transaction directory.";
            else if (!IsPathWithinDirectoryWithoutReparsePoints(tx.ManifestPath, stage) ||
                     !IsPathWithinDirectoryWithoutReparsePoints(tx.PackagePath, stage) ||
                     !IsPathWithinDirectoryWithoutReparsePoints(tx.PayloadDir, stage) ||
                     !IsPathWithinDirectoryWithoutReparsePoints(tx.BackupDir, stage))
                error = "Transaction staging paths cannot contain symbolic links or reparse points.";
            else if (!Same(tx.TargetDir, install))
                error = "Transaction target does not match the running Kodo installation.";
            else
            {
                var exeName = Path.GetFileName(tx.KodoExePath);
                var validName = OperatingSystem.IsWindows()
                    ? string.Equals(exeName, "Kodo.exe", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(exeName, "Kodo", StringComparison.Ordinal) || string.Equals(exeName, "kodo", StringComparison.Ordinal);
                if (!validName || !Same(tx.KodoExePath, Path.Combine(install, exeName)))
                    error = "Transaction restart executable is outside the Kodo installation.";
            }
        }
        catch (Exception ex) { error = $"Transaction paths are invalid: {ex.GetType().Name}."; }
        return error is null;
    }

    public static bool ValidateManifest(string json, string expectedBase, int installedLevel, string expectedPlatform, out string? error)
    {
        if (!TryParseManifest(json, out var manifest, out error) || manifest is null) return false;
        error = ValidateManifestForApply(manifest, expectedBase, installedLevel, expectedPlatform);
        return error is null;
    }

    // --- Manifest parsing (JsonDocument: trim-safe) ---

    public static bool TryParseManifest(string json, out HotfixPackageManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Manifest is empty.";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Manifest root must be an object.";
                return false;
            }
            var m = new HotfixPackageManifest
            {
                SchemaVersion = GetInt(doc.RootElement, "schemaVersion"),
                Product = GetString(doc.RootElement, "product"),
                BaseVersion = GetString(doc.RootElement, "baseVersion"),
                Hotfix = GetInt(doc.RootElement, "hotfix"),
                MinimumHotfix = GetInt(doc.RootElement, "minimumHotfix"),
                Platform = GetString(doc.RootElement, "platform"),
            };
            if (doc.RootElement.TryGetProperty("files", out var files) &&
                !string.Equals("files", "FILES", StringComparison.Ordinal))
            {
                if (files.ValueKind != JsonValueKind.Array)
                {
                    error = "Manifest 'files' must be an array.";
                    return false;
                }
                foreach (var f in files.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object)
                    {
                        error = "Manifest file entry must be an object.";
                        return false;
                    }
                    m.Files.Add(new HotfixPackageFile
                    {
                        Path = GetString(f, "path"),
                        Sha256 = GetString(f, "sha256"),
                        Executable = GetBool(f, "executable", false),
                    });
                }
            }
            manifest = m;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Manifest is malformed: {ex.GetType().Name}.";
            return false;
        }
    }

    private static string GetString(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString() ?? "";
        }
        return "";
    }

    private static int GetInt(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.Number &&
                prop.Value.TryGetInt32(out var v))
                return v;
        }
        return 0;
    }

    // --- Transaction parsing / status flip (JsonDocument round-trip: trim-safe) ---

    public static bool TryParseTransaction(string json, out HotfixTransaction? tx, out string? error)
    {
        tx = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Transaction is empty.";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Transaction root must be an object.";
                return false;
            }
            if (!string.Equals(GetString(root, "kind"), "hotfix", StringComparison.OrdinalIgnoreCase))
            {
                error = "Not a hotfix transaction.";
                return false;
            }
            tx = new HotfixTransaction
            {
                Kind = "hotfix",
                TransactionId = GetString(root, "transactionId"),
                Status = GetString(root, "status"),
                PackagePath = GetString(root, "packagePath"),
                ManifestPath = GetString(root, "manifestPath"),
                PayloadDir = GetString(root, "payloadDir"),
                BackupDir = GetString(root, "backupDir"),
                TargetDir = GetString(root, "targetDir"),
                KodoExePath = GetString(root, "kodoExePath"),
                KodoPid = GetInt(root, "kodoPid"),
                RestartAfterUpdate = GetBool(root, "restartAfterUpdate", true),
                CreatedAtUtc = GetDateTime(root, "createdAtUtc"),
                BaseVersion = GetString(root, "baseVersion"),
                HotfixLevel = GetInt(root, "hotfix"),
                PreviousHotfixLevel = GetInt(root, "previousHotfixLevel"),
                PreviousLastKnownGood = GetInt(root, "previousLastKnownGood"),
                LastKnownGoodHotfix = GetInt(root, "lastKnownGoodHotfix"),
                ConfirmationDeadlineUtc = GetNullableDateTime(root, "confirmationDeadlineUtc"),
                FailureReason = GetString(root, "failureReason"),
                PlatformRid = GetString(root, "platformRid"),
            };
            if (tx.HotfixLevel == 0) tx.HotfixLevel = GetInt(root, "hotfixLevel");
            if (string.IsNullOrWhiteSpace(tx.TransactionId)
                || string.IsNullOrWhiteSpace(tx.PayloadDir)
                || string.IsNullOrWhiteSpace(tx.TargetDir))
            {
                error = "Transaction is missing required fields.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Transaction is malformed: {ex.GetType().Name}.";
            return false;
        }
    }

    private static bool GetBool(JsonElement el, string name, bool fallback)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.True) return true;
                if (prop.Value.ValueKind == JsonValueKind.False) return false;
            }
        }
        return fallback;
    }

    private static DateTime GetDateTime(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(prop.Value.GetString(), out var dt))
                return dt.ToUniversalTime();
        }
        return DateTime.MinValue;
    }

    private static DateTime? GetNullableDateTime(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(prop.Value.GetString(), out var dt))
                return dt.ToUniversalTime();
        }
        return null;
    }

    public static bool IsHotfixTransactionJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                string.Equals(GetString(doc.RootElement, "kind"), "hotfix", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string MarkApplied(string transactionJson, DateTime appliedAtUtc) =>
        MarkStatus(transactionJson, HotfixTransactionStatus.Applied, "appliedAtUtc", appliedAtUtc);

    public static string MarkAwaitingConfirmation(string transactionJson, DateTime deadlineUtc, int lastKnownGood) =>
        MarkStatus(transactionJson, HotfixTransactionStatus.AwaitingConfirmation, "confirmationDeadlineUtc", deadlineUtc,
            ("lastKnownGoodHotfix", lastKnownGood.ToString(System.Globalization.CultureInfo.InvariantCulture), false));

    public static string MarkConfirmed(string transactionJson, DateTime confirmedAtUtc) =>
        MarkStatus(transactionJson, HotfixTransactionStatus.Confirmed, "confirmedAtUtc", confirmedAtUtc);

    public static string MarkRolledBack(string transactionJson, DateTime rolledBackAtUtc) =>
        MarkStatus(transactionJson, HotfixTransactionStatus.RolledBack, "rolledBackAtUtc", rolledBackAtUtc);

    public static string MarkFailed(string transactionJson, string reason) =>
        MarkStatus(transactionJson, HotfixTransactionStatus.Failed, null, null,
            ("failureReason", reason ?? "", true));

    private static string MarkStatus(
        string transactionJson,
        string status,
        string? timestampField,
        DateTime? timestamp,
        (string Name, string Value, bool IsString)? extra = null)
    {
        using var doc = JsonDocument.Parse(transactionJson);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            var wroteStatus = false;
            var wroteTimestamp = timestampField is null;
            var wroteExtra = extra is null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "status", StringComparison.OrdinalIgnoreCase))
                {
                    w.WriteString("status", status);
                    wroteStatus = true;
                }
                else if (timestampField is not null &&
                    string.Equals(prop.Name, timestampField, StringComparison.OrdinalIgnoreCase) &&
                    timestamp is not null)
                {
                    w.WriteString(timestampField, timestamp.Value.ToUniversalTime().ToString("o"));
                    wroteTimestamp = true;
                }
                else if (extra is not null &&
                    string.Equals(prop.Name, extra.Value.Name, StringComparison.OrdinalIgnoreCase))
                {
                    WriteExtra(w, extra.Value);
                    wroteExtra = true;
                }
                else
                {
                    prop.WriteTo(w);
                }
            }
            if (!wroteStatus) w.WriteString("status", status);
            if (!wroteTimestamp && timestampField is not null && timestamp is not null)
                w.WriteString(timestampField, timestamp.Value.ToUniversalTime().ToString("o"));
            if (!wroteExtra && extra is not null) WriteExtra(w, extra.Value);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteExtra(Utf8JsonWriter w, (string Name, string Value, bool IsString) extra)
    {
        if (extra.IsString) w.WriteString(extra.Name, extra.Value);
        else if (int.TryParse(extra.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)) w.WriteNumber(extra.Name, n);
        else w.WriteString(extra.Name, extra.Value);
    }

    // --- Apply ---

    public static HotfixApplyResult Apply(HotfixApplyRequest request, Action<string>? log = null)
    {
        if (request is null) return HotfixApplyResult.Fail("Apply request is missing.");
        void Log(string m) { try { log?.Invoke(m); } catch { } }

        Log("Hotfix application started");
        if (!TryParseManifest(request.ManifestJson, out var manifest, out var parseError) || manifest is null)
            return HotfixApplyResult.Fail(parseError ?? "Manifest is malformed.");

        var manifestError = ValidateManifestForApply(
            manifest, request.ExpectedBaseVersion, request.InstalledHotfixLevel, request.ExpectedPlatformRid);
        if (manifestError is not null) return HotfixApplyResult.Fail(manifestError);
        if (request.ExpectedHotfixLevel > 0 && manifest.Hotfix != request.ExpectedHotfixLevel)
            return HotfixApplyResult.Fail($"Manifest hotfix HF{manifest.Hotfix} does not match transaction HF{request.ExpectedHotfixLevel}.");

        if (string.IsNullOrWhiteSpace(request.PayloadDir) || !Directory.Exists(request.PayloadDir))
            return HotfixApplyResult.Fail($"Payload directory not found: {request.PayloadDir}.");
        if (string.IsNullOrWhiteSpace(request.TargetDir) || !Directory.Exists(request.TargetDir))
            return HotfixApplyResult.Fail($"Target directory not found: {request.TargetDir}.");

        var targetRoot = Path.GetFullPath(request.TargetDir);
        var payloadRoot = Path.GetFullPath(request.PayloadDir);

        // Phase 1: verify EVERYTHING before copying anything.
        var planned = new List<(HotfixPackageFile Entry, string PayloadPath, string DestPath)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (entry is null) return HotfixApplyResult.Fail("Manifest contains an empty file entry.");
            if (!IsSafeRelativePath(entry.Path))
                return HotfixApplyResult.Fail($"Unsafe path in manifest: '{entry.Path}'.");
            var normalized = entry.Path.Trim().Replace('\\', '/');
            if (!seen.Add(normalized))
                return HotfixApplyResult.Fail($"Duplicate path in manifest: '{entry.Path}'.");
            string dest;
            try
            {
                dest = Path.GetFullPath(Path.Combine(targetRoot, entry.Path.Trim()));
            }
            catch { return HotfixApplyResult.Fail($"Invalid path in manifest: '{entry.Path}'."); }
            if (!IsPathWithinDirectoryWithoutReparsePoints(dest, targetRoot))
                return HotfixApplyResult.Fail($"Path escapes installation directory: '{entry.Path}'.");
            if (!IsValidSha256(entry.Sha256))
                return HotfixApplyResult.Fail($"Invalid SHA-256 for '{entry.Path}'.");
            string payload;
            try
            {
                payload = Path.GetFullPath(Path.Combine(payloadRoot, entry.Path.Trim()));
            }
            catch { return HotfixApplyResult.Fail($"Invalid payload path: '{entry.Path}'."); }
            if (!IsPathWithinDirectoryWithoutReparsePoints(payload, payloadRoot) || !File.Exists(payload))
                return HotfixApplyResult.Fail($"Payload file missing: '{entry.Path}'.");
            if (!VerifyFileSha256(payload, entry.Sha256))
                return HotfixApplyResult.Fail($"Payload hash mismatch: '{entry.Path}'. Staged package discarded from apply; installation untouched.");
            planned.Add((entry, payload, dest));
        }
        if (planned.Count == 0) return HotfixApplyResult.Fail("Manifest lists no files.");

        // Phase 2: back up replaced files (retained until confirmed for rollback).
        // Idempotent: if a previous (interrupted) run already backed everything
        // up, reuse it so a retry never snapshots half-applied files as "good".
        var result = new HotfixApplyResult { Success = true };
        var backupRecords = new List<(string Rel, bool Existed)>();
        try
        {
            Directory.CreateDirectory(request.BackupDir);
            var backupRoot = Path.GetFullPath(request.BackupDir);
            if (TryReadBackupIndex(Path.Combine(backupRoot, "backup.json"), out var existing) &&
                BackupCoversAll(existing!, manifest.Files))
            {
                foreach (var (rel, existed) in existing!)
                {
                    if (existed) result.BackedUpFiles.Add(rel);
                    else result.CreatedFiles.Add(rel);
                }
                Log("Backup already present from a previous run; reusing it.");
            }
            else
            {
                foreach (var (entry, _, dest) in planned)
                {
                    var rel = entry.Path.Trim().Replace('\\', '/');
                    if (File.Exists(dest))
                    {
                        var backupPath = Path.GetFullPath(Path.Combine(backupRoot, rel));
                        if (!IsPathWithinDirectoryWithoutReparsePoints(backupPath, backupRoot))
                            return HotfixApplyResult.Fail($"Backup path escapes backup directory: '{rel}'.");
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Copy(dest, backupPath, overwrite: true);
                        result.BackedUpFiles.Add(rel);
                        backupRecords.Add((rel, true));
                        Log($"Backed up: {rel}");
                    }
                    else
                    {
                        // Recorded so rollback can remove it.
                        backupRecords.Add((rel, false));
                        result.CreatedFiles.Add(rel);
                        Log($"New file (no backup): {rel}");
                    }
                }
                WriteBackupIndex(Path.Combine(backupRoot, "backup.json"), backupRecords);
            }
        }
        catch (Exception ex)
        {
            return HotfixApplyResult.Fail($"Backup failed: {ex.GetType().Name}: {ex.Message}. Installation untouched.");
        }

        try { request.BeforeReplace?.Invoke(); }
        catch (Exception ex)
        {
            return HotfixApplyResult.Fail($"Recovery state could not be persisted before replacement: {ex.GetType().Name}: {ex.Message}. Installation untouched.");
        }

        // Phase 3: copy payload over the installation (never execute package files).
        try
        {
            foreach (var (entry, payload, dest) in planned)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(payload, dest, overwrite: true);
                if (entry.Executable && !EnsureExecutable(dest))
                    throw new InvalidOperationException($"Executable permission could not be set: '{entry.Path}'.");
                result.ReplacedFiles.Add(dest);
            }
            Log($"Files replaced: {result.ReplacedFiles.Count}");
        }
        catch (Exception ex)
        {
            var rollback = Rollback(new HotfixRollbackRequest
            {
                ManifestJson = request.ManifestJson, BackupDir = request.BackupDir,
                TargetDir = request.TargetDir, StateFilePath = request.StateFilePath,
                BaseVersion = manifest.BaseVersion, RestoreHotfixLevel = Math.Max(0, request.InstalledHotfixLevel),
                RestoreLastKnownGood = Math.Max(0, request.PreviousLastKnownGood),
            }, log);
            return HotfixApplyResult.Fail(rollback.Success
                ? $"File replacement failed and was rolled back: {ex.GetType().Name}: {ex.Message}."
                : $"File replacement failed: {ex.GetType().Name}: {ex.Message}. Automatic rollback failed: {rollback.Error}");
        }

        // Phase 4: update hotfix state. hotfixLevel advances, but
        // lastKnownGoodHotfix is preserved until startup confirmation commits it.
        try
        {
            var preserved = ReadLastKnownGood(request.StateFilePath, manifest.BaseVersion, manifest.Hotfix);
            WriteStateFile(request.StateFilePath, manifest.BaseVersion.Trim(), manifest.Hotfix, preserved);
        }
        catch (Exception ex)
        {
            var rollback = Rollback(new HotfixRollbackRequest
            {
                ManifestJson = request.ManifestJson, BackupDir = request.BackupDir,
                TargetDir = request.TargetDir, StateFilePath = request.StateFilePath,
                BaseVersion = manifest.BaseVersion, RestoreHotfixLevel = Math.Max(0, request.InstalledHotfixLevel),
                RestoreLastKnownGood = Math.Max(0, request.PreviousLastKnownGood),
            }, log);
            return HotfixApplyResult.Fail(rollback.Success
                ? $"State update failed and files were rolled back: {ex.GetType().Name}: {ex.Message}."
                : $"State update failed: {ex.GetType().Name}: {ex.Message}. Automatic rollback failed: {rollback.Error}");
        }

        Log("Hotfix application completed");
        return result;
    }

    // --- Rollback (Phase 4) ---

    public static HotfixRollbackResult Rollback(HotfixRollbackRequest request, Action<string>? log = null)
    {
        if (request is null) return HotfixRollbackResult.Fail("Rollback request is missing.");
        void Log(string m) { try { log?.Invoke(m); } catch { } }

        Log("Rollback started");
        if (!TryParseManifest(request.ManifestJson, out var manifest, out var parseError) || manifest is null)
            return HotfixRollbackResult.Fail(parseError ?? "Manifest is malformed.");
        if (string.IsNullOrWhiteSpace(request.BackupDir) || !Directory.Exists(request.BackupDir))
            return HotfixRollbackResult.Fail($"Backup directory not found: {request.BackupDir}. Cannot roll back safely.");
        if (string.IsNullOrWhiteSpace(request.TargetDir) || !Directory.Exists(request.TargetDir))
            return HotfixRollbackResult.Fail($"Target directory not found: {request.TargetDir}.");
        if (NormalizeBaseVersion(manifest.BaseVersion) is null ||
            !AreSameBaseVersion(manifest.BaseVersion, request.BaseVersion))
            return HotfixRollbackResult.Fail($"Base version mismatch: manifest is '{manifest.BaseVersion}', expected '{request.BaseVersion}'.");

        if (!TryReadBackupIndex(Path.Combine(request.BackupDir, "backup.json"), out var records) || records is null)
            return HotfixRollbackResult.Fail("Backup index (backup.json) is missing or malformed. Cannot roll back safely.");
        if (!BackupCoversAll(records, manifest.Files))
            return HotfixRollbackResult.Fail("Backup index does not cover all manifest files. Cannot roll back safely.");

        var targetRoot = Path.GetFullPath(request.TargetDir);
        var backupRoot = Path.GetFullPath(request.BackupDir);
        var byRel = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, existed) in records) byRel[rel] = existed;

        var result = new HotfixRollbackResult { Success = true };
        try
        {
            foreach (var entry in manifest.Files)
            {
                var rel = entry.Path.Trim().Replace('\\', '/');
                string dest;
                try { dest = Path.GetFullPath(Path.Combine(targetRoot, rel)); }
                catch { return HotfixRollbackResult.Fail($"Invalid path in manifest: '{entry.Path}'."); }
                if (!IsPathWithinDirectoryWithoutReparsePoints(dest, targetRoot))
                    return HotfixRollbackResult.Fail($"Path escapes installation directory: '{entry.Path}'.");
                if (!byRel.TryGetValue(rel, out var existed))
                    return HotfixRollbackResult.Fail($"No backup record for '{rel}'.");
                if (existed)
                {
                    var backupPath = Path.GetFullPath(Path.Combine(backupRoot, rel));
                    if (!IsPathWithinDirectoryWithoutReparsePoints(backupPath, backupRoot) || !File.Exists(backupPath))
                        return HotfixRollbackResult.Fail($"Backup file missing: '{rel}'.");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(backupPath, dest, overwrite: true);
                    if (entry.Executable && !EnsureExecutable(dest))
                        return HotfixRollbackResult.Fail($"Executable permission could not be restored: '{entry.Path}'.");
                    result.RestoredFiles.Add(rel);
                    Log($"File restored: {rel}");
                }
                else
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    result.RemovedFiles.Add(rel);
                    Log($"Added file removed: {rel}");
                }
            }
            WriteStateFile(request.StateFilePath, request.BaseVersion.Trim(), request.RestoreHotfixLevel, request.RestoreLastKnownGood);
            Log("Hotfix state restored");
        }
        catch (Exception ex)
        {
            return HotfixRollbackResult.Fail($"Rollback failed: {ex.GetType().Name}: {ex.Message}.");
        }

        // Backups are deliberately retained for diagnostics; confirmation-time
        // cleanup removes them (see HotfixRecovery).
        Log("Rollback completed");
        return result;
    }

    private static string? ValidateManifestForApply(
        HotfixPackageManifest manifest, string expectedBase, int installedLevel, string expectedPlatform)
    {
        if (manifest.SchemaVersion != 1)
            return $"Unsupported schema version {manifest.SchemaVersion} (expected 1).";
        if (!string.Equals(manifest.Product?.Trim(), "Kodo", StringComparison.Ordinal))
            return $"Unexpected product '{manifest.Product}' (expected 'Kodo').";
        if (NormalizeBaseVersion(manifest.BaseVersion) is null)
            return $"Invalid base version '{manifest.BaseVersion}'.";
        if (!AreSameBaseVersion(manifest.BaseVersion, expectedBase))
            return $"Base version mismatch: package is '{manifest.BaseVersion}', installed is '{expectedBase}'.";
        if (manifest.Hotfix < 1)
            return $"Invalid hotfix number {manifest.Hotfix} (must be >= 1).";
        if (manifest.Hotfix <= installedLevel)
            return $"Hotfix HF{manifest.Hotfix} is not newer than installed HF{installedLevel}.";
        if (manifest.MinimumHotfix < 0 || manifest.MinimumHotfix > manifest.Hotfix)
            return $"Invalid minimumHotfix {manifest.MinimumHotfix}.";
        if (installedLevel >= 0 && installedLevel < manifest.MinimumHotfix)
            return $"Hotfix requires HF{manifest.MinimumHotfix} or newer; installed HF{installedLevel}.";
        if (string.IsNullOrWhiteSpace(manifest.Platform) ||
            !string.Equals(manifest.Platform.Trim(), expectedPlatform?.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Platform mismatch: package is '{manifest.Platform}', expected '{expectedPlatform}'.";
        if (manifest.Files is null || manifest.Files.Count == 0)
            return "Manifest lists no files.";
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsSafeRelativePath(file.Path)) return "Manifest contains an unsafe file path.";
            var normalized = file.Path.Trim().Replace('\\', '/');
            if (!seen.Add(normalized)) return $"Manifest contains duplicate path '{file.Path}'.";
            if (!IsValidSha256(file.Sha256)) return $"Manifest contains an invalid SHA-256 for '{file.Path}'.";
        }
        return null;
    }

    private static void WriteBackupIndex(string path, List<(string Rel, bool Existed)> records)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartArray();
                foreach (var (rel, existed) in records)
                {
                    w.WriteStartObject();
                    w.WriteString("path", rel);
                    w.WriteBoolean("existed", existed);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            bytes = ms.ToArray();
        }
        WriteBytesAtomically(path, bytes);
    }

    internal static void WriteStateFile(string path, string baseVersion, int hotfixLevel, int lastKnownGood = 0)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var lkg = Math.Max(0, Math.Min(lastKnownGood, hotfixLevel));
        var payload = "{\"baseVersion\":\"" + baseVersion.Trim() + "\",\"hotfixLevel\":" + hotfixLevel +
            ",\"lastKnownGoodHotfix\":" + lkg + "}";
        WriteTextAtomically(path, payload);
    }

    private static int ReadLastKnownGood(string stateFilePath, string newBaseVersion, int newLevel)
    {
        try
        {
            if (!File.Exists(stateFilePath)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(stateFilePath));
            var baseVersion = "";
            var lkg = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "baseVersion", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    baseVersion = prop.Value.GetString() ?? "";
                if (string.Equals(prop.Name, "lastKnownGoodHotfix", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var v))
                    lkg = v;
            }
            if (!AreSameBaseVersion(baseVersion, newBaseVersion)) return 0;
            return Math.Max(0, Math.Min(lkg, newLevel));
        }
        catch { return 0; }
    }

    internal static bool TryReadBackupIndex(string path, out List<(string Rel, bool Existed)>? records)
    {
        records = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            var list = new List<(string Rel, bool Existed)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var rel = GetString(item, "path");
                if (string.IsNullOrWhiteSpace(rel)) return false;
                var existed = false;
                foreach (var prop in item.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "existed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.True) existed = true;
                        else if (prop.Value.ValueKind == JsonValueKind.False) existed = false;
                        else return false;
                    }
                }
                list.Add((rel, existed));
            }
            records = list;
            return true;
        }
        catch { return false; }
    }

    private static bool BackupCoversAll(
        List<(string Rel, bool Existed)> records, List<HotfixPackageFile> files)
    {
        if (records is null || files is null) return false;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, _) in records) set.Add(rel.Trim().Replace('\\', '/'));
        foreach (var f in files)
        {
            if (f is null) return false;
            if (!set.Contains(f.Path.Trim().Replace('\\', '/'))) return false;
        }
        return true;
    }

    public static bool IsStale(DateTime createdAtUtc, DateTime nowUtc, double staleHours = 24) =>
        createdAtUtc != DateTime.MinValue && createdAtUtc.ToUniversalTime() < nowUtc.ToUniversalTime().AddHours(-staleHours);

    // --- Failure tracker (loop prevention): hotfix-failures.json in update root ---

    public static int RecordFailure(string updateRoot, string baseVersion, int hotfixLevel)
    {
        try
        {
            var path = FailuresPath(updateRoot);
            var entries = ReadFailureEntries(path);
            var normalized = NormalizeBaseVersion(baseVersion) ?? baseVersion.Trim();
            var found = false;
            foreach (var e in entries)
            {
                if (AreSameBaseVersion(e.BaseVersion, normalized) && e.HotfixLevel == hotfixLevel)
                {
                    e.Failures++;
                    e.LastFailureUtc = DateTime.UtcNow;
                    found = true;
                }
            }
            if (!found)
                entries.Add(new HotfixFailureEntry
                {
                    BaseVersion = normalized,
                    HotfixLevel = hotfixLevel,
                    Failures = 1,
                    LastFailureUtc = DateTime.UtcNow,
                });
            WriteFailureEntries(path, entries);
            foreach (var e in entries)
                if (AreSameBaseVersion(e.BaseVersion, normalized) && e.HotfixLevel == hotfixLevel)
                    return e.Failures;
            return 1;
        }
        catch { return 1; }
    }

    public static int FailureCount(string updateRoot, string baseVersion, int hotfixLevel)
    {
        try
        {
            foreach (var e in ReadFailureEntries(FailuresPath(updateRoot)))
                if (AreSameBaseVersion(e.BaseVersion, baseVersion) && e.HotfixLevel == hotfixLevel)
                    return Math.Max(0, e.Failures);
            return 0;
        }
        catch { return 0; }
    }

    public static bool IsBlocked(string updateRoot, string baseVersion, int hotfixLevel, int maxAttempts = HotfixFailurePolicy.MaxAttempts) =>
        FailureCount(updateRoot, baseVersion, hotfixLevel) >= Math.Max(1, maxAttempts);

    public static void ClearFailures(string updateRoot, string baseVersion, int hotfixLevel)
    {
        try
        {
            var path = FailuresPath(updateRoot);
            var entries = ReadFailureEntries(path);
            entries.RemoveAll(e => AreSameBaseVersion(e.BaseVersion, baseVersion) && e.HotfixLevel == hotfixLevel);
            WriteFailureEntries(path, entries);
        }
        catch { }
    }

    private static string FailuresPath(string updateRoot) =>
        Path.Combine(updateRoot ?? "", HotfixFailurePolicy.FailuresFileName);

    private sealed class HotfixFailureEntry
    {
        public string BaseVersion { get; set; } = "";
        public int HotfixLevel { get; set; }
        public int Failures { get; set; }
        public DateTime LastFailureUtc { get; set; }
    }

    private static List<HotfixFailureEntry> ReadFailureEntries(string path)
    {
        var list = new List<HotfixFailureEntry>();
        try
        {
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var baseVersion = GetString(item, "baseVersion");
                if (NormalizeBaseVersion(baseVersion) is null) continue;
                var entry = new HotfixFailureEntry { BaseVersion = baseVersion };
                foreach (var prop in item.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "hotfixLevel", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var l))
                        entry.HotfixLevel = l;
                    else if (string.Equals(prop.Name, "failures", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var f))
                        entry.Failures = f;
                    else if (string.Equals(prop.Name, "lastFailureUtc", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(prop.Value.GetString(), out var dt))
                        entry.LastFailureUtc = dt.ToUniversalTime();
                }
                if (entry.HotfixLevel >= 1) list.Add(entry);
            }
        }
        catch { }
        return list;
    }

    private static void WriteFailureEntries(string path, List<HotfixFailureEntry> entries)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartArray();
                foreach (var e in entries)
                {
                    w.WriteStartObject();
                    w.WriteString("baseVersion", e.BaseVersion);
                    w.WriteNumber("hotfixLevel", e.HotfixLevel);
                    w.WriteNumber("failures", Math.Max(0, e.Failures));
                    w.WriteString("lastFailureUtc", e.LastFailureUtc.ToUniversalTime().ToString("o"));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            bytes = ms.ToArray();
        }
        WriteBytesAtomically(path, bytes);
    }

    public static string ResolveRestartTarget(string kodoExePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(kodoExePath) && File.Exists(kodoExePath))
                return kodoExePath;
            if (OperatingSystem.IsWindows())
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kodo", "Kodo.exe");
                if (File.Exists(fallback)) return fallback;
            }
        }
        catch { }
        return kodoExePath;
    }

    // --- Self-contained primitives (mirrors Kodo-side helpers; no shared deps) ---

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
        foreach (var s in core.Split('.'))
        {
            if (s.Length == 0) return null;
            foreach (var c in s) if (!char.IsDigit(c)) return null;
            if (!int.TryParse(s, out _)) return null;
        }
        return core;
    }

    internal static bool AreSameBaseVersion(string? a, string? b)
    {
        var na = NormalizeBaseVersion(a);
        var nb = NormalizeBaseVersion(b);
        if (na is null || nb is null) return false;
        var pa = na.Split('.');
        var pb = nb.Split('.');
        var max = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < max; i++)
        {
            var ia = i < pa.Length && int.TryParse(pa[i], out var va) ? va : 0;
            var ib = i < pb.Length && int.TryParse(pb[i], out var vb) ? vb : 0;
            if (ia != ib) return false;
        }
        return true;
    }

    internal static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hex = value.Trim();
        if (hex.Length != 64) return false;
        foreach (var c in hex) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        if (p.Length == 0 || p.Length > 260) return false;
        if (Path.IsPathRooted(p)) return false;
        if (p.StartsWith('/') || p.StartsWith('\\')) return false;
        if (p.Length >= 2 && p[1] == ':') return false;
        foreach (var seg in p.Split('/', '\\'))
        {
            if (seg.Length == 0) return false;
            if (seg == "." || seg == ".." || seg == "~") return false;
            if (seg.EndsWith(':')) return false;
            // A colon anywhere enables NTFS alternate data streams
            // ("file:stream") on Windows; payload names must be plain files.
            if (seg.Contains(':')) return false;
            // Windows strips trailing dots/spaces, so "file " and "file" would
            // land on the same file while hashing as different names.
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return false;
            if (IsWindowsReservedDeviceName(seg)) return false;
        }
        if (p.Contains("..", StringComparison.Ordinal)) return false;
        return true;
    }

    // Bare Windows device names (CON, NUL, COM1, ...) address devices rather
    // than files. Names with a real extension (e.g. "nul.txt") are ordinary
    // files and stay allowed.
    private static bool IsWindowsReservedDeviceName(string segment)
    {
        var name = segment.TrimEnd('.', ' ').ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL") return true;
        if (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
            name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9') return true;
        return false;
    }

    internal static bool IsInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory)) return false;
        try
        {
            path = Path.GetFullPath(path);
            directory = Path.GetFullPath(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch { return false; }
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var sep = Path.DirectorySeparatorChar.ToString();
        return string.Equals(path, directory, comparison) ||
            path.StartsWith(directory + sep, comparison);
    }

    internal static bool IsPathWithinDirectoryWithoutReparsePoints(string path, string directory)
    {
        if (!IsInsideDirectory(path, directory)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (relative == ".") return true;
            var cursor = fullRoot;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                cursor = Path.Combine(cursor, part);
                if (!File.Exists(cursor) && !Directory.Exists(cursor)) continue;
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    internal static bool VerifyFileSha256(string path, string expectedHex)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                hasher.AppendData(buffer, 0, read);
            var actual = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
            return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    internal static string ComputeSha256Hex(byte[] bytes)
    {
        using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        hasher.AppendData(bytes);
        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }

    // --- Linux managed-tarball full updates (parity with the Windows installer flow) ---
    //
    // A user-writable archive install (marker file present) can be updated
    // automatically: the verified tarball is extracted to staging, validated,
    // then swapped into place with the previous install kept as a
    // same-operation backup. Anything else on Linux (AppImage, .deb, foreign
    // layouts) stays on the manual path and never reaches here.
    public const string ManagedInstallMarkerFileName = ".kodo-managed-install";

    public static TarballInstallResult InstallTarballUpdate(TarballInstallRequest request, Action<string>? log = null)
    {
        if (request is null) return TarballInstallResult.Fail("Install request is missing.");
        void Log(string m) { try { log?.Invoke(m); } catch { } }
        Log("Tarball installation started");

        string tarball;
        string targetRoot;
        string workRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(request.TarballPath) || !File.Exists(request.TarballPath))
                return TarballInstallResult.Fail($"Tarball not found: {request.TarballPath}.");
            if (string.IsNullOrWhiteSpace(request.TargetDir) || !Directory.Exists(request.TargetDir))
                return TarballInstallResult.Fail($"Target directory not found: {request.TargetDir}.");
            if (string.IsNullOrWhiteSpace(request.WorkRoot))
                return TarballInstallResult.Fail("Work directory is missing.");
            tarball = Path.GetFullPath(request.TarballPath);
            targetRoot = Path.GetFullPath(request.TargetDir);
            workRoot = Path.GetFullPath(request.WorkRoot);
            Directory.CreateDirectory(workRoot);
        }
        catch (Exception ex) { return TarballInstallResult.Fail($"Invalid install paths: {ex.GetType().Name}."); }

        // Fail closed before touching anything: managed marker, writable
        // target (and parent, for the rename swap), no symlink games.
        if (!File.Exists(Path.Combine(targetRoot, ManagedInstallMarkerFileName)))
            return TarballInstallResult.Fail("Target is not a managed Kodo archive install (marker missing). Installation untouched.");
        if (!IsWritableDirectory(targetRoot))
            return TarballInstallResult.Fail("Target directory is not writable by the current user. Installation untouched.");
        var targetParent = Path.GetDirectoryName(targetRoot);
        if (string.IsNullOrWhiteSpace(targetParent) || !IsWritableDirectory(targetParent))
            return TarballInstallResult.Fail("Installation parent directory is not writable. Installation untouched.");
        try
        {
            if ((File.GetAttributes(targetRoot) & FileAttributes.ReparsePoint) != 0)
                return TarballInstallResult.Fail("Target directory is a reparse point. Installation untouched.");
        }
        catch (Exception ex) { return TarballInstallResult.Fail($"Target cannot be inspected: {ex.GetType().Name}. Installation untouched."); }

        var kodoName = string.IsNullOrWhiteSpace(request.KodoExeName) ? "Kodo" : request.KodoExeName;
        var updaterName = string.IsNullOrWhiteSpace(request.KodoUpdaterName) ? "KodoUpdater" : request.KodoUpdaterName;
        var stageDir = Path.Combine(workRoot, "staging");
        var backupDir = Path.Combine(workRoot, "backup");
        var previousDir = Path.Combine(backupDir, "previous");

        TarballInstallResult FailRestored(string error)
        {
            Exception? restoreError = null;
            try
            {
                if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, recursive: true);
                MoveOrCopyDirectory(previousDir, targetRoot);
                EnsureBestEffortExecutable(targetRoot, kodoName, updaterName);
            }
            catch (Exception rex) { restoreError = rex; }
            if (restoreError is null)
            {
                CleanupQuietly(stageDir);
                CleanupQuietly(backupDir);
                return TarballInstallResult.Fail(error + " Previous installation was restored.");
            }
            return TarballInstallResult.Fail(
                error + $" Automatic restore failed: {restoreError.GetType().Name}: {restoreError.Message}." +
                $" Previous install retained at '{previousDir}' for manual recovery.",
                previousDir);
        }

        try
        {
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            Directory.CreateDirectory(stageDir);
            Directory.CreateDirectory(backupDir);

            // 1. Extract (tar preserves Unix modes; the binaries get an
            // explicit chmod below regardless of archive metadata).
            try
            {
                using var fs = File.OpenRead(tarball);
                using var gzip = new GZipStream(fs, CompressionMode.Decompress);
                System.Formats.Tar.TarFile.ExtractToDirectory(gzip, stageDir, overwriteFiles: false);
            }
            catch (Exception ex)
            {
                CleanupQuietly(stageDir);
                CleanupQuietly(backupDir);
                return TarballInstallResult.Fail($"Tarball cannot be extracted: {ex.GetType().Name}: {ex.Message}. Installation untouched.");
            }

            // 2. Validate the staged tree (our tarballs nest under kodo/).
            var stagedRoot = Directory.Exists(Path.Combine(stageDir, "kodo"))
                ? Path.Combine(stageDir, "kodo")
                : stageDir;
            if (!File.Exists(Path.Combine(stagedRoot, kodoName)) ||
                !File.Exists(Path.Combine(stagedRoot, updaterName)))
            {
                CleanupQuietly(stageDir);
                CleanupQuietly(backupDir);
                return TarballInstallResult.Fail("Staged tarball does not contain a complete Kodo installation. Installation untouched.");
            }

            // 3. Swap with same-operation backup (rename; copy fallback).
            try
            {
                MoveOrCopyDirectory(targetRoot, previousDir);
            }
            catch (Exception ex)
            {
                CleanupQuietly(stageDir);
                CleanupQuietly(backupDir);
                return TarballInstallResult.Fail($"Previous installation could not be backed up: {ex.GetType().Name}: {ex.Message}. Installation untouched.");
            }
            try
            {
                MoveOrCopyDirectory(stagedRoot, targetRoot);
            }
            catch (Exception ex)
            {
                return FailRestored($"New files could not be put in place: {ex.GetType().Name}: {ex.Message}.");
            }

            // 4. Explicit +x on both binaries (never rely on modes alone).
            if (!EnsureExecutable(Path.Combine(targetRoot, kodoName)) ||
                !EnsureExecutable(Path.Combine(targetRoot, updaterName)))
            {
                return FailRestored("Executable permission could not be set on the new installation.");
            }

            CleanupQuietly(stageDir);
            CleanupQuietly(backupDir);
            Log("Tarball installation completed");
            return new TarballInstallResult { Success = true };
        }
        catch (Exception ex)
        {
            return TarballInstallResult.Fail($"Tarball installation failed: {ex.GetType().Name}: {ex.Message}.");
        }
    }

    internal static bool IsWritableDirectory(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, ".kodo-write-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    internal static void MoveOrCopyDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
            return;
        }
        catch (IOException)
        {
            // Cross-filesystem (or similar): copy, then remove the source.
            CopyDirectoryRecursive(source, dest);
            Directory.Delete(source, recursive: true);
        }
    }

    internal static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    internal static void CleanupQuietly(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static void EnsureBestEffortExecutable(string targetRoot, string kodoName, string updaterName)
    {
        try { EnsureExecutable(Path.Combine(targetRoot, kodoName)); } catch { }
        try { EnsureExecutable(Path.Combine(targetRoot, updaterName)); } catch { }
    }

    // Ensures a manifest-flagged executable stays runnable on Unix after an
    // apply or rollback. Managed payload DLLs never need this; native
    // binaries (Kodo, KodoUpdater) do. No-op on Windows. Returns false
    // instead of throwing so callers fail loudly (triggering rollback)
    // rather than reporting a success whose binary cannot start.
    internal static bool EnsureExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return true;
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Cumulative-release stamp (fresh installs start at the shipped level) ---
    //
    // Release tooling bakes hotfix-build.json into the publish directory, so
    // every installer/tarball carries the hotfix level its files already
    // contain. A fresh install therefore starts at e.g. 2.1.0 HF3 instead of
    // HF0 and never replays the HF1..HF3 chain through the updater.
    //
    // The stamp is a floor, never a ceiling: callers take
    // max(persistedLevel, shippedLevel) when the base versions match, and
    // ignore the stamp entirely when they do not. Never throws.
    //
    // Since stamp v2 the file may also carry "files" (relative path plus
    // SHA-256, hashed from the publish directory at stamp time). Kodo retains
    // that list and reconciles it against the installed files, so reinstalling
    // an older package over a cumulative install is detected and repaired
    // instead of silently claiming a hotfix level whose files are gone.
    public const string ShippedStampFileName = "hotfix-build.json";

    public const int MaxStampBytes = 65536;

    public static bool TryGetShippedHotfixLevel(string? installDir, string? baseVersion, out int level)
    {
        level = 0;
        return TryGetShippedStamp(installDir, baseVersion, out level, out _);
    }

    public static bool TryGetShippedStamp(
        string? installDir, string? baseVersion, out int level, out List<HotfixPackageFile>? files)
    {
        level = 0;
        files = null;
        try
        {
            if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(baseVersion))
                return false;
            var stampPath = Path.Combine(installDir, ShippedStampFileName);
            if (!File.Exists(stampPath))
                return false;
            var json = File.ReadAllText(stampPath);
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStampBytes)
                return false;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var stampedBase = GetString(doc.RootElement, "baseVersion");
            var stampedLevel = GetInt(doc.RootElement, "hotfixLevel");
            if (NormalizeBaseVersion(stampedBase) is null || stampedLevel < 0)
                return false;
            if (!AreSameBaseVersion(stampedBase, baseVersion))
                return false;
            level = stampedLevel;
            files = ReadStampFiles(doc.RootElement);
            return true;
        }
        catch
        {
            level = 0;
            files = null;
            return false;
        }
    }

    private static List<HotfixPackageFile>? ReadStampFiles(JsonElement root)
    {
        try
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "files", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Array) return null;
                var list = new List<HotfixPackageFile>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) return null;
                    var path = GetString(item, "path");
                    var sha256 = GetString(item, "sha256");
                    if (!IsSafeRelativePath(path) || !IsValidSha256(sha256)) return null;
                    var normalized = path.Trim().Replace('\\', '/');
                    if (list.Exists(f => string.Equals(
                        f.Path.Trim().Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)))
                        return null;
                    list.Add(new HotfixPackageFile { Path = normalized, Sha256 = sha256.Trim().ToLowerInvariant() });
                }
                return list;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
