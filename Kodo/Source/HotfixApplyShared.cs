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
    public string ExpectedPlatformRid { get; set; } = "";
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

internal static class HotfixShared
{
    public static readonly string[] KnownPlatforms = { "win-x64", "linux-x64", "linux-arm64" };

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
            if (!IsInsideDirectory(dest, targetRoot))
                return HotfixApplyResult.Fail($"Path escapes installation directory: '{entry.Path}'.");
            if (!IsValidSha256(entry.Sha256))
                return HotfixApplyResult.Fail($"Invalid SHA-256 for '{entry.Path}'.");
            string payload;
            try
            {
                payload = Path.GetFullPath(Path.Combine(payloadRoot, entry.Path.Trim()));
            }
            catch { return HotfixApplyResult.Fail($"Invalid payload path: '{entry.Path}'."); }
            if (!IsInsideDirectory(payload, payloadRoot) || !File.Exists(payload))
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
                        if (!IsInsideDirectory(backupPath, backupRoot))
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

        // Phase 3: copy payload over the installation (never execute package files).
        try
        {
            foreach (var (_, payload, dest) in planned)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(payload, dest, overwrite: true);
                result.ReplacedFiles.Add(dest);
            }
            Log($"Files replaced: {result.ReplacedFiles.Count}");
        }
        catch (Exception ex)
        {
            return HotfixApplyResult.Fail($"File replacement failed: {ex.GetType().Name}: {ex.Message}. Restore from backup before retry.");
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
            return HotfixApplyResult.Fail($"Files replaced but state update failed: {ex.GetType().Name}: {ex.Message}.");
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
                if (!IsInsideDirectory(dest, targetRoot))
                    return HotfixRollbackResult.Fail($"Path escapes installation directory: '{entry.Path}'.");
                if (!byRel.TryGetValue(rel, out var existed))
                    return HotfixRollbackResult.Fail($"No backup record for '{rel}'.");
                if (existed)
                {
                    var backupPath = Path.GetFullPath(Path.Combine(backupRoot, rel));
                    if (!IsInsideDirectory(backupPath, backupRoot) || !File.Exists(backupPath))
                        return HotfixRollbackResult.Fail($"Backup file missing: '{rel}'.");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(backupPath, dest, overwrite: true);
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
        if (string.IsNullOrWhiteSpace(manifest.Platform) ||
            !string.Equals(manifest.Platform.Trim(), expectedPlatform?.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Platform mismatch: package is '{manifest.Platform}', expected '{expectedPlatform}'.";
        if (manifest.Files is null || manifest.Files.Count == 0)
            return "Manifest lists no files.";
        return null;
    }

    private static void WriteBackupIndex(string path, List<(string Rel, bool Existed)> records)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
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

    internal static void WriteStateFile(string path, string baseVersion, int hotfixLevel, int lastKnownGood = 0)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var lkg = Math.Max(0, Math.Min(lastKnownGood, hotfixLevel));
        var payload = "{\"baseVersion\":\"" + baseVersion.Trim() + "\",\"hotfixLevel\":" + hotfixLevel +
            ",\"lastKnownGoodHotfix\":" + lkg + "}";
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, path, overwrite: true);
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
        using (var fs = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
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
        File.Move(path + ".tmp", path, overwrite: true);
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
        }
        if (p.Contains("..", StringComparison.Ordinal)) return false;
        return true;
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
}
