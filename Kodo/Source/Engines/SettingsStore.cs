// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using System.Text.Json;
using Kodo.Models;

namespace Kodo;

/// <summary>
/// Single owner of <c>kodosettings.json</c>: the path, the on-disk read, the
/// write, and an in-memory copy of the last known settings.
/// </summary>
/// <remarks>
/// Previously four independent code paths read or wrote this file, three of
/// which each declared their own copy of the file-name constant. Two of them
/// (<c>AccentResolver</c> and <c>ThemeResolver</c>) read straight from disk on
/// every call, so any reader could observe settings that were older than the
/// ones the window was already displaying - the persisted copy trails the live
/// one by the length of the save debounce. They now project from
/// <see cref="Current"/>, which <see cref="Publish"/> updates synchronously the
/// moment a snapshot is taken, so readers never see a stale value.
/// <para>
/// Persisting stays debounced and asynchronous, driven by the caller; this type
/// only owns the IO.
/// </para>
/// </remarks>
internal static class SettingsStore
{
    public const string FileName = "kodosettings.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 32 };
    private static readonly object Gate = new();
    private static AppSettings? _current;

    public static string FilePath => KodoPaths.SettingsFilePath(FileName);

    private static string WritePath => KodoPaths.SettingsWritePath(FileName);

    /// <summary>
    /// The most recently loaded or published settings. Loads from disk on first
    /// use; never hits the disk again unless <see cref="Reload"/> is called.
    /// </summary>
    public static AppSettings Current
    {
        get
        {
            lock (Gate) { return _current ??= ReadFromDisk(); }
        }
    }

    /// <summary>
    /// Makes a snapshot visible to every reader immediately, without waiting for
    /// the debounced write to land. Call this as soon as settings change, then
    /// persist on whatever schedule the caller prefers.
    /// </summary>
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

            // Write-then-rename so a crash mid-write cannot truncate settings.
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

    /// <summary>Drops the cached copy so the next read comes from disk.</summary>
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

            // Settings written by older builds used a different key name.
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

    /// <summary>Projects the cached settings into the theme-only view.</summary>
    public static ThemeSettings Theme => new()
    {
        ThemeName = Current.ThemeName,
        CachedThemeWindowBackgroundHex = Current.CachedThemeWindowBackgroundHex,
    };

    /// <summary>Projects the cached settings into the accent-only view.</summary>
    public static AccentSettings Accent => new()
    {
        AccentColorMode = Current.AccentColorMode,
        CustomAccentHex = Current.CustomAccentHex,
        CachedThemeAccentHex = Current.CachedThemeAccentHex,
    };

    /// <summary>Projects the cached settings into the auto-update-only view.</summary>
    public static AutoUpdateSettings AutoUpdate => new()
    {
        AutoUpdateAppEnabled = Current.AutoUpdateAppEnabled,
        AutoUpdateAppInBackgroundEnabled = Current.AutoUpdateAppInBackgroundEnabled,
    };
}
