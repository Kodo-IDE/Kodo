// Licensed under the GNU GPL-v3.0
using System;
using GitSourceControl;

namespace Kodo;

public partial class MainWindow
{
    private GitSourceControlIntegration? _gitSourceControlIntegration;

    private void UpdateGitSourceControlLifecycle()
    {
        if (!IsGitSourceControlEnabled)
        {
            StopGitSourceControl();
            return;
        }

        if (_gitSourceControlIntegration is not null)
            return;

        var integration = new GitSourceControlIntegration();
        try
        {
            integration.OnLoad(this);
            _gitSourceControlIntegration = integration;
        }
        catch (Exception ex)
        {
            try { integration.OnUnload(); } catch { }
            KodoDiagnostics.LogDebug("Git Source Control failed to start", ex);
        }
    }

    private void StopGitSourceControl()
    {
        var integration = _gitSourceControlIntegration;
        _gitSourceControlIntegration = null;
        if (integration is null)
            return;

        try { integration.OnUnload(); }
        catch (Exception ex) { KodoDiagnostics.LogDebug("Git Source Control failed to stop cleanly", ex); }
    }
}
