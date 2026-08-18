// Licensed under GPL-v3.0
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Kodo.Models;

// Represents a single row in the file explorer tree
public class FileTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public int Depth { get; init; }

    // Pixel indentation based on nesting depth
    public double IndentWidth => Depth * 14.0;

    // Chevron shown next to directories; blank for files
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

    // Icon varies between open/closed folder vs file
    public string Icon => IsDirectory ? (_isExpanded ? "\U0001F4C2" : "\U0001F4C1") : GetFileIcon(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Returns a simple file-type icon based on extension.
    internal static string GetFileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
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
            _ => "..",
        };
    }
}