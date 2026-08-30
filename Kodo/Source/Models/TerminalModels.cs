// Licensed under GPL-v3.0
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

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

    // Last-saved screen buffer; null until first shown
    public TerminalSnapshot? Snapshot { get; set; }

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
        "Ready" => "#22C55E", // green
        "Paused" => "#38BDF8", // blue
        "Exited" or "Closed" => "#94A3B8", // gray
        var s when s.StartsWith("Failed") => "#EF4444", // red
        _ => "#F59E0B", // amber
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
            // Ignore cleanup failures on shutdown
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}