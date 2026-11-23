// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo.Insight;

public sealed record InsightQuickActionPreference(string ActionId, string ContextKey, double Strength, DateTime LastUsedUtc);

public sealed record InsightPaletteCommandPreference(string CommandId, double Strength, DateTime LastUsedUtc);

public sealed record InsightQueryAssociation(string Query, string CommandId, double Strength, DateTime LastUsedUtc);

public sealed record InsightLearningSnapshot(
    bool LearningEnabled,
    bool QuickActionsLearningEnabled,
    bool CommandPaletteLearningEnabled,
    IReadOnlyList<InsightQuickActionPreference> QuickActions,
    IReadOnlyList<InsightPaletteCommandPreference> CommandPalette,
    IReadOnlyList<InsightQueryAssociation> QueryAssociations)
{
    public int TotalCount => QuickActions.Count + CommandPalette.Count + QueryAssociations.Count;
}
