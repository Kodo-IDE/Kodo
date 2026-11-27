// Licensed under the GNU GPL-v3.0
using Kodo.QuickActions;
using System;
using System.Linq;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionObservationPipelineTests
{
    private const string DocumentSignature = "/workspace/Program.cs|True|True";

    [Fact]
    public void PoolResolvesAnActionThatIsNotAmongTheDisplayedSuggestions()
    {
        var pool = new QuickActionObservationPool();
        pool.Update(DocumentSignature, ActionCatalog.EditingContext());

        var ranked = ActionCatalog.EditingContext().Take(3).Select(action => action.Id).ToHashSet();
        var observed = pool.Resolve("DeleteLine");

        Assert.NotNull(observed);
        Assert.DoesNotContain("DeleteLine", ranked);
        Assert.Equal("editor|.cs", observed!.ContextKey);
    }

    [Fact]
    public void PoolKeepsActionsObservedEarlierInTheSameContext()
    {
        var pool = new QuickActionObservationPool();
        var candidates = ActionCatalog.EditingContext();

        pool.Update(DocumentSignature, candidates);

        pool.Update(DocumentSignature, candidates.Where(action => action.Id != "DeleteLine"));

        Assert.NotNull(pool.Resolve("DeleteLine"));
        Assert.Equal(candidates.Count, pool.Count);
    }

    [Fact]
    public void PoolIsReplacedWhenTheDocumentChanges()
    {
        var pool = new QuickActionObservationPool();

        pool.Update(DocumentSignature, ActionCatalog.EditingContext());
        pool.Update("/workspace/Notes.md|True|True", ActionCatalog.WorkspaceClosedContext());

        Assert.Null(pool.Resolve("DeleteLine"));
        Assert.NotNull(pool.Resolve("Format"));
        Assert.Equal(ActionCatalog.WorkspaceClosedContext().Count, pool.Count);
    }

    [Fact]
    public void PoolTracksPluginCommandsThroughRegistrationAndRemoval()
    {
        var pool = new QuickActionObservationPool();
        var pluginCommand = ActionCatalog.Create("plugin.formatter.run", "plugin|.cs", 40, "category:plugin");

        pool.Update(DocumentSignature, ActionCatalog.EditingContext());
        Assert.Null(pool.Resolve(pluginCommand.Id));

        pool.Update(DocumentSignature, ActionCatalog.EditingContext().Append(pluginCommand));
        Assert.NotNull(pool.Resolve(pluginCommand.Id));

        pool.Update("/workspace/Other.cs|True|True", ActionCatalog.EditingContext());
        Assert.Null(pool.Resolve(pluginCommand.Id));
    }

    [Fact]
    public void PoolClearsWhenQuickActionsAreDisabled()
    {
        var pool = new QuickActionObservationPool();
        pool.Update(DocumentSignature, ActionCatalog.EditingContext());

        pool.Clear();

        Assert.Equal(0, pool.Count);
        Assert.Null(pool.Signature);
        Assert.Null(pool.Resolve("Format"));
    }

    [Fact]
    public void PoolRejectsBlankIdentifiersOnResolution()
    {
        var pool = new QuickActionObservationPool();
        pool.Update(DocumentSignature, ActionCatalog.EditingContext());

        Assert.Null(pool.Resolve(null));
        Assert.Null(pool.Resolve("   "));
        Assert.Null(pool.Resolve("Unknown"));
        Assert.NotNull(pool.Resolve("  Format  "));
    }

    [Fact]
    public void OneExecutionReportedByPaletteAndSharedFunctionCountsOnce()
    {
        var pool = new QuickActionObservationPool();
        var log = new QuickActionObservationLog();
        var execution = Guid.NewGuid();

        pool.Update(DocumentSignature, ActionCatalog.EditingContext());

        var paletteId = QuickActionIdentity.ForCommand("cmd.format-document");
        var quickActionId = QuickActionIdentity.ForCommand("Format");
        var action = pool.Resolve(paletteId);

        Assert.NotNull(action);
        Assert.Equal("Format", action!.Id);
        Assert.Equal(action.Id, quickActionId);

        Assert.True(log.TryRecord(action.Id, execution));
        Assert.False(log.TryRecord(quickActionId, execution));
    }

    [Fact]
    public void SeparateInvocationsOfTheSameActionBothCount()
    {
        var pool = new QuickActionObservationPool();
        var log = new QuickActionObservationLog();

        pool.Update(DocumentSignature, ActionCatalog.EditingContext());

        var first = pool.Resolve("MoveLineDown");
        var second = pool.Resolve("MoveLineDown");

        Assert.NotNull(first);
        Assert.Same(second, first);
        Assert.True(log.TryRecord(first!.Id, Guid.NewGuid()));
        Assert.True(log.TryRecord(first.Id, Guid.NewGuid()));
    }

    [Fact]
    public void UnresolvableActionsAreDroppedInsteadOfLearnedUnderAGuess()
    {
        var pool = new QuickActionObservationPool();
        pool.Update("/workspace/Notes.md|False|False", ActionCatalog.WorkspaceClosedContext());

        Assert.Null(pool.Resolve(QuickActionIdentity.ForCommand("cmd.delete-line")));
    }
}
