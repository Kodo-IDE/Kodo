// Licensed under GPL v3.0
using System.Text.RegularExpressions;

namespace Kodo;

public readonly record struct CompiledSyntaxRule(Regex Regex, string ColorTokenName, string FallbackHex);

public enum EmbeddedBlockContentMode { AwaitingContent, Raw, InCData }

public enum EmbeddedSyntaxScanMode { Normal, LineComment, BlockComment, String, MultiLineString }

public readonly record struct EmbeddedSyntaxState(
    string BracketStack,
    EmbeddedSyntaxScanMode Mode,
    string? Delimiter,
    bool IsVerbatimString,
    bool CanSpanMultipleLines)
{
    public static EmbeddedSyntaxState Empty { get; } = new(string.Empty, EmbeddedSyntaxScanMode.Normal, null, false, false);
}
