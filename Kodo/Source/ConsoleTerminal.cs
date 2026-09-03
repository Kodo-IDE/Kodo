// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;

namespace Kodo;

public sealed class ConsoleTerminal : Control
{
    private static readonly Color[] AnsiPalette =
    [
        Color.FromRgb(12,  12,  12),
        Color.FromRgb(197, 15,  31),
        Color.FromRgb(19,  161, 14),
        Color.FromRgb(193, 156, 0),
        Color.FromRgb(0,   55,  218),
        Color.FromRgb(136, 23,  152),
        Color.FromRgb(58,  150, 221),
        Color.FromRgb(204, 204, 204),
        Color.FromRgb(118, 118, 118),
        Color.FromRgb(231, 72,  86),
        Color.FromRgb(22,  198, 12),
        Color.FromRgb(249, 241, 165),
        Color.FromRgb(59,  120, 255),
        Color.FromRgb(180, 0,   158),
        Color.FromRgb(97,  214, 214),
        Color.FromRgb(242, 242, 242),
    ];

    private static readonly Color DefaultFg = Color.FromRgb(204, 204, 204);
    private static readonly Color DefaultBg = Color.FromRgb(12, 12, 12);

    private const double CellW = 8.4;
    private const double CellH = 17.0;
    private const string FontFamily = "Cascadia Mono,Consolas,Courier New,monospace";
    private const double FontSize = 13.0;

    private readonly object _lock = new();
    private TermCell[,] _cells = new TermCell[24, 80];
    private int _rows = 24, _cols = 80;
    private int _cursorRow, _cursorCol;
    private bool _cursorVisible = true;

    private const int MaxScrollbackLines = 5000;
    private readonly List<TermCell[]> _scrollback = new();
    private int _scrollOffset;

    private Color _fg = DefaultFg, _bg = DefaultBg;
    private bool _bold, _underline, _reverse;

    private static readonly Color SelectionBg = Color.FromArgb(120, 51, 153, 255);
    private bool _selecting;
    private (int Row, int Col)? _selStart;
    private (int Row, int Col)? _selEnd;

    private bool _bracketedPasteMode;

    private static readonly Color SearchMatchBg = Color.FromArgb(140, 255, 213, 79);
    private static readonly Color SearchCurrentBg = Color.FromArgb(200, 255, 140, 0);
    private bool _searchActive;
    private readonly StringBuilder _searchQuery = new();
    private readonly List<(int AbsRow, int Col)> _searchMatches = new();
    private int _searchIndex = -1;

    public enum ParseState { Ground, Escape, CsiEntry, CsiParam, CsiIgnore, OscString, OscStringEsc }
    private ParseState _parseState = ParseState.Ground;
    private readonly StringBuilder _csiParam = new();
    private readonly StringBuilder _oscBuf = new();

    private IntPtr _hPcon = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;
    private Stream? _writeStream;
    private Stream? _readStream;
    private CancellationTokenSource? _cts;

    private long _suppressOutputUntilTick;

    private readonly DispatcherTimer _blinkTimer;
    private bool _cursorBlinkOn = true;

    public ConsoleTerminal()
    {
        Focusable = true;
        ClipToBounds = true;

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) => { _cursorBlinkOn = !_cursorBlinkOn; InvalidateVisual(); };
        _blinkTimer.Start();

        AttachedToVisualTree += (_, _) => Focus();
    }

    public event EventHandler<IntPtr>? SessionExited;

    public event EventHandler<string>? TitleChanged;

    public event EventHandler<string>? WorkingDirectoryChanged;

    public IReadOnlyDictionary<string, KeyGesture>? Keybinds { get; set; }

    private static readonly KeyGesture DefaultCopy = new(Key.C, KeyModifiers.Control | KeyModifiers.Shift);
    private static readonly KeyGesture DefaultPaste = new(Key.V, KeyModifiers.Control);
    private static readonly KeyGesture DefaultSearch = new(Key.F, KeyModifiers.Control);

    public void Start(string shellPath, string arguments, string workingDirectory,
                      bool suppressOutputUntilRestored = false)
    {
        Stop();
        _cts = new CancellationTokenSource();

        try
        {
            var (cols, rows) = CalcSize();
            _cols = cols; _rows = rows;
            if (!suppressOutputUntilRestored)
            {
                ResizeCells(rows, cols);
                _scrollback.Clear();
                _scrollOffset = 0;
            }

            NativeConPty.CreatePipe(out var hReadPtyOutput, out var hWritePtyOutput, IntPtr.Zero, 0);
            NativeConPty.CreatePipe(out var hReadPtyInput, out var hWritePtyInput, IntPtr.Zero, 0);

            var result = NativeConPty.CreatePseudoConsole(
                new NativeConPty.COORD { X = (short)cols, Y = (short)rows },
                hReadPtyInput, hWritePtyOutput,
                0, out _hPcon);

            if (result != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{result:X}");

            NativeConPty.CloseHandle(hReadPtyInput);
            NativeConPty.CloseHandle(hWritePtyOutput);

            _writeStream = new FileStream(
                new Microsoft.Win32.SafeHandles.SafeFileHandle(hWritePtyInput, true), FileAccess.Write);
            _readStream = new FileStream(
                new Microsoft.Win32.SafeHandles.SafeFileHandle(hReadPtyOutput, true), FileAccess.Read);

            var cmdLine = new StringBuilder($"\"{shellPath}\" {arguments}");
            NativeConPty.LaunchProcess(cmdLine, workingDirectory, _hPcon,
                out _hProcess, out _hThread);

            _suppressOutputUntilTick = suppressOutputUntilRestored
                ? Environment.TickCount64 + 500
                : 0;

            _ = Task.Run(() => ReadOutputLoop(_cts.Token), _cts.Token);

            var watchedHandle = _hProcess;
            _ = Task.Run(() =>
            {
                NativeConPty.WaitForSingleObject(watchedHandle, 0xFFFFFFFF);
                Dispatcher.UIThread.Post(() =>
                {
                    SessionExited?.Invoke(this, watchedHandle);
                });
            }, _cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConPTY] Start failed: {ex}");
        }
    }

    public void Stop()
    {
        _suppressOutputUntilTick = 0;
        _cts?.Cancel();
        _cts = null;

        try { _writeStream?.Dispose(); } catch { }
        try { _readStream?.Dispose(); } catch { }
        _writeStream = null;
        _readStream = null;

        if (_hPcon != IntPtr.Zero)
        {
            NativeConPty.ClosePseudoConsole(_hPcon);
            _hPcon = IntPtr.Zero;
        }
        if (_hProcess != IntPtr.Zero) { NativeConPty.CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
        if (_hThread != IntPtr.Zero) { NativeConPty.CloseHandle(_hThread); _hThread = IntPtr.Zero; }
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || (cols == _cols && rows == _rows))
            return;

        _cols = cols; _rows = rows;
        ResizeCells(rows, cols);
        _scrollOffset = 0;

        if (_hPcon != IntPtr.Zero)
            NativeConPty.ResizePseudoConsole(_hPcon,
                new NativeConPty.COORD { X = (short)cols, Y = (short)rows });
    }

    public void SendInput(string text)
    {
        if (_writeStream is null || string.IsNullOrEmpty(text)) return;
        _scrollOffset = 0;
        var bytes = Encoding.UTF8.GetBytes(text);
        try { _writeStream.Write(bytes, 0, bytes.Length); _writeStream.Flush(); }
        catch (Exception ex) { Console.WriteLine($"[ConPTY] SendInput failed: {ex.Message}"); }
    }

    public void SendKey(Key key, KeyModifiers mods)
    {
        var seq = KeyToVt(key, mods);
        if (seq is not null) SendInput(seq);
    }

    public bool HasLiveProcess => _hPcon != IntPtr.Zero;

    public IntPtr CurrentProcessHandle => _hProcess;

    public TerminalSnapshot SaveSnapshot()
    {
        lock (_lock)
        {
            var cells = (TermCell[,])_cells.Clone();
            return new TerminalSnapshot(
                cells, _rows, _cols,
                _cursorRow, _cursorCol, _cursorVisible,
                _fg, _bg, _bold, _underline, _reverse,
                _parseState, _csiParam.ToString());
        }
    }

    public void RestoreSnapshot(TerminalSnapshot snap)
    {
        lock (_lock)
        {
            var copyR = Math.Min(_rows, snap.Rows);
            var copyC = Math.Min(_cols, snap.Cols);
            var newCells = new TermCell[_rows, _cols];
            for (var r = 0; r < copyR; r++)
                for (var c = 0; c < copyC; c++)
                    newCells[r, c] = snap.Cells[r, c];
            _cells = newCells;

            _cursorRow = Math.Min(snap.CursorRow, _rows - 1);
            _cursorCol = Math.Min(snap.CursorCol, _cols - 1);
            _cursorVisible = snap.CursorVisible;
            _fg = snap.Fg;
            _bg = snap.Bg;
            _bold = snap.Bold;
            _underline = snap.Underline;
            _reverse = snap.Reverse;
            _parseState = snap.ParseState;
            _csiParam.Clear();
            _csiParam.Append(snap.CsiParam);
            _scrollOffset = 0;
        }
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_searchActive)
        {
            HandleSearchKeyDown(e);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        if (MatchesTerminalKeybind(e, "TerminalCopy", DefaultCopy))
        {
            _ = CopySelectionToClipboardAsync();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        if (MatchesTerminalKeybind(e, "TerminalPaste", DefaultPaste))
        {
            _ = PasteFromClipboardAsync();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        if (MatchesTerminalKeybind(e, "TerminalSearch", DefaultSearch))
        {
            OpenSearch();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        var seq = KeyToVt(e.Key, e.KeyModifiers);
        if (seq is not null)
        {
            SendInput(seq);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var ch = ControlChar(e.Key);
            if (ch.HasValue)
            {
                SendInput(((char)ch.Value).ToString());
                e.Handled = true;
                base.OnKeyDown(e);
                return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Alt)
        {
            var altChar = AltKeyChar(e.Key);
            if (altChar is not null)
            {
                SendInput("\x1b" + altChar);
                e.Handled = true;
                base.OnKeyDown(e);
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private bool MatchesTerminalKeybind(KeyEventArgs e, string id, KeyGesture fallback)
    {
        var gesture = fallback;
        if (Keybinds is not null && Keybinds.TryGetValue(id, out var custom))
            gesture = custom;

        if (gesture.KeyModifiers == KeyModifiers.None &&
            (gesture.Key < Key.F1 || gesture.Key > Key.F24))
        {
            return false;
        }

        return e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (_searchActive)
        {
            if (!string.IsNullOrEmpty(e.Text)) { _searchQuery.Append(e.Text); RunSearch(); }
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text)) { SendInput(e.Text); e.Handled = true; }
    }

    protected override Size MeasureOverride(Size available) => available;

    protected override Size ArrangeOverride(Size final)
    {
        if (Environment.TickCount64 >= _suppressOutputUntilTick)
        {
            var (cols, rows) = CalcSize(final);
            Resize(cols, rows);
        }
        return final;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var pos = PointToCell(e.GetPosition(this));
            _selStart = _selEnd = pos;
            _selecting = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        _selEnd = PointToCell(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
    }

    private (int Row, int Col) PointToCell(Point p)
    {
        var screenRow = Math.Clamp((int)(p.Y / CellH), 0, _rows - 1);
        var col = Math.Clamp((int)(p.X / CellW), 0, _cols - 1);
        return (ScreenRowToAbsRow(screenRow), col);
    }

    private const int WheelLinesPerNotch = 3;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = (int)Math.Round(e.Delta.Y * WheelLinesPerNotch);
        if (delta == 0) return;

        lock (_lock)
        {
            _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, _scrollback.Count);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private static readonly Typeface TypefaceNormal = new(FontFamily, FontStyle.Normal, FontWeight.Regular);
    private static readonly Typeface TypefaceBold = new(FontFamily, FontStyle.Normal, FontWeight.Bold);

    public override void Render(DrawingContext ctx)
    {
        lock (_lock)
        {
            ctx.FillRectangle(new SolidColorBrush(DefaultBg), new Rect(Bounds.Size));

            var isCursorVisible = _scrollOffset == 0 && _cursorVisible && _cursorBlinkOn;
            var scrollbackStart = _scrollback.Count - _scrollOffset;

            (int r0, int c0, int r1, int c1)? selRange = null;
            if (_selStart is not null && _selEnd is not null)
            {
                var (sr0, sc0) = _selStart.Value;
                var (sr1, sc1) = _selEnd.Value;
                if (sr0 > sr1 || (sr0 == sr1 && sc0 > sc1)) (sr0, sc0, sr1, sc1) = (sr1, sc1, sr0, sc0);
                if (sr0 != sr1 || sc0 != sc1) selRange = (sr0, sc0, sr1, sc1);
            }

            Dictionary<int, List<(int Col, int Len)>>? searchHighlights = null;
            if (_searchActive && _searchMatches.Count > 0)
            {
                searchHighlights = new();
                var qLen = Math.Max(1, _searchQuery.Length);
                foreach (var (absRowM, colM) in _searchMatches)
                {
                    var screenRowM = absRowM - scrollbackStart;
                    if (screenRowM < 0 || screenRowM >= _rows) continue;
                    if (!searchHighlights.TryGetValue(absRowM, out var list))
                        searchHighlights[absRowM] = list = new();
                    list.Add((colM, qLen));
                }
            }

            for (var r = 0; r < _rows; r++)
                for (var c = 0; c < _cols; c++)
                {
                    var cell = GetDisplayCell(r, c, scrollbackStart);
                    var absRow = ScreenRowToAbsRow(r);

                    var x = (int)Math.Round(c * CellW);
                    var x1 = (int)Math.Round((c + 1) * CellW);
                    var y = r * CellH;
                    var w = x1 - x;
                    var rect = new Rect(x, y, w, CellH);

                    var atCursor = isCursorVisible && r == _cursorRow && c == _cursorCol;
                    var selected = selRange is not null && IsCellSelected(absRow, c, selRange.Value);

                    var isMatch = false;
                    var isCurrentMatch = false;
                    if (searchHighlights is not null && searchHighlights.TryGetValue(absRow, out var ranges))
                    {
                        foreach (var (mc, len) in ranges)
                        {
                            if (c < mc || c >= mc + len) continue;
                            isMatch = true;
                            if (_searchIndex >= 0 && _searchMatches[_searchIndex].AbsRow == absRow &&
                                c >= _searchMatches[_searchIndex].Col && c < _searchMatches[_searchIndex].Col + len)
                                isCurrentMatch = true;
                            break;
                        }
                    }

                    var bg = atCursor ? DefaultFg
                           : isCurrentMatch ? SearchCurrentBg
                           : isMatch ? SearchMatchBg
                           : selected ? SelectionBg
                           : (cell.Bg ?? DefaultBg);
                    if (bg != DefaultBg)
                        ctx.FillRectangle(new SolidColorBrush(bg), rect);

                    if (cell.Char != '\0' && cell.Char != ' ')
                    {
                        var fg = atCursor ? DefaultBg : (cell.Fg ?? DefaultFg);
                        var typeface = cell.Bold ? TypefaceBold : TypefaceNormal;

                        var ft = new FormattedText(
                            cell.Char.ToString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            FontSize,
                            new SolidColorBrush(fg));

                        ctx.DrawText(ft, new Point(x, y));
                    }

                    if (cell.Underline)
                    {
                        var fg = cell.Fg ?? DefaultFg;
                        ctx.DrawLine(new Pen(new SolidColorBrush(fg)),
                            new Point(x, y + CellH - 2), new Point(x + w, y + CellH - 2));
                    }
                }

            if (_scrollOffset > 0)
                DrawScrollIndicator(ctx);

            if (_searchActive)
                DrawSearchBar(ctx);
        }
    }

    private void DrawSearchBar(DrawingContext ctx)
    {
        var label = $"Find: {_searchQuery}" +
                    (_searchMatches.Count > 0 ? $"   {_searchIndex + 1}/{_searchMatches.Count}" : "   no matches");
        var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, TypefaceNormal, FontSize, new SolidColorBrush(DefaultFg));

        const int pad = 6;
        var w = ft.Width + pad * 2;
        var h = ft.Height + pad * 2;
        var rect = new Rect(Bounds.Width - w - 10, 6, w, h);

        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)), rect);
        ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90))), rect);
        ctx.DrawText(ft, new Point(rect.X + pad, rect.Y + pad));
    }

    private TermCell GetDisplayCell(int screenRow, int col, int scrollbackStart)
    {
        if (_scrollOffset == 0)
            return _cells[screenRow, col];

        if (screenRow < _scrollOffset)
        {
            var row = _scrollback[scrollbackStart + screenRow];
            return col < row.Length ? row[col] : default;
        }

        var liveRow = screenRow - _scrollOffset;
        return _cells[liveRow, col];
    }

    private void DrawScrollIndicator(DrawingContext ctx)
    {
        var totalLines = _scrollback.Count + _rows;
        var trackH = Bounds.Height;
        var thumbH = Math.Max(20, trackH * _rows / (double)totalLines);
        var thumbY = (trackH - thumbH) * (1 - _scrollOffset / (double)_scrollback.Count);

        var brush = new SolidColorBrush(Color.FromArgb(160, 204, 204, 204));
        ctx.FillRectangle(brush, new Rect(Bounds.Width - 4, thumbY, 4, thumbH));
    }

    private int TotalAbsRows => _scrollback.Count + _rows;

    private int ScreenRowToAbsRow(int screenRow) => _scrollback.Count - _scrollOffset + screenRow;

    private int ColsForAbsRow(int absRow) => absRow < _scrollback.Count ? _scrollback[absRow].Length : _cols;

    private TermCell GetAbsCell(int absRow, int col)
    {
        if (absRow < _scrollback.Count)
        {
            var row = _scrollback[absRow];
            return col < row.Length ? row[col] : default;
        }
        var liveRow = absRow - _scrollback.Count;
        if (liveRow < 0 || liveRow >= _rows || col < 0 || col >= _cols) return default;
        return _cells[liveRow, col];
    }

    private static bool IsCellSelected(int absRow, int col, (int r0, int c0, int r1, int c1) sel)
    {
        if (absRow < sel.r0 || absRow > sel.r1) return false;
        if (sel.r0 == sel.r1) return col >= sel.c0 && col < sel.c1;
        if (absRow == sel.r0) return col >= sel.c0;
        if (absRow == sel.r1) return col < sel.c1;
        return true;
    }

    private string? GetSelectedText()
    {
        if (_selStart is null || _selEnd is null) return null;
        var (r0, c0) = _selStart.Value;
        var (r1, c1) = _selEnd.Value;
        if (r0 > r1 || (r0 == r1 && c0 > c1)) (r0, c0, r1, c1) = (r1, c1, r0, c0);
        if (r0 == r1 && c0 == c1) return null;

        var sb = new StringBuilder();
        for (var row = r0; row <= r1; row++)
        {
            var startCol = row == r0 ? c0 : 0;
            var endCol = row == r1 ? c1 : ColsForAbsRow(row);
            var lineSb = new StringBuilder();
            for (var col = startCol; col < endCol; col++)
            {
                var ch = GetAbsCell(row, col).Char;
                lineSb.Append(ch == '\0' ? ' ' : ch);
            }
            sb.Append(lineSb.ToString().TrimEnd());
            if (row < r1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private async Task CopySelectionToClipboardAsync()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(text); }
        catch (Exception ex) { Console.WriteLine($"[Terminal] Copy failed: {ex.Message}"); }
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Terminal] Paste failed: {ex.Message}"); return; }
        if (string.IsNullOrEmpty(text)) return;

        text = text.Replace("\r\n", "\r").Replace("\n", "\r");
        SendInput(_bracketedPasteMode ? $"\x1b[200~{text}\x1b[201~" : text);
    }

    private void OpenSearch()
    {
        _searchActive = true;
        _searchQuery.Clear();
        _searchMatches.Clear();
        _searchIndex = -1;
        InvalidateVisual();
    }

    private void CloseSearch()
    {
        _searchActive = false;
        _searchQuery.Clear();
        _searchMatches.Clear();
        _searchIndex = -1;
        InvalidateVisual();
    }

    private void HandleSearchKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                break;
            case Key.Enter:
            case Key.F3:
                JumpToMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.Back:
                if (_searchQuery.Length > 0) _searchQuery.Length--;
                RunSearch();
                break;
        }
    }

    private void RunSearch()
    {
        _searchMatches.Clear();
        _searchIndex = -1;

        var query = _searchQuery.ToString();
        if (query.Length == 0) { InvalidateVisual(); return; }

        for (var absRow = 0; absRow < TotalAbsRows; absRow++)
        {
            var lineLen = ColsForAbsRow(absRow);
            var lineSb = new StringBuilder(lineLen);
            for (var col = 0; col < lineLen; col++)
            {
                var ch = GetAbsCell(absRow, col).Char;
                lineSb.Append(ch == '\0' ? ' ' : ch);
            }

            var line = lineSb.ToString();
            var idx = 0;
            while ((idx = line.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                _searchMatches.Add((absRow, idx));
                idx += Math.Max(1, query.Length);
            }
        }

        if (_searchMatches.Count > 0)
        {
            _searchIndex = _searchMatches.Count - 1;
            ScrollToMatch();
        }
        InvalidateVisual();
    }

    private void JumpToMatch(int direction)
    {
        if (_searchMatches.Count == 0) return;
        _searchIndex = ((_searchIndex + direction) % _searchMatches.Count + _searchMatches.Count) % _searchMatches.Count;
        ScrollToMatch();
        InvalidateVisual();
    }

    private void ScrollToMatch()
    {
        if (_searchIndex < 0 || _searchIndex >= _searchMatches.Count) return;
        var (absRow, _) = _searchMatches[_searchIndex];
        var targetScreenRow = _rows / 2;
        _scrollOffset = Math.Clamp(_scrollback.Count + targetScreenRow - absRow, 0, _scrollback.Count);
    }

    private (int cols, int rows) CalcSize(Size? size = null)
    {
        var s = size ?? Bounds.Size;
        var cols = Math.Max(10, (int)(s.Width / CellW));
        var rows = Math.Max(3, (int)(s.Height / CellH));
        return (cols, rows);
    }

    private void ResizeCells(int rows, int cols)
    {
        lock (_lock)
        {
            var next = new TermCell[rows, cols];
            var copyR = Math.Min(rows, _cells.GetLength(0));
            var copyC = Math.Min(cols, _cells.GetLength(1));
            for (var r = 0; r < copyR; r++)
                for (var c = 0; c < copyC; c++)
                    next[r, c] = _cells[r, c];
            _cells = next;
            _cursorRow = Math.Min(_cursorRow, rows - 1);
            _cursorCol = Math.Min(_cursorCol, cols - 1);
        }
    }

    private async Task ReadOutputLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _readStream!.ReadAsync(buf, 0, buf.Length, ct);
                if (n <= 0) break;
                var text = Encoding.UTF8.GetString(buf, 0, n);
                lock (_lock)
                {
                    if (Environment.TickCount64 >= _suppressOutputUntilTick)
                        foreach (var ch in text) ProcessChar(ch);
                }
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[ConPTY] ReadOutputLoop: {ex.Message}"); }
    }

    private void ProcessChar(char ch)
    {
        switch (_parseState)
        {
            case ParseState.Ground:
                switch (ch)
                {
                    case '\x1B': _parseState = ParseState.Escape; break;
                    case '\r': _cursorCol = 0; break;
                    case '\n': LineFeed(); break;
                    case '\b': if (_cursorCol > 0) _cursorCol--; break;
                    case '\t': _cursorCol = Math.Min(_cols - 1, (_cursorCol / 8 + 1) * 8); break;
                    case '\a': break;
                    case '\x0E': break;
                    case '\x0F': break;
                    default:
                        if (ch >= ' ')
                        {
                            SetCell(_cursorRow, _cursorCol, ch);
                            _cursorCol++;
                            if (_cursorCol >= _cols) { _cursorCol = 0; LineFeed(); }
                        }
                        break;
                }
                break;

            case ParseState.Escape:
                switch (ch)
                {
                    case '[': _csiParam.Clear(); _parseState = ParseState.CsiEntry; break;
                    case ']': _oscBuf.Clear(); _parseState = ParseState.OscString; break;
                    case 'M': ReverseLineFeed(); _parseState = ParseState.Ground; break;
                    case 'c': ResetTerminal(); _parseState = ParseState.Ground; break;
                    default: _parseState = ParseState.Ground; break;
                }
                break;

            case ParseState.CsiEntry:
            case ParseState.CsiParam:
                if (ch == '?' || ch == '>' || ch == '!')
                {
                    _csiParam.Append(ch);
                    _parseState = ParseState.CsiParam;
                }
                else if (ch >= '0' && ch <= '9' || ch == ';')
                {
                    _csiParam.Append(ch);
                    _parseState = ParseState.CsiParam;
                }
                else if (ch >= 0x40 && ch <= 0x7E)
                {
                    DispatchCsi(ch, _csiParam.ToString());
                    _parseState = ParseState.Ground;
                }
                else
                {
                    _parseState = ParseState.CsiIgnore;
                }
                break;

            case ParseState.CsiIgnore:
                if (ch >= 0x40 && ch <= 0x7E) _parseState = ParseState.Ground;
                break;

            case ParseState.OscString:
                if (ch == '\x07' || ch == '\x9C') { HandleOscComplete(); _parseState = ParseState.Ground; }
                else if (ch == '\x1B') _parseState = ParseState.OscStringEsc;
                else _oscBuf.Append(ch);
                break;

            case ParseState.OscStringEsc:
                if (ch == '\\') HandleOscComplete();
                _parseState = ParseState.Ground;
                break;
        }
    }

    private void HandleOscComplete()
    {
        var s = _oscBuf.ToString();
        var sep = s.IndexOf(';');
        if (sep < 0 || !int.TryParse(s.AsSpan(0, sep), out var code)) return;

        var payload = s[(sep + 1)..];

        if (code == 0 || code == 2)
        {
            var handler = TitleChanged;
            if (handler is not null)
                Dispatcher.UIThread.Post(() => handler(this, payload));
            return;
        }

        if (code == 1337 && payload.StartsWith("CurrentDir=", StringComparison.Ordinal))
        {
            var path = payload["CurrentDir=".Length..].Trim();
            if (path.Length == 0) return;

            var handler = WorkingDirectoryChanged;
            if (handler is not null)
                Dispatcher.UIThread.Post(() => handler(this, path));
        }
    }

    private void DispatchCsi(char cmd, string param)
    {
        var priv = param.StartsWith('?');
        var raw = priv ? param[1..] : param;
        var nums = ParseNums(raw);
        int P(int i, int def = 1) => (i < nums.Count && nums[i] > 0) ? nums[i] : def;
        int P0(int i) => (i < nums.Count) ? nums[i] : 0;

        switch (cmd)
        {
            case 'A': _cursorRow = Math.Max(0, _cursorRow - P(0)); break;
            case 'B': _cursorRow = Math.Min(_rows - 1, _cursorRow + P(0)); break;
            case 'C': _cursorCol = Math.Min(_cols - 1, _cursorCol + P(0)); break;
            case 'D': _cursorCol = Math.Max(0, _cursorCol - P(0)); break;
            case 'E': _cursorRow = Math.Min(_rows - 1, _cursorRow + P(0)); _cursorCol = 0; break;
            case 'F': _cursorRow = Math.Max(0, _cursorRow - P(0)); _cursorCol = 0; break;
            case 'G': _cursorCol = Math.Min(_cols - 1, Math.Max(0, P(0) - 1)); break;
            case 'H':
            case 'f':
                _cursorRow = Math.Min(_rows - 1, Math.Max(0, P(0) - 1));
                _cursorCol = Math.Min(_cols - 1, Math.Max(0, P(1) - 1));
                break;
            case 'd': _cursorRow = Math.Min(_rows - 1, Math.Max(0, P(0) - 1)); break;

            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;

            case 'm': ApplySgr(nums); break;

            case 'h':
                if (priv && P0(0) == 25) _cursorVisible = true;
                else if (priv && P0(0) == 2004) _bracketedPasteMode = true;
                break;
            case 'l':
                if (priv && P0(0) == 25) _cursorVisible = false;
                else if (priv && P0(0) == 2004) _bracketedPasteMode = false;
                break;

            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;

            case '@': InsertChars(P(0)); break;
            case 'P': DeleteChars(P(0)); break;
            case 'X': EraseChars(P(0)); break;

            case 'S': ScrollUp(P(0)); break;
            case 'T': ScrollDown(P(0)); break;
        }
    }

    private void ApplySgr(List<int> nums)
    {
        if (nums.Count == 0) { ResetAttrs(); return; }
        for (var i = 0; i < nums.Count; i++)
        {
            switch (nums[i])
            {
                case 0: ResetAttrs(); break;
                case 1: _bold = true; break;
                case 4: _underline = true; break;
                case 7: _reverse = true; break;
                case 22: _bold = false; break;
                case 24: _underline = false; break;
                case 27: _reverse = false; break;
                case >= 30 and <= 37: _fg = AnsiPalette[nums[i] - 30]; break;
                case 38:
                    if (i + 2 < nums.Count && nums[i + 1] == 5) { _fg = Palette256(nums[i + 2]); i += 2; }
                    else if (i + 4 < nums.Count && nums[i + 1] == 2) { _fg = Color.FromRgb((byte)nums[i + 2], (byte)nums[i + 3], (byte)nums[i + 4]); i += 4; }
                    break;
                case 39: _fg = DefaultFg; break;
                case >= 40 and <= 47: _bg = AnsiPalette[nums[i] - 40]; break;
                case 48:
                    if (i + 2 < nums.Count && nums[i + 1] == 5) { _bg = Palette256(nums[i + 2]); i += 2; }
                    else if (i + 4 < nums.Count && nums[i + 1] == 2) { _bg = Color.FromRgb((byte)nums[i + 2], (byte)nums[i + 3], (byte)nums[i + 4]); i += 4; }
                    break;
                case 49: _bg = DefaultBg; break;
                case >= 90 and <= 97: _fg = AnsiPalette[nums[i] - 90 + 8]; break;
                case >= 100 and <= 107: _bg = AnsiPalette[nums[i] - 100 + 8]; break;
            }
        }
    }

    private void ResetAttrs() { _fg = DefaultFg; _bg = DefaultBg; _bold = false; _underline = false; _reverse = false; }
    private void ResetTerminal() { ResetAttrs(); _cursorRow = 0; _cursorCol = 0; ClearAllCells(); }

    private void SetCell(int r, int c, char ch)
    {
        if (r < 0 || r >= _rows || c < 0 || c >= _cols) return;
        var fg = _reverse ? _bg : _fg;
        var bg = _reverse ? _fg : _bg;
        _cells[r, c] = new TermCell(ch, fg, bg, _bold, _underline);
    }

    private void LineFeed()
    {
        _cursorRow++;
        if (_cursorRow >= _rows) { ScrollUp(1); _cursorRow = _rows - 1; }
    }

    private void ReverseLineFeed()
    {
        _cursorRow--;
        if (_cursorRow < 0) { ScrollDown(1); _cursorRow = 0; }
    }

    private void ScrollUp(int n)
    {
        n = Math.Clamp(n, 0, _rows);
        for (var r = 0; r < n; r++)
        {
            var row = new TermCell[_cols];
            for (var c = 0; c < _cols; c++) row[c] = _cells[r, c];
            _scrollback.Add(row);
        }
        if (_scrollOffset > 0)
            _scrollOffset += n;

        var trimmed = _scrollback.Count > MaxScrollbackLines ? _scrollback.Count - MaxScrollbackLines : 0;
        if (trimmed > 0)
            _scrollback.RemoveRange(0, trimmed);

        _scrollOffset = Math.Clamp(_scrollOffset - trimmed, 0, _scrollback.Count);

        for (var r = 0; r < _rows - n; r++)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = _cells[r + n, c];
        for (var r = Math.Max(0, _rows - n); r < _rows; r++)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = default;
    }

    private void ScrollDown(int n)
    {
        n = Math.Clamp(n, 0, _rows);
        for (var r = _rows - 1; r >= n; r--)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = _cells[r - n, c];
        for (var r = 0; r < n; r++)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = default;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var r = _cursorRow + 1; r < _rows; r++) ClearRow(r);
                break;
            case 1:
                for (var r = 0; r < _cursorRow; r++) ClearRow(r);
                EraseLine(1);
                break;
            case 2: ClearAllCells(); break;
            case 3: ClearAllCells(); _scrollback.Clear(); _scrollOffset = 0; break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: for (var c = _cursorCol; c < _cols; c++) _cells[_cursorRow, c] = default; break;
            case 1: for (var c = 0; c <= _cursorCol; c++) _cells[_cursorRow, c] = default; break;
            case 2: ClearRow(_cursorRow); break;
        }
    }

    private void EraseChars(int n)
    {
        var end = Math.Min(_cursorCol + n, _cols);
        for (var c = _cursorCol; c < end; c++)
            _cells[_cursorRow, c] = default;
    }

    private void ClearRow(int r) { for (var c = 0; c < _cols; c++) _cells[r, c] = default; }

    private void ClearAllCells()
    {
        for (var r = 0; r < _rows; r++)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = default;
    }

    private void InsertLines(int n)
    {
        for (var r = _rows - 1; r >= _cursorRow + n; r--)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = _cells[r - n, c];
        for (var r = _cursorRow; r < _cursorRow + n && r < _rows; r++) ClearRow(r);
    }

    private void DeleteLines(int n)
    {
        for (var r = _cursorRow; r < _rows - n; r++)
            for (var c = 0; c < _cols; c++)
                _cells[r, c] = _cells[r + n, c];
        for (var r = Math.Max(0, _rows - n); r < _rows; r++) ClearRow(r);
    }

    private void InsertChars(int n)
    {
        for (var c = _cols - 1; c >= _cursorCol + n; c--)
            _cells[_cursorRow, c] = _cells[_cursorRow, c - n];
        for (var c = _cursorCol; c < _cursorCol + n && c < _cols; c++)
            _cells[_cursorRow, c] = default;
    }

    private void DeleteChars(int n)
    {
        for (var c = _cursorCol; c < _cols - n; c++)
            _cells[_cursorRow, c] = _cells[_cursorRow, c + n];
        for (var c = Math.Max(0, _cols - n); c < _cols; c++)
            _cells[_cursorRow, c] = default;
    }

    private static string? KeyToVt(Key key, KeyModifiers mods)
    {
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var mod = (shift ? 1 : 0) | (alt ? 2 : 0) | (ctrl ? 4 : 0);
        var modSuffix = mod > 0 ? $";{mod + 1}" : "";

        return key switch
        {
            Key.Up => mod > 0 ? $"\x1b[1{modSuffix}A" : "\x1b[A",
            Key.Down => mod > 0 ? $"\x1b[1{modSuffix}B" : "\x1b[B",
            Key.Right => mod > 0 ? $"\x1b[1{modSuffix}C" : "\x1b[C",
            Key.Left => mod > 0 ? $"\x1b[1{modSuffix}D" : "\x1b[D",

            Key.Home => mod > 0 ? $"\x1b[1{modSuffix}H" : "\x1bOH",
            Key.End => mod > 0 ? $"\x1b[1{modSuffix}F" : "\x1bOF",

            Key.Insert => $"\x1b[2{modSuffix}~",
            Key.Delete => $"\x1b[3{modSuffix}~",
            Key.PageUp => $"\x1b[5{modSuffix}~",
            Key.PageDown => $"\x1b[6{modSuffix}~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Return => "\r",
            Key.Escape => "\x1b",
            Key.Back => ctrl ? "\x08" : "\x7f",
            _ => null
        };
    }

    private static int? ControlChar(Key key) => key switch
    {
        Key.A => 1,
        Key.B => 2,
        Key.C => 3,
        Key.D => 4,
        Key.E => 5,
        Key.F => 6,
        Key.G => 7,
        Key.H => 8,
        Key.I => 9,
        Key.J => 10,
        Key.K => 11,
        Key.L => 12,
        Key.M => 13,
        Key.N => 14,
        Key.O => 15,
        Key.P => 16,
        Key.Q => 17,
        Key.R => 18,
        Key.S => 19,
        Key.T => 20,
        Key.U => 21,
        Key.V => 22,
        Key.W => 23,
        Key.X => 24,
        Key.Y => 25,
        Key.Z => 26,
        _ => null
    };

    private static string? AltKeyChar(Key key) => key switch
    {
        Key.B => "b",
        Key.F => "f",
        Key.D => "d",
        Key.Back => "\x7f",
        Key.OemPeriod => ".",
        _ => null
    };

    private static List<int> ParseNums(string s)
    {
        var result = new List<int>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out var n)) result.Add(n);
            else result.Add(0);
        return result;
    }

    private static Color Palette256(int n)
    {
        if (n < 16) return AnsiPalette[n];
        if (n < 232)
        {
            n -= 16;
            var b = n % 6; var g = (n / 6) % 6; var r = n / 36;
            return Color.FromRgb((byte)(r > 0 ? 55 + r * 40 : 0),
                                  (byte)(g > 0 ? 55 + g * 40 : 0),
                                  (byte)(b > 0 ? 55 + b * 40 : 0));
        }
        var v = (byte)(8 + (n - 232) * 10);
        return Color.FromRgb(v, v, v);
    }
}

public readonly record struct TermCell(
    char Char,
    Color? Fg,
    Color? Bg,
    bool Bold,
    bool Underline);

public sealed class TerminalSnapshot(
    TermCell[,] cells,
    int rows, int cols,
    int cursorRow, int cursorCol, bool cursorVisible,
    Color fg, Color bg, bool bold, bool underline, bool reverse,
    ConsoleTerminal.ParseState parseState, string csiParam)
{
    internal TermCell[,] Cells { get; } = cells;
    public int Rows { get; } = rows;
    public int Cols { get; } = cols;
    internal int CursorRow { get; } = cursorRow;
    internal int CursorCol { get; } = cursorCol;
    internal bool CursorVisible { get; } = cursorVisible;
    internal Color Fg { get; } = fg;
    internal Color Bg { get; } = bg;
    internal bool Bold { get; } = bold;
    internal bool Underline { get; } = underline;
    internal bool Reverse { get; } = reverse;
    internal ConsoleTerminal.ParseState ParseState { get; } = parseState;
    internal string CsiParam { get; } = csiParam;
}

internal static class NativeConPty
{
    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int STARTF_USESTDHANDLES = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr hRead, out IntPtr hWrite, IntPtr lpAttr, int nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr hPcon);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPcon);

    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr hPcon, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr val, IntPtr size, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? app, StringBuilder cmd,
        IntPtr pAttr, IntPtr tAttr,
        bool inherit, uint flags,
        IntPtr env, string? dir,
        ref STARTUPINFOEXW si, out PROCESS_INFORMATION pi);

    public static void LaunchProcess(StringBuilder cmdLine, string workingDir, IntPtr hPcon,
        out IntPtr hProcess, out IntPtr hThread)
    {
        var attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList: {Marshal.GetLastWin32Error()}");

            var hPconLocal = hPcon;
            var hPconPtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(hPconPtr, hPconLocal);
            try
            {
                if (!UpdateProcThreadAttribute(attrList, 0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        hPconLocal, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException($"UpdateProcThreadAttribute: {Marshal.GetLastWin32Error()}");

                var si = new STARTUPINFOEXW
                {
                    StartupInfo = new STARTUPINFOW
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                        dwFlags = 0
                    },
                    lpAttributeList = attrList
                };

                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDir, ref si, out var pi))
                    throw new InvalidOperationException($"CreateProcessW: {Marshal.GetLastWin32Error()}");

                hProcess = pi.hProcess;
                hThread = pi.hThread;
            }
            finally { Marshal.FreeHGlobal(hPconPtr); }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }
}

public sealed class TerminalShellOption
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
}

public static class TerminalShellSupport
{
    private const double MinPanelHeight = 120;
    private const double MaxPanelHeight = 420;

    private const string BashCwdHookCommand = """
        export PROMPT_COMMAND='printf \"\033]1337;CurrentDir=%s\007\" \"$(pwd -W)\"'; exec bash --login -i
        """;

    public static IEnumerable<TerminalShellOption> DetectTerminalShells(bool enablePSReadLinePrediction = false)
    {
        var shells = new List<TerminalShellOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddShell(string id, string displayName, string? resolvedPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath) || !seen.Add(resolvedPath))
                return;

            shells.Add(new TerminalShellOption
            {
                Id = id,
                DisplayName = displayName,
                FileName = resolvedPath,
                Arguments = arguments
            });
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var predictionCommand = enablePSReadLinePrediction
                ? "try { Set-PSReadLineOption -PredictionSource HistoryAndPlugin } catch { try { Set-PSReadLineOption -PredictionSource History } catch {} }; "
                : "try { Set-PSReadLineOption -PredictionSource None } catch {}; ";

            const string reportCwdCommand =
                "if (-not $global:__KodoOrigPrompt) { $global:__KodoOrigPrompt = $function:prompt }; " +
                "function global:prompt { " +
                "try { [Console]::Out.Write([char]27 + ']1337;CurrentDir=' + (Get-Location).Path + [char]7) } catch {}; " +
                "& $global:__KodoOrigPrompt }";

            AddShell(
                "powershell",
                "PowerShell",
                ResolveExecutable("pwsh.exe")
                    ?? ResolveExecutable("powershell.exe", Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe")),
                $"-NoLogo -NoExit -Command \"{predictionCommand}{reportCwdCommand}\"");
            AddShell(
                "windows-powershell",
                "Windows PowerShell",
                ResolveExecutable("powershell.exe", Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe")),
                $"-NoLogo -NoExit -Command \"{predictionCommand}{reportCwdCommand}\"");
            AddShell(
                "cmd",
                "Command Prompt",
                ResolveExecutable(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                    ?? ResolveExecutable("cmd.exe"),
                "/K prompt $E]1337;CurrentDir=$P\a$P$G");
            AddShell(
                "bash",
                "Git Bash",
                ResolveExecutable("bash.exe",
                    @"C:\Program Files\Git\bin\bash.exe",
                    @"C:\Program Files\Git\usr\bin\bash.exe"),
                $"--login -i -c \"{BashCwdHookCommand}\"");
        }
        else
        {
            AddShell("bash", "Bash", ResolveExecutable("bash"), "-i");
            AddShell("zsh", "Zsh", ResolveExecutable("zsh"), "-i");
            AddShell("sh", "Shell", ResolveExecutable("sh"), "-i");
        }

        return shells;
    }

    private static string? ResolveExecutable(string fileName, params string[] fallbacks)
    {
        if (Path.IsPathFullyQualified(fileName) && File.Exists(fileName))
            return fileName;

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (Path.HasExtension(fileName))
            {
                var candidate = Path.Combine(path, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            else
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(path, fileName + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        foreach (var fallback in fallbacks)
        {
            if (!string.IsNullOrWhiteSpace(fallback) && File.Exists(fallback))
                return fallback;
        }

        return null;
    }

    public static string GetClearCommandForShell(string shellId) =>
        shellId switch
        {
            "bash" or "zsh" or "sh" => "clear\r",
            _ => "cls\r"
        };

    public static double NormalizeTerminalPanelHeight(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinPanelHeight, MaxPanelHeight)
            : AppSettings.DefaultTerminalPanelHeight;

    public static string ExtractDisplayTitleFromShellTitle(string rawTitle)
    {
        if (!LooksLikeFilePath(rawTitle))
            return rawTitle;

        var trimmedPath = rawTitle.TrimEnd('\\', '/');

        if (File.Exists(trimmedPath) || (!Directory.Exists(trimmedPath) && Path.HasExtension(trimmedPath)))
        {
            var parentDir = Path.GetDirectoryName(trimmedPath);
            var parentName = string.IsNullOrEmpty(parentDir) ? null : Path.GetFileName(parentDir);
            if (!string.IsNullOrWhiteSpace(parentName))
                return parentName;
        }

        var lastSegment = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(lastSegment) ? rawTitle : lastSegment;
    }

    private static bool LooksLikeFilePath(string text)
    {
        if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
            return true;
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        if (text.StartsWith("/", StringComparison.Ordinal) && text.Contains('/'))
            return true;
        return false;
    }
}
