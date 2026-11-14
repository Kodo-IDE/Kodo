// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using GitSourceControl;
using Kodo.Editor;
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordAssetsModel = DiscordRPC.Assets;
using DiscordRichPresenceModel = DiscordRPC.RichPresence;
using DiscordRpcClient = DiscordRPC.DiscordRpcClient;

namespace Kodo;

public partial class MainWindow
{
    private enum PaletteMode { Files, Commands, Symbols, Line }

    private sealed record PaletteCommand(
        string Title,
        string Category,
        string ShortcutId,
        Func<Task> Execute,
        Func<bool>? IsAvailable = null,
        string Keywords = "",
        string Detail = "",
        string Section = "",
        string Badge = "",
        bool KeepOpen = false,
        IReadOnlyList<int>? TitleMatches = null,
        IReadOnlyList<int>? DetailMatches = null);

    private sealed record QuickAccessFile(string Path, string FileName, string Relative, string Directory);

    private const int PaletteMaxResults = 80;
    private const int PaletteMaxFuzzyGap = 20;
    private const int PaletteMaxRecentCommands = 5;
    private static readonly bool ShowRareCommandPalettePreviewThisSession = Random.Shared.Next(1_000_000) == 0;
    private static readonly TimeSpan QuickAccessIndexLifetime = TimeSpan.FromSeconds(15);
    private static readonly Regex PaletteFileLineSuffix = new(@"^(?<query>.*?):(?<line>\d+)(?::(?<column>\d+))?$", RegexOptions.CultureInvariant);
    private static readonly Regex PaletteLineSpec = new(@"^(?<line>\d+)(?:[:,\s]+(?<column>\d+))?$", RegexOptions.CultureInvariant);
    private static readonly string[] LspSymbolKindNames =
    [
        "", "File", "Module", "Namespace", "Package", "Class", "Method", "Property", "Field", "Constructor", "Enum",
        "Interface", "Function", "Variable", "Constant", "String", "Number", "Boolean", "Array", "Object", "Key",
        "Null", "Enum Member", "Struct", "Event", "Operator", "Type Parameter",
    ];

    private Grid? _commandPaletteOverlay;
    private Border? _commandPaletteCard;
    private ScrollViewer? _commandPaletteScroll;
    private StackPanel? _commandPaletteList;
    private TextBlock? _commandPaletteStatus;
    private TextBlock? _commandPalettePreviewText;
    private TextBox? _commandPaletteSearchBox;
    private TextBox? _quickAccessSearchBox;
    private bool _commandPaletteUsesOverlaySearchBox;
    private List<PaletteCommand> _commandPaletteCommands = [];
    private List<PaletteCommand> _filteredPaletteCommands = [];
    private readonly List<(Button Row, Border Accent)> _paletteRows = [];
    private readonly List<string> _recentPaletteCommands = [];
    private int _paletteSelectedIndex = -1;
    private int _palettePressedIndex = -1;
    private bool _paletteShortcutIsCommands;
    private bool _suppressQuickAccessTextChanged;
    private bool _isCommandPaletteRarePreviewVisible = ShowRareCommandPalettePreviewThisSession;

    internal const double MinCommandPaletteWidth = 320;
    internal const double MaxCommandPaletteWidth = 760;

    internal static double NormalizeCommandPaletteWidth(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinCommandPaletteWidth, MaxCommandPaletteWidth)
            : AppSettings.DefaultCommandPaletteWidth;

    private double _commandPaletteWidth = AppSettings.DefaultCommandPaletteWidth;
    private Action? _commandPaletteSyncLayout;
    private bool _isResizingCommandPalette;
    private bool _commandPaletteResizeFromLeft;
    private double _commandPaletteResizeStartX;
    private double _commandPaletteResizeStartWidth;
    private bool _isResizingQuickAccessSearch;
    private bool _quickAccessResizeFromLeft;
    private double _quickAccessResizeStartX;
    private double _quickAccessResizeStartWidth;

    public double CommandPaletteWidth
    {
        get => _commandPaletteWidth;
        set
        {
            var normalized = NormalizeCommandPaletteWidth(value);
            if (Math.Abs(_commandPaletteWidth - normalized) < 0.5) return;
            _commandPaletteWidth = normalized;
            OnPropertyChanged();
            CenterQuickAccessSearchFrame();
            _commandPaletteSyncLayout?.Invoke();
            SaveSettings(immediate: false);
        }
    }

    private double GetEffectiveCommandPaletteWidth() => GetFrameGeometry().Width;

    private (double Width, double Delta) GetFrameGeometry()
    {
        var frame = this.FindControl<Border>("QuickAccessSearchFrame");
        if (frame?.Parent is not Panel host || host.Bounds.Width <= 0 ||
            Content is not Grid root || root.Bounds.Width <= 0)
            return (CommandPaletteWidth, 0);
        if (host.TranslatePoint(new Point(0, 0), root) is not { } origin)
            return (CommandPaletteWidth, 0);

        var windowCenter = root.Bounds.Width / 2 - origin.X;
        var brandRight = NaturalHorizontalWidth(this.FindControl<StackPanel>("TopBarBrandPanel"));
        var buttonsLeft = host.Bounds.Width - NaturalHorizontalWidth(this.FindControl<StackPanel>("TopBarRightToolbarStack"));

        const double gap = 12;
        var leftRoom = (windowCenter - brandRight - gap) * 2;
        var rightRoom = (buttonsLeft - windowCenter - gap) * 2;
        var width = Math.Max(0, Math.Min(CommandPaletteWidth, Math.Min(leftRoom, rightRoom)));
        var delta = windowCenter - host.Bounds.Width / 2;
        return (width, delta);
    }

    private static double NaturalHorizontalWidth(StackPanel? stack)
    {
        if (stack is null)
            return 0;

        double width = 0;
        double spacingTotal = 0;
        foreach (var child in stack.Children.OfType<Control>())
        {
            width += child.DesiredSize.Width;
            spacingTotal += stack.Spacing;
        }
        return width + Math.Max(0, spacingTotal - stack.Spacing);
    }

    public bool IsCommandPaletteRarePreviewVisible
    {
        get => _isCommandPaletteRarePreviewVisible;
        private set
        {
            if (_isCommandPaletteRarePreviewVisible == value) return;
            _isCommandPaletteRarePreviewVisible = value;
            OnPropertyChanged();
        }
    }

    private void InitializeQuickAccessPlaceholder()
    {
        _quickAccessSearchBox = this.FindControl<TextBox>("QuickAccessSearchBox");
        if (_quickAccessSearchBox is not null)
        {
            _quickAccessSearchBox.PlaceholderText = ShowRareCommandPalettePreviewThisSession
                ? string.Empty
                : "Search files (> commands, : line, @ symbol)";
        }

        OnPropertyChanged(nameof(IsCommandPaletteRarePreviewVisible));
    }
    private string? _quickAccessIndexedFolder;
    private bool _quickAccessIndexing;
    private DateTime _quickAccessIndexedAtUtc = DateTime.MinValue;
    private List<QuickAccessFile> _quickAccessWorkspaceEntries = [];
    private HashSet<string> _quickAccessWorkspacePaths = new(FileSystemPaths.Comparer);
    private List<QuickAccessFile> _quickAccessExtraEntries = [];
    private List<QuickAccessFile> _quickAccessOpenEntries = [];
    private List<QuickAccessFile> _quickAccessRecentEntries = [];
    private Dictionary<string, int> _quickAccessBoosts = new(FileSystemPaths.Comparer);

    private List<(string Name, string Kind, int Offset)>? _paletteSymbols;
    private bool _paletteSymbolsLoading;
    private int _paletteSymbolsVersion;
    private List<GitBranch>? _paletteGitBranches;
    private bool _paletteGitBranchesLoading;
    private int _paletteGitBranchesVersion;

    private static PaletteMode GetPaletteMode(string? rawText, out string query)
    {
        var raw = (rawText ?? string.Empty).TrimStart();
        if (raw.StartsWith('>')) { query = raw[1..].Trim(); return PaletteMode.Commands; }
        if (raw.StartsWith('@')) { query = raw[1..].Trim(); return PaletteMode.Symbols; }
        if (raw.StartsWith(':')) { query = raw[1..].Trim(); return PaletteMode.Line; }
        query = raw.Trim();
        return PaletteMode.Files;
    }

    private static bool IsKodoTaglineQuery(string? text)
    {
        var normalized = string.Concat((text ?? string.Empty).Where(char.IsLetterOrDigit));
        return normalized.Equals("Codefaststaylight", StringComparison.OrdinalIgnoreCase);
    }

    private void ShowCommandPalette(bool commandsOnly = false, string? prefix = null)
    {
        _quickAccessSearchBox ??= this.FindControl<TextBox>("QuickAccessSearchBox");
        var useOverlaySearchBox = !IsEditorPageVisible || _quickAccessSearchBox?.IsVisible != true;
        var paletteSearchBox = useOverlaySearchBox
            ? new TextBox
            {
                FontSize = 13,
                Padding = new Thickness(10, 8),
                Margin = new Thickness(4, 4, 4, 8),
                Background = WindowBackgroundBrush,
                Foreground = PrimaryTextBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = KodoDesignTokens.ControlRadius,
                PlaceholderText = "Search files, commands, symbols, or lines",
            }
            : _quickAccessSearchBox;
        if (paletteSearchBox is null)
            return;

        var initialText = commandsOnly ? ">" : prefix ?? _quickAccessSearchBox?.Text;
        if (_commandPaletteOverlay is { IsVisible: true })
        {
            if (initialText is not null)
                SetPaletteText(initialText);
            _commandPaletteSearchBox?.Focus();
            return;
        }

        if (Content is not Grid root)
            return;

        _commandPaletteCommands = BuildCommandPaletteCommands();
        _paletteSymbols = null;
        _paletteSymbolsLoading = false;
        _paletteSymbolsVersion++;
        _paletteGitBranches = null;
        _paletteGitBranchesLoading = false;
        _paletteGitBranchesVersion++;
        RebuildQuickAccessExtras();
        StartQuickAccessFileIndex();

        var list = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 460,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = MutedTextBrush,
            Margin = new Thickness(10, 6, 10, 2),
            IsVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var body = new Grid
        {
            RowDefinitions = useOverlaySearchBox ? new RowDefinitions("Auto,*,Auto") : new RowDefinitions("*,Auto")
        };
        if (useOverlaySearchBox)
        {
            var searchRow = new Grid();
            searchRow.Children.Add(paletteSearchBox);
            _commandPalettePreviewText = CreateRareCommandPalettePreviewText();
            searchRow.Children.Add(_commandPalettePreviewText);
            body.Children.Add(searchRow);
            Grid.SetRow(searchRow, 0);
        }
        body.Children.Add(scroll);
        body.Children.Add(status);
        Grid.SetRow(scroll, useOverlaySearchBox ? 1 : 0);
        Grid.SetRow(status, useOverlaySearchBox ? 2 : 1);

        var card = new Border
        {
            Background = CardBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = KodoDesignTokens.ControlRadius,
            Padding = new Thickness(6),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, Spread = 0, Color = Color.FromArgb(55, 0, 0, 0) }),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = AnimationKit.ReduceMotion ? 1d : 0d,
            RenderTransform = AnimationKit.ReduceMotion ? null : AnimationKit.PresetLift(18),
        };
        var cardContent = new Grid();
        cardContent.Children.Add(body);
        cardContent.Children.Add(CreateCommandPaletteResizeGrip(isLeft: true));
        cardContent.Children.Add(CreateCommandPaletteResizeGrip(isLeft: false));
        card.Child = cardContent;
        var overlay = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        overlay.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        });
        overlay.Children.Add(card);

        void SyncLayout()
        {
            var frame = useOverlaySearchBox ? null : this.FindControl<Border>("QuickAccessSearchFrame");
            if (frame is { IsVisible: false })
                frame = null;
            var frameWidth = frame is null ? CommandPaletteWidth : GetEffectiveCommandPaletteWidth();
            var available = overlay.Bounds.Width > 0 ? Math.Max(1, overlay.Bounds.Width - 16) : frameWidth;
            card.Width = Math.Max(MinCommandPaletteWidth, Math.Min(frameWidth, available));
            var resultsTop = useOverlaySearchBox ? 64d : 8d;
            if (frame is not null && overlay.Bounds.Width > 0)
            {
                var frameOrigin = frame.TranslatePoint(new Point(0, 0), overlay);
                if (frameOrigin is { } origin)
                {
                    var left = Math.Clamp(origin.X + frame.Bounds.Width / 2 - card.Width / 2, 8, Math.Max(8, overlay.Bounds.Width - card.Width - 8));
                    resultsTop = Math.Max(8, origin.Y + frame.Bounds.Height + 20);
                    card.Margin = new Thickness(left, resultsTop, 0, 0);
                }
            }
            else if (overlay.Bounds.Width > 0)
            {
                var content = this.FindControl<Grid>("ContentRegionGrid");
                if (content?.TranslatePoint(new Point(0, 0), overlay) is { } contentOrigin)
                {
                    var left = Math.Clamp(
                        contentOrigin.X + (content.Bounds.Width - card.Width) / 2,
                        8,
                        Math.Max(8, overlay.Bounds.Width - card.Width - 8));
                    card.Margin = new Thickness(left, resultsTop, 0, 0);
                }
                else
                {
                    card.Margin = new Thickness(Math.Max(8, (overlay.Bounds.Width - card.Width) / 2), resultsTop, 0, 0);
                }
            }
            scroll.MaxHeight = overlay.Bounds.Height > 0 ? Math.Clamp(overlay.Bounds.Height - resultsTop - 30, 120, 460) : 460;
        }
        _commandPaletteSyncLayout = SyncLayout;

        Grid.SetRow(overlay, 1);
        Grid.SetColumnSpan(overlay, Math.Max(1, root.ColumnDefinitions.Count));
        root.Children.Add(overlay);
        overlay.ZIndex = int.MaxValue;
        SyncLayout();
        overlay.SizeChanged += (_, _) => SyncLayout();

        card.AddHandler(InputElement.PointerPressedEvent, (_, _) => _commandPaletteSearchBox?.Focus(), RoutingStrategies.Bubble);

        AddHandler(InputElement.PointerPressedEvent, PaletteWindowPointerPressed, RoutingStrategies.Tunnel);

        _commandPaletteOverlay = overlay;
        _commandPaletteCard = card;
        _commandPaletteScroll = scroll;
        _commandPaletteList = list;
        _commandPaletteStatus = status;
        _commandPaletteSearchBox = paletteSearchBox;
        _commandPaletteUsesOverlaySearchBox = useOverlaySearchBox;
        Dispatcher.UIThread.Post(() => AnimationKit.FadeIn(card, 170, 18), DispatcherPriority.Loaded);
        if (useOverlaySearchBox)
        {
            paletteSearchBox.TextChanged += CommandPalettePopupSearchBox_OnTextChanged;
            paletteSearchBox.KeyDown += CommandPaletteSearch_OnKeyDown;
        }

        if (initialText is not null)
        {
            _suppressQuickAccessTextChanged = true;
            try
            {
                _commandPaletteSearchBox.Text = initialText;
                _commandPaletteSearchBox.CaretIndex = initialText.Length;
            }
            finally { _suppressQuickAccessTextChanged = false; }
        }
        RefreshCommandPaletteResults();
        Dispatcher.UIThread.Post(() => _commandPaletteSearchBox?.Focus(), DispatcherPriority.Input);
    }

    private TextBlock CreateRareCommandPalettePreviewText()
    {
        var inlines = new Avalonia.Controls.Documents.InlineCollection
        {
            new Avalonia.Controls.Documents.Run("Code fast, stay "),
            new Avalonia.Controls.Documents.Run("light") { Foreground = AccentBrush },
            new Avalonia.Controls.Documents.Run("."),
        };
        return new TextBlock
        {
            FontSize = 13,
            Margin = new Thickness(2, 0),
            Foreground = MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            IsVisible = IsCommandPaletteRarePreviewVisible,
            Inlines = inlines,
        };
    }

    private void CommandPalettePopupSearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressQuickAccessTextChanged)
            return;
        UpdatePaletteShortcutText();
        if (_commandPaletteOverlay is { IsVisible: true })
            RefreshCommandPaletteResults();
    }

    private void PaletteWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_commandPaletteOverlay is null)
            return;
        if (_isResizingCommandPalette || _isResizingQuickAccessSearch)
            return;
        if (e.Source is Visual source)
        {
            var frame = this.FindControl<Border>("QuickAccessSearchFrame");
            foreach (var visual in source.GetSelfAndVisualAncestors())
            {
                if (ReferenceEquals(visual, _commandPaletteCard) || ReferenceEquals(visual, frame))
                    return;
            }
        }
        DismissCommandPalette(restoreFocus: false);
    }

    private void DismissCommandPalette(bool restoreFocus = true)
    {
        var overlay = _commandPaletteOverlay;
        var paletteSearchBox = _commandPaletteSearchBox;
        if (_commandPaletteUsesOverlaySearchBox && paletteSearchBox is not null)
        {
            paletteSearchBox.TextChanged -= CommandPalettePopupSearchBox_OnTextChanged;
            paletteSearchBox.KeyDown -= CommandPaletteSearch_OnKeyDown;
            paletteSearchBox.Text = string.Empty;
        }
        _commandPaletteOverlay = null;
        _commandPaletteCard = null;
        _commandPaletteScroll = null;
        _commandPaletteList = null;
        _commandPaletteStatus = null;
        _commandPalettePreviewText = null;
        _commandPaletteSyncLayout = null;
        _paletteRows.Clear();
        _filteredPaletteCommands = [];
        _paletteSelectedIndex = -1;
        _palettePressedIndex = -1;
        _paletteSymbols = null;
        _paletteSymbolsLoading = false;
        _paletteSymbolsVersion++;
        _paletteGitBranches = null;
        _paletteGitBranchesLoading = false;
        _paletteGitBranchesVersion++;
        IsCommandPaletteRarePreviewVisible = ShowRareCommandPalettePreviewThisSession;
        RemoveHandler(InputElement.PointerPressedEvent, PaletteWindowPointerPressed);
        if (overlay is not null && Content is Grid root)
            root.Children.Remove(overlay);

        _suppressQuickAccessTextChanged = true;
        try
        {
            if (_quickAccessSearchBox is not null)
            {
                _quickAccessSearchBox.Text = string.Empty;
                _quickAccessSearchBox.PlaceholderText = ShowRareCommandPalettePreviewThisSession
                    ? string.Empty
                    : "Search files (> commands, : line, @ symbol)";
            }
        }
        finally { _suppressQuickAccessTextChanged = false; }
        _commandPaletteSearchBox = _quickAccessSearchBox;
        _commandPaletteUsesOverlaySearchBox = false;
        UpdatePaletteShortcutText();
        if (restoreFocus)
            ReleasePaletteFocus();
    }

    private void ReleasePaletteFocus()
    {
        if (IsEditorPageVisible && IsTextEditorVisible && EditorTextBox?.TextArea is { } area)
            area.Focus();
        else if (_commandPaletteSearchBox?.IsFocused == true)
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void SetPaletteText(string text)
    {
        if (_commandPaletteSearchBox is null)
            return;
        _suppressQuickAccessTextChanged = true;
        try
        {
            _commandPaletteSearchBox.Text = text;
            _commandPaletteSearchBox.CaretIndex = text.Length;
        }
        finally { _suppressQuickAccessTextChanged = false; }
        _commandPaletteSearchBox.Focus();
        RefreshCommandPaletteResults();
    }

    private void UpdatePaletteShortcutText()
    {
        var isCommands = GetPaletteMode(_commandPaletteSearchBox?.Text, out _) == PaletteMode.Commands;
        if (isCommands == _paletteShortcutIsCommands)
            return;
        _paletteShortcutIsCommands = isCommands;
        OnPropertyChanged(nameof(QuickAccessShortcutText));
    }

    private void QuickAccessSearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressQuickAccessTextChanged)
            return;
        UpdatePaletteShortcutText();
        if (_commandPaletteOverlay is { IsVisible: true })
            RefreshCommandPaletteResults();
        else if (sender is TextBox { IsKeyboardFocusWithin: true })
            ShowCommandPalette();
    }

    private void QuickAccessSearchBox_OnGotFocus(object? sender, RoutedEventArgs e)
    {
        IsQuickAccessSearchFocused = true;
        ShowCommandPalette();
    }

    private void QuickAccessSearchBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        IsQuickAccessSearchFocused = false;
        if (_commandPaletteOverlay is not { IsVisible: true } || !IsActive)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_commandPaletteOverlay is { IsVisible: true } && IsActive &&
                !_isResizingCommandPalette && !_isResizingQuickAccessSearch &&
                _commandPaletteSearchBox?.IsKeyboardFocusWithin != true)
                DismissCommandPalette(restoreFocus: false);
        }, DispatcherPriority.Input);
    }

    private static readonly Cursor CommandPaletteResizeCursor = new(StandardCursorType.SizeWestEast);

    private Border CreateCommandPaletteResizeGrip(bool isLeft)
    {
        var grip = new Border
        {
            Width = 7,
            Background = Brushes.Transparent,
            HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = CommandPaletteResizeCursor,
        };
        ToolTip.SetTip(grip, "Drag to resize the command palette · double-click to reset");
        Avalonia.Automation.AutomationProperties.SetName(grip,
            isLeft ? "Resize command palette from the left" : "Resize command palette from the right");
        grip.PointerPressed += (_, e) => BeginCommandPaletteResize(e, grip, isLeft);
        grip.PointerMoved += (_, e) => ContinueCommandPaletteResize(e);
        grip.PointerReleased += (_, e) => EndCommandPaletteResize(e, grip);
        grip.PointerCaptureLost += (_, _) => EndCommandPaletteResize(null, grip);
        grip.DoubleTapped += (_, e) =>
        {
            CommandPaletteWidth = AppSettings.DefaultCommandPaletteWidth;
            SaveSettings(immediate: true);
            e.Handled = true;
        };
        return grip;
    }

    private void BeginCommandPaletteResize(PointerPressedEventArgs e, Border grip, bool isLeft)
    {
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        _isResizingCommandPalette = true;
        _commandPaletteResizeFromLeft = isLeft;
        _commandPaletteResizeStartX = e.GetPosition(this).X;
        _commandPaletteResizeStartWidth = CommandPaletteWidth;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void ContinueCommandPaletteResize(PointerEventArgs e)
    {
        if (!_isResizingCommandPalette) return;
        var delta = e.GetPosition(this).X - _commandPaletteResizeStartX;
        if (_commandPaletteResizeFromLeft) delta = -delta;
        CommandPaletteWidth = _commandPaletteResizeStartWidth + delta * 2;
        e.Handled = true;
    }

    private void EndCommandPaletteResize(PointerReleasedEventArgs? e, Border grip)
    {
        if (!_isResizingCommandPalette) return;
        _isResizingCommandPalette = false;
        e?.Pointer.Capture(null);
        if (_commandPaletteOverlay is { IsVisible: true })
            _commandPaletteSearchBox?.Focus();
        SaveSettings(immediate: true);
        if (e is not null) e.Handled = true;
    }

    private static bool IsNearQuickAccessResizeEdge(Border frame, Point point, out bool fromLeft)
    {
        const double edge = 7;
        fromLeft = point.X <= edge;
        return fromLeft || point.X >= frame.Bounds.Width - edge;
    }

    private void QuickAccessSearchFrame_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border frame &&
            e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed &&
            IsNearQuickAccessResizeEdge(frame, e.GetPosition(frame), out var fromLeft))
        {
            _isResizingQuickAccessSearch = true;
            _quickAccessResizeFromLeft = fromLeft;
            _quickAccessResizeStartX = e.GetPosition(this).X;
            _quickAccessResizeStartWidth = CommandPaletteWidth;
            e.Pointer.Capture(frame);
            e.Handled = true;
            return;
        }

        _commandPaletteSearchBox?.Focus();
    }

    private void QuickAccessSearchFrame_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border frame) return;

        if (_isResizingQuickAccessSearch)
        {
            var delta = e.GetPosition(this).X - _quickAccessResizeStartX;
            if (_quickAccessResizeFromLeft) delta = -delta;
            CommandPaletteWidth = _quickAccessResizeStartWidth + delta * 2;
            e.Handled = true;
            return;
        }

        frame.Cursor = IsNearQuickAccessResizeEdge(frame, e.GetPosition(frame), out _)
            ? CommandPaletteResizeCursor
            : null;
    }

    private void QuickAccessSearchFrame_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingQuickAccessSearch) return;
        _isResizingQuickAccessSearch = false;
        e.Pointer.Capture(null);
        if (_commandPaletteOverlay is { IsVisible: true })
            _commandPaletteSearchBox?.Focus();
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private void QuickAccessSearchFrame_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isResizingQuickAccessSearch) return;
        _isResizingQuickAccessSearch = false;
        if (_commandPaletteOverlay is { IsVisible: true })
            _commandPaletteSearchBox?.Focus();
        SaveSettings(immediate: true);
    }

    private void QuickAccessSearchFrame_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border frame ||
            !IsNearQuickAccessResizeEdge(frame, e.GetPosition(frame), out _)) return;
        CommandPaletteWidth = AppSettings.DefaultCommandPaletteWidth;
        SaveSettings(immediate: true);
        e.Handled = true;
    }

    private void EncodingStatusBarPaletteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (HasFileOpen && IsTextEditorVisible)
            ShowCommandPalette(prefix: "> Encoding:");
    }

    private void LineEndingStatusBarPaletteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (HasFileOpen && IsTextEditorVisible)
            ShowCommandPalette(prefix: "> Line Ending:");
    }

    private void IndentationStatusBarPaletteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsTextEditorVisible)
            ShowCommandPalette(prefix: "> Indentation:");
    }

    private static QuickAccessFile CreateQuickAccessFile(string path, string? root, bool? insideRoot = null)
    {
        var inside = insideRoot ?? (!string.IsNullOrWhiteSpace(root) && IsPathInsideDirectory(path, root));
        var relative = inside ? Path.GetRelativePath(root!, path) : path;
        return new QuickAccessFile(path, Path.GetFileName(path), relative, Path.GetDirectoryName(relative) ?? string.Empty);
    }

    private void StartQuickAccessFileIndex()
    {
        var folder = WorkspaceFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || _quickAccessIndexing)
            return;

        var sameFolder = FileSystemPaths.Equals(folder, _quickAccessIndexedFolder);
        if (sameFolder && DateTime.UtcNow - _quickAccessIndexedAtUtc < QuickAccessIndexLifetime)
            return;
        if (!sameFolder)
        {

            _quickAccessWorkspaceEntries = [];
            _quickAccessWorkspacePaths = new HashSet<string>(FileSystemPaths.Comparer);
            RebuildQuickAccessExtras();
        }

        _quickAccessIndexing = true;
        _quickAccessIndexedFolder = folder;
        _ = Task.Run(() =>
        {
            var entries = new List<QuickAccessFile>();
            var paths = new HashSet<string>(FileSystemPaths.Comparer);
            try
            {
                var files = new List<string>();
                var rules = SearchIgnoreRules.Load(folder);
                rules.RootSnapshot = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
                SearchEngine.EnumerateProjectFiles(folder, files, rules);
                foreach (var file in files)
                {
                    if (entries.Count >= 100_000) break;
                    if (paths.Add(file))
                        entries.Add(CreateQuickAccessFile(file, folder, insideRoot: true));
                }
                entries.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                KodoDiagnostics.LogDebug("Quick access file indexing failed.", ex);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _quickAccessIndexing = false;
                if (!FileSystemPaths.Equals(folder, WorkspaceFolderPath))
                {
                    if (_commandPaletteOverlay is { IsVisible: true })
                        StartQuickAccessFileIndex();
                    return;
                }
                _quickAccessWorkspaceEntries = entries;
                _quickAccessWorkspacePaths = paths;
                _quickAccessIndexedAtUtc = DateTime.UtcNow;
                RebuildQuickAccessExtras();
                if (_commandPaletteOverlay is { IsVisible: true })
                    RefreshCommandPaletteResults();
            });
        });
    }

    private void RebuildQuickAccessExtras()
    {
        var root = IsFolderOpen ? WorkspaceFolderPath : null;
        var boosts = new Dictionary<string, int>(FileSystemPaths.Comparer);
        var open = new List<QuickAccessFile>();
        var recent = new List<QuickAccessFile>();
        var extras = new List<QuickAccessFile>();
        var extraSeen = new HashSet<string>(FileSystemPaths.Comparer);

        foreach (var tab in OpenTabs)
        {
            if (tab.IsUntitled || string.IsNullOrWhiteSpace(tab.Path) || boosts.ContainsKey(tab.Path))
                continue;
            boosts[tab.Path] = 60;
            open.Add(CreateQuickAccessFile(tab.Path, root));
        }
        foreach (var item in RecentFiles)
        {
            if (item.IsFolder || boosts.ContainsKey(item.Path) || !File.Exists(item.Path))
                continue;
            boosts[item.Path] = 30;
            recent.Add(CreateQuickAccessFile(item.Path, root));
        }
        foreach (var file in open.Concat(recent))
        {
            if (!_quickAccessWorkspacePaths.Contains(file.Path) && extraSeen.Add(file.Path))
                extras.Add(file);
        }
        if (_quickAccessWorkspaceEntries.Count == 0)
        {
            foreach (var item in FileTreeItems)
            {
                if (item.IsDirectory || string.IsNullOrWhiteSpace(item.FullPath) || !extraSeen.Add(item.FullPath))
                    continue;
                extras.Add(CreateQuickAccessFile(item.FullPath, root));
            }
        }

        _quickAccessBoosts = boosts;
        _quickAccessOpenEntries = open;
        _quickAccessRecentEntries = recent;
        _quickAccessExtraEntries = extras;
    }

    private void ForgetQuickAccessFile(string path)
    {
        _quickAccessWorkspaceEntries.RemoveAll(entry => FileSystemPaths.Equals(entry.Path, path));
        _quickAccessWorkspacePaths.Remove(path);
        _quickAccessExtraEntries.RemoveAll(entry => FileSystemPaths.Equals(entry.Path, path));
        _quickAccessOpenEntries.RemoveAll(entry => FileSystemPaths.Equals(entry.Path, path));
        _quickAccessRecentEntries.RemoveAll(entry => FileSystemPaths.Equals(entry.Path, path));
    }

    private static bool IsFuzzySubsequence(string text, string term)
    {
        var termIndex = 0;
        for (var i = 0; i < text.Length && termIndex < term.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(term[termIndex]))
                termIndex++;
        }
        return termIndex == term.Length;
    }

    private static bool TryPaletteMatch(string term, string text, out int score, out IReadOnlyList<int> indices)
    {
        score = 0;
        indices = Array.Empty<int>();
        if (term.Length == 0 || text.Length == 0 || !IsFuzzySubsequence(text, term))
            return false;
        var (matchScore, matchIndices) = FuzzyMatch.Match(term, text);
        if (matchIndices.Count == 0)
            return false;
        if (matchScore < 1000 && matchIndices.Count > 1 &&
            matchIndices[^1] - matchIndices[0] + 1 - matchIndices.Count > PaletteMaxFuzzyGap)
            return false;
        score = matchScore;
        indices = matchIndices;
        return true;
    }

    private static string[] SplitPaletteTerms(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ScoreQuickAccessFile(QuickAccessFile file, string[] terms, List<int>? nameMatches, List<int>? directoryMatches)
    {
        var total = 0;
        foreach (var term in terms)
        {
            if (TryPaletteMatch(term, file.FileName, out var nameScore, out var nameIndices))
            {
                total += nameScore + 300;
                nameMatches?.AddRange(nameIndices);
                continue;
            }
            if (!TryPaletteMatch(term, file.Relative, out var pathScore, out var pathIndices))
                return int.MinValue;
            total += pathScore;
            if (directoryMatches is not null)
            {
                foreach (var index in pathIndices)
                {
                    if (index < file.Directory.Length)
                        directoryMatches.Add(index);
                }
            }
        }
        return total;
    }

    private static int ScorePaletteCommand(PaletteCommand command, string[] terms, List<int>? titleMatches)
    {
        var total = 0;
        foreach (var term in terms)
        {
            if (TryPaletteMatch(term, command.Title, out var titleScore, out var titleIndices))
            {
                total += titleScore + 200;
                titleMatches?.AddRange(titleIndices);
            }
            else if (command.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
                total += 60;
            else if (TryPaletteMetadataMatch(term, command.Keywords, out var keywordScore))
                total += keywordScore + 40;
            else if (TryPaletteMetadataMatch(term, command.Detail, out var detailScore))
                total += detailScore + 20;
            else
                return int.MinValue;
        }
        return total;
    }

    private static bool TryPaletteMetadataMatch(string term, string metadata, out int score)
    {
        score = 0;
        var matched = false;
        foreach (var candidate in metadata.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryPaletteMatch(term, candidate, out var candidateScore, out _))
                continue;
            matched = true;
            score = Math.Max(score, candidateScore);
        }
        if (!matched && metadata.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            matched = true;
            score = 0;
        }
        return matched;
    }

    private async Task OpenPaletteFileAsync(string path, int line = 0, int column = 1)
    {
        if (!File.Exists(path))
        {
            ForgetQuickAccessFile(path);
            await ShowNotFoundDialogAsync(path, isFolder: false);
            return;
        }

        await OpenFileFromPathAsync(path);
        if (line > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (FileSystemPaths.Equals(_currentFilePath, path))
                    GoToLineInActiveEditor(line, column);
            }, DispatcherPriority.Background);
        }
    }

    private async Task OpenPaletteFolderAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            await ShowNotFoundDialogAsync(path, isFolder: true);
            return;
        }
        await OpenFolderFromPathAsync(path);
    }

    private async Task ApplyPaletteFileEncodingAsync(System.Text.Encoding encoding)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        if (!HasFileOpen || !IsTextEditorVisible || ActiveEditorTab is not { IsUntitled: false } tab ||
            EditorTextBox?.Document is not { } document || string.IsNullOrWhiteSpace(tab.Path))
            return;

        var pane = _activeEditorPane;
        var editor = EditorTextBox;
        var path = tab.Path;
        var originalEncoding = tab.Encoding ?? _currentFileEncoding;
        var lineEnding = tab.LineEnding;
        if (encoding.CodePage == originalEncoding.CodePage &&
            encoding.GetPreamble().Length == originalEncoding.GetPreamble().Length)
            return;

        try
        {
            if (IsTabDiskStateChanged(tab))
                throw new IOException($"\"{Path.GetFileName(path)}\" changed on disk and was left untouched.");

            var documentVersion = document.Version;
            var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
            var expectedWriteTimeUtc = tab.LastKnownLength >= 0 ? tab.LastKnownWriteTimeUtc : (DateTime?)null;
            var expectedLength = tab.LastKnownLength >= 0 ? tab.LastKnownLength : (long?)null;
            await Task.Run(() => WriteTextFileAtomicallyAsync(
                path, snapshot, lineEnding, encoding, expectedWriteTimeUtc, expectedLength,
                async () => await Dispatcher.UIThread.InvokeAsync(() => OpenTabs.Contains(tab) && FileSystemPaths.Equals(tab.Path, path))));

            if (!OpenTabs.Contains(tab) || !FileSystemPaths.Equals(tab.Path, path))
                return;

            var documentUnchanged = ReferenceEquals(document.Version, documentVersion);
            tab.Content = string.Empty;
            tab.Encoding = encoding;
            tab.IsDirty = !documentUnchanged;
            tab.IsModifiedExternally = false;
            CaptureTabDiskState(tab);
            if (ReferenceEquals(_activeEditorPane, pane) && ReferenceEquals(EditorTextBox, editor) && ReferenceEquals(pane.ActiveTab, tab))
            {
                _currentFileEncoding = encoding;
                _isDirty = !documentUnchanged;
                OnPropertyChanged(nameof(EncodingDisplayText));
                OnPropertyChanged(nameof(IsDocumentDirty));
            }
            RefreshState(fullRefresh: true);
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Change encoding", ex);
        }
    }

    private async Task ApplyPaletteLineEndingAsync(Kodo.Models.LineEnding lineEnding)
    {
        if (!HasFileOpen || !IsTextEditorVisible || ActiveEditorTab is not { IsUntitled: false } tab ||
            EditorTextBox?.Document is not { } document || string.IsNullOrWhiteSpace(tab.Path))
            return;

        var pane = _activeEditorPane;
        var editor = EditorTextBox;
        var path = tab.Path;
        var originalLineEnding = tab.LineEnding;
        var encoding = tab.Encoding ?? _currentFileEncoding;
        if (lineEnding == originalLineEnding)
            return;

        try
        {
            if (IsTabDiskStateChanged(tab))
                throw new IOException($"\"{Path.GetFileName(path)}\" changed on disk and was left untouched.");

            var documentVersion = document.Version;
            var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
            var expectedWriteTimeUtc = tab.LastKnownLength >= 0 ? tab.LastKnownWriteTimeUtc : (DateTime?)null;
            var expectedLength = tab.LastKnownLength >= 0 ? tab.LastKnownLength : (long?)null;
            await Task.Run(() => WriteTextFileAtomicallyAsync(
                path, snapshot, lineEnding, encoding, expectedWriteTimeUtc, expectedLength,
                async () => await Dispatcher.UIThread.InvokeAsync(() => OpenTabs.Contains(tab) && FileSystemPaths.Equals(tab.Path, path))));

            if (!OpenTabs.Contains(tab) || !FileSystemPaths.Equals(tab.Path, path))
                return;

            var documentUnchanged = ReferenceEquals(document.Version, documentVersion);
            tab.LineEnding = lineEnding;
            tab.Content = string.Empty;
            tab.IsDirty = !documentUnchanged;
            tab.IsModifiedExternally = false;
            CaptureTabDiskState(tab);
            if (ReferenceEquals(_activeEditorPane, pane) && ReferenceEquals(EditorTextBox, editor) && ReferenceEquals(pane.ActiveTab, tab))
            {
                _currentLineEnding = lineEnding;
                _isDirty = !documentUnchanged;
                OnPropertyChanged(nameof(LineEndingDisplayText));
                OnPropertyChanged(nameof(IsLineEndingVisible));
                OnPropertyChanged(nameof(IsDocumentDirty));
            }
            RefreshState(fullRefresh: true);
        }
        catch (Exception ex)
        {
            await ShowWarningDialogAsync("Change line ending", ex);
        }
    }

    private bool GoToLineInActiveEditor(int line, int column = 1)
    {
        if (!IsTextEditorVisible || EditorTextBox?.Document is not { } doc || EditorTextBox.TextArea is not { } textArea)
            return false;
        var docLine = doc.GetLineByNumber(Math.Clamp(line, 1, doc.LineCount));
        textArea.ClearSelection();
        SetCaretOffsetSafely(textArea.Caret, doc, docLine.Offset + Math.Clamp(column - 1, 0, docLine.Length));
        EditorTextBox.Focus();
        EditorInvalidateEditorLayers();
        return true;
    }

    private bool GoToOffsetInActiveEditor(int offset)
    {
        if (!IsTextEditorVisible || EditorTextBox?.Document is not { } doc || EditorTextBox.TextArea is not { } textArea)
            return false;
        textArea.ClearSelection();
        SetCaretOffsetSafely(textArea.Caret, doc, offset);
        EditorTextBox.Focus();
        EditorInvalidateEditorLayers();
        return true;
    }

    internal void OpenBranchPalette() => ShowCommandPalette(prefix: "> Switch Branch:");

    private List<PaletteCommand> BuildCommandPaletteCommands()
    {
        var hasLsp = ResolveLspExtensionForFile(_currentFilePath) is { HasLsp: true };
        var rules = CurrentLanguageExtension?.LangRules;
        var hasDefinitionProvider = rules?.HasDefinitionProvider == true || hasLsp;
        var hasFormatter = rules?.HasFormatter == true || hasLsp;
        var hasReferences = rules?.HasReferenceProvider == true || hasLsp;
        var hasCodeActions = rules?.HasCodeActionProvider == true || hasLsp;
        var hasWritableTextFile = HasFileOpen && IsTextEditorVisible && ActiveEditorTab is { IsUntitled: false, Path.Length: > 0 };

        bool HasDocument() => IsTextEditorVisible && EditorTextBox?.Document is not null;
        bool HasLspDocument() => HasDocument() && hasLsp;
        Task WithEditor(Action<AvaloniaEdit.Document.TextDocument, AvaloniaEdit.Editing.TextArea> action)
        {
            if (EditorTextBox?.Document is { } doc && EditorTextBox.TextArea is { } area)
                action(doc, area);
            return Task.CompletedTask;
        }
        Task RunMenuHandler(Action<object?, RoutedEventArgs> handler)
        {
            handler(null, null!);
            return Task.CompletedTask;
        }
        async Task RunCompilerCommandAsync(bool isBuild) => await ExecuteCurrentCommandAsync(isBuild, extraArgs: null);
        async Task ToggleExplorerExpansionAsync()
        {
            _isFileTreeExpanded = !_isFileTreeExpanded;
            if (!_isFileTreeExpanded)
            {
                if (!string.IsNullOrWhiteSpace(_currentFolderPath))
                    await PopulateFileTreeAsync(_currentFolderPath);
                return;
            }

            var rootDirs = FileTreeItems.Where(item => item.IsDirectory && item.Depth == 0 && !item.IsExpanded).ToList();
            foreach (var directory in rootDirs)
                await ToggleDirectoryExpansionAsync(directory);
        }
        async Task CloseAllTabsAsync()
        {
            foreach (var tab in OpenTabs.ToList())
            {
                if (!await RequestCloseTabAsync(tab))
                    break;
            }
        }
        async Task CloseOtherTabsAsync()
        {
            if (ActiveEditorTab is not { } activeTab) return;
            foreach (var tab in OpenTabs.Where(tab => !ReferenceEquals(tab, activeTab)).ToList())
                if (!await RequestCloseTabAsync(tab)) break;
        }
        async Task CloseTabsToRightAsync()
        {
            if (ActiveEditorTab is not { } activeTab) return;
            var index = OpenTabs.IndexOf(activeTab);
            if (index < 0) return;
            foreach (var tab in OpenTabs.Skip(index + 1).ToList())
                if (!await RequestCloseTabAsync(tab)) break;
        }
        void ActivateAdjacentTab(int direction)
        {
            var pane = _activeEditorPane;
            var tabs = pane.Tabs.Count > 0 ? pane.Tabs : OpenTabs;
            if (tabs.Count == 0) return;
            var index = pane.ActiveTab is null ? -1 : tabs.IndexOf(pane.ActiveTab);
            var next = (index + direction + tabs.Count) % tabs.Count;
            ActivateTab(tabs[next]);
        }
        async Task CopyActiveFilePathAsync(bool relative)
        {
            if (ActiveEditorTab is not { IsUntitled: false, Path: { } path } ||
                TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(relative ? GetRelativePathOrFullPath(path) : path);
        }
        async Task RevealActiveFileAsync()
        {
            if (ActiveEditorTab is { IsUntitled: false, Path: { } path })
                await OpenPathInSystemExplorer(path, selectItem: true);
        }
        void OpenActiveFileInTerminal()
        {
            if (ActiveEditorTab is not { IsUntitled: false, Path: { } path }) return;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                CreateTerminalSession(workingDirectoryOverride: directory);
        }

        var commands = new List<PaletteCommand>
        {
            new("Go to Home", "Navigation", "GoHome", () => { NavigateTo(AppPage.Home); return Task.CompletedTask; }, Keywords: "welcome start"),
            new("Go to Editor", "Navigation", "GoEditor", () => { NavigateTo(AppPage.Editor); FocusEditor(); return Task.CompletedTask; }, Keywords: "code workspace"),
            new("Open Settings", "Preferences", "OpenSettings", () => { NavigateTo(AppPage.Settings); return Task.CompletedTask; }, Keywords: "preferences options"),
            new("Search Settings…", "Preferences", "", () =>
            {
                NavigateTo(AppPage.Settings);
                Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("SettingsSearchTextBox")?.Focus(), DispatcherPriority.Input);
                return Task.CompletedTask;
            }, Keywords: "preferences find setting configuration"),
            new("Open Marketplace", "Extensions", "OpenExtensions", () => { NavigateTo(AppPage.Extensions); RefreshMarketplaceConnectivityState(); _ = RefreshExtensionsDataAsync(); return Task.CompletedTask; }, Keywords: "plugins extensions themes"),
            new("Keyboard Shortcuts", "Preferences", "", ShowKeyboardShortcutsAsync, Keywords: "keybind hotkey dialog window"),
            new("Open Tutorial", "Help", "", () => { NavigateTo(AppPage.Tutorial); return Task.CompletedTask; }, Keywords: "getting started help guide"),
            new("Open What's New", "Help", "", () => { NavigateTo(AppPage.WhatsNew); return Task.CompletedTask; }, Keywords: "release notes updates changelog"),
            new(IsSplitScreenVisible ? "Close Split Editor" : "Split Editor Right", "View", "ToggleSplitEditor", () => { if (IsSplitScreenVisible) CloseSplitEditor(); else ShowSplitEditor(); return Task.CompletedTask; }, () => CanToggleSplitEditor, "side by side pane group"),
            new("Focus First Editor Group", "View", "FocusFirstEditorGroup", () => { FocusEditorPane(_editorPane); return Task.CompletedTask; }, () => IsSplitScreenVisible, "pane group left"),
            new("Focus Second Editor Group", "View", "FocusSecondEditorGroup", () => { FocusEditorPane(_secondaryEditorPane); return Task.CompletedTask; }, () => IsSplitScreenVisible, "pane group right"),
            new("Focus Other Editor Pane", "View", "FocusOtherEditor", () => { FocusOtherEditorPaneButton_OnClick(null, new RoutedEventArgs()); return Task.CompletedTask; }, () => IsSplitScreenVisible && (_editorPane.ActiveTab is not null || _secondaryEditorPane.ActiveTab is not null), "switch group editor"),
            new("Open Active File to the Side", "View", "OpenActiveFileToSide", () => { if (ActiveEditorTab is { } tab) OpenEditorTabToSide(tab, moveFromSourcePane: true); return Task.CompletedTask; }, () => ActiveEditorTab is not null && IsTextEditorVisible, "split move beside"),

            new("Search Files by Name…", "Navigation", "QuickAccess", () => { SetPaletteText(string.Empty); return Task.CompletedTask; }, Keywords: "quick open file path", KeepOpen: true),
            new("Go to Line…", "Navigation", "GoToLine", () => { SetPaletteText(":"); return Task.CompletedTask; }, HasDocument, "jump line number", KeepOpen: true, Badge: ":"),
            new("Select Current Line", "Editor", "SelectCurrentLine", () => WithEditor((doc, area) => EditorSelectCurrentLine(doc, area)), HasDocument, "select line"),
            new("Go to Symbol in File…", "Navigation", "", () => { SetPaletteText("@"); return Task.CompletedTask; }, HasDocument, "outline function method class member", KeepOpen: true, Badge: "@"),
            new("Next Tab", "Navigation", "", () => { ActivateAdjacentTab(1); return Task.CompletedTask; }, () => _activeEditorPane.Tabs.Count > 1 || OpenTabs.Count > 1, "editor file cycle"),
            new("Previous Tab", "Navigation", "", () => { ActivateAdjacentTab(-1); return Task.CompletedTask; }, () => _activeEditorPane.Tabs.Count > 1 || OpenTabs.Count > 1, "editor file cycle"),

            new("New File", "File", "NewFile", () => { NewFile(); return Task.CompletedTask; }, Keywords: "create"),
            new("Open File…", "File", "OpenFile", async () => await OpenFileAsync(), Keywords: "browse picker dialog"),
            new("Open Folder…", "File", "OpenFolder", async () => await OpenFolderAsync(), Keywords: "workspace project picker dialog"),
            new("Close Folder", "File", "CloseFolder", () => { if (IsFolderOpen) CloseFolder(); return Task.CompletedTask; }, () => IsFolderOpen, "workspace project"),
            new("Save", "File", "Save", async () => { await SaveAsync(); }, () => ActiveEditorTab is not null, "file picker dialog"),
            new("Save As…", "File", "SaveAs", async () => { await SaveAsAsync(); }, () => ActiveEditorTab is not null, "file picker dialog"),
            new("Save All", "File", "", async () => await SaveAllAsync(), () => OpenTabs.Any(tab => tab.IsDirty), "dialog file"),
            new("Change File Encoding…", "File", "", () => { SetPaletteText("> Encoding:"); return Task.CompletedTask; }, () => hasWritableTextFile, "charset utf8 bom utf16 codepage", KeepOpen: true),
            new("Encoding: UTF-8", "File", "", () => ApplyPaletteFileEncodingAsync(new System.Text.UTF8Encoding(false)), () => hasWritableTextFile, "charset no bom"),
            new("Encoding: UTF-8 with BOM", "File", "", () => ApplyPaletteFileEncodingAsync(new System.Text.UTF8Encoding(true)), () => hasWritableTextFile, "charset signature"),
            new("Encoding: UTF-16 LE", "File", "", () => ApplyPaletteFileEncodingAsync(System.Text.Encoding.Unicode), () => hasWritableTextFile, "charset little endian unicode"),
            new("Encoding: UTF-16 BE", "File", "", () => ApplyPaletteFileEncodingAsync(System.Text.Encoding.BigEndianUnicode), () => hasWritableTextFile, "charset big endian unicode"),
            new("Encoding: UTF-32", "File", "", () => ApplyPaletteFileEncodingAsync(System.Text.Encoding.UTF32), () => hasWritableTextFile, "charset unicode"),
            new("Encoding: Windows-1252", "File", "", () => ApplyPaletteFileEncodingAsync(System.Text.Encoding.GetEncoding(1252)), () => hasWritableTextFile, "charset ansi"),
            new("Encoding: ASCII", "File", "", () => ApplyPaletteFileEncodingAsync(System.Text.Encoding.ASCII), () => hasWritableTextFile, "charset"),
            new("Change Line Ending…", "File", "", () => { SetPaletteText("> Line Ending:"); return Task.CompletedTask; }, () => hasWritableTextFile, "eol newline crlf lf cr", KeepOpen: true),
            new("Line Ending: LF", "File", "", () => ApplyPaletteLineEndingAsync(Kodo.Models.LineEnding.LF), () => hasWritableTextFile, "eol unix macos newline"),
            new("Line Ending: CRLF", "File", "", () => ApplyPaletteLineEndingAsync(Kodo.Models.LineEnding.CRLF), () => hasWritableTextFile, "eol windows newline"),
            new("Line Ending: CR", "File", "", () => ApplyPaletteLineEndingAsync(Kodo.Models.LineEnding.CR), () => hasWritableTextFile, "eol classic mac newline"),
            new("Change Indentation…", "Editor", "", () => { SetPaletteText("> Indentation:"); return Task.CompletedTask; }, () => HasDocument(), "spaces tabs tab size indent", KeepOpen: true),
            new("Indentation: Spaces 2", "Editor", "", () => { InsertSpaces = true; TabSize = 2; return Task.CompletedTask; }, () => HasDocument(), "indent tab width"),
            new("Indentation: Spaces 4", "Editor", "", () => { InsertSpaces = true; TabSize = 4; return Task.CompletedTask; }, () => HasDocument(), "indent tab width"),
            new("Indentation: Spaces 8", "Editor", "", () => { InsertSpaces = true; TabSize = 8; return Task.CompletedTask; }, () => HasDocument(), "indent tab width"),
            new("Indentation: Tabs, size 2", "Editor", "", () => { InsertSpaces = false; TabSize = 2; return Task.CompletedTask; }, () => HasDocument(), "indent tab width"),
            new("Indentation: Tabs, size 4", "Editor", "", () => { InsertSpaces = false; TabSize = 4; return Task.CompletedTask; }, () => HasDocument(), "indent tab width"),
            new("Indentation: Tabs, size 8", "Editor", "", () => { InsertSpaces = false; TabSize = 8; return Task.CompletedTask; }, () => HasDocument(), "indent tab width"),
            new("Close Active Tab", "File", "CloseTab", async () => { if (ActiveEditorTab is not null) await RequestCloseTabAsync(ActiveEditorTab); }, () => ActiveEditorTab is not null),
            new("Close Other Tabs", "File", "", CloseOtherTabsAsync, () => OpenTabs.Count > 1, "close tabs documents"),
            new("Close Tabs to the Right", "File", "", CloseTabsToRightAsync, () => ActiveEditorTab is not null && OpenTabs.IndexOf(ActiveEditorTab) < OpenTabs.Count - 1, "close tabs below after"),
            new("Close All Tabs", "File", "", CloseAllTabsAsync, () => OpenTabs.Count > 0, "close documents"),
            new("Copy Active File Path", "File", "", async () =>
            {
                if (ActiveEditorTab is { IsUntitled: false } tab && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(tab.Path);
            }, () => ActiveEditorTab is { IsUntitled: false }, "copy location directory"),
            new("Copy Active File Relative Path", "File", "", () => CopyActiveFilePathAsync(relative: true), () => ActiveEditorTab is { IsUntitled: false }, "copy location workspace"),
            new("Copy Active File Name", "File", "", async () =>
            {
                if (ActiveEditorTab is { IsUntitled: false, Path: { } path } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(Path.GetFileName(path));
            }, () => ActiveEditorTab is { IsUntitled: false }, "copy basename"),
            new("Reveal Active File in File Manager", "File", "", RevealActiveFileAsync, () => ActiveEditorTab is { IsUntitled: false }, "show explorer folder finder"),
            new("Open Active File Folder in Terminal", "Terminal", "", () => { OpenActiveFileInTerminal(); return Task.CompletedTask; }, () => ActiveEditorTab is { IsUntitled: false }, "integrated shell directory"),

            new("Find in File", "Search", "FindInFile", () => { OpenSearchPanel(SearchMode.FindInFile); return Task.CompletedTask; }, () => ActiveEditorTab is not null),
            new("Find in Project", "Search", "FindInProject", () => { OpenSearchPanel(SearchMode.ProjectSearch); return Task.CompletedTask; }, () => IsFolderOpen, "search text files"),
            new("Search in Current Folder", "Search", "", () => { OpenSearchPanel(SearchMode.ProjectSearch); return Task.CompletedTask; }, () => IsFolderOpen, "search in directory workspace"),
            new("Find Next", "Search", "", () => RunMenuHandler((_, _) => FindInEditor(forward: true)), () => IsSearchPanelActive && IsTextEditorVisible && !string.IsNullOrEmpty(FindText), "next match"),
            new("Find Previous", "Search", "", () => RunMenuHandler((_, _) => FindInEditor(forward: false)), () => IsSearchPanelActive && IsTextEditorVisible && !string.IsNullOrEmpty(FindText), "previous match"),
            new("Toggle Match Case", "Search", "", () => { IsSearchMatchCaseEnabled = !IsSearchMatchCaseEnabled; return Task.CompletedTask; }, () => IsSearchPanelActive, "search case sensitive"),
            new("Toggle Whole Word", "Search", "", () => { IsSearchWholeWordEnabled = !IsSearchWholeWordEnabled; return Task.CompletedTask; }, () => IsSearchPanelActive, "search exact word"),
            new("Toggle Regular Expression Search", "Search", "", () => { IsSearchRegexEnabled = !IsSearchRegexEnabled; return Task.CompletedTask; }, () => IsSearchPanelActive, "search regex pattern"),

            new("Go to Definition", "Editor", "GoToDefinition", () => { GoToDefinitionAtCaret(); return Task.CompletedTask; }, () => HasDocument() && hasDefinitionProvider, "language server navigation"),
            new("Go Back", "Navigation", "EditorGoBack", () => NavigateEditorHistoryAsync(true), HasDocument, "previous location history"),
            new("Go Forward", "Navigation", "EditorGoForward", () => NavigateEditorHistoryAsync(false), HasDocument, "next location history"),
            new("Next Problem", "Editor", "NextProblem", () => { NavigateEditorDiagnostic(1); return Task.CompletedTask; }, () => HasDocument() && (_errorHighlightRenderer.Spans.Count > 0 || _deadCodeHighlightRenderer.Spans.Count > 0), "diagnostic error warning"),
            new("Previous Problem", "Editor", "PreviousProblem", () => { NavigateEditorDiagnostic(-1); return Task.CompletedTask; }, () => HasDocument() && (_errorHighlightRenderer.Spans.Count > 0 || _deadCodeHighlightRenderer.Spans.Count > 0), "diagnostic error warning"),
            new("Go to Type Definition", "Editor", "", () => RunMenuHandler(EditorTypeDefinitionMenuItem_OnClick), HasLspDocument, "language server"),
            new("Go to Implementation", "Editor", "", () => RunMenuHandler(EditorImplementationMenuItem_OnClick), HasLspDocument, "language server"),
            new("Find All References", "Editor", "", () => RunMenuHandler(EditorFindReferencesMenuItem_OnClick), () => HasDocument() && hasReferences, "usages"),
            new("Rename Symbol…", "Editor", "", () => RunMenuHandler(EditorRenameSymbolMenuItem_OnClick), HasLspDocument, "refactor dialog rename input"),
            new("Show Document Symbols", "Editor", "", () => RunMenuHandler(EditorDocumentSymbolsMenuItem_OnClick), HasLspDocument, "outline language server"),
            new("Show Signature Help", "Editor", "", () => RunMenuHandler(EditorSignatureHelpMenuItem_OnClick), HasLspDocument, "parameters"),
            new("Quick Fix…", "Editor", "", () => RunMenuHandler(EditorQuickFixMenuItem_OnClick), () => HasDocument() && hasCodeActions, "code action"),
            new("Format Document", "Editor", "", () => RunMenuHandler(EditorFormatDocumentMenuItem_OnClick), () => HasDocument() && hasFormatter, "prettify indent"),
            new("Change All Occurrences", "Editor", "ChangeAllOccurrences", () => RunMenuHandler(EditorChangeAllOccurrencesMenuItem_OnClick), () => HasDocument() && EditorTextBox?.TextArea?.Selection is { IsEmpty: false }, "multi cursor replace"),
            new("Undo", "Editor", "", () => WithEditor((doc, _) => { if (doc.UndoStack.CanUndo) doc.UndoStack.Undo(); }), HasDocument, "edit revert"),
            new("Redo", "Editor", "", () => WithEditor((doc, _) => { if (doc.UndoStack.CanRedo) doc.UndoStack.Redo(); }), HasDocument, "edit repeat"),
            new("Select All Text", "Editor", "", () => { EditorTextBox?.SelectAll(); return Task.CompletedTask; }, HasDocument, "highlight document contents"),
            new("Cut", "Editor", "Cut", () => RunMenuHandler(EditorCutMenuItem_OnClick), () => HasDocument() && EditorTextBox?.TextArea?.Selection is { IsEmpty: false }, "clipboard remove"),
            new("Copy", "Editor", "Copy", () => RunMenuHandler(EditorCopyMenuItem_OnClick), () => HasDocument() && EditorTextBox?.TextArea?.Selection is { IsEmpty: false }, "clipboard"),
            new("Paste", "Editor", "Paste", () => RunMenuHandler(EditorPasteMenuItem_OnClick), () => HasDocument() && EditorTextBox?.TextArea is not null, "clipboard insert"),
            new("Scroll Editor Left", "Editor", "ScrollEditorLeft", () => { ScrollEditorHorizontally(-EditorHorizontalScrollStep); return Task.CompletedTask; }, HasDocument, "horizontal view"),
            new("Scroll Editor Right", "Editor", "ScrollEditorRight", () => { ScrollEditorHorizontally(EditorHorizontalScrollStep); return Task.CompletedTask; }, HasDocument, "horizontal view"),
            new("Scroll Editor Left by Page", "Editor", "ScrollEditorPageLeft", () => { ScrollEditorHorizontallyByPage(-1); return Task.CompletedTask; }, HasDocument, "horizontal view"),
            new("Scroll Editor Right by Page", "Editor", "ScrollEditorPageRight", () => { ScrollEditorHorizontallyByPage(1); return Task.CompletedTask; }, HasDocument, "horizontal view"),
            new("Run Current File", "Run", "", () => RunCompilerCommandAsync(isBuild: false), () => IsRunButtonEnabled, "execute start launch"),
            new("Build Current File", "Build", "", () => RunCompilerCommandAsync(isBuild: true), () => IsBuildButtonEnabled, "compile project"),
            new("Toggle Line Comment", "Editor", "ToggleLineComment", () => WithEditor((doc, area) => ToggleLineComment(doc, area, area.Selection, area.Caret)), () => HasDocument() && IsSmartSyntaxEnabled(), "comment uncomment"),
            new("Move Line Up", "Editor", "MoveLineUp", () => WithEditor((doc, area) => EditorMoveLines(doc, area, -1)), HasDocument),
            new("Move Line Down", "Editor", "MoveLineDown", () => WithEditor((doc, area) => EditorMoveLines(doc, area, 1)), HasDocument),
            new("Duplicate Line Up", "Editor", "DuplicateLineUp", () => WithEditor((doc, area) => EditorDuplicateLines(doc, area, -1)), HasDocument, "copy"),
            new("Duplicate Line Down", "Editor", "DuplicateLineDown", () => WithEditor((doc, area) => EditorDuplicateLines(doc, area, 1)), HasDocument, "copy"),
            new("Delete Line", "Editor", "DeleteLine", () => WithEditor((doc, area) => EditorDeleteLines(doc, area)), HasDocument, "remove"),
            new("Insert Line Above", "Editor", "InsertLineAbove", () => WithEditor((doc, area) => EditorInsertLine(doc, area, false)), HasDocument),
            new("Insert Line Below", "Editor", "InsertLineBelow", () => WithEditor((doc, area) => EditorInsertLine(doc, area, true)), HasDocument),
            new("Toggle Current Line Highlight", "Editor", "ToggleHighlightCurrentLine", () => { EditorToggleHighlightCurrentLine(); return Task.CompletedTask; }, HasDocument),
            new(IsWordWrapEnabled ? "Disable Word Wrap" : "Enable Word Wrap", "Editor", "", () => { IsWordWrapEnabled = !IsWordWrapEnabled; return Task.CompletedTask; }, Keywords: "toggle wrap lines"),
            new(IsInsightEnabled ? "Disable Insight" : "Enable Insight", "Editor", "", () => { IsInsightEnabled = !IsInsightEnabled; return Task.CompletedTask; }, Keywords: "toggle language intelligence completion errors"),
            new(LspDiagnosticsEnabled ? "Disable Language Server Diagnostics" : "Enable Language Server Diagnostics", "Editor", "", () => { LspDiagnosticsEnabled = !LspDiagnosticsEnabled; return Task.CompletedTask; }, Keywords: "toggle lsp errors warnings"),

            new("Toggle File Explorer", "View", "ToggleFileExplorer", () => { IsFileExplorerVisible = !IsFileExplorerVisible; return Task.CompletedTask; }, () => IsFolderOpen, Keywords: "sidebar"),
            new("New File in Workspace", "File", "", async () => { if (IsFolderOpen) await CreateExplorerEntryAsync(GetExplorerRootDirectory(), "new-file", ".txt", true); }, () => IsFolderOpen, "create explorer"),
            new("New Folder in Workspace", "File", "", async () => { if (IsFolderOpen) await CreateExplorerEntryAsync(GetExplorerRootDirectory(), "New Folder", null, false); }, () => IsFolderOpen, "create directory explorer"),
            new("Refresh File Explorer", "View", "", async () => await RefreshExplorerTreeAsync(), () => IsFolderOpen, "reload refresh project tree"),
            new(_isFileTreeExpanded ? "Collapse All Folders" : "Expand All Folders", "View", "", ToggleExplorerExpansionAsync, () => IsFolderOpen, "explorer tree directories"),
            new("Toggle Terminal", "View", "ToggleTerminal", () => { ToggleTerminalPanel(); return Task.CompletedTask; }, Keywords: "panel console"),
            new(IsMinimapVisible ? "Hide Minimap" : "Show Minimap", "View", "", () => { IsMinimapVisible = !IsMinimapVisible; return Task.CompletedTask; }, Keywords: "toggle overview code map"),
            new("Focus Terminal", "Terminal", "", () => { ToggleTerminalPanel(ensureVisible: true); FocusActiveTerminal(); return Task.CompletedTask; }, () => IsTerminalSupported, "activate switch panel"),
            new("Zoom In", "View", "ZoomIn", () => { ZoomImageIn(); return Task.CompletedTask; }, () => HasImagePreview, "image"),
            new("Zoom Out", "View", "ZoomOut", () => { ZoomImageOut(); return Task.CompletedTask; }, () => HasImagePreview, "image"),
            new("Reset Zoom", "View", "ZoomReset", () => { ZoomImageReset(); return Task.CompletedTask; }, () => HasImagePreview, "image"),
            new("New Terminal", "Terminal", "NewTerminalSession", () => { CreateTerminalSession(); return Task.CompletedTask; }, () => IsTerminalSupported, "shell console"),
            new("Clear Terminal", "Terminal", "", () => { ClearActiveTerminal(); return Task.CompletedTask; }, () => IsTerminalSupported && ActiveTerminalSession is not null, "erase terminal output"),
            new("Restart Terminal", "Terminal", "", () => { RestartActiveTerminal(); return Task.CompletedTask; }, () => IsTerminalSupported && ActiveTerminalSession is not null, "shell relaunch"),
            new("Close Terminal Session", "Terminal", "", () => { if (ActiveTerminalSession is { } session) CloseTerminalSession(session); return Task.CompletedTask; }, () => IsTerminalSupported && ActiveTerminalSession is not null, "remove shell"),
            new("Close All Terminal Sessions", "Terminal", "", () => { CloseAllTerminalSessions(); return Task.CompletedTask; }, () => IsTerminalSupported && TerminalSessions.Count > 0, "remove shells"),
            new(IsAutoSaveEnabled ? "Disable Auto Save" : "Enable Auto Save", "File", "", () => { IsAutoSaveEnabled = !IsAutoSaveEnabled; return Task.CompletedTask; }, Keywords: "toggle automatic save"),
            new(IsPerformanceModeEnabled ? "Disable Performance Mode" : "Enable Performance Mode", "Preferences", "", () => { IsPerformanceModeEnabled = !IsPerformanceModeEnabled; return Task.CompletedTask; }, Keywords: "toggle speed lag lightweight"),
            new(IsStatusBarFilePathVisible ? "Show Short File Names in Status Bar" : "Show Full File Paths in Status Bar", "View", "", () => { IsStatusBarFilePathVisible = !IsStatusBarFilePathVisible; return Task.CompletedTask; }, Keywords: "toggle status path location"),
            new("Use Dark Theme", "Appearance", "", () => { ApplyTheme("Dark"); return Task.CompletedTask; }, () => !IsDarkThemeActive, "appearance"),
            new("Use Light Theme", "Appearance", "", () => { ApplyTheme("Light"); return Task.CompletedTask; }, () => !IsLightThemeActive, "appearance"),
            new("Use System Theme", "Appearance", "", () => { ApplyTheme("System"); return Task.CompletedTask; }, () => !IsSystemThemeActive, "appearance operating system"),

            new("Check for Updates", "Help", "", async () => await CheckForUpdatesManuallyAsync(), Keywords: "update version"),
        };

        void AddSettingToggle(string name, Func<bool> getValue, Action<bool> setValue, string keywords = "", Func<bool>? isAvailable = null)
        {
            var enabled = getValue();
            commands.Add(new PaletteCommand(
                $"{(enabled ? "Disable" : "Enable")} {name}",
                "Settings",
                string.Empty,
                () => { setValue(!getValue()); return Task.CompletedTask; },
                IsAvailable: isAvailable,
                Keywords: $"settings preferences toggle {keywords}"));
        }

        AddSettingToggle("Code Suggestions", () => IsInsightCodeSuggestionsEnabled, value => IsInsightCodeSuggestionsEnabled = value, "insight completion autocomplete");
        AddSettingToggle("Dead Code Detection", () => IsInsightDeadCodeEnabled, value => IsInsightDeadCodeEnabled = value, "insight analysis");
        AddSettingToggle("Error Detection", () => IsInsightErrorDetectionEnabled, value => IsInsightErrorDetectionEnabled = value, "insight diagnostics errors");
        AddSettingToggle("Language Server Features", () => LspEnabled, value => LspEnabled = value, "lsp language intelligence");
        AddSettingToggle("Language Server Completions", () => LspCompletionEnabled, value => LspCompletionEnabled = value, "lsp autocomplete");
        AddSettingToggle("Language Server Hover", () => LspHoverEnabled, value => LspHoverEnabled = value, "lsp tooltips documentation");
        AddSettingToggle("Signature Help", () => LspSignatureHelpEnabled, value => LspSignatureHelpEnabled = value, "lsp parameters");
        AddSettingToggle("Inlay Hints", () => LspInlayHintsEnabled, value => LspInlayHintsEnabled = value, "lsp inline hints");
        AddSettingToggle("Code Folding", () => LspFoldingEnabled, value => LspFoldingEnabled = value, "lsp collapse regions");
        AddSettingToggle("Document Highlights", () => LspDocumentHighlightsEnabled, value => LspDocumentHighlightsEnabled = value, "lsp references occurrences");
        AddSettingToggle("Semantic Highlighting", () => LspSemanticHighlightingEnabled, value => LspSemanticHighlightingEnabled = value, "lsp colors colours tokens");
        AddSettingToggle("Automatic Language Server Installation", () => LspAutoInstall, value => LspAutoInstall = value, "lsp install download");
        AddSettingToggle("Prefer Managed Language Servers", () => LspPreferManaged, value => LspPreferManaged = value, "lsp install bundled");
        AddSettingToggle("Prefer System Language Servers", () => LspPreferSystem, value => LspPreferSystem = value, "lsp install external");
        AddSettingToggle("Confirm Before Closing Unsaved Tabs", () => IsConfirmBeforeClosingUnsavedTabsEnabled, value => IsConfirmBeforeClosingUnsavedTabsEnabled = value, "close save prompt");
        AddSettingToggle("Restore Open Tabs on Launch", () => IsRestoreOpenTabsOnLaunchEnabled, value => IsRestoreOpenTabsOnLaunchEnabled = value, "startup session restore");

        commands.Add(new PaletteCommand("Increase Editor Font Size", "Settings", string.Empty,
            () => { EditorFontSize = Math.Min(32, EditorFontSize + 1); return Task.CompletedTask; },
            () => EditorFontSize < 32, "font zoom text"));
        commands.Add(new PaletteCommand("Decrease Editor Font Size", "Settings", string.Empty,
            () => { EditorFontSize = Math.Max(8, EditorFontSize - 1); return Task.CompletedTask; },
            () => EditorFontSize > 8, "font zoom text"));
        commands.Add(new PaletteCommand("Use 2 Spaces for Indentation", "Settings", string.Empty,
            () => { TabSizeIndex = 0; return Task.CompletedTask; }, () => InsertSpaces && TabSize != 2, "indent tab size"));
        commands.Add(new PaletteCommand("Use 4 Spaces for Indentation", "Settings", string.Empty,
            () => { TabSizeIndex = 1; return Task.CompletedTask; }, () => InsertSpaces && TabSize != 4, "indent tab size"));
        commands.Add(new PaletteCommand("Use 8 Spaces for Indentation", "Settings", string.Empty,
            () => { TabSizeIndex = 2; return Task.CompletedTask; }, () => InsertSpaces && TabSize != 8, "indent tab size"));
        commands.Add(new PaletteCommand("Use Tabs for Indentation", "Settings", string.Empty,
            () => { TabSizeIndex = 3; return Task.CompletedTask; }, () => InsertSpaces, "indent tab size"));
        foreach (var fontFamily in EditorFontChoices)
        {
            var selectedFontFamily = fontFamily;
            commands.Add(new PaletteCommand($"Use {selectedFontFamily} Editor Font", "Settings", string.Empty,
                () => { EditorFontFamily = selectedFontFamily; return Task.CompletedTask; },
                () => !string.Equals(EditorFontFamily, selectedFontFamily, StringComparison.OrdinalIgnoreCase),
                "editor typography font family"));
        }

        foreach (var (ownerName, command) in _pluginPaletteCommands.Values
                     .OrderBy(item => item.OwnerName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Command.Title, StringComparer.OrdinalIgnoreCase))
        {
            var category = string.IsNullOrWhiteSpace(command.Category) ? "Plugin" : $"Plugin · {command.Category}";
            commands.Add(new PaletteCommand(
                command.Title,
                category,
                string.Empty,
                command.Execute,
                command.CanExecute,
                command.Keywords,
                command.Detail,
                ownerName,
                command.Shortcut,
                command.KeepPaletteOpen));
        }

        commands.AddRange(BuildGitBranchCommands());

        foreach (var recent in RecentFiles.Where(item => !item.IsFolder).Take(MaxRecentFiles))
        {
            var path = recent.Path;
            commands.Add(new PaletteCommand($"Open Recent: {Path.GetFileName(path)}", "Recent", "", () => OpenPaletteFileAsync(path), Keywords: path, Detail: path));
        }
        foreach (var recent in RecentFiles.Where(item => item.IsFolder).Take(MaxRecentFiles))
        {
            var path = recent.Path;
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            commands.Add(new PaletteCommand($"Open Recent Folder: {(string.IsNullOrEmpty(name) ? path : name)}", "Recent", "", () => OpenPaletteFolderAsync(path), Keywords: path, Detail: path));
        }
        return commands;
    }

    private List<PaletteCommand> BuildCommandModeEntries(string query, out int totalMatches)
    {
        var available = _commandPaletteCommands.Where(command => command.IsAvailable?.Invoke() != false).ToList();
        var terms = SplitPaletteTerms(query);
        if (terms.Length == 0)
        {
            totalMatches = available.Count;
            var recentTitles = _recentPaletteCommands;
            var recent = recentTitles
                .Select(title => available.FirstOrDefault(command => command.Title == title))
                .Where(command => command is not null)
                .Select(command => command! with { Section = "Recently Used" })
                .ToList();
            var recentSet = new HashSet<string>(recent.Select(command => command.Title), StringComparer.Ordinal);
            var others = available
                .Where(command => !recentSet.Contains(command.Title))
                .Select(command => recent.Count > 0 ? command with { Section = "Commands" } : command);
            return recent.Concat(others).ToList();
        }

        var scored = new List<(PaletteCommand Command, int Score, List<int> Matches)>();
        foreach (var command in available)
        {
            var matches = new List<int>();
            var score = ScorePaletteCommand(command, terms, matches);
            if (score != int.MinValue)
                scored.Add((command, score, matches));
        }
        totalMatches = scored.Count;
        return scored
            .OrderByDescending(item => item.Score)
            .Take(PaletteMaxResults)
            .Select(item => item.Command with { TitleMatches = item.Matches })
            .ToList();
    }

    private PaletteCommand CreateQuickAccessFileEntry(QuickAccessFile file, string section, IReadOnlyList<int>? nameMatches, IReadOnlyList<int>? directoryMatches, int line = 0, int column = 1) =>
        new(file.FileName, "File", "", () => OpenPaletteFileAsync(file.Path, line, column),
            Detail: file.Directory, Section: section, TitleMatches: nameMatches, DetailMatches: directoryMatches);

    private List<PaletteCommand> BuildQuickAccessHintEntries()
    {
        const string section = "Quick Access";
        var entries = new List<PaletteCommand>
        {
            new("Show and Run Commands", "Quick Access", "CommandPalette", () => { SetPaletteText(">"); return Task.CompletedTask; },
                Section: section, KeepOpen: true),
            new("Go to Symbol in File", "Quick Access", "", () => { SetPaletteText("@"); return Task.CompletedTask; },
                () => IsTextEditorVisible && EditorTextBox?.Document is not null, Section: section, Badge: "@", KeepOpen: true),
            new("Go to Line", "Quick Access", "", () => { SetPaletteText(":"); return Task.CompletedTask; },
                () => IsTextEditorVisible && EditorTextBox?.Document is not null, Section: section, Badge: ":", KeepOpen: true),
            new("Search for Text in Project", "Quick Access", "FindInProject", () => { OpenSearchPanel(SearchMode.ProjectSearch); return Task.CompletedTask; },
                () => IsFolderOpen, Section: section),
        };
        return entries.Where(entry => entry.IsAvailable?.Invoke() != false).ToList();
    }

    private List<PaletteCommand> BuildQuickAccessFileCommands(string query, out int totalMatches)
    {
        var line = 0;
        var column = 1;
        var suffix = PaletteFileLineSuffix.Match(query);
        if (suffix.Success)
        {
            query = suffix.Groups["query"].Value.Trim();
            if (!int.TryParse(suffix.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out line))
                line = int.MaxValue;
            if (!suffix.Groups["column"].Success || !int.TryParse(suffix.Groups["column"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out column))
                column = 1;
        }

        var terms = SplitPaletteTerms(query)
            .Select(term => term.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))
            .ToArray();

        if (terms.Length == 0)
        {
            var entries = new List<PaletteCommand>();
            var shown = new HashSet<string>(FileSystemPaths.Comparer);
            foreach (var file in _quickAccessOpenEntries)
            {
                if (shown.Add(file.Path))
                    entries.Add(CreateQuickAccessFileEntry(file, "Open", null, null, line, column));
            }
            foreach (var file in _quickAccessRecentEntries)
            {
                if (shown.Add(file.Path))
                    entries.Add(CreateQuickAccessFileEntry(file, "Recent", null, null, line, column));
            }
            var recentCommands = _recentPaletteCommands
                .Select(title => BuildCommandPaletteCommands().FirstOrDefault(command => command.Title == title))
                .Where(command => command is not null && command.IsAvailable?.Invoke() != false)
                .Select(command => command! with { Section = "Recently Used" })
                .ToList();
            entries.AddRange(recentCommands);
            entries.AddRange(BuildQuickAccessHintEntries());

            var filesShown = 0;
            foreach (var file in _quickAccessWorkspaceEntries.Concat(_quickAccessExtraEntries))
            {
                if (filesShown >= PaletteMaxResults)
                    break;
                if (!shown.Add(file.Path))
                    continue;
                entries.Add(CreateQuickAccessFileEntry(file, "Files", null, null, line, column));
                filesShown++;
            }
            totalMatches = _quickAccessWorkspaceEntries.Count + _quickAccessExtraEntries.Count;
            return entries;
        }

        var queue = new PriorityQueue<(QuickAccessFile File, int Score), long>();
        var matched = 0;
        void Consider(QuickAccessFile file)
        {
            var score = ScoreQuickAccessFile(file, terms, null, null);
            if (score == int.MinValue)
                return;
            matched++;
            if (_quickAccessBoosts.TryGetValue(file.Path, out var boost))
                score += boost;
            queue.Enqueue((file, score), (long)score * 4096 - Math.Min(file.Relative.Length, 4095));
            if (queue.Count > PaletteMaxResults)
                queue.Dequeue();
        }
        foreach (var file in _quickAccessOpenEntries)
            Consider(file);
        foreach (var file in _quickAccessRecentEntries)
            Consider(file);
        foreach (var file in _quickAccessWorkspaceEntries)
            Consider(file);
        foreach (var file in _quickAccessExtraEntries)
            Consider(file);

        var top = new List<QuickAccessFile>(queue.Count);
        while (queue.TryDequeue(out var item, out _))
            top.Add(item.File);
        top.Reverse();

        totalMatches = matched;
        var results = new List<PaletteCommand>(top.Count);
        foreach (var file in top)
        {
            var nameMatches = new List<int>();
            var directoryMatches = new List<int>();
            ScoreQuickAccessFile(file, terms, nameMatches, directoryMatches);
            results.Add(CreateQuickAccessFileEntry(file, string.Empty, nameMatches, directoryMatches, line, column));
        }
        return results;
    }

    private List<PaletteCommand> BuildLineCommands(string query, out string? message)
    {
        message = null;
        if (!IsTextEditorVisible || EditorTextBox?.Document is not { } doc)
        {
            message = "Open a text file to go to a line.";
            return [];
        }

        var spec = PaletteLineSpec.Match(query);
        if (!spec.Success)
        {
            message = $"Type a line number between 1 and {doc.LineCount:N0} (optionally followed by :column).";
            return [];
        }

        if (!int.TryParse(spec.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedLine))
            requestedLine = int.MaxValue;
        var line = Math.Clamp(requestedLine, 1, doc.LineCount);
        var column = 1;
        if (spec.Groups["column"].Success && int.TryParse(spec.Groups["column"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedColumn))
            column = Math.Max(1, requestedColumn);
        var title = spec.Groups["column"].Success ? $"Go to line {line}, column {column}" : $"Go to line {line}";
        return [new PaletteCommand(title, "Line", "", () => { GoToLineInActiveEditor(line, column); return Task.CompletedTask; }, () => IsTextEditorVisible && EditorTextBox?.Document is not null, Detail: $"of {doc.LineCount:N0} lines")];
    }

    private void EnsurePaletteSymbols(TextDocument document)
    {
        if (_paletteSymbols is not null || _paletteSymbolsLoading)
            return;

        var filePath = _currentFilePath;
        var languageRules = CurrentLanguageExtension?.LangRules is { HasSymbolAnalyzer: true } rules ? rules : null;
        var hasLsp = !string.IsNullOrWhiteSpace(filePath) && ResolveLspExtensionForFile(filePath) is { HasLsp: true };
        if (languageRules is null && !hasLsp)
        {
            _paletteSymbols = [];
            return;
        }

        _paletteSymbolsLoading = true;
        var revision = EditorSession.Revision;
        var revisionToken = EditorSession.RevisionCancellationToken;
        var snapshot = EditorTextSnapshotStore.GetSnapshot(document);
        var encoding = hasLsp ? GetLspClientForFile(filePath!)?.PositionEncoding ?? "utf-16" : "utf-16";
        _ = LoadPaletteSymbolsAsync(document, snapshot, revision, revisionToken, filePath, languageRules, hasLsp, encoding, _paletteSymbolsVersion);
    }

    private void EnsurePaletteGitBranches()
    {
        if (_paletteGitBranches is not null || _paletteGitBranchesLoading)
            return;
        if (!IsGitSourceControlEnabled || _gitSourceControlIntegration is null)
        {
            _paletteGitBranches = [];
            return;
        }

        _paletteGitBranchesLoading = true;
        var version = _paletteGitBranchesVersion;
        _ = LoadPaletteGitBranchesAsync(version);
    }

    private async Task LoadPaletteGitBranchesAsync(int version)
    {
        try
        {
            var branches = await _gitSourceControlIntegration!.LoadBranchesForPaletteAsync().ConfigureAwait(false);
            await ApplyPaletteGitBranchesAsync(version, branches).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Palette branch list load failed.", ex);
            await ApplyPaletteGitBranchesAsync(version, []).ConfigureAwait(false);
        }
    }

    private async Task ApplyPaletteGitBranchesAsync(int version, IReadOnlyList<GitBranch> branches)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _paletteGitBranchesVersion || _commandPaletteOverlay is null)
                return;
            _paletteGitBranches = branches.OrderBy(branch => branch.IsRemote).ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _paletteGitBranchesLoading = false;
            RefreshCommandPaletteResults();
        });
    }

    private List<PaletteCommand> BuildGitBranchCommands()
    {
        EnsurePaletteGitBranches();
        var commands = new List<PaletteCommand>();
        commands.Add(new PaletteCommand("Switch Branch.", "Git", "",
            () => { SetPaletteText("> Switch Branch:"); return Task.CompletedTask; },
            () => IsGitSourceControlEnabled && _gitSourceControlIntegration is not null,
            "git branch checkout switch", Section: "Git", KeepOpen: true));

        if (_paletteGitBranches is null)
            return commands;

        foreach (var branch in _paletteGitBranches.Take(PaletteMaxResults))
        {
            var captured = branch;
            commands.Add(new PaletteCommand($"Switch Branch: {branch.Name}", "Git", "",
                () => SwitchGitBranchFromPaletteAsync(captured),
                () => IsGitSourceControlEnabled && _gitSourceControlIntegration is not null,
                "git branch checkout",
                branch.IsCurrent ? "current branch" : string.Empty,
                Section: "Git",
                Badge: branch.IsCurrent ? "current" : string.Empty));
        }

        return commands;
    }

    private async Task SwitchGitBranchFromPaletteAsync(GitBranch branch)
    {
        var integration = _gitSourceControlIntegration;
        if (integration is null) return;
        _paletteGitBranchesVersion++;
        _paletteGitBranches = null;
        _paletteGitBranchesLoading = false;
        await integration.SwitchGitBranchAsync(branch).ConfigureAwait(false);
    }

    private async Task LoadPaletteSymbolsAsync(
        TextDocument document,
        EditorTextSnapshot snapshot,
        long revision,
        CancellationToken revisionToken,
        string? filePath,
        LangRulesAdapter? languageRules,
        bool hasLsp,
        string encoding,
        int version)
    {
        try
        {
            var text = await snapshot.GetTextAsync(revisionToken).ConfigureAwait(false);
            if (languageRules is not null)
            {
                var found = await Task.Run(() => languageRules.AnalyzeSymbols(text)
                    .Where(symbol => symbol.IsDeclaration && !string.IsNullOrWhiteSpace(symbol.Name))
                    .Select(symbol => (Name: symbol.Name, Kind: symbol.Kind ?? string.Empty, Offset: Math.Clamp(symbol.Start, 0, text.Length)))
                    .Distinct()
                    .OrderBy(symbol => symbol.Offset)
                    .ToList(), revisionToken).ConfigureAwait(false);
                if (found.Count > 0)
                {
                    await ApplyPaletteSymbolsAsync(document, revision, version, found).ConfigureAwait(false);
                    return;
                }
            }

            if (hasLsp && !string.IsNullOrWhiteSpace(filePath))
            {
                await LoadPaletteLspSymbolsAsync(document, revision, revisionToken, filePath, text, encoding, version).ConfigureAwait(false);
                return;
            }
            await ApplyPaletteSymbolsAsync(document, revision, version, []).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _paletteSymbolsVersion || _commandPaletteOverlay is null)
                    return;
                _paletteSymbolsLoading = false;
                _paletteSymbols = null;
                RefreshCommandPaletteResults();
            });
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Palette symbol analysis failed.", ex);
            await ApplyPaletteSymbolsAsync(document, revision, version, []).ConfigureAwait(false);
        }
    }

    private async Task LoadPaletteLspSymbolsAsync(TextDocument document, long revision, CancellationToken revisionToken, string filePath, string text, string encoding, int version)
    {
        var symbols = new List<(string Name, string Kind, int Offset)>();
        try
        {
            var result = await GetLspDocumentSymbolsAsync(filePath);
            if (result is { ValueKind: JsonValueKind.Array } array)
            {
                void AddSymbols(JsonElement items)
                {
                    foreach (var symbol in items.EnumerateArray())
                    {
                        if (!symbol.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                            continue;
                        var range = symbol.TryGetProperty("selectionRange", out var selection) ? selection
                            : symbol.TryGetProperty("range", out var fallback) ? fallback
                            : symbol.TryGetProperty("location", out var location) && location.TryGetProperty("range", out var locationRange) ? locationRange
                            : default;
                        if (range.ValueKind == JsonValueKind.Object && range.TryGetProperty("start", out var start) &&
                            TryReadLspPosition(start, out var symbolLine, out var symbolCharacter))
                        {
                            var kind = symbol.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.Number &&
                                       kindElement.TryGetInt32(out var kindValue) && kindValue > 0 && kindValue < LspSymbolKindNames.Length
                                ? LspSymbolKindNames[kindValue]
                                : string.Empty;
                            symbols.Add((name.GetString() ?? string.Empty, kind, LspPath.OffsetFromLspPosition(text, symbolLine, symbolCharacter, encoding)));
                        }
                        if (symbol.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                            AddSymbols(children);
                    }
                }
                AddSymbols(array);
            }
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug("Palette LSP symbol request failed.", ex);
        }

        await ApplyPaletteSymbolsAsync(document, revision, version, symbols.Where(symbol => symbol.Name.Length > 0).OrderBy(symbol => symbol.Offset).ToList(), revisionToken);
    }

    private async Task ApplyPaletteSymbolsAsync(TextDocument document, long revision, int version, List<(string Name, string Kind, int Offset)> symbols, CancellationToken revisionToken = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _paletteSymbolsVersion || _commandPaletteOverlay is null)
                return;
            if (revisionToken.IsCancellationRequested || !ReferenceEquals(EditorTextBox?.Document, document) || EditorSession.Revision != revision)
            {
                _paletteSymbols = null;
                _paletteSymbolsLoading = false;
                RefreshCommandPaletteResults();
                return;
            }
            _paletteSymbols = symbols;
            _paletteSymbolsLoading = false;
            RefreshCommandPaletteResults();
        });
    }

    private List<PaletteCommand> BuildSymbolCommands(string query, out string? message, out int totalMatches)
    {
        message = null;
        totalMatches = 0;
        if (!IsTextEditorVisible || ActiveEditorTab is null || EditorTextBox?.Document is not { } doc)
        {
            message = "Open a text file to go to a symbol.";
            return [];
        }

        EnsurePaletteSymbols(doc);
        if (_paletteSymbols is null)
        {
            message = "Loading symbols…";
            return [];
        }
        if (_paletteSymbols.Count == 0)
        {
            message = "No symbols were found in this file.";
            return [];
        }

        var terms = SplitPaletteTerms(query);
        var scored = new List<(PaletteCommand Command, int Score)>();
        foreach (var (name, kind, offset) in _paletteSymbols)
        {
            var lineNumber = doc.GetLineByOffset(Math.Clamp(offset, 0, doc.TextLength)).LineNumber;
            var matches = new List<int>();
            var score = 0;
            foreach (var term in terms)
            {
                if (!TryPaletteMatch(term, name, out var termScore, out var termIndices))
                {
                    score = int.MinValue;
                    break;
                }
                score += termScore;
                matches.AddRange(termIndices);
            }
            if (score == int.MinValue)
                continue;

            var symbolOffset = offset;
            var detail = kind.Length > 0 ? $"{kind}  ·  line {lineNumber:N0}" : $"line {lineNumber:N0}";
            scored.Add((new PaletteCommand(name, "Symbol", "", () => { GoToOffsetInActiveEditor(symbolOffset); return Task.CompletedTask; },
                Detail: detail, TitleMatches: matches), score));
        }

        totalMatches = scored.Count;
        var ordered = terms.Length == 0 ? scored : scored.OrderByDescending(item => item.Score).ToList();
        return ordered.Take(PaletteMaxResults * 2).Select(item => item.Command).ToList();
    }

    private void RefreshCommandPaletteResults()
    {
        if (_commandPaletteList is null || _commandPaletteSearchBox is null) return;

        var mode = GetPaletteMode(_commandPaletteSearchBox.Text, out var query);
        UpdatePaletteShortcutText();
        var showRarePreview = ShowRareCommandPalettePreviewThisSession && string.IsNullOrEmpty(_commandPaletteSearchBox.Text);
        IsCommandPaletteRarePreviewVisible = showRarePreview;
        if (_commandPalettePreviewText is not null)
            _commandPalettePreviewText.IsVisible = showRarePreview;
        _commandPaletteSearchBox.PlaceholderText = showRarePreview ? string.Empty : mode switch
        {
            PaletteMode.Commands => "Show and Run Commands",
            PaletteMode.Symbols => "Go to Symbol in File",
            PaletteMode.Line => "Go to Line",
            _ => "Search files by name (: line, @ symbol, > commands)",
        };

        string? message = null;
        var totalMatches = 0;
        List<PaletteCommand> entries;
        if (IsKodoTaglineQuery(_commandPaletteSearchBox.Text))
        {
            totalMatches = 0;
            entries = [];
            message = "Code fast, stay light";
        }
        else switch (mode)
            {
                case PaletteMode.Commands:
                    _commandPaletteCommands = BuildCommandPaletteCommands();
                    entries = BuildCommandModeEntries(query, out totalMatches);
                    break;
                case PaletteMode.Symbols:
                    entries = BuildSymbolCommands(query, out message, out totalMatches);
                    break;
                case PaletteMode.Line:
                    entries = BuildLineCommands(query, out message);
                    break;
                default:
                    entries = BuildQuickAccessFileCommands(query, out totalMatches);
                    break;
            }
        _filteredPaletteCommands = entries;

        var list = _commandPaletteList;
        list.Children.Clear();
        _paletteRows.Clear();
        _palettePressedIndex = -1;
        var lastSection = string.Empty;
        for (var i = 0; i < _filteredPaletteCommands.Count; i++)
        {
            var command = _filteredPaletteCommands[i];
            if (command.Section.Length > 0 && !string.Equals(command.Section, lastSection, StringComparison.Ordinal))
            {
                lastSection = command.Section;
                list.Children.Add(CreatePaletteSectionHeader(command.Section, list.Children.Count == 0));
            }

            var (row, accent) = CreatePaletteCommandRow(command, mode);
            var index = _paletteRows.Count;
            row.PointerMoved += (_, _) =>
            {
                if (_paletteSelectedIndex != index)
                    SetPaletteSelection(index, scrollIntoView: false);
            };
            row.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                {
                    _palettePressedIndex = index;
                    SetPaletteSelection(index, scrollIntoView: false);
                }
            };
            row.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || _palettePressedIndex != index)
                    return;
                _palettePressedIndex = -1;
                SetPaletteSelection(index, scrollIntoView: false);
            };
            row.Click += async (_, _) =>
            {
                if (_paletteSelectedIndex != index)
                    SetPaletteSelection(index, scrollIntoView: false);
                await ExecuteSelectedPaletteCommandAsync();
            };
            list.Children.Add(row);
            _paletteRows.Add((row, accent));
        }

        if (_paletteRows.Count == 0)
        {
            var text = message ?? mode switch
            {
                PaletteMode.Commands => "No matching commands",
                PaletteMode.Files when !IsFolderOpen && _quickAccessOpenEntries.Count == 0 && _quickAccessRecentEntries.Count == 0
                    => "No matching files. Open a folder to search its files.",
                PaletteMode.Files => _quickAccessIndexing ? "Indexing files…" : "No matching files",
                _ => "No results",
            };
            list.Children.Add(new TextBlock { Text = text, Foreground = MutedTextBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 14) });
        }

        _paletteSelectedIndex = -1;
        SetPaletteSelection(0, scrollIntoView: false);
        if (_commandPaletteScroll is not null)
            _commandPaletteScroll.Offset = new Vector(0, 0);

        string? statusText = null;
        if (mode == PaletteMode.Files && _quickAccessIndexing)
            statusText = "Indexing workspace files…";
        else if (_paletteRows.Count > 0 && totalMatches > _filteredPaletteCommands.Count(entry => entry.Section is "" or "Files"))
            statusText = $"Showing the first {PaletteMaxResults:N0} of {totalMatches:N0} results. Keep typing to narrow it down.";
        if (_commandPaletteStatus is not null)
        {
            _commandPaletteStatus.Text = statusText ?? string.Empty;
            _commandPaletteStatus.IsVisible = statusText is not null;
        }
    }

    private Control CreatePaletteSectionHeader(string section, bool first) =>
        new TextBlock
        {
            Text = section.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            Margin = new Thickness(10, first ? 4 : 10, 10, 4),
        };

    private void AppendHighlightedRuns(Avalonia.Controls.Documents.InlineCollection inlines, string text, IReadOnlyList<int>? matches, IBrush? foreground, double fontSize)
    {
        Avalonia.Controls.Documents.Run MakeRun(string value, bool highlighted)
        {
            var run = new Avalonia.Controls.Documents.Run(value);
            if (foreground is not null) run.Foreground = foreground;
            if (fontSize > 0) run.FontSize = fontSize;
            if (highlighted)
            {
                run.FontWeight = FontWeight.Bold;
                run.TextDecorations = TextDecorations.Underline;
            }
            return run;
        }

        if (matches is not { Count: > 0 })
        {
            inlines.Add(MakeRun(text, false));
            return;
        }

        var matched = new HashSet<int>(matches);
        var start = 0;
        while (start < text.Length)
        {
            var highlighted = matched.Contains(start);
            var end = start + 1;
            while (end < text.Length && matched.Contains(end) == highlighted)
                end++;
            inlines.Add(MakeRun(text[start..end], highlighted));
            start = end;
        }
    }

    private (Button Row, Border Accent) CreatePaletteCommandRow(PaletteCommand command, PaletteMode mode)
    {
        var isFileResult = command.Category == "File";
        var accent = new Border
        {
            Width = 3,
            Margin = new Thickness(0, 6, 8, 6),
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
        };

        var detail = mode == PaletteMode.Commands ? command.Category : command.Detail;

        TextBlock CreateText(string value, IReadOnlyList<int>? matches, IBrush? foreground, double fontSize, FontWeight weight)
        {
            var inlines = new Avalonia.Controls.Documents.InlineCollection();
            AppendHighlightedRuns(inlines, value, matches, foreground, fontSize);
            return new TextBlock
            {
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = foreground ?? PrimaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Inlines = inlines,
            };
        }

        Control text;
        if (isFileResult && detail.Length > 0)
        {
            text = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    CreateText(command.Title, command.TitleMatches, null, 13, FontWeight.Medium),
                    CreateText(detail, command.DetailMatches, MutedTextBrush, 11, FontWeight.Normal),
                },
            };
        }
        else
        {
            var inlines = new Avalonia.Controls.Documents.InlineCollection();
            AppendHighlightedRuns(inlines, command.Title, command.TitleMatches, null, 0);
            if (detail.Length > 0)
            {
                inlines.Add(new Avalonia.Controls.Documents.Run("   ") { FontSize = 11 });
                AppendHighlightedRuns(inlines, detail, command.DetailMatches, MutedTextBrush, 11);
            }
            text = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = PrimaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Inlines = inlines,
            };
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(accent);
        Grid.SetColumn(accent, 0);
        if (command.Category == "File")
        {
            var icon = new TextBlock
            {
                Text = FileTreeItem.GetFileIcon(command.Title),
                FontSize = 10,
                FontFamily = new FontFamily("JetBrains Mono,DejaVu Sans Mono,Ubuntu Mono,Noto Sans Mono,Cascadia Code,Consolas,Menlo,Monospace"),
                Foreground = MutedTextBrush,
                Width = 32,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            grid.Children.Add(icon);
            Grid.SetColumn(icon, 1);
        }
        grid.Children.Add(text);
        Grid.SetColumn(text, 2);

        var shortcut = !string.IsNullOrWhiteSpace(command.ShortcutId) && !_disabledKeybinds.Contains(command.ShortcutId) && _keybinds.TryGetValue(command.ShortcutId, out var gesture)
            ? FormatGesture(gesture)
            : command.Badge;
        if (shortcut.Length > 0)
        {
            var keycap = new Border
            {
                Background = WindowBackgroundBrush,
                BorderBrush = SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = shortcut, FontSize = 10, Foreground = MutedTextBrush },
            };
            grid.Children.Add(keycap);
            Grid.SetColumn(keycap, 3);
        }

        var row = new Button
        {
            MinHeight = isFileResult && detail.Length > 0 ? 42 : 30,
            Padding = new Thickness(2, isFileResult && detail.Length > 0 ? 3 : 0, 10, isFileResult && detail.Length > 0 ? 3 : 0),
            CornerRadius = KodoDesignTokens.CompactRadius,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = grid,
            Tag = command,
        };
        Avalonia.Automation.AutomationProperties.SetName(row, command.Title);
        return (row, accent);
    }

    private void SetPaletteSelection(int index, bool scrollIntoView = true)
    {
        if (_paletteRows.Count == 0)
        {
            _paletteSelectedIndex = -1;
            return;
        }

        _paletteSelectedIndex = Math.Clamp(index, 0, _paletteRows.Count - 1);
        StyleCommandPaletteRows();
        if (!scrollIntoView)
            return;
        if (_paletteSelectedIndex == 0 && _commandPaletteScroll is not null)
            _commandPaletteScroll.Offset = new Vector(0, 0);
        else
            _paletteRows[_paletteSelectedIndex].Row.BringIntoView();
    }

    private void MovePaletteSelection(int delta, bool wrap = true)
    {
        var count = _paletteRows.Count;
        if (count == 0) return;
        var next = _paletteSelectedIndex < 0 ? (delta > 0 ? 0 : count - 1) : _paletteSelectedIndex + delta;
        next = wrap ? ((next % count) + count) % count : Math.Clamp(next, 0, count - 1);
        SetPaletteSelection(next);
    }

    private void StyleCommandPaletteRows()
    {
        for (var i = 0; i < _paletteRows.Count; i++)
        {
            var selected = i == _paletteSelectedIndex;
            _paletteRows[i].Row.Background = selected ? ButtonHoverBrush : Brushes.Transparent;
            _paletteRows[i].Accent.Background = selected ? AccentBrush : Brushes.Transparent;
        }
    }

    private void CommandPaletteSearch_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_commandPaletteOverlay is not { IsVisible: true }) return;
        switch (e.Key)
        {
            case Key.Escape:
                DismissCommandPalette();
                e.Handled = true;
                break;
            case Key.Down:
                MovePaletteSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MovePaletteSelection(8, wrap: false);
                e.Handled = true;
                break;
            case Key.PageUp:
                MovePaletteSelection(-8, wrap: false);
                e.Handled = true;
                break;
            case Key.Tab:
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                _ = ExecuteSelectedPaletteCommandAsync();
                break;
        }
    }

    private bool HandlePaletteHotkey(KeyEventArgs e)
    {
        var mode = GetPaletteMode(_commandPaletteSearchBox?.Text, out _);
        if (MatchesKeybind(e, "CommandPalette"))
        {
            if (mode == PaletteMode.Commands) DismissCommandPalette();
            else SetPaletteText(">");
            return true;
        }
        if (MatchesKeybind(e, "QuickAccess"))
        {
            if (mode == PaletteMode.Files) DismissCommandPalette();
            else SetPaletteText(string.Empty);
            return true;
        }
        return false;
    }

    private async Task ExecuteSelectedPaletteCommandAsync()
    {
        if (_commandPaletteOverlay is null || _paletteSelectedIndex < 0 || _paletteSelectedIndex >= _filteredPaletteCommands.Count)
            return;

        var command = _filteredPaletteCommands[_paletteSelectedIndex];
        if (command.IsAvailable?.Invoke() == false)
        {
            RefreshCommandPaletteResults();
            return;
        }
        var mode = GetPaletteMode(_commandPaletteSearchBox?.Text, out _);
        try
        {
            if (command.KeepOpen)
            {
                await command.Execute();
                RefreshCommandPaletteResults();
                return;
            }

            if (mode == PaletteMode.Commands)
            {
                _recentPaletteCommands.Remove(command.Title);
                _recentPaletteCommands.Insert(0, command.Title);
                if (_recentPaletteCommands.Count > PaletteMaxRecentCommands)
                    _recentPaletteCommands.RemoveRange(PaletteMaxRecentCommands, _recentPaletteCommands.Count - PaletteMaxRecentCommands);
            }

            DismissCommandPalette(restoreFocus: false);
            ReleasePaletteFocus();
            await command.Execute();
            ReleasePaletteFocus();
            Dispatcher.UIThread.Post(ReleasePaletteFocus, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            KodoDiagnostics.LogDebug($"Command palette entry '{command.Title}' failed.", ex);
        }
    }

    private void SetKeybindEnabled(string id, bool enabled)
    {
        if (enabled) _disabledKeybinds.Remove(id);
        else _disabledKeybinds.Add(id);
        SaveSettings();
    }

    private static string FormatGesture(KeyGesture gesture)
    {
        var parts = new List<string>();
        if ((gesture.KeyModifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((gesture.KeyModifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((gesture.KeyModifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((gesture.KeyModifiers & KeyModifiers.Meta) != 0)
            parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : OperatingSystem.IsLinux() ? "Meta" : "Win");
        parts.Add(FormatKey(gesture.Key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key) => key switch
    {
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.Oem3 => "`",
        Key.OemPipe => "\\",
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        _ => key.ToString(),
    };

    private string ShortcutText(string id) =>
        _keybinds.TryGetValue(id, out var gesture) ? FormatGesture(gesture) : string.Empty;

    public string NewFileTooltip => $"New file ({ShortcutText("NewFile")})";
    public string OpenFileTooltip => $"Open file ({ShortcutText("OpenFile")})";
    public string OpenFolderTooltip => $"Open folder ({ShortcutText("OpenFolder")})";
    public string CloseFolderTooltip => $"Close folder ({ShortcutText("CloseFolder")})";
    public string ToggleExplorerTooltip => $"Toggle Explorer ({ShortcutText("ToggleFileExplorer")})";
    public string OpenExtensionsTooltip => $"Marketplace ({ShortcutText("OpenExtensions")})";
    public string SaveTooltip => $"Save ({ShortcutText("Save")})";
    public string SaveAsTooltip => $"Save As ({ShortcutText("SaveAs")})";
    public string ToggleTerminalTooltip => $"Toggle terminal ({ShortcutText("ToggleTerminal")})";
    public string NewTerminalTooltip => $"New terminal ({ShortcutText("NewTerminalSession")})";
    public string FindInFileTooltip => $"Find in file ({ShortcutText("FindInFile")})";
    public string FindInProjectTooltip => $"Find in project ({ShortcutText("FindInProject")})";
    public string SettingsTooltip => $"Settings ({ShortcutText("OpenSettings")})";
    public string HomeTooltip => $"Home ({ShortcutText("GoHome")})";
    public string EditorTooltip => $"Editor ({ShortcutText("GoEditor")})";
    public string ZoomOutTooltip => $"Zoom out ({ShortcutText("ZoomOut")})";
    public string ZoomResetTooltip => $"Reset zoom ({ShortcutText("ZoomReset")})";
    public string ZoomInTooltip => $"Zoom in ({ShortcutText("ZoomIn")})";
    public string QuickAccessShortcutText
    {
        get
        {
            var id = _commandPaletteSearchBox?.Text?.TrimStart().StartsWith('>') == true
                ? "CommandPalette"
                : "QuickAccess";
            return IsKeybindEnabled(id) ? ShortcutText(id) : string.Empty;
        }
    }

    private bool _isQuickAccessSearchFocused;
    public bool IsQuickAccessSearchFocused
    {
        get => _isQuickAccessSearchFocused;
        private set
        {
            if (_isQuickAccessSearchFocused == value) return;
            _isQuickAccessSearchFocused = value;
            OnPropertyChanged(nameof(QuickAccessSearchBorderBrush));
        }
    }

    public IBrush QuickAccessSearchBorderBrush => IsQuickAccessSearchFocused ? AccentBrush : SurfaceBorderBrush;

}
