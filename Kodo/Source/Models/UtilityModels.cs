// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using Avalonia;

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

public sealed class NewsItem
{
    public string Title     { get; init; } = string.Empty;
    public string Body      { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public bool HasTitle     => !string.IsNullOrWhiteSpace(Title);
    public bool HasBody      => !string.IsNullOrWhiteSpace(Body);
    public bool HasUpdatedAt => !string.IsNullOrWhiteSpace(UpdatedAt);
}

public class ReleaseInfo
{
    public string Name { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public class ReleaseLinkItem
{
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

// One inline run (bold or normal) within a release-notes paragraph.
public sealed class FormattedRun
{
    public string Text   { get; init; } = string.Empty;
    public bool   IsBold { get; init; }
}

// One release-notes paragraph; the bullet/number marker is kept separate so wrapped lines hang-indent.
public sealed class FormattedParagraph
{
    public IReadOnlyList<FormattedRun> Runs      { get; init; } = [];
    // Extra top margin so paragraphs breathe; bullet items get slightly less.
    public Thickness TopMargin { get; init; } = new Thickness(0, 6, 0, 0);
    // "•" for bullets, "1." / "2." / ... for ordered items, or empty for a
    // plain paragraph/heading (in which case MarkerColumnWidth is 0).
    public string Marker { get; init; } = string.Empty;
    // Fixed width of the marker column, shared across rows so every bullet's
    // wrapped text lines up under its own first line instead of under "• ".
    public double MarkerColumnWidth { get; init; }
}
