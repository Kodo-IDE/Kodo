// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using Kodo;
using Kodo.Models;
using Xunit;

namespace Kodo.Tests;

[Collection("RegionalSpelling")]
public sealed class RegionalSpellingTests
{
    [Fact]
    public void UnitedStates_UsesAmericanSpelling()
    {
        using var scope = new RegionalSpellingScope("US");
        Assert.True(RegionalSpelling.IsAmericanEnglish);
        Assert.Equal("color", RegionalSpelling.Color);
        Assert.Equal("canceled", RegionalSpelling.Cancelled);
        Assert.Equal("behavior", RegionalSpelling.Behavior);
        Assert.Equal("gray", RegionalSpelling.Gray);
        Assert.Equal("center", RegionalSpelling.Center);
        Assert.Equal("personalize", RegionalSpelling.Personalize);
        Assert.Equal("personalization", RegionalSpelling.Personalization);
        Assert.Equal("organize", RegionalSpelling.Organize);
    }

    [Fact]
    public void Philippines_MapsToAmericanSpelling()
    {
        using var scope = new RegionalSpellingScope("PH");
        Assert.True(RegionalSpelling.IsAmericanEnglish);
        Assert.Equal("color", RegionalSpelling.Color);
        Assert.Equal("personalize", RegionalSpelling.Personalize);
    }

    [Fact]
    public void GreatBritain_UsesBritishSpelling()
    {
        using var scope = new RegionalSpellingScope("GB");
        Assert.False(RegionalSpelling.IsAmericanEnglish);
        Assert.Equal("colour", RegionalSpelling.Color);
        Assert.Equal("cancelled", RegionalSpelling.Cancelled);
        Assert.Equal("behaviour", RegionalSpelling.Behavior);
        Assert.Equal("grey", RegionalSpelling.Gray);
        Assert.Equal("centre", RegionalSpelling.Center);
        Assert.Equal("personalise", RegionalSpelling.Personalize);
        Assert.Equal("organise", RegionalSpelling.Organize);
    }

    [Fact]
    public void Canada_UsesAmericanPersonalizeButBritishColor()
    {
        using var scope = new RegionalSpellingScope("CA");
        Assert.False(RegionalSpelling.IsAmericanEnglish);
        Assert.Equal("colour", RegionalSpelling.Color);
        Assert.Equal("personalization", RegionalSpelling.Personalization);
        Assert.Equal("customize", RegionalSpelling.Customize);
        Assert.Equal("organize", RegionalSpelling.Organize);
    }

    [Fact]
    public void AustraliaAndNewZealand_UseBritishSpelling()
    {
        using var scope = new RegionalSpellingScope("AU");
        Assert.Equal("colour", RegionalSpelling.Color);
        Assert.Equal("personalise", RegionalSpelling.Personalize);

        RegionalSpelling.SetFromUserCountry("NZ");
        Assert.Equal("AU", RegionalSpelling.LocaleCode);
        Assert.Equal("colour", RegionalSpelling.Color);
    }

    [Fact]
    public void Ireland_MapsToBritishSpelling()
    {
        using var scope = new RegionalSpellingScope("IE");
        Assert.Equal("GB", RegionalSpelling.LocaleCode);
        Assert.Equal("colour", RegionalSpelling.Color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ZZ")]
    [InlineData("not-a-country")]
    public void UnknownCountry_FallsBackToBritishSpelling(string country)
    {
        using var scope = new RegionalSpellingScope(country);
        Assert.False(RegionalSpelling.IsAmericanEnglish);
        Assert.Equal("colour", RegionalSpelling.Color);
    }

    [Fact]
    public void SetFromUserCountry_IsCaseAndWhitespaceInsensitive()
    {
        using var scope = new RegionalSpellingScope("  us  ");
        Assert.True(RegionalSpelling.IsAmericanEnglish);
        Assert.Equal("US", RegionalSpelling.LocaleCode);
    }

    [Fact]
    public void ApplyTutorial_NormalizesBothSpellings()
    {
        using var scope = new RegionalSpellingScope("GB");
        Assert.Equal("accent colour", RegionalSpelling.ApplyTutorial("accent color"));
        Assert.Equal("accent colour", RegionalSpelling.ApplyTutorial("accent colour"));
        Assert.Equal("personalise", RegionalSpelling.ApplyTutorial("personalize"));
        Assert.Equal("behaviour", RegionalSpelling.ApplyTutorial("behavior"));

        RegionalSpelling.SetFromUserCountry("US");
        Assert.Equal("accent color", RegionalSpelling.ApplyTutorial("accent colour"));
        Assert.Equal("personalize", RegionalSpelling.ApplyTutorial("personalise"));
        Assert.Equal("behavior", RegionalSpelling.ApplyTutorial("behaviour"));
    }
}

public sealed class FileSystemPathTests
{
    [Fact]
    public void IsPrefixOf_MatchesSameDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "kodo-prefix-root");
        Assert.True(FileSystemPaths.IsPrefixOf(root, root));
    }

    [Fact]
    public void IsPrefixOf_MatchesNestedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "kodo-prefix-root");
        var file = Path.Combine(root, "src", "file.cs");
        Assert.True(FileSystemPaths.IsPrefixOf(file, root));
    }

    [Fact]
    public void IsPrefixOf_RejectsSiblingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "kodo-prefix-root");
        var sibling = Path.Combine(Path.GetTempPath(), "kodo-prefix-root-other", "file.cs");
        Assert.False(FileSystemPaths.IsPrefixOf(sibling, root));
    }

    [Fact]
    public void IsPrefixOf_HandlesTrailingSeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), "kodo-prefix-root") + Path.DirectorySeparatorChar;
        var file = Path.Combine(Path.GetTempPath(), "kodo-prefix-root", "a.txt");
        Assert.True(FileSystemPaths.IsPrefixOf(file, root));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    public void IsPrefixOf_ReturnsFalseForEmptyInput(string? path, string? prefix)
    {
        Assert.False(FileSystemPaths.IsPrefixOf(path, prefix));
    }

    [Fact]
    public void StartsWithAndEndsWith_FollowPlatformComparison()
    {
        var expectedCaseInsensitive = OperatingSystem.IsWindows();
        Assert.Equal(expectedCaseInsensitive, FileSystemPaths.StartsWith(@"C:\Kodo\File.cs", @"c:\kodo"));
        Assert.Equal(expectedCaseInsensitive, FileSystemPaths.EndsWith(@"C:\Kodo\File.CS", ".cs"));
    }

    [Fact]
    public void Equals_UsesPlatformComparison()
    {
        var equal = OperatingSystem.IsWindows()
            ? FileSystemPaths.Equals(@"C:\Kodo", @"c:\kodo")
            : !FileSystemPaths.Equals(@"C:\Kodo", @"c:\kodo");
        Assert.True(equal);
        Assert.True(FileSystemPaths.Equals("same", "same"));
        Assert.False(FileSystemPaths.Equals("a", "b"));
    }
}

public sealed class LspPathTests
{
    [Fact]
    public void FilePathToUri_ProducesFileUri()
    {
        var path = Path.Combine(Path.GetTempPath(), "kodo space", "file.cs");
        var uri = LspPath.FilePathToUri(path);
        Assert.StartsWith("file:", uri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file.cs", uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileUriToPath_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "kodo space", "file.cs");
        var restored = LspPath.FileUriToPath(LspPath.FilePathToUri(path));
        Assert.Equal(LspPath.NormalizeFilePath(path), LspPath.NormalizeFilePath(restored));
    }

    [Fact]
    public void FileUriToPath_ReturnsRootedPathForUri()
    {
        var restored = LspPath.FileUriToPath("file:///C:/Users/me/a%20b.cs");
        Assert.True(Path.IsPathRooted(restored));
        Assert.EndsWith("a b.cs", restored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileUriToPath_PassesThroughPlainPaths()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "plain.cs"));
        Assert.Equal(path, LspPath.FileUriToPath(path));
    }

    [Fact]
    public void NormalizeFilePath_TrimsTrailingSeparator()
    {
        var directory = Path.GetFullPath(Path.GetTempPath());
        var normalized = LspPath.NormalizeFilePath(directory + Path.DirectorySeparatorChar);
        Assert.Equal(Path.TrimEndingDirectorySeparator(directory), normalized);
    }

    [Fact]
    public void IsSameDocument_MatchesEquivalentForms()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "doc.cs"));
        Assert.True(LspPath.IsSameDocument(path, LspPath.FilePathToUri(path)));
        Assert.False(LspPath.IsSameDocument(path, null));
        Assert.False(LspPath.IsSameDocument(null, path));
    }

    [Fact]
    public void GetLineStarts_HandlesMixedLineEndings()
    {
        var starts = LspPath.GetLineStarts("ab\ncd\r\nef");
        Assert.Equal(new[] { 0, 3, 7 }, starts);
    }

    [Fact]
    public void GetLanguageId_PrefersExplicitLanguagesThenExtension()
    {
        var lsp = new LspConfiguration { Languages = ["csharp"] };
        Assert.Equal("csharp", LspPath.GetLanguageId(lsp, "file.cs"));

        var extensionOnly = new LspConfiguration { Languages = [] };
        Assert.Equal("cs", LspPath.GetLanguageId(extensionOnly, "file.CS"));
        Assert.Equal("plaintext", LspPath.GetLanguageId(extensionOnly, null));
        Assert.Equal("plaintext", LspPath.GetLanguageId(extensionOnly, "Makefile"));
    }
}

public sealed class NaturalSortComparerTests
{
    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("a", "b")]
    [InlineData("item9", "item10")]
    public void SortsNumericallyAndAlphabetically(string smaller, string larger)
    {
        Assert.True(NaturalSortComparer.OrdinalIgnoreCase.Compare(smaller, larger) < 0);
        Assert.True(NaturalSortComparer.OrdinalIgnoreCase.Compare(larger, smaller) > 0);
    }

    [Fact]
    public void ZeroPaddedNumbers_CompareEqual()
    {
        Assert.Equal(0, NaturalSortComparer.OrdinalIgnoreCase.Compare("file02", "file2"));
    }

    [Fact]
    public void Nulls_SortFirstAndEqual()
    {
        Assert.True(NaturalSortComparer.OrdinalIgnoreCase.Compare(null, "a") < 0);
        Assert.True(NaturalSortComparer.OrdinalIgnoreCase.Compare("a", null) > 0);
        Assert.Equal(0, NaturalSortComparer.OrdinalIgnoreCase.Compare(null, null));
    }

    [Fact]
    public void SortsMixedListNaturally()
    {
        var files = new List<string> { "file10", "file2", "file1" };
        files.Sort(NaturalSortComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "file1", "file2", "file10" }, files);
    }
}
