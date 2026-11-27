// Licensed under the GNU GPL-v3.0
using Kodo.Insight;
using Kodo.Models;
using Kodo.QuickActions;
using System;
using System.Linq;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionLearningTests
{
    private static readonly DateTime Now = new(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc);

    private static QuickActionItem ProjectTerminalInEditorContext() =>
        ActionCatalog.Create("ProjectTerminal", "editor|.cs", 60, "category:editor", "file:.cs");

    [Fact]
    public void Adjustment_AppliesToTheFinalScore()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var candidates = new[]
        {
            ActionCatalog.Create("Frequent", "project", 60, "category:project"),
            ActionCatalog.Create("Baseline", "editor|.cs", 85, "category:editor"),
        };

        for (var use = 0; use < 7; use++)
            store.RecordQuickActionSelection("Frequent", "project", Now.AddMinutes(use), learningEnabled: true, ["category:project"]);

        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now.AddMinutes(30), maximum: 2);

        Assert.Equal("Frequent", ranked[0].Id);
    }

    [Fact]
    public void MaximumAdjustment_IsThirty()
    {
        Assert.Equal(30, InsightLearningStore.MaximumQuickActionAdjustment);
    }

    [Fact]
    public void Learning_CannotOutrankHigherPrioritySignals()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var candidates = new[]
        {
            ActionCatalog.Create("Frequent", "project", 60, "category:project"),
            ActionCatalog.Create("Diagnostic", "diagnostic|.cs", 120, "category:diagnostic"),
        };

        for (var use = 0; use < 300; use++)
            store.RecordQuickActionSelection("Frequent", "project", Now.AddMinutes(use), learningEnabled: true, ["category:project"]);

        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now.AddMinutes(400), maximum: 2);

        Assert.Equal("Diagnostic", ranked[0].Id);
        Assert.InRange(store.GetQuickActionAdjustment("Frequent", "project", Now.AddMinutes(400)), 0, InsightLearningStore.MaximumQuickActionAdjustment);
    }

    [Fact]
    public void IrrelevantContext_DoesNotPromoteAnAction()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var inEditorContext = ProjectTerminalInEditorContext();
        var baseline = ActionCatalog.Create("Format", "format|.cs", 85, "category:format", "file:.cs");

        for (var use = 0; use < 60; use++)
            store.RecordQuickActionSelection(
                "ProjectTerminal", "project", Now.AddMinutes(use), learningEnabled: true,
                ["category:project", "command-category:project"]);

        Assert.Equal(0, store.GetQuickActionAdjustment("ProjectTerminal", "editor|.cs", Now.AddMinutes(70)));

        var ranked = store.RankQuickActions([inEditorContext, baseline], learningEnabled: true, Now.AddMinutes(70), maximum: 2);

        Assert.Equal("Format", ranked[0].Id);
    }

    [Fact]
    public void SingleExecution_CreatesOnePreferenceEntry()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now, learningEnabled: true, terminal.LearningFeatures);

        var preference = Assert.Single(store.GetQuickActionPreferences(Now));
        Assert.Equal("ProjectTerminal", preference.ActionId);
        Assert.Equal("project", preference.ContextKey);
    }

    [Fact]
    public void SingleExecution_KeepsAdjustmentSmall()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now, learningEnabled: true, terminal.LearningFeatures);

        var adjustment = store.GetQuickActionAdjustment("ProjectTerminal", "project", Now);

        Assert.InRange(adjustment, 0, InsightLearningStore.MaximumQuickActionAdjustment / 3);
    }

    [Fact]
    public void DiscoveredCommands_DoNotOutrankContextualActionsBeforeLearning()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var discovered = QuickActionDiscovery.Discover(
            [new QuickActionCommandRegistration("cmd.new-terminal", "New Terminal", "Terminal", string.Empty, null, () => System.Threading.Tasks.Task.CompletedTask)],
            new QuickActionDiscoveryContext(".cs", true, true, true));

        var candidates = ActionCatalog.EditingContext().Concat(discovered.Select(entry => entry.Item)).ToList();
        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now, maximum: 3);

        Assert.Equal(new[] { "Format", "MoveLineUp", "MoveLineDown" }, ranked.Select(action => action.Id));
    }

    [Fact]
    public void LearnedDiscoveredCommand_SurfacesInItsOwnContext()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var discovered = QuickActionDiscovery.Discover(
            [new QuickActionCommandRegistration("cmd.new-terminal", "New Terminal", "Terminal", string.Empty, null, () => System.Threading.Tasks.Task.CompletedTask)],
            new QuickActionDiscoveryContext(".cs", true, true, true));
        var command = Assert.Single(discovered).Item;

        for (var use = 0; use < 15; use++)
            store.RecordQuickActionSelection(command.Id, command.ContextKey, Now.AddMinutes(use), learningEnabled: true, command.LearningFeatures);

        var candidates = ActionCatalog.EditingContext().Concat([command]).ToList();
        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now.AddMinutes(20), maximum: 3);

        Assert.Equal("cmd.new-terminal", ranked[0].Id);
    }

    [Fact]
    public void LearnedDiscoveredCommand_DoesNotSurfaceInAnotherFileType()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var discovered = QuickActionDiscovery.Discover(
            [new QuickActionCommandRegistration("cmd.new-terminal", "New Terminal", "Terminal", string.Empty, null, () => System.Threading.Tasks.Task.CompletedTask)],
            new QuickActionDiscoveryContext(".cs", true, true, true));
        var command = Assert.Single(discovered).Item;

        for (var use = 0; use < 15; use++)
            store.RecordQuickActionSelection(command.Id, command.ContextKey, Now.AddMinutes(use), learningEnabled: true, command.LearningFeatures);

        var pythonCommand = ActionCatalog.Create("cmd.new-terminal", "command|terminal|.py", 58, "category:command", "command-category:terminal", "file:.py");
        var candidates = new[]
        {
            ActionCatalog.Create("Format", "format|.py", 85, "category:format", "file:.py"),
            pythonCommand,
        };

        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now.AddMinutes(20), maximum: 2);

        Assert.Equal("Format", ranked[0].Id);
    }

    [Fact]
    public void CrossFileTypePromotion_RequiresTheLearnedFileType()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var formatInCs = ActionCatalog.Create("Format", "format|.cs", 70, "category:format", "file:.cs");

        for (var use = 0; use < 30; use++)
            store.RecordQuickActionSelection(formatInCs.Id, formatInCs.ContextKey, Now.AddMinutes(use), learningEnabled: true, formatInCs.LearningFeatures);

        var sameFileType = store.RankQuickActions(
            [ActionCatalog.Create("ToggleComment", "editor|.cs", 80, "category:editor", "file:.cs"), formatInCs],
            learningEnabled: true, Now.AddMinutes(40), maximum: 1);
        var otherFileType = store.RankQuickActions(
            [ActionCatalog.Create("ToggleComment", "editor|.py", 80, "category:editor", "file:.py"),
             ActionCatalog.Create("Format", "format|.py", 70, "category:format", "file:.py")],
            learningEnabled: true, Now.AddMinutes(40), maximum: 1);

        Assert.Equal("Format", sameFileType[0].Id);
        Assert.Equal("ToggleComment", otherFileType[0].Id);
    }
}
