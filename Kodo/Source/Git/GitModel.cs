// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitSourceControl;

// ---------------------------------------------------------------------------
// Pure Git logic - no Avalonia / Kodo types - so every command and parser here
// can be exercised against real repositories in tests. The plugin class only
// wires these into the UI.
// ---------------------------------------------------------------------------

/// <summary>One entry from <c>git status</c>. Statuses use ' ' for "unmodified" (porcelain v2 uses '.').</summary>
internal sealed record GitChange(string Path, char IndexStatus, char WorktreeStatus, string? OriginalPath = null)
{
    public bool IsUntracked => IndexStatus == '?' && WorktreeStatus == '?';

    /// <summary>Unmerged paths (merge/rebase/cherry-pick conflicts).</summary>
    public bool IsConflicted => IndexStatus == 'U' || WorktreeStatus == 'U'
                                || (IndexStatus == 'A' && WorktreeStatus == 'A')
                                || (IndexStatus == 'D' && WorktreeStatus == 'D');

    public bool IsStaged => !IsUntracked && !IsConflicted && IndexStatus is not ' ' and not '?' and not '!';
    public bool IsUnstaged => !IsConflicted && (IsUntracked || WorktreeStatus is not ' ' and not '?' and not '!');
    public bool IsRenameOrCopy => IndexStatus is 'R' or 'C' || WorktreeStatus is 'R' or 'C';

    /// <summary>True when the file no longer exists in the working tree (so there is nothing to open).</summary>
    public bool IsDeletedInWorktree => WorktreeStatus == 'D' || (IndexStatus == 'D' && WorktreeStatus == ' ');

    public char DisplayStatus(bool staged) => IsConflicted ? 'U' : staged ? IndexStatus : IsUntracked ? '?' : WorktreeStatus;

    /// <summary>
    /// Paths an index operation must name. A staged rename is a deletion of the old path plus an addition of
    /// the new one: acting on only the new path leaves the deletion staged (verified against real git).
    /// </summary>
    public IReadOnlyList<string> Pathspecs
        => IsRenameOrCopy && !string.IsNullOrEmpty(OriginalPath) ? [OriginalPath, Path] : [Path];
}

internal sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, string? Upstream);

internal sealed record GitCommit(string Hash, string ShortHash, string Author, string RelativeDate, string Subject);

internal sealed record GitStatusSnapshot(
    string? Branch,
    bool IsDetached,
    string? Oid,
    string? Upstream,
    int Ahead,
    int Behind,
    bool UpstreamGone,
    IReadOnlyList<GitChange> Changes)
{
    public bool HasCommits => !string.IsNullOrEmpty(Oid);
    public bool HasUpstream => !string.IsNullOrEmpty(Upstream) && !UpstreamGone;

    public string BranchDisplay => IsDetached
        ? (HasCommits ? $"Detached @ {Oid![..Math.Min(7, Oid!.Length)]}" : "Detached HEAD")
        : Branch ?? "Select branch";

    public string UpstreamSummary
    {
        get
        {
            if (string.IsNullOrEmpty(Upstream)) return string.Empty;
            if (UpstreamGone) return $"Upstream missing · {Upstream}";
            if (Ahead == 0 && Behind == 0) return $"Up to date · {Upstream}";
            var parts = new List<string>(2);
            if (Ahead > 0) parts.Add($"↑{Ahead} to push");
            if (Behind > 0) parts.Add($"↓{Behind} to pull");
            return $"{string.Join("  ", parts)} · {Upstream}";
        }
    }
}

internal static class GitStatusParser
{
    /// <summary>
    /// Parses <c>git status --porcelain=v2 -z --branch --untracked-files=all</c>. v2 states rename origins, conflicts,
    /// the HEAD commit and upstream tracking explicitly, so nothing has to be inferred from free-form text.
    /// </summary>
    public static GitStatusSnapshot Parse(string output)
    {
        string? branch = null, oid = null, upstream = null;
        var detached = false;
        int ahead = 0, behind = 0;
        var hasAb = false;
        var changes = new List<GitChange>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            if (r.Length < 2) continue;

            if (r[0] == '#')
            {
                if (r.StartsWith("# branch.oid ", StringComparison.Ordinal))
                {
                    var v = r[13..];
                    oid = v == "(initial)" ? null : v;
                }
                else if (r.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    var v = r[14..];
                    if (v == "(detached)") detached = true;
                    else branch = v;
                }
                else if (r.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                {
                    upstream = r[18..];
                }
                else if (r.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    // "# branch.ab +3 -1"
                    var parts = r[12..].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && TryCount(parts[0], '+', out var a) && TryCount(parts[1], '-', out var b))
                    {
                        ahead = a;
                        behind = b;
                        hasAb = true;
                    }
                }
                continue;
            }

            switch (r[0])
            {
                case '1': // ordinary: 1 XY sub mH mI mW hH hI path
                {
                    var f = Fields(r, 9);
                    if (f.Length == 9 && f[1].Length == 2)
                        changes.Add(new GitChange(f[8], Norm(f[1][0]), Norm(f[1][1])));
                    break;
                }
                case '2': // rename/copy: 2 XY sub mH mI mW hH hI Xscore path <NUL> origPath
                {
                    var f = Fields(r, 10);
                    var orig = i + 1 < records.Length ? records[i + 1] : null;
                    i++; // the origin path is its own NUL-delimited record
                    if (f.Length == 10 && f[1].Length == 2)
                        changes.Add(new GitChange(f[9], Norm(f[1][0]), Norm(f[1][1]), string.IsNullOrEmpty(orig) ? null : orig));
                    break;
                }
                case 'u': // unmerged: u XY sub m1 m2 m3 mW h1 h2 h3 path
                {
                    var f = Fields(r, 11);
                    if (f.Length == 11 && f[1].Length == 2)
                        changes.Add(new GitChange(f[10], Norm(f[1][0]), Norm(f[1][1])));
                    break;
                }
                case '?':
                    if (r.Length > 2) changes.Add(new GitChange(r[2..], '?', '?'));
                    break;
                // '!' (ignored) entries are never requested; anything else is skipped for forward compatibility.
            }
        }

        // An upstream is configured but git reported no ahead/behind line => the upstream ref no longer exists.
        var gone = !string.IsNullOrEmpty(upstream) && !hasAb;
        return new GitStatusSnapshot(branch, detached, oid, upstream, ahead, behind, gone, changes);

        static char Norm(char c) => c == '.' ? ' ' : c;

        // Splits on single spaces into at most 'max' fields; the last field keeps any further spaces
        // (file names may contain them). Manual so the parse path uses only long-established BCL members.
        static string[] Fields(string text, int max)
        {
            var result = new List<string>(max);
            var start = 0;
            while (result.Count < max - 1)
            {
                var idx = text.IndexOf(' ', start);
                if (idx < 0) break;
                result.Add(text.Substring(start, idx - start));
                start = idx + 1;
            }
            result.Add(text.Substring(start));
            return result.ToArray();
        }

        static bool TryCount(string token, char sign, out int value)
        {
            value = 0;
            return token.Length > 1 && token[0] == sign && int.TryParse(token.Substring(1), out value);
        }
    }
}

internal static class GitCommands
{
    public static readonly string[] StatusArgs = ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"];

    public static IReadOnlyList<string> Stage(GitChange change) => ["add", "--", .. change.Pathspecs];

    public static IReadOnlyList<string> StageAll() => ["add", "--all"];

    public static IReadOnlyList<string> Unstage(GitChange change, bool hasHead) => hasHead
        ? ["restore", "--staged", "--", .. change.Pathspecs]
        : ["rm", "--cached", "--force", "--ignore-unmatch", "--", .. change.Pathspecs];

    public static IReadOnlyList<string> UnstageAll(bool hasHead) => hasHead
        ? ["restore", "--staged", "--", "."]
        : ["rm", "--cached", "--force", "--ignore-unmatch", "-r", "--", "."];

    public static IReadOnlyList<string> DiscardTracked(GitChange change) => ["restore", "--worktree", "--", .. change.Pathspecs];

    public static IReadOnlyList<string> DeleteUntracked(IEnumerable<string> paths) => ["clean", "-f", "--", .. paths];

    /// <summary>Plain push when an upstream exists; otherwise publish the branch and set its upstream.</summary>
    public static IReadOnlyList<string> Push(GitStatusSnapshot? snapshot, IReadOnlyList<string> remotes)
    {
        if (snapshot is null || snapshot.HasUpstream || snapshot.IsDetached || string.IsNullOrEmpty(snapshot.Branch) || remotes.Count == 0)
            return ["push"];
        var remote = remotes.FirstOrDefault(r => string.Equals(r, "origin", StringComparison.Ordinal)) ?? remotes[0];
        return ["push", "--set-upstream", remote, snapshot.Branch!];
    }

    public static IReadOnlyList<string> Commit(string message, bool amend)
    {
        if (!amend) return ["commit", "-m", message];
        return string.IsNullOrWhiteSpace(message) ? ["commit", "--amend", "--no-edit"] : ["commit", "--amend", "-m", message];
    }

    public static readonly string[] StashPush = ["stash", "push", "--include-untracked"];
    public static readonly string[] StashPop = ["stash", "pop"];
    public static readonly string[] StashList = ["stash", "list"];
    public static readonly string[] UndoLastCommit = ["reset", "--soft", "HEAD~1"];
    public static readonly string[] LastCommitMessage = ["log", "-1", "--format=%B"];
    public static readonly string[] Remotes = ["remote"];

    public static IReadOnlyList<string> Log(int count) =>
        ["log", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-color", "--format=%H%x1f%h%x1f%an%x1f%ar%x1f%s%x1e"];

    public static IReadOnlyList<string> ShowCommit(string hash) => ["show", "--no-color", "--no-ext-diff", "--stat", "--patch", "--format=fuller", hash];

    public static readonly string[] BranchList =
        ["for-each-ref", "--format=%(refname)%09%(refname:short)%09%(HEAD)%09%(upstream:short)", "refs/heads", "refs/remotes"];

    public static IReadOnlyList<string> SwitchTo(GitBranch branch) =>
        branch.IsRemote ? ["switch", "--track", branch.Name] : ["switch", branch.Name];

    public static IReadOnlyList<string> DeleteBranch(string name, bool force) => ["branch", force ? "-D" : "-d", "--", name];

    public static IReadOnlyList<string> DiffArgs(GitChange change, bool staged, string nullDevice)
    {
        if (change.IsUntracked && !staged)
            return ["diff", "--no-index", "--no-ext-diff", "--no-color", "--", nullDevice, change.Path];
        return staged
            ? ["diff", "--cached", "--no-ext-diff", "--no-color", "--", .. change.Pathspecs]
            : ["diff", "--no-ext-diff", "--no-color", "--", .. change.Pathspecs];
    }

    // ---- parsers ----

    public static List<GitBranch> ParseBranches(string output)
    {
        var result = new List<GitBranch>();
        foreach (var line in output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 3) continue;
            var refName = f[0];
            var shortName = f[1];
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal)) continue; // origin/HEAD symbolic ref
            var isRemote = refName.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var upstream = f.Length > 3 && f[3].Length > 0 ? f[3] : null;
            if (shortName.Length > 0) result.Add(new GitBranch(shortName, isRemote, f[2].Trim() == "*", upstream));
        }
        return result;
    }

    public static List<GitCommit> ParseLog(string output)
    {
        var result = new List<GitCommit>();
        foreach (var record in output.Split(new[] { '\u001e' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Trim('\r', '\n').Split('\u001f');
            if (f.Length >= 5) result.Add(new GitCommit(f[0], f[1], f[2], f[3], f[4]));
        }
        return result;
    }

    public static int CountLines(string output) => output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

    public static List<string> ParseRemotes(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>Cheap pre-check mirroring <c>git check-ref-format --branch</c>; git itself remains the authority.</summary>
    public static string? ValidateBranchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a branch name.";
        name = name.Trim();
        if (name == "@") return "'@' is not a valid branch name.";
        if (name.StartsWith('-')) return "A branch name cannot start with '-'.";
        if (name.StartsWith('/') || name.EndsWith('/')) return "A branch name cannot start or end with '/'.";
        if (name.EndsWith('.')) return "A branch name cannot end with '.'.";
        if (name.EndsWith(".lock", StringComparison.Ordinal)) return "A branch name cannot end with '.lock'.";
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal))
            return "A branch name cannot contain '..', '//' or '@{'.";
        foreach (var c in name)
            if (c <= ' ' || c == 0x7f || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return "A branch name cannot contain spaces or any of ~ ^ : ? * [ \\.";
        foreach (var part in name.Split('/'))
            if (part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal))
                return "No path segment of a branch name may start with '.' or end with '.lock'.";
        return null;
    }

    /// <summary>Splits "src/app/Main.cs" into ("Main.cs", "src/app").</summary>
    public static (string Name, string Directory) SplitPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? (normalized, string.Empty) : (normalized[(slash + 1)..], normalized[..slash]);
    }

    public static IEnumerable<string[]> Chunk(IReadOnlyList<string> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
            yield return items.Skip(i).Take(size).ToArray();
    }
}

internal enum DiffLineKind { None, Added, Removed, FileHeader, Hunk, Meta }

internal static class GitDiffClassifier
{
    /// <summary>
    /// Classifies one line of a unified diff. <c>---</c>/<c>+++</c> are only file headers when they form the
    /// header pair directly in front of a hunk: a removed line whose content starts with "-- " (e.g. a SQL comment)
    /// also begins with "--- " and must still be shown as removed (verified against real git output).
    /// </summary>
    /// <summary>
    /// Fallback for when neighbouring lines can't be read: recognise the conventional header shapes
    /// ("--- a/x", "+++ b/x", "--- /dev/null") and treat every other "--- " / "+++ " line as content.
    /// </summary>
    public static DiffLineKind ClassifyWithoutNeighbours(string line)
    {
        if (line.StartsWith("--- a/", StringComparison.Ordinal) || line.StartsWith("--- /dev/null", StringComparison.Ordinal)
            || line.StartsWith("+++ b/", StringComparison.Ordinal) || line.StartsWith("+++ /dev/null", StringComparison.Ordinal)
            || line.StartsWith("--- \"a/", StringComparison.Ordinal) || line.StartsWith("+++ \"b/", StringComparison.Ordinal))
            return DiffLineKind.FileHeader;
        return Classify(line, null, null);
    }

    public static DiffLineKind Classify(string line, string? previous, string? next)
    {
        if (line.Length == 0) return DiffLineKind.None;
        if (line.StartsWith("@@", StringComparison.Ordinal)) return DiffLineKind.Hunk;
        if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("new file mode", StringComparison.Ordinal) || line.StartsWith("deleted file mode", StringComparison.Ordinal)
            || line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal)
            || line.StartsWith("similarity index ", StringComparison.Ordinal) || line.StartsWith("rename from ", StringComparison.Ordinal)
            || line.StartsWith("rename to ", StringComparison.Ordinal) || line.StartsWith("Binary files ", StringComparison.Ordinal))
            return DiffLineKind.Meta;

        if (line.StartsWith("--- ", StringComparison.Ordinal))
            return next is not null && next.StartsWith("+++ ", StringComparison.Ordinal) ? DiffLineKind.FileHeader : DiffLineKind.Removed;
        if (line.StartsWith("+++ ", StringComparison.Ordinal))
            return previous is not null && previous.StartsWith("--- ", StringComparison.Ordinal)
                   && (next is null || next.StartsWith("@@", StringComparison.Ordinal))
                ? DiffLineKind.FileHeader
                : DiffLineKind.Added;
        if (line[0] == '+') return DiffLineKind.Added;
        if (line[0] == '-') return DiffLineKind.Removed;
        return DiffLineKind.None;
    }
}
