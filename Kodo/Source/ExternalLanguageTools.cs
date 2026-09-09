using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kodo.Models;

namespace Kodo;

public sealed record ExternalToolDiagnostic(int Start, int Length, string Message, string Severity, string Code, string Source);

public static class ExternalLanguageToolRunner
{
    private static readonly Regex CompilerLine = new(
        @"^(?:(?<gccfile>.*?):(?<line>\d+):(?<column>\d+)|(?<msfile>.*)\((?<msline>\d+),(?<mscolumn>\d+)\))(?::\s*(?<severity>error|warning|info|note))?\s*:?[ \t]*(?:(?<code>[A-Za-z_][\w.-]*)\s*:)?\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PythonTracebackLine = new(
        @"^\s*File\s+[""'](?<file>.*?)[""'],\s+line\s+(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PythonDiagnosticLine = new(
        @"^(?<kind>SyntaxError|IndentationError|TabError|NameError|TypeError|ImportError|ModuleNotFoundError|UnboundLocalError)\s*:\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<IReadOnlyList<ExternalToolDiagnostic>> AnalyzeAsync(
        LoadedExtension? extension,
        string? filePath,
        string documentText,
        CancellationToken cancellationToken = default)
    {
        if (extension?.ExternalTools is not { Count: > 0 } tools || string.IsNullOrWhiteSpace(documentText))
            return [];

        var results = new List<ExternalToolDiagnostic>();
        foreach (var tool in tools.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Command)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryFile = CreateTemporarySource(filePath, documentText);
            try
            {
                var projectFile = FindProjectFile(filePath, tool.ProjectFiles);
                if (tool.RequiresProject && projectFile is null)
                    continue;
                var output = await RunToolAsync(tool, temporaryFile, filePath, projectFile, cancellationToken).ConfigureAwait(false);
                results.AddRange(ParseOutput(output, tool, documentText, filePath, projectFile));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug($"External checker '{tool.Id}' failed for '{filePath ?? "<untitled>"}'.", ex);
            }
            finally
            {
                try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); } catch { }
            }
        }
        return results;
    }

    private static string CreateTemporarySource(string? filePath, string text)
    {
        var extension = Path.GetExtension(filePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
            extension = ".txt";
        var path = Path.Combine(Path.GetTempPath(), $"kodo-language-tool-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<string> RunToolAsync(
        ExternalLanguageTool tool,
        string temporaryFile,
        string? originalFile,
        string? projectFile,
        CancellationToken cancellationToken)
    {
        var arguments = string.Join(" ", tool.Arguments.Select(argument => QuoteArgument(
            argument.Replace("{file}", temporaryFile, StringComparison.OrdinalIgnoreCase)
                .Replace("{fileName}", Path.GetFileName(originalFile ?? temporaryFile), StringComparison.OrdinalIgnoreCase))));
        arguments = arguments.Replace("{project}", projectFile ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{projectName}", Path.GetFileName(projectFile ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = tool.Command,
            Arguments = arguments,
            WorkingDirectory = Directory.Exists(Path.GetDirectoryName(projectFile ?? originalFile))
                ? Path.GetDirectoryName(projectFile ?? originalFile)!
                : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return string.Empty;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
    }

    private static string? FindProjectFile(string? filePath, IReadOnlyList<string> projectNames)
    {
        if (string.IsNullOrWhiteSpace(filePath) || projectNames.Count == 0)
            return null;
        var directory = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            foreach (var projectName in projectNames)
            {
                if (projectName.Contains('*'))
                {
                    var match = Directory.EnumerateFiles(directory, projectName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (match is not null) return match;
                    continue;
                }
                var candidate = Path.Combine(directory, projectName);
                if (File.Exists(candidate)) return candidate;
            }
            var parent = Directory.GetParent(directory);
            if (parent is null || string.Equals(parent.FullName, directory, StringComparison.OrdinalIgnoreCase)) break;
            directory = parent.FullName;
        }
        return null;
    }

    private static IEnumerable<ExternalToolDiagnostic> ParseOutput(string output, ExternalLanguageTool tool, string documentText, string? originalFile, string? projectFile)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        if (string.Equals(tool.Format, "json", StringComparison.OrdinalIgnoreCase))
            return ParseJson(output, tool, documentText);

        var results = new List<ExternalToolDiagnostic>();
        var pendingPythonLine = -1;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            var traceback = PythonTracebackLine.Match(trimmed);
            if (traceback.Success)
            {
                _ = int.TryParse(traceback.Groups["line"].Value, out pendingPythonLine);
                continue;
            }

            var python = PythonDiagnosticLine.Match(trimmed);
            if (python.Success && pendingPythonLine > 0)
            {
                results.Add(new ExternalToolDiagnostic(
                    OffsetAtLineColumn(documentText, pendingPythonLine, 1), 1,
                    python.Groups["message"].Value.Trim(),
                    python.Groups["kind"].Value.Equals("NameError", StringComparison.OrdinalIgnoreCase) ? "warning" : "error",
                    python.Groups["kind"].Value, tool.Id));
                pendingPythonLine = -1;
                continue;
            }

            var match = CompilerLine.Match(trimmed);
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["message"].Value)) continue;
            var lineNumber = match.Groups["line"].Success ? match.Groups["line"].Value : match.Groups["msline"].Value;
            var columnNumber = match.Groups["column"].Success ? match.Groups["column"].Value : match.Groups["mscolumn"].Value;
            var reportedFile = match.Groups["gccfile"].Success ? match.Groups["gccfile"].Value : match.Groups["msfile"].Value;
            if (projectFile is not null && !IsDiagnosticForFile(reportedFile, originalFile, projectFile))
                continue;
            var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
            {
                "warning" => "warning",
                "info" => "info",
                "note" => "hint",
                _ => "error",
            };
            results.Add(new ExternalToolDiagnostic(
                OffsetAtLineColumn(documentText, int.Parse(lineNumber), int.Parse(columnNumber)),
                1,
                match.Groups["message"].Value.Trim(),
                severity,
                match.Groups["code"].Success ? match.Groups["code"].Value : tool.Id,
                tool.Id));
        }
        return results;
    }

    private static bool IsDiagnosticForFile(string reportedFile, string? originalFile, string projectFile)
    {
        if (string.IsNullOrWhiteSpace(reportedFile) || string.IsNullOrWhiteSpace(originalFile))
            return true;
        try
        {
            var baseDirectory = Path.GetDirectoryName(projectFile) ?? Environment.CurrentDirectory;
            var resolved = Path.IsPathRooted(reportedFile)
                ? Path.GetFullPath(reportedFile)
                : Path.GetFullPath(Path.Combine(baseDirectory, reportedFile));
            return string.Equals(resolved, Path.GetFullPath(originalFile), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileName(resolved), Path.GetFileName(originalFile), StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static int OffsetAtLineColumn(string text, int line, int column)
    {
        var offset = 0;
        for (var currentLine = 1; currentLine < Math.Max(1, line) && offset < text.Length; currentLine++)
        {
            var newline = text.IndexOf('\n', offset);
            offset = newline < 0 ? text.Length : newline + 1;
        }
        var lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        return Math.Clamp(offset + Math.Max(0, column - 1), offset, Math.Max(offset, lineEnd - 1));
    }

    private static IEnumerable<ExternalToolDiagnostic> ParseJson(string output, ExternalLanguageTool tool, string documentText)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var diagnostics = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : document.RootElement.TryGetProperty("diagnostics", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();
            return diagnostics.Select(item =>
            {
                var startOffset = item.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number
                    ? start.GetInt32()
                    : item.TryGetProperty("range", out var range) && range.TryGetProperty("start", out var rangeStart)
                        ? OffsetAtLineColumnFromJson(rangeStart, documentText)
                        : 0;
                var length = item.TryGetProperty("length", out var lengthValue) && lengthValue.ValueKind == JsonValueKind.Number
                    ? Math.Max(1, lengthValue.GetInt32())
                    : item.TryGetProperty("range", out var rangeValue) && rangeValue.TryGetProperty("end", out var end)
                        ? Math.Max(1, OffsetAtLineColumnFromJson(end, documentText) - startOffset)
                        : 1;
                var severity = item.TryGetProperty("severity", out var severityValue)
                    ? NormalizeExternalSeverity(severityValue)
                    : "error";
                return new ExternalToolDiagnostic(
                    startOffset,
                    length,
                    item.TryGetProperty("message", out var message) ? message.GetString() ?? "External tool diagnostic" : "External tool diagnostic",
                    severity,
                    item.TryGetProperty("code", out var code) ? code.ToString() ?? tool.Id : tool.Id,
                    tool.Id);
            }).ToArray();
        }
        catch { return []; }
    }

    private static string NormalizeExternalSeverity(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.Number ? value.GetInt32().ToString() : value.GetString() ?? "error";
        return raw.ToLowerInvariant() switch
        {
            "1" or "error" => "error",
            "2" or "warning" or "warn" => "warning",
            "3" or "info" or "information" => "info",
            "4" or "hint" => "hint",
            _ => "error",
        };
    }

    private static int OffsetAtLineColumnFromJson(JsonElement position, string text)
    {
        // JSON external tools may provide LSP positions. Conversion to offsets is
        // completed by the caller when a source text is available; this fallback
        // keeps malformed/incomplete payloads harmless.
        if (position.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Number)
            return Math.Clamp(offset.GetInt32(), 0, Math.Max(0, text.Length - 1));
        var line = position.TryGetProperty("line", out var lineValue) ? lineValue.GetInt32() + 1 : 1;
        var character = position.TryGetProperty("character", out var characterValue) ? characterValue.GetInt32() + 1 : 1;
        return OffsetAtLineColumn(text, line, character);
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        return $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
