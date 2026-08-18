// Licensed under GPL-v3.0
using System;

namespace Kodo.Models;

// Single source of truth for the built-in Markdown extension id, used by several colorizers.
public static class KodoExtensionIds
{
    public const string Markdown = "markdown-kodo-extension";

    public static bool IsMarkdown(string? extensionId) =>
        string.Equals(extensionId, Markdown, StringComparison.OrdinalIgnoreCase);
}