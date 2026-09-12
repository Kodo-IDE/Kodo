// Licensed under GPL-v3.0
using System.Collections.Generic;

namespace Kodo;

public sealed record LanguageDocumentSnapshot(string Uri, long Version, string Text);
public sealed record LanguageTextChange(int Start, int Length, string NewText);

public sealed record LanguageWorkerRequest(
    string Method,
    LanguageDocumentSnapshot Document,
    string? Symbol = null,
    int Offset = 0,
    IReadOnlyList<LanguageTextChange>? Changes = null);

public sealed record LanguageWorkerResponse(
    string Method,
    long Version,
    object? Result,
    string? Error = null);
