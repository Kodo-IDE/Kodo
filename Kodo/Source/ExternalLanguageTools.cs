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
        @"^(?<file>.*?):(?<line>\d+):(?<column>\d+)(?::\s*(?<severity>error|warning|info))?\s*:?[ \t]*(?<message>.*)$",
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
                var output = await RunToolAsync(tool, temporaryFile, filePath, cancellationToken).ConfigureAwait(false);
                results.AddRange(ParseOutput(output, tool, documentText));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
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
        CancellationToken cancellationToken)
    {
        var arguments = string.Join(" ", tool.Arguments.Select(argument => QuoteArgument(
            argument.Replace("{file}", temporaryFile, StringComparison.OrdinalIgnoreCase)
                .Replace("{fileName}", Path.GetFileName(originalFile ?? temporaryFile), StringComparison.OrdinalIgnoreCase))));
        var startInfo = new ProcessStartInfo
        {
            FileName = tool.Command,
            Arguments = arguments,
            WorkingDirectory = Directory.Exists(Path.GetDirectoryName(originalFile))
                ? Path.GetDirectoryName(originalFile)!
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

    private static IEnumerable<ExternalToolDiagnostic> ParseOutput(string output, ExternalLanguageTool tool, string documentText)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        if (string.Equals(tool.Format, "json", StringComparison.OrdinalIgnoreCase))
            return ParseJson(output, tool);

        var results = new List<ExternalToolDiagnostic>();
        foreach (var line in output.Split('\n'))
        {
            var match = CompilerLine.Match(line.Trim());
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["message"].Value)) continue;
            var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
            {
                "warning" => "warning",
                "info" => "info",
                _ => "error",
            };
            results.Add(new ExternalToolDiagnostic(
                OffsetAtLineColumn(documentText, int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value)),
                1,
                match.Groups["message"].Value.Trim(),
                severity,
                tool.Id,
                tool.Id));
        }
        return results;
    }

    private static int OffsetAtLineColumn(string text, int line, int column)
    {
        var offset = 0;
        for (var currentLine = 1; currentLine < Math.Max(1, line) && offset < text.Length; currentLine++)
        {
            var newline = text.IndexOf('\n', offset);
            offset = newline < 0 ? text.Length : newline + 1;
        }
        return Math.Clamp(offset + Math.Max(0, column - 1), 0, Math.Max(0, text.Length - 1));
    }

    private static IEnumerable<ExternalToolDiagnostic> ParseJson(string output, ExternalLanguageTool tool)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var diagnostics = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                : document.RootElement.TryGetProperty("diagnostics", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();
            return diagnostics.Select(item => new ExternalToolDiagnostic(
                item.TryGetProperty("start", out var start) ? start.GetInt32() : 0,
                item.TryGetProperty("length", out var length) ? Math.Max(1, length.GetInt32()) : 1,
                item.TryGetProperty("message", out var message) ? message.GetString() ?? "External tool diagnostic" : "External tool diagnostic",
                item.TryGetProperty("severity", out var severity) ? severity.GetString() ?? "error" : "error",
                item.TryGetProperty("code", out var code) ? code.GetString() ?? tool.Id : tool.Id,
                tool.Id)).ToArray();
        }
        catch { return []; }
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;
        return $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
