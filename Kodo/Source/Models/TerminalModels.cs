// Licensed under the GNU GPL-v3.0
using Avalonia.Media;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Kodo.Models;

public sealed class TerminalSession : INotifyPropertyChanged, IDisposable
{
    private string _title;
    private string _workingDirectory;
    private bool _isSelected;
    private bool _isRunning;
    private string _statusText;
    private Process? _process;
    private IntPtr _windowHandle;
    private int? _exitCode;

    public TerminalSession(string shellId, string shellDisplayName, string title, string workingDirectory)
    {
        Id = Guid.NewGuid().ToString("N");
        ShellId = shellId;
        ShellDisplayName = shellDisplayName;
        _title = title;
        _workingDirectory = workingDirectory;
        _statusText = "Starting...";
    }

    public string Id { get; }

    public string ShellId { get; }

    public string ShellDisplayName { get; }

    public TerminalSnapshot? Snapshot { get; set; }

    public TerminalProcessHandle? LiveHandle { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;

            _title = value;
            HasCustomTitle = true;
            OnPropertyChanged();
        }
    }

    public bool HasCustomTitle { get; private set; }

    public void ApplyAutoTitle(string title)
    {
        if (HasCustomTitle || _title == title)
            return;

        _title = title;
        OnPropertyChanged(nameof(Title));
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (_workingDirectory == value)
                return;

            _workingDirectory = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
                return;

            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDotColor));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDotColor));
        }
    }

    public string StatusDotColor => StatusText switch
    {
        "Ready" => "#22C55E",
        "Paused" => "#38BDF8",
        "Exited" or "Closed" => "#94A3B8",
        var s when s.StartsWith("Failed") => "#EF4444",
        _ => "#F59E0B",
    };

    public Process? Process
    {
        get => _process;
        set
        {
            if (ReferenceEquals(_process, value))
                return;

            _process = value;
            OnPropertyChanged();
        }
    }

    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set
        {
            if (_windowHandle == value)
                return;

            _windowHandle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWindowHandle));
        }
    }

    public bool HasWindowHandle => WindowHandle != IntPtr.Zero;

    public int? ExitCode
    {
        get => _exitCode;
        set
        {
            if (_exitCode == value)
                return;

            _exitCode = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        try
        {
            Process?.Dispose();
        }
        catch
        {
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum TerminalParseState { Ground, Escape, CsiEntry, CsiParam, CsiIgnore, OscString, OscStringEsc }

public readonly record struct TermCell(string? Text, Color? Fg, Color? Bg, bool Bold, bool Underline);

public sealed class TerminalSnapshot(
    TermCell[,] cells,
    int rows, int cols,
    int cursorRow, int cursorCol, bool cursorVisible,
    Color fg, Color bg, bool bold, bool underline, bool reverse,
    TerminalParseState parseState, string csiParam,
    bool altActive = false,
    TermCell[,]? altCells = null,
    int altCursorRow = 0, int altCursorCol = 0,
    int scrollTop = 0, int scrollBottom = int.MaxValue,
    int savedRow = 0, int savedCol = 0)
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
    internal TerminalParseState ParseState { get; } = parseState;
    internal string CsiParam { get; } = csiParam;
    internal bool AltActive { get; } = altActive;
    internal TermCell[,]? AltCells { get; } = altCells;
    internal int AltCursorRow { get; } = altCursorRow;
    internal int AltCursorCol { get; } = altCursorCol;
    internal int ScrollTop { get; } = scrollTop;
    internal int ScrollBottom { get; } = scrollBottom;
    internal int SavedRow { get; } = savedRow;
    internal int SavedCol { get; } = savedCol;
}

public sealed class TerminalProcessHandle
{
    public Stream? ReadStream;
    public Stream? WriteStream;
    public int UnixMasterFd = -1;
    public int UnixChildPid = -1;
    public Process? PipeProcess;
    public IntPtr HPcon = IntPtr.Zero;
    public IntPtr HProcess = IntPtr.Zero;
    public IntPtr HThread = IntPtr.Zero;
    public bool IsUnixPty;
    public bool Exited;
    public CancellationTokenSource? WatcherCts;
}

public sealed class TerminalShellOption
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
}
