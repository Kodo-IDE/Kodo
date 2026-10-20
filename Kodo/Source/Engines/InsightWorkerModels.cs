// Licensed under the GNU GPL-v3.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kodo.Models;

namespace Kodo;

internal sealed class WorkerRequest
{
    [JsonPropertyName("i")]
    public int Id { get; set; }
    [JsonPropertyName("o")]
    public string Op { get; set; } = string.Empty;
    [JsonPropertyName("k")]
    public string? Key { get; set; }
    [JsonPropertyName("e")]
    public WorkerExtensionSnapshot? Extension { get; set; }
    [JsonPropertyName("E")]
    public List<WorkerExtensionSnapshot>? Registry { get; set; }
    [JsonPropertyName("t")]
    public string? Text { get; set; }
    [JsonPropertyName("d")]
    public string? FolderPath { get; set; }
}

internal sealed class WorkerResponse
{
    [JsonPropertyName("i")]
    public int Id { get; set; }
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;
    [JsonPropertyName("m")]
    public string? Message { get; set; }
    [JsonPropertyName("v")]
    public List<string>? Variables { get; set; }
    [JsonPropertyName("s")]
    public List<WorkerSpan>? Dead { get; set; }
    [JsonPropertyName("r")]
    public List<WorkerErrorSpan>? Errors { get; set; }
}

internal sealed class WorkerSpan
{
    [JsonPropertyName("a")]
    public int Start { get; set; }
    [JsonPropertyName("b")]
    public int Length { get; set; }
    [JsonPropertyName("c")]
    public string Reason { get; set; } = string.Empty;
}

internal sealed class WorkerErrorSpan
{
    [JsonPropertyName("a")]
    public int Start { get; set; }
    [JsonPropertyName("b")]
    public int Length { get; set; }
    [JsonPropertyName("c")]
    public string Severity { get; set; } = string.Empty;
    [JsonPropertyName("d")]
    public string Code { get; set; } = string.Empty;
    [JsonPropertyName("e")]
    public string Message { get; set; } = string.Empty;
    [JsonPropertyName("f")]
    public string Source { get; set; } = string.Empty;
}

internal sealed class WorkerExtensionSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("ver")]
    public string Version { get; set; } = string.Empty;
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    [JsonPropertyName("exts")]
    public string[] Extensions { get; set; } = [];
    [JsonPropertyName("kw")]
    public string[] Keywords { get; set; } = [];
    [JsonPropertyName("types")]
    public string[] Types { get; set; } = [];
    [JsonPropertyName("fns")]
    public string[] Functions { get; set; } = [];
    [JsonPropertyName("props")]
    public string[] Properties { get; set; } = [];
    [JsonPropertyName("ns")]
    public string[] Namespaces { get; set; } = [];
    [JsonPropertyName("bl")]
    public string[] Blacklist { get; set; } = [];
    [JsonPropertyName("dci")]
    public string[] DeadCodeIgnore { get; set; } = [];
    [JsonPropertyName("dcep")]
    public string[] DeadCodeEntryPoints { get; set; } = [];
    [JsonPropertyName("cl")]
    public string CommentLine { get; set; } = "//";
    [JsonPropertyName("cbs")]
    public string CommentBlockStart { get; set; } = "/*";
    [JsonPropertyName("cbe")]
    public string CommentBlockEnd { get; set; } = "*/";
    [JsonPropertyName("sd")]
    public string[] StringDelimiters { get; set; } = ["\"", "'"];
    [JsonPropertyName("msd")]
    public string[] MultiLineStringDelimiters { get; set; } = [];
    [JsonPropertyName("nosq")]
    public bool DisableSingleQuoteStrings { get; set; }
    [JsonPropertyName("sem")]
    public bool EnableSemanticDiagnostics { get; set; }
    [JsonPropertyName("lpf")]
    public string? LanguagePluginFolderPath { get; set; }
    [JsonPropertyName("laf")]
    public string? LanguagePluginAssemblyFileName { get; set; }
    [JsonPropertyName("spx")]
    public string[][] SyntaxProfileExtensions { get; set; } = [];

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static WorkerExtensionSnapshot From(LoadedExtension ext)
    {
        return new WorkerExtensionSnapshot
        {
            Id = ext.Id,
            Version = ext.Version,
            Name = ext.Name,
            Type = ext.Type,
            Extensions = ext.Extensions ?? [],
            Keywords = ext.Keywords ?? [],
            Types = ext.Types ?? [],
            Functions = ext.Functions ?? [],
            Properties = ext.Properties ?? [],
            Namespaces = ext.Namespaces ?? [],
            Blacklist = ext.Blacklist ?? [],
            DeadCodeIgnore = ext.DeadCodeIgnore ?? [],
            DeadCodeEntryPoints = ext.DeadCodeEntryPoints ?? [],
            CommentLine = ext.CommentLine,
            CommentBlockStart = ext.CommentBlockStart,
            CommentBlockEnd = ext.CommentBlockEnd,
            StringDelimiters = ext.StringDelimiters ?? ["\"", "'"],
            MultiLineStringDelimiters = ext.MultiLineStringDelimiters ?? [],
            DisableSingleQuoteStrings = ext.DisableSingleQuoteStrings,
            EnableSemanticDiagnostics = ext.EnableSemanticDiagnostics,
            LanguagePluginFolderPath = ext.LanguagePluginFolderPath,
            LanguagePluginAssemblyFileName = ext.LanguagePluginAssemblyFileName,
            SyntaxProfileExtensions = [.. ext.SyntaxProfiles.Select(p => p.Extensions ?? [])]
        };
    }

    public LoadedExtension ToLoadedExtension()
    {
        var result = new LoadedExtension
        {
            Id = Id,
            Version = Version,
            Name = Name,
            Type = Type,
            Extensions = Extensions,
            Keywords = Keywords,
            Types = Types,
            Functions = Functions,
            Properties = Properties,
            Namespaces = Namespaces,
            Blacklist = Blacklist,
            DeadCodeIgnore = DeadCodeIgnore,
            DeadCodeEntryPoints = DeadCodeEntryPoints,
            CommentLine = CommentLine,
            CommentBlockStart = CommentBlockStart,
            CommentBlockEnd = CommentBlockEnd,
            StringDelimiters = StringDelimiters,
            MultiLineStringDelimiters = MultiLineStringDelimiters,
            DisableSingleQuoteStrings = DisableSingleQuoteStrings,
            EnableSemanticDiagnostics = EnableSemanticDiagnostics,
            LanguagePluginFolderPath = LanguagePluginFolderPath,
            LanguagePluginAssemblyFileName = LanguagePluginAssemblyFileName,
            LangRules = LangRulesAdapter.TryLoad(LanguagePluginFolderPath, LanguagePluginAssemblyFileName)
        };
        for (var i = 0; i < SyntaxProfileExtensions.Length; i++)
            result.SyntaxProfiles.Add(new LanguageSyntaxProfile { Extensions = SyntaxProfileExtensions[i] ?? [] });
        return result;
    }
}
