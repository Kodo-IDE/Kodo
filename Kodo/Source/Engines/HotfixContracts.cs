// Licensed under the GNU GPL-v3.0
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

    public string? RetainedBackupDir { get; set; }

    public static TarballInstallResult Fail(string error, string? retainedBackupDir = null) =>
        new() { Success = false, Error = error, RetainedBackupDir = retainedBackupDir };
}
