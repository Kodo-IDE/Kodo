// Licensed under the GNU GPL-v3.0
using Kodo.QuickActions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionObservationWrapperTests
{
    [Fact]
    public void ExecutionIdentityIsCreatedWhenTheObservationRuns()
    {
        var identities = new List<Guid>();
        var wrapper = QuickActionObservation.Wrap("Format", null, (_, execution) => identities.Add(execution));

        Assert.Empty(identities);

        wrapper();

        Assert.Single(identities);
        Assert.NotEqual(Guid.Empty, identities[0]);
    }

    [Fact]
    public void ReusingOneWrapperGivesEachInvocationItsOwnExecution()
    {
        var log = new QuickActionObservationLog();
        var identities = new List<Guid>();
        var wrapper = QuickActionObservation.Wrap("Format", null, (id, execution) =>
        {
            identities.Add(execution);
            log.TryRecord(id, execution);
        });

        wrapper();
        wrapper();

        Assert.Equal(2, identities.Count);
        Assert.NotEqual(identities[0], identities[1]);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void RepeatedCallbacksForOnePropagatedExecutionRecordOnce()
    {
        var log = new QuickActionObservationLog();
        var execution = QuickActionExecution.Begin();
        var wrapper = QuickActionObservation.Wrap("Format", null, (id, token) => log.TryRecord(id, token), execution);

        wrapper();
        wrapper();

        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void SeparateWrapperInstancesRecordSeparateExecutions()
    {
        var log = new QuickActionObservationLog();

        for (var attempt = 0; attempt < 3; attempt++)
            QuickActionObservation.Wrap("MoveLineDown", null, (id, execution) => log.TryRecord(id, execution))();

        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void PropagatedExecutionIsSharedAcrossLayers()
    {
        var log = new QuickActionObservationLog();
        var execution = QuickActionExecution.Begin();
        var quickActionLayer = QuickActionObservation.Wrap("Format", null, (id, token) => log.TryRecord(id, token), execution);
        var editorLayer = QuickActionObservation.Wrap("Format", null, (id, token) => log.TryRecord(id, token), execution);

        quickActionLayer();
        editorLayer();

        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void FailingSuccessCallbackIsNotRecorded()
    {
        var recorded = 0;
        var wrapper = QuickActionObservation.Wrap(
            "Format",
            () => throw new InvalidOperationException("formatter failed"),
            (_, _) => recorded++);

        Assert.Throws<InvalidOperationException>(wrapper);
        Assert.Equal(0, recorded);
    }

    [Fact]
    public void MissingSuccessCallbackStillRecordsTheExecution()
    {
        var log = new QuickActionObservationLog();

        QuickActionObservation.Wrap("CopyProjectPath", null, (id, execution) => log.TryRecord(id, execution))();

        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void BeginProducesDistinctExecutions()
    {
        Assert.NotEqual(QuickActionExecution.Begin().Id, QuickActionExecution.Begin().Id);
    }

    [Fact]
    public async Task RunnerWithoutCallbackStillReportsTheResult()
    {
        var succeeded = await QuickActionRunner.RunAsync(() => Task.FromResult(true));
        var failed = await QuickActionRunner.RunAsync(() => Task.FromResult(false));
        var error = await QuickActionRunner.RunAsync(() => Task.FromException<bool>(new InvalidOperationException("host failed")));

        Assert.True(succeeded.Succeeded);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Error);
        Assert.False(error.Succeeded);
        Assert.IsType<InvalidOperationException>(error.Error);
    }

    [Fact]
    public void ExecutionsOfDifferentActionsAreIndependent()
    {
        var log = new QuickActionObservationLog();
        var execution = QuickActionExecution.Begin();

        Assert.True(log.TryRecord("Format", execution.Id));
        Assert.True(log.TryRecord("MoveLineDown", execution.Id));
    }
}
