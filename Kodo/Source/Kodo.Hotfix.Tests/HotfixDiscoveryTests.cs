// Licensed under GPL-v3.0
using Kodo;

namespace Kodo.Hotfix.Tests;

// Phase 2 discovery tests. GitHub/network access is mocked by constructing
// GitHubRelease/GitHubAsset objects directly — no live GitHub calls.
[TestClass]
public sealed class HotfixDiscoveryTests
{
    private static GitHubAsset HotfixAsset(string rid, string tag) =>
        new()
        {
            Name = $"Kodo-hotfix-{tag.Replace('/', '-')}-{rid}.zip",
            BrowserDownloadUrl = $"https://example.invalid/{rid}/{tag.Replace('/', '-')}.zip",
            Size = 1234,
        };

    private static GitHubRelease HotfixRelease(
        string tag,
        string rid = "win-x64",
        bool draft = false,
        bool prerelease = false) =>
        new()
        {
            TagName = tag,
            HtmlUrl = "https://example.invalid/releases/" + tag.Replace('/', '-'),
            Draft = draft,
            Prerelease = prerelease,
            Assets = new[] { HotfixAsset(rid, tag) },
        };

    // --- Tag parsing ---

    [TestMethod]
    public void ValidTag_Parses()
    {
        Assert.IsTrue(HotfixDiscovery.TryParseHotfixTag("hotfix/2.1.0/1", out var b1, out var l1));
        Assert.AreEqual("2.1.0", b1);
        Assert.AreEqual(1, l1);

        Assert.IsTrue(HotfixDiscovery.TryParseHotfixTag("hotfix/2.1.0/12", out _, out var l2));
        Assert.AreEqual(12, l2);

        Assert.IsTrue(HotfixDiscovery.TryParseHotfixTag("Hotfix/2.1.0/2", out _, out _));
        Assert.IsTrue(HotfixDiscovery.TryParseHotfixTag("hotfix/v2.1.0/3", out var b3, out var l3));
        Assert.AreEqual("2.1.0", b3);
        Assert.AreEqual(3, l3);

        Assert.IsTrue(HotfixDiscovery.IsHotfixTag("hotfix/2.1.0/1"));
        Assert.IsFalse(HotfixDiscovery.IsHotfixTag("v2.1.0"));
    }

    [TestMethod]
    public void InvalidTag_Rejected()
    {
        string?[] tags =
        {
            null, "", "   ",
            "2.1.0", "v2.1.0", "v2.2.0",
            "hotfix/2.1.0", "hotfix/2.1.0/0", "hotfix/2.1.0/-1",
            "hotfix/2.1.0/x", "hotfix//1", "hotfix/2.1.0/1/extra",
            "release/2.1.0/1", "hotfix/banana/1", "hotfix/2.1/1x",
        };
        foreach (var tag in tags)
            Assert.IsFalse(HotfixDiscovery.TryParseHotfixTag(tag, out _, out _), $"should reject: '{tag}'");
    }

    // --- Selection ---

    [TestMethod]
    public void NewerHotfix_Selected_WithDownloadInfo()
    {
        var releases = new[]
        {
            HotfixRelease("hotfix/2.1.0/1"),
            HotfixRelease("hotfix/2.1.0/2"),
        };
        var best = HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 1, "win-x64");
        Assert.IsNotNull(best);
        Assert.AreEqual("2.1.0", best.BaseVersion);
        Assert.AreEqual(2, best.HotfixLevel);
        Assert.AreEqual("win-x64", best.PlatformRid);
        Assert.AreEqual("hotfix/2.1.0/2", best.TagName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(best.AssetName));
        Assert.IsFalse(string.IsNullOrWhiteSpace(best.AssetDownloadUrl));
        Assert.IsGreaterThan(0, best.AssetSizeBytes);
    }

    [TestMethod]
    public void MultipleHotfixes_NewestSelected()
    {
        var releases = new[]
        {
            HotfixRelease("hotfix/2.1.0/3"),
            HotfixRelease("hotfix/2.1.0/1"),
            HotfixRelease("hotfix/2.1.0/2"),
        };
        var best = HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 1, "win-x64");
        Assert.IsNotNull(best);
        Assert.AreEqual(3, best.HotfixLevel);
        Assert.AreEqual("hotfix/2.1.0/3", best.TagName);
    }

    [TestMethod]
    public void DifferentBase_Ignored()
    {
        var releases = new[] { HotfixRelease("hotfix/2.2.0/1") };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 0, "win-x64"));
    }

    [TestMethod]
    public void SameLevel_NotOffered()
    {
        var releases = new[] { HotfixRelease("hotfix/2.1.0/3") };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 3, "win-x64"));
    }

    [TestMethod]
    public void OlderHotfix_Ignored()
    {
        var releases = new[] { HotfixRelease("hotfix/2.1.0/1") };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 2, "win-x64"));
    }

    [TestMethod]
    public void Draft_Ignored()
    {
        var releases = new[] { HotfixRelease("hotfix/2.1.0/2", draft: true) };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 1, "win-x64"));
    }

    [TestMethod]
    public void Prerelease_Ignored()
    {
        var releases = new[] { HotfixRelease("hotfix/2.1.0/2", prerelease: true) };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 1, "win-x64"));
    }

    [TestMethod]
    public void WrongPlatform_Ignored()
    {
        var releases = new[] { HotfixRelease("hotfix/2.1.0/2", rid: "win-x64") };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 1, "linux-x64"));
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(releases, "2.1.0", 1, "linux-arm64"));

        var linux = new[] { HotfixRelease("hotfix/2.1.0/2", rid: "linux-arm64") };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(linux, "2.1.0", 1, "win-x64"));
        Assert.IsNotNull(HotfixDiscovery.SelectNewestHotfix(linux, "2.1.0", 1, "linux-arm64"));
    }

    [TestMethod]
    public void NoCompatible_ReturnsNull()
    {
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(Array.Empty<GitHubRelease>(), "2.1.0", 0, "win-x64"));
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(null, "2.1.0", 0, "win-x64"));
        var fullOnly = new[] { new GitHubRelease { TagName = "v2.2.0", Assets = Array.Empty<GitHubAsset>() } };
        Assert.IsNull(HotfixDiscovery.SelectNewestHotfix(fullOnly, "2.1.0", 0, "win-x64"));
    }

    [TestMethod]
    public void MalformedData_Safe()
    {
        GitHubRelease?[] releases =
        {
            null,
            new GitHubRelease { TagName = "" },
            new GitHubRelease { TagName = "hotfix/2.1.0/2", Assets = null },
            new GitHubRelease { TagName = "hotfix/2.1.0/2", Assets = Array.Empty<GitHubAsset>() },
            HotfixRelease("hotfix/2.1.0/2"),
        };
        var best = HotfixDiscovery.SelectNewestHotfix(releases!, "2.1.0", 1, "win-x64");
        Assert.IsNotNull(best);
        Assert.AreEqual(2, best.HotfixLevel);
    }

    // --- Precedence ---

    [TestMethod]
    public void FullRelease_TakesPrecedence()
    {
        var full = new UpdateInfo("v2.2.0", "https://example.invalid/notes", "https://example.invalid/dl", "Kodo-2.2.0-Installer.exe", 10, null);
        var hotfix = new HotfixCandidate("2.1.0", 3, "win-x64", "hotfix/2.1.0/3", "https://example.invalid/notes", "a.zip", "https://example.invalid/a.zip", 1);

        var both = UpdateCheckResult.Create(full, hotfix);
        Assert.AreEqual(UpdateKind.FullRelease, both.Kind);
        Assert.IsNotNull(both.FullRelease);
        Assert.IsNull(both.Hotfix);

        var onlyHotfix = UpdateCheckResult.Create(null, hotfix);
        Assert.AreEqual(UpdateKind.Hotfix, onlyHotfix.Kind);
        Assert.IsNotNull(onlyHotfix.Hotfix);

        var none = UpdateCheckResult.Create(null, null);
        Assert.AreEqual(UpdateKind.None, none.Kind);
        Assert.IsFalse(none.HasUpdate);
    }
}
