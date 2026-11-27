// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using Kodo.QuickActions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Kodo.Tests;

public sealed class QuickActionDiscoveryTests
{
    private static QuickActionCommandRegistration Command(
        string id,
        string category = "Editor",
        string title = "Command",
        Func<bool>? isAvailable = null,
        Func<Task>? execute = null) =>
        new(id, title, category, string.Empty, isAvailable, execute ?? (() => Task.CompletedTask));

    private static QuickActionDiscoveryContext Context(
        string fileType = ".cs",
        bool hasDocument = true,
        bool isFolderOpen = true,
        bool isTerminalSupported = true) =>
        new(fileType, hasDocument, isFolderOpen, isTerminalSupported);

    [Fact]
    public void RegisteredCommand_OutsideCuratedList_BecomesCandidate()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.new-terminal", "Terminal", "New Terminal")], Context());

        var candidate = Assert.Single(discovered);
        Assert.Equal("cmd.new-terminal", candidate.Item.Id);
        Assert.Equal("New Terminal", candidate.Item.Title);
        Assert.Equal("command|terminal|.cs", candidate.Item.ContextKey);
        Assert.Equal(QuickActionDiscovery.MaximumCommandRelevance, candidate.Item.BaseRelevance);
    }

    [Fact]
    public void DiscoveredRelevance_StaysBelowContextualActions()
    {
        string[] categories = ["Terminal", "Run", "Build", "Editor", "Search", "Navigation", "Git", "File", "View", "Appearance", "Preferences", "Help", "Unknown"];
        var commands = categories.Select((category, index) => Command($"cmd.c{index}", category)).ToArray();

        var discovered = QuickActionDiscovery.Discover(commands, Context());

        Assert.Equal(categories.Length, discovered.Count);
        Assert.All(discovered, candidate => Assert.InRange(candidate.Item.BaseRelevance, 0, QuickActionDiscovery.MaximumCommandRelevance));
        Assert.All(discovered, candidate => Assert.True(candidate.Item.BaseRelevance < 63));
    }

    [Fact]
    public void UnavailableCommands_AreNotSurfaced()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.document-only", isAvailable: () => false), Command("cmd.always")], Context());

        Assert.Equal("cmd.always", Assert.Single(discovered).Item.Id);
    }

    [Fact]
    public void CoveredCommands_AreNotSurfacedTwice()
    {
        var covered = new HashSet<string>(StringComparer.Ordinal) { "cmd.open-workspace-in-terminal" };

        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.open-workspace-in-terminal", "Terminal"), Command("cmd.toggle-terminal", "Terminal")],
            Context(),
            covered);

        Assert.Equal("cmd.toggle-terminal", Assert.Single(discovered).Item.Id);
    }

    [Theory]
    [InlineData("cmd.encoding-utf-8")]
    [InlineData("cmd.line-ending-lf")]
    [InlineData("cmd.indentation-spaces-4")]
    [InlineData("cmd.change-file-encoding")]
    public void MaintenanceCommands_AreNotSurfaced(string id)
    {
        var discovered = QuickActionDiscovery.Discover([Command(id, "File")], Context());

        Assert.Empty(discovered);
    }

    [Fact]
    public void DuplicateRegistrations_ProduceOneCandidate()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.new-terminal", "Terminal"), Command("cmd.new-terminal", "Terminal")], Context());

        Assert.Single(discovered);
    }

    [Fact]
    public void DuplicateRegistration_PrefersTheFirstAvailableCommand()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.new-terminal", isAvailable: () => false), Command("cmd.new-terminal", isAvailable: () => true)], Context());

        Assert.Single(discovered);
    }

    [Fact]
    public void CommandsWithoutStableIdentity_AreSkipped()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command(string.Empty), Command("   "), Command("cmd.no-title", title: "  "), Command("cmd.valid")], Context());

        Assert.Equal("cmd.valid", Assert.Single(discovered).Item.Id);
    }

    [Fact]
    public void OverlongIdentifier_IsSkipped()
    {
        var discovered = QuickActionDiscovery.Discover([Command("cmd." + new string('a', 200))], Context());

        Assert.Empty(discovered);
    }

    [Fact]
    public void ThrowingAvailabilityCheck_IsReportedAndSkipped()
    {
        var error = new InvalidOperationException("broken plugin predicate");
        Exception? reported = null;

        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.broken", isAvailable: () => throw error), Command("cmd.working")],
            Context(),
            onAvailabilityError: exception => reported = exception);

        Assert.Same(error, reported);
        Assert.Equal("cmd.working", Assert.Single(discovered).Item.Id);
    }

    [Fact]
    public void Discovery_DoesNotExecuteCommands()
    {
        var executions = 0;

        QuickActionDiscovery.Discover(
            [Command("cmd.new-terminal", execute: () => { executions++; return Task.CompletedTask; })], Context());

        Assert.Equal(0, executions);
    }

    [Fact]
    public void RegistrationOrder_IsPreserved()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.a", "Terminal"), Command("cmd.b", "Editor"), Command("cmd.c", "Help")], Context());

        Assert.Equal(new[] { "cmd.a", "cmd.b", "cmd.c" }, discovered.Select(candidate => candidate.Item.Id));
    }

    [Fact]
    public void ContextKeys_SeparateCommandFamiliesAndFileTypes()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.terminal", "Terminal"), Command("cmd.format", "Editor")], Context(".py"));

        Assert.Equal("command|terminal|.py", discovered[0].Item.ContextKey);
        Assert.Equal("command|editor|.py", discovered[1].Item.ContextKey);
        Assert.NotEqual(discovered[0].Item.ContextKey, discovered[1].Item.ContextKey);
    }

    [Fact]
    public void MissingDocument_UsesUnknownFileTypeAndRecordsContextFlags()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("cmd.new-terminal", "Terminal")], Context(string.Empty, hasDocument: false, isFolderOpen: false, isTerminalSupported: false));

        var candidate = Assert.Single(discovered);
        Assert.Equal("command|terminal|unknown", candidate.Item.ContextKey);
        Assert.Contains("project:False", candidate.Item.LearningFeatures);
        Assert.Contains("terminal:False", candidate.Item.LearningFeatures);
        Assert.Contains("document:False", candidate.Item.LearningFeatures);
    }

    [Fact]
    public void PluginCategory_GroupsUnderSingleContext()
    {
        var discovered = QuickActionDiscovery.Discover(
            [Command("plugin.command", "Plugin · Formatters")], Context());

        Assert.Equal("command|plugin|.cs", Assert.Single(discovered).Item.ContextKey);
    }

    [Fact]
    public void EmptyRegistrationSet_ReturnsNoCandidates()
    {
        Assert.Empty(QuickActionDiscovery.Discover([], Context()));
    }

    [Fact]
    public void RemovedRegistration_StopsProducingCandidates()
    {
        var commands = new List<QuickActionCommandRegistration> { Command("cmd.a"), Command("cmd.b") };
        Assert.Equal(2, QuickActionDiscovery.Discover(commands, Context()).Count);

        commands.RemoveAt(1);

        Assert.Equal("cmd.a", Assert.Single(QuickActionDiscovery.Discover(commands, Context())).Item.Id);
    }

    [Fact]
    public void DiscoveredCandidates_CarryLearningFeaturesForTheirFamily()
    {
        var discovered = QuickActionDiscovery.Discover([Command("cmd.new-terminal", "Terminal")], Context());

        var features = Assert.Single(discovered).Item.LearningFeatures;
        Assert.Contains("command-category:terminal", features);
        Assert.Contains("file:.cs", features);
        Assert.DoesNotContain("category:command", features);
    }
}
