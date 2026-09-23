// Licensed under GPL-v3.0
using Kodo;
using Kodo.HotfixShared;
using Shared = Kodo.HotfixShared.HotfixShared;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kodo.Hotfix.Tests;

// Phase 4 tests: confirmation, commit, rollback, retry limits, and end-to-end
// lifecycles. All filesystem work uses temp directories; updater supervision
// (process launch/wait) is exercised through the same shared functions the
// updater calls. No live processes are started.
[TestClass]
public sealed class HotfixRecoveryTests
{
    private static readonly string Rid = HotfixDiscovery.GetCurrentPlatformRid();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kodo-hf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ShaHex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HotfixManifest Manifest(string baseVersion, int hotfix, string platform, params (string Path, byte[] Content)[] files)
    {
        var m = new HotfixManifest
        {
            SchemaVersion = 1,
            Product = "Kodo",
            BaseVersion = baseVersion,
            Hotfix = hotfix,
            MinimumHotfix = 0,
            Platform = platform,
        };
        foreach (var (p, c) in files)
            m.Files.Add(new HotfixFileEntry { Path = p, Sha256 = ShaHex(c) });
        return m;
    }

    private sealed class Lab
    {
        public string Root = "";
        public string UpdateRoot = "";
        public string InstallDir = "";
        public string StageDir = "";
        public string PayloadDir = "";
        public string BackupDir = "";
        public string ManifestPath = "";
        public string TxPath = "";
        public string StatePath = "";
        public string TxId = Guid.NewGuid().ToString("N");
        public HotfixManifest Manifest = null!;
        public string ManifestJson = "";
    }

    // Builds: install/a.dll="v0", staged HF1 payload="v1", applied via shared
    // Apply, transaction awaitingConfirmation. Returns the lab for assertions.
    private static Lab BuildAppliedLab(byte[]? oldBytes = null, byte[]? newBytes = null, int hotfix = 1)
    {
        oldBytes ??= System.Text.Encoding.UTF8.GetBytes("v0");
        newBytes ??= System.Text.Encoding.UTF8.GetBytes("v1");
        var root = NewTempRoot();
        var lab = new Lab
        {
            Root = root,
            UpdateRoot = Path.Combine(root, "update"),
            InstallDir = Path.Combine(root, "install"),
        };
        lab.StageDir = Path.Combine(lab.UpdateRoot, "hotfix", lab.TxId);
        lab.PayloadDir = Path.Combine(lab.StageDir, "payload");
        lab.BackupDir = Path.Combine(lab.StageDir, "backup");
        lab.ManifestPath = Path.Combine(lab.StageDir, "manifest.json");
        lab.TxPath = Path.Combine(lab.StageDir, "transaction.json");
        lab.StatePath = Path.Combine(lab.UpdateRoot, "hotfix-state.json");
        Directory.CreateDirectory(lab.PayloadDir);
        Directory.CreateDirectory(lab.InstallDir);

        File.WriteAllBytes(Path.Combine(lab.InstallDir, "a.dll"), oldBytes);
        lab.Manifest = Manifest("2.1.0", hotfix, Rid, ("a.dll", newBytes));
        lab.ManifestJson = JsonSerializer.Serialize(lab.Manifest);
        File.WriteAllText(lab.ManifestPath, lab.ManifestJson);
        File.WriteAllBytes(Path.Combine(lab.PayloadDir, "a.dll"), newBytes);

        var apply = Shared.Apply(new HotfixApplyRequest
        {
            ManifestJson = lab.ManifestJson,
            PayloadDir = lab.PayloadDir,
            BackupDir = lab.BackupDir,
            TargetDir = lab.InstallDir,
            StateFilePath = lab.StatePath,
            ExpectedBaseVersion = "2.1.0",
            InstalledHotfixLevel = 0,
            ExpectedPlatformRid = Rid,
        }, null);
        Assert.IsTrue(apply.Success, apply.Error);

        var tx = new HotfixTransaction
        {
            Kind = "hotfix",
            TransactionId = lab.TxId,
            Status = HotfixTransactionStatus.Staged,
            PackagePath = Path.Combine(lab.StageDir, "package.zip"),
            ManifestPath = lab.ManifestPath,
            PayloadDir = lab.PayloadDir,
            BackupDir = lab.BackupDir,
            TargetDir = lab.InstallDir,
            KodoExePath = Path.Combine(lab.InstallDir, "Kodo"),
            KodoPid = 0,
            RestartAfterUpdate = false,
            CreatedAtUtc = DateTime.UtcNow,
            BaseVersion = "2.1.0",
            HotfixLevel = hotfix,
            PreviousHotfixLevel = 0,
            PreviousLastKnownGood = 0,
            LastKnownGoodHotfix = 0,
            PlatformRid = Rid,
        };
        File.WriteAllText(lab.TxPath, Shared.MarkAwaitingConfirmation(
            JsonSerializer.Serialize(tx, Camel), DateTime.UtcNow.AddMinutes(3), 0));
        return lab;
    }

    // --- Confirmation / commit ---

    [TestMethod]
    public async Task Confirm_Success_CommitsAndCleansUp()
    {
        var lab = BuildAppliedLab();
        Shared.RecordFailure(lab.UpdateRoot, "2.1.0", 1);

        var result = await HotfixRecovery.ConfirmStartupAsync(lab.UpdateRoot, lab.InstallDir, "2.1.0");
        Assert.IsNull(result.NeedsRollbackTxPath);
        HasCount(1, result.Confirmed);

        var state = HotfixStateStore.LoadOrDefault(lab.StatePath);
        Assert.AreEqual("2.1.0", state.BaseVersion);
        Assert.AreEqual(1, state.HotfixLevel);
        Assert.AreEqual(1, state.LastKnownGoodHotfix);
        Assert.IsFalse(Directory.Exists(lab.StageDir), "stage dir (backup/package/payload) must be removed on commit");
        Assert.AreEqual(0, Shared.FailureCount(lab.UpdateRoot, "2.1.0", 1));
    }

    [TestMethod]
    public async Task Confirm_Mismatch_RequestsRollback()
    {
        var lab = BuildAppliedLab();
        File.WriteAllBytes(Path.Combine(lab.InstallDir, "a.dll"), System.Text.Encoding.UTF8.GetBytes("tampered"));

        var result = await HotfixRecovery.ConfirmStartupAsync(lab.UpdateRoot, lab.InstallDir, "2.1.0");
        Assert.AreEqual(lab.TxPath, result.NeedsRollbackTxPath);
        Assert.IsEmpty(result.Confirmed);
        Assert.IsTrue(Directory.Exists(lab.StageDir), "staging data must be kept for rollback");
    }

    [TestMethod]
    public async Task Confirm_BaseMismatch_ClosedWithoutStateChange()
    {
        var lab = BuildAppliedLab();
        var result = await HotfixRecovery.ConfirmStartupAsync(lab.UpdateRoot, lab.InstallDir, "9.9.9");
        Assert.IsNull(result.NeedsRollbackTxPath);
        Assert.IsEmpty(result.Confirmed);
        HasCount(1, result.Closed);
        Assert.IsFalse(Directory.Exists(lab.StageDir));
    }

    [TestMethod]
    public async Task Confirm_NoPendingTransactions_NoOp()
    {
        var root = NewTempRoot();
        var result = await HotfixRecovery.ConfirmStartupAsync(Path.Combine(root, "update"), Path.Combine(root, "install"), "2.1.0");
        Assert.IsNull(result.NeedsRollbackTxPath);
        Assert.IsEmpty(result.Confirmed);
        Assert.IsNull(result.Error);
    }

    // --- Rollback ---

    [TestMethod]
    public void Rollback_RestoresFilesAndState()
    {
        var lab = BuildAppliedLab();
        var req = new HotfixRollbackRequest
        {
            ManifestJson = lab.ManifestJson,
            BackupDir = lab.BackupDir,
            TargetDir = lab.InstallDir,
            StateFilePath = lab.StatePath,
            BaseVersion = "2.1.0",
            RestoreHotfixLevel = 0,
            RestoreLastKnownGood = 0,
        };
        var result = Shared.Rollback(req, null);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("v0", File.ReadAllText(Path.Combine(lab.InstallDir, "a.dll")));
        HasCount(1, result.RestoredFiles);

        var state = HotfixStateStore.LoadOrDefault(lab.StatePath);
        Assert.AreEqual(0, state.HotfixLevel);
        Assert.AreEqual(0, state.LastKnownGoodHotfix);

        var rolledBack = Shared.MarkRolledBack(File.ReadAllText(lab.TxPath), DateTime.UtcNow);
        File.WriteAllText(lab.TxPath, rolledBack);
        Assert.IsTrue(Shared.TryParseTransaction(File.ReadAllText(lab.TxPath), out var tx, out _));
        Assert.AreEqual(HotfixTransactionStatus.RolledBack, tx!.Status);
        Assert.IsTrue(Directory.Exists(lab.BackupDir), "backups retained for diagnostics");
    }

    [TestMethod]
    public void Rollback_RemovesAddedFiles()
    {
        var root = NewTempRoot();
        var install = Path.Combine(root, "install");
        var payload = Path.Combine(root, "payload");
        var backup = Path.Combine(root, "backup");
        var statePath = Path.Combine(root, "update", "hotfix-state.json");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        var added = new byte[] { 7, 7 };
        var manifest = Manifest("2.1.0", 1, Rid, ("plugins/extra.dll", added));
        var manifestJson = JsonSerializer.Serialize(manifest);
        Directory.CreateDirectory(Path.Combine(payload, "plugins"));
        File.WriteAllBytes(Path.Combine(payload, "plugins", "extra.dll"), added);

        var apply = Shared.Apply(new HotfixApplyRequest
        {
            ManifestJson = manifestJson,
            PayloadDir = payload,
            BackupDir = backup,
            TargetDir = install,
            StateFilePath = statePath,
            ExpectedBaseVersion = "2.1.0",
            InstalledHotfixLevel = 0,
            ExpectedPlatformRid = Rid,
        }, null);
        Assert.IsTrue(apply.Success, apply.Error);
        Assert.IsTrue(File.Exists(Path.Combine(install, "plugins", "extra.dll")));

        var rollback = Shared.Rollback(new HotfixRollbackRequest
        {
            ManifestJson = manifestJson,
            BackupDir = backup,
            TargetDir = install,
            StateFilePath = statePath,
            BaseVersion = "2.1.0",
            RestoreHotfixLevel = 0,
            RestoreLastKnownGood = 0,
        }, null);
        Assert.IsTrue(rollback.Success, rollback.Error);
        Assert.IsFalse(File.Exists(Path.Combine(install, "plugins", "extra.dll")));
        HasCount(1, rollback.RemovedFiles);
    }

    [TestMethod]
    public void Rollback_MultipleFiles()
    {
        var root = NewTempRoot();
        var install = Path.Combine(root, "install");
        var payload = Path.Combine(root, "payload");
        var backup = Path.Combine(root, "backup");
        var statePath = Path.Combine(root, "update", "hotfix-state.json");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        var keep = System.Text.Encoding.UTF8.GetBytes("keep-orig");
        var over = System.Text.Encoding.UTF8.GetBytes("over-orig");
        var keepNew = System.Text.Encoding.UTF8.GetBytes("keep-new");
        var overNew = System.Text.Encoding.UTF8.GetBytes("over-new");
        var addedNew = new byte[] { 9 };
        File.WriteAllBytes(Path.Combine(install, "keep.dll"), keep);
        File.WriteAllBytes(Path.Combine(install, "over.dll"), over);
        var manifest = Manifest("2.1.0", 1, Rid,
            ("keep.dll", keepNew), ("over.dll", overNew), ("added.dll", addedNew));
        var manifestJson = JsonSerializer.Serialize(manifest);
        File.WriteAllBytes(Path.Combine(payload, "keep.dll"), keepNew);
        File.WriteAllBytes(Path.Combine(payload, "over.dll"), overNew);
        File.WriteAllBytes(Path.Combine(payload, "added.dll"), addedNew);

        Assert.IsTrue(Shared.Apply(new HotfixApplyRequest
        {
            ManifestJson = manifestJson,
            PayloadDir = payload,
            BackupDir = backup,
            TargetDir = install,
            StateFilePath = statePath,
            ExpectedBaseVersion = "2.1.0",
            InstalledHotfixLevel = 0,
            ExpectedPlatformRid = Rid,
        }, null).Success);

        var rollback = Shared.Rollback(new HotfixRollbackRequest
        {
            ManifestJson = manifestJson,
            BackupDir = backup,
            TargetDir = install,
            StateFilePath = statePath,
            BaseVersion = "2.1.0",
            RestoreHotfixLevel = 0,
            RestoreLastKnownGood = 0,
        }, null);
        Assert.IsTrue(rollback.Success, rollback.Error);
        CollectionAssert.AreEqual(keep, File.ReadAllBytes(Path.Combine(install, "keep.dll")));
        CollectionAssert.AreEqual(over, File.ReadAllBytes(Path.Combine(install, "over.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(install, "added.dll")));
        HasCount(2, rollback.RestoredFiles);
        HasCount(1, rollback.RemovedFiles);
    }

    [TestMethod]
    public void Rollback_MissingBackupIndex_Refused()
    {
        var lab = BuildAppliedLab();
        File.Delete(Path.Combine(lab.BackupDir, "backup.json"));
        var result = Shared.Rollback(new HotfixRollbackRequest
        {
            ManifestJson = lab.ManifestJson,
            BackupDir = lab.BackupDir,
            TargetDir = lab.InstallDir,
            StateFilePath = lab.StatePath,
            BaseVersion = "2.1.0",
            RestoreHotfixLevel = 0,
            RestoreLastKnownGood = 0,
        }, null);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(lab.InstallDir, "a.dll")), "failed rollback must not touch the install");
    }

    // --- Interrupted-update recovery ---

    [TestMethod]
    public void Reapply_AfterInterruption_ReusesBackup()
    {
        var lab = BuildAppliedLab();
        // Simulate an interrupted retry: dest holds half-applied content.
        File.WriteAllText(Path.Combine(lab.InstallDir, "a.dll"), "half-applied");
        var retry = Shared.Apply(new HotfixApplyRequest
        {
            ManifestJson = lab.ManifestJson,
            PayloadDir = lab.PayloadDir,
            BackupDir = lab.BackupDir,
            TargetDir = lab.InstallDir,
            StateFilePath = lab.StatePath,
            ExpectedBaseVersion = "2.1.0",
            InstalledHotfixLevel = 0,
            ExpectedPlatformRid = Rid,
        }, null);
        Assert.IsTrue(retry.Success, retry.Error);
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(lab.InstallDir, "a.dll")));
        Assert.AreEqual("v0", File.ReadAllText(Path.Combine(lab.BackupDir, "a.dll")),
            "retry must not snapshot half-applied files as the backup");
    }

    [TestMethod]
    public void StaleTransaction_Detected()
    {
        Assert.IsTrue(Shared.IsStale(DateTime.UtcNow.AddHours(-25), DateTime.UtcNow));
        Assert.IsFalse(Shared.IsStale(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
        Assert.IsFalse(Shared.IsStale(DateTime.MinValue, DateTime.UtcNow));
    }

    // --- Retry limits / loop prevention ---

    [TestMethod]
    public void RetryLimit_BlocksAfterThreeFailures()
    {
        var root = NewTempRoot();
        var updateRoot = Path.Combine(root, "update");
        Assert.AreEqual(1, Shared.RecordFailure(updateRoot, "2.1.0", 2));
        Assert.IsFalse(Shared.IsBlocked(updateRoot, "2.1.0", 2));
        Assert.AreEqual(2, Shared.RecordFailure(updateRoot, "2.1.0", 2));
        Assert.IsFalse(Shared.IsBlocked(updateRoot, "2.1.0", 2));
        Assert.AreEqual(3, Shared.RecordFailure(updateRoot, "2.1.0", 2));
        Assert.IsTrue(Shared.IsBlocked(updateRoot, "2.1.0", 2));
        Assert.IsFalse(Shared.IsBlocked(updateRoot, "2.1.0", 3), "other levels unaffected");
        Shared.ClearFailures(updateRoot, "2.1.0", 2);
        Assert.IsFalse(Shared.IsBlocked(updateRoot, "2.1.0", 2));
    }

    private sealed class ThrowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("HTTP must not be called for blocked hotfixes");
    }

    [TestMethod]
    public async Task Prepare_BlockedHotfix_RefusedWithoutDownload()
    {
        var root = NewTempRoot();
        var updateRoot = Path.Combine(root, "update");
        Shared.RecordFailure(updateRoot, "2.1.0", 2);
        Shared.RecordFailure(updateRoot, "2.1.0", 2);
        Shared.RecordFailure(updateRoot, "2.1.0", 2);
        var candidate = new HotfixCandidate("2.1.0", 2, Rid, "hotfix/2.1.0/2",
            "https://example.invalid/notes", "pkg.zip", "https://example.invalid/pkg.zip", 10);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            HotfixStaging.PrepareAsync(candidate, httpClient: new HttpClient(new ThrowHandler()),
                updateRootOverride: updateRoot,
                targetDirOverride: Path.Combine(root, "install"),
                installedBaseOverride: "2.1.0", installedLevelOverride: 1,
                kodoPidOverride: 0));
    }

    // --- End-to-end ---

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        public FakeHandler(byte[] bytes) => _bytes = bytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) });
    }

    private static async Task<(string UpdateRoot, string InstallDir, HotfixStaging.PrepareResult Prepared, string ManifestJson)> PrepareEndToEndAsync(string root)
    {
        var updateRoot = Path.Combine(root, "update");
        var installDir = Path.Combine(root, "install");
        Directory.CreateDirectory(installDir);
        File.WriteAllBytes(Path.Combine(installDir, "a.dll"), System.Text.Encoding.UTF8.GetBytes("v0"));
        var payload = new Dictionary<string, byte[]> { ["a.dll"] = System.Text.Encoding.UTF8.GetBytes("v1") };
        var manifest = Manifest("2.1.0", 1, Rid, ("a.dll", payload["a.dll"]));
        var packageBytes = HotfixPackaging.CreatePackage(manifest, payload);
        var candidate = new HotfixCandidate("2.1.0", 1, Rid, "hotfix/2.1.0/1",
            "https://example.invalid/notes", "pkg.zip", "https://example.invalid/pkg.zip", packageBytes.Length);

        var prepared = await HotfixStaging.PrepareAsync(candidate,
            httpClient: new HttpClient(new FakeHandler(packageBytes)),
            updateRootOverride: updateRoot,
            targetDirOverride: installDir,
            installedBaseOverride: "2.1.0", installedLevelOverride: 0,
            kodoPidOverride: 0);
        Assert.IsFalse(prepared.AlreadyInstalled);
        Assert.IsNotNull(prepared.TransactionPath);
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(
            Path.GetDirectoryName(prepared.TransactionPath)!, "manifest.json"));
        return (updateRoot, installDir, prepared, manifestJson);
    }

    [TestMethod]
    public async Task EndToEnd_ApplyConfirmCommit()
    {
        // Kodo HF0 -> discover HF1 -> download -> verify -> stage -> apply ->
        // restart (simulated) -> startup confirmation -> commit.
        var root = NewTempRoot();
        var (updateRoot, installDir, prepared, manifestJson) = await PrepareEndToEndAsync(root);
        var tx = prepared.Transaction!;
        var stageDir = Path.GetDirectoryName(prepared.TransactionPath)!;

        var apply = Shared.Apply(new HotfixApplyRequest
        {
            ManifestJson = manifestJson,
            PayloadDir = tx.PayloadDir,
            BackupDir = tx.BackupDir,
            TargetDir = installDir,
            StateFilePath = Path.Combine(updateRoot, "hotfix-state.json"),
            ExpectedBaseVersion = tx.BaseVersion,
            InstalledHotfixLevel = tx.PreviousHotfixLevel,
            ExpectedPlatformRid = tx.PlatformRid,
        }, null);
        Assert.IsTrue(apply.Success, apply.Error);
        File.WriteAllText(prepared.TransactionPath!,
            Shared.MarkAwaitingConfirmation(File.ReadAllText(prepared.TransactionPath!), DateTime.UtcNow.AddMinutes(3), 0));

        var confirm = await HotfixRecovery.ConfirmStartupAsync(updateRoot, installDir, "2.1.0");
        Assert.IsNull(confirm.NeedsRollbackTxPath);
        HasCount(1, confirm.Confirmed);
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(installDir, "a.dll")));
        var state = HotfixStateStore.LoadOrDefault(Path.Combine(updateRoot, "hotfix-state.json"));
        Assert.AreEqual(1, state.HotfixLevel);
        Assert.AreEqual(1, state.LastKnownGoodHotfix);
        Assert.IsFalse(Directory.Exists(stageDir));
    }

    [TestMethod]
    public async Task EndToEnd_ApplyFailureRollbackRestoresHF0()
    {
        // Kodo HF0 -> apply HF1 -> startup failure (no confirm) -> rollback -> HF0.
        var root = NewTempRoot();
        var (updateRoot, installDir, prepared, manifestJson) = await PrepareEndToEndAsync(root);
        var tx = prepared.Transaction!;
        File.WriteAllText(prepared.TransactionPath!,
            Shared.MarkAwaitingConfirmation(File.ReadAllText(prepared.TransactionPath!), DateTime.UtcNow.AddMinutes(3), 0));

        var apply = Shared.Apply(new HotfixApplyRequest
        {
            ManifestJson = manifestJson,
            PayloadDir = tx.PayloadDir,
            BackupDir = tx.BackupDir,
            TargetDir = installDir,
            StateFilePath = Path.Combine(updateRoot, "hotfix-state.json"),
            ExpectedBaseVersion = tx.BaseVersion,
            InstalledHotfixLevel = tx.PreviousHotfixLevel,
            ExpectedPlatformRid = tx.PlatformRid,
        }, null);
        Assert.IsTrue(apply.Success, apply.Error);
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(installDir, "a.dll")));

        // Startup fails: updater path — roll back to last known good.
        var rollback = Shared.Rollback(new HotfixRollbackRequest
        {
            ManifestJson = manifestJson,
            BackupDir = tx.BackupDir,
            TargetDir = installDir,
            StateFilePath = Path.Combine(updateRoot, "hotfix-state.json"),
            BaseVersion = tx.BaseVersion,
            RestoreHotfixLevel = tx.PreviousHotfixLevel,
            RestoreLastKnownGood = tx.PreviousLastKnownGood,
        }, null);
        Assert.IsTrue(rollback.Success, rollback.Error);
        File.WriteAllText(prepared.TransactionPath!,
            Shared.MarkRolledBack(File.ReadAllText(prepared.TransactionPath!), DateTime.UtcNow));
        Shared.RecordFailure(updateRoot, tx.BaseVersion, tx.HotfixLevel);

        Assert.AreEqual("v0", File.ReadAllText(Path.Combine(installDir, "a.dll")));
        var state = HotfixStateStore.LoadOrDefault(Path.Combine(updateRoot, "hotfix-state.json"));
        Assert.AreEqual(0, state.HotfixLevel);
        Assert.AreEqual(0, state.LastKnownGoodHotfix);
        Assert.AreEqual(1, Shared.FailureCount(updateRoot, "2.1.0", 1));
        Assert.IsTrue(Shared.TryParseTransaction(File.ReadAllText(prepared.TransactionPath!), out var finalTx, out _));
        Assert.AreEqual(HotfixTransactionStatus.RolledBack, finalTx!.Status);
    }

    private static void HasCount<T>(int expected, System.Collections.Generic.List<T> list)
    {
        if (list.Count != expected)
            Assert.Fail($"expected {expected} items, got {list.Count}");
    }
}
