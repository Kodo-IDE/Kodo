
// Licensed under the GNU GPL-v3.0
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit;

namespace Kodo.Models;

internal sealed class SpanOverlapIndex
{
    public static readonly SpanOverlapIndex Empty = new([], [], [], []);

    private readonly int[] _starts;
    private readonly long[] _ends;
    private readonly int[] _order;
    private readonly long[] _subtreeMaxEnd;

    private SpanOverlapIndex(int[] starts, long[] ends, int[] order, long[] subtreeMaxEnd)
    {
        _starts = starts;
        _ends = ends;
        _order = order;
        _subtreeMaxEnd = subtreeMaxEnd;
    }

    public static SpanOverlapIndex Build<T>(IReadOnlyList<T> spans, Func<T, int> startSelector, Func<T, long> endSelector)
    {
        var count = spans.Count;
        var order = Enumerable.Range(0, count).OrderBy(i => startSelector(spans[i])).ToArray();
        var starts = new int[count];
        var ends = new long[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = startSelector(spans[order[i]]);
            ends[i] = endSelector(spans[order[i]]);
        }
        var subtreeMaxEnd = new long[count * 4];
        var index = new SpanOverlapIndex(starts, ends, order, subtreeMaxEnd);
        if (count > 0) index.Build(1, 0, count);
        return index;
    }

    public bool Overlaps(long start, int endInclusive)
    {
        var limit = UpperBound(endInclusive);
        return limit > 0 && Overlaps(1, 0, _starts.Length, limit, start);
    }

    public void Collect(long start, int endInclusive, List<int> result)
    {
        result.Clear();
        var limit = UpperBound(endInclusive);
        if (limit > 0) Collect(1, 0, _starts.Length, limit, start, result);
        if (result.Count > 1) result.Sort();
    }

    private long Build(int node, int left, int right)
    {
        if (right - left == 1) return _subtreeMaxEnd[node] = _ends[left];
        var middle = left + (right - left) / 2;
        return _subtreeMaxEnd[node] = Math.Max(Build(node * 2, left, middle), Build(node * 2 + 1, middle, right));
    }

    private int UpperBound(int endInclusive)
    {
        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_starts[middle] <= endInclusive) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private bool Overlaps(int node, int left, int right, int limit, long start)
    {
        if (left >= limit || _subtreeMaxEnd[node] <= start) return false;
        if (right - left == 1) return true;
        var middle = left + (right - left) / 2;
        return Overlaps(node * 2, left, middle, limit, start) || Overlaps(node * 2 + 1, middle, right, limit, start);
    }

    private void Collect(int node, int left, int right, int limit, long start, List<int> result)
    {
        if (left >= limit || _subtreeMaxEnd[node] <= start) return;
        if (right - left == 1)
        {
            result.Add(_order[left]);
            return;
        }
        var middle = left + (right - left) / 2;
        Collect(node * 2, left, middle, limit, start, result);
        Collect(node * 2 + 1, middle, right, limit, start, result);
    }
}

public enum LineEnding
{
    LF,
    CRLF
}

internal enum UnsavedTabAction
{
    Save,
    Discard,
    Cancel
}

public class EditorTab : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isDirty;
    private bool _isSelected;
    private IBrush _backgroundBrush = Brushes.Transparent;
    private IBrush _foregroundBrush = Brushes.White;

    public EditorTab(string path, string displayName, string content, bool isUntitled = false, LineEnding lineEnding = LineEnding.CRLF)
    {
        Path = path;
        DisplayName = displayName;
        _content = content;
        IsUntitled = isUntitled;
        LineEnding = lineEnding;
    }

    public string Path { get; set; }

    public string DisplayName { get; private set; }

    public string Icon => FileTreeItem.GetFileIcon(DisplayName);

    public bool IsUntitled { get; set; }

    public LineEnding LineEnding { get; set; }

    public System.Text.Encoding Encoding { get; set; } = System.Text.Encoding.UTF8;

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value)
            {
                return;
            }

            _content = value;
            OnPropertyChanged();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabTitle));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public IBrush BackgroundBrush
    {
        get => _backgroundBrush;
        set
        {
            if (Equals(_backgroundBrush, value))
            {
                return;
            }

            _backgroundBrush = value;
            OnPropertyChanged();
        }
    }

    public IBrush ForegroundBrush
    {
        get => _foregroundBrush;
        set
        {
            if (Equals(_foregroundBrush, value))
            {
                return;
            }

            _foregroundBrush = value;
            OnPropertyChanged();
        }
    }

    public string TabTitle => IsDirty ? $"{DisplayName} •" : DisplayName;

    private bool _isRenaming;
    private string _renameText = string.Empty;

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming == value) return; _isRenaming = value; OnPropertyChanged(); }
    }

    public string RenameText
    {
        get => _renameText;
        set { if (_renameText == value) return; _renameText = value; OnPropertyChanged(); }
    }

    public int TopLineNumber { get; set; } = 1;

    public double ScrollOffsetY { get; set; } = 0.0;

    public int CaretOffset { get; set; } = 0;

    private int _errorCount;
    private int _warningCount;
    private int _infoCount;
    private int _unusedCount;
    private string _diagnosticsText = string.Empty;
    private string _diagnosticsTooltip = string.Empty;

    public int ErrorCount => _errorCount;
    public int WarningCount => _warningCount;
    public int InfoCount => _infoCount;
    public int UnusedCount => _unusedCount;

    public bool HasDiagnostics => _errorCount > 0 || _warningCount > 0 || _infoCount > 0 || _unusedCount > 0;
    public bool HasErrorDiagnostics => _errorCount > 0;

    public string DiagnosticsText => _diagnosticsText;
    public string DiagnosticsTooltip => _diagnosticsTooltip;

    public int DiagnosticsSeverity
    {
        get
        {
            if (_errorCount > 0) return 3;
            if (_warningCount > 0) return 2;
            if (_infoCount > 0 || _unusedCount > 0) return 1;
            return 0;
        }
    }

    public void UpdateDiagnostics(int errors, int warnings, int infos, int unused)
    {
        if (_errorCount == errors && _warningCount == warnings && _infoCount == infos && _unusedCount == unused)
            return;
        _errorCount = errors;
        _warningCount = warnings;
        _infoCount = infos;
        _unusedCount = unused;

        if (errors == 0 && warnings == 0 && infos == 0 && unused == 0)
        {
            _diagnosticsText = string.Empty;
            _diagnosticsTooltip = "No problems";
        }
        else
        {
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            if (infos > 0) parts.Add($"{infos} info");
            if (unused > 0) parts.Add($"{unused} unused");
            _diagnosticsText = string.Join(" • ", parts);
            _diagnosticsTooltip = string.Join(Environment.NewLine, parts.Select(p => $"• {p}"));
        }

        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(UnusedCount));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasErrorDiagnostics));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(DiagnosticsTooltip));
        OnPropertyChanged(nameof(DiagnosticsSeverity));
    }

    public void ClearDiagnostics() => UpdateDiagnostics(0, 0, 0, 0);

    public void Rename(string path, string displayName)
    {
        Path = path;
        DisplayName = displayName;
        IsUntitled = false;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(Icon));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum ExplorerClipboardMode
{
    Copy,
    Cut
}

public class FileNode
{
    public FileNode(string name, string path, bool isDirectory)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsDirectory { get; }

    public string Icon => IsDirectory ? "▸" : "•";

    public ObservableCollection<FileNode> Children { get; } = [];
}

public class FileTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isRenaming;
    private string _renameText = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public int Depth { get; init; }

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming == value) return; _isRenaming = value; OnPropertyChanged(); }
    }

    public string RenameText
    {
        get => _renameText;
        set { if (_renameText == value) return; _renameText = value; OnPropertyChanged(); }
    }

    public void ApplyRenamedName(string name)
    {
        Name = name;
        RenameText = name;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Icon));
    }

    public double IndentWidth => Depth * 14.0;

    public IEnumerable<int> GuideLevels => Enumerable.Range(0, Depth);

    public string ChevronText => IsDirectory ? (_isExpanded ? "↓" : "→") : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChevronText));
            OnPropertyChanged(nameof(Icon));
        }
    }

    public string Icon => IsDirectory ? (_isExpanded ? "\U0001F4C2" : "\U0001F4C1") : GetFileIcon(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    internal static string GetFileIcon(string fileName)
    {
        if (_iconCache.TryGetValue(fileName, out var cached)) return cached;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var icon = ext switch
        {
            ".cs" or ".csproj" or ".axaml.cs" or ".csx" => "C#",
            ".xml" => "XML",
            ".axaml" or ".xaml" => "XAML",
            ".html" or ".htm" => "HTML",
            ".json" or ".yaml" or ".yml" or ".toml" or ".jsonc" or ".jsonl" => "JSON",
            ".txt" or ".rst" or ".log" => "TXT",
            ".md" or ".markdown" => "MD",
            ".png" => "PNG",
            ".jpg" or ".jpeg" => "JPG",
            ".gif" => "GIF",
            ".svg" => "SVG",
            ".ico" => "ICO",
            ".webp" => "WBP",
            ".bmp" => "BMP",
            ".py" => "PY",
            ".js" or ".jsx" => "JS",
            ".ts" or ".tsx" => "TS",
            ".vue" or ".svelte" => "UI",
            ".css" or ".scss" or ".less" => "CSS",
            ".sh" => "SH",
            ".bat" => "BAT",
            ".ps1" => "PS1",
            ".zip" or ".tar" or ".gz" or ".rar" => "ZIP",
            ".cpp" or ".cc" or ".cxx" => "C++",
            ".c" => "C",
            ".h" or ".hpp" or ".hxx" => "C++",
            ".rs" => "RS",
            ".go" => "GO",
            ".rb" => "RB",
            ".java" => "JAVA",
            ".kt" or ".kts" => "KT",
            ".swift" => "SW",
            ".fs" or ".fsi" or ".fsx" => "F#",
            ".sql" => "DB",
            ".lua" => "LUA",
            ".r" => "R",
            ".lock" => "Lk",
            ".csv" or ".tsv" => "CSV",
            ".nova" => "NOVA",
            ".kox" => "KOX",
            ".exe" => "EXE",
            ".dll" => "DLL",
            ".gitignore" => "IGNR",
            ".shine" => "SHINE",
            ".asm" or ".s" or ".S" => "ASM",
            ".iss" => "ISS",
            _ => "..",
        };
        _iconCache.TryAdd(fileName, icon);
        return icon;
    }
}
