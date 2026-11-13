// Licensed under the GNU GPL-v3.0
using System;
using System.IO;
using Avalonia.Platform;

namespace Kodo;

internal static class KodoIcons
{
    private static string Load(string name)
    {
        var uri = new Uri($"avares://Kodo/Assets/Icons/{name}.svg");
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Gear { get; } = Load("gear");
    public static string Home { get; } = Load("home");
    public static string Pencil { get; } = Load("pencil");
    public static string GitHub { get; } = Load("github");
    public static string PagePlus { get; } = Load("page-plus");
    public static string ArrowDownTray { get; } = Load("arrow-down-tray");
    public static string FolderPlus { get; } = Load("folder-plus");
    public static string FolderChevronLeft { get; } = Load("folder-chevron-left");
    public static string FolderChevronRight { get; } = Load("folder-chevron-right");
    public static string PuzzlePiece { get; } = Load("puzzle-piece");
    public static string ChevronUpDown { get; } = Load("chevron-up-down");
    public static string FolderX { get; } = Load("folder-x");
    public static string Circle { get; } = Load("circle");
    public static string XMark { get; } = Load("x-mark");
    public static string Folder { get; } = Load("folder");
    public static string FolderOpen { get; } = Load("folder-open");
    public static string Magnifier { get; } = Load("magnifier");
    public static string Minus { get; } = Load("minus");
    public static string Plus { get; } = Load("plus");
    public static string ExclamationTriangle { get; } = Load("exclamation-triangle");
    public static string NewsFile { get; } = Load("news-file");
    public static string Sparkle { get; } = Load("sparkle");
    public static string Pin { get; } = Load("pin");
    public static string Discord { get; } = Load("discord");
    public static string InstagramCircle { get; } = Load("instagram-circle");
    public static string InstagramDot { get; } = Load("instagram-dot");
    public static string InstagramSquare { get; } = Load("instagram-square");
    public static string Globe { get; } = Load("globe");
    public static string Tag { get; } = Load("tag");
    public static string Trash { get; } = Load("trash");
    public static string ArrowPath { get; } = Load("arrow-path");
    public static string InformationCircle { get; } = Load("information-circle");
    public static string ChevronUp { get; } = Load("chevron-up");
    public static string ChevronDown { get; } = Load("chevron-down");
    public static string Archive { get; } = Load("archive");

    public static string SymbolVariable { get; } = Load("symbol-variable");
    public static string SymbolFunction { get; } = Load("symbol-function");
    public static string SymbolProperty { get; } = Load("symbol-property");
    public static string SymbolType { get; } = Load("symbol-type");
    public static string SymbolNamespace { get; } = Load("symbol-namespace");
    public static string SymbolKeyword { get; } = Load("symbol-keyword");
}

