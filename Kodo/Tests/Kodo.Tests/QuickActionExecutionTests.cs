// Licensed under the GNU GPL-v3.0
using Kodo.QuickActions;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionExecutionTests
{
    [Fact]
    public async Task SuccessfulAction_RecordsExactlyOnce()
    {
        var records = 0;

        var result = await QuickActionRunner.RunAsync(() => Task.FromResult(true), () => records++);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(1, records);
    }

    [Fact]
    public async Task UnsuccessfulAction_DoesNotRecord()
    {
        var records = 0;

        var result = await QuickActionRunner.RunAsync(() => Task.FromResult(false), () => records++);

        Assert.False(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(0, records);
    }

    [Fact]
    public async Task TerminalCreationFailure_DoesNotRecord()
    {
        var records = 0;
        var workspaceOpen = true;
        var terminalAvailable = true;

        var result = await QuickActionRunner.RunAsync(
            () => Task.FromResult(workspaceOpen && terminalAvailable),
            () => records++);

        Assert.True(result.Succeeded);
        Assert.Equal(1, records);

        terminalAvailable = false;
        var failed = await QuickActionRunner.RunAsync(
            () => Task.FromResult(workspaceOpen && terminalAvailable),
            () => records++);

        Assert.False(failed.Succeeded);
        Assert.Equal(1, records);
    }

    [Fact]
    public async Task ThrowingAction_ReportsTheErrorWithoutRecording()
    {
        var records = 0;
        var failure = new InvalidOperationException("terminal host failed");

        var result = await QuickActionRunner.RunAsync(() => Task.FromException<bool>(failure), () => records++);

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error);
        Assert.Equal(0, records);
    }

    [Fact]
    public async Task RepeatedExecutions_RecordEachRunOnce()
    {
        var records = 0;

        for (var attempt = 0; attempt < 3; attempt++)
            await QuickActionRunner.RunAsync(() => Task.FromResult(true), () => records++);

        Assert.Equal(3, records);
    }
}
