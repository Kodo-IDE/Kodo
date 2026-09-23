// Licensed under GPL-v3.0
using System.IO;
using Kodo;

namespace Kodo.Hotfix.Tests;

[TestClass]
public sealed class HotfixTests
{
    private static string NewTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kodo-hf-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
    }

    private static HotfixManifest ValidManifest() => new()
    {
        SchemaVersion = 1,
        Product = "Kodo",
        BaseVersion = "2.1.0",
        Hotfix = 1,
        MinimumHotfix = 0,
        Platform = "win-x64",
        Files =
        {
            new HotfixFileEntry
            {
                Path = "Kodo.Core.dll",
                Sha256 = new string('a', 64),
            },
        },
    };

    // --- State ---

    [TestMethod]
    public void MissingState_AssumesHF0()
    {
        var path = NewTempPath();
        Assert.IsTrue(HotfixStateStore.TryLoad(path, out var state, out var error), $"error: {error}");
        Assert.IsNotNull(state);
        Assert.AreEqual(0, state.HotfixLevel);
        Assert.IsTrue(HotfixVersion.IsValidBaseVersion(state.BaseVersion));
    }

    [TestMethod]
    public void ValidState_LoadsCorrectly()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, """{"baseVersion":"2.1.0","hotfixLevel":2}""");
            Assert.IsTrue(HotfixStateStore.TryLoad(path, out var state, out var error), $"error: {error}");
            Assert.IsNotNull(state);
            Assert.AreEqual("2.1.0", state.BaseVersion);
            Assert.AreEqual(2, state.HotfixLevel);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [TestMethod]
    public void MalformedState_IsRejected()
    {
        foreach (var json in new[]
        {
            "",
            "not json at all",
            """{"baseVersion":"2.1.0"}""", // missing level defaults to 0 -> still valid? (documents leniency below)
            """{"baseVersion":"","hotfixLevel":0}""",
            """{"baseVersion":"2.1.0","hotfixLevel":-1}""",
            """{"baseVersion":"banana","hotfixLevel":0}""",
        })
        {
            var path = NewTempPath();
            try
            {
                File.WriteAllText(path, json);
                var ok = HotfixStateStore.TryLoad(path, out _, out _);
                if (json == """{"baseVersion":"2.1.0"}""")
                    Assert.IsTrue(ok, "missing hotfixLevel should default to 0, not fail");
                else
                    Assert.IsFalse(ok, $"should reject: {json}");
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }

    [TestMethod]
    public void State_RoundTrip_SaveAndLoad()
    {
        var path = NewTempPath();
        try
        {
            HotfixStateStore.Save(new HotfixState { BaseVersion = "2.1.0", HotfixLevel = 1 }, path);
            Assert.IsTrue(HotfixStateStore.TryLoad(path, out var state, out var error), $"error: {error}");
            Assert.IsNotNull(state);
            Assert.AreEqual("2.1.0", state.BaseVersion);
            Assert.AreEqual(1, state.HotfixLevel);
            Assert.IsFalse(File.Exists(path + ".tmp"), "temp file should be moved, not left behind");
        }
        finally { try { File.Delete(path); } catch { } try { File.Delete(path + ".tmp"); } catch { } }
    }

    // --- Manifest ---

    [TestMethod]
    public void ValidManifest_Passes()
    {
        Assert.IsTrue(HotfixValidator.TryValidate(ValidManifest(), out var error), $"error: {error}");
    }

    [TestMethod]
    public void InvalidManifest_IsRejected()
    {
        var badSchema = ValidManifest(); badSchema.SchemaVersion = 99;
        var badProduct = ValidManifest(); badProduct.Product = "NotKodo";
        var badBase = ValidManifest(); badBase.BaseVersion = "banana";
        var badHotfix = ValidManifest(); badHotfix.Hotfix = 0;
        var badMin = ValidManifest(); badMin.MinimumHotfix = 5;
        var badPlatform = ValidManifest(); badPlatform.Platform = "dos-16bit";
        var emptyFiles = ValidManifest(); emptyFiles.Files.Clear();

        foreach (var m in new[] { badSchema, badProduct, badBase, badHotfix, badMin, badPlatform, emptyFiles })
            Assert.IsFalse(HotfixValidator.TryValidate(m, out _), "expected invalid manifest");
    }

    [TestMethod]
    public void PathTraversal_IsRejected()
    {
        foreach (var p in new[]
        {
            "../evil.dll",
            "sub/../../evil.dll",
            "..\\evil.dll",
            "/etc/passwd",
            "C:\\Windows\\evil.dll",
            "C:/evil.dll",
            "",
            "   ",
        })
        {
            var m = ValidManifest();
            m.Files[0].Path = p;
            Assert.IsFalse(HotfixValidator.TryValidate(m, out _), $"should reject path: '{p}'");
        }
    }

    [TestMethod]
    public void InvalidSha_IsRejected()
    {
        foreach (var sha in new[]
        {
            "",
            "abc",
            new string('z', 64), // non-hex
            new string('a', 63),
            new string('a', 65),
        })
        {
            var m = ValidManifest();
            m.Files[0].Sha256 = sha;
            Assert.IsFalse(HotfixValidator.TryValidate(m, out _), $"should reject sha: '{sha}'");
        }
    }

    // --- Version comparison ---

    [TestMethod]
    public void NewerHotfixSameBase_IsUpdateAvailable()
    {
        Assert.IsTrue(HotfixVersion.IsHotfixUpdateAvailable("2.1.0", 0, "2.1.0", 1));
    }

    [TestMethod]
    public void OlderRemoteHotfix_IsIgnored()
    {
        Assert.IsFalse(HotfixVersion.IsHotfixUpdateAvailable("2.1.0", 2, "2.1.0", 1));
        Assert.IsFalse(HotfixVersion.IsHotfixUpdateAvailable("2.1.0", 1, "2.1.0", 1));
    }

    [TestMethod]
    public void DifferentBase_IsNotHotfixUpdate()
    {
        // 2.2.0 HF0 over 2.1.0 HF2 is a full release, not a hotfix.
        Assert.IsFalse(HotfixVersion.IsHotfixUpdateAvailable("2.1.0", 2, "2.2.0", 0));
        Assert.IsFalse(HotfixVersion.IsHotfixUpdateAvailable("2.1.0", 0, "2.2.0", 1));
        Assert.IsFalse(HotfixVersion.AreSameBaseVersion("2.1.0", "2.2.0"));
        Assert.IsTrue(HotfixVersion.AreSameBaseVersion("2.1.0", "v2.1.0"));
    }
}
