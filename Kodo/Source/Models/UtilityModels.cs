// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using Avalonia;

namespace Kodo.Models;

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
                while (xi < x.Length && x[xi] == '0') xi++;
                while (yi < y.Length && y[yi] == '0') yi++;

                var xStart = xi;
                var yStart = yi;
                while (xi < x.Length && char.IsAsciiDigit(x[xi])) xi++;
                while (yi < y.Length && char.IsAsciiDigit(y[yi])) yi++;

                var xLen = xi - xStart;
                var yLen = yi - yStart;

                if (xLen != yLen) return xLen.CompareTo(yLen);

                var cmp = string.Compare(x, xStart, y, yStart, xLen, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
            else
            {
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

public sealed class FormattedParagraph
{
    public IReadOnlyList<FormattedRun> Runs      { get; init; } = [];
    // Extra top margin so paragraphs breathe; bullet items get slightly less.
    public Thickness TopMargin { get; init; } = new Thickness(0, 6, 0, 0);
    public string Marker { get; init; } = string.Empty;
    public double MarkerColumnWidth { get; init; }
}
