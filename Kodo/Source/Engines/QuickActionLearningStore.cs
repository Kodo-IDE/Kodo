// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Kodo;

public sealed class QuickActionLearningStore
{
    private const int CurrentVersion = 2;
    private const int MaximumEntries = 128;
    private const int MaximumModelParameters = 4096;
    private const double MaximumWeight = 64;
    private const double HalfLifeDays = 45;
    private const double MaximumAdjustment = 1.5;
    private const double ModelHalfLifeDays = 90;
    private const double ModelLearningRate = 0.35;
    private const double ModelRegularization = 0.01;
    private const double MaximumModelWeight = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    private readonly Dictionary<string, Preference> _preferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelParameter> _model = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public QuickActionLearningStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }
    public bool HasPreferences { get { lock (_gate) return _preferences.Count > 0 || _model.Count > 0; } }
    public bool LoadFailed { get; private set; }

    public static QuickActionLearningStore Load(string filePath)
    {
        var store = new QuickActionLearningStore(filePath);
        if (!File.Exists(filePath)) return store;

        try
        {
            var document = JsonSerializer.Deserialize<LearningDocument>(File.ReadAllText(filePath), JsonOptions);
            if (document is null || document.Version is < 1 or > CurrentVersion || document.Preferences is null) return store;

            foreach (var pair in document.Preferences
                         .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { Weight: > 0 } &&
                                        double.IsFinite(pair.Value.Weight) && pair.Value.LastUsedUtcTicks > 0 &&
                                        pair.Value.LastUsedUtcTicks <= DateTime.MaxValue.Ticks)
                         .OrderByDescending(pair => pair.Value.LastUsedUtcTicks)
                         .Take(MaximumEntries))
            {
                store._preferences[pair.Key] = new Preference
                {
                    Weight = Math.Min(MaximumWeight, pair.Value.Weight),
                    LastUsedUtcTicks = pair.Value.LastUsedUtcTicks,
                };
            }
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
                        Observations = Math.Clamp(pair.Value.Observations, 0, MaximumEntries),
                    };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            store.LoadFailed = true;
            store._preferences.Clear();
        }

        return store;
    }

    public double GetAdjustment(string actionId, string contextKey, DateTime utcNow)
    {
        if (!TryGetPreferenceKey(actionId, contextKey, out var key)) return 0;

        lock (_gate)
        {
            if (!_preferences.TryGetValue(key, out var preference)) return 0;
            var weight = Decay(preference.Weight, preference.LastUsedUtcTicks, utcNow);
            return Math.Min(MaximumAdjustment, Math.Sqrt(weight) * 0.3);
        }
    }

    public void RecordSelection(string actionId, string contextKey, DateTime utcNow, bool learningEnabled, IEnumerable<string>? learningFeatures = null)
    {
        if (!learningEnabled || !TryGetPreferenceKey(actionId, contextKey, out var key)) return;

        lock (_gate)
        {
            var ticks = utcNow.ToUniversalTime().Ticks;
            if (_preferences.TryGetValue(key, out var existing))
            {
                var current = Decay(existing.Weight, existing.LastUsedUtcTicks, utcNow);
                existing.Weight = Math.Min(MaximumWeight, current + 1);
                existing.LastUsedUtcTicks = ticks;
            }
            else
            {
                if (_preferences.Count >= MaximumEntries)
                {
                    var weakest = _preferences.MinBy(pair => Decay(pair.Value.Weight, pair.Value.LastUsedUtcTicks, utcNow));
                    if (!string.IsNullOrEmpty(weakest.Key)) _preferences.Remove(weakest.Key);
                }

                _preferences[key] = new Preference { Weight = 1, LastUsedUtcTicks = ticks };
            }
        }

        TrainPositiveChoice(actionId.Trim(), learningFeatures, utcNow);
    }

    public IReadOnlyList<QuickActionItem> Rank(IEnumerable<QuickActionItem> availableActions, bool learningEnabled, DateTime utcNow, int maximum = 3)
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

    public void Reset()
    {
        lock (_gate)
        {
            _preferences.Clear();
            _model.Clear();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Learning data path has no parent folder.");

            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (PlatformNotSupportedException) { }
            }

            var document = new LearningDocument { Version = CurrentVersion, Preferences = _preferences, Model = _model };
            var payload = JsonSerializer.Serialize(document, JsonOptions);
            var tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

                File.Move(tempPath, FilePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
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

    private double GetModelAdjustment(string actionId, IEnumerable<string>? learningFeatures, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return 0;
        var features = NormalizeFeatures(learningFeatures);
        lock (_gate)
        {
            var logit = ReadModelWeight(actionId, "bias", utcNow) +
                        features.Sum(feature => ReadModelWeight(actionId, feature, utcNow));
            return Math.Clamp((Logistic(logit) - 0.5) * 3, 0, MaximumAdjustment);
        }
    }

    private double GetPersonalizedAdjustment(QuickActionItem action, DateTime utcNow)
    {
        var biasKey = action.Id + "\u001f" + "bias";
        if (_model.TryGetValue(biasKey, out var bias) && bias.Observations >= 3)
            return GetModelAdjustment(action.Id, action.LearningFeatures, utcNow);
        return GetAdjustment(action.Id, action.ContextKey, utcNow);
    }

    private double ReadModelWeight(string actionId, string feature, DateTime utcNow)
    {
        var key = actionId + "\u001f" + feature;
        return _model.TryGetValue(key, out var parameter)
            ? parameter.Weight * Math.Pow(0.5, Math.Max(0, (utcNow.ToUniversalTime().Ticks - parameter.LastUsedUtcTicks) / (double)TimeSpan.TicksPerDay) / ModelHalfLifeDays)
            : 0;
    }

    private void UpdateModelWeight(string actionId, string feature, double weight, long ticks, bool countObservation = false)
    {
        var key = actionId + "\u001f" + feature;
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
            Observations = Math.Min(MaximumEntries, observations + (countObservation ? 1 : 0)),
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

    private static double Decay(double weight, long lastUsedUtcTicks, DateTime utcNow)
    {
        var usedAt = new DateTime(lastUsedUtcTicks, DateTimeKind.Utc);
        var elapsedDays = Math.Max(0, (utcNow.ToUniversalTime() - usedAt).TotalDays);
        return Math.Clamp(weight, 0, MaximumWeight) * Math.Pow(0.5, elapsedDays / HalfLifeDays);
    }

    private static bool TryGetPreferenceKey(string actionId, string contextKey, out string key)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(contextKey))
        {
            key = string.Empty;
            return false;
        }

        key = contextKey.Trim() + "\u001f" + actionId.Trim();
        return key.Length <= 160;
    }

    private sealed class LearningDocument
    {
        public LearningDocument() { }
        public int Version { get; set; }
        public Dictionary<string, Preference>? Preferences { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModelParameter>? Model { get; set; } = new(StringComparer.Ordinal);
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
