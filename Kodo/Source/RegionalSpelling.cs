// Licensed under the GNU GPL-v3.0
namespace Kodo;

internal static class RegionalSpelling
{
    public static bool IsAmericanEnglish { get; set; }

    public static string Cancelled => IsAmericanEnglish ? "canceled" : "cancelled";
}
