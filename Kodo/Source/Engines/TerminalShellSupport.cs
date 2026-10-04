// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;

namespace Kodo;

internal static class BrushCache
{
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> _cache = new();
    public static SolidColorBrush Get(Color c)
    {
        var key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        return _cache.GetOrAdd(key, _ => new SolidColorBrush(c));
    }
}

internal static class PenCache
{
    private const int MaxEntries = 512;
    private static readonly ConcurrentDictionary<(uint Color, double Thickness), Pen> _cache = new();

    public static Pen Get(Color c, double thickness = 1)
    {
        var key = (((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B, thickness);
        return _cache.GetOrAdd(key, _ =>
        {
            if (_cache.Count >= MaxEntries) _cache.Clear();
            return new Pen(BrushCache.Get(c), thickness);
        });
    }
}

internal static class TerminalGlyphCache
{
    private const int MaxEntries = 4096;
    private static readonly ConcurrentDictionary<(string Text, uint Color, bool Bold, double Size), FormattedText> _cache = new();

    public static FormattedText Get(string text, Color foreground, bool bold, double fontSize)
    {
        var key = (text,
                   ((uint)foreground.A << 24) | ((uint)foreground.R << 16) | ((uint)foreground.G << 8) | foreground.B,
                   bold,
                   fontSize);
        return _cache.GetOrAdd(key, _ =>
        {
            if (_cache.Count >= MaxEntries) _cache.Clear();
            return new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                bold ? Typefaces.Bold : Typefaces.Regular,
                fontSize,
                BrushCache.Get(foreground));
        });
    }
}

internal static class Typefaces
{
    public static readonly Typeface Regular = new("JetBrains Mono,DejaVu Sans Mono,Ubuntu Mono,Noto Sans Mono,Cascadia Mono,Consolas,Courier New,monospace", FontStyle.Normal, FontWeight.Regular);
    public static readonly Typeface Bold = new("JetBrains Mono,DejaVu Sans Mono,Ubuntu Mono,Noto Sans Mono,Cascadia Mono,Consolas,Courier New,monospace", FontStyle.Normal, FontWeight.Bold);
}

public static class TerminalShellSupport
{
    private const double MinPanelHeight = 120;
    private const double MaxPanelHeight = 420;

    private const string BashCwdHookCommand = """
        export PROMPT_COMMAND='printf \"\033]1337;CurrentDir=%s\007\" \"$(pwd -W)\"'; exec bash --login -i
        """;

    public static IEnumerable<TerminalShellOption> DetectTerminalShells(bool enablePSReadLinePrediction = false)
    {
        var shells = new List<TerminalShellOption>();

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(FileSystemPaths.Comparer);

        void AddShell(string id, string displayName, string? resolvedPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(resolvedPath)) return;
            if (!File.Exists(resolvedPath)) return;
            if (!seenIds.Add(id)) return;
            if (!seenPaths.Add(resolvedPath)) return;

            shells.Add(new TerminalShellOption
            {
                Id = id,
                DisplayName = displayName,
                FileName = resolvedPath,
                Arguments = arguments
            });
        }

        static string DisplayNameFor(string baseName) => baseName switch
        {
            "bash" => "Bash",
            "zsh" => "Zsh",
            "fish" => "Fish",
            "sh" => "Shell",
            "dash" => "Shell",
            "powershell" or "pwsh" => "PowerShell",
            _ => char.ToUpperInvariant(baseName[0]) + baseName[1..]
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var predictionCommand = enablePSReadLinePrediction
                ? "try { Set-PSReadLineOption -PredictionSource HistoryAndPlugin } catch { try { Set-PSReadLineOption -PredictionSource History } catch {} }; "
                : "try { Set-PSReadLineOption -PredictionSource None } catch {}; ";

            const string reportCwdCommand =
                "if (-not $global:__KodoOrigPrompt) { $global:__KodoOrigPrompt = $function:prompt }; " +
                "function global:prompt { " +
                "try { [Console]::Out.Write([char]27 + ']1337;CurrentDir=' + (Get-Location).Path + [char]7) } catch {}; " +
                "& $global:__KodoOrigPrompt }";

            AddShell(
                "powershell",
                "PowerShell",
                ResolveExecutable("pwsh.exe"),
                $"-NoLogo -NoExit -Command \"{predictionCommand}{reportCwdCommand}\"");
            AddShell(
                "windows-powershell",
                "Windows PowerShell",
                ResolveExecutable("powershell.exe", Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe")),
                $"-NoLogo -NoExit -Command \"{predictionCommand}{reportCwdCommand}\"");
            AddShell(
                "windows-powershell",
                "Windows PowerShell",
                ResolveExecutable("powershell.exe", Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe")),
                $"-NoLogo -NoExit -Command \"{predictionCommand}{reportCwdCommand}\"");
            AddShell(
                "cmd",
                "Command Prompt",
                ResolveExecutable(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                    ?? ResolveExecutable("cmd.exe"),
                "/K prompt $E]1337;CurrentDir=$P\a$P$G");
            AddShell(
                "bash",
                "Git Bash",
                ResolveExecutable("bash.exe",
                    @"C:\Program Files\Git\bin\bash.exe",
                    @"C:\Program Files\Git\usr\bin\bash.exe"),
                $"--login -i -c \"{BashCwdHookCommand}\"");
        }
        else
        {
            var loginShell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrWhiteSpace(loginShell))
            {
                var baseName = Path.GetFileName(loginShell.Trim()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(baseName))
                    AddShell(baseName, DisplayNameFor(baseName), ResolveExecutable(loginShell.Trim(), loginShell.Trim()), "-i");
            }
            AddShell("bash", "Bash", ResolveExecutable("bash"), "-i");
            AddShell("zsh", "Zsh", ResolveExecutable("zsh"), "-i");
            AddShell("fish", "Fish", ResolveExecutable("fish"), "-i");
            AddShell("powershell", "PowerShell", ResolveExecutable("pwsh"), "-NoLogo");
            AddShell("sh", "Shell", ResolveExecutable("sh"), "-i");
        }

        return shells;
    }

    public static string GetDefaultShellId()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var loginShell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrWhiteSpace(loginShell))
            {
                var baseName = Path.GetFileName(loginShell.Trim()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(baseName))
                    return baseName;
            }
        }
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "bash";
    }

    private static string? ResolveExecutable(string fileName, params string[] fallbacks)
    {
        if (Path.IsPathFullyQualified(fileName) && File.Exists(fileName))
            return fileName;

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (Path.HasExtension(fileName))
            {
                var candidate = Path.Combine(path, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            else
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(path, fileName + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        foreach (var fallback in fallbacks)
        {
            if (!string.IsNullOrWhiteSpace(fallback) && File.Exists(fallback))
                return fallback;
        }

        return null;
    }

    public static string GetClearCommandForShell(string shellId) =>
        shellId switch
        {
            "bash" or "zsh" or "sh" or "fish" => "clear\r",
            _ => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cls\r" : "clear\r"
        };

    public static double NormalizeTerminalPanelHeight(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinPanelHeight, MaxPanelHeight)
            : AppSettings.DefaultTerminalPanelHeight;

    public static string ExtractDisplayTitleFromShellTitle(string rawTitle)
    {
        if (!LooksLikeFilePath(rawTitle))
            return rawTitle;

        var trimmedPath = rawTitle.TrimEnd('\\', '/');

        if (File.Exists(trimmedPath) || (!Directory.Exists(trimmedPath) && Path.HasExtension(trimmedPath)))
        {
            var parentDir = Path.GetDirectoryName(trimmedPath);
            var parentName = string.IsNullOrEmpty(parentDir) ? null : Path.GetFileName(parentDir);
            if (!string.IsNullOrWhiteSpace(parentName))
                return parentName;
        }

        var lastSegment = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(lastSegment) ? rawTitle : lastSegment;
    }

    private static bool LooksLikeFilePath(string text)
    {
        if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
            return true;
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        if (text.StartsWith("/", StringComparison.Ordinal) && text.Contains('/'))
            return true;
        return false;
    }
}
