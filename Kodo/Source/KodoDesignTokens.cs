// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Media;

namespace Kodo;

/// <summary>Shared corner radii for Kodo controls and surfaces.</summary>
public static class KodoDesignTokens
{
    public static CornerRadius CompactRadius { get; } = new(6);
    public static CornerRadius ControlRadius { get; } = new(8);
    public static CornerRadius CardRadius { get; } = new(12);
    public static CornerRadius HeroRadius { get; } = new(16);
    public static CornerRadius PillRadius { get; } = new(999);

    public static Color DangerColor { get; } = Color.Parse("#E5484D");
    public static Color WarningColor { get; } = Color.Parse("#FFA040");
    public static Color WarningSurfaceColor { get; } = Color.Parse("#2D1F00");
    public static Color WarningBorderColor { get; } = Color.Parse("#6B4800");
}
