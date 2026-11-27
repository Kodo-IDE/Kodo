// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Kodo;
using Kodo.Models;
using Xunit;

namespace Kodo.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchDocumentedBehaviour()
    {
        var settings = new AppSettings();

        Assert.Equal("Dark", settings.ThemeName);
        Assert.False(settings.Fluent2ThemeEnabled);
        Assert.False(settings.AutoSaveEnabled);
        Assert.True(settings.ScreenReaderAnnouncementsEnabled);
        Assert.True(settings.StatusBarFilePathVisible);
        Assert.True(settings.MinimapVisible);
        Assert.True(settings.QuickActionsEnabled);
        Assert.True(settings.QuickActionsLearningEnabled);
        Assert.True(settings.PaletteLearningEnabled);
        Assert.True(settings.InsightLearningEnabled);
        Assert.True(settings.StickyScrollEnabled);
        Assert.Equal(5, settings.StickyScrollMaxLines);
        Assert.True(settings.InsightEnabled);
        Assert.Equal(".txt,.md", settings.InsightBlacklistExtensions);
        Assert.Equal(4, settings.TabSize);
        Assert.True(settings.InsertSpaces);
        Assert.True(settings.AutoDetectIndentationEnabled);
        Assert.Equal(14, settings.EditorFontSize);
        Assert.True(settings.ConfirmBeforeClosingUnsavedTabsEnabled);
        Assert.Equal(AppSettings.DefaultTerminalPanelHeight, settings.TerminalPanelHeight);
        Assert.Equal(AppSettings.DefaultTerminalSidebarWidth, settings.TerminalSidebarWidth);
    }

    [Fact]
    public void CodePredictAlias_TracksInsightEnabledButIsNotSerialized()
    {
        var settings = new AppSettings { CodePredictEnabled = false };
        Assert.False(settings.InsightEnabled);

        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("CodePredictEnabled", json, StringComparison.Ordinal);
        Assert.Contains("InsightEnabled", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializationRoundTrip_PreservesCustomValues()
    {
        var settings = new AppSettings
        {
            ThemeName = "Light",
            Fluent2ThemeEnabled = true,
            TabSize = 2,
            EditorFontSize = 18,
            EditorFontFamily = "JetBrains Mono",
            StickyScrollMaxLines = 3,
            PreferredTerminalShellId = "bash",
            TerminalScrollbackLines = 20000,
            LastOpenedFolderPath = @"C:\work",
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("Light", restored.ThemeName);
        Assert.True(restored.Fluent2ThemeEnabled);
        Assert.Equal(2, restored.TabSize);
        Assert.Equal(18, restored.EditorFontSize);
        Assert.Equal("JetBrains Mono", restored.EditorFontFamily);
        Assert.Equal(3, restored.StickyScrollMaxLines);
        Assert.Equal("bash", restored.PreferredTerminalShellId);
        Assert.Equal(20000, restored.TerminalScrollbackLines);
        Assert.Equal(@"C:\work", restored.LastOpenedFolderPath);
    }

    [Fact]
    public void LegacyUnknownKeys_AreIgnored()
    {
        const string json = "{\"ThemeName\":\"Dark\",\"PSReadLinePredictionEnabled\":true}";

        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal("Dark", restored!.ThemeName);
    }
}

public sealed class WelcomeMessageBuilderTests
{
    [Fact]
    public void BuildMessages_ReturnsNonEmptyUniqueMessages()
    {
        var messages = WelcomeMessageBuilder.BuildMessages(string.Empty, "US", 0, string.Empty, false, 0);

        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
    }

    [Fact]
    public void BuildMessages_HonoursNameAndBirthday()
    {
        var messages = WelcomeMessageBuilder.BuildMessages("Ada", "GB", 1, "0", true, 2);

        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.DoesNotContain("  ", message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("+5:30")]
    [InlineData("5:30:15")]
    [InlineData("not-a-number")]
    [InlineData("99")]
    public void BuildMessages_ToleratesAnyTimezoneInput(string timezoneOffset)
    {
        var messages = WelcomeMessageBuilder.BuildMessages("Sam", "AU", 2, timezoneOffset, false, 0);

        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
    }

    [Fact]
    public void TaglineParts_RecomposeTheGreeting()
    {
        Assert.Equal(WelcomeMessageBuilder.TaglineGreeting, WelcomeMessageBuilder.TaglinePrefix + WelcomeMessageBuilder.TaglineAccentSuffix);
        Assert.EndsWith("light.", WelcomeMessageBuilder.TaglineAccentSuffix, StringComparison.Ordinal);
    }
}

public sealed class SmartFileIconsTests
{
    private static readonly IReadOnlyDictionary<string, string> ExtensionIcons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [".zzz"] = "ZZ" };

    [Fact]
    public void ExtensionMap_IsUsedForKnownExtensions()
    {
        var icon = SmartFileIcons.ResolveIcon("sample.zzz", @"C:\work\sample.zzz", false, @"C:\work", ExtensionIcons);
        Assert.Equal("ZZ", icon);
    }

    [Fact]
    public void Directories_ResolveToAnIcon()
    {
        var icon = SmartFileIcons.ResolveIcon("src", @"C:\work\src", true, @"C:\work", ExtensionIcons);
        Assert.False(string.IsNullOrWhiteSpace(icon));
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("README.md")]
    [InlineData("unknown.qqq")]
    public void Files_AlwaysResolveToAnIcon(string name)
    {
        var icon = SmartFileIcons.ResolveIcon(name, Path.Combine(@"C:\work", name), false, @"C:\work", ExtensionIcons);
        Assert.False(string.IsNullOrWhiteSpace(icon));
    }

    [Fact]
    public void ExtensionLookup_IsCaseInsensitive()
    {
        Assert.Equal("ZZ", SmartFileIcons.ResolveIcon("SAMPLE.ZZZ", @"C:\work\SAMPLE.ZZZ", false, @"C:\work", ExtensionIcons));
    }

    [Fact]
    public void ExtensionIcons_CanBeBuiltFromExtensions()
    {
        var icons = SmartFileIcons.BuildExtensionIcons([]);
        Assert.NotNull(icons);
        Assert.True(icons.Count >= 0);
    }
}
