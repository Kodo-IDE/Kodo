// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using System.Text.Json;
using Kodo.Models;

namespace Kodo;

internal static class SettingsStore
{
    public const string FileName = "kodosettings.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 32 };
    private static readonly object Gate = new();
    private static AppSettings? _current;

    public static string FilePath => KodoPaths.SettingsFilePath(FileName);

    private static string WritePath => KodoPaths.SettingsWritePath(FileName);

    public static AppSettings Current
    {
        get
        {
            lock (Gate) { return _current ??= ReadFromDisk(); }
        }
    }

    public static void Publish(AppSettings snapshot)
    {
        if (snapshot is null) return;
        lock (Gate) { _current = snapshot; }
    }

    public static void WriteToDisk(AppSettings snapshot)
    {
        if (snapshot is null) return;
        try
        {
            var writePath = WritePath;
            var dir = Path.GetDirectoryName(writePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var tempPath = writePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot));
            File.Move(tempPath, writePath, overwrite: true);

            lock (Gate) { _current = snapshot; }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("SettingsStore.WriteToDisk", ex, operation: $"Failed to save settings to '{FilePath}'");
        }
    }

    public static void Reload()
    {
        lock (Gate) { _current = null; }
    }

    private static AppSettings ReadFromDisk()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new AppSettings();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

            if (json.Contains("\"CodePredictEnabled\"", StringComparison.Ordinal))
                json = json.Replace("\"CodePredictEnabled\"", "\"InsightEnabled\"", StringComparison.Ordinal);

            return JsonSerializer.Deserialize<AppSettings>(json, ReadOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("SettingsStore.ReadFromDisk", ex, operation: $"Failed to load settings from '{FilePath}'");
            return new AppSettings();
        }
    }

    public static ThemeSettings Theme => new()
    {
        ThemeName = Current.ThemeName,
        CachedThemeWindowBackgroundHex = Current.CachedThemeWindowBackgroundHex,
    };

    public static AccentSettings Accent => new()
    {
        AccentColorMode = Current.AccentColorMode,
        CustomAccentHex = Current.CustomAccentHex,
        CachedThemeAccentHex = Current.CachedThemeAccentHex,
    };

    public static AutoUpdateSettings AutoUpdate => new()
    {
        AutoUpdateAppEnabled = Current.AutoUpdateAppEnabled,
        AutoUpdateAppInBackgroundEnabled = Current.AutoUpdateAppInBackgroundEnabled,
    };
}
