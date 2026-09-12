// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

public enum LspServerSource
{
    Managed,
    System,
    UserOverride,
    Installable,
    RuntimeMissing,
    ManualRequired,
    Missing,
    Disabled
}

public sealed record LspResolution(
    LspServerSource Source,
    string? ExecutablePath,
    LspConfiguration ResolvedConfiguration,
    string? Version,
    string? Error,
    bool CanInstall)
{
    public bool IsReady => Source == LspServerSource.Managed || Source == LspServerSource.System || Source == LspServerSource.UserOverride;
}

internal static class LspServerResolver
{
    /// <summary>Resolves the best available executable for a provider, in order:
    /// 1. User override (AppSettings)
    /// 2. Managed install
    /// 3. System PATH
    /// 4. Installable / manual / missing
    /// Also checks runtime requirements.
    /// </summary>
    public static async Task<LspResolution> ResolveAsync(
        LoadedExtension extension,
        AppSettings? settings = null,
        CancellationToken ct = default)
    {
        var cfg = extension.Lsp;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Command))
            return new(LspServerSource.Missing, null, cfg ?? new LspConfiguration(), null, "No LSP configured", false);
        // Per-language disabled?
        if (settings != null && settings.LspDisabledLanguages.TryGetValue(extension.Id, out var disabled) && disabled)
            return new(LspServerSource.Disabled, null, cfg, null, "LSP disabled for this language", false);
        if (settings != null && !settings.LspEnabled)
            return new(LspServerSource.Disabled, null, cfg, null, "LSP globally disabled", false);

        // Runtime check first – if missing, report before path checks
        if (!string.IsNullOrWhiteSpace(cfg.Runtime))
        {
            var rt = await LspRuntimeDetector.DetectAsync(cfg.Runtime!, cfg.RuntimeMinVersion, ct).ConfigureAwait(false);
            if (!rt.Found || !string.IsNullOrWhiteSpace(rt.Error))
            {
                return new(LspServerSource.RuntimeMissing, null, cfg, null, rt.Error, false);
            }
        }

        // 1. User override
        if (settings != null && settings.LspExecutableOverrides.TryGetValue(extension.Id, out var ov) && !string.IsNullOrWhiteSpace(ov))
        {
            var trimmed = ov.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                var resolved = CloneWithCommand(cfg, trimmed);
                var ver = await ProbeVersionAsync(trimmed, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.UserOverride, trimmed, resolved, ver, null, false);
            }
            // also try FindOnPath for override value
            var found = LspRuntimeDetector.FindOnPath(trimmed);
            if (found != null)
            {
                var resolved = CloneWithCommand(cfg, found);
                var ver = await ProbeVersionAsync(found, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.UserOverride, found, resolved, ver, null, false);
            }
            // override points to non-existent – treat as error but fallback to other sources
            KodoDiagnostics.LogDebug($"LSP user override for {extension.Id} not found: {trimmed}");
        }

        // 2 & 3: Deterministic preference handling
        bool preferManaged = settings?.LspPreferManaged ?? true;
        bool preferSystem = settings?.LspPreferSystem ?? true;
        // Validate managed presence (requires actual executable, not just dir)
        var managedExe = LspInstallationManager.FindManagedExecutable(cfg, settings);
        bool managedExists = managedExe != null && File.Exists(managedExe);
        string? systemExe = null;
        if (cfg.AllowSystem && preferSystem)
            systemExe = LspInstallationManager.FindSystemExecutable(cfg);
        // Also try .cmd fallback for system
        if (systemExe == null && cfg.AllowSystem && preferSystem && cfg.Command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var without = cfg.Command[..^4];
            systemExe = LspRuntimeDetector.FindOnPath(without);
        }
        bool systemExists = systemExe != null && File.Exists(systemExe);

        // Deterministic order:
        // - UserOverride already returned
        // - If preferManaged && preferSystem => Managed first, then System
        // - If preferManaged && !preferSystem => Managed only
        // - If !preferManaged && preferSystem => System first, then Managed
        // - If !preferManaged && !preferSystem => neither (go to installable)
        if (preferManaged && preferSystem)
        {
            if (managedExists)
            {
                var resolved = CloneWithCommand(cfg, managedExe!);
                var ver = await ProbeVersionAsync(managedExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                // Verify executable actually works (version probe not required to succeed, but file must exist)
                return new(LspServerSource.Managed, managedExe!, resolved, ver, null, false);
            }
            if (systemExists)
            {
                var resolved = CloneWithCommand(cfg, systemExe!);
                var ver = await ProbeVersionAsync(systemExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.System, systemExe!, resolved, ver, null, false);
            }
        }
        else if (preferManaged && !preferSystem)
        {
            if (managedExists)
            {
                var resolved = CloneWithCommand(cfg, managedExe!);
                var ver = await ProbeVersionAsync(managedExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.Managed, managedExe!, resolved, ver, null, false);
            }
            // System explicitly disabled – don't fall back
        }
        else if (!preferManaged && preferSystem)
        {
            if (systemExists)
            {
                var resolved = CloneWithCommand(cfg, systemExe!);
                var ver = await ProbeVersionAsync(systemExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.System, systemExe!, resolved, ver, null, false);
            }
            if (managedExists)
            {
                var resolved = CloneWithCommand(cfg, managedExe!);
                var ver = await ProbeVersionAsync(managedExe!, cfg.VersionArgs, ct).ConfigureAwait(false);
                return new(LspServerSource.Managed, managedExe!, resolved, ver, null, false);
            }
        }
        // If both disabled, skip both and go to installable/manual

        // 4. Installable? Enforce SHA-256 for github artifacts
        bool isGithub = (cfg.InstallMethod?.Equals("github", StringComparison.OrdinalIgnoreCase) ?? false)
            || (cfg.DownloadUrl?.Contains("github.com", StringComparison.OrdinalIgnoreCase) ?? false);
        bool hasSha = !string.IsNullOrWhiteSpace(cfg.Sha256);
        bool canInstall = cfg.AllowAutoInstall && !string.Equals(cfg.InstallMethod, "manual", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(cfg.DownloadUrl) || !string.IsNullOrWhiteSpace(cfg.PackageName) || cfg.InstallMethod == "npm" || cfg.InstallMethod == "github");
        // For github/standalone without checksum, treat as manual – security requirement
        if (canInstall && isGithub && !hasSha)
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' download requires SHA-256 verification (no checksum provided). Please install manually from {cfg.DownloadUrl} or update provider metadata.", false);
        }
        if (canInstall && !isGithub && !hasSha && !string.IsNullOrWhiteSpace(cfg.DownloadUrl) && cfg.InstallMethod != "npm")
        {
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' cannot be auto-installed without SHA-256. Install manually.", false);
        }
        if (canInstall)
            return new(LspServerSource.Installable, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' not installed. Can be installed on demand.", true);
        if (cfg.InstallMethod != null && cfg.InstallMethod.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return new(LspServerSource.ManualRequired, null, cfg, null, $"Language server '{cfg.EffectiveProviderId}' requires manual installation. Ensure '{cfg.Command}' is on PATH.", false);
        return new(LspServerSource.Missing, null, cfg, null, $"Language server '{cfg.Command}' not found on PATH and no install source configured.", false);
    }

    private static LspConfiguration CloneWithCommand(LspConfiguration cfg, string newCommand) => new()
    {
        Command = newCommand,
        Arguments = cfg.Arguments,
        Languages = cfg.Languages,
        FileExtensions = cfg.FileExtensions,
        Env = new Dictionary<string, string>(cfg.Env, StringComparer.OrdinalIgnoreCase),
        WorkingDirectory = cfg.WorkingDirectory,
        InitializationOptions = cfg.InitializationOptions,
        RootMarkers = cfg.RootMarkers,
        ProviderId = cfg.ProviderId,
        DisplayName = cfg.DisplayName,
        Version = cfg.Version,
        InstallMethod = cfg.InstallMethod,
        PackageName = cfg.PackageName,
        DownloadUrl = cfg.DownloadUrl,
        Sha256 = cfg.Sha256,
        Runtime = cfg.Runtime,
        RuntimeMinVersion = cfg.RuntimeMinVersion,
        AllowAutoInstall = cfg.AllowAutoInstall,
        AllowSystem = cfg.AllowSystem,
        VersionArgs = cfg.VersionArgs
    };

    private static async Task<string?> ProbeVersionAsync(string exe, string[] versionArgs, CancellationToken ct)
    {
        try
        {
            var (ok, ver, err) = await LspInstallationManager.TryGetVersionAsync(exe, versionArgs, ct).ConfigureAwait(false);
            if (ok && !string.IsNullOrWhiteSpace(ver))
            {
                // return first line
                var first = ver.Split('\n')[0].Trim();
                return first.Length > 120 ? first[..120] : first;
            }
            return null;
        }
        catch { return null; }
    }

    public static string GetStatusText(LspResolution r)
    {
        return r.Source switch
        {
            LspServerSource.Managed => $"Managed ({r.Version ?? "installed"})",
            LspServerSource.System => $"System ({r.Version ?? "found"})",
            LspServerSource.UserOverride => $"Custom ({r.Version ?? "override"})",
            LspServerSource.Installable => "Not installed – can install",
            LspServerSource.RuntimeMissing => $"Runtime missing: {r.Error}",
            LspServerSource.ManualRequired => "Manual install required",
            LspServerSource.Disabled => "Disabled",
            _ => "Not installed"
        };
    }
}
