// Licensed under GPL-v3.0
namespace Kodo.Models;

public class ExtensionThemeDefinition
{
    public string ThemeId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string BaseTheme { get; init; } = "Dark";
    public string WindowBackground { get; init; } = "#000000";
    public string TopBar { get; init; } = "#0E0E0E";
    public string Sidebar { get; init; } = "#0E0E0E";
    public string Button { get; init; } = "#242424";
    public string ButtonHover { get; init; } = "#343434";
    public string EditorBackground { get; init; } = "#000000";
    public string Card { get; init; } = "#121212";
    public string PrimaryText { get; init; } = "#FFFFFF";
    public string MutedText { get; init; } = "#BDBDBD";
    public string SurfaceBorder { get; init; } = "#4A4A4A";
    public string Accent { get; init; } = "#8C00FF";
    public string PreviewBackground { get; init; } = "#000000";
    public string PreviewBorder { get; init; } = "#4A4A4A";
}