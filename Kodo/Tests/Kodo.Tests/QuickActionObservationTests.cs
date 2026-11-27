// Licensed under the GNU GPL-v3.0
using Kodo.QuickActions;
using System;
using System.Linq;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionObservationTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstObservationIsRecorded()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Now));
    }

    [Fact]
    public void RepeatedObservationInsideTheWindowIsSuppressed()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Now));
        Assert.False(log.TryRecord("Format", Now));
        Assert.False(log.TryRecord("Format", Now.AddMilliseconds(500)));
        Assert.False(log.TryRecord("Format", Now + QuickActionObservationLog.MinimumInterval - TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ObservationAfterTheWindowIsRecorded()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Now));
        Assert.True(log.TryRecord("Format", Now + QuickActionObservationLog.MinimumInterval));
    }

    [Fact]
    public void DistinctActionsAreTrackedSeparately()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Now));
        Assert.True(log.TryRecord("MoveLineUp", Now));
        Assert.True(log.TryRecord("ToggleComment", Now));
    }

    [Fact]
    public void BlankIdentifiersAreIgnored()
    {
        var log = new QuickActionObservationLog();

        Assert.False(log.TryRecord(string.Empty, Now));
        Assert.False(log.TryRecord("   ", Now));
    }

    [Fact]
    public void IdentifiersAreCaseSensitive()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Now));
        Assert.True(log.TryRecord("format", Now));
    }

    [Fact]
    public void ClearAllowsAnImmediateRecord()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Now));
        Assert.False(log.TryRecord("Format", Now));

        log.Clear();

        Assert.True(log.TryRecord("Format", Now));
    }

    [Fact]
    public void TrackedActionsStayBounded()
    {
        var log = new QuickActionObservationLog();

        for (var index = 0; index < 300; index++)
            Assert.True(log.TryRecord($"Action{index}", Now.AddMilliseconds(index)));

        Assert.True(log.TryRecord("Action0", Now.AddSeconds(10)));
        Assert.False(log.TryRecord("Action0", Now.AddSeconds(10)));
    }

    [Fact]
    public void PaletteCommandAndShortcutShareIdentity()
    {
        Assert.Equal("Format", QuickActionIdentity.ForCommand("cmd.format-document"));
        Assert.Equal("Format", QuickActionIdentity.ForCommand("Format"));
        Assert.Equal("ToggleComment", QuickActionIdentity.ForCommand("cmd.toggle-line-comment"));
        Assert.Equal("ToggleComment", QuickActionIdentity.ForCommand("ToggleLineComment"));
        Assert.Equal("ProjectSearch", QuickActionIdentity.ForCommand("cmd.find-in-project"));
        Assert.Equal("ProjectSearch", QuickActionIdentity.ForCommand("FindInProject"));
        Assert.Equal("ProjectTerminal", QuickActionIdentity.ForCommand("cmd.open-workspace-in-terminal"));
    }

    [Fact]
    public void UnknownCommandsKeepTheirOwnIdentity()
    {
        Assert.Equal("plugin.formatter.run", QuickActionIdentity.ForCommand("plugin.formatter.run"));
        Assert.Equal("cmd.toggle-terminal", QuickActionIdentity.ForCommand("cmd.toggle-terminal"));
    }

    [Fact]
    public void CommandIdentifiersAreTrimmedAndBlankSafe()
    {
        Assert.Equal("Format", QuickActionIdentity.ForCommand("  cmd.format-document  "));
        Assert.Equal(string.Empty, QuickActionIdentity.ForCommand(null));
        Assert.Equal(string.Empty, QuickActionIdentity.ForCommand("   "));
    }

    [Fact]
    public void AliasedCommandsAreCoveredSoDiscoverySkipsThem()
    {
        Assert.Contains("cmd.format-document", QuickActionIdentity.CoveredCommandIds);
        Assert.Contains("cmd.open-workspace-in-terminal", QuickActionIdentity.CoveredCommandIds);
        Assert.DoesNotContain("cmd.toggle-terminal", QuickActionIdentity.CoveredCommandIds);
        Assert.DoesNotContain("Format", QuickActionIdentity.CoveredCommandIds);
    }

    [Fact]
    public void AliasedCommandsAreRecordedByTheirOwnExecution()
    {
        Assert.All(QuickActionIdentity.CoveredCommandIds, id => Assert.True(QuickActionIdentity.IsAliased(id)));
        Assert.False(QuickActionIdentity.IsAliased("cmd.toggle-terminal"));
        Assert.False(QuickActionIdentity.IsAliased("Format"));
        Assert.False(QuickActionIdentity.IsAliased(null));
    }

    [Fact]
    public void AliasesNeverPointAtAnotherAliasedCommand()
    {
        Assert.All(QuickActionIdentity.CoveredCommandIds, id =>
        {
            var actionId = QuickActionIdentity.ForCommand(id);
            Assert.NotEqual(id, actionId);
            Assert.False(QuickActionIdentity.CoveredCommandIds.Contains(actionId));
        });
    }
}
