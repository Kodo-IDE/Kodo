// Licensed under GPL-v3.0
using System;
using System.Text.Json.Serialization;

namespace Kodo;

internal sealed record UpdateInfo(
    string Version,
    string ReleaseNotesUrl,
    string AssetDownloadUrl,
    string AssetName,
    long AssetSizeBytes);

internal sealed record UpdateDownloadProgress(double Fraction, string Label);

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
