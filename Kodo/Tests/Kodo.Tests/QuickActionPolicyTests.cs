// Licensed under the GNU GPL-v3.0
using Kodo.Insight;
using Kodo.Models;
using System;
using System.Linq;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionPolicyTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 7, 30, 0, DateTimeKind.Utc);

    private static InsightUserIntelligence CreateIntelligence(TempLearningFile file, out QuickActionItem terminal)
    {
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        terminal = ActionCatalog.ProjectTerminal();
        for (var day = 0; day < 5; day++)
            for (var use = 0; use < 4; use++)
                intelligence.RecordQuickActionSelection(terminal, Now.AddDays(day).AddMinutes(use));
        return intelligence;
    }

    [Fact]
    public void CandidateList_DrivesEligibilityIndependentlyOfLearning()
    {
        using var file = new TempLearningFile();
        var intelligence = CreateIntelligence(file, out _);
        var withWorkspace = ActionCatalog.EditingContext();
        var withoutWorkspace = ActionCatalog.WorkspaceClosedContext();

        var rankedWithWorkspace = intelligence.RankQuickActions(withWorkspace, Now.AddDays(5), maximum: 3);
        var rankedWithoutWorkspace = intelligence.RankQuickActions(withoutWorkspace, Now.AddDays(5), maximum: 3);

        Assert.Contains(rankedWithWorkspace, action => action.Id == "ProjectTerminal");
        Assert.DoesNotContain(rankedWithoutWorkspace, action => action.Id == "ProjectTerminal");
        Assert.All(rankedWithoutWorkspace, action => Assert.Contains(withoutWorkspace, candidate => candidate.Id == action.Id));
    }

    [Fact]
    public void TopRankedAction_IsTheCtrlPeriodTarget()
    {
        using var file = new TempLearningFile();
        var intelligence = CreateIntelligence(file, out _);
        var candidates = ActionCatalog.EditingContext();

        var ranked = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3);

        Assert.Equal("ProjectTerminal", ranked[0].Id);
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void FailedOrCancelledAction_LeavesNoLearning()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);
        var candidates = ActionCatalog.EditingContext();

        for (var attempt = 0; attempt < 10; attempt++)
            intelligence.RankQuickActions(candidates, Now.AddMinutes(attempt), maximum: 3);
        intelligence.RecordPaletteCommandSelection("cmd.focus-terminal", Now);

        Assert.False(intelligence.HasQuickActionLearningData);
        Assert.Equal("Format", intelligence.RankQuickActions(candidates, Now.AddMinutes(11), maximum: 3)[0].Id);
    }

    [Fact]
    public void HeavilyUsedAction_DoesNotCrowdOutOtherSlots()
    {
        using var file = new TempLearningFile();
        var intelligence = CreateIntelligence(file, out var terminal);
        var candidates = ActionCatalog.EditingContext();

        for (var use = 0; use < 100; use++)
            intelligence.RecordQuickActionSelection(terminal, Now.AddDays(5).AddMinutes(use));

        var ranked = intelligence.RankQuickActions(candidates, Now.AddDays(6), maximum: 3);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(3, ranked.Select(action => action.Id).Distinct().Count());
        Assert.Contains(ranked, action => action.Id != "ProjectTerminal");
    }

    [Fact]
    public void EquivalentInputs_ProduceIdenticalRanking()
    {
        using var file = new TempLearningFile();
        var intelligence = CreateIntelligence(file, out _);
        var candidates = ActionCatalog.EditingContext();

        var first = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3).Select(action => action.Id).ToArray();
        var second = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3).Select(action => action.Id).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Ranking_IsIndependentOfCandidateEnumerationOrderForDistinctScores()
    {
        using var file = new TempLearningFile();
        var intelligence = CreateIntelligence(file, out _);
        var candidates = ActionCatalog.EditingContext().Reverse().ToList();

        var ranked = intelligence.RankQuickActions(candidates, Now.AddDays(5), maximum: 3);

        Assert.Equal("ProjectTerminal", ranked[0].Id);
    }
}
