// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo;

public sealed class InstalledCompilerRecord
{
    public string Version { get; set; } = string.Empty;
    public DateTime InstalledOnUtc { get; set; }
    public string InstalledExePath { get; set; } = string.Empty;
}

public sealed class ManualCompilerRecord
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime AddedOnUtc { get; set; }
    public bool AutoDetected { get; set; }
    public string? CanonicalCompilerId { get; set; }
}

public sealed class ManualCompilerRegistryFile
{
    public Dictionary<string, ManualCompilerRecord> Entries { get; set; } = new();
    public List<string> DismissedAutoDetectIds { get; set; } = new();
}

public sealed record CompilerResolverSpec(string Kind, Dictionary<string, string> Params);

public sealed record CompilerResolution(string Version, string DownloadUrl, string FileName);

internal sealed record CompilerIndexEntry(
    string Id, string Name, string Type, string Author, string Description, string IconUrl,
    CompilerResolverSpec? Resolver,
    string FallbackVersion, string FallbackDownloadUrl, string FallbackFileName,
    string[] FileExtensions, string[] LanguageExtensionIds,
    string? RunCommandTemplate, string? BuildCommandTemplate,
    IReadOnlyDictionary<string, CompilerFileCommands>? FileCommands);

internal sealed record CompilerFileCommands(string? Run, string? Build);

internal sealed record BuiltinCompilerFallback(
    string[] FileExtensions, string[] LanguageExtensionIds,
    string? Run, string? Build,
    IReadOnlyDictionary<string, (string? Run, string? Build)>? FileCommands);

internal sealed class ResolvedCompilerCacheEntry
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime ResolvedUtc { get; set; }
}
