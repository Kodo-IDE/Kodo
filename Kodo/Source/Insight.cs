// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Kodo.Models;

namespace Kodo;

// Source of an InsightSuggestion - drives sort order and label.
public enum InsightKind { Variable, Function, Property, Type, Namespace, Keyword }

public sealed class InsightSuggestion : ICompletionData
{
    // Set by MainWindow from its live theme brushes before building suggestions.
    public static IBrush PanelForeground { get; set; } = Brushes.WhiteSmoke;
    public static IBrush MutedForeground { get; set; } = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public InsightKind Kind { get; }
    public string Text { get; }
    public IImage? Image => null;
    private Control? _content;
    public object Content => _content ??= BuildContentVisual();
    public object? Description => null;
    public double Priority => Kind switch
    {
        InsightKind.Variable  => 5,
        InsightKind.Function  => 4,
        InsightKind.Property  => 3,
        InsightKind.Type      => 2,
        InsightKind.Namespace => 1,
        InsightKind.Keyword   => 0,
        _ => 0,
    };

    public InsightSuggestion(string text, InsightKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);

    private static string KindLabel(InsightKind kind) => kind switch
    {
        InsightKind.Variable  => "Variable (this file)",
        InsightKind.Function  => "Function",
        InsightKind.Property  => "Property",
        InsightKind.Type      => "Type",
        InsightKind.Namespace => "Namespace",
        InsightKind.Keyword   => "Keyword",
        _ => string.Empty,
    };

    private static (string Glyph, string Color) GlyphAndColorFor(InsightKind kind) => kind switch
    {
        InsightKind.Variable  => ("V", "#3a79df"),
        InsightKind.Function  => ("F", "#9c51e2"),
        InsightKind.Property  => ("P", "#1bc0ad"),
        InsightKind.Type      => ("T", "#e76e17"),
        InsightKind.Namespace => ("N", "#0db373"),
        InsightKind.Keyword   => ("K", "#5b5dda"),
        _ => ("•", "#6B7280"),
    };

    private static readonly Dictionary<InsightKind, IBrush> GlyphBrushes =
        Enum.GetValues<InsightKind>().ToDictionary(
            k => k,
            k => (IBrush)new SolidColorBrush(Color.Parse(GlyphAndColorFor(k).Color)));

    private static readonly FontFamily MonoFontFamily = new("Cascadia Code,Consolas,Menlo,Monospace");

    private static readonly Geometry VariableIconGeometry = Geometry.Parse(
        "M0 7.008v-3.008q0-1.632 1.184-2.816t2.816-1.184h4q1.664 0 2.816 1.184t1.184 2.816h4q2.496 0 4.256 1.76l9.984 10.016q1.76 1.728 1.76 4.224t-1.76 4.256l-5.984 6.016q-1.76 1.728-4.224 1.728t-4.256-1.728l-10.016-10.016q-1.76-1.76-1.76-4.256v-12h6.016q0-0.832-0.608-1.408t-1.408-0.576h-4q-0.832 0-1.408 0.576t-0.576 1.408v3.008q0 0.608-0.512 0.864t-0.992 0-0.512-0.864zM8 16q0 0.832 0.608 1.408l9.984 10.016q0.608 0.576 1.44 0.576t1.376-0.576l6.016-6.016q0.576-0.576 0.576-1.408t-0.576-1.408l-10.016-10.016q-0.576-0.576-1.408-0.576h-1.024q1.024 1.376 1.024 3.008 0 1.12-0.384 2.048t-0.992 1.536-1.472 0.992-1.728 0.416-1.76-0.192-1.664-0.832v1.024zM8 11.008q0 0.8 0.32 1.44t0.864 0.928 1.184 0.48 1.28 0 1.152-0.48 0.864-0.928 0.352-1.44q0-0.96-0.576-1.728t-1.44-1.056v2.784q0 0.608-0.512 0.864t-0.992 0-0.48-0.864v-2.784q-0.896 0.288-1.44 1.056t-0.576 1.728z");

    private static readonly Geometry FunctionIconGeometry = Geometry.Parse(
        "M16.6582 9.28638C18.098 10.1862 18.8178 10.6361 19.0647 11.2122C19.2803 11.7152 19.2803 12.2847 19.0647 12.7878C18.8178 13.3638 18.098 13.8137 16.6582 14.7136L9.896 18.94C8.29805 19.9387 7.49907 20.4381 6.83973 20.385C6.26501 20.3388 5.73818 20.0469 5.3944 19.584C5 19.053 5 18.1108 5 16.2264V7.77357C5 5.88919 5 4.94701 5.3944 4.41598C5.73818 3.9531 6.26501 3.66111 6.83973 3.6149C7.49907 3.5619 8.29805 4.06126 9.896 5.05998L16.6582 9.28638Z");

    private static readonly Geometry PropertyIconGeometry = Geometry.Parse(
        "M2.46148 12.8001C2.29321 12.5087 2.20908 12.3629 2.17615 12.208C2.14701 12.0709 2.14701 11.9293 2.17615 11.7922C2.20908 11.6373 2.29321 11.4915 2.46148 11.2001L6.53772 4.13984C6.70598 3.8484 6.79011 3.70268 6.90782 3.5967C7.01196 3.50293 7.13465 3.43209 7.26793 3.38879C7.41856 3.33984 7.58683 3.33984 7.92336 3.33984H16.0758C16.4124 3.33984 16.5806 3.33984 16.7313 3.38879C16.8645 3.43209 16.9872 3.50293 17.0914 3.5967C17.2091 3.70268 17.2932 3.8484 17.4615 4.13984L21.5377 11.2001C21.706 11.4915 21.7901 11.6373 21.823 11.7922C21.8522 11.9293 21.8522 12.0709 21.823 12.208C21.7901 12.3629 21.706 12.5087 21.5377 12.8001L17.4615 19.8604C17.2932 20.1518 17.2091 20.2975 17.0914 20.4035C16.9872 20.4973 16.8645 20.5681 16.7313 20.6114C16.5806 20.6604 16.4124 20.6604 16.0758 20.6604H7.92336C7.58683 20.6604 7.41856 20.6604 7.26793 20.6114C7.13465 20.5681 7.01196 20.4973 6.90782 20.4035C6.79011 20.2975 6.70598 20.1518 6.53772 19.8604L2.46148 12.8001Z");

    private static readonly Geometry TypeIconGeometry = Geometry.Parse(
        "M0 12L6 1.60769H18L24 12L18 22.3923H6L0 12Z");

    private static readonly Geometry NamespaceIconGeometry = Geometry.Parse(
        "M108,36H48A12,12,0,0,0,36,48v60a12,12,0,0,0,12,12h60a12,12,0,0,0,12-12V48A12,12,0,0,0,108,36ZM96,96H60V60H96Z" +
        "M208,36H148a12,12,0,0,0-12,12v60a12,12,0,0,0,12,12h60a12,12,0,0,0,12-12V48A12,12,0,0,0,208,36ZM196,96H160V60h36Z" +
        "M108,136H48a12,12,0,0,0-12,12v60a12,12,0,0,0,12,12h60a12,12,0,0,0,12-12V148A12,12,0,0,0,108,136ZM96,196H60V160H96Z" +
        "M208,136H148a12,12,0,0,0-12,12v60a12,12,0,0,0,12,12h60a12,12,0,0,0,12-12V148A12,12,0,0,0,208,136Zm-12,60H160V160h36Z");

    private static readonly Geometry KeywordIconGeometry = Geometry.Parse(
        "M12 10.2308L3.08495 7.02346M12 10.2308L20.9178 7.03406M12 10.2308V20.8791" +
        "M5.13498 18.5771L10.935 20.6242C11.3297 20.7635 11.527 20.8331 11.7294 20.8608" +
        "C11.909 20.8853 12.091 20.8853 12.2706 20.8608C12.473 20.8331 12.6703 20.7635 13.065 20.6242" +
        "L18.865 18.5771C19.6337 18.3058 20.018 18.1702 20.3018 17.9269C20.5523 17.7121 20.7459 17.4386 20.8651 17.1308" +
        "C21 16.7823 21 16.3747 21 15.5595V8.44058C21 7.62542 21 7.21785 20.8651 6.86935" +
        "C20.7459 6.56155 20.5523 6.28804 20.3018 6.0732C20.018 5.82996 19.6337 5.69431 18.865 5.42301" +
        "L13.065 3.37595C12.6703 3.23665 12.473 3.167 12.2706 3.13936C12.091 3.11484 11.909 3.11484 11.7294 3.13936" +
        "C11.527 3.167 11.3297 3.23665 10.935 3.37595L5.13498 5.42301C4.36629 5.69431 3.98195 5.82996 3.69824 6.0732" +
        "C3.44766 6.28804 3.25414 6.56155 3.13495 6.86935C3 7.21785 3 7.62542 3 8.44058V15.5595" +
        "C3 16.3747 3 16.7823 3.13495 17.1308C3.25414 17.4386 3.44766 17.7121 3.69824 17.9269" +
        "C3.98195 18.1702 4.36629 18.3058 5.13498 18.5771Z");

    // [icon chip] [name] [kind label, right-aligned], themed to Kodo's live colors.
    private Control BuildContentVisual()
    {
        var iconGeometry = Kind switch
        {
            InsightKind.Variable  => VariableIconGeometry,
            InsightKind.Function  => FunctionIconGeometry,
            InsightKind.Property  => PropertyIconGeometry,
            InsightKind.Type      => TypeIconGeometry,
            InsightKind.Namespace => NamespaceIconGeometry,
            InsightKind.Keyword   => KeywordIconGeometry,
            _ => KeywordIconGeometry,
        };

        var iconChip = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Background = GlyphBrushes[Kind],
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = iconGeometry,
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var nameBlock = new TextBlock
        {
            Text = Text,
            Foreground = PanelForeground,
            FontFamily = MonoFontFamily,
            FontSize = 13,
            Margin = new Thickness(8, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        var kindBlock = new TextBlock
        {
            Text = KindLabel(Kind),
            Foreground = MutedForeground,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 160,
        };

        var row = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 40 });
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        Grid.SetColumn(iconChip, 0);
        Grid.SetColumn(nameBlock, 1);
        Grid.SetColumn(kindBlock, 2);
        row.Children.Add(iconChip);
        row.Children.Add(nameBlock);
        row.Children.Add(kindBlock);

        return row;
    }
}

public sealed class InsightEngine
{
    private readonly Dictionary<string, HashSet<string>> _variablesByFile = new(StringComparer.OrdinalIgnoreCase);

    private const string NotCompoundOrArrow = @"(?<![=!<>+\-*/%&|^~])=(?![=>])";

    private static readonly Regex TypedOrKeywordDeclaration = new(
        @"\b(?:var|let|val|const|int|long|short|byte|sbyte|uint|ulong|ushort|float|double|decimal|bool|char|string|object|dynamic|auto)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:[:,][^=]*)?" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex ModifiedBinding = new(
        @"\b(?:let|var|val)\s+(?:mut\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?::[^=]*)?" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex BareAssignment = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex ColonAnnotatedAssignment = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*[A-Za-z_][A-Za-z0-9_.<>\[\]\?\|,\s]*\s*" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex QualifiedTypeAssignment = new(
        @"^\s*(?:(?:public|private|protected|internal|static|readonly|sealed|abstract|virtual|override|async|extern|unsafe|partial|const|volatile|new|required|file)\s+)*(?:[A-Za-z_][A-Za-z0-9_]*\s*(?:<[^>]*>)?\s*(?:\[\s*\])*\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*[A-Za-z_][A-Za-z0-9_.<>\[\]\?\|,\s]*)?\s*" + NotCompoundOrArrow,
        RegexOptions.Compiled);

    private static readonly Regex LoopOrHandlerBinding = new(
        @"\b(?:foreach|for|catch|using)\s*\(\s*(?:var|[A-Za-z_][A-Za-z0-9_<>,.\[\]\s]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:in\b|=|\))",
        RegexOptions.Compiled);

    private static readonly Regex[] DeclarationPatterns =
    {
        TypedOrKeywordDeclaration,
        ModifiedBinding,
        BareAssignment,
        ColonAnnotatedAssignment,
        QualifiedTypeAssignment,
        LoopOrHandlerBinding,
    };

    private static readonly HashSet<string> KnownColonTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "string", "object", "dynamic",
        "var", "auto", "void", "list", "dict", "set", "tuple", "array", "map",
        "str", "num", "number", "boolean", "any", "unknown", "never", "optional",
        "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64",
        "float32", "float64",
    };

    private static readonly Regex BatchSetDeclaration = new(
        @"^\s*set\s+(?:/a\s+|/p\s+)?""?([A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elif", "elseif", "else", "for", "foreach", "while", "switch", "match", "case",
        "return", "try", "catch", "finally", "throw", "raise", "new", "class", "struct",
        "interface", "trait", "enum", "namespace", "module", "package", "using", "import",
        "from", "require", "include", "export", "public", "private", "protected", "internal",
        "static", "readonly", "sealed", "abstract", "virtual", "override", "async", "await",
        "void", "null", "nil", "none", "undefined", "true", "false", "this", "self", "base",
        "super", "def", "function", "fn", "lambda", "in", "is", "as", "out", "ref", "params",
        "yield", "break", "continue", "do", "default", "goto", "mut", "let", "var", "val",
        "const", "end", "then", "done", "fi", "esac", "not", "and", "or",
        "echo", "print", "println", "printf", "console", "exit", "call", "cls", "pause", "rem",
        "cd", "dir", "mkdir", "rmdir", "del", "copy", "move", "ren", "shift", "setlocal",
        "endlocal", "errorlevel", "start", "taskkill", "set", "unset", "source",
        "sudo", "chmod", "chown", "curl", "wget", "ssh", "scp", "tar", "zip", "unzip",
        "git", "npm", "npx", "yarn", "pnpm", "pip", "python", "python3", "node", "dotnet",
        "cargo", "go", "make", "cmake", "gradle", "mvn", "docker", "kubectl", "brew", "apt",
        "build", "publish", "install", "run", "test", "clean", "deploy", "restore", "release",
        "debug", "lock", "fixed", "checked", "unchecked", "synchronized", "with", "unsafe",
        "when", "while", "func",
    };

    private static readonly Regex TrailingLineComment = new(
        @"(?:(?<=\s)|^)(?://|\#)(?!\S).*$|(?:(?<=\s)|^)(?://|\#)\s.*$",
        RegexOptions.Compiled);

    private static string MaskStringLiterals(string lineText)
    {
        if (lineText.IndexOfAny(['"', '\'']) < 0)
            return lineText;

        var chars = lineText.ToCharArray();
        char? quote = null;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (quote is null)
            {
                if (c is '"' or '\'')
                    quote = c;
                continue;
            }

            if (c == '\\' && i + 1 < chars.Length)
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (c == quote)
            {
                quote = null;
                continue;
            }

            chars[i] = ' ';
        }

        return new string(chars);
    }

    private static string BuildMaskedDocument(string documentText, LoadedExtension? extension)
    {
        if (string.IsNullOrEmpty(documentText))
            return documentText;

        var commentLine = extension?.CommentLine is { Length: > 0 } cl ? cl : "//";
        var blockStart = extension?.CommentBlockStart is { Length: > 0 } bs ? bs : "/*";
        var blockEnd = extension?.CommentBlockEnd is { Length: > 0 } be ? be : "*/";
        var multiDelims = extension?.MultiLineStringDelimiters is { Length: > 0 } mls ? mls : Array.Empty<string>();
        var disableSingle = extension?.DisableSingleQuoteStrings ?? false;

        var chars = documentText.ToCharArray();
        var masked = new char[chars.Length];
        for (var i = 0; i < chars.Length; i++) masked[i] = chars[i];

        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inMulti = false;
        char delim = '\0';
        var isInterpolated = false;
        var isVerbatim = false;
        var inInterpolationCode = false;
        var interpolationDepth = 0;

        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }
                else if (c != '\r')
                {
                    masked[i] = ' ';
                }
                continue;
            }

            if (inBlockComment)
            {
                if (MatchesAt(documentText, i, blockEnd))
                {
                    for (var k = 0; k < blockEnd.Length && i + k < chars.Length; k++)
                        if (masked[i + k] != '\n' && masked[i + k] != '\r')
                            masked[i + k] = ' ';
                    inBlockComment = false;
                    i += blockEnd.Length - 1;
                }
                else
                {
                    if (c != '\n' && c != '\r')
                        masked[i] = ' ';
                }
                continue;
            }

            if (inString)
            {
                // Interpolated string: code inside { } is real code, not string
                if (isInterpolated)
                {
                    if (inInterpolationCode)
                    {
                        if (c == '{' && i + 1 < chars.Length && chars[i + 1] == '{')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '}' && i + 1 < chars.Length && chars[i + 1] == '}')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '{')
                        {
                            interpolationDepth++;
                            continue;
                        }
                        if (c == '}')
                        {
                            interpolationDepth--;
                            if (interpolationDepth <= 0)
                            {
                                inInterpolationCode = false;
                                interpolationDepth = 0;
                            }
                            continue;
                        }
                        if (c == '"' && !inMulti)
                        {
                            continue;
                        }
                        continue;
                    }
                    else
                    {
                        if (c == '{' && i + 1 < chars.Length && chars[i + 1] == '{')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '}' && i + 1 < chars.Length && chars[i + 1] == '}')
                        {
                            masked[i] = ' ';
                            masked[i + 1] = ' ';
                            i++;
                            continue;
                        }
                        if (c == '{')
                        {
                            inInterpolationCode = true;
                            interpolationDepth = 1;
                            continue;
                        }
                    }
                }

                if (c == '\\' && !inMulti && !isVerbatim)
                {
                    // escaped char - blank both (verbatim strings use "" not \ to escape)
                    masked[i] = ' ';
                    if (i + 1 < chars.Length && chars[i + 1] != '\n' && chars[i + 1] != '\r')
                    {
                        masked[i + 1] = ' ';
                        i++;
                    }
                    continue;
                }

                if (c == '\n' && !inMulti && !isVerbatim)
                {
                    // unterminated single-line string - treat newline as terminator for masking
                    inString = false;
                    inMulti = false;
                    isInterpolated = false;
                    isVerbatim = false;
                    inInterpolationCode = false;
                    continue;
                }

                string? closing = null;
                if (inMulti)
                    closing = multiDelims.FirstOrDefault(d => MatchesAt(documentText, i, d));
                else if (delim != '\0' && MatchesAt(documentText, i, delim.ToString()))
                {
                    if (isVerbatim && i + 1 < chars.Length && chars[i + 1] == delim)
                    {
                        masked[i] = ' ';
                        masked[i + 1] = ' ';
                        i++;
                        continue;
                    }
                    closing = delim.ToString();
                }

                if (closing is not null)
                {
                    // keep closing delimiters as-is (so `x = "a"` still has quotes)
                    inString = false;
                    inMulti = false;
                    isInterpolated = false;
                    isVerbatim = false;
                    inInterpolationCode = false;
                    i += closing.Length - 1;
                    continue;
                }

                if (c != '\n' && c != '\r')
                    masked[i] = ' ';
                continue;
            }

            if (MatchesAt(documentText, i, commentLine))
            {
                for (var k = 0; k < commentLine.Length && i + k < chars.Length; k++)
                    if (masked[i + k] != '\n' && masked[i + k] != '\r')
                        masked[i + k] = ' ';
                inLineComment = true;
                i += commentLine.Length - 1;
                continue;
            }

            if (MatchesAt(documentText, i, blockStart))
            {
                for (var k = 0; k < blockStart.Length && i + k < chars.Length; k++)
                    if (masked[i + k] != '\n' && masked[i + k] != '\r')
                        masked[i + k] = ' ';
                inBlockComment = true;
                i += blockStart.Length - 1;
                continue;
            }

            var multi = multiDelims.FirstOrDefault(d => MatchesAt(documentText, i, d));
            if (multi is not null)
            {
                inString = true;
                inMulti = true;
                isInterpolated = false;
                isVerbatim = false;
                // check if this multi-line delimiter is interpolated (e.g., $""" )
                if (i > 0 && chars[i - 1] == '$')
                    isInterpolated = true;
                else if (i > 1 && chars[i - 2] == '$' && chars[i - 1] == '@')
                    isInterpolated = true;
                i += multi.Length - 1;
                continue;
            }

            if (c == '"' || (c == '\'' && !disableSingle))
            {
                // check for interpolated / verbatim prefix: $", $@", @$", @"
                var interpolated = false;
                var verbatim = false;
                if (i > 0 && chars[i - 1] == '$')
                    interpolated = true;
                else if (i > 0 && chars[i - 1] == '@')
                    verbatim = true;
                else if (i > 0 && (chars[i - 1] == 'r' || chars[i - 1] == 'R') &&
                         (i < 2 || !char.IsLetterOrDigit(chars[i - 2])))
                    verbatim = true; // Python/Rust raw string: backslash is literal, no doubled-quote escape
                if (i > 1 && ((chars[i - 2] == '$' && chars[i - 1] == '@') || (chars[i - 2] == '@' && chars[i - 1] == '$')))
                {
                    interpolated = true;
                    verbatim = true;
                }

                inString = true;
                inMulti = false;
                delim = c;
                isInterpolated = interpolated;
                isVerbatim = verbatim;
                inInterpolationCode = false;
                interpolationDepth = 0;
                continue;
            }
        }

        return new string(masked);
    }

    private static string? BuildFolderMaskedText(string folderPath, string currentFilePath, LoadedExtension? ext)
    {
        try
        {
            var currentExt = System.IO.Path.GetExtension(currentFilePath);
            var files = System.IO.Directory.EnumerateFiles(folderPath, "*", System.IO.SearchOption.AllDirectories)
                .Where(f => !f.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase))
                .Where(f => string.IsNullOrWhiteSpace(currentExt) || System.IO.Path.GetExtension(f).Equals(currentExt, StringComparison.OrdinalIgnoreCase))
                .Where(f =>
                {
                    var lower = f.ToLowerInvariant();
                    return !lower.Contains($"{System.IO.Path.DirectorySeparatorChar}.git{System.IO.Path.DirectorySeparatorChar}") &&
                           !lower.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}") &&
                           !lower.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}") &&
                           !lower.Contains($"{System.IO.Path.DirectorySeparatorChar}node_modules{System.IO.Path.DirectorySeparatorChar}") &&
                           !lower.Contains($"{System.IO.Path.DirectorySeparatorChar}.vs{System.IO.Path.DirectorySeparatorChar}");
                })
                .Take(400);

            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                try
                {
                    var info = new System.IO.FileInfo(f);
                    if (info.Length > 1_500_000) continue;
                    var text = System.IO.File.ReadAllText(f);
                    if (text.IndexOf('\0') >= 0) continue;
                    var masked = BuildMaskedDocument(text, ext);
                    sb.AppendLine(masked);
                }
                catch { }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlausibleVariableName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length > 64) return false;
        // Reject numeric-like or hyphenated artifacts that slipped through.
        if (char.IsDigit(name[0])) return false;
        if (name.Contains('-')) return false;
        return true;
    }

    private static bool IsFalsePositiveDeclaration(string scanText, string name, Regex pattern)
    {
        var trimmed = scanText.Trim();
        if (trimmed.StartsWith("[") && trimmed.Contains("=") && trimmed.Contains("]"))
        {
            var idxName = scanText.IndexOf(name, StringComparison.Ordinal);
            if (idxName > 0)
            {
                var before = scanText.Substring(0, idxName);
                if (before.Contains('[') || before.Contains('('))
                {
                    var beforeTrim = before.Trim();
                    if (!beforeTrim.EndsWith("var", StringComparison.Ordinal) &&
                        !beforeTrim.EndsWith("let", StringComparison.Ordinal) &&
                        !beforeTrim.EndsWith("const", StringComparison.Ordinal))
                        return true;
                }
            }
        }

        if (pattern == ColonAnnotatedAssignment)
        {
            // Extract content between ':' and '=' for this specific match
            var colonIdx = scanText.IndexOf(':', StringComparison.Ordinal);
            var eqIdx = scanText.IndexOf('=', colonIdx + 1);
            if (colonIdx >= 0 && eqIdx > colonIdx)
            {
                var typePart = scanText.Substring(colonIdx + 1, eqIdx - colonIdx - 1).Trim();
                var firstToken = typePart.Split(new[] { ' ', '\t', '<', '>', '[', ']', ',', '|', '?' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstToken))
                {
                    firstToken = firstToken.TrimEnd('?');
                    if (firstToken.Length > 0 && char.IsLower(firstToken[0]) && !KnownColonTypes.Contains(firstToken))
                    {
                        // If type part is exactly one identifier with no type syntax, reject
                        var tokens = typePart.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length == 1 && !typePart.Contains("<") && !typePart.Contains("[") && !typePart.Contains("|"))
                            return true;
                    }
                }
            }
        }

        var eqPos = scanText.IndexOf('=');
        if (eqPos > 0)
        {
            var beforeEq = scanText.Substring(0, eqPos);
            if (beforeEq.Contains('.') || beforeEq.Contains("->") || beforeEq.Contains("::"))
            {
                var nameIdx = beforeEq.LastIndexOf(name, StringComparison.Ordinal);
                if (nameIdx > 0 && beforeEq.Substring(0, nameIdx).Contains('.'))
                    return true;
                // Also reject bracket-like `arr[0] =` where LHS is not a simple identifier.
                if (beforeEq.Contains('[') || beforeEq.Contains(']'))
                    return true;
            }
            // Reject lines like `return x = 5`, `throw x = 5`, `yield x = 5`
            var beforeTrimLower = beforeEq.TrimStart().ToLowerInvariant();
            if (beforeTrimLower.StartsWith("return ") || beforeTrimLower.StartsWith("throw ") ||
                beforeTrimLower.StartsWith("yield ") || beforeTrimLower.StartsWith("await ") ||
                beforeTrimLower.StartsWith("case ") || beforeTrimLower.StartsWith("default:"))
                return true;
        }

        // Reject names that are language keywords even if ReservedWords missed
        if (name.Length >= 2 && name.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length >= 2 && name.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length >= 2 && name.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static IEnumerable<string> IdentifyVariableInitializations(string lineText)
    {
        if (string.IsNullOrWhiteSpace(lineText))
            return [];

        var trimmed = lineText.TrimStart();
        if (trimmed.StartsWith("//") || trimmed.StartsWith('#') ||
            trimmed.StartsWith('*') || trimmed.StartsWith("'''") || trimmed.StartsWith("\"\"\"") ||
            trimmed.StartsWith("--") || trimmed.StartsWith(';') ||
            trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("REM", StringComparison.OrdinalIgnoreCase))
            return [];

        var batchMatch = BatchSetDeclaration.Match(trimmed);
        if (batchMatch.Success)
        {
            var batchName = batchMatch.Groups[1].Value;
            return string.IsNullOrEmpty(batchName) ? [] : [batchName];
        }

        var scanText = TrailingLineComment.Replace(MaskStringLiterals(lineText), string.Empty);
        if (string.IsNullOrWhiteSpace(scanText))
            return [];

        if (!scanText.Contains('=')) return [];

        List<string>? found = null;
        foreach (var pattern in DeclarationPatterns)
        {
            foreach (Match match in pattern.Matches(scanText))
            {
                var name = match.Groups[1].Value;
                if (string.IsNullOrEmpty(name) || ReservedWords.Contains(name) || !IsPlausibleVariableName(name))
                    continue;
                if (IsFalsePositiveDeclaration(scanText, name, pattern))
                    continue;
                (found ??= []).Add(name);
            }
        }

        return found is null ? [] : found.Distinct(StringComparer.Ordinal);
    }

    public void ScanDocument(string fileKey, string documentText, LoadedExtension? languageExtension = null)
    {
        if (string.IsNullOrEmpty(fileKey))
            return;

        var variables = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(documentText))
        {
            var maskedDoc = BuildMaskedDocument(documentText, languageExtension);
            var lines = documentText.Split('\n');
            var maskedLines = maskedDoc.Split('\n');
            var depthAtLine = new int[lines.Length];
            var parenDepthAtLine = new int[lines.Length];
            var bracketDepthAtLine = new int[lines.Length];
            var curDepth = 0;
            var curParen = 0;
            var curBracket = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                depthAtLine[i] = curDepth;
                parenDepthAtLine[i] = curParen;
                bracketDepthAtLine[i] = curBracket;
                if (i < maskedLines.Length)
                {
                    curDepth += CountChar(maskedLines[i], '{') - CountChar(maskedLines[i], '}');
                    curParen += CountChar(maskedLines[i], '(') - CountChar(maskedLines[i], ')');
                    curBracket += CountChar(maskedLines[i], '[') - CountChar(maskedLines[i], ']');
                }
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var maskedLine = i < maskedLines.Length ? maskedLines[i] : lines[i];
                // Skip lines inside attribute brackets or paren kwarg contexts
                if (parenDepthAtLine[i] > 0 || bracketDepthAtLine[i] > 0)
                {
                    var t = maskedLine.Trim();
                    var isVarDecl = t.StartsWith("var ", StringComparison.Ordinal) ||
                                    t.StartsWith("let ", StringComparison.Ordinal) ||
                                    t.StartsWith("const ", StringComparison.Ordinal) ||
                                    t.StartsWith("val ", StringComparison.Ordinal);
                    if (!isVarDecl)
                        continue;
                }
                if (depthAtLine[i] > 0 && maskedLine.Trim().Length > 0 && char.IsUpper(maskedLine.Trim()[0]) && maskedLine.Contains("="))
                {
                    var t = maskedLine.Trim();
                    var isVarDecl2 = t.StartsWith("var ", StringComparison.Ordinal) ||
                                     t.StartsWith("let ", StringComparison.Ordinal) ||
                                     t.StartsWith("const ", StringComparison.Ordinal) ||
                                     t.StartsWith("val ", StringComparison.Ordinal);
                    if (!isVarDecl2) continue;
                }
                if (depthAtLine[i] > 0 && maskedLine.Contains("=") && maskedLine.TrimEnd().EndsWith(","))
                {
                    var t = maskedLine.Trim();
                    var isVarDecl3 = t.StartsWith("var ", StringComparison.Ordinal) ||
                                     t.StartsWith("let ", StringComparison.Ordinal) ||
                                     t.StartsWith("const ", StringComparison.Ordinal);
                    if (!isVarDecl3 && !t.Contains(";"))
                    {
                        // If the line looks like `prop = value,` with no semicolon, treat as property
                        var eqIdx = t.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            var lhs = t.Substring(0, eqIdx).Trim();
                            // If lhs is single identifier without type keywords, likely property
                            if (System.Text.RegularExpressions.Regex.IsMatch(lhs, @"^[A-Za-z_][A-Za-z0-9_]*$") && !lhs.Contains(" "))
                                continue;
                        }
                    }
                }

                foreach (var name in IdentifyVariableInitializations(maskedLine))
                {
                    if (name == "_" || name.StartsWith('_')) continue;
                    variables.Add(name);
                }
            }
        }

        _variablesByFile[fileKey] = variables;
    }

    public void ForgetFile(string fileKey) => _variablesByFile.Remove(fileKey);

    private static readonly Regex KeywordFunctionDeclaration = new(
        @"\b(?:function|def|fn|func|sub|proc)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CStyleFunctionDeclaration = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex BlockTerminatorStatement = new(
        @"^\s*(?:return\b|throw\b|break\b|continue\b)[^{}]*;?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PossibleJumpTarget = new(
        @"^(?:case\b|default\s*:|[A-Za-z_][A-Za-z0-9_]*\s*:)",
        RegexOptions.Compiled);

    private static readonly Regex ClosingBraceOnlyLine = new(@"^\}+;?$", RegexOptions.Compiled);

    private static int ContentLength(string line) => line.EndsWith('\r') ? line.Length - 1 : line.Length;

    private static readonly HashSet<string> EntryPointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "wmain", "winmain", "wwinmain", "dllmain", "_start",
    };

    private static int CountChar(string text, char c)
    {
        var count = 0;
        foreach (var ch in text)
            if (ch == c) count++;
        return count;
    }

    public sealed class DeadCodeSpan
    {
        public int StartOffset { get; }
        public int Length { get; }
        public string Reason { get; }

        public DeadCodeSpan(int startOffset, int length, string reason)
        {
            StartOffset = startOffset;
            Length = length;
            Reason = reason;
        }
    }

    public List<DeadCodeSpan> FindDeadCode(string documentText, LoadedExtension? languageExtension = null, string? folderPath = null, string? currentFilePath = null)
    {
        var spans = new List<DeadCodeSpan>();
        if (string.IsNullOrEmpty(documentText))
            return spans;

        var ignoreNames = languageExtension?.DeadCodeIgnore is { Length: > 0 } ignoreArr
            ? new HashSet<string>(ignoreArr, StringComparer.OrdinalIgnoreCase)
            : null;
        var entryPoints = languageExtension?.DeadCodeEntryPoints is { Length: > 0 } entryArr
            ? new HashSet<string>(entryArr, StringComparer.OrdinalIgnoreCase)
            : null;

        var lines = documentText.Split('\n');
        var lineStart = new int[lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStart[i] = offset;
            offset += lines[i].Length + 1; // +1 accounts for the '\n' consumed by Split.
        }

        var maskedDoc = BuildMaskedDocument(documentText, languageExtension);
        var masked = maskedDoc.Split('\n');

        string? folderMaskedText = null;
        if (!string.IsNullOrWhiteSpace(folderPath) && !string.IsNullOrWhiteSpace(currentFilePath) && System.IO.Directory.Exists(folderPath))
            folderMaskedText = BuildFolderMaskedText(folderPath, currentFilePath, languageExtension);

        int CountWholeWord(string name)
        {
            var count = Regex.Matches(maskedDoc, $@"\b{Regex.Escape(name)}\b").Count;
            if (folderMaskedText is not null)
                count += Regex.Matches(folderMaskedText, $@"\b{Regex.Escape(name)}\b").Count;
            return count;
        }

        var depthAtLine = new int[lines.Length];
        var parenDepthAtLine = new int[lines.Length];
        var bracketDepthAtLine = new int[lines.Length];
        var curDepthForVar = 0;
        var curParenDepthForVar = 0;
        var curBracketDepthForVar = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            depthAtLine[i] = curDepthForVar;
            parenDepthAtLine[i] = curParenDepthForVar;
            bracketDepthAtLine[i] = curBracketDepthForVar;
            curDepthForVar += CountChar(masked[i], '{') - CountChar(masked[i], '}');
            curParenDepthForVar += CountChar(masked[i], '(') - CountChar(masked[i], ')');
            curBracketDepthForVar += CountChar(masked[i], '[') - CountChar(masked[i], ']');
        }

        var seenVariable = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var name in IdentifyVariableInitializations(masked[i]))
            {
                if (!seenVariable.Add(name)) continue; // only flag a name's first declaration
                if (ignoreNames is not null && ignoreNames.Contains(name)) continue;

                if (name == "_" || name.StartsWith('_')) continue;

                var trimmedForProp = masked[i].Trim();

                if (parenDepthAtLine[i] > 0 || bracketDepthAtLine[i] > 0)
                {
                    var isVarDeclInArg = trimmedForProp.StartsWith("var ", StringComparison.Ordinal) ||
                                         trimmedForProp.StartsWith("let ", StringComparison.Ordinal) ||
                                         trimmedForProp.StartsWith("const ", StringComparison.Ordinal) ||
                                         trimmedForProp.StartsWith("val ", StringComparison.Ordinal);
                    if (!isVarDeclInArg)
                        continue;
                }

                if (depthAtLine[i] > 0 && trimmedForProp.Contains("="))
                {
                    var isVarDecl = trimmedForProp.StartsWith("var ", StringComparison.Ordinal) ||
                                    trimmedForProp.StartsWith("let ", StringComparison.Ordinal) ||
                                    trimmedForProp.StartsWith("const ", StringComparison.Ordinal) ||
                                    trimmedForProp.StartsWith("val ", StringComparison.Ordinal);
                    if (!isVarDecl)
                    {
                        if (trimmedForProp.Length > 0 && char.IsUpper(trimmedForProp[0]))
                            continue;
                        if (trimmedForProp.TrimEnd().EndsWith(",") && !trimmedForProp.Contains(";"))
                        {
                            var eqIdx = trimmedForProp.IndexOf('=');
                            if (eqIdx > 0)
                            {
                                var lhs = trimmedForProp.Substring(0, eqIdx).Trim();
                                if (Regex.IsMatch(lhs, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                                    continue;
                            }
                        }
                    }
                }

                if (CountWholeWord(name) <= 1)
                    spans.Add(new DeadCodeSpan(lineStart[i], ContentLength(lines[i]), "Unused variable"));
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = masked[i];
            string? name = null;

            var keywordMatch = KeywordFunctionDeclaration.Match(line);
            if (keywordMatch.Success)
            {
                name = keywordMatch.Groups[1].Value;
            }
            else
            {
                var cStyleMatch = CStyleFunctionDeclaration.Match(line);
                if (cStyleMatch.Success && !ReservedWords.Contains(cStyleMatch.Groups[1].Value))
                    name = cStyleMatch.Groups[1].Value;
            }

            if (name is null || EntryPointNames.Contains(name)) continue;
            if (entryPoints is not null && entryPoints.Contains(name)) continue;
            if (ignoreNames is not null && ignoreNames.Contains(name)) continue;

            var refCount = CountWholeWord(name);
            if (refCount > 1) continue;

            var endLine = i;
            var depth = CountChar(line, '{') - CountChar(line, '}');
            if (depth > 0)
            {
                var j = i + 1;
                while (j < lines.Length && depth > 0)
                {
                    depth += CountChar(masked[j], '{') - CountChar(masked[j], '}');
                    j++;
                }
                endLine = Math.Min(j - 1, lines.Length - 1);
            }

            var start = lineStart[i];
            var end = lineStart[endLine] + ContentLength(lines[endLine]);
            spans.Add(new DeadCodeSpan(start, end - start, "Unused function"));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (!BlockTerminatorStatement.IsMatch(masked[i]))
                continue;
            if (IsConditionalTerminator(masked, i))
                continue;

            var depth = 0;
            var deadStart = -1;
            var deadEnd = -1;
            var j = i + 1;
            while (j < lines.Length)
            {
                var trimmed = masked[j].Trim();
                if (trimmed.Length == 0) { j++; continue; }
                if (ClosingBraceOnlyLine.IsMatch(trimmed)) break; // end of the enclosing block
                if (PossibleJumpTarget.IsMatch(trimmed)) break;   // could still be jumped to

                if (deadStart < 0) deadStart = j;

                var lineDepthChange = CountChar(masked[j], '{') - CountChar(masked[j], '}');
                if (depth + lineDepthChange < 0) break; // this line closes the enclosing block too
                depth += lineDepthChange;
                deadEnd = j;
                j++;
            }

            if (deadStart >= 0 && deadEnd >= deadStart)
            {
                var start = lineStart[deadStart];
                var end = lineStart[deadEnd] + ContentLength(lines[deadEnd]);
                spans.Add(new DeadCodeSpan(start, end - start, "Unreachable code"));
                i = deadEnd; // skip past the section we just flagged
            }
        }

        return spans;
    }

    public IReadOnlyCollection<string> GetVariables(string fileKey) =>
        _variablesByFile.TryGetValue(fileKey, out var vars) ? vars : Array.Empty<string>();

    public sealed class ErrorSpan
    {
        public int StartOffset { get; }
        public int Length { get; }
        public string Message { get; }

        public ErrorSpan(int startOffset, int length, string message)
        {
            StartOffset = startOffset;
            Length = Math.Max(1, length);
            Message = message;
        }
    }

    private static readonly Regex BlockKeywordLine = new(
        @"^\s*(?:if|elif|else|for|while|def|class|try|except|finally|with)\b",
        RegexOptions.Compiled);

    private static readonly Regex ColonStyleSample = new(
        @"^\s*(?:if|elif|else|for|while|def|class|try|except|finally|with)\b.*:\s*$",
        RegexOptions.Compiled);

    private static readonly Regex StatementSafeLineEnd = new(
        @"[;{}:,\\+\-*/%&|^~<>=!\[(]\s*$|^\s*$|^\s*(?://|#|\*|/\*)|^\s*@|^\s*\)|^\s*\}",
        RegexOptions.Compiled);

    // Preprocessor / attribute / label lines that never take a trailing ';'.
    private static readonly Regex StatementExemptLine = new(
        @"^\s*(?:#|@|\[|using\s+[\w.]+\s*;?\s*$|namespace\b|package\b|import\b|from\b|module\b)",
        RegexOptions.Compiled);

    private static readonly Regex NoSemicolonControlHeader = new(
        @"^\s*(?:if|else|else\s+if|for|foreach|while|do|try|catch|finally|using|lock|switch|case|default)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NoSemicolonTypeHeader = new(
        @"^\s*(?:(?:public|private|protected|internal|static|abstract|virtual|override|sealed|async|extern|unsafe|partial|readonly|const|volatile|new)\s+)*(?:class|struct|interface|enum|record|namespace|void)\b",
        RegexOptions.Compiled);

    private static readonly Regex NoSemicolonMethodHeader = new(
        @"^\s*(?:(?:public|private|protected|internal|static|abstract|virtual|override|sealed|async|extern|unsafe|partial|readonly)\s+)*(?:\w+(?:<[^>]+>)?\s+)+\w+\s*\(.*\)\s*$",
        RegexOptions.Compiled);

    private static string? GetNextNonEmptyLine(string[] maskedLines, int lineIndex)
    {
        for (var j = lineIndex + 1; j < maskedLines.Length; j++)
        {
            var next = maskedLines[j].Trim();
            if (next.Length == 0) continue;
            return next;
        }
        return null;
    }

    private static bool IsNoSemicolonNeeded(string trimmedEnd, string trimmedNoIndent, int lineIndex, string[] maskedLines)
    {
        if (trimmedNoIndent.StartsWith("?") || trimmedNoIndent.StartsWith(":") ||
            trimmedNoIndent.StartsWith(".") || trimmedNoIndent.StartsWith("+") ||
            trimmedNoIndent.StartsWith(","))
            return true;

        if (NoSemicolonControlHeader.IsMatch(trimmedNoIndent) || NoSemicolonTypeHeader.IsMatch(trimmedNoIndent))
            return true;
        if (NoSemicolonMethodHeader.IsMatch(trimmedEnd))
            return true;

        var next = GetNextNonEmptyLine(maskedLines, lineIndex);
        if (next is not null)
        {
            if (next.StartsWith("{") && trimmedEnd.Contains("= new"))
                return true;
            if (next.StartsWith("{") && trimmedEnd.TrimEnd().EndsWith("=", StringComparison.Ordinal))
                return true;
            if (next.StartsWith("{") && trimmedEnd.Contains("=") && !trimmedEnd.Contains(";") && trimmedEnd.Contains("new"))
                return true;
            // Ternary continuation: `Text = isTerminating` + `? "..."` or `? "..."` + `:`
            if ((next.StartsWith("?") || next.StartsWith(":") || next.StartsWith(".")) && trimmedEnd.Contains("="))
                return true;
            if ((next.StartsWith(".") || next.StartsWith("+")) && !trimmedEnd.EndsWith(";", StringComparison.Ordinal) && !trimmedEnd.EndsWith("{", StringComparison.Ordinal))
                return true;
            // Header like `public partial class App : Application` + `{`
            if (next.StartsWith("{") && (trimmedNoIndent.Contains("class") || trimmedNoIndent.Contains("struct") ||
                trimmedNoIndent.Contains("interface") || trimmedNoIndent.Contains("enum") ||
                trimmedNoIndent.Contains("record") || trimmedNoIndent.Contains("namespace")))
                return true;
            // Control header with `)` + `{` like `if (x)` + `{` or `void Foo()` + `{`
            if (trimmedEnd.EndsWith(")", StringComparison.Ordinal) && next.StartsWith("{"))
                return true;
            // Fallback: any `class`/`struct` etc without `=`/`;` before `{`
            if (next.StartsWith("{") && !trimmedEnd.Contains("=") && !trimmedEnd.Contains(";"))
                return true;
            // Multi-line expression: current line is `? "..."` and next is `:`
            if (trimmedNoIndent.StartsWith("?") && next.StartsWith(":"))
                return true;
            if (trimmedNoIndent.StartsWith(":") && (next.StartsWith(",") || next.StartsWith("}")))
                return true;
        }

        // Line itself ends with `=` (e.g. `Children =`) – continuation, not missing `;`
        if (trimmedEnd.EndsWith("=", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool IsConditionalTerminator(string[] masked, int terminatorIndex)
    {
        var line = masked[terminatorIndex].Trim();
        if (Regex.IsMatch(line, @"\bif\s*\(.*\)\s*(?:return|throw|break|continue)\b"))
            return true;
        if (Regex.IsMatch(line, @"\belse\b.*\b(?:return|throw|break|continue)\b"))
            return true;
        // `if (cond)` on previous line and `return;` on this line without `{`
        for (var k = terminatorIndex - 1; k >= 0; k--)
        {
            var prev = masked[k].Trim();
            if (prev.Length == 0) continue;
            if (Regex.IsMatch(prev, @"\bif\s*\(.*\)\s*$") || Regex.IsMatch(prev, @"\belse\b\s*$"))
            {
                if (Regex.IsMatch(line, @"^\s*(?:return|throw|break|continue)\b") && !prev.Contains("{"))
                    return true;
            }
            break;
        }
        return false;
    }

    private static readonly Regex EmptyAssignment = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*[A-Za-z_][A-Za-z0-9_.<>\[\],\s]*)?" + NotCompoundOrArrow + @"\s*$",
        RegexOptions.Compiled);

    private static int LevenshteinDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }

    public List<ErrorSpan> FindErrors(string documentText, LoadedExtension? languageExtension = null)
    {
        var spans = new List<ErrorSpan>();
        if (string.IsNullOrEmpty(documentText))
            return spans;

        var commentLine = languageExtension?.CommentLine is { Length: > 0 } cl ? cl : "//";
        var blockStart = languageExtension?.CommentBlockStart is { Length: > 0 } bs ? bs : "/*";
        var blockEnd = languageExtension?.CommentBlockEnd is { Length: > 0 } be ? be : "*/";
        var multiLineStringDelims = languageExtension?.MultiLineStringDelimiters is { Length: > 0 } mls
            ? mls
            : Array.Empty<string>();

        var stack = new Stack<(char Bracket, int Offset)>();
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var stringOpenOffset = -1;
        var stringDelimiter = '\0';
        var inMultiLineString = false;
        var inVerbatimString = false;

        for (var i = 0; i < documentText.Length; i++)
        {
            var c = documentText[i];

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (MatchesAt(documentText, i, blockEnd)) { inBlockComment = false; i += blockEnd.Length - 1; }
                continue;
            }

            if (inString)
            {
                if (c == '\\' && !inMultiLineString && !inVerbatimString) { i++; continue; }
                if (c == '\n' && !inMultiLineString && !inVerbatimString)
                {
                    spans.Add(new ErrorSpan(stringOpenOffset, 1, "Unterminated string literal"));
                    inString = false;
                    continue;
                }
                if (inVerbatimString && c == stringDelimiter)
                {
                    if (i + 1 < documentText.Length && documentText[i + 1] == stringDelimiter)
                    {
                        i++;
                        continue;
                    }
                    inString = false;
                    inVerbatimString = false;
                    continue;
                }
                var closingDelim = inMultiLineString
                    ? multiLineStringDelims.FirstOrDefault(d => MatchesAt(documentText, i, d))
                    : (MatchesAt(documentText, i, stringDelimiter.ToString()) ? stringDelimiter.ToString() : null);
                if (closingDelim is not null)
                {
                    inString = false;
                    inMultiLineString = false;
                    i += closingDelim.Length - 1;
                }
                continue;
            }

            if (MatchesAt(documentText, i, commentLine)) { inLineComment = true; i += commentLine.Length - 1; continue; }
            if (MatchesAt(documentText, i, blockStart)) { inBlockComment = true; i += blockStart.Length - 1; continue; }

            var multiDelim = multiLineStringDelims.FirstOrDefault(d => MatchesAt(documentText, i, d));
            if (multiDelim is not null)
            {
                inString = true;
                inMultiLineString = true;
                stringOpenOffset = i;
                i += multiDelim.Length - 1;
                continue;
            }

            if (c is '"' or '\'')
            {
                var prefixStart = i;
                while (prefixStart > 0 && documentText[prefixStart - 1] is '@' or '$')
                    prefixStart--;
                var hasAtPrefix = prefixStart < i && documentText[prefixStart..i].Contains('@');
                var hasRPrefix = i > 0 && (documentText[i - 1] is 'r' or 'R') &&
                                  (i < 2 || !char.IsLetterOrDigit(documentText[i - 2]));

                inString = true;
                inMultiLineString = false;
                inVerbatimString = hasAtPrefix || hasRPrefix;
                stringDelimiter = c;
                stringOpenOffset = i;
                continue;
            }

            switch (c)
            {
                case '(' or '{' or '[':
                    stack.Push((c, i));
                    break;
                case ')' or '}' or ']':
                    var expected = c switch { ')' => '(', '}' => '{', _ => '[' };
                    if (stack.Count > 0 && stack.Peek().Bracket == expected)
                    {
                        stack.Pop();
                    }
                    else
                    {
                        spans.Add(new ErrorSpan(i, 1, $"Unexpected '{c}' - no matching '{expected}'"));
                    }
                    break;
            }
        }

        if (inString && !inMultiLineString)
            spans.Add(new ErrorSpan(stringOpenOffset, 1, "Unterminated string literal"));
        else if (inString && inMultiLineString)
            spans.Add(new ErrorSpan(stringOpenOffset, 1, "Unterminated string literal (unclosed multi-line string)"));

        foreach (var (bracket, offset) in stack)
        {
            var expectedClose = bracket switch { '(' => ')', '{' => '}', _ => ']' };
            spans.Add(new ErrorSpan(offset, 1, $"'{bracket}' is never closed it's missing a '{expectedClose}'"));
        }

        var lines = documentText.Split('\n');
        var lineStart = new int[lines.Length];
        var offsetAcc = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStart[i] = offsetAcc;
            offsetAcc += lines[i].Length + 1;
        }

        var maskedDocForLines = BuildMaskedDocument(documentText, languageExtension);
        var masked = maskedDocForLines.Split('\n');

        var nonBlankLines = masked.Count(l => l.Trim().Length > 0);
        var semicolonLines = masked.Count(l => l.TrimEnd().EndsWith(';'));
        var looksSemicolonStyle = semicolonLines >= 3 && semicolonLines >= nonBlankLines * 0.5;
        var looksColonStyle = !looksSemicolonStyle && masked.Any(l => ColonStyleSample.IsMatch(l));

        var knownKeywords = languageExtension?.Keywords is { Length: > 0 } kw
            ? new HashSet<string>(kw, StringComparer.Ordinal)
            : null;

        var variableNamesInFile = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ml in masked)
        {
            foreach (var v in IdentifyVariableInitializations(ml))
                variableNamesInFile.Add(v);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = masked[i];
            var trimmed = line.TrimEnd('\r');
            var trimmedEnd = trimmed.TrimEnd();
            var trimmedNoIndent = trimmedEnd.TrimStart();

            if (looksSemicolonStyle && trimmedNoIndent.Length > 0 &&
                !StatementSafeLineEnd.IsMatch(trimmedEnd) && !StatementExemptLine.IsMatch(trimmedNoIndent) &&
                !IsNoSemicolonNeeded(trimmedEnd, trimmedNoIndent, i, masked))
            {
                var contentLen = ContentLength(lines[i]);
                if (contentLen > 0)
                    spans.Add(new ErrorSpan(lineStart[i] + contentLen - 1, 1, "Missing ';'"));
            }

            if (looksColonStyle && BlockKeywordLine.IsMatch(trimmedNoIndent) &&
                !trimmed.TrimEnd().EndsWith(':') && !trimmed.TrimEnd().EndsWith('\\') &&
                trimmedNoIndent.Length > 0)
            {
                var contentLen = ContentLength(lines[i]);
                if (contentLen > 0)
                    spans.Add(new ErrorSpan(lineStart[i] + contentLen - 1, 1, "Missing ':'"));
            }

            var emptyAssignMatch = EmptyAssignment.Match(trimmedNoIndent);
            if (emptyAssignMatch.Success &&
                !ReservedWords.Contains(emptyAssignMatch.Groups[1].Value) &&
                !BatchSetDeclaration.IsMatch(trimmedNoIndent))
            {
                var nextForEmpty = GetNextNonEmptyLine(masked, i);
                if (nextForEmpty is not null &&
                    (nextForEmpty.StartsWith("{") || nextForEmpty.StartsWith("?") || nextForEmpty.StartsWith(":") ||
                     nextForEmpty.StartsWith("\"") || nextForEmpty.StartsWith("'") || nextForEmpty.StartsWith("new", StringComparison.Ordinal) ||
                     nextForEmpty.StartsWith("(") || nextForEmpty.StartsWith("[")))
                {
                }
                else
                {
                    var contentLen = ContentLength(lines[i]);
                    if (contentLen > 0)
                        spans.Add(new ErrorSpan(
                            lineStart[i], contentLen,
                            $"'{emptyAssignMatch.Groups[1].Value}' declares nothing, expected a value after '='"));
                }
            }

            if (knownKeywords is not null)
            {
                var wordMatch = Regex.Match(trimmedNoIndent, @"^([A-Za-z_][A-Za-z0-9_]*)\b");
                if (wordMatch.Success)
                {
                    var word = wordMatch.Groups[1].Value;
                    if (word.Length >= 4 && !knownKeywords.Contains(word) && !variableNamesInFile.Contains(word))
                    {
                        var afterIdx = wordMatch.Index + word.Length;
                        var afterWord = afterIdx < trimmedNoIndent.Length ? trimmedNoIndent.Substring(afterIdx).TrimStart() : string.Empty;
                        if (afterWord.StartsWith(".") || afterWord.StartsWith("(") || afterWord.StartsWith(":") || afterWord.StartsWith("="))
                        {
                        }
                        else
                        {
                            var closest = knownKeywords.FirstOrDefault(k => Math.Abs(k.Length - word.Length) <= 1 && LevenshteinDistance(word, k) == 1);
                            if (closest is not null)
                            {
                                var wordOffsetInLine = trimmed.Length - trimmedNoIndent.Length;
                                spans.Add(new ErrorSpan(
                                    lineStart[i] + wordOffsetInLine,
                                    word.Length,
                                    $"Possibly misspelled '{word}', did you mean '{closest}'?"));
                            }
                        }
                    }
                }
            }
        }

        return spans;
    }

    private static bool MatchesAt(string text, int index, string token)
    {
        if (string.IsNullOrEmpty(token) || index + token.Length > text.Length)
            return false;
        for (var k = 0; k < token.Length; k++)
            if (text[index + k] != token[k]) return false;
        return true;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static int FindWordStart(string documentText, int caretOffset)
    {
        var start = caretOffset;
        while (start > 0 && IsWordChar(documentText[start - 1]))
            start--;
        return start;
    }

    private static IReadOnlyCollection<string> GetEnclosingCallNames(string documentText, int caretOffset)
    {
        if (string.IsNullOrEmpty(documentText) || caretOffset <= 0)
            return Array.Empty<string>();

        var callStack = new Stack<string?>();
        var token = new List<char>();
        var inString = false;
        var stringDelimiter = '\0';
        var escaped = false;
        var inLineComment = false;

        static string? FlushToken(List<char> chars)
        {
            if (chars.Count == 0)
                return null;
            var value = new string(chars.ToArray());
            chars.Clear();
            return value;
        }

        for (var i = 0; i < caretOffset; i++)
        {
            var c = documentText[i];

            if (inLineComment)
            {
                if (c == '\n')
                    inLineComment = false;
                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == stringDelimiter)
                {
                    inString = false;
                    stringDelimiter = '\0';
                }

                continue;
            }

            if (c == '/' && i + 1 < caretOffset && documentText[i + 1] == '/')
            {
                inLineComment = true;
                token.Clear();
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                stringDelimiter = c;
                token.Clear();
                continue;
            }

            if (IsWordChar(c))
            {
                token.Add(c);
                continue;
            }

            if (c == '(')
            {
                callStack.Push(FlushToken(token));
                continue;
            }

            token.Clear();

            if (c == ')')
            {
                if (callStack.Count > 0)
                    callStack.Pop();
            }
        }

        return callStack.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray();
    }

    public List<InsightSuggestion> GetSuggestions(
        string prefix,
        string fileKey,
        LoadedExtension? languageExtension,
        string documentText,
        int caretOffset,
        int maxResults = 25)
    {
        var results = new List<InsightSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var enclosingCalls = GetEnclosingCallNames(documentText, caretOffset);
        var blacklist = languageExtension?.Blacklist is { Length: > 0 }
            ? new HashSet<string>(languageExtension.Blacklist, StringComparer.OrdinalIgnoreCase)
            : null;

        void AddCandidates(IEnumerable<string> names, InsightKind kind)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!string.IsNullOrEmpty(prefix) &&
                    !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (blacklist is not null && enclosingCalls.Any(call => blacklist.Contains(call) && string.Equals(call, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!seen.Add(name)) continue;
                results.Add(new InsightSuggestion(name, kind));
            }
        }

        AddCandidates(GetVariables(fileKey), InsightKind.Variable);

        if (languageExtension is not null)
        {
            AddCandidates(languageExtension.Functions, InsightKind.Function);
            AddCandidates(languageExtension.Properties, InsightKind.Property);
            AddCandidates(languageExtension.Types, InsightKind.Type);
            AddCandidates(languageExtension.Namespaces, InsightKind.Namespace);
            AddCandidates(languageExtension.Keywords, InsightKind.Keyword);
        }

        return results
            .Where(s => !string.Equals(s.Text, prefix, StringComparison.Ordinal))
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }
}