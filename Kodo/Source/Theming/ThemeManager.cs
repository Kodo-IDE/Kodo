// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Fluent2Palette = Avalonia.Themes.Fluent2.ColorPaletteResources;
using Fluent2Theme = Avalonia.Themes.Fluent2.Fluent2Theme;

namespace Kodo;

internal static class ThemeManager
{
    public static bool IsFluent2Active { get; private set; }

    public static void Apply(bool useFluent2)
    {
        if (Application.Current is not { } app)
            return;

        var styles = app.Styles;
        var themeIndex = 0;

        for (var i = 0; i < styles.Count; i++)
        {
            if (styles[i] is not (FluentTheme or Fluent2Theme))
                continue;

            themeIndex = i;
            styles.RemoveAt(i);
            break;
        }

        styles.Insert(themeIndex, useFluent2 ? CreateFluent2() : CreateFluent());
        IsFluent2Active = useFluent2;
    }

    private static FluentTheme CreateFluent()
    {
        var theme = new FluentTheme();
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#8C00FF") };
        theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.Parse("#7B00FF") };
        return theme;
    }

    private static Fluent2Theme CreateFluent2()
    {
        var theme = new Fluent2Theme();
        theme.Palettes[ThemeVariant.Dark] = new Fluent2Palette { Accent = Color.Parse("#8C00FF") };
        theme.Palettes[ThemeVariant.Light] = new Fluent2Palette { Accent = Color.Parse("#7B00FF") };
        return theme;
    }
}
