// Licensed under the GNU GPL-v3.0
using Kodo.Insight;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionRankingTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WithoutHistory_RanksByBaseRelevance()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var actions = new[]
        {
            ActionCatalog.Create("Low", "editor|.cs", 60),
            ActionCatalog.Create("High", "project", 120),
            ActionCatalog.Create("Mid", "editor|.cs", 80),
        };

        var ranked = store.RankQuickActions(actions, learningEnabled: true, Now, maximum: 3);

        Assert.Equal(new[] { "High", "Mid", "Low" }, ranked.Select(action => action.Id));
    }

    [Fact]
    public void WithoutHistory_RespectsSlotLimit()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var actions = Enumerable.Range(0, 5)
            .Select(index => ActionCatalog.Create($"Action{index}", "editor|.cs", 100 - index))
            .ToArray();

        var ranked = store.RankQuickActions(actions, learningEnabled: true, Now, maximum: 3);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(new[] { "Action0", "Action1", "Action2" }, ranked.Select(action => action.Id));
    }

    [Fact]
    public void EqualScores_KeepRegistrationOrder()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var actions = new[]
        {
            ActionCatalog.Create("First", "editor|.cs", 70),
            ActionCatalog.Create("Second", "editor|.cs", 70),
            ActionCatalog.Create("Third", "editor|.cs", 70),
        };

        var ranked = store.RankQuickActions(actions, learningEnabled: true, Now, maximum: 2);

        Assert.Equal(new[] { "First", "Second" }, ranked.Select(action => action.Id));
    }

    [Fact]
    public void RepeatedSuccess_InSameContext_SurfacesAction()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        for (var use = 0; use < 5; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);

        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now.AddMinutes(30), maximum: 3);

        Assert.Equal("ProjectTerminal", ranked[0].Id);
    }

    [Fact]
    public void SingleInvocation_DoesNotDominate()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now, learningEnabled: true, terminal.LearningFeatures);

        var ranked = store.RankQuickActions(candidates, learningEnabled: true, Now, maximum: 3);

        Assert.DoesNotContain(ranked, action => action.Id == "ProjectTerminal");
        Assert.InRange(store.GetQuickActionAdjustment("ProjectTerminal", "project", Now), 0.1, 10);
    }

    [Fact]
    public void Contexts_AreLearnedIndependently()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 6; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);

        Assert.True(store.GetQuickActionAdjustment("ProjectTerminal", "project", Now) > 0);
        Assert.Equal(0, store.GetQuickActionAdjustment("ProjectTerminal", "editor|.cs", Now));
    }

    [Fact]
    public void Recency_DecaysPreference()
    {
        using var oldFile = new TempLearningFile();
        using var recentFile = new TempLearningFile();
        var oldStore = new InsightLearningStore(oldFile.FilePath);
        var recentStore = new InsightLearningStore(recentFile.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 6; use++)
        {
            oldStore.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddDays(-400).AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);
            recentStore.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);
        }

        var stale = oldStore.GetQuickActionAdjustment("ProjectTerminal", "project", Now);
        var fresh = recentStore.GetQuickActionAdjustment("ProjectTerminal", "project", Now);

        Assert.InRange(stale, 0, 1);
        Assert.True(fresh > 20);
    }

    [Fact]
    public void Adjustment_IsCapped()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 300; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);

        var adjustment = store.GetQuickActionAdjustment("ProjectTerminal", "project", Now.AddMinutes(400));

        Assert.InRange(adjustment, InsightLearningStore.MaximumQuickActionAdjustment - 1, InsightLearningStore.MaximumQuickActionAdjustment);
    }

    [Fact]
    public void UnavailableActions_AreNeverRanked()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 20; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);

        var ranked = store.RankQuickActions(ActionCatalog.WorkspaceClosedContext(), learningEnabled: true, Now.AddMinutes(30), maximum: 3);

        Assert.DoesNotContain(ranked, action => action.Id == "ProjectTerminal");
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void DisabledPersonalization_KeepsBaseOrder()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        for (var use = 0; use < 10; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);

        var ranked = store.RankQuickActions(candidates, learningEnabled: false, Now.AddMinutes(30), maximum: 3);

        Assert.Equal("Format", ranked[0].Id);
        Assert.DoesNotContain(ranked, action => action.Id == "ProjectTerminal");
    }

    [Fact]
    public void DisabledLearning_DoesNotRecord()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now, learningEnabled: false, terminal.LearningFeatures);

        Assert.False(store.HasQuickActionData);
        Assert.Equal(0, store.GetQuickActionAdjustment("ProjectTerminal", "project", Now));
    }

    [Fact]
    public void Reset_ClearsLearnedPreferences()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now, learningEnabled: true, terminal.LearningFeatures);
        Assert.True(store.HasQuickActionData);

        store.ResetQuickActions();

        Assert.False(store.HasQuickActionData);
        Assert.Equal(0, store.GetQuickActionAdjustment("ProjectTerminal", "project", Now));
    }

    [Fact]
    public void Persistence_RestoresLearnedPreferences()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 4; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);
        store.Save();

        var restored = InsightLearningStore.Load(file.FilePath);

        Assert.False(restored.LoadFailed);
        Assert.True(restored.HasQuickActionData);
        Assert.True(restored.GetQuickActionAdjustment("ProjectTerminal", "project", Now) > 20);
    }

    [Fact]
    public void CorruptFile_LoadsEmptyWithoutThrowing()
    {
        using var file = new TempLearningFile();
        File.WriteAllText(file.FilePath, "{ this is not valid json");

        var store = InsightLearningStore.Load(file.FilePath);
        var ranked = store.RankQuickActions(ActionCatalog.EditingContext(), learningEnabled: true, Now, maximum: 3);

        Assert.True(store.LoadFailed);
        Assert.False(store.HasQuickActionData);
        Assert.Equal("Format", ranked[0].Id);
    }

    [Fact]
    public void UnknownDocumentVersion_IsIgnored()
    {
        using var file = new TempLearningFile();
        File.WriteAllText(file.FilePath, "{\"Version\":99,\"Preferences\":{\"project\\u001fProjectTerminal\":{\"Weight\":5,\"LastUsedUtcTicks\":638000000000000000}}}");

        var store = InsightLearningStore.Load(file.FilePath);

        Assert.False(store.LoadFailed);
        Assert.False(store.HasQuickActionData);
    }

    [Fact]
    public void MalformedPreferenceEntries_AreSkipped()
    {
        using var file = new TempLearningFile();
        var longKey = new string('k', 200);
        File.WriteAllText(
            file.FilePath,
            "{\"Version\":5,\"Preferences\":{" +
            "\"project\\u001fNegative\":{\"Weight\":-4,\"LastUsedUtcTicks\":638000000000000000}," +
            "\"\":{\"Weight\":4,\"LastUsedUtcTicks\":638000000000000000}," +
            "\"project\\u001fNoTimestamp\":{\"Weight\":4,\"LastUsedUtcTicks\":0}," +
            $"\"{longKey}\":{{\"Weight\":4,\"LastUsedUtcTicks\":638000000000000000}}" +
            "}}");

        var store = InsightLearningStore.Load(file.FilePath);

        Assert.False(store.LoadFailed);
        Assert.False(store.HasQuickActionData);
        Assert.Equal(0, store.GetQuickActionAdjustment("Negative", "project", Now));
    }
}
