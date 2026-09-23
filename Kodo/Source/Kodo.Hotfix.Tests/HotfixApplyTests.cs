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

// Phase 3 tests: download / verify / stage / apply. All filesystem work uses
// temp directories; the real Kodo installation is never touched. Network is
// mocked via in-memory HttpMessageHandlers.
[TestClass]
public sealed class HotfixApplyTests
{
    private static readonly string Rid = HotfixDiscovery.GetCurrentPlatformRid();

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

    private static HotfixCandidate Candidate(string tag, long size, string platform) => new(
        BaseVersion: "2.1.0",
        HotfixLevel: 2,
        PlatformRid: platform,
        TagName: tag,
        ReleaseNotesUrl: "https://example.invalid/notes",
        AssetName: "pkg.zip",
        AssetDownloadUrl: "https://example.invalid/pkg.zip",
        AssetSizeBytes: size);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public int Calls;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_fn(request));
        }
    }

    private sealed class ThrowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("simulated network failure");
    }

    private static HttpResponseMessage BytesResponse(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    // --- Download ---

    [TestMethod]
    public async Task Download_Success()
    {
        var root = NewTempRoot();
        var payload = new byte[] { 1, 2, 3, 4 };
        var handler = new FakeHandler(_ => BytesResponse(payload));
        var dest = Path.Combine(root, "pkg.zip");
        var candidate = Candidate("hotfix/2.1.0/2", payload.Length, Rid);

        var result = await HotfixStaging.DownloadAsync(new HttpClient(handler), candidate, dest);
        Assert.AreEqual(dest, result);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(dest));
        Assert.IsFalse(File.Exists(dest + ".partial"));
    }

    [TestMethod]
    public async Task Download_Interrupted_LeavesNoArtifacts()
    {
        var root = NewTempRoot();
        var dest = Path.Combine(root, "pkg.zip");
        var candidate = Candidate("hotfix/2.1.0/2", 4, Rid);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            HotfixStaging.DownloadAsync(new HttpClient(new ThrowHandler()), candidate, dest));
        Assert.IsFalse(File.Exists(dest));
        Assert.IsFalse(File.Exists(dest + ".partial"));
    }

    [TestMethod]
    public async Task Download_SizeMismatch_Discarded()
    {
        var root = NewTempRoot();
        var dest = Path.Combine(root, "pkg.zip");
        var handler = new FakeHandler(_ => BytesResponse(new byte[] { 1, 2 }));
        var candidate = Candidate("hotfix/2.1.0/2", 999, Rid); // declared size != actual

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            HotfixStaging.DownloadAsync(new HttpClient(handler), candidate, dest));
        Assert.IsFalse(File.Exists(dest));
        Assert.IsFalse(File.Exists(dest + ".partial"));
    }

    // --- Verification ---

    private static (string PackagePath, HotfixManifest Manifest) WritePackage(
        string root, HotfixManifest manifest, Dictionary<string, byte[]> payload)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, HotfixPackaging.CreatePackage(manifest, payload));
        return (path, manifest);
    }

    [TestMethod]
    public void Verify_ValidPackage()
    {
        var root = NewTempRoot();
        var content = new byte[] { 9, 8, 7 };
        var manifest = Manifest("2.1.0", 2, Rid, ("app/core.dll", content));
        var (path, _) = WritePackage(root, manifest, new Dictionary<string, byte[]> { ["app/core.dll"] = content });

        var result = HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid);
        Assert.IsTrue(result.Ok, result.Error);
        Assert.IsNotNull(result.Manifest);
        Assert.IsTrue(File.Exists(path), "valid package must be kept for staging");
    }

    [TestMethod]
    public void Verify_InvalidPackage_Discarded()
    {
        var root = NewTempRoot();
        var path = Path.Combine(root, "pkg.zip");
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 });

        var result = HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid);
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(File.Exists(path), "invalid package must be discarded");
    }

    [TestMethod]
    public void Verify_InvalidManifest_Discarded()
    {
        var root = NewTempRoot();
        var content = new byte[] { 5 };
        var manifest = Manifest("2.1.0", 2, Rid, ("a.dll", content));
        manifest.Product = "EvilApp";
        var payload = new Dictionary<string, byte[]> { ["a.dll"] = content };
        var (path, _) = WritePackage(root, manifest, payload);

        var result = HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid);
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Verify_HashMismatch_Discarded()
    {
        var root = NewTempRoot();
        var content = new byte[] { 5 };
        var manifest = Manifest("2.1.0", 2, Rid, ("a.dll", content));
        var (path, _) = WritePackage(root, manifest, new Dictionary<string, byte[]> { ["a.dll"] = new byte[] { 6 } }); // different bytes than hashed

        var result = HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid);
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Verify_WrongBase_Discarded()
    {
        var root = NewTempRoot();
        var content = new byte[] { 5 };
        var manifest = Manifest("9.9.9", 1, Rid, ("a.dll", content));
        var (path, _) = WritePackage(root, manifest, new Dictionary<string, byte[]> { ["a.dll"] = content });

        Assert.IsFalse(HotfixStaging.VerifyPackage(path, "2.1.0", 0, Rid).Ok);
    }

    [TestMethod]
    public void Verify_WrongPlatform_Discarded()
    {
        var root = NewTempRoot();
        var other = Rid == "win-x64" ? "linux-x64" : "win-x64";
        var content = new byte[] { 5 };
        var manifest = Manifest("2.1.0", 2, other, ("a.dll", content));
        var (path, _) = WritePackage(root, manifest, new Dictionary<string, byte[]> { ["a.dll"] = content });

        Assert.IsFalse(HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid).Ok);
    }

    [TestMethod]
    public void Verify_OlderHotfix_Discarded()
    {
        var root = NewTempRoot();
        var content = new byte[] { 5 };
        var manifest = Manifest("2.1.0", 1, Rid, ("a.dll", content));
        var (path, _) = WritePackage(root, manifest, new Dictionary<string, byte[]> { ["a.dll"] = content });

        Assert.IsFalse(HotfixStaging.VerifyPackage(path, "2.1.0", 2, Rid).Ok);
    }

    [TestMethod]
    public void Verify_UnsafePath_Discarded()
    {
        var root = NewTempRoot();
        var content = new byte[] { 5 };
        // Manifest itself lists a traversal path.
        var manifest = Manifest("2.1.0", 2, Rid, ("../evil.dll", content));
        var (path, _) = WritePackage(root, manifest, new Dictionary<string, byte[]> { ["../evil.dll"] = content });

        Assert.IsFalse(HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid).Ok);
    }

    [TestMethod]
    public void Verify_UnexpectedExtraFile_Discarded()
    {
        var root = NewTempRoot();
        var content = new byte[] { 5 };
        var manifest = Manifest("2.1.0", 2, Rid, ("a.dll", content));
        var manifestJson = JsonSerializer.Serialize(manifest);
        var raw = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = System.Text.Encoding.UTF8.GetBytes(manifestJson),
            ["a.dll"] = content,
            ["extra/dropper.dll"] = new byte[] { 7 },
        };
        var path = Path.Combine(root, "pkg.zip");
        File.WriteAllBytes(path, HotfixPackaging.CreatePackageRaw(raw));

        Assert.IsFalse(HotfixStaging.VerifyPackage(path, "2.1.0", 1, Rid).Ok);
    }

    // --- Staging / transaction ---

    [TestMethod]
    public async Task Prepare_EndToEnd_StagesTransaction()
    {
        var root = NewTempRoot();
        var content = new byte[] { 1, 2, 3 };
        var manifest = Manifest("2.1.0", 2, Rid, ("lib/a.dll", content));
        var packageBytes = HotfixPackaging.CreatePackage(manifest, new Dictionary<string, byte[]> { ["lib/a.dll"] = content });
        var handler = new FakeHandler(_ => BytesResponse(packageBytes));
        var candidate = Candidate("hotfix/2.1.0/2", packageBytes.Length, Rid);

        var prepared = await HotfixStaging.PrepareAsync(
            candidate, httpClient: new HttpClient(handler),
            updateRootOverride: Path.Combine(root, "update"),
            targetDirOverride: Path.Combine(root, "install"),
            installedBaseOverride: "2.1.0", installedLevelOverride: 1,
            kodoPidOverride: 0);

        Assert.IsFalse(prepared.AlreadyInstalled);
        Assert.IsNotNull(prepared.TransactionPath);
        Assert.IsTrue(File.Exists(prepared.TransactionPath));
        var stageDir = Path.GetDirectoryName(prepared.TransactionPath)!;
        Assert.IsTrue(File.Exists(Path.Combine(stageDir, "package.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(stageDir, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(stageDir, "payload", "lib", "a.dll")));
        Assert.IsTrue(Directory.Exists(Path.Combine(stageDir, "backup")));
        Assert.IsNotNull(prepared.Transaction);
        Assert.AreEqual("hotfix", prepared.Transaction.Kind);
        Assert.AreEqual("staged", prepared.Transaction.Status);
        Assert.AreEqual(2, prepared.Transaction.HotfixLevel);
    }

    [TestMethod]
    public async Task Prepare_AlreadyInstalled_SkipsDownload()
    {
        var root = NewTempRoot();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        var candidate = Candidate("hotfix/2.1.0/2", 10, Rid);

        var prepared = await HotfixStaging.PrepareAsync(
            candidate, httpClient: new HttpClient(handler),
            updateRootOverride: Path.Combine(root, "update"),
            targetDirOverride: Path.Combine(root, "install"),
            installedBaseOverride: "2.1.0", installedLevelOverride: 2,
            kodoPidOverride: 0);

        Assert.IsTrue(prepared.AlreadyInstalled);
        Assert.AreEqual(0, handler.Calls);
    }

    // --- Apply (shared core, temp install dir) ---

    private static HotfixApplyRequest ApplyRequest(
        string root, HotfixManifest manifest, Dictionary<string, byte[]> payloadFiles,
        string? preExistingDestContent = "unset",
        int installedLevel = 1)
    {
        var payloadDir = Path.Combine(root, "payload");
        var backupDir = Path.Combine(root, "backup");
        var targetDir = Path.Combine(root, "install");
        Directory.CreateDirectory(payloadDir);
        Directory.CreateDirectory(targetDir);
        foreach (var (rel, bytes) in payloadFiles)
        {
            var p = Path.Combine(payloadDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        if (preExistingDestContent != "unset")
        {
            foreach (var f in manifest.Files)
            {
                var d = Path.Combine(targetDir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                File.WriteAllText(d, preExistingDestContent);
            }
        }
        return new HotfixApplyRequest
        {
            ManifestJson = JsonSerializer.Serialize(manifest),
            PayloadDir = payloadDir,
            BackupDir = backupDir,
            TargetDir = targetDir,
            StateFilePath = Path.Combine(root, "hotfix-state.json"),
            ExpectedBaseVersion = "2.1.0",
            InstalledHotfixLevel = installedLevel,
            ExpectedPlatformRid = Rid,
        };
    }

    [TestMethod]
    public void Apply_BackupAndReplace()
    {
        var root = NewTempRoot();
        var oldBytes = System.Text.Encoding.UTF8.GetBytes("old");
        var newBytes = System.Text.Encoding.UTF8.GetBytes("new-content");
        var manifest = Manifest("2.1.0", 2, Rid, ("lib/a.dll", newBytes));
        var req = ApplyRequest(root, manifest,
            new Dictionary<string, byte[]> { ["lib/a.dll"] = newBytes },
            preExistingDestContent: "old");

        var result = Shared.Apply(req, null);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("new-content", File.ReadAllText(Path.Combine(root, "install", "lib", "a.dll")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(root, "backup", "lib", "a.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "backup", "backup.json")));
        var state = JsonSerializer.Deserialize<HotfixState>(File.ReadAllText(Path.Combine(root, "hotfix-state.json")))!;
        Assert.AreEqual("2.1.0", state.BaseVersion);
        Assert.AreEqual(2, state.HotfixLevel);
        Assert.HasCount(1, result.BackedUpFiles);
    }

    [TestMethod]
    public void Apply_MissingTarget_CreatedAndRecorded()
    {
        var root = NewTempRoot();
        var newBytes = new byte[] { 42 };
        var manifest = Manifest("2.1.0", 2, Rid, ("fresh/b.dll", newBytes));
        var req = ApplyRequest(root, manifest,
            new Dictionary<string, byte[]> { ["fresh/b.dll"] = newBytes },
            preExistingDestContent: "unset"); // no dest files at all

        var result = Shared.Apply(req, null);
        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(newBytes, File.ReadAllBytes(Path.Combine(root, "install", "fresh", "b.dll")));
        Assert.HasCount(1, result.CreatedFiles);
        Assert.IsEmpty(result.BackedUpFiles);
        var backupIndex = File.ReadAllText(Path.Combine(root, "backup", "backup.json"));
        Assert.IsTrue(backupIndex.Contains("\"existed\": false") || backupIndex.Contains("\"existed\":false"), backupIndex);
    }

    [TestMethod]
    public void Apply_TamperedPayload_FailsWithoutChanges()
    {
        var root = NewTempRoot();
        var realBytes = new byte[] { 1 };
        var manifest = Manifest("2.1.0", 2, Rid, ("lib/a.dll", realBytes));
        var req = ApplyRequest(root, manifest,
            new Dictionary<string, byte[]> { ["lib/a.dll"] = new byte[] { 2 } }, // hash mismatch
            preExistingDestContent: "original");

        var result = Shared.Apply(req, null);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "install", "lib", "a.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "hotfix-state.json")));
    }

    [TestMethod]
    public void Transaction_MarkApplied_RetainsForRollback()
    {
        var txJson = "{\"kind\":\"hotfix\",\"transactionId\":\"abc\",\"status\":\"staged\",\"payloadDir\":\"p\",\"targetDir\":\"t\"}";
        var updated = Shared.MarkApplied(txJson, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.IsTrue(Shared.TryParseTransaction(updated, out var tx, out var error), error);
        Assert.IsNotNull(tx);
        Assert.AreEqual("applied", tx.Status);
        Assert.AreEqual("abc", tx.TransactionId);
    }

    [TestMethod]
    public void RestartTarget_ExistingExe_ReturnsItself()
    {
        var root = NewTempRoot();
        var exe = Path.Combine(root, "Kodo.exe");
        File.WriteAllBytes(exe, new byte[] { 0 });
        Assert.AreEqual(exe, Shared.ResolveRestartTarget(exe));
    }
}
