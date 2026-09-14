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

public enum InstallResultKind { Success, AlreadyInstalled, Failed, Cancelled, Offline, RuntimeMissing, NotInstallable }

public sealed record InstallResult(InstallResultKind Kind, string? Message, string? InstalledPath);

public sealed record RuntimeInfo(bool Found, string? Version, string? RawOutput, string? Error);

internal sealed record LspRawDiagnostic(int StartLine, int StartChar, int EndLine, int EndChar, string Message, string Severity, string Code, string Source);
