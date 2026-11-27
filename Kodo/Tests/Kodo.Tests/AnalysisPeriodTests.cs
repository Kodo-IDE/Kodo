// Licensed under the GNU GPL-v3.0
using Kodo.Insight;
using System;
using Xunit;

namespace Kodo.Tests;

public sealed class AnalysisPeriodTests
{
    private static readonly DateTime Now = new(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RepeatedActivityInOneDay_CountsAsOneCodingDay()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 8; use++)
            intelligence.RecordQuickActionSelection(terminal, Now.AddMinutes(use * 5));

        Assert.Equal(1, intelligence.AnalysisCompletedDays);
        Assert.False(intelligence.IsAnalysisComplete);
    }

    [Fact]
    public void InactiveDays_DoNotAdvanceThePeriod()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        intelligence.RecordQuickActionSelection(terminal, Now);
        intelligence.RecordQuickActionSelection(terminal, Now.AddDays(30));
        intelligence.RecordQuickActionSelection(terminal, Now.AddDays(90));

        Assert.Equal(3, intelligence.AnalysisCompletedDays);
        Assert.False(intelligence.IsAnalysisComplete);
    }

    [Fact]
    public void RankingDoesNotAdvanceThePeriod()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        intelligence.RecordQuickActionSelection(terminal, Now);
        intelligence.RecordQuickActionSelection(terminal, Now.AddDays(1));

        for (var attempt = 0; attempt < 20; attempt++)
            intelligence.RankQuickActions(ActionCatalog.EditingContext(), Now.AddDays(2).AddMinutes(attempt), maximum: 3);

        Assert.Equal(2, intelligence.AnalysisCompletedDays);
    }

    [Fact]
    public void PeriodProgress_SurvivesRestart()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var day = 0; day < 3; day++)
            intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day));
        intelligence.Save();

        var restored = InsightUserIntelligence.Load(file.FilePath);

        Assert.False(restored.LoadFailed);
        Assert.Equal(3, restored.AnalysisCompletedDays);
        Assert.False(restored.IsAnalysisComplete);

        restored.RecordQuickActionSelection(terminal, Now.AddDays(3));
        restored.RecordQuickActionSelection(terminal, Now.AddDays(4));

        Assert.True(restored.IsAnalysisComplete);
    }

    [Fact]
    public void InfluenceBeginsWhenTheFifthCodingDayCompletes()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();
        var candidates = ActionCatalog.EditingContext();

        for (var day = 0; day < 5; day++)
            intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day));

        var ranked = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3);

        Assert.True(intelligence.IsAnalysisComplete);
        Assert.Equal("ProjectTerminal", ranked[0].Id);
    }

    [Fact]
    public void ResetRestartsThePeriodAndClearsPreferences()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var day = 0; day < 5; day++)
            intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day));

        Assert.True(intelligence.IsAnalysisComplete);

        intelligence.ResetQuickActionLearning();
        intelligence.RestartAnalysisPeriod();

        Assert.False(intelligence.IsAnalysisComplete);
        Assert.Equal(0, intelligence.AnalysisCompletedDays);
        Assert.False(intelligence.HasQuickActionLearningData);
    }

    [Fact]
    public void DisabledLearning_DoesNotAdvanceThePeriod()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        intelligence.IsQuickActionsLearningEnabled = false;
        intelligence.RecordQuickActionSelection(terminal, Now);

        Assert.Equal(0, intelligence.AnalysisCompletedDays);
    }

    [Fact]
    public void MalformedAnalysisDays_AreIgnoredOnLoad()
    {
        using var file = new TempLearningFile();
        System.IO.File.WriteAllText(
            file.FilePath,
            "{\"Version\":5,\"AnalysisActiveDays\":[-4,999999999,0]}");

        var store = InsightLearningStore.Load(file.FilePath);

        Assert.False(store.LoadFailed);
        Assert.Equal(1, store.AnalysisCompletedDays);
    }
}
