// Licensed under GPL-v3.0
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
    Disabled,
    Incompatible
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
    public LspDependencyStatus ToDependencyStatus() => Source switch
    {
        LspServerSource.Managed or LspServerSource.System or LspServerSource.UserOverride => LspDependencyStatus.Available,
        LspServerSource.Installable => LspDependencyStatus.Missing,
        LspServerSource.Incompatible => LspDependencyStatus.Incompatible,
        LspServerSource.RuntimeMissing => LspDependencyStatus.RuntimeMissing,
        LspServerSource.ManualRequired => LspDependencyStatus.ManualRequired,
        LspServerSource.Disabled => LspDependencyStatus.Disabled,
        _ => LspDependencyStatus.Missing
    };
}
