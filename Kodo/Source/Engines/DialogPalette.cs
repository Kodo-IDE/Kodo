// Licensed under the GNU GPL-v3.0
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kodo.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Shared = Kodo.HotfixShared.HotfixShared;

namespace Kodo;

internal static class DialogPalette
{
    public static readonly Color Surface = Color.Parse("#1E1E1E");
    public static readonly Color SurfaceDeep = Color.Parse("#1A1A1A");
    public static readonly Color Border = Color.Parse("#3A3A3A");
    public static readonly Color BadgeBg = Color.Parse("#2B2B2B");
    public static readonly Color Text = Color.Parse("#F4F4F4");
    public static readonly Color TextMuted = Color.Parse("#A0A0A0");
    public static readonly Color TextDim = Color.Parse("#606060");
    public static readonly Color TokenBlue = Color.Parse("#9CDCFE");
    public static readonly Color TokenOrange = Color.Parse("#CE9178");
}
