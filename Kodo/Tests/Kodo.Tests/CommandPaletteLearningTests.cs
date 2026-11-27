// Licensed under the GNU GPL-v3.0
using Kodo.Insight;
using System;
using Xunit;

namespace Kodo.Tests;

public sealed class CommandPaletteLearningTests
{
    private static readonly DateTime Now = new(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc);
    private const string CommandId = "cmd.focus-terminal";

    [Fact]
    public void LearnedCommandSelection_BoostsPaletteScore()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);

        for (var day = 0; day < 5; day++)
            for (var use = 0; use < 2; use++)
                intelligence.RecordPaletteCommandSelection(CommandId, Now.AddDays(day).AddMinutes(use));

        var score = intelligence.ScorePaletteCommand(10, CommandId, null, Now.AddDays(5));

        Assert.True(score > 10);
        Assert.True(score <= 10 + InsightLearningStore.MaximumPaletteAdjustment);
    }

    [Fact]
    public void PaletteInfluence_WaitsForAnalysisPeriod()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);

        for (var day = 0; day < 4; day++)
            for (var use = 0; use < 2; use++)
                intelligence.RecordPaletteCommandSelection(CommandId, Now.AddDays(day).AddMinutes(use));

        Assert.False(intelligence.IsAnalysisComplete);
        Assert.Equal(10, intelligence.ScorePaletteCommand(10, CommandId, null, Now.AddDays(4)));
        Assert.True(intelligence.HasPaletteLearningData);
    }

    [Fact]
    public void QueryAssociation_BoostsScoreForMatchingQuery()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);

        for (var day = 0; day < 5; day++)
            intelligence.RecordPaletteQuerySelection("open terminal", CommandId, Now.AddDays(day));

        var normalized = InsightUserIntelligence.NormalizeQuery("Open Terminal");

        Assert.Equal("open terminal", normalized);
        Assert.True(intelligence.GetPaletteQueryAdjustment(normalized, CommandId, Now.AddDays(5)) > 0);
        Assert.True(intelligence.ScorePaletteCommand(0, CommandId, normalized, Now.AddDays(5)) >
                    intelligence.ScorePaletteCommand(0, CommandId, null, Now.AddDays(5)));
    }

    [Fact]
    public void QueryAssociation_IgnoresUnrelatedQueries()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);

        for (var day = 0; day < 5; day++)
            intelligence.RecordPaletteQuerySelection("open terminal", CommandId, Now.AddDays(day));

        Assert.Equal(0, intelligence.GetPaletteQueryAdjustment("format document", CommandId, Now.AddDays(5)));
    }

    [Fact]
    public void PaletteReset_ClearsCommandsAndQueryAssociations()
    {
        using var file = new TempLearningFile();
        var intelligence = InsightUserIntelligence.Load(file.FilePath);

        for (var day = 0; day < 5; day++)
        {
            intelligence.RecordPaletteCommandSelection(CommandId, Now.AddDays(day));
            intelligence.RecordPaletteQuerySelection("open terminal", CommandId, Now.AddDays(day));
        }

        intelligence.ResetPaletteLearning();

        Assert.False(intelligence.HasPaletteLearningData);
        Assert.False(intelligence.HasQueryAssociationData);
        Assert.Equal(0, intelligence.GetPaletteCommandAdjustment(CommandId, Now.AddDays(6)));
        Assert.Equal(0, intelligence.GetPaletteQueryAdjustment("open terminal", CommandId, Now.AddDays(6)));
    }

    [Fact]
    public void QuickActionRecordings_DoNotCreatePaletteData()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);
        var terminal = ActionCatalog.ProjectTerminal();

        for (var use = 0; use < 10; use++)
            store.RecordQuickActionSelection(terminal.Id, terminal.ContextKey, Now.AddMinutes(use), learningEnabled: true, terminal.LearningFeatures);

        Assert.True(store.HasQuickActionData);
        Assert.False(store.HasPaletteData);
        Assert.Equal(0, store.GetPaletteAdjustment("ProjectTerminal", Now));
    }

    [Fact]
    public void PaletteSelections_DoNotCreateQuickActionData()
    {
        using var file = new TempLearningFile();
        var store = new InsightLearningStore(file.FilePath);

        for (var use = 0; use < 10; use++)
            store.RecordPaletteSelection("cmd.focus-terminal", Now.AddMinutes(use), learningEnabled: true);

        Assert.False(store.HasQuickActionData);
        Assert.Equal(0, store.GetQuickActionAdjustment("ProjectTerminal", "project", Now));
    }
}
