// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kodo;

internal static class SettingsStore
{
    public const string FileName = "kodosettings.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 32 };
    private static readonly object Gate = new();
    private static AppSettings? _current;
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string FilePath => KodoPaths.SettingsFilePath(FileName);

    private static string WritePath => KodoPaths.SettingsWritePath(FileName);

    /// <summary>Path of the on-disk settings backup (kodosettings.json.bak).</summary>
    public static string BackupFilePath => WritePath + ".bak";

    /// <summary>True when a settings backup file exists on disk.</summary>
    public static bool HasBackup => File.Exists(BackupFilePath);

    /// <summary>
    /// Regenerates the settings backup file from the settings currently saved on
    /// disk. When no settings file exists yet, backs up the in-memory snapshot
    /// instead so a restore can still recover the current state.
    /// </summary>
    public static void CreateBackup()
    {
        var writePath = WritePath;
        var dir = Path.GetDirectoryName(writePath);
        if (string.IsNullOrWhiteSpace(dir))
            throw new IOException("Settings path has no parent directory.");

        EnsurePrivateDirectory(dir);

        var payload = File.Exists(writePath)
            ? File.ReadAllText(writePath)
            : JsonSerializer.Serialize(Current);
        if (string.IsNullOrWhiteSpace(payload))
            payload = JsonSerializer.Serialize(new AppSettings());

        try { JsonNode.Parse(payload); }
        catch (Exception ex) when (ex is JsonException)
        {
            throw new InvalidDataException("Current settings are not readable JSON; refusing to back up a corrupt file.", ex);
        }

        AtomicWrite(BackupFilePath, payload);
    }

    /// <summary>
    /// Overwrites the current settings file with the on-disk backup after
    /// validating it, and clears the in-memory snapshot so the next
    /// <see cref="Current"/> read reloads from disk.
    /// </summary>
    public static void RestoreBackup()
    {
        var backupPath = BackupFilePath;
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("No settings backup file exists.", backupPath);

        var payload = File.ReadAllText(backupPath);
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidDataException("Settings backup file is empty.");

        try { JsonNode.Parse(payload); }
        catch (Exception ex) when (ex is JsonException)
        {
            throw new InvalidDataException("Settings backup is not readable JSON.", ex);
        }

        AtomicWrite(WritePath, payload);
        Reload();
    }

    /// <summary>Atomic write (temp file + move) used for settings and backup files.</summary>
    private static void AtomicWrite(string path, string payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(dir))
            throw new IOException("Settings path has no parent directory.");

        EnsurePrivateDirectory(dir);

        var tempPath = path + ".tmp";
        try
        {
            if (File.Exists(tempPath))
                EnsurePrivateFile(tempPath);

            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = PrivateFileMode;

            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            EnsurePrivateFile(tempPath);
            File.Move(tempPath, path, overwrite: true);
            EnsurePrivateFile(path);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

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
            if (string.IsNullOrWhiteSpace(dir))
                throw new IOException("Settings path has no parent directory.");

            EnsurePrivateDirectory(dir);

            var tempPath = writePath + ".tmp";
            try
            {
                if (File.Exists(tempPath))
                    EnsurePrivateFile(tempPath);

                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = PrivateFileMode;

                using (var stream = new FileStream(tempPath, options))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(snapshot));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                EnsurePrivateFile(tempPath);
                File.Move(tempPath, writePath, overwrite: true);
                EnsurePrivateFile(writePath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

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

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                EnsurePrivateDirectory(dir);
            EnsurePrivateFile(path);

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(MigrateLegacySettingsJson(json), ReadOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogWarning("SettingsStore.ReadFromDisk", ex, operation: $"Failed to load settings from '{FilePath}'");
            return new AppSettings();
        }
    }

    private static string MigrateLegacySettingsJson(string json)
    {
        if (!json.Contains("\"CodePredictEnabled\"", StringComparison.Ordinal))
            return json;
        if (JsonNode.Parse(json) is not JsonObject root)
            return json;

        string? legacyKey = null;
        JsonNode? legacyValue = null;
        foreach (var property in root)
        {
            if (!string.Equals(property.Key, "CodePredictEnabled", StringComparison.Ordinal))
                continue;
            legacyKey = property.Key;
            legacyValue = property.Value?.DeepClone();
            break;
        }
        if (legacyKey is null)
            return json;

        root.Remove(legacyKey);
        if (legacyValue is not null && !root.ContainsKey("InsightEnabled"))
            root["InsightEnabled"] = legacyValue;
        return root.ToJsonString();
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("Could not identify the current Windows user.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        ApplyUnixModeBestEffort(path, PrivateDirectoryMode);
    }

    private static void EnsurePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("Could not identify the current Windows user.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        ApplyUnixModeBestEffort(path, PrivateFileMode);
    }

    private static void ApplyUnixModeBestEffort(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException or NotSupportedException)
        {
            KodoDiagnostics.LogDebug($"SettingsStore: '{path}' does not support POSIX permission bits; leaving file modes unchanged.", ex);
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
