// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo.Models;

/// <summary>
/// Fuzzy subsequence matching with scoring. Used for "Find files by name"
/// to rank results by relevance rather than simple substring containment.
/// </summary>
internal static class FuzzyMatch
{
    /// <summary>
    /// Returns a relevance score and the matched character indices.
    /// Returns (-1, empty) if the query does not match as a subsequence.
    /// </summary>
    public static (int Score, IReadOnlyList<int> Indices) Match(string query, string value, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(value))
            return (-1, Array.Empty<int>());

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Fast path: exact substring match — high base score.
        var exactIndex = value.IndexOf(query, comparison);
        if (exactIndex >= 0)
        {
            var exactIndices = new List<int>(query.Length);
            for (var i = 0; i < query.Length; i++)
                exactIndices.Add(exactIndex + i);

            var exactScore = 1000 + (exactIndex == 0 ? 200 : 0);
            if (string.Compare(query, 0, value, exactIndex, query.Length, comparison) == 0
                && query.Length == value.Substring(exactIndex, query.Length).Length)
                exactScore += 100;

            return (exactScore, exactIndices);
        }

        // Fuzzy subsequence match.
        var qi = 0;
        var vi = 0;
        var fuzzyIndices = new List<int>(query.Length);
        var fuzzyScore = 0;
        var lastMatchVi = -1;
        var consecutiveStreak = 0;

        while (qi < query.Length && vi < value.Length)
        {
            if (char.ToUpperInvariant(query[qi]) == char.ToUpperInvariant(value[vi]))
            {
                fuzzyIndices.Add(vi);

                if (lastMatchVi == vi - 1)
                {
                    consecutiveStreak++;
                    fuzzyScore += consecutiveStreak * 15;
                }
                else
                {
                    consecutiveStreak = 0;
                }

                if (vi == 0)
                {
                    fuzzyScore += 150;
                }
                else
                {
                    var prev = value[vi - 1];
                    if (prev is '_' or '-' or '.' or '/' or '\\')
                        fuzzyScore += 100;
                    else if (char.IsUpper(value[vi]) && char.IsLower(prev))
                        fuzzyScore += 80;
                }

                if (query[qi] == value[vi])
                    fuzzyScore += 5;

                lastMatchVi = vi;
                qi++;
            }
            vi++;
        }

        if (qi < query.Length)
            return (-1, Array.Empty<int>());

        var span = fuzzyIndices[^1] - fuzzyIndices[0];
        fuzzyScore -= span * 2;
        fuzzyScore -= (value.Length - query.Length) * 3;

        return (fuzzyScore, fuzzyIndices);
    }
}
