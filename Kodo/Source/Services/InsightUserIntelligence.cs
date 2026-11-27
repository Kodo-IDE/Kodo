// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;

namespace Kodo.Insight;

public sealed class InsightUserIntelligence
{
    private readonly InsightLearningStore _store;

    public InsightUserIntelligence(InsightLearningStore store) => _store = store;

    public static InsightUserIntelligence Load(string filePath) => new(InsightLearningStore.Load(filePath));

    public string FilePath => _store.FilePath;

    public string BackupFilePath => _store.BackupFilePath;

    public bool HasBackup => _store.HasBackup;

    public bool LoadFailed => _store.LoadFailed;

    public void CreateBackup() => _store.CreateBackup();

    public void RestoreBackup() => _store.RestoreBackup();

    public void DeleteBackup() => _store.DeleteBackup();

    public bool IsQuickActionsLearningEnabled { get; set; } = true;

    public bool IsPaletteLearningEnabled { get; set; } = true;

    public bool IsLearningEnabled { get; set; } = true;

    public bool HasQuickActionLearningData => _store.HasQuickActionData;

    public bool HasPaletteLearningData => _store.HasPaletteData;

    public bool HasQueryAssociationData => _store.HasQueryAssociationData;

    public bool HasAnyLearningData => HasQuickActionLearningData || HasPaletteLearningData;

    public bool IsAnalysisComplete => _store.IsAnalysisComplete;

    public int AnalysisCompletedDays => _store.AnalysisCompletedDays;

    public int AnalysisRequiredDays => InsightLearningStore.AnalysisPeriodDays;

    public void RestartAnalysisPeriod() => _store.RestartAnalysisPeriod();

    private bool QuickActionsLearningActive => IsLearningEnabled && IsQuickActionsLearningEnabled;

    private bool PaletteLearningActive => IsLearningEnabled && IsPaletteLearningEnabled;

    private bool QuickActionsInfluenceActive => QuickActionsLearningActive && IsAnalysisComplete;

    private bool PaletteInfluenceActive => PaletteLearningActive && IsAnalysisComplete;

    public static string? NormalizeQuery(string? query) => InsightLearningStore.NormalizeQuery(query);

    public IReadOnlyList<QuickActionItem> RankQuickActions(IEnumerable<QuickActionItem> availableActions, DateTime utcNow, int maximum = 3) =>
        _store.RankQuickActions(availableActions, QuickActionsInfluenceActive, utcNow, maximum);

    public void RecordQuickActionSelection(QuickActionItem selected, DateTime utcNow) =>
        _store.RecordQuickActionSelection(selected.Id, selected.ContextKey, utcNow, QuickActionsLearningActive, selected.LearningFeatures);

    public double GetPaletteCommandAdjustment(string commandId, DateTime utcNow) =>
        PaletteInfluenceActive ? _store.GetPaletteAdjustment(commandId, utcNow) : 0;

    public void RecordPaletteCommandSelection(string commandId, DateTime utcNow) =>
        _store.RecordPaletteSelection(commandId, utcNow, PaletteLearningActive);

    public double GetPaletteQueryAdjustment(string? normalizedQuery, string commandId, DateTime utcNow) =>
        PaletteInfluenceActive ? _store.GetQueryAssociationAdjustment(normalizedQuery, commandId, utcNow) : 0;

    public bool RecordPaletteQuerySelection(string? query, string commandId, DateTime utcNow) =>
        _store.RecordQueryAssociation(query, commandId, utcNow, PaletteLearningActive);

    public double ScorePaletteCommand(int textualScore, string commandId, string? normalizedQuery, DateTime utcNow) =>
        textualScore
        + Math.Clamp(GetPaletteCommandAdjustment(commandId, utcNow), 0, InsightLearningStore.MaximumPaletteAdjustment)
        + Math.Clamp(GetPaletteQueryAdjustment(normalizedQuery, commandId, utcNow), 0, InsightLearningStore.MaximumQueryAssociationAdjustment);

    public InsightLearningSnapshot GetSnapshot(DateTime utcNow) => new(
        IsLearningEnabled,
        IsQuickActionsLearningEnabled,
        IsPaletteLearningEnabled,
        _store.GetQuickActionPreferences(utcNow),
        _store.GetPaletteCommandPreferences(utcNow),
        _store.GetQueryAssociations(utcNow));

    public bool RemoveQuickActionPreference(InsightQuickActionPreference entry) =>
        _store.RemoveQuickActionPreference(entry.ActionId, entry.ContextKey);

    public bool RemovePaletteCommandPreference(InsightPaletteCommandPreference entry) =>
        _store.RemovePaletteCommandPreference(entry.CommandId);

    public bool RemoveQueryAssociation(InsightQueryAssociation entry) =>
        _store.RemoveQueryAssociation(entry.Query, entry.CommandId);

    public void ResetQuickActionLearning() => _store.ResetQuickActions();

    public void ResetPaletteLearning() => _store.ResetPalette();

    public void ResetQueryAssociationLearning() => _store.ResetQueryAssociations();

    public void ResetAllLearning()
    {
        _store.ResetQuickActions();
        _store.ResetPalette();
    }

    public void Save() => _store.Save();
}
