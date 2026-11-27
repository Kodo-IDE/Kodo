// Licensed under the GNU GPL-v3.0
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kodo.Insight;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace Kodo;

public sealed record InsightLearningRow(string Title, string Detail, string StrengthText, object Key);

public partial class MainWindow
{
    private Dictionary<string, string>? _insightCommandTitles;

    public ObservableCollection<InsightLearningRow> InsightQuickActionRows { get; } = new();

    public ObservableCollection<InsightLearningRow> InsightCommandPaletteRows { get; } = new();

    public ObservableCollection<InsightLearningRow> InsightQueryRows { get; } = new();

    public bool HasInsightQuickActionRows => InsightQuickActionRows.Count > 0;

    public bool HasInsightCommandPaletteRows => InsightCommandPaletteRows.Count > 0;

    public bool HasInsightQueryRows => InsightQueryRows.Count > 0;

    public bool HasInsightLearningData => UserInsight.HasAnyLearningData;

    public bool HasInsightQueryAssociations => UserInsight.HasQueryAssociationData;

    public string InsightQuickActionSummary => !UserInsight.IsAnalysisComplete
        ? AnalysisHeldLabel
        : Summary(InsightQuickActionRows.Count, "Quick Actions preference", "Quick Actions preferences");

    public string InsightCommandPaletteSummary => !UserInsight.IsAnalysisComplete
        ? AnalysisHeldLabel
        : Summary(InsightCommandPaletteRows.Count, "command preference", "command preferences");

    public string InsightQuerySummary => !UserInsight.IsAnalysisComplete
        ? AnalysisHeldLabel
        : Summary(InsightQueryRows.Count, "search association", "search associations");

    public string InsightLearningSummary => !UserInsight.IsAnalysisComplete
        ? $"Analysis Period: {UserInsight.AnalysisCompletedDays} of {UserInsight.AnalysisRequiredDays} coding days completed."
        : UserInsight.HasAnyLearningData
            ? $"{InsightQuickActionRows.Count + InsightCommandPaletteRows.Count + InsightQueryRows.Count} learned items stored locally on this device."
            : "Insight has not learned anything yet.";

    public bool IsInsightAnalysisPeriodVisible => !UserInsight.IsAnalysisComplete;

    public string InsightAnalysisProgressText
    {
        get
        {
            var required = UserInsight.AnalysisRequiredDays;
            var completed = UserInsight.AnalysisCompletedDays;
            if (completed == 0)
                return $"No coding days recorded yet. Insight shows its first results after {required} days of use.";

            var remaining = Math.Max(0, required - completed);
            return $"{completed} of {required} coding days completed, {remaining} {(remaining == 1 ? "day" : "days")} to go.";
        }
    }

    public string InsightAnalysisDetailText =>
        "Only days you actually use Kodo count, so time away does not move the count. Nothing is shown until the period ends. Clearing learning data starts the Analysis Period again.";

    private const string AnalysisHeldLabel = "Held until the Analysis Period ends.";

    private bool _isInsightDashboardExpanded;

    public bool IsInsightDashboardExpanded
    {
        get => _isInsightDashboardExpanded;
        set
        {
            if (_isInsightDashboardExpanded == value) return;
            _isInsightDashboardExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InsightDashboardToggleText));
            OnPropertyChanged(nameof(InsightDashboardToggleTooltip));
        }
    }

    public string InsightDashboardToggleText => _isInsightDashboardExpanded ? "Hide details" : "Show details";

    public string InsightDashboardToggleTooltip => _isInsightDashboardExpanded
        ? "Collapse Insight personalization details"
        : "Expand Insight personalization details";

    private void ToggleInsightDashboardExpandedButton_OnClick(object? sender, RoutedEventArgs e) =>
        IsInsightDashboardExpanded = !IsInsightDashboardExpanded;

    public bool IsInsightLearningEnabled
    {
        get => UserInsight.IsLearningEnabled;
        set
        {
            if (UserInsight.IsLearningEnabled == value) return;
            UserInsight.IsLearningEnabled = value;
            OnPropertyChanged();
            SaveSettings();
            RefreshQuickActions();
            RefreshOpenCommandPaletteRanking();
            RefreshInsightDashboard();
            ShowStatusBarMessage(value
                ? "Insight learning enabled."
                : "Insight learning disabled. Existing learning is kept.");
        }
    }

    private bool _hasInsightBackupFile;

    public bool HasInsightBackupFile
    {
        get => _hasInsightBackupFile;
        private set
        {
            if (_hasInsightBackupFile == value) return;
            _hasInsightBackupFile = value;
            OnPropertyChanged();
        }
    }

    private void RefreshInsightBackupState() => HasInsightBackupFile = UserInsight.HasBackup;

    private static string Summary(int count, string singular, string plural) => count switch
    {
        0 => "Nothing learned yet.",
        1 => $"1 {singular}.",
        _ => $"{count} {plural}.",
    };

    private void RefreshInsightDashboard()
    {
        InsightQuickActionRows.Clear();
        InsightCommandPaletteRows.Clear();
        InsightQueryRows.Clear();

        if (UserInsight.IsAnalysisComplete)
        {
            var snapshot = UserInsight.GetSnapshot(DateTime.UtcNow);

            foreach (var entry in snapshot.QuickActions)
                InsightQuickActionRows.Add(new InsightLearningRow(
                    HumanizeIdentifier(entry.ActionId),
                    ContextLabel(entry.ContextKey),
                    StrengthLabel(entry.Strength),
                    entry));

            foreach (var entry in snapshot.CommandPalette)
                InsightCommandPaletteRows.Add(new InsightLearningRow(
                    CommandTitle(entry.CommandId),
                    "Chosen in the Command Palette",
                    StrengthLabel(entry.Strength),
                    entry));

            foreach (var entry in snapshot.QueryAssociations)
                InsightQueryRows.Add(new InsightLearningRow(
                    $"“{entry.Query}” leads to {CommandTitle(entry.CommandId)}",
                    "Learned search association",
                    StrengthLabel(entry.Strength),
                    entry));
        }

        KodoDiagnostics.LogDebug($"Insight dashboard refreshed: analysisDays={UserInsight.AnalysisCompletedDays} analysisComplete={UserInsight.IsAnalysisComplete} quickActions={InsightQuickActionRows.Count} palette={InsightCommandPaletteRows.Count} queries={InsightQueryRows.Count}");

        RaiseMany(
            nameof(HasInsightQuickActionRows),
            nameof(HasInsightCommandPaletteRows),
            nameof(HasInsightQueryRows),
            nameof(HasInsightLearningData),
            nameof(HasInsightQueryAssociations),
            nameof(InsightQuickActionSummary),
            nameof(InsightCommandPaletteSummary),
            nameof(InsightQuerySummary),
            nameof(IsInsightAnalysisPeriodVisible),
            nameof(InsightAnalysisProgressText),
            nameof(InsightAnalysisDetailText),
            nameof(InsightLearningSummary));
    }

    private static string StrengthLabel(double strength) => $"{Math.Round(Math.Clamp(strength, 0, 1) * 100)}% influence";

    private string CommandTitle(string commandId)
    {
        _insightCommandTitles ??= BuildCommandPaletteCommands()
            .Where(command => command.Id.Length > 0)
            .GroupBy(command => command.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.Ordinal);

        return _insightCommandTitles.TryGetValue(commandId, out var title) ? title : HumanizeIdentifier(commandId);
    }

    private static string ContextLabel(string contextKey)
    {
        var parts = contextKey.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "Any context";
        return parts.Length == 1
            ? HumanizeIdentifier(parts[0])
            : $"{HumanizeIdentifier(parts[0])} · {parts[1]}";
    }

    private static string HumanizeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length + 4);
        foreach (var character in identifier.Replace("cmd.", string.Empty, StringComparison.Ordinal))
        {
            if (character is '.' or '-' or '_')
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            if (char.IsUpper(character) && builder.Length > 0 && builder[^1] != ' ' && char.IsLower(builder[^1]))
                builder.Append(' ');

            builder.Append(character);
        }

        var label = builder.ToString().Trim();
        return label.Length == 0 ? identifier : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private void AfterInsightLearningChanged(string message)
    {
        _insightSaveTimer.Stop();
        RefreshQuickActions();
        RefreshOpenCommandPaletteRanking();
        RefreshInsightDashboard();
        ShowStatusBarMessage(message);
        _ = SaveInsightAsync();
    }

    private void RemoveInsightQuickActionPreference_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InsightQuickActionPreference preference }) return;
        if (!UserInsight.RemoveQuickActionPreference(preference)) return;
        AfterInsightLearningChanged("Quick Actions preference removed.");
    }

    private void RemoveInsightCommandPalettePreference_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InsightPaletteCommandPreference preference }) return;
        if (!UserInsight.RemovePaletteCommandPreference(preference)) return;
        AfterInsightLearningChanged("Command Palette preference removed.");
    }

    private void RemoveInsightQueryAssociation_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InsightQueryAssociation association }) return;
        if (!UserInsight.RemoveQueryAssociation(association)) return;
        AfterInsightLearningChanged("Search association removed.");
    }

    private async void ResetPersonalizedSearchLearningButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await ShowConfirmationDialogAsync(
                "Reset Personalized Search learning",
                "Learned search terms and the commands they lead to will be removed. Command Palette command preferences are kept.",
                "Reset", "Cancel", isDestructive: true))
            return;

        UserInsight.ResetQueryAssociationLearning();
        RestartInsightAnalysisAfterClear();
        AfterInsightLearningChanged("Personalized Search learning reset.");
    }

    private async void ResetAllInsightLearningButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!await ShowConfirmationDialogAsync(
                "Reset all Insight learning",
                "Quick Actions preferences, Command Palette preferences and Personalized Search associations will all be removed. This cannot be undone.",
                "Reset", "Cancel", isDestructive: true))
            return;

        UserInsight.ResetAllLearning();
        RestartInsightAnalysisAfterClear();
        AfterInsightLearningChanged("All Insight learning reset.");
    }

    private void BackupInsightDataButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            UserInsight.CreateBackup();
            RefreshInsightBackupState();
            ShowStatusBarMessage($"Insight data backed up to {Path.GetFileName(UserInsight.BackupFilePath)}.");
        }
        catch (Exception ex)
        {
            ShowStatusBarMessage($"Could not back up Insight data: {ex.Message}");
            KodoDiagnostics.LogWarning("MainWindow.BackupInsightDataButton", ex, "Insight data backup failed");
        }
    }

    private async void RestoreInsightBackupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "Restore Insight backup?",
            "This overwrites your current Insight data with the last backup (quick-actions-learning.json.bak) and relaunches Kodo. Your current Insight data is not backed up first. This can't be undone.",
            confirmLabel: "Restore",
            isDestructive: true);

        if (!confirmed) return;

        _insightSaveTimer.Stop();
        try
        {
            UserInsight.RestoreBackup();
        }
        catch (Exception ex)
        {
            ShowStatusBarMessage($"Could not restore Insight backup: {ex.Message}");
            KodoDiagnostics.LogWarning("MainWindow.RestoreInsightBackupButton", ex, "Insight data backup restore failed");
            return;
        }

        _isConfirmedClose = true;
        _suppressSettingsSave = true;
        _settingsSaveDebounceTimer.Stop();

        var exeFileName = OperatingSystem.IsWindows() ? "Kodo.exe" : "Kodo";
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            exePath = Path.Combine(AppContext.BaseDirectory, exeFileName);
        if (File.Exists(exePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--relaunch",
                    UseShellExecute = true,
                });
                Environment.Exit(0);
            }
            catch { }
        }

        _isConfirmedClose = false;
        _suppressSettingsSave = false;
        ShowStatusBarMessage("Insight backup restored. Relaunch Kodo manually to load it.");
    }

    private async void ClearInsightBackupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "Clear Insight backup?",
            "This permanently deletes quick-actions-learning.json.bak. You will no longer be able to restore Insight data from this backup.",
            confirmLabel: "Clear Backup",
            isDestructive: true);

        if (!confirmed) return;

        try
        {
            UserInsight.DeleteBackup();
            RefreshInsightBackupState();
            ShowStatusBarMessage("Insight backup cleared.");
        }
        catch (Exception ex)
        {
            ShowStatusBarMessage($"Could not clear Insight backup: {ex.Message}");
            KodoDiagnostics.LogWarning("MainWindow.ClearInsightBackupButton", ex, "Insight data backup deletion failed");
        }
    }

    private void RestartInsightAnalysisAfterClear()
    {
        if (!UserInsight.IsLearningEnabled) return;
        UserInsight.RestartAnalysisPeriod();
    }
}
