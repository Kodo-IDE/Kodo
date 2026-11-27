// Licensed under the GNU GPL-v3.0
using Kodo.Insight;
using System;
using System.Linq;
using Xunit;

namespace Kodo.Tests;

public sealed class InsightUserIntelligenceTests
{
    private static readonly DateTime Now = new(2026, 4, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnalysisProgress_CountsDistinctDays()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        intelligence.RecordQuickActionSelection(terminal, Now);
        intelligence.RecordQuickActionSelection(terminal, Now.AddHours(2));
        intelligence.RecordQuickActionSelection(terminal, Now.AddDays(1));

        Assert.Equal(2, intelligence.AnalysisCompletedDays);
        Assert.False(intelligence.IsAnalysisComplete);
    }

    [Fact]
    public void LearningInfluence_WaitsForAnalysisPeriod()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        for (var day = 0; day < 4; day++)
            for (var use = 0; use < 3; use++)
                intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day).AddMinutes(use));

        var beforeAnalysis = intelligence.RankQuickActions(candidates, Now.AddDays(4), maximum: 3);

        Assert.False(intelligence.IsAnalysisComplete);
        Assert.DoesNotContain(beforeAnalysis, action => action.Id == "ProjectTerminal");

        intelligence.RecordQuickActionSelection(terminal, Now.AddDays(4));

        var afterAnalysis = intelligence.RankQuickActions(candidates, Now.AddDays(4), maximum: 3);

        Assert.True(intelligence.IsAnalysisComplete);
        Assert.Equal("ProjectTerminal", afterAnalysis[0].Id);
    }

    [Fact]
    public void DisablingQuickActionPersonalization_RemovesInfluence()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        for (var day = 0; day < 5; day++)
            for (var use = 0; use < 4; use++)
                intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day).AddMinutes(use));

        Assert.Equal("ProjectTerminal", intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3)[0].Id);

        intelligence.IsQuickActionsLearningEnabled = false;

        var disabled = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3);

        Assert.Equal("Format", disabled[0].Id);
        Assert.DoesNotContain(disabled, action => action.Id == "ProjectTerminal");
    }

    [Fact]
    public void DisablingLearning_StopsRecordsAndInfluence()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        for (var day = 0; day < 5; day++)
            for (var use = 0; use < 4; use++)
                intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day).AddMinutes(use));

        intelligence.IsLearningEnabled = false;
        intelligence.RecordQuickActionSelection(terminal, Now.AddDays(5));

        var ranked = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3);

        Assert.Equal("Format", ranked[0].Id);
        Assert.DoesNotContain(ranked, action => action.Id == "ProjectTerminal");
    }

    [Fact]
    public void ResetThroughIntelligence_ClearsLearnedData()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var candidates = ActionCatalog.EditingContext();
        var terminal = candidates.First(action => action.Id == "ProjectTerminal");

        for (var day = 0; day < 5; day++)
            for (var use = 0; use < 4; use++)
                intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day).AddMinutes(use));

        intelligence.ResetQuickActionLearning();

        var ranked = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3);

        Assert.False(intelligence.HasQuickActionLearningData);
        Assert.Equal("Format", ranked[0].Id);
        Assert.DoesNotContain(ranked, action => action.Id == "ProjectTerminal");
    }

    [Fact]
    public void Snapshot_ReportsLearnedPreferences()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var day = 0; day < 5; day++)
            intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day));

        var snapshot = intelligence.GetSnapshot(Now.AddDays(6));

        var preference = Assert.Single(snapshot.QuickActions);
        Assert.Equal("ProjectTerminal", preference.ActionId);
        Assert.Equal("project", preference.ContextKey);
        Assert.InRange(preference.Strength, 0.01, 1);
        Assert.Empty(snapshot.CommandPalette);
        Assert.Empty(snapshot.QueryAssociations);
    }

    [Fact]
    public void QuickActionLearning_DoesNotAffectPaletteScores()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var day = 0; day < 5; day++)
            for (var use = 0; use < 4; use++)
                intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day).AddMinutes(use));

        Assert.Equal(0, intelligence.GetPaletteCommandAdjustment("ProjectTerminal", Now.AddDays(5)));
        Assert.Equal(12, intelligence.ScorePaletteCommand(12, "ProjectTerminal", null, Now.AddDays(5)));
        Assert.False(intelligence.HasPaletteLearningData);
    }

    [Fact]
    public void PaletteReset_KeepsQuickActionLearning()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var day = 0; day < 5; day++)
        {
            intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day));
            intelligence.RecordPaletteCommandSelection("cmd.focus-terminal", Now.AddDays(day));
        }

        Assert.True(intelligence.HasQuickActionLearningData);
        Assert.True(intelligence.HasPaletteLearningData);

        intelligence.ResetPaletteLearning();

        Assert.True(intelligence.HasQuickActionLearningData);
        Assert.False(intelligence.HasPaletteLearningData);
        Assert.True(intelligence.GetPaletteCommandAdjustment("cmd.focus-terminal", Now.AddDays(5)) == 0);
    }
}
