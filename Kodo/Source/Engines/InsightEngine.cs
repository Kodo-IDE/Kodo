// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Kodo.Models;

namespace Kodo;

public sealed class InsightEngine
{
    private readonly object _fileCacheLock = new();

    private readonly Dictionary<string, HashSet<string>> _variablesByFile = new(FileSystemPaths.Comparer);

    private readonly Dictionary<string, string[]> _declarationNamesByFile = new(FileSystemPaths.Comparer);

    private const string NotCompoundOrArrow = @"(?<![=!<>+\-*/%&|^~])=(?![=>])";

    private static readonly Regex TypedOrKeywordDeclaration = new(
        @"\b(?:var|let|val|const|int|long|short|byte|sbyte|uint|ulong|ushort|float|double|decimal|bool|char|string|object|dynamic|auto)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:[:,][^=]*)?" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex ModifiedBinding = new(
        @"\b(?:let|var|val)\s+(?:mut\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?::[^=]*)?" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex BareAssignment = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex ColonAnnotatedAssignment = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*[A-Za-z_][A-Za-z0-9_.<>\[\]\?\|,\s]*\s*" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex QualifiedTypeAssignment = new(
        @"^\s*(?:(?:public|private|protected|internal|static|readonly|sealed|abstract|virtual|override|async|extern|unsafe|partial|const|volatile|new|required|file)\s+)*(?:[A-Za-z_][A-Za-z0-9_]*\s*(?:<[^>]*>)?\s*(?:\[\s*\])*\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*[A-Za-z_][A-Za-z0-9_.<>\[\]\?\|,\s]*)?\s*" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex LoopOrHandlerBinding = new(
        @"\b(?:foreach|for|catch|using)\s*\(\s*(?:var|[A-Za-z_][A-Za-z0-9_<>,.\[\]\s]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:in\b|=|\))",
        RegexOptions.Compiled);

    private static readonly Regex[] DeclarationPatterns =
    {
        TypedOrKeywordDeclaration,
        ModifiedBinding,
        BareAssignment,
        ColonAnnotatedAssignment,
        QualifiedTypeAssignment,
        LoopOrHandlerBinding,
    };

    private static readonly HashSet<string> KnownColonTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "string", "object", "dynamic",
        "var", "auto", "void", "list", "dict", "set", "tuple", "array", "map",
        "str", "num", "number", "boolean", "any", "unknown", "never", "optional",
        "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64",
        "float32", "float64",
    };

    private static readonly Regex BatchSetDeclaration = new(
        @"^\s*set\s+(?:/a\s+|/p\s+)?""?([A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elif", "elseif", "else", "for", "foreach", "while", "switch", "match", "case",
        "return", "try", "catch", "finally", "throw", "raise", "new", "class", "struct",
        "interface", "trait", "enum", "namespace", "module", "package", "using", "import",
        "from", "require", "include", "export", "public", "private", "protected", "internal",
        "static", "readonly", "sealed", "abstract", "virtual", "override", "async", "await",
        "void", "null", "nil", "none", "undefined", "true", "false", "this", "self", "base",
        "super", "def", "function", "fn", "lambda", "in", "is", "as", "out", "ref", "params",
        "yield", "break", "continue", "do", "default", "goto", "mut", "let", "var", "val",
        "const", "end", "then", "done", "fi", "esac", "not", "and", "or",
        "echo", "print", "println", "printf", "console", "exit", "call", "cls", "pause", "rem",
        "cd", "dir", "mkdir", "rmdir", "del", "copy", "move", "ren", "shift", "setlocal",
        "endlocal", "errorlevel", "start", "taskkill", "set", "unset", "source",
        "sudo", "chmod", "chown", "curl", "wget", "ssh", "scp", "tar", "zip", "unzip",
        "git", "npm", "npx", "yarn", "pnpm", "pip", "python", "python3", "node", "dotnet",
        "cargo", "go", "make", "cmake", "gradle", "mvn", "docker", "kubectl", "brew", "apt",
        "build", "publish", "install", "run", "test", "clean", "deploy", "restore", "release",
        "debug", "lock", "fixed", "checked", "unchecked", "synchronized", "with", "unsafe",
        "when", "while", "func",
    };

    private static readonly Regex TrailingLineComment = new(
        @"(?:(?<=\s)|^)(?://|\#)(?!\S).*$|(?:(?<=\s)|^)(?://|\#)\s.*$",
        RegexOptions.Compiled);

    private static string MaskStringLiterals(string lineText)
    {
        if (lineText.IndexOfAny(['"', '\'']) < 0)
            return lineText;

        var chars = System.Buffers.ArrayPool<char>.Shared.Rent(lineText.Length);
        lineText.AsSpan().CopyTo(chars);
        var n = lineText.Length;
        char? quote = null;
        for (var i = 0; i < n; i++)
        {
            var c = chars[i];
            if (quote is null)
            {
                if (c is '"' or '\'')
                    quote = c;
                continue;
            }

            if (c == '\\' && i + 1 < n)
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (c == quote)
            {
                quote = null;
                continue;
            }

            chars[i] = ' ';
        }

        var result = new string(chars, 0, n);
        System.Buffers.ArrayPool<char>.Shared.Return(chars);
        return result;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (string ext, string masked)> _maskedCache = new();

    private static ulong HashText64(string text, string extKey)
    {
        ulong h = 0xcbf29ce484222325UL;
        for (var i = 0; i < text.Length; i++)
        {
            h ^= text[i];
            h *= 0x100000001b3UL;
        }
        for (var i = 0; i < extKey.Length; i++)
        {
            h ^= (ulong)extKey[i] + 0x9e3779b97f4a7c15UL;
            h *= 0x100000001b3UL;
        }
        h ^= (ulong)(uint)text.Length;
        h *= 0x100000001b3UL;
        return h;
    }

    private static ulong HashSpan64(ReadOnlySpan<char> content)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (var ch in content)
        {
            h ^= ch;
            h *= 0x100000001b3UL;
        }
        h ^= (ulong)(uint)content.Length;
        h *= 0x100000001b3UL;
        return h;
    }

    private sealed class LineFacts
    {
        public ulong Hash;
        public int ContentLen;
        public Dictionary<string, int>? WordCounts;
        public string[] VarNames = Array.Empty<string>();
        public string? DeclName;
        public bool DeclValid;
        public bool TermStarts;
        public bool BlockTerm;
        public bool ClosingBrace;
        public bool JumpTarget;
        public bool CondIf;
        public bool CondElse;
        public bool OpenIf;
        public bool OpenElse;
        public bool LeadingJump;
        public bool TrimmedEmpty;
        public bool HasBrace;
    }

    private sealed class LineState
    {
        public string ExtId = "";
        public ulong[] HashesBuf = Array.Empty<ulong>();
        public int HashCount;
        public readonly Dictionary<ulong, int> Multiplicity = new();
        public readonly Dictionary<ulong, LineFacts> ByHash = new();
        public readonly Dictionary<string, int> WordFreq = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> WordFolderFreq = new(StringComparer.Ordinal);
        public readonly HashSet<string> ScanNames = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ScanContrib = new(StringComparer.Ordinal);
        public string?[] PrevContrib = Array.Empty<string?>();
        public LineFacts[] FactsAt = Array.Empty<LineFacts>();
        public readonly object SyncRoot = new();
    }

    private readonly Dictionary<string, LineState> _lineStates = new(StringComparer.Ordinal);

    private LineState GetLineState(string fileKey, string flavor, LoadedExtension? languageExtension)
    {
        var key = flavor + fileKey;
        if (!_lineStates.TryGetValue(key, out var st) || st is null)
        {
            st = new LineState();
            _lineStates[key] = st;
        }
        var extKey = flavor[0] + ":" + (languageExtension?.Id ?? "plain");
        if (st.ExtId != extKey)
        {
            lock (st.SyncRoot)
            {
                if (st.ExtId.Length > 0)
                {
                    st.Multiplicity.Clear();
                    st.ByHash.Clear();
                    st.WordFreq.Clear();
                    st.ScanNames.Clear();
                    st.ScanContrib.Clear();
                }
                st.ExtId = extKey;
            }
        }
        return st;
    }

    private static LineFacts ComputeLineFacts(ReadOnlySpan<char> content)
    {
        var f = new LineFacts
        {
            ContentLen = content.Length,
            Hash = HashSpan64(content),
            HasBrace = content.Contains('{'),
        };

        var s = 0;
        var e = content.Length;
        while (s < e && char.IsWhiteSpace(content[s])) s++;
        while (e > s && char.IsWhiteSpace(content[e - 1])) e--;
        var trimmed = content[s..e];
        f.TrimmedEmpty = s == e;

        System.Collections.Generic.Dictionary<string, int>? wordCounts = null;
        foreach (var m in WordIdentifier.EnumerateMatches(content))
        {
            var w = content.Slice(m.Index, m.Length).ToString();
            (wordCounts ??= new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal))[w] =
                wordCounts.TryGetValue(w, out var wc) ? wc + 1 : 1;
        }
        f.WordCounts = wordCounts;

        if (content.Contains('='))
        {
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in IdentifyVariableInitializations(content.ToString()))
                if (seen.Add(n))
                    unique.Add(n);
            f.VarNames = unique.ToArray();
        }

        var hasParens = content.Contains('(');
        var hasKeyword = content.Contains("function", StringComparison.Ordinal) ||
                         content.Contains("def", StringComparison.Ordinal) ||
                         content.Contains("fn", StringComparison.Ordinal) ||
                         content.Contains("func", StringComparison.Ordinal) ||
                         content.Contains("sub", StringComparison.Ordinal) ||
                         content.Contains("proc", StringComparison.Ordinal);
        if (hasParens && (hasKeyword || f.HasBrace))
        {
            var line = content.ToString();
            var keywordMatch = hasKeyword ? KeywordFunctionDeclaration.Match(line) : Match.Empty;
            if (keywordMatch.Success)
            {
                f.DeclName = keywordMatch.Groups[1].Value;
                f.DeclValid = true;
            }
            else if (f.HasBrace)
            {
                var cStyleMatch = CStyleFunctionDeclaration.Match(line);
                if (cStyleMatch.Success && !ReservedWords.Contains(cStyleMatch.Groups[1].Value))
                {
                    f.DeclName = cStyleMatch.Groups[1].Value;
                    f.DeclValid = true;
                }
            }
        }

        f.TermStarts = trimmed.StartsWith("return", StringComparison.Ordinal) ||
                       trimmed.StartsWith("throw", StringComparison.Ordinal) ||
                       trimmed.StartsWith("break", StringComparison.Ordinal) ||
                       trimmed.StartsWith("continue", StringComparison.Ordinal);
        f.BlockTerm = BlockTerminatorStatement.IsMatch(content);
        f.ClosingBrace = trimmed.Length > 0 && trimmed[0] == '}' && ClosingBraceOnlyLine.IsMatch(trimmed);
        f.JumpTarget = trimmed.Length > 0 && ((trimmed[0] == 'c' || trimmed[0] == 'd') || trimmed.Contains(':')) && PossibleJumpTarget.IsMatch(trimmed);
        f.CondIf = ConditionalTerminatorOnIf.IsMatch(trimmed);
        f.CondElse = ConditionalTerminatorOnElse.IsMatch(trimmed);
        f.OpenIf = OpenConditionalOnIf.IsMatch(trimmed);
        f.OpenElse = OpenConditionalOnElse.IsMatch(trimmed);
        f.LeadingJump = LeadingJumpStatement.IsMatch(trimmed);

        return f;
    }

    private void RegisterLines(
        LineState st,
        string maskedDoc,
        int[] lineStart,
        int[] lineLen,
        ulong[] hashes,
        int lineCount,
        bool patchWordFreq,
        CancellationToken cancellationToken)
    {
        if (st.FactsAt.Length < lineCount)
            st.FactsAt = new LineFacts[Math.Max(lineCount, st.FactsAt.Length * 2)];

        var prev = st.HashesBuf;
        var prevCount = st.HashCount;
        var diff = new Dictionary<ulong, int>();
        var minN = Math.Min(prevCount, lineCount);
        for (var i = 0; i < minN; i++)
        {
            if (prev[i] != hashes[i])
            {
                AddRef(diff, prev[i], -1);
                AddRef(diff, hashes[i], +1);
            }
        }
        for (var i = minN; i < prevCount; i++) AddRef(diff, prev[i], -1);
        for (var i = minN; i < lineCount; i++) AddRef(diff, hashes[i], +1);

        var firstAppear = new Dictionary<ulong, int>();
        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var h = hashes[i];
            if (diff.TryGetValue(h, out var d) && d > 0 && !firstAppear.ContainsKey(h))
                firstAppear[h] = i;
        }

        var pending = new Dictionary<ulong, LineFacts>();
        foreach (var kv in diff)
        {
            if (kv.Value <= 0 || st.ByHash.ContainsKey(kv.Key)) continue;
            var fi = firstAppear[kv.Key];
            cancellationToken.ThrowIfCancellationRequested();
            pending[kv.Key] = ComputeLineFacts(maskedDoc.AsSpan(lineStart[fi], lineLen[fi]));
        }

        lock (st.SyncRoot)
        {
            foreach (var pf in pending)
                st.ByHash[pf.Key] = pf.Value;

            foreach (var kv in diff)
            {
                var h = kv.Key;
                var d = kv.Value;
                if (!st.ByHash.TryGetValue(h, out var facts) || facts is null)
                    continue;

                var cur = st.Multiplicity.TryGetValue(h, out var mv) ? mv : 0;
                var next = cur + d;

                if (patchWordFreq && facts.WordCounts is not null)
                {
                    foreach (var wc in facts.WordCounts)
                        PatchFreq(st.WordFreq, wc.Key, wc.Value * d);
                }

                if (next <= 0)
                {
                    if (cur > 0)
                        st.Multiplicity.Remove(h);
                    st.ByHash.Remove(h);
                }
                else
                {
                    st.Multiplicity[h] = next;
                }
            }

            for (var i = 0; i < lineCount; i++)
                st.FactsAt[i] = st.ByHash.TryGetValue(hashes[i], out var fx) ? fx! : null!;

            if (st.HashesBuf.Length < lineCount)
                Array.Resize(ref st.HashesBuf, Math.Max(lineCount, st.HashesBuf.Length * 2));
            hashes.AsSpan(0, lineCount).CopyTo(st.HashesBuf);
            st.HashCount = lineCount;
        }
    }

    private static void AddRef(Dictionary<ulong, int> diff, ulong hash, int delta)
    {
        diff.TryGetValue(hash, out var v);
        var nv = v + delta;
        if (nv == 0) diff.Remove(hash);
        else diff[hash] = nv;
    }

    private static void PatchFreq(Dictionary<string, int> freq, string word, int delta)
    {
        freq.TryGetValue(word, out var v);
        var nv = v + delta;
        if (nv <= 0) freq.Remove(word);
        else freq[word] = nv;
    }


    private static string BuildMaskedDocument(string documentText, LoadedExtension? extension)
    {
        if (string.IsNullOrEmpty(documentText))
            return documentText;

        var extKey = extension?.Id ?? "plain";
        var hash = HashText64(documentText, extKey);
        if (_maskedCache.TryGetValue(hash, out var cached) && cached.ext == extKey)
            return cached.masked;

        var commentLine = extension?.CommentLine is { Length: > 0 } cl ? cl : "//";
        var blockStart = extension?.CommentBlockStart is { Length: > 0 } bs ? bs : "/*";
        var blockEnd = extension?.CommentBlockEnd is { Length: > 0 } be ? be : "*/";
        var multiDelims = extension?.MultiLineStringDelimiters is { Length: > 0 } mls ? mls : Array.Empty<string>();
        var disableSingle = extension?.DisableSingleQuoteStrings ?? false;

        var docLen = documentText.Length;
        var chars = System.Buffers.ArrayPool<char>.Shared.Rent(docLen);
        documentText.AsSpan().CopyTo(chars);
        var masked = System.Buffers.ArrayPool<char>.Shared.Rent(docLen);
        Array.Copy(chars, masked, docLen);

        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inMulti = false;
        char delim = '\0';
        var isInterpolated = false;
        var isVerbatim = false;
        var inInterpolationCode = false;
        var interpolationDepth = 0;

        for (var i = 0; i < docLen; i++)
        {
            var c = chars[i];

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }
                else if (c != '\r')
                {
                    masked[i] = ' ';
                }
                continue;
            }

            if (inBlockComment)
            {
                if (MatchesAt(documentText, i, blockEnd))
                {
                    for (var k = 0; k < blockEnd.Length && i + k < docLen; k++)
                        if (masked[i + k] != '\n' && masked[i + k] != '\r')
                            masked[i + k] = ' ';
                    inBlockComment = false;
                    i += blockEnd.Length - 1;
                }
                else
                {
                    if (c != '\n' && c != '\r')
                        masked[i] = ' ';
                }
                continue;
            }

            if (inString)
            {
                if (isInterpolated)
                {
                    if (inInterpolationCode)
                    {
                        if (c == '{' && i + 1 < docLen && chars[i + 1] == '{')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '}' && i + 1 < docLen && chars[i + 1] == '}')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '{')
                        {
                            interpolationDepth++;
                            continue;
                        }
                        if (c == '}')
                        {
                            interpolationDepth--;
                            if (interpolationDepth <= 0)
                            {
                                inInterpolationCode = false;
                                interpolationDepth = 0;
                            }
                            continue;
                        }
                        if (c == '"' && !inMulti)
                        {
                            continue;
                        }
                        continue;
                    }
                    else
                    {
                        if (c == '{' && i + 1 < docLen && chars[i + 1] == '{')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '}' && i + 1 < docLen && chars[i + 1] == '}')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '{')
                        {
                            inInterpolationCode = true;
                            interpolationDepth = 1;
                            continue;
                        }
                    }
                }

                if (c == '\\' && !inMulti && !isVerbatim)
                {
                    masked[i] = ' ';
                    if (i + 1 < docLen && chars[i + 1] != '\n' && chars[i + 1] != '\r')
                    {
                        masked[i + 1] = ' ';
                        i++;
                    }
                    continue;
                }

                if (c == '\n' && !inMulti && !isVerbatim)
                {
                    inString = false;
                    inMulti = false;
                    isInterpolated = false;
                    isVerbatim = false;
                    inInterpolationCode = false;
                    continue;
                }

                string? closing = null;
                if (inMulti)
                    closing = multiDelims.FirstOrDefault(d => MatchesAt(documentText, i, d));
                else if (delim != '\0' && MatchesAt(documentText, i, delim.ToString()))
                {
                    if (isVerbatim && i + 1 < docLen && chars[i + 1] == delim)
                    {
                        masked[i] = ' ';
                        masked[i + 1] = ' ';
                        i++;
                        continue;
                    }
                    closing = delim.ToString();
                }

                if (closing is not null)
                {
                    inString = false;
                    inMulti = false;
                    isInterpolated = false;
                    isVerbatim = false;
                    inInterpolationCode = false;
                    i += closing.Length - 1;
                    continue;
                }

                if (c != '\n' && c != '\r')
                    masked[i] = ' ';
                continue;
            }

            if (MatchesAt(documentText, i, commentLine))
            {
                for (var k = 0; k < commentLine.Length && i + k < docLen; k++)
                    if (masked[i + k] != '\n' && masked[i + k] != '\r')
                        masked[i + k] = ' ';
                inLineComment = true;
                i += commentLine.Length - 1;
                continue;
            }

            if (MatchesAt(documentText, i, blockStart))
            {
                for (var k = 0; k < blockStart.Length && i + k < docLen; k++)
                    if (masked[i + k] != '\n' && masked[i + k] != '\r')
                        masked[i + k] = ' ';
                inBlockComment = true;
                i += blockStart.Length - 1;
                continue;
            }

            var multi = multiDelims.FirstOrDefault(d => MatchesAt(documentText, i, d));
            if (multi is not null)
            {
                inString = true;
                inMulti = true;
                isInterpolated = false;
                isVerbatim = false;
                if (i > 0 && chars[i - 1] == '$')
                    isInterpolated = true;
                else if (i > 1 && chars[i - 2] == '$' && chars[i - 1] == '@')
                    isInterpolated = true;
                i += multi.Length - 1;
                continue;
            }

            if (c == '"' || (c == '\'' && !disableSingle))
            {
                var interpolated = false;
                var verbatim = false;
                if (i > 0 && chars[i - 1] == '$')
                    interpolated = true;
                else if (i > 0 && chars[i - 1] == '@')
                    verbatim = true;
                else if (i > 0 && (chars[i - 1] == 'r' || chars[i - 1] == 'R') &&
                         (i < 2 || !char.IsLetterOrDigit(chars[i - 2])))
                    verbatim = true;
                if (i > 1 && ((chars[i - 2] == '$' && chars[i - 1] == '@') || (chars[i - 2] == '@' && chars[i - 1] == '$')))
                {
                    interpolated = true;
                    verbatim = true;
                }

                inString = true;
                inMulti = false;
                delim = c;
                isInterpolated = interpolated;
                isVerbatim = verbatim;
                inInterpolationCode = false;
                interpolationDepth = 0;
                continue;
            }
        }

        var result = new string(masked, 0, docLen);
        System.Buffers.ArrayPool<char>.Shared.Return(chars);
        System.Buffers.ArrayPool<char>.Shared.Return(masked);
        _maskedCache[hash] = (extKey, result);
        if (_maskedCache.Count > 32) { foreach (var k in new System.Collections.Generic.List<ulong>(_maskedCache.Keys)) { if (_maskedCache.Count <= 24) break; _maskedCache.TryRemove(k, out _); } }
        return result;
    }

    private static readonly Dictionary<string, (DateTime mtime, string? masked)> _folderMaskCache = new(FileSystemPaths.Comparer);
    private static string? BuildFolderMaskedText(string folderPath, string currentFilePath, LoadedExtension? ext)
    {
        try
        {
            try
            {
                var folderMtime = global::System.IO.Directory.GetLastWriteTimeUtc(folderPath);
                var cacheKey = folderPath + "|" + global::System.IO.Path.GetExtension(currentFilePath);
                lock (_folderMaskCache)
                {
                    if (_folderMaskCache.TryGetValue(cacheKey, out var cached) && cached.mtime == folderMtime && cached.masked is not null)
                        return cached.masked;
                }
                var result = BuildFolderMaskedTextCore(folderPath, currentFilePath, ext);
                lock (_folderMaskCache)
                {
                    _folderMaskCache[cacheKey] = (folderMtime, result);
                    if (_folderMaskCache.Count > 8) _folderMaskCache.Remove(_folderMaskCache.Keys.First());
                }
                return result;
            }
            catch { }
            return BuildFolderMaskedTextCore(folderPath, currentFilePath, ext);
        }
        catch { return null; }
    }
    private static string? BuildFolderMaskedTextCore(string folderPath, string currentFilePath, LoadedExtension? ext)
    {
        try
        {
            var currentExt = global::System.IO.Path.GetExtension(currentFilePath);
            var files = global::System.IO.Directory.EnumerateFiles(folderPath, "*", new global::System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = global::System.IO.FileAttributes.Hidden | global::System.IO.FileAttributes.System })
                .Where(f => !FileSystemPaths.Equals(f, currentFilePath))
                .Where(f => string.IsNullOrWhiteSpace(currentExt) || System.IO.Path.GetExtension(f).Equals(currentExt, StringComparison.OrdinalIgnoreCase))
                .Where(f =>
                {
                    var lower = f.ToLowerInvariant();
                    return !lower.Contains("/.git/") && !lower.Contains("\\.git\\") &&
                           !lower.Contains("/bin/") && !lower.Contains("\\bin\\") &&
                           !lower.Contains("/obj/") && !lower.Contains("\\obj\\") &&
                           !lower.Contains("/node_modules/") && !lower.Contains("\\node_modules\\") &&
                           !lower.Contains("/.vs/") && !lower.Contains("\\.vs\\");
                })
                .Take(20);

            var sb = new global::System.Text.StringBuilder();
            foreach (var f in files)
            {
                try
                {
                    var info = new global::System.IO.FileInfo(f);
                    if (info.Length > 1_500_000) continue;
                    var text = global::System.IO.File.ReadAllText(f);
                    if (text.IndexOf('\0') >= 0) continue;
                    var masked = BuildMaskedDocument(text, ext);
                    sb.AppendLine(masked);
                }
                catch { }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlausibleVariableName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length > 64) return false;
        if (char.IsDigit(name[0])) return false;
        if (name.Contains('-')) return false;
        return true;
    }

    private static bool IsFalsePositiveDeclaration(string scanText, string name, Regex pattern)
    {
        var trimmed = scanText.Trim();
        if (trimmed.StartsWith("[") && trimmed.Contains("=") && trimmed.Contains("]"))
        {
            var idxName = scanText.IndexOf(name, StringComparison.Ordinal);
            if (idxName > 0)
            {
                var before = scanText.Substring(0, idxName);
                if (before.Contains('[') || before.Contains('('))
                {
                    var beforeTrim = before.Trim();
                    if (!beforeTrim.EndsWith("var", StringComparison.Ordinal) &&
                        !beforeTrim.EndsWith("let", StringComparison.Ordinal) &&
                        !beforeTrim.EndsWith("const", StringComparison.Ordinal))
                        return true;
                }
            }
        }

        if (pattern == ColonAnnotatedAssignment)
        {
            var colonIdx = scanText.IndexOf(':', StringComparison.Ordinal);
            var eqIdx = scanText.IndexOf('=', colonIdx + 1);
            if (colonIdx >= 0 && eqIdx > colonIdx)
            {
                var typePart = scanText.Substring(colonIdx + 1, eqIdx - colonIdx - 1).Trim();
                var firstToken = typePart.Split(new[] { ' ', '\t', '<', '>', '[', ']', ',', '|', '?' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstToken))
                {
                    firstToken = firstToken.TrimEnd('?');
                    if (firstToken.Length > 0 && char.IsLower(firstToken[0]) && !KnownColonTypes.Contains(firstToken))
                    {
                        var tokens = typePart.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length == 1 && !typePart.Contains("<") && !typePart.Contains("[") && !typePart.Contains("|"))
                            return true;
                    }
                }
            }
        }

        var eqPos = scanText.IndexOf('=');
        if (eqPos > 0)
        {
            var beforeEq = scanText.Substring(0, eqPos);
            if (beforeEq.Contains('.') || beforeEq.Contains("->") || beforeEq.Contains("::"))
            {
                var nameIdx = beforeEq.LastIndexOf(name, StringComparison.Ordinal);
                if (nameIdx > 0 && beforeEq.Substring(0, nameIdx).Contains('.'))
                    return true;
                if (beforeEq.Contains('[') || beforeEq.Contains(']'))
                    return true;
            }
            var beforeTrimLower = beforeEq.TrimStart().ToLowerInvariant();
            if (beforeTrimLower.StartsWith("return ") || beforeTrimLower.StartsWith("throw ") ||
                beforeTrimLower.StartsWith("yield ") || beforeTrimLower.StartsWith("await ") ||
                beforeTrimLower.StartsWith("case ") || beforeTrimLower.StartsWith("default:"))
                return true;
        }

        if (name.Length >= 2 && name.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length >= 2 && name.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length >= 2 && name.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static IEnumerable<string> IdentifyVariableInitializations(string lineText)
    {
        if (string.IsNullOrWhiteSpace(lineText))
            return [];

        var trimmed = lineText.TrimStart();
        if (trimmed.StartsWith("//") || trimmed.StartsWith('#') ||
            trimmed.StartsWith('*') || trimmed.StartsWith("'''") || trimmed.StartsWith("\"\"\"") ||
            trimmed.StartsWith("--") || trimmed.StartsWith(';') ||
            trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("REM", StringComparison.OrdinalIgnoreCase))
            return [];

        var batchMatch = BatchSetDeclaration.Match(trimmed);
        if (batchMatch.Success)
        {
            var batchName = batchMatch.Groups[1].Value;
            return string.IsNullOrEmpty(batchName) ? [] : [batchName];
        }

        var scanText = TrailingLineComment.Replace(MaskStringLiterals(lineText), string.Empty);
        if (string.IsNullOrWhiteSpace(scanText))
            return [];

        if (!scanText.Contains('=')) return [];

        List<string>? found = null;
        foreach (var pattern in DeclarationPatterns)
        {
            var searchFrom = 0;
            while (searchFrom <= scanText.Length)
            {
                var match = pattern.Match(scanText, searchFrom);
                if (!match.Success || match.Groups.Count < 2)
                    break;
                searchFrom = match.Index + Math.Max(1, match.Length);
                var name = match.Groups[1].Value;
                if (string.IsNullOrEmpty(name) || ReservedWords.Contains(name) || !IsPlausibleVariableName(name))
                    continue;
                if (IsFalsePositiveDeclaration(scanText, name, pattern))
                    continue;
                (found ??= []).Add(name);
            }
        }

        return found is null ? [] : found.Distinct(StringComparer.Ordinal);
    }

    public void ScanDocument(string fileKey, string documentText, LoadedExtension? languageExtension = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileKey))
            return;
        cancellationToken.ThrowIfCancellationRequested();

        var extNames = new List<string>();
        var declarationNames = Array.Empty<string>();
        if (languageExtension?.LangRules is { } generated)
        {
            foreach (var name in generated.GetVariableLikeNames(documentText))
                extNames.Add(name);
            var symbols = generated.AnalyzeSymbols(documentText);
            if (symbols.Count > 0)
            {
                var names = new List<string>(symbols.Count);
                foreach (var symbol in symbols)
                {
                    if (!symbol.IsDeclaration || string.IsNullOrWhiteSpace(symbol.Name)) continue;
                    names.Add(symbol.Name);
                    extNames.Add(symbol.Name);
                }
                declarationNames = names.ToArray();
            }
        }

        var st = GetLineState(fileKey, "s", languageExtension);
        var maskedDoc = BuildMaskedDocument(documentText, languageExtension);
        var lineStart = BuildLineStartOffsets(maskedDoc);
        var lineCount = lineStart.Length;

        var hashes = System.Buffers.ArrayPool<ulong>.Shared.Rent(lineCount);
        try
        {
            var lineLen = new int[lineCount];
            var depthAtLine = new int[lineCount];
            var parenDepthAtLine = new int[lineCount];
            var bracketDepthAtLine = new int[lineCount];
        var cb = 0; var cp = 0; var ck = 0;
        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            depthAtLine[i] = cb; parenDepthAtLine[i] = cp; bracketDepthAtLine[i] = ck;
            var contentEnd = i + 1 < lineCount ? lineStart[i + 1] : maskedDoc.Length;
            var len = LineContentLength(maskedDoc, lineStart[i], contentEnd);
            lineLen[i] = len;
            var h = 0xcbf29ce484222325UL;
            var nb = 0; var np = 0; var nk = 0;
            foreach (var ch in maskedDoc.AsSpan(lineStart[i], len))
            {
                h ^= ch;
                h *= 0x100000001b3UL;
                if (ch == '{') nb++;
                else if (ch == '}') nb--;
                else if (ch == '(') np++;
                else if (ch == ')') np--;
                else if (ch == '[') nk++;
                else if (ch == ']') nk--;
            }
            h ^= (ulong)(uint)len;
            h *= 0x100000001b3UL;
            hashes[i] = h;
            cb += nb; cp += np; ck += nk;
        }
        RegisterLines(st, maskedDoc, lineStart, lineLen, hashes, lineCount, patchWordFreq: false, cancellationToken);

        var localContrib = new List<(int pos, string? joined)>();
        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var contentEnd = i + 1 < lineCount ? lineStart[i + 1] : maskedDoc.Length;
            var maskedLineSpan = maskedDoc.AsSpan(lineStart[i], LineContentLength(maskedDoc, lineStart[i], contentEnd));
            var facts = st.FactsAt[i];
            if (facts is null) continue;
            if (facts.VarNames.Length == 0) continue;
            var trimmedStart = 0;
            var trimmedEnd = maskedLineSpan.Length;
            while (trimmedStart < trimmedEnd && char.IsWhiteSpace(maskedLineSpan[trimmedStart])) trimmedStart++;
            while (trimmedEnd > trimmedStart && char.IsWhiteSpace(maskedLineSpan[trimmedEnd - 1])) trimmedEnd--;
            var maskedLineTrimmed = maskedLineSpan[trimmedStart..trimmedEnd];
            if ((parenDepthAtLine[i] > 0 || bracketDepthAtLine[i] > 0) && !IsVarDeclPrefix(maskedLineTrimmed)) continue;
            if (depthAtLine[i] > 0 && maskedLineTrimmed.Length > 0 && char.IsUpper(maskedLineTrimmed[0]) && !IsVarDeclPrefix(maskedLineTrimmed)) continue;
            if (depthAtLine[i] > 0 && maskedLineSpan[..trimmedEnd].EndsWith(","))
            {
                var t = maskedLineTrimmed;
                if (!IsVarDeclPrefix(t) && !t.Contains(';'))
                {
                    var eqIdx = t.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var lhs = t[..eqIdx].Trim().ToString();
                        if (SimpleAssignmentTarget.IsMatch(lhs) && !lhs.Contains(' '))
                            continue;
                    }
                }
            }

            var sb = new System.Text.StringBuilder();
            foreach (var name in facts.VarNames)
            {
                if (name == "_" || name.StartsWith('_')) continue;
                if (sb.Length > 0) sb.Append('\u0001');
                sb.Append(name);
            }
            localContrib.Add((i, sb.ToString()));
        }

        lock (st.SyncRoot)
        {
            var pc = st.PrevContrib;
            for (var pos = 0; pos < lineCount; pos++)
            {
                if (pos < pc.Length && pc[pos] is { } old)
                    PatchContribDelta(st, DiffNames(old, null), -1);
            }
            for (var pos = lineCount; pos < pc.Length; pos++)
            {
                if (pc[pos] is { } old)
                    PatchContribDelta(st, DiffNames(old, null), -1);
            }

            if (pc.Length >= lineCount)
                for (var i = 0; i < pc.Length; i++) pc[i] = null;
            else
                st.PrevContrib = pc = new string?[lineCount];

            foreach (var (pos, joined) in localContrib)
            {
                if (joined is { Length: > 0 })
                    PatchContribDelta(st, DiffNames(joined, null), +1);
                st.PrevContrib[pos] = joined;
            }
        }

        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in extNames) variables.Add(n);
        lock (st.SyncRoot)
        {
            foreach (var n in st.ScanNames) variables.Add(n);
        }

        lock (_fileCacheLock)
        {
            _variablesByFile[fileKey] = variables;
            _declarationNamesByFile[fileKey] = declarationNames;
        }
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(hashes);
        }
    }

    private static void PatchContribDelta(LineState st, IEnumerable<string> names, int delta)
    {
        foreach (var n in names)
        {
            st.ScanContrib.TryGetValue(n, out var c);
            var nc = c + delta;
            if (nc <= 0)
            {
                if (c > 0)
                {
                    st.ScanContrib.Remove(n);
                    st.ScanNames.Remove(n);
                }
            }
            else
            {
                if (c <= 0) st.ScanNames.Add(n);
                st.ScanContrib[n] = nc;
            }
        }
    }

    private static IEnumerable<string> DiffNames(string? leave, string? enter)
    {
        if (string.IsNullOrEmpty(leave)) yield break;
        var set = string.IsNullOrEmpty(enter) ? null : new HashSet<string>(enter.Split('\u0001'), StringComparer.Ordinal);
        foreach (var n in leave.Split('\u0001'))
            if (set is null || !set.Contains(n))
                yield return n;
    }

    public void ForgetFile(string fileKey)
    {
        lock (_fileCacheLock)
        {
            _variablesByFile.Remove(fileKey);
            _declarationNamesByFile.Remove(fileKey);
            _lineStates.Remove("s" + fileKey);
            _lineStates.Remove("d" + fileKey);
        }
    }

    private static readonly Regex KeywordFunctionDeclaration = new(
        @"\b(?:function|def|fn|func|sub|proc)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CStyleFunctionDeclaration = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex BlockTerminatorStatement = new(
        @"^\s*(?:return\b|throw\b|break\b|continue\b)[^{}]*;?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PossibleJumpTarget = new(
        @"^(?:case\b|default\s*:|[A-Za-z_][A-Za-z0-9_]*\s*:)",
        RegexOptions.Compiled);

    private static readonly Regex ClosingBraceOnlyLine = new(@"^\}+;?$", RegexOptions.Compiled);

    private static readonly Regex SimpleAssignmentTarget = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Regex WordIdentifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);

    private static readonly Regex ConditionalTerminatorOnIf = new(
        @"\bif\s*\(.*\)\s*(?:return|throw|break|continue)\b",
        RegexOptions.Compiled);

    private static readonly Regex ConditionalTerminatorOnElse = new(
        @"\belse\b.*\b(?:return|throw|break|continue)\b",
        RegexOptions.Compiled);

    private static readonly Regex OpenConditionalOnIf = new(@"\bif\s*\(.*\)\s*$", RegexOptions.Compiled);

    private static readonly Regex OpenConditionalOnElse = new(@"\belse\b\s*$", RegexOptions.Compiled);

    private static readonly Regex LeadingJumpStatement = new(
        @"^\s*(?:return|throw|break|continue)\b",
        RegexOptions.Compiled);

    private static int[] BuildLineStartOffsets(string text)
    {
        var starts = new List<int>(64) { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        return starts.ToArray();
    }

    private static int LineContentLength(string text, int contentStart, int exclusiveEnd)
    {
        var end = exclusiveEnd;
        if (end > contentStart && text[end - 1] == '\n') end--;
        if (end > contentStart && text[end - 1] == '\r') end--;
        return end - contentStart;
    }

    private static readonly HashSet<string> EntryPointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "wmain", "winmain", "wwinmain", "dllmain", "_start",
    };

    private static bool IsVarDeclPrefix(ReadOnlySpan<char> t)
        => t.StartsWith("var ", StringComparison.Ordinal) || t.StartsWith("let ", StringComparison.Ordinal) ||
           t.StartsWith("const ", StringComparison.Ordinal) || t.StartsWith("val ", StringComparison.Ordinal);
    public List<DeadCodeSpan> FindDeadCode(string documentText, LoadedExtension? languageExtension = null, string? folderPath = null, string? currentFilePath = null, CancellationToken cancellationToken = default, string? fileKey = null)
    {
        var spans = new List<DeadCodeSpan>();
        if (string.IsNullOrEmpty(documentText))
            return spans;
        cancellationToken.ThrowIfCancellationRequested();

        var ignoreNames = languageExtension?.DeadCodeIgnore is { Length: > 0 } ignoreArr
            ? new HashSet<string>(ignoreArr, StringComparer.OrdinalIgnoreCase)
            : null;
        var entryPoints = languageExtension?.DeadCodeEntryPoints is { Length: > 0 } entryArr
            ? new HashSet<string>(entryArr, StringComparer.OrdinalIgnoreCase)
            : null;

        var st = fileKey is { Length: > 0 } ? GetLineState(fileKey, "d", languageExtension) : new LineState();

        var lineStart = BuildLineStartOffsets(documentText);
        var lineCount = lineStart.Length;

        var maskedDoc = BuildMaskedDocument(documentText, languageExtension);
        var lineLen = new int[lineCount];
        var braceDelta = new int[lineCount];

        var hashes = System.Buffers.ArrayPool<ulong>.Shared.Rent(lineCount);
        try
        {
        string? folderMaskedText = null;
        if (!string.IsNullOrWhiteSpace(folderPath) && !string.IsNullOrWhiteSpace(currentFilePath) && System.IO.Directory.Exists(folderPath))
            folderMaskedText = BuildFolderMaskedText(folderPath, currentFilePath, languageExtension);

        var depthAtLine = new int[lineCount];
        var parenDepthAtLine = new int[lineCount];
        var bracketDepthAtLine = new int[lineCount];
        var cb = 0; var cp = 0; var ck = 0;
        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var contentStart = lineStart[i];
            var contentEnd = i + 1 < lineCount ? lineStart[i + 1] : maskedDoc.Length;
            lineLen[i] = LineContentLength(maskedDoc, contentStart, contentEnd);
            depthAtLine[i] = cb; parenDepthAtLine[i] = cp; bracketDepthAtLine[i] = ck;
            var h = 0xcbf29ce484222325UL;
            var nb = 0; var np = 0; var nk = 0;
            foreach (var ch in maskedDoc.AsSpan(contentStart, lineLen[i]))
            {
                h ^= ch;
                h *= 0x100000001b3UL;
                if (ch == '{') nb++;
                else if (ch == '}') nb--;
                else if (ch == '(') np++;
                else if (ch == ')') np--;
                else if (ch == '[') nk++;
                else if (ch == ']') nk--;
            }
            h ^= (ulong)(uint)lineLen[i];
            h *= 0x100000001b3UL;
            hashes[i] = h;
            cb += nb; cp += np; ck += nk;
            braceDelta[i] = nb;
        }
        RegisterLines(st, maskedDoc, lineStart, lineLen, hashes, lineCount, patchWordFreq: true, cancellationToken);

        if (folderMaskedText is not null)
        {
            lock (st.SyncRoot)
            {
                st.WordFolderFreq.Clear();
                var freqCount = 0;
                foreach (var m in WordIdentifier.EnumerateMatches(folderMaskedText))
                {
                    if ((freqCount++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    PatchFreq(st.WordFolderFreq, folderMaskedText.Substring(m.Index, m.Length), 1);
                }
            }
        }

        int CountWholeWord(string name) =>
            (st.WordFreq.TryGetValue(name, out var c1) ? c1 : 0) +
            (st.WordFolderFreq.TryGetValue(name, out var c2) ? c2 : 0);

        ReadOnlySpan<char> Line(int i) => maskedDoc.AsSpan(lineStart[i], lineLen[i]);

        var seenVariable = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var facts = st.FactsAt[i];
            if (facts is null) continue;
            if (facts.VarNames.Length == 0) continue;
            var lineSpan = Line(i);
            var line = lineSpan.ToString();
            var trimmedForProp = line.Trim();
            foreach (var name in facts.VarNames)
            {
                if (!seenVariable.Add(name)) continue;
                if (ignoreNames is not null && ignoreNames.Contains(name)) continue;

                if (name == "_" || name.StartsWith('_')) continue;

                if (parenDepthAtLine[i] > 0 || bracketDepthAtLine[i] > 0)
                {
                    var isVarDeclInArg = IsVarDeclPrefix(trimmedForProp.AsSpan());
                    if (!isVarDeclInArg)
                        continue;
                }

                if (depthAtLine[i] > 0 && trimmedForProp.Contains("="))
                {
                    var isVarDecl = IsVarDeclPrefix(trimmedForProp.AsSpan());
                    if (!isVarDecl)
                    {
                        if (trimmedForProp.Length > 0 && char.IsUpper(trimmedForProp[0]))
                            continue;
                        if (trimmedForProp.TrimEnd().EndsWith(",") && !trimmedForProp.Contains(";"))
                        {
                            var eqIdx = trimmedForProp.IndexOf('=');
                            if (eqIdx > 0)
                            {
                                var lhs = trimmedForProp.Substring(0, eqIdx).Trim();
                                if (SimpleAssignmentTarget.IsMatch(lhs))
                                    continue;
                            }
                        }
                    }
                }

                if (CountWholeWord(name) <= 1)
                {
                    var lineEnd = i + 1 < lineCount ? lineStart[i + 1] : documentText.Length;
                    spans.Add(new DeadCodeSpan(lineStart[i], LineContentLength(documentText, lineStart[i], lineEnd), "Unused variable"));
                }
            }
        }

        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var facts = st.FactsAt[i];
            if (facts is null) continue;
            string? name = facts.DeclValid ? facts.DeclName : null;

            if (name is null || EntryPointNames.Contains(name)) continue;
            if (entryPoints is not null && entryPoints.Contains(name)) continue;
            if (ignoreNames is not null && ignoreNames.Contains(name)) continue;

            var refCount = CountWholeWord(name);
            if (refCount > 1) continue;

            var endLine = i;
            var depth = braceDelta[i];
            if (depth > 0)
            {
                var j = i + 1;
                while (j < lineCount && depth > 0)
                {
                    depth += braceDelta[j];
                    j++;
                }
                endLine = Math.Min(j - 1, lineCount - 1);
            }

            var start = lineStart[i];
            var end = lineStart[endLine] + LineContentLength(documentText, lineStart[endLine], endLine + 1 < lineCount ? lineStart[endLine + 1] : documentText.Length);
            spans.Add(new DeadCodeSpan(start, end - start, "Unused function"));
        }

        for (var i = 0; i < lineCount; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var facts = st.FactsAt[i];
            if (facts is null) continue;
            if (!facts.TermStarts) continue;
            if (!facts.BlockTerm)
                continue;
            if (IsConditionalTerminatorCtx(st.FactsAt, i))
                continue;

            var depth = 0;
            var deadStart = -1;
            var deadEnd = -1;
            var j = i + 1;
            while (j < lineCount)
            {
                var jFacts = st.FactsAt[j];
                if (jFacts is null) { j++; continue; }
                if (jFacts.TrimmedEmpty) { j++; continue; }
                if (jFacts.ClosingBrace) break;
                if (jFacts.JumpTarget) break;

                if (deadStart < 0) deadStart = j;

                var lineDepthChange = braceDelta[j];
                if (depth + lineDepthChange < 0) break;
                depth += lineDepthChange;
                deadEnd = j;
                j++;
            }

            if (deadStart >= 0 && deadEnd >= deadStart)
            {
                var start = lineStart[deadStart];
                var end = lineStart[deadEnd] + LineContentLength(documentText, lineStart[deadEnd], deadEnd + 1 < lineCount ? lineStart[deadEnd + 1] : documentText.Length);
                spans.Add(new DeadCodeSpan(start, end - start, "Unreachable code"));
                i = deadEnd;
            }
        }

        return spans;
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(hashes);
        }
    }

    private static bool IsConditionalTerminatorCtx(LineFacts[] factsAt, int terminatorIndex)
    {
        var f = factsAt[terminatorIndex];
        if (f.CondIf || f.CondElse)
            return true;
        for (var k = terminatorIndex - 1; k >= 0; k--)
        {
            var p = factsAt[k];
            if (p is null || p.TrimmedEmpty) continue;
            if (p.OpenIf || p.OpenElse)
            {
                if (f.LeadingJump && !p.HasBrace)
                    return true;
            }
            break;
        }
        return false;
    }

    public IReadOnlyCollection<string> GetVariables(string fileKey)
    {
        lock (_fileCacheLock)
            return _variablesByFile.TryGetValue(fileKey, out var vars) ? vars : Array.Empty<string>();
    }

    private IReadOnlyList<string> GetDeclarationNames(string fileKey, string documentText, LangRulesAdapter rules)
    {
        lock (_fileCacheLock)
        {
            if (_declarationNamesByFile.TryGetValue(fileKey, out var cached)) return cached;
        }
        var symbols = rules.AnalyzeSymbols(documentText);
        var names = new List<string>(symbols.Count);
        foreach (var symbol in symbols)
            if (symbol.IsDeclaration && !string.IsNullOrWhiteSpace(symbol.Name))
                names.Add(symbol.Name);
        return names;
    }

    public List<ErrorSpan> FindErrors(
        string documentText,
        LoadedExtension? languageExtension = null,
        Func<string, LoadedExtension?>? embeddedLanguageResolver = null,
        CancellationToken cancellationToken = default)
    {
        var spans = new List<ErrorSpan>();
        if (string.IsNullOrEmpty(documentText))
            return spans;
        cancellationToken.ThrowIfCancellationRequested();

        if (languageExtension?.LangRules is { HasDiagnostics: true } generated)
        {
            var extensionDiagnostics = generated.AnalyzeSyntax(documentText);
            AddDiagnostics(spans, extensionDiagnostics, documentText.Length, cancellationToken);
        }

        if (languageExtension?.EnableSemanticDiagnostics == true &&
            languageExtension.LangRules is { HasSemanticAnalyzer: true } semanticRules)
            AddDiagnostics(spans, semanticRules.AnalyzeSemantics(documentText), documentText.Length, cancellationToken);

        if (embeddedLanguageResolver is not null && languageExtension?.LangRules is { HasEmbeddedRegions: true } embeddedRules)
        {
            foreach (var region in embeddedRules.GetEmbeddedRegions(documentText))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (region.Start < 0 || region.Length <= 0 || region.Start >= documentText.Length ||
                    region.Start + region.Length > documentText.Length)
                    continue;

                var embeddedExtension = embeddedLanguageResolver(region.LanguageId);
                if (embeddedExtension?.LangRules is null)
                    continue;

                var embeddedText = documentText.Substring(region.Start, region.Length);
                var embeddedSpans = FindErrors(embeddedText, embeddedExtension, null, cancellationToken);
                foreach (var embeddedSpan in embeddedSpans)
                {
                    var mappedStart = region.Start + embeddedSpan.StartOffset;
                    spans.Add(new ErrorSpan(
                        mappedStart,
                        embeddedSpan.Length,
                        embeddedSpan.Message,
                        embeddedSpan.Severity,
                        embeddedSpan.Code,
                        embeddedSpan.Source));
                }
            }
        }
        return spans
            .GroupBy(span => (span.StartOffset, span.Length, Severity: span.Severity.Trim().ToLowerInvariant(), Message: span.Message.Trim().ToLowerInvariant(), span.Code))
            .Select(group => group.OrderByDescending(span => span.Source.Contains("Recovery", StringComparison.OrdinalIgnoreCase)).First())
            .ToList();
    }
    private static void AddDiagnostics(
        ICollection<ErrorSpan> destination,
        IReadOnlyList<LangRuleDiagnostic> diagnostics,
        int documentLength,
        CancellationToken cancellationToken)
    {
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(diagnostic.Message) || diagnostic.Start < 0 || diagnostic.Start >= documentLength)
                continue;

            var length = Math.Clamp(diagnostic.Length, 1, documentLength - diagnostic.Start);
            destination.Add(new ErrorSpan(
                diagnostic.Start,
                length,
                diagnostic.Message.Trim(),
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Source));
        }
    }

    private static bool MatchesAt(string text, int index, string token)
    {
        if (string.IsNullOrEmpty(token) || index + token.Length > text.Length)
            return false;
        for (var k = 0; k < token.Length; k++)
            if (text[index + k] != token[k]) return false;
        return true;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static int FindWordStart(string documentText, int caretOffset)
    {
        var start = caretOffset;
        while (start > 0 && IsWordChar(documentText[start - 1]))
            start--;
        return start;
    }

    private static IReadOnlyCollection<string> GetEnclosingCallNames(string documentText, int caretOffset)
    {
        if (string.IsNullOrEmpty(documentText) || caretOffset <= 0)
            return Array.Empty<string>();

        var callStack = new Stack<string?>();
        var token = new List<char>();
        var inString = false;
        var stringDelimiter = '\0';
        var escaped = false;
        var inLineComment = false;

        static string? FlushToken(List<char> chars)
        {
            if (chars.Count == 0)
                return null;
            var value = new string(chars.ToArray());
            chars.Clear();
            return value;
        }

        for (var i = 0; i < caretOffset; i++)
        {
            var c = documentText[i];

            if (inLineComment)
            {
                if (c == '\n')
                    inLineComment = false;
                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == stringDelimiter)
                {
                    inString = false;
                    stringDelimiter = '\0';
                }

                continue;
            }

            if (c == '/' && i + 1 < caretOffset && documentText[i + 1] == '/')
            {
                inLineComment = true;
                token.Clear();
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                stringDelimiter = c;
                token.Clear();
                continue;
            }

            if (IsWordChar(c))
            {
                token.Add(c);
                continue;
            }

            if (c == '(')
            {
                callStack.Push(FlushToken(token));
                continue;
            }

            token.Clear();

            if (c == ')')
            {
                if (callStack.Count > 0)
                    callStack.Pop();
            }
        }

        return callStack.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray();
    }

    public List<InsightSuggestion> GetSuggestions(
        string prefix,
        string fileKey,
        LoadedExtension? languageExtension,
        string documentText,
        int caretOffset,
        int maxResults = 25)
    {
        var results = new List<InsightSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var enclosingCalls = GetEnclosingCallNames(documentText, caretOffset);
        var blacklist = languageExtension?.Blacklist is { Length: > 0 }
            ? new HashSet<string>(languageExtension.Blacklist, StringComparer.OrdinalIgnoreCase)
            : null;

        void AddCandidates(IEnumerable<string> names, InsightKind kind)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!string.IsNullOrEmpty(prefix) &&
                    !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (blacklist is not null && enclosingCalls.Any(call => blacklist.Contains(call) && string.Equals(call, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!seen.Add(name)) continue;
                results.Add(new InsightSuggestion(name, kind));
            }
        }

        AddCandidates(GetVariables(fileKey), InsightKind.Variable);

        if (languageExtension is not null)
        {
            if (languageExtension.LangRules is { } generated)
            {
                AddCandidates(generated.GetCompletions(prefix, documentText), InsightKind.Keyword);
                AddCandidates(GetDeclarationNames(fileKey, documentText, generated), InsightKind.Variable);
            }
            AddCandidates(languageExtension.Functions, InsightKind.Function);
            AddCandidates(languageExtension.Properties, InsightKind.Property);
            AddCandidates(languageExtension.Types, InsightKind.Type);
            AddCandidates(languageExtension.Namespaces, InsightKind.Namespace);
            AddCandidates(languageExtension.Keywords, InsightKind.Keyword);
        }

        return results
            .Where(s => !string.Equals(s.Text, prefix, StringComparison.Ordinal))
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }
}
