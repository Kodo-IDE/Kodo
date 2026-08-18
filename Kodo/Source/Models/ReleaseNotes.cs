// Licensed under GPL-v3.0
using System.Collections.Generic;
using Avalonia;

namespace Kodo.Models;

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