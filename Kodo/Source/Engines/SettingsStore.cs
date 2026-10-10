// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Kodo.Models;

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

        File.SetUnixFileMode(path, PrivateDirectoryMode);
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

        File.SetUnixFileMode(path, PrivateFileMode);
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
