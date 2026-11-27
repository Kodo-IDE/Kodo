// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using System.Linq;
using Kodo;
using Kodo.Models;
using Xunit;

namespace Kodo.Tests;

public sealed class SearchIgnoreRulesTests
{
    [Fact]
    public void GitIgnorePatterns_SkipMatchingFiles()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.log\n");
        var rules = SearchIgnoreRules.Load(temp.Path);

        Assert.True(rules.ShouldSkipFile(temp.Combine("debug.log")));
        Assert.False(rules.ShouldSkipFile(temp.Combine("main.cs")));
    }

    [Fact]
    public void NegatedGitIgnorePatterns_RestoreFiles()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.log\n!keep.log\n");
        var rules = SearchIgnoreRules.Load(temp.Path);

        Assert.True(rules.ShouldSkipFile(temp.Combine("debug.log")));
        Assert.False(rules.ShouldSkipFile(temp.Combine("keep.log")));
    }

    [Fact]
    public void DirectoryPatterns_SkipMatchingDirectories()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "build/\n");
        var build = temp.CreateDirectory("build");
        var source = temp.CreateDirectory("src");
        var rules = SearchIgnoreRules.Load(temp.Path);

        Assert.True(rules.ShouldSkipDirectory(build));
        Assert.False(rules.ShouldSkipDirectory(source));
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    public void DefaultIgnoreDirectories_AreSkipped(string directoryName)
    {
        using var temp = new TempDirectory();
        var directory = temp.CreateDirectory(directoryName);
        var rules = SearchIgnoreRules.Load(temp.Path);

        Assert.True(rules.ShouldSkipDirectory(directory));
    }

    [Fact]
    public void IncludeFilter_ExcludesEverythingElse()
    {
        using var temp = new TempDirectory();
        var rules = SearchIgnoreRules.Load(temp.Path, includeFilter: "*.cs");

        Assert.False(rules.ShouldSkipFile(temp.Combine("main.cs")));
        Assert.True(rules.ShouldSkipFile(temp.Combine("notes.txt")));
    }

    [Fact]
    public void ExcludeFilter_SkipsMatchingFiles()
    {
        using var temp = new TempDirectory();
        var rules = SearchIgnoreRules.Load(temp.Path, excludeFilter: "*.tmp,secrets.txt");

        Assert.True(rules.ShouldSkipFile(temp.Combine("scratch.tmp")));
        Assert.True(rules.ShouldSkipFile(temp.Combine("secrets.txt")));
        Assert.False(rules.ShouldSkipFile(temp.Combine("main.cs")));
    }

    [Fact]
    public void IncludeAndExcludeFilters_WorkTogether()
    {
        using var temp = new TempDirectory();
        var rules = SearchIgnoreRules.Load(temp.Path, includeFilter: "*.cs", excludeFilter: "*.g.cs");

        Assert.False(rules.ShouldSkipFile(temp.Combine("main.cs")));
        Assert.True(rules.ShouldSkipFile(temp.Combine("generated.g.cs")));
        Assert.True(rules.ShouldSkipFile(temp.Combine("readme.md")));
    }

    [Fact]
    public void MultiPatternIncludeFilter_AcceptsEachPattern()
    {
        using var temp = new TempDirectory();
        var rules = SearchIgnoreRules.Load(temp.Path, includeFilter: "*.cs,*.axaml");

        Assert.False(rules.ShouldSkipFile(temp.Combine("Main.cs")));
        Assert.False(rules.ShouldSkipFile(temp.Combine("App.axaml")));
        Assert.True(rules.ShouldSkipFile(temp.Combine("readme.md")));
    }

    [Fact]
    public void ParentGitIgnore_AppliesToNestedRoot()
    {
        using var temp = new TempDirectory();
        temp.Write(".gitignore", "*.log\n");
        var nested = temp.CreateDirectory("src/app");
        var rules = SearchIgnoreRules.Load(nested);

        Assert.True(rules.ShouldSkipFile(Path.Combine(nested, "run.log")));
    }

    [Theory]
    [InlineData("*.cs", "Main.cs", true)]
    [InlineData("*.cs", "Main.cs.bak", false)]
    [InlineData("main.?s", "main.cs", true)]
    [InlineData("exact.txt", "exact.txt", true)]
    [InlineData("*", "anything", true)]
    public void MatchesGlob_HandlesWildcards(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, SearchIgnoreRules.MatchesGlob(pattern, value));
    }
}

public sealed class TerminalShellSupportTests
{
    [Fact]
    public void ClearCommand_UsesShellConventions()
    {
        Assert.Equal("clear\r", TerminalShellSupport.GetClearCommandForShell("bash"));
        Assert.Equal("clear\r", TerminalShellSupport.GetClearCommandForShell("zsh"));
        Assert.Equal("clear\r", TerminalShellSupport.GetClearCommandForShell("fish"));

        var expected = OperatingSystem.IsWindows() ? "cls\r" : "clear\r";
        Assert.Equal(expected, TerminalShellSupport.GetClearCommandForShell("powershell"));
    }

    [Theory]
    [InlineData(-100, 120)]
    [InlineData(0, 120)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    [InlineData(420, 420)]
    [InlineData(10_000, 420)]
    public void NormalizeTerminalPanelHeight_Clamps(double value, double expected)
    {
        Assert.Equal(expected, TerminalShellSupport.NormalizeTerminalPanelHeight(value));
    }

    [Fact]
    public void NormalizeTerminalPanelHeight_FallsBackForNonFiniteValues()
    {
        Assert.Equal(AppSettings.DefaultTerminalPanelHeight, TerminalShellSupport.NormalizeTerminalPanelHeight(double.NaN));
        Assert.Equal(AppSettings.DefaultTerminalPanelHeight, TerminalShellSupport.NormalizeTerminalPanelHeight(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(0, 180)]
    [InlineData(180, 180)]
    [InlineData(300, 300)]
    [InlineData(800, 800)]
    [InlineData(5000, 800)]
    public void NormalizeTerminalSidebarWidth_Clamps(double value, double expected)
    {
        Assert.Equal(expected, TerminalShellSupport.NormalizeTerminalSidebarWidth(value));
    }

    [Fact]
    public void NormalizeTerminalSidebarWidth_FallsBackForNonFiniteValues()
    {
        Assert.Equal(AppSettings.DefaultTerminalSidebarWidth, TerminalShellSupport.NormalizeTerminalSidebarWidth(double.NaN));
    }

    [Theory]
    [InlineData("Kodo - Terminal")]
    [InlineData("my-project")]
    [InlineData("PowerShell")]
    public void ExtractDisplayTitle_KeepsNonPathTitles(string title)
    {
        Assert.Equal(title, TerminalShellSupport.ExtractDisplayTitleFromShellTitle(title));
    }

    [Fact]
    public void ExtractDisplayTitle_UsesParentFolderForFilePaths()
    {
        Assert.Equal("repo", TerminalShellSupport.ExtractDisplayTitleFromShellTitle(@"C:\Users\me\repo\program.cs"));
    }

    [Fact]
    public void ExtractDisplayTitle_UsesLastSegmentForDirectoryPaths()
    {
        Assert.Equal("repo", TerminalShellSupport.ExtractDisplayTitleFromShellTitle(@"C:\Users\me\repo"));
        Assert.Equal("repo", TerminalShellSupport.ExtractDisplayTitleFromShellTitle(@"/home/me/repo/"));
    }

    [Fact]
    public void DetectTerminalShells_ReturnsUsableShells()
    {
        var shells = TerminalShellSupport.DetectTerminalShells().ToList();

        Assert.NotEmpty(shells);
        Assert.All(shells, shell =>
        {
            Assert.False(string.IsNullOrWhiteSpace(shell.Id));
            Assert.False(string.IsNullOrWhiteSpace(shell.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(shell.FileName));
            Assert.NotNull(shell.Arguments);
        });
        Assert.Equal(shells.Count, shells.Select(shell => shell.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DetectTerminalShells_DoesNotOverridePredictionSource()
    {
        foreach (var shell in TerminalShellSupport.DetectTerminalShells())
        {
            Assert.DoesNotContain("PSReadLine", shell.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PredictionSource", shell.Arguments, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DetectTerminalShells_UsesPlatformDefault()
    {
        var ids = TerminalShellSupport.DetectTerminalShells().Select(shell => shell.Id).ToList();
        var defaultId = TerminalShellSupport.GetDefaultShellId();

        Assert.Contains(defaultId, ids);
        if (OperatingSystem.IsWindows())
            Assert.Contains("cmd", ids);
    }
}
