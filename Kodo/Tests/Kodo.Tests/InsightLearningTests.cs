// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using System.Linq;
using Kodo.Insight;
using Kodo.Models;
using Xunit;

namespace Kodo.Tests;

public sealed class InsightLearningStoreTests
{
    private static readonly DateTime Now = new(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc);

    private static InsightLearningStore CreateStore(TempDirectory temp) =>
        new(temp.Combine("quick-actions-learning.json"));

    private static QuickActionItem Action(string id, double relevance, string context = "editor") => new()
    {
        Id = id,
        Title = id,
        ContextKey = context,
        BaseRelevance = relevance,
    };

    [Fact]
    public void NewStore_StartsEmpty()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        Assert.False(store.LoadFailed);
        Assert.False(store.HasQuickActionData);
        Assert.False(store.HasPaletteData);
        Assert.False(store.HasQueryAssociationData);
        Assert.False(store.IsAnalysisComplete);
        Assert.Equal(0, store.AnalysisCompletedDays);
        Assert.Equal(0, store.GetQuickActionAdjustment("QuickFix", "editor", Now));
        Assert.Equal(0, store.GetPaletteAdjustment("cmd.save", Now));
    }

    [Theory]
    [InlineData("Git Comm", "git comm")]
    [InlineData("  git  commit  ", "git commit")]
    [InlineData("Git--Commit", "git--commit")]
    [InlineData("format?document", "format document")]
    [InlineData("a#b", "a#b")]
    [InlineData("x_y+z", "x_y+z")]
    public void NormalizeQuery_CleansAndLowercases(string input, string expected)
    {
        Assert.Equal(expected, InsightLearningStore.NormalizeQuery(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("!!")]
    public void NormalizeQuery_RejectsTooShortQueries(string? input)
    {
        Assert.Null(InsightLearningStore.NormalizeQuery(input));
    }

    [Fact]
    public void NormalizeQuery_RejectsOverlongQueries()
    {
        Assert.Null(InsightLearningStore.NormalizeQuery(new string('a', 65)));
        Assert.NotNull(InsightLearningStore.NormalizeQuery(new string('a', 64)));
    }

    [Fact]
    public void RecordingQuickAction_RaisesAdjustment()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);

        Assert.True(store.HasQuickActionData);
        var adjustment = store.GetQuickActionAdjustment("QuickFix", "editor", Now);
        Assert.True(adjustment > 0);
        Assert.True(adjustment <= InsightLearningStore.MaximumQuickActionAdjustment);
    }

    [Fact]
    public void RecordingQuickAction_IsIgnoredWhenLearningDisabled()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: false);

        Assert.False(store.HasQuickActionData);
        Assert.Equal(0, store.GetQuickActionAdjustment("QuickFix", "editor", Now));
    }

    [Fact]
    public void Adjustment_IsScopedToContext()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);

        Assert.True(store.GetQuickActionAdjustment("QuickFix", "editor", Now) > 0);
        Assert.Equal(0, store.GetQuickActionAdjustment("QuickFix", "terminal", Now));
        Assert.Equal(0, store.GetQuickActionAdjustment("Rename", "editor", Now));
    }

    [Fact]
    public void Adjustment_DecaysOverTime()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);

        var fresh = store.GetQuickActionAdjustment("QuickFix", "editor", Now);
        var later = store.GetQuickActionAdjustment("QuickFix", "editor", Now.AddDays(90));

        Assert.True(later < fresh);
        Assert.True(later >= 0);
    }

    [Fact]
    public void Preferences_AreListedAndRemovable()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);

        var preference = Assert.Single(store.GetQuickActionPreferences(Now));
        Assert.Equal("QuickFix", preference.ActionId);
        Assert.Equal("editor", preference.ContextKey);
        Assert.True(preference.Strength > 0);

        Assert.True(store.RemoveQuickActionPreference("QuickFix", "editor"));
        Assert.False(store.RemoveQuickActionPreference("QuickFix", "editor"));
        Assert.Empty(store.GetQuickActionPreferences(Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankIdentifiers_AreIgnored(string identifier)
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.RecordQuickActionSelection(identifier, "editor", Now, learningEnabled: true);
        store.RecordPaletteSelection(identifier, Now, learningEnabled: true);

        Assert.False(store.HasQuickActionData);
        Assert.False(store.HasPaletteData);
    }

    [Fact]
    public void PaletteLearning_RaisesBoundedAdjustment()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        for (var i = 0; i < 40; i++)
            store.RecordPaletteSelection("cmd.save", Now, learningEnabled: true);

        var adjustment = store.GetPaletteAdjustment("cmd.save", Now);
        Assert.True(adjustment > 0);
        Assert.True(adjustment <= InsightLearningStore.MaximumPaletteAdjustment);
        Assert.Equal(0, store.GetPaletteAdjustment("cmd.other", Now));

        var preference = Assert.Single(store.GetPaletteCommandPreferences(Now));
        Assert.Equal("cmd.save", preference.CommandId);
    }

    [Fact]
    public void PaletteLearning_IsIgnoredWhenDisabled()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.RecordPaletteSelection("cmd.save", Now, learningEnabled: false);

        Assert.False(store.HasPaletteData);
        Assert.Equal(0, store.GetPaletteAdjustment("cmd.save", Now));
    }

    [Fact]
    public void QueryAssociations_AreRecordedAndRemoved()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        Assert.True(store.RecordQueryAssociation("Git Comm", "cmd.commit", Now, learningEnabled: true));
        Assert.True(store.HasQueryAssociationData);
        Assert.True(store.GetQueryAssociationAdjustment("git comm", "cmd.commit", Now) > 0);
        Assert.Equal(0, store.GetQueryAssociationAdjustment("git comm", "cmd.other", Now));
        Assert.Equal(0, store.GetQueryAssociationAdjustment(null, "cmd.commit", Now));

        var association = Assert.Single(store.GetQueryAssociations(Now));
        Assert.Equal("git comm", association.Query);
        Assert.Equal("cmd.commit", association.CommandId);

        Assert.True(store.RemoveQueryAssociation("git comm", "cmd.commit"));
        Assert.False(store.RemoveQueryAssociation("git comm", "cmd.commit"));
        Assert.False(store.HasQueryAssociationData);
    }

    [Fact]
    public void QueryAssociations_AreIgnoredWhenLearningDisabled()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        Assert.False(store.RecordQueryAssociation("git comm", "cmd.commit", Now, learningEnabled: false));
        Assert.False(store.HasQueryAssociationData);
    }

    [Fact]
    public void RankQuickActions_OrdersByRelevanceWithoutLearning()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        var ranked = store.RankQuickActions(
            [Action("Low", 1), Action("High", 9), Action("Mid", 5)],
            learningEnabled: false,
            Now);

        Assert.Equal(new[] { "High", "Mid", "Low" }, ranked.Select(action => action.Id));
    }

    [Fact]
    public void RankQuickActions_RespectsMaximumAndZeroLimit()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        QuickActionItem[] actions = [Action("A", 3), Action("B", 2), Action("C", 1)];

        Assert.Equal(2, store.RankQuickActions(actions, learningEnabled: false, Now, maximum: 2).Count);
        Assert.Empty(store.RankQuickActions(actions, learningEnabled: false, Now, maximum: 0));
    }

    [Fact]
    public void RankQuickActions_KeepsStableOrderForTies()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        var ranked = store.RankQuickActions(
            [Action("First", 5), Action("Second", 5)],
            learningEnabled: false,
            Now);

        Assert.Equal(new[] { "First", "Second" }, ranked.Select(action => action.Id));
    }

    [Fact]
    public void AnalysisPeriod_CompletesAfterFiveDistinctDays()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.MarkAnalysisActivity(Now);
        store.MarkAnalysisActivity(Now.AddHours(2));
        Assert.Equal(1, store.AnalysisCompletedDays);

        for (var day = 1; day < InsightLearningStore.AnalysisPeriodDays; day++)
            store.MarkAnalysisActivity(Now.AddDays(day));

        Assert.Equal(InsightLearningStore.AnalysisPeriodDays, store.AnalysisCompletedDays);
        Assert.True(store.IsAnalysisComplete);
    }

    [Fact]
    public void AnalysisPeriod_DoesNotExceedRequiredDays()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        for (var day = 0; day < 20; day++)
            store.MarkAnalysisActivity(Now.AddDays(day));

        Assert.Equal(InsightLearningStore.AnalysisPeriodDays, store.AnalysisCompletedDays);
    }

    [Fact]
    public void RestartAnalysisPeriod_ClearsProgress()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        for (var day = 0; day < InsightLearningStore.AnalysisPeriodDays; day++)
            store.MarkAnalysisActivity(Now.AddDays(day));

        store.RestartAnalysisPeriod();

        Assert.False(store.IsAnalysisComplete);
        Assert.Equal(0, store.AnalysisCompletedDays);
    }

    [Fact]
    public void RecordingLearning_StartsAnalysisActivity()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);

        Assert.Equal(1, store.AnalysisCompletedDays);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsLearningData()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);
        store.RecordPaletteSelection("cmd.save", Now, learningEnabled: true);
        store.RecordQueryAssociation("git comm", "cmd.commit", Now, learningEnabled: true);
        store.MarkAnalysisActivity(Now);
        store.Save();

        var loaded = InsightLearningStore.Load(store.FilePath);

        Assert.False(loaded.LoadFailed);
        Assert.True(loaded.HasQuickActionData);
        Assert.True(loaded.HasPaletteData);
        Assert.True(loaded.HasQueryAssociationData);
        Assert.Equal(1, loaded.AnalysisCompletedDays);
        Assert.True(loaded.GetQuickActionAdjustment("QuickFix", "editor", Now) > 0);
        Assert.True(loaded.GetPaletteAdjustment("cmd.save", Now) > 0);
        Assert.True(loaded.GetQueryAssociationAdjustment("git comm", "cmd.commit", Now) > 0);
    }

    [Fact]
    public void ResetMethods_ClearLearningData()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);
        store.RecordPaletteSelection("cmd.save", Now, learningEnabled: true);
        store.RecordQueryAssociation("git comm", "cmd.commit", Now, learningEnabled: true);

        store.ResetQuickActions();
        Assert.False(store.HasQuickActionData);
        Assert.True(store.HasPaletteData);

        store.ResetPalette();
        Assert.False(store.HasPaletteData);
        Assert.False(store.HasQueryAssociationData);
    }

    [Fact]
    public void CorruptFile_IsReportedAsLoadFailure()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("insight.json", "{ this is not json");
        var store = InsightLearningStore.Load(path);

        Assert.True(store.LoadFailed);
        Assert.False(store.HasQuickActionData);
        Assert.False(store.IsAnalysisComplete);
    }

    [Fact]
    public void MissingFile_LoadsEmptyStore()
    {
        using var temp = new TempDirectory();
        var store = InsightLearningStore.Load(temp.Combine("absent.json"));

        Assert.False(store.LoadFailed);
        Assert.Equal(0, store.AnalysisCompletedDays);
    }

    [Fact]
    public void UnsupportedDocumentVersion_IsIgnored()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("insight.json", "{\"Version\":99,\"Preferences\":{}}");
        var store = InsightLearningStore.Load(path);

        Assert.False(store.HasQuickActionData);
    }

    [Fact]
    public void Backup_RoundTripsThroughFile()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        store.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);

        store.CreateBackup();
        Assert.True(store.HasBackup);
        Assert.True(File.Exists(store.BackupFilePath));

        store.ResetQuickActions();
        Assert.False(store.HasQuickActionData);

        store.RestoreBackup();
        var reloaded = InsightLearningStore.Load(store.FilePath);
        Assert.True(reloaded.HasQuickActionData);

        store.DeleteBackup();
        Assert.False(store.HasBackup);
    }

    [Fact]
    public void RestoreBackup_ThrowsWhenMissingOrInvalid()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        Assert.Throws<FileNotFoundException>(() => store.RestoreBackup());

        File.WriteAllText(store.BackupFilePath, "{ broken");
        Assert.Throws<InvalidDataException>(() => store.RestoreBackup());
    }

    [Fact]
    public void Save_CreatesMissingDirectories()
    {
        using var temp = new TempDirectory();
        var nested = new InsightLearningStore(temp.Combine("nested", "deeper", "learning.json"));

        nested.RecordQuickActionSelection("QuickFix", "editor", Now, learningEnabled: true);
        nested.Save();

        Assert.True(File.Exists(nested.FilePath));
    }
}
