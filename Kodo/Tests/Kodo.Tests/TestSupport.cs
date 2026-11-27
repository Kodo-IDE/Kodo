// Licensed under the GNU GPL-v3.0
using System;
using System.IO;

namespace Kodo.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kodo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

internal sealed class RegionalSpellingScope : IDisposable
{
    private readonly string _previous = RegionalSpelling.LocaleCode;

    public RegionalSpellingScope(string country) => RegionalSpelling.SetFromUserCountry(country);

    public void Dispose() => RegionalSpelling.LocaleCode = _previous;
}
