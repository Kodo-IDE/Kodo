// Licensed under the GNU GPL-v3.0
using Kodo.QuickActions;
using System;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionObservationTests
{
    [Fact]
    public void FirstObservationOfAnExecutionIsRecorded()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Guid.NewGuid()));
    }

    [Fact]
    public void OneExecutionReportedBySeveralLayersIsRecordedOnce()
    {
        var log = new QuickActionObservationLog();
        var execution = Guid.NewGuid();

        Assert.True(log.TryRecord("Format", execution));
        Assert.False(log.TryRecord("Format", execution));
        Assert.False(log.TryRecord(" Format ", execution));
    }

    [Fact]
    public void SeparateExecutionsOfTheSameActionAreEachRecorded()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("MoveLineDown", Guid.NewGuid()));
        Assert.True(log.TryRecord("MoveLineDown", Guid.NewGuid()));
        Assert.True(log.TryRecord("MoveLineDown", Guid.NewGuid()));
    }

    [Fact]
    public void DistinctActionsAreTrackedSeparately()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Guid.NewGuid()));
        Assert.True(log.TryRecord("MoveLineUp", Guid.NewGuid()));
        Assert.True(log.TryRecord("ToggleComment", Guid.NewGuid()));
    }

    [Fact]
    public void BlankIdentifiersAreIgnored()
    {
        var log = new QuickActionObservationLog();

        Assert.False(log.TryRecord(string.Empty, Guid.NewGuid()));
        Assert.False(log.TryRecord("   ", Guid.NewGuid()));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void IdentifiersAreCaseSensitive()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("Format", Guid.NewGuid()));
        Assert.True(log.TryRecord("format", Guid.NewGuid()));
    }

    [Fact]
    public void ClearAllowsAnImmediateRecordOfTheSameExecution()
    {
        var log = new QuickActionObservationLog();
        var execution = Guid.NewGuid();

        Assert.True(log.TryRecord("Format", execution));
        Assert.False(log.TryRecord("Format", execution));

        log.Clear();

        Assert.True(log.TryRecord("Format", execution));
    }

    [Fact]
    public void TrackedExecutionsStayBounded()
    {
        var log = new QuickActionObservationLog();
        var firstExecution = Guid.NewGuid();

        Assert.True(log.TryRecord("Action0", firstExecution));
        for (var index = 1; index < 300; index++)
            Assert.True(log.TryRecord($"Action{index}", Guid.NewGuid()));

        Assert.Equal(256, log.Count);
        Assert.True(log.TryRecord("Action0", firstExecution));
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
        Assert.DoesNotContain("cmd.new-terminal", QuickActionIdentity.CoveredCommandIds);
        Assert.DoesNotContain("Format", QuickActionIdentity.CoveredCommandIds);
    }

    [Fact]
    public void CommandsWithTheirOwnExecutionReportAreNotRecordedByThePalette()
    {
        Assert.All(QuickActionIdentity.CoveredCommandIds, id => Assert.True(QuickActionIdentity.IsObservedByExecution(id)));

        Assert.True(QuickActionIdentity.IsObservedByExecution("cmd.new-terminal"));
        Assert.True(QuickActionIdentity.IsObservedByExecution("cmd.open-active-file-folder-in-terminal"));
        Assert.True(QuickActionIdentity.IsObservedByExecution("cmd.search-in-current-folder"));
        Assert.False(QuickActionIdentity.IsObservedByExecution("cmd.toggle-terminal"));
        Assert.False(QuickActionIdentity.IsObservedByExecution("plugin.formatter.run"));
        Assert.False(QuickActionIdentity.IsObservedByExecution("Format"));
        Assert.False(QuickActionIdentity.IsObservedByExecution(null));
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
