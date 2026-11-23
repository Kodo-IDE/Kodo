// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kodo.Insight;

public sealed class InsightLearningStore
{
    internal const int DocumentVersion = 5;
    public const int AnalysisPeriodDays = 5;
    internal const int MaximumQuickActionEntries = 128;
    internal const int MaximumPaletteEntries = 256;
    internal const int MaximumModelParameters = 4096;
    internal const int MinimumModelObservations = 3;
    internal const int MaximumQueryEntries = 128;
    internal const int MaximumCommandsPerQuery = 6;
    internal const int MinimumQueryLength = 2;
    internal const int MaximumQueryLength = 64;
    internal const double MaximumQueryWeight = 8;
    public const double MaximumQuickActionAdjustment = 10;
    public const double MaximumPaletteAdjustment = 1.5;

    public const double MaximumQueryAssociationAdjustment = 30;
    private const double MaximumWeight = 64;
    private const double PreferenceHalfLifeDays = 45;
    private const double ModelHalfLifeDays = 90;
    private const double ModelLearningRate = 0.35;
    private const double ModelRegularization = 0.01;
    private const double MaximumModelWeight = 3;
    private const string KeySeparator = "\u001f";
    private const string BackupSuffix = ".bak";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    private readonly Dictionary<string, Preference> _quickActionPreferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Preference> _palettePreferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Preference>> _queryAssociations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelParameter> _model = new(StringComparer.Ordinal);
    private readonly SortedSet<int> _analysisActiveDays = new();
    private readonly object _gate = new();

    public InsightLearningStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }
    public bool LoadFailed { get; private set; }

    public bool HasQuickActionData
    {
        get
        {
            lock (_gate)
                return _quickActionPreferences.Count > 0 || _model.Count > 0;
        }
    }

    public bool HasPaletteData
    {
        get
        {
            lock (_gate)
                return _palettePreferences.Count > 0 || _queryAssociations.Count > 0;
        }
    }

    public bool IsAnalysisComplete
    {
        get
        {
            lock (_gate) return _analysisActiveDays.Count >= AnalysisPeriodDays;
        }
    }

    public int AnalysisCompletedDays
    {
        get
        {
            lock (_gate) return Math.Min(_analysisActiveDays.Count, AnalysisPeriodDays);
        }
    }

    public void MarkAnalysisActivity(DateTime utcNow)
    {
        lock (_gate)
        {
            if (_analysisActiveDays.Count >= AnalysisPeriodDays) return;
            _analysisActiveDays.Add(DateOnly.FromDateTime(utcNow.ToLocalTime()).DayNumber);
        }
    }

    public void RestartAnalysisPeriod()
    {
        lock (_gate) _analysisActiveDays.Clear();
    }

    public static InsightLearningStore Load(string filePath)
    {
        var store = new InsightLearningStore(filePath);
        if (!File.Exists(filePath)) return store;

        try
        {
            var document = JsonSerializer.Deserialize<LearningDocument>(File.ReadAllText(filePath), JsonOptions);
            if (document is null || document.Version is < 1 or > DocumentVersion) return store;

            LoadPreferences(document.Preferences, store._quickActionPreferences, MaximumQuickActionEntries);
            if (document.Version >= 2 && document.Model is not null)
            {
                foreach (var pair in document.Model
                             .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { } value &&
                                            double.IsFinite(value.Weight) && Math.Abs(value.Weight) <= MaximumModelWeight &&
                                            value.LastUsedUtcTicks > 0 && value.LastUsedUtcTicks <= DateTime.MaxValue.Ticks)
                             .Take(MaximumModelParameters))
                    store._model[pair.Key] = new ModelParameter
                    {
                        Weight = pair.Value.Weight,
                        LastUsedUtcTicks = pair.Value.LastUsedUtcTicks,
                        Observations = Math.Clamp(pair.Value.Observations, 0, MaximumQuickActionEntries),
                    };
            }
            if (document.Version >= 3)
                LoadPreferences(document.PalettePreferences, store._palettePreferences, MaximumPaletteEntries);
            if (document.Version >= 4)
                LoadQueryAssociations(document.QueryAssociations, store._queryAssociations);
            if (document.Version >= 5)
                LoadAnalysisDays(document.AnalysisActiveDays, store._analysisActiveDays);
            else
                SeedAnalysisDaysFromHistory(store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            store.LoadFailed = true;
            store._quickActionPreferences.Clear();
            store._palettePreferences.Clear();
            store._queryAssociations.Clear();
            store._model.Clear();
            store._analysisActiveDays.Clear();
        }

        return store;
    }

    public double GetQuickActionAdjustment(string actionId, string contextKey, DateTime utcNow)
    {
        if (!TryGetPreferenceKey(actionId, contextKey, out var key)) return 0;
        lock (_gate) return GetPreferenceAdjustment(_quickActionPreferences, key, utcNow, MaximumQuickActionAdjustment);
    }

    public void RecordQuickActionSelection(string actionId, string contextKey, DateTime utcNow, bool learningEnabled, IEnumerable<string>? learningFeatures = null)
    {
        var trimmedId = actionId.Trim();
        if (!learningEnabled || !TryGetPreferenceKey(trimmedId, contextKey, out var key)) return;
        MarkAnalysisActivity(utcNow);

        lock (_gate) RecordPreference(_quickActionPreferences, MaximumQuickActionEntries, key, utcNow);
        TrainPositiveChoice(trimmedId, learningFeatures, utcNow);
    }

    public IReadOnlyList<QuickActionItem> RankQuickActions(IEnumerable<QuickActionItem> availableActions, bool learningEnabled, DateTime utcNow, int maximum = 3)
    {
        if (maximum <= 0) return Array.Empty<QuickActionItem>();

        lock (_gate)
        {
            return availableActions
                .Select((action, index) => new
                {
                    Action = action,
                    Index = index,
                    Score = action.BaseRelevance + (learningEnabled ? GetPersonalizedAdjustment(action, utcNow) : 0),
                })
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Index)
                .Take(maximum)
                .Select(entry => entry.Action)
                .ToArray();
        }
    }

    public double GetPaletteAdjustment(string commandId, DateTime utcNow)
    {
        if (!TryNormalizeIdentifier(commandId, out var key)) return 0;
        lock (_gate) return GetPreferenceAdjustment(_palettePreferences, key, utcNow, MaximumPaletteAdjustment);
    }

    public void RecordPaletteSelection(string commandId, DateTime utcNow, bool learningEnabled)
    {
        if (!learningEnabled || !TryNormalizeIdentifier(commandId, out var key)) return;
        MarkAnalysisActivity(utcNow);
        lock (_gate) RecordPreference(_palettePreferences, MaximumPaletteEntries, key, utcNow);
    }

    public static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var builder = new StringBuilder(query.Length);
        var pendingSeparator = false;
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '+' or '#')
            {
                if (pendingSeparator)
                {
                    builder.Append(' ');
                    pendingSeparator = false;
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0)
            {
                pendingSeparator = true;
            }
        }

        if (builder.Length < MinimumQueryLength || builder.Length > MaximumQueryLength) return null;
        return builder.ToString();
    }

    public bool RecordQueryAssociation(string? query, string commandId, DateTime utcNow, bool learningEnabled)
    {
        if (!learningEnabled) return false;
        var normalized = NormalizeQuery(query);
        if (normalized is null || !TryNormalizeIdentifier(commandId, out var key)) return false;
        MarkAnalysisActivity(utcNow);

        lock (_gate)
        {
            if (!_queryAssociations.TryGetValue(normalized, out var commands))
            {
                if (_queryAssociations.Count >= MaximumQueryEntries) EvictWeakestQuery(utcNow);
                commands = new Dictionary<string, Preference>(StringComparer.Ordinal);
                _queryAssociations[normalized] = commands;
            }

            RecordPreference(commands, MaximumCommandsPerQuery, key, utcNow, MaximumQueryWeight);
        }

        return true;
    }

    public double GetQueryAssociationAdjustment(string? normalizedQuery, string commandId, DateTime utcNow)
    {
        if (normalizedQuery is null || !TryNormalizeIdentifier(commandId, out var key)) return 0;

        lock (_gate)
        {
            if (!_queryAssociations.TryGetValue(normalizedQuery, out var commands)) return 0;

            return GetPreferenceAdjustment(commands, key, utcNow, MaximumQueryAssociationAdjustment);
        }
    }

    private void EvictWeakestQuery(DateTime utcNow)
    {
        string? weakestQuery = null;
        var weakestStrength = double.MaxValue;
        foreach (var pair in _queryAssociations)
        {
            double strength = 0;
            foreach (var preference in pair.Value.Values)
                strength += Decay(preference.Weight, preference.LastUsedUtcTicks, utcNow);
            if (strength >= weakestStrength) continue;
            weakestStrength = strength;
            weakestQuery = pair.Key;
        }

        if (weakestQuery is not null) _queryAssociations.Remove(weakestQuery);
    }

    public bool HasQueryAssociationData
    {
        get
        {
            lock (_gate) return _queryAssociations.Count > 0;
        }
    }

    public IReadOnlyList<InsightQuickActionPreference> GetQuickActionPreferences(DateTime utcNow)
    {
        lock (_gate)
        {
            var entries = new List<InsightQuickActionPreference>(_quickActionPreferences.Count);
            foreach (var pair in _quickActionPreferences)
            {
                var separator = pair.Key.IndexOf(KeySeparator, StringComparison.Ordinal);
                if (separator <= 0) continue;
                var strength = Strength(pair.Value, utcNow);
                if (strength <= 0) continue;
                entries.Add(new InsightQuickActionPreference(
                    pair.Key[(separator + KeySeparator.Length)..],
                    pair.Key[..separator],
                    strength,
                    new DateTime(pair.Value.LastUsedUtcTicks, DateTimeKind.Utc)));
            }

            return entries
                .OrderByDescending(entry => entry.Strength)
                .ThenBy(entry => entry.ContextKey, StringComparer.Ordinal)
                .ThenBy(entry => entry.ActionId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public IReadOnlyList<InsightPaletteCommandPreference> GetPaletteCommandPreferences(DateTime utcNow)
    {
        lock (_gate)
        {
            return _palettePreferences
                .Select(pair => new InsightPaletteCommandPreference(
                    pair.Key, Strength(pair.Value, utcNow), new DateTime(pair.Value.LastUsedUtcTicks, DateTimeKind.Utc)))
                .Where(entry => entry.Strength > 0)
                .OrderByDescending(entry => entry.Strength)
                .ThenBy(entry => entry.CommandId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public IReadOnlyList<InsightQueryAssociation> GetQueryAssociations(DateTime utcNow)
    {
        lock (_gate)
        {
            var entries = new List<InsightQueryAssociation>();
            foreach (var pair in _queryAssociations)
            foreach (var command in pair.Value)
            {
                var strength = Strength(command.Value, utcNow);
                if (strength <= 0) continue;
                entries.Add(new InsightQueryAssociation(
                    pair.Key, command.Key, strength, new DateTime(command.Value.LastUsedUtcTicks, DateTimeKind.Utc)));
            }

            return entries
                .OrderByDescending(entry => entry.Strength)
                .ThenBy(entry => entry.Query, StringComparer.Ordinal)
                .ThenBy(entry => entry.CommandId, StringComparer.Ordinal)
                .ToList();
        }
    }

    public bool RemoveQuickActionPreference(string actionId, string contextKey)
    {
        if (!TryGetPreferenceKey(actionId, contextKey, out var key)) return false;
        lock (_gate) return _quickActionPreferences.Remove(key);
    }

    public bool RemovePaletteCommandPreference(string commandId)
    {
        if (!TryNormalizeIdentifier(commandId, out var key)) return false;
        lock (_gate) return _palettePreferences.Remove(key);
    }

    public bool RemoveQueryAssociation(string? query, string commandId)
    {
        var normalized = NormalizeQuery(query);
        if (normalized is null || !TryNormalizeIdentifier(commandId, out var key)) return false;

        lock (_gate)
        {
            if (!_queryAssociations.TryGetValue(normalized, out var commands) || !commands.Remove(key)) return false;
            if (commands.Count == 0) _queryAssociations.Remove(normalized);
            return true;
        }
    }

    public void ResetQueryAssociations()
    {
        lock (_gate) _queryAssociations.Clear();
    }

    public void ResetQuickActions()
    {
        lock (_gate)
        {
            _quickActionPreferences.Clear();
            _model.Clear();
        }
    }

    public void ResetPalette()
    {
        lock (_gate)
        {
            _palettePreferences.Clear();
            _queryAssociations.Clear();
        }
    }

    public string BackupFilePath => FilePath + BackupSuffix;

    public bool HasBackup => File.Exists(BackupFilePath);

    public void CreateBackup()
    {
        Save();

        var payload = File.ReadAllText(FilePath);
        try { JsonNode.Parse(payload); }
        catch (Exception ex) when (ex is JsonException)
        {
            throw new InvalidDataException("Current Insight data is not readable JSON; refusing to back up a corrupt file.", ex);
        }

        WriteAtomically(BackupFilePath, payload);
    }

    public void RestoreBackup()
    {
        var backupPath = BackupFilePath;
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("No Insight backup file exists.", backupPath);

        var payload = File.ReadAllText(backupPath);
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidDataException("Insight backup file is empty.");

        try { JsonNode.Parse(payload); }
        catch (Exception ex) when (ex is JsonException)
        {
            throw new InvalidDataException("Insight backup is not readable JSON.", ex);
        }

        WriteAtomically(FilePath, payload);
    }

    public void DeleteBackup()
    {
        var backupPath = BackupFilePath;
        if (File.Exists(backupPath))
            File.Delete(backupPath);
    }

    public void Save()
    {
        lock (_gate)
        {
            var document = new LearningDocument
            {
                Version = DocumentVersion,
                Preferences = _quickActionPreferences,
                Model = _model,
                PalettePreferences = _palettePreferences,
                QueryAssociations = _queryAssociations,
                AnalysisActiveDays = _analysisActiveDays.ToList(),
            };
            WriteAtomically(FilePath, JsonSerializer.Serialize(document, JsonOptions));
        }
    }

    private static void WriteAtomically(string path, string payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Learning data path has no parent folder.");

        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (PlatformNotSupportedException) { }
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void LoadQueryAssociations(
        Dictionary<string, Dictionary<string, Preference>>? source,
        Dictionary<string, Dictionary<string, Preference>> target)
    {
        if (source is null) return;
        foreach (var pair in source
                     .Where(pair => pair.Value is not null &&
                                    pair.Key.Length <= MaximumQueryLength &&
                                    string.Equals(NormalizeQuery(pair.Key), pair.Key, StringComparison.Ordinal))
                     .OrderByDescending(pair => MostRecentUseTicks(pair.Value!))
                     .Take(MaximumQueryEntries))
        {
            var commands = new Dictionary<string, Preference>(StringComparer.Ordinal);
            LoadPreferences(pair.Value, commands, MaximumCommandsPerQuery, MaximumQueryWeight);
            if (commands.Count > 0) target[pair.Key] = commands;
        }
    }

    private static void LoadAnalysisDays(List<int>? source, SortedSet<int> target)
    {
        if (source is null) return;
        foreach (var day in source
                     .Where(day => day >= 0 && day < DateOnly.MaxValue.DayNumber)
                     .OrderBy(day => day))
        {
            target.Add(day);
            if (target.Count >= AnalysisPeriodDays) return;
        }
    }

    private static void SeedAnalysisDaysFromHistory(InsightLearningStore store)
    {
        var days = new SortedSet<int>();
        foreach (var preference in store._quickActionPreferences.Values)
            AddAnalysisDay(days, preference.LastUsedUtcTicks);
        foreach (var parameter in store._model.Values)
            AddAnalysisDay(days, parameter.LastUsedUtcTicks);
        foreach (var preference in store._palettePreferences.Values)
            AddAnalysisDay(days, preference.LastUsedUtcTicks);
        foreach (var commands in store._queryAssociations.Values)
        foreach (var preference in commands.Values)
            AddAnalysisDay(days, preference.LastUsedUtcTicks);

        foreach (var day in days.Take(AnalysisPeriodDays))
            store._analysisActiveDays.Add(day);
    }

    private static void AddAnalysisDay(SortedSet<int> days, long utcTicks)
    {
        if (utcTicks <= 0 || utcTicks > DateTime.MaxValue.Ticks) return;
        days.Add(DateOnly.FromDateTime(new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime()).DayNumber);
    }

    private static long MostRecentUseTicks(Dictionary<string, Preference> preferences)
    {
        long mostRecent = 0;
        foreach (var preference in preferences.Values)
            if (preference is not null && preference.LastUsedUtcTicks > mostRecent)
                mostRecent = preference.LastUsedUtcTicks;
        return mostRecent;
    }

    private static void LoadPreferences(Dictionary<string, Preference>? source, Dictionary<string, Preference> target, int maximum, double maximumWeight = MaximumWeight)
    {
        if (source is null) return;
        foreach (var pair in source
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 160 &&
                                    pair.Value is { Weight: > 0 } && double.IsFinite(pair.Value.Weight) &&
                                    pair.Value.LastUsedUtcTicks > 0 &&
                                    pair.Value.LastUsedUtcTicks <= DateTime.MaxValue.Ticks)
                     .OrderByDescending(pair => pair.Value.LastUsedUtcTicks)
                     .Take(maximum))
        {
            target[pair.Key] = new Preference
            {
                Weight = Math.Min(maximumWeight, pair.Value.Weight),
                LastUsedUtcTicks = pair.Value.LastUsedUtcTicks,
            };
        }
    }

    private static void RecordPreference(Dictionary<string, Preference> preferences, int maximum, string key, DateTime utcNow, double maximumWeight = MaximumWeight)
    {
        var ticks = utcNow.ToUniversalTime().Ticks;
        if (preferences.TryGetValue(key, out var existing))
        {
            var current = Decay(existing.Weight, existing.LastUsedUtcTicks, utcNow);
            existing.Weight = Math.Min(maximumWeight, current + 1);
            existing.LastUsedUtcTicks = ticks;
            return;
        }

        if (preferences.Count >= maximum)
        {
            var weakest = preferences.MinBy(pair => Decay(pair.Value.Weight, pair.Value.LastUsedUtcTicks, utcNow));
            if (!string.IsNullOrEmpty(weakest.Key)) preferences.Remove(weakest.Key);
        }

        preferences[key] = new Preference { Weight = 1, LastUsedUtcTicks = ticks };
    }

    private static double GetPreferenceAdjustment(Dictionary<string, Preference> preferences, string key, DateTime utcNow, double cap)
    {
        if (!preferences.TryGetValue(key, out var preference)) return 0;
        var weight = Decay(preference.Weight, preference.LastUsedUtcTicks, utcNow);
        return cap * (1 - Math.Exp(-weight / 3));
    }

    private void TrainPositiveChoice(string actionId, IEnumerable<string>? learningFeatures, DateTime utcNow)
    {
        var features = NormalizeFeatures(learningFeatures);
        var ticks = utcNow.ToUniversalTime().Ticks;
        lock (_gate)
        {
            var bias = ReadModelWeight(actionId, "bias", utcNow);
            var featureWeights = features.Select(feature => ReadModelWeight(actionId, feature, utcNow)).ToArray();
            var probability = Logistic(bias + featureWeights.Sum());
            var gradient = (1 - probability) * ModelLearningRate;
            UpdateModelWeight(actionId, "bias", bias + gradient - bias * ModelRegularization, ticks, countObservation: true);
            foreach (var (feature, weight) in features.Zip(featureWeights))
                UpdateModelWeight(actionId, feature, weight + gradient - weight * ModelRegularization, ticks);
        }
    }

    private double GetModelAdjustment(string actionId, string[] features, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return 0;
        var logit = ReadModelWeight(actionId, "bias", utcNow) +
                    features.Sum(feature => ReadModelWeight(actionId, feature, utcNow));
        return Math.Clamp((Logistic(logit) - 0.5) * 3, 0, MaximumPaletteAdjustment);
    }

    private double GetPersonalizedAdjustment(QuickActionItem action, DateTime utcNow)
    {
        var contextual = GetPreferenceAdjustment(_quickActionPreferences, action.ContextKey.Trim() + KeySeparator + action.Id.Trim(), utcNow, MaximumQuickActionAdjustment);
        var biasKey = action.Id + KeySeparator + "bias";
        if (!_model.TryGetValue(biasKey, out var bias) || bias.Observations < MinimumModelObservations)
            return contextual;

        var features = NormalizeFeatures(action.LearningFeatures);
        if (!features.Any(feature => ReadModelWeight(action.Id, feature, utcNow) > 0))
            return contextual;

        var ramp = Math.Min(1, bias.Observations / (double)(MinimumModelObservations * 2));
        var modelled = GetModelAdjustment(action.Id, features, utcNow) * ramp;
        return Math.Clamp(Math.Max(contextual, modelled), 0, MaximumQuickActionAdjustment);
    }

    private double ReadModelWeight(string actionId, string feature, DateTime utcNow)
    {
        var key = actionId + KeySeparator + feature;
        if (!_model.TryGetValue(key, out var parameter)) return 0;

        var elapsedDays = Math.Max(0, (utcNow.ToUniversalTime().Ticks - parameter.LastUsedUtcTicks) / (double)TimeSpan.TicksPerDay);
        var decayed = parameter.Weight * Math.Pow(0.5, elapsedDays / ModelHalfLifeDays);
        return double.IsFinite(decayed) ? Math.Clamp(decayed, -MaximumModelWeight, MaximumModelWeight) : 0;
    }

    private void UpdateModelWeight(string actionId, string feature, double weight, long ticks, bool countObservation = false)
    {
        var key = actionId + KeySeparator + feature;
        if (!_model.ContainsKey(key) && _model.Count >= MaximumModelParameters)
        {
            var weakest = _model.MinBy(pair => Math.Abs(pair.Value.Weight));
            if (!string.IsNullOrEmpty(weakest.Key)) _model.Remove(weakest.Key);
        }
        var observations = _model.TryGetValue(key, out var existing) ? existing.Observations : 0;
        _model[key] = new ModelParameter
        {
            Weight = Math.Clamp(weight, -MaximumModelWeight, MaximumModelWeight),
            LastUsedUtcTicks = ticks,
            Observations = Math.Min(MaximumQuickActionEntries, observations + (countObservation ? 1 : 0)),
        };
    }

    private static string[] NormalizeFeatures(IEnumerable<string>? features) => (features ?? [])
        .Where(feature => !string.IsNullOrWhiteSpace(feature) && feature.Length <= 80 && !feature.Contains(Path.DirectorySeparatorChar) && !feature.Contains(Path.AltDirectorySeparatorChar))
        .Select(feature => feature.Trim())
        .Distinct(StringComparer.Ordinal)
        .Take(16)
        .ToArray();

    private static double Logistic(double value)
    {
        var clamped = Math.Clamp(value, -12, 12);
        return 1 / (1 + Math.Exp(-clamped));
    }

    private static double Strength(Preference preference, DateTime utcNow) =>
        1 - Math.Exp(-Decay(preference.Weight, preference.LastUsedUtcTicks, utcNow) / 3);

    private static double Decay(double weight, long lastUsedUtcTicks, DateTime utcNow)
    {
        var usedAt = new DateTime(lastUsedUtcTicks, DateTimeKind.Utc);
        var elapsedDays = Math.Max(0, (utcNow.ToUniversalTime() - usedAt).TotalDays);
        return Math.Clamp(weight, 0, MaximumWeight) * Math.Pow(0.5, elapsedDays / PreferenceHalfLifeDays);
    }

    private static bool TryGetPreferenceKey(string actionId, string contextKey, out string key)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(contextKey))
        {
            key = string.Empty;
            return false;
        }

        key = contextKey.Trim() + KeySeparator + actionId.Trim();
        return key.Length <= 160;
    }

    private static bool TryNormalizeIdentifier(string? value, out string key)
    {
        if (string.IsNullOrWhiteSpace(value)) { key = string.Empty; return false; }
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 120 || trimmed.Contains(KeySeparator) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            key = string.Empty;
            return false;
        }
        key = trimmed;
        return true;
    }

    private sealed class LearningDocument
    {
        public LearningDocument() { }
        public int Version { get; set; }
        public Dictionary<string, Preference>? Preferences { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelParameter>? Model { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Preference>? PalettePreferences { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, Preference>>? QueryAssociations { get; set; } = new(StringComparer.Ordinal);
        public List<int>? AnalysisActiveDays { get; set; } = new();
    }

    private sealed class Preference
    {
        public Preference() { }
        public double Weight { get; set; }
        public long LastUsedUtcTicks { get; set; }
    }

    private sealed class ModelParameter
    {
        public ModelParameter() { }
        public double Weight { get; set; }
        public long LastUsedUtcTicks { get; set; }
        public int Observations { get; set; }
    }
}
