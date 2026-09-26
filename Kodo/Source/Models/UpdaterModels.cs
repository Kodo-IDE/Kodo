// Licensed under the GNU GPL-v3.0
using System;
using System.Text.Json.Serialization;

namespace Kodo;

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
    string? AssetName = null);

internal sealed record UpdateInfo(
    string Version,
    string ReleaseNotesUrl,
    string AssetDownloadUrl,
    string AssetName,
    long AssetSizeBytes,
    string? Sha256 = null);

internal sealed record UpdateDownloadProgress(double Fraction, string Label);

internal enum UpdateKind
{
    None,
    FullRelease,
    Hotfix,
}

internal sealed record UpdateCheckResult(
    UpdateKind Kind,
    UpdateInfo? FullRelease,
    HotfixCandidate? Hotfix)
{
    public static readonly UpdateCheckResult None = new(UpdateKind.None, null, null);

    public bool HasUpdate => Kind != UpdateKind.None;

    public static UpdateCheckResult Create(UpdateInfo? fullRelease, HotfixCandidate? hotfix) =>
        fullRelease is not null
            ? new UpdateCheckResult(UpdateKind.FullRelease, fullRelease, null)
            : hotfix is not null
                ? new UpdateCheckResult(UpdateKind.Hotfix, null, hotfix)
                : None;
}

internal sealed class AutoUpdateSettings
{
    public bool AutoUpdateAppEnabled { get; set; } = true;
    public bool AutoUpdateAppInBackgroundEnabled { get; set; }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public GitHubAsset[]? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
