// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Media;

namespace Kodo;

public static class KodoDesignTokens
{
    public static CornerRadius CompactRadius { get; } = new(6);
    public static CornerRadius ControlRadius { get; } = new(8);
    public static CornerRadius CardRadius { get; } = new(12);
    public static CornerRadius HeroRadius { get; } = new(16);
    public static CornerRadius PillRadius { get; } = new(999);
    public static CornerRadius AccentPillRadius { get; } = new(2);
    public static CornerRadius HoverRowRadius { get; } = new(3);
    public static CornerRadius HairlineBorderRadius { get; } = new(5);
    public static CornerRadius SmallRadius { get; } = new(1);
    public static CornerRadius TabUnderlineRadius { get; } = new(1.5, 1.5, 0, 0);
    public static CornerRadius SidebarLeftRadius { get; } = new(12, 0, 0, 12);
    public static CornerRadius TerminalTopRadius { get; } = new(12, 0, 0, 0);
    public static CornerRadius SidebarRightRadius { get; } = new(0, 12, 12, 0);

    public static Thickness BorderThin { get; } = new(1);
    public static Thickness BorderThick { get; } = new(2);
    public static Thickness BorderRightHairline { get; } = new(0, 0, 1, 0);
    public static Thickness BorderBottomHairline { get; } = new(0, 0, 0, 1);
    public static Thickness BorderTopHairline { get; } = new(0, 1, 0, 0);
    public static Thickness BorderLeftHairline { get; } = new(1, 0, 0, 0);
    public const double SplitterWidth = 4.0;
    public static Thickness HorizontalPadding { get; } = new(12, 0);
    public static Thickness LeanPadding { get; } = new(8, 0);
    public static Thickness MediumPadding { get; } = new(10, 6);
    public static Thickness AveragePadding { get; } = new(14, 7);
    public static Thickness ButtonPadding { get; } = new(14, 8);
    public static Thickness TightPadding { get; } = new(8, 4);
    public static Thickness StrongPadding { get; } = new(16, 8);
    public static Thickness DialogPadding { get; } = new(20);
    public static Thickness CardPadding { get; } = new(8);
    public static Thickness RichPadding { get; } = new(10);

    public const double BorderStrokeThin = 1.0;
    public const double BorderStrokeHairline = 0.75;
    public const double BorderStrokeThick = 1.5;
    public const double AccentPillWidth = 3.0;
    public const double DividerHeight = 1.0;

    public const double DefaultControlHeight = 30.0;
    public const double SmallControlHeight = 28.0;
    public const double MiniControlHeight = 24.0;
    public const double LargeControlHeight = 36.0;
    public const double ExtraLargeControlHeight = 44.0;
    public const double HeavyControlHeight = 48.0;

    public const double MicroFontSize = 9.0;
    public const double CaptionFontSize = 10.0;
    public const double SmallFontSize = 11.0;
    public const double BodyFontSize = 12.0;
    public const double BodyLargeFontSize = 14.0;
    public const double TitleFontSize = 15.0;
    public const double SectionFontSize = 16.0;
    public const double HeaderFontSize = 18.0;
    public const double SubheaderFontSize = 20.0;
    public const double HeroFontSize = 22.0;
    public const double HeroLargeFontSize = 28.0;
    public const double DisplayFontSize = 30.0;
    public const double DisplayLargeFontSize = 32.0;
    public const double DisplayHugeFontSize = 48.0;

    public const double StrongOpacity = 0.90;
    public const double SecondaryOpacity = 0.85;
    public const double SubduedSecondaryOpacity = 0.75;
    public const double MutedForegroundOpacity = 0.60;
    public const double SelectedOpacity = 0.55;
    public const double FaintOpacity = 0.35;
    public const double AccentPillOpacity = 0.18;
    public const double ActivityButtonHeight = 44.0;
    public const double ActivityBarWidth = 60.0;
    public const double RightRailWidth = 44.0;
    public const double SidebarWidth = 220.0;
    public const double TerminalSidebarWidth = 220.0;
    public const double StatusBarHeight = 30.0;
    public const double StatusFontSize = 11.5;
    public const double GlyphCanvasSize = 24.0;
    public const double IconWellSize = 40.0;
    public const double IconWellMediumSize = 42.0;
    public const double IconWellLargeSize = 52.0;
    public const double EmptyStateIconSize = 32.0;
    public const double ToolBarHeight = 40.0;
    public const double TitleBarHeight = 32.0;
    public const double ActivityIconSize = 18.0;
    public const double ActivityIconLargeSize = 20.0;
    public const double ToolbarIconSize = 16.0;
    public const double IconSize = 22.0;
    public const double HeaderBarHeight = 26.0;
    public const double CodeAreaIndent = 12.0;
    public const double SectionSpacing = 16.0;
    public const double GroupSpacing = 10.0;
    public const double KnotSpacing = 6.0;
    public const double IconSpacing = 6.0;
    public const double RowSpacing = 8.0;
    public const double PanePadding = 8.0;
    public const double DialogPadConst = 20.0;
    public const double CardPadConst = 16.0;
    public const double EditorPadConst = 16.0;
    public const double TerminalPadConst = 8.0;

    public const double DisabledOpacity = 0.45;
    public const double MutedOpacity = 0.60;
    public const double SecondaryTextOpacity = 0.75;
    public const double SubduedOpacity = 0.85;
    public const double DisabledBorderOpacity = 0.40;
    public const double OverlayOpacity = 0.30;
    public const double SelectedStripeOpacity = 0.30;
    public const double IndentOpacity = 0.40;
    public const double DeadCodeOpacity = 0.22;
    public const double DeadCodeTextOpacity = 0.30;
    public const double HoverHoverOpacity = 0.90;
    public const double DividerOpacity = 0.70;
    public const double StrongGuideOpacity = 0.40;
    public const double StripeOpacity = 0.18;

    public const double ToolbarButtonWidth = 80.0;
    public const double ToggleTapIndent = 0.0;
    public const double DialogMinWidth = 340.0;
    public const double DialogMaxHeight = 560.0;
    public const double DialogDefaultWidth = 380.0;
    public const double UpdateDialogDefaultWidth = 780.0;
    public const double LargeToolbarButtonWidth = 152.0;

    public const string DefaultAccentHex = "#8C00FF";
    public const string LinkHex = "#5BA3D9";
    public const string SelectionTintHex = "#8C00FF";
    public const string ErrorUnderlineHex = "#FF5C67";
    public const string InfoUnderlineHex = "#5BA7FF";
    public const string WarningUnderlineHex = "#F2C94C";
    public const string HintUnderlineHex = "#D4D8E0";

    public static Color AccentColor { get; } = Color.Parse(DefaultAccentHex);
    public static Color DangerColor { get; } = Color.Parse("#E5484D");
    public static Color WarningColor { get; } = Color.Parse("#FFA040");
    public static Color SuccessColor { get; } = Color.Parse("#2EA043");
    public static Color InfoColor { get; } = Color.Parse("#3794FF");
    public static Color HintColor { get; } = Color.Parse("#8A8A8A");
    public static Color AccentSelectionColor { get; } = Color.Parse("#8C00FF");
    public static Color HighlightSelectionColor { get; } = Color.Parse("#9CDCFE");
    public static Color BracketYellowColor { get; } = Color.Parse("#FFD700");
    public static Color BracketTypeColor { get; } = Color.Parse("#DA70D6");
    public static Color BracketControlColor { get; } = Color.Parse("#4FC1FF");
    public static Color BracketKeywordColor { get; } = Color.Parse("#C586C0");
    public static Color BracketStringColor { get; } = Color.Parse("#CE9178");
    public static Color BracketCommentColor { get; } = Color.Parse("#6A9955");

    public static Color WarningSurfaceColor { get; } = Color.FromArgb(0x26, WarningColor.R, WarningColor.G, WarningColor.B);
    public static Color WarningBorderColor { get; } = Color.FromArgb(0x59, WarningColor.R, WarningColor.G, WarningColor.B);
    public static Color DangerSurfaceColor { get; } = Color.FromArgb(0x26, DangerColor.R, DangerColor.G, DangerColor.B);
    public static Color DangerBorderColor { get; } = Color.FromArgb(0x59, DangerColor.R, DangerColor.G, DangerColor.B);
    public static Color SuccessSurfaceColor { get; } = Color.FromArgb(0x26, SuccessColor.R, SuccessColor.G, SuccessColor.B);
    public static Color SuccessBorderColor { get; } = Color.FromArgb(0x59, SuccessColor.R, SuccessColor.G, SuccessColor.B);
    public static Color InfoSurfaceColor { get; } = Color.FromArgb(0x26, InfoColor.R, InfoColor.G, InfoColor.B);
    public static Color InfoBorderColor { get; } = Color.FromArgb(0x59, InfoColor.R, InfoColor.G, InfoColor.B);
    public static Color HintSurfaceColor { get; } = Color.FromArgb(0x26, HintColor.R, HintColor.G, HintColor.B);
    public static Color HintBorderColor { get; } = Color.FromArgb(0x59, HintColor.R, HintColor.G, HintColor.B);

    public static IBrush DangerBrush { get; } = new SolidColorBrush(DangerColor);
    public static IBrush WarningBrush { get; } = new SolidColorBrush(WarningColor);
    public static IBrush SuccessBrush { get; } = new SolidColorBrush(SuccessColor);
    public static IBrush InfoBrush { get; } = new SolidColorBrush(InfoColor);
    public static IBrush HintBrush { get; } = new SolidColorBrush(HintColor);
    public static IBrush AccentBrush { get; } = new SolidColorBrush(AccentColor);
    public static IBrush BracketYellowBrush { get; } = new SolidColorBrush(BracketYellowColor);
    public static IBrush BracketTypeBrush { get; } = new SolidColorBrush(BracketTypeColor);
    public static IBrush BracketControlBrush { get; } = new SolidColorBrush(BracketControlColor);
    public static IBrush BracketKeywordBrush { get; } = new SolidColorBrush(BracketKeywordColor);
    public static IBrush BracketStringBrush { get; } = new SolidColorBrush(BracketStringColor);
    public static IBrush BracketCommentBrush { get; } = new SolidColorBrush(BracketCommentColor);

    public static IBrush DangerFillBrush { get; } = new SolidColorBrush(DangerSurfaceColor);
    public static IBrush WarningFillBrush { get; } = new SolidColorBrush(WarningSurfaceColor);
    public static IBrush SuccessFillBrush { get; } = new SolidColorBrush(SuccessSurfaceColor);
    public static IBrush InfoFillBrush { get; } = new SolidColorBrush(InfoSurfaceColor);
    public static IBrush HintFillBrush { get; } = new SolidColorBrush(HintSurfaceColor);

    public static IBrush DangerStrokeBrush { get; } = new SolidColorBrush(DangerBorderColor);
    public static IBrush WarningStrokeBrush { get; } = new SolidColorBrush(WarningBorderColor);
    public static IBrush SuccessStrokeBrush { get; } = new SolidColorBrush(SuccessBorderColor);
    public static IBrush InfoStrokeBrush { get; } = new SolidColorBrush(InfoBorderColor);
    public static IBrush HintStrokeBrush { get; } = new SolidColorBrush(HintBorderColor);

    public static IBrush AccentSelectionBrush { get; } = new SolidColorBrush(AccentSelectionColor);
    public static IBrush HighlightSelectionBrush { get; } = new SolidColorBrush(HighlightSelectionColor);
    public static IBrush WarningHighlightBrush { get; } = new SolidColorBrush(Color.FromArgb(0x2E, 0xCC, 0xA7, 0x00));
    public static IBrush InfoHighlightBrush { get; } = new SolidColorBrush(Color.FromArgb(0x24, 0x37, 0x94, 0xFF));
    public static IBrush HighlightCommitBrush { get; } = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    public static IBrush LinkBrush { get; } = new SolidColorBrush(Color.Parse(LinkHex));
    public static IBrush LinkActiveBrush { get; } = new SolidColorBrush(Color.Parse("#C8E6FF"));
    public static IBrush HintUnderlineBrush { get; } = new SolidColorBrush(Color.Parse(HintUnderlineHex));
    public static IBrush InfoUnderlineBrush { get; } = new SolidColorBrush(Color.Parse(InfoUnderlineHex));
    public static IBrush WarningUnderlineBrush { get; } = new SolidColorBrush(Color.Parse(WarningUnderlineHex));
    public static IBrush ErrorUnderlineBrush { get; } = new SolidColorBrush(Color.Parse(ErrorUnderlineHex));

    public static IBrush MutedSurfaceBrush { get; } = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
    public static IBrush DeadCodeSurfaceBrush { get; } = new SolidColorBrush(Color.FromArgb(0x24, 0xA0, 0xA0, 0xA0));
    public static IBrush DeadCodeSurfaceLightBrush { get; } = new SolidColorBrush(Color.FromArgb(0x30, 0x6B, 0x6B, 0x6B));
    public static Color NeutralGrey { get; } = Color.Parse("#9AA0A6");
    public static Color NeutralGreyLight { get; } = Color.Parse("#5F6B7A");
    public static Color SlateGrey { get; } = Color.Parse("#8A8A8A");

    public static Color DarkTextColor { get; } = Color.Parse("#202124");
    public static Color LightTextColor { get; } = Color.Parse("#F4F4F4");

    public const double StripOpacity = 0.22;
    public static IBrush NeutralDarkGreyBrush { get; } = new SolidColorBrush(Color.Parse("#9AA0A6"), 0.22);
    public static IBrush NeutralDarkGreyLightBrush { get; } = new SolidColorBrush(Color.Parse("#5F6B7A"), 0.30);
    public static IBrush NeutralLightGreyBrush { get; } = new SolidColorBrush(Color.Parse("#D4D4D4"));
    public static IBrush GreyTextBrush { get; } = new SolidColorBrush(KodoDesignTokens.LightTextColor);
    public static IBrush LightTextBrush { get; } = new SolidColorBrush(KodoDesignTokens.LightTextColor);
    public static IBrush DarkTextBrush { get; } = new SolidColorBrush(KodoDesignTokens.DarkTextColor);
    public static IBrush DarkMutedTextBrush { get; } = new SolidColorBrush(KodoDesignTokens.NeutralGreyLight);
    public static IBrush LightMutedTextBrush { get; } = new SolidColorBrush(Color.Parse("#A0A0A0"));
    public static IBrush SlateGreyBrush { get; } = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public static IBrush DeadCodeStrikeBrush { get; } = new SolidColorBrush(Color.Parse("#000000"));

    public static IBrush ChangedAddedBrush { get; } = new SolidColorBrush(Color.Parse("#2EA043"), 0.18);
    public static IBrush ChangedRemovedBrush { get; } = new SolidColorBrush(Color.Parse("#F85149"), 0.18);
    public static IBrush ChangedHunkBrush { get; } = new SolidColorBrush(Color.Parse("#58A6FF"));
    public static IBrush ChangedMetaBrush { get; } = new SolidColorBrush(Color.Parse("#8B949E"));

    public static Color TokenBlue { get; } = Color.Parse("#569CD6");
    public static Color TokenTeal { get; } = Color.Parse("#4EC9B0");
    public static Color TokenYellow { get; } = Color.Parse("#DCDCAA");
    public static Color TokenPurple { get; } = Color.Parse("#C586C0");
    public static Color TokenOrange { get; } = Color.Parse("#CE9178");
    public static Color TokenCyan { get; } = Color.Parse("#9CDCFE");
    public static Color TokenGreen { get; } = Color.Parse("#B5CEA8");
    public static Color TokenGold { get; } = Color.Parse("#D7BA7D");

    public static IBrush TokenBlueBrush { get; } = new SolidColorBrush(TokenCyan);
    public static IBrush TokenOrangeBrush { get; } = new SolidColorBrush(TokenOrange);
    public static IBrush TokenSalmonBrush { get; } = new SolidColorBrush(TokenYellow);
    public static IBrush TokenPurpleBrush { get; } = new SolidColorBrush(TokenPurple);
    public static IBrush TokenTealBrush { get; } = new SolidColorBrush(TokenBlue);
    public static IBrush TokenGreenBrush { get; } = new SolidColorBrush(TokenTeal);
    public static IBrush TokenPinkBrush { get; } = new SolidColorBrush(TokenOrange);
    public static IBrush TokenCyanBrush { get; } = new SolidColorBrush(TokenCyan);
    public static IBrush TokenGoldBrush { get; } = new SolidColorBrush(TokenGold);

    public const string WindowBackground = "#1E1E1E";
    public const string TopBar = "#181818";
    public const string Sidebar = "#181818";
    public const string Button = "#252526";
    public const string ButtonHover = "#313437";
    public const string EditorBackground = "#1E1E1E";
    public const string Card = "#252526";
    public const string PrimaryText = "#F4F4F4";
    public const string MutedText = "#A0A0A0";
    public const string SurfaceBorder = "#2B2B2B";
    public const string Accent = "#8C00FF";
    public const string AccentDark = "#1A0545";
    public const string AccentLight = "#F0C4FF";
    public const string LightWindowBackground = "#F3F3F3";
    public const string LightTopBar = "#FFFFFF";
    public const string LightSidebar = "#EFF2F7";
    public const string LightButton = "#E3E8F1";
    public const string LightButtonHover = "#D5DDE9";
    public const string LightEditorBackground = "#FFFFFF";
    public const string LightCard = "#F7F9FC";
    public const string LightPrimaryText = "#202124";
    public const string LightMutedText = "#5F6B7A";
    public const string LightSurfaceBorder = "#D7DCE5";
    public const string LightAccent = "#8C00FF";
    public const string DialogSurface = "#1A1A1A";
    public const string DialogBorder = "#3A3A3A";
    public const string DialogBadge = "#2B2B2B";
    public const string DialogTextMuted = "#A0A0A0";
    public const string DialogTokenBlue = "#9CDCFE";
    public const string DialogTokenOrange = "#CE9178";
}
