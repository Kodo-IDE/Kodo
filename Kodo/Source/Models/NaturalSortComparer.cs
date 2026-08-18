// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo.Models;

// Compares strings the way humans expect - splits into digit/non-digit chunks, compares digits numerically.
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer OrdinalIgnoreCase = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return  1;

        var xi = 0;
        var yi = 0;

        while (xi < x.Length && yi < y.Length)
        {
            var xIsDigit = char.IsAsciiDigit(x[xi]);
            var yIsDigit = char.IsAsciiDigit(y[yi]);

            if (xIsDigit && yIsDigit)
            {
                // Skip leading zeros so "007" == "7" numerically
                while (xi < x.Length && x[xi] == '0') xi++;
                while (yi < y.Length && y[yi] == '0') yi++;

                // Find end of digit run in both strings
                var xStart = xi;
                var yStart = yi;
                while (xi < x.Length && char.IsAsciiDigit(x[xi])) xi++;
                while (yi < y.Length && char.IsAsciiDigit(y[yi])) yi++;

                var xLen = xi - xStart;
                var yLen = yi - yStart;

                // Longer digit sequence is numerically larger
                if (xLen != yLen) return xLen.CompareTo(yLen);

                // Same length: compare digit-by-digit
                var cmp = string.Compare(x, xStart, y, yStart, xLen, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
            else
            {
                // Non-digit chunk: plain case-insensitive char comparison
                var cmp = char.ToUpperInvariant(x[xi])
                              .CompareTo(char.ToUpperInvariant(y[yi]));
                if (cmp != 0) return cmp;
                xi++;
                yi++;
            }
        }

        return (x.Length - xi).CompareTo(y.Length - yi);
    }
}