// Licensed under GPL-v3.0
using System;

namespace Kodo;

internal static class FileSystemPaths
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool Equals(string? a, string? b) =>
        string.Equals(a, b, Comparison);

    public static bool StartsWith(string? path, string? prefix) =>
        path is not null && prefix is not null &&
        path.StartsWith(prefix, Comparison);

    public static bool EndsWith(string? path, string? suffix) =>
        path is not null && suffix is not null &&
        path.EndsWith(suffix, Comparison);

    public static bool IsPrefixOf(string? path, string? dirPrefix)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dirPrefix))
            return false;
        var sep = System.IO.Path.DirectorySeparatorChar.ToString();
        return Equals(path, dirPrefix) || StartsWith(path, dirPrefix + sep);
    }
}
