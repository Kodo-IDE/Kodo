// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using Kodo.Models;
using Kodo.QuickActions;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionIdentityTests
{
    [Theory]
    [InlineData("cmd.quick-fix", "QuickFix")]
    [InlineData("cmd.rename-symbol", "Rename")]
    [InlineData("cmd.go-to-definition", "Definition")]
    [InlineData("cmd.find-in-project", "ProjectSearch")]
    [InlineData("ToggleLineComment", "ToggleComment")]
    [InlineData("NewTerminalSession", "cmd.new-terminal")]
    public void ForCommand_MapsKnownAliases(string commandId, string expected)
    {
        Assert.Equal(expected, QuickActionIdentity.ForCommand(commandId));
    }

    [Fact]
    public void ForCommand_TrimsAndPassesThroughUnknownIds()
    {
        Assert.Equal("cmd.custom", QuickActionIdentity.ForCommand("  cmd.custom  "));
        Assert.Equal(string.Empty, QuickActionIdentity.ForCommand(null));
        Assert.Equal(string.Empty, QuickActionIdentity.ForCommand("   "));
    }

    [Fact]
    public void IsAliased_RequiresExactTrimmedMatch()
    {
        Assert.True(QuickActionIdentity.IsAliased("cmd.quick-fix"));
        Assert.True(QuickActionIdentity.IsAliased(" cmd.quick-fix "));
        Assert.False(QuickActionIdentity.IsAliased("cmd.new-terminal"));
        Assert.False(QuickActionIdentity.IsAliased("cmd.unknown"));
        Assert.False(QuickActionIdentity.IsAliased(null));
    }

    [Theory]
    [InlineData("cmd.new-terminal", true)]
    [InlineData("cmd.open-active-file-folder-in-terminal", true)]
    [InlineData("cmd.search-in-current-folder", true)]
    [InlineData("cmd.quick-fix", true)]
    [InlineData("cmd.not-observed", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsObservedByExecution_CoversAliasesAndExecutionCommands(string? commandId, bool expected)
    {
        Assert.Equal(expected, QuickActionIdentity.IsObservedByExecution(commandId));
    }

    [Fact]
    public void CoveredCommandIds_ContainsEveryAliasKey()
    {
        Assert.Contains("cmd.quick-fix", QuickActionIdentity.CoveredCommandIds);
        Assert.Contains("ToggleLineComment", QuickActionIdentity.CoveredCommandIds);
        Assert.DoesNotContain("cmd.new-terminal", QuickActionIdentity.CoveredCommandIds);
    }
}

public sealed class QuickActionObservationLogTests
{
    [Fact]
    public void SameActionAndExecution_IsRecordedOnce()
    {
        var log = new QuickActionObservationLog();
        var execution = Guid.NewGuid();

        Assert.True(log.TryRecord("QuickFix", execution));
        Assert.False(log.TryRecord("QuickFix", execution));
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void DistinctExecutions_AreRecordedSeparately()
    {
        var log = new QuickActionObservationLog();

        Assert.True(log.TryRecord("QuickFix", Guid.NewGuid()));
        Assert.True(log.TryRecord("QuickFix", Guid.NewGuid()));
        Assert.True(log.TryRecord("Rename", Guid.NewGuid()));
        Assert.Equal(3, log.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankActionId_IsRejected(string? actionId)
    {
        var log = new QuickActionObservationLog();
        Assert.False(log.TryRecord(actionId!, Guid.NewGuid()));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void ActionIds_AreTrimmed()
    {
        var log = new QuickActionObservationLog();
        var execution = Guid.NewGuid();

        Assert.True(log.TryRecord("  QuickFix  ", execution));
        Assert.False(log.TryRecord("QuickFix", execution));
    }

    [Fact]
    public void OldestEntries_AreEvictedAtCapacity()
    {
        var log = new QuickActionObservationLog();
        var executions = new List<Guid>();
        for (var i = 0; i < 300; i++)
        {
            var execution = Guid.NewGuid();
            executions.Add(execution);
            Assert.True(log.TryRecord("Action" + i, execution));
        }

        Assert.Equal(256, log.Count);
        Assert.True(log.TryRecord("Action0", executions[0]));
        Assert.False(log.TryRecord("Action299", executions[299]));
    }

    [Fact]
    public void Clear_ResetsTrackedExecutions()
    {
        var log = new QuickActionObservationLog();
        var execution = Guid.NewGuid();

        Assert.True(log.TryRecord("QuickFix", execution));
        log.Clear();
        Assert.Equal(0, log.Count);
        Assert.True(log.TryRecord("QuickFix", execution));
    }
}

public sealed class QuickActionObservationPoolTests
{
    private static QuickActionItem CreateAction(string id, string title = "Action") => new()
    {
        Id = id,
        Title = title,
        ContextKey = "editor",
        BaseRelevance = 1,
    };

    [Fact]
    public void Update_PublishesActionsForResolution()
    {
        var pool = new QuickActionObservationPool();
        pool.Update("signature", [CreateAction("QuickFix"), CreateAction("Rename")]);

        Assert.Equal("signature", pool.Signature);
        Assert.Equal(2, pool.Count);
        Assert.Equal("Rename", pool.Resolve("Rename")?.Id);
        Assert.Equal("QuickFix", pool.Resolve("  QuickFix  ")?.Id);
    }

    [Fact]
    public void SignatureChange_ClearsPreviousActions()
    {
        var pool = new QuickActionObservationPool();
        pool.Update("one", [CreateAction("QuickFix")]);
        pool.Update("two", [CreateAction("Rename")]);

        Assert.Null(pool.Resolve("QuickFix"));
        Assert.NotNull(pool.Resolve("Rename"));
    }

    [Fact]
    public void SameSignature_KeepsAccumulatedActions()
    {
        var pool = new QuickActionObservationPool();
        pool.Update("one", [CreateAction("QuickFix")]);
        pool.Update("one", [CreateAction("Rename")]);

        Assert.Equal(2, pool.Count);
        Assert.NotNull(pool.Resolve("QuickFix"));
        Assert.NotNull(pool.Resolve("Rename"));
    }

    [Fact]
    public void BlankIds_AreIgnoredAndUnknownIdsReturnNull()
    {
        var pool = new QuickActionObservationPool();
        pool.Update("signature", [CreateAction(""), CreateAction("   "), CreateAction("QuickFix")]);

        Assert.Equal(1, pool.Count);
        Assert.Null(pool.Resolve("missing"));
        Assert.Null(pool.Resolve(null));
        Assert.Null(pool.Resolve("   "));
    }

    [Fact]
    public void Clear_RemovesSignatureAndActions()
    {
        var pool = new QuickActionObservationPool();
        pool.Update("signature", [CreateAction("QuickFix")]);
        pool.Clear();

        Assert.Null(pool.Signature);
        Assert.Equal(0, pool.Count);
        Assert.Null(pool.Resolve("QuickFix"));
    }
}

public sealed class QuickActionObservationWrapperTests
{
    [Fact]
    public void Wrap_InvokesSuccessThenRecordsWithExecutionId()
    {
        var calls = new List<string>();
        var execution = QuickActionExecution.Begin();
        Guid recorded = default;

        var action = QuickActionObservation.Wrap(
            "QuickFix",
            () => calls.Add("success"),
            (actionId, id) =>
            {
                calls.Add("record:" + actionId);
                recorded = id;
            },
            execution);

        action();

        Assert.Equal(new[] { "success", "record:QuickFix" }, calls);
        Assert.Equal(execution.Id, recorded);
    }

    [Fact]
    public void Wrap_WithoutSuccessStillRecords()
    {
        var recordedIds = new List<Guid>();
        var action = QuickActionObservation.Wrap("Rename", null, (_, id) => recordedIds.Add(id));

        action();

        var id = Assert.Single(recordedIds);
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void Wrap_WithoutExecution_MintsIdentityPerInvocation()
    {
        var recordedIds = new List<Guid>();
        var action = QuickActionObservation.Wrap("Rename", null, (_, id) => recordedIds.Add(id));

        action();
        action();

        Assert.Equal(2, recordedIds.Count);
        Assert.NotEqual(recordedIds[0], recordedIds[1]);
    }

    [Fact]
    public void Wrap_WithPropagatedExecution_ReusesIdentity()
    {
        var recordedIds = new List<Guid>();
        var execution = QuickActionExecution.Begin();
        var action = QuickActionObservation.Wrap("Rename", null, (_, id) => recordedIds.Add(id), execution);

        action();
        action();

        Assert.All(recordedIds, id => Assert.Equal(execution.Id, id));
    }

    [Fact]
    public void Wrap_RequiresRecordCallback()
    {
        Assert.Throws<ArgumentNullException>(() => QuickActionObservation.Wrap("QuickFix", null, null!));
    }
}
