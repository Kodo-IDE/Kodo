// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitSourceControl;

internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

internal static class GitProcess
{
    private const int MaxCapturedCharacters = 1_000_000;

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        var argumentList = arguments as IReadOnlyList<string> ?? new List<string>(arguments);
        // --literal-pathspecs stops file names such as "a[1].txt" or "*.cs" being read as globs. It must NOT be
        // applied to stash: stash runs its own internal pathspec plumbing (e.g. ":/" for the final clean-up), which
        // the flag makes literal, so `stash push --include-untracked` would stash untracked files yet leave them in
        // the working tree - and a later pop then refuses to restore them. (Verified against git 2.43.)
        var literalPathspecs = !(argumentList.Count > 0 && argumentList[0] == "stash");
        if (literalPathspecs) startInfo.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);

        // Keep background status reads quiet and prevent Git from waiting for
        // interactive terminal input that Kodo cannot display.
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Never block on an editor Kodo cannot display (merge/commit message prompts fall back to the default text).
        startInfo.Environment["GIT_EDITOR"] = "true";
        startInfo.Environment["GIT_MERGE_AUTOEDIT"] = "no";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new GitResult(-1, string.Empty, "Git could not be started.");

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            var outputTask = ReadLimitedAsync(process.StandardOutput, MaxCapturedCharacters);
            var errorTask = ReadLimitedAsync(process.StandardError, 32_000);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                var timeoutOutput = await outputTask.ConfigureAwait(false);
                var timeoutError = await errorTask.ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                return new GitResult(-1, timeoutOutput, string.IsNullOrWhiteSpace(timeoutError) ? "Git operation timed out." : timeoutError);
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new GitResult(process.ExitCode, output, error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GitResult(-1, string.Empty, "Git was not found. Install Git and make sure it is on PATH.");
        }
        catch (InvalidOperationException ex)
        {
            return new GitResult(-1, string.Empty, ex.Message);
        }
    }

    private static async Task<string> ReadLimitedAsync(System.IO.StreamReader reader, int maximum)
    {
        var builder = new StringBuilder(Math.Min(maximum, 16_384));
        var buffer = new char[8_192];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = maximum - builder.Length;
            if (count > remaining) truncated = true;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(count, remaining));
        }
        if (truncated)
            builder.AppendLine().Append("[Output truncated]");
        return builder.ToString();
    }
}
