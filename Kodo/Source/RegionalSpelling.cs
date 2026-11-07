// Licensed under the GNU GPL-v3.0
using System;

namespace Kodo;

internal static class RegionalSpelling
{
    public static string LocaleCode { get; set; } = "GB";

    public static bool IsAmericanEnglish => LocaleCode == "US";

    public static string Color => IsAmericanEnglish ? "color" : "colour";

    public static string Cancelled => IsAmericanEnglish ? "canceled" : "cancelled";

    public static string Behavior => IsAmericanEnglish ? "behavior" : "behaviour";

    public static string Personalize => LocaleCode is "US" or "PH" or "CA" ? "personalize" : "personalise";

    public static string Personalization => LocaleCode is "US" or "PH" or "CA" ? "personalization" : "personalisation";

    public static string Customize => LocaleCode is "US" or "PH" or "CA" ? "customize" : "customise";

    public static string Gray => LocaleCode is "US" or "PH" ? "gray" : "grey";

    public static string Organize => LocaleCode is "US" or "PH" or "CA" ? "organize" : "organise";

    public static string Center => IsAmericanEnglish ? "center" : "centre";

    public static string ApplyTutorial(string text) => text
        .Replace("accent colour", $"accent {Color}", StringComparison.Ordinal)
        .Replace("accent color", $"accent {Color}", StringComparison.Ordinal)
        .Replace("personalize", Personalize, StringComparison.Ordinal)
        .Replace("personalise", Personalize, StringComparison.Ordinal)
        .Replace("behavior", Behavior, StringComparison.Ordinal)
        .Replace("behaviour", Behavior, StringComparison.Ordinal);

    public static void SetFromUserCountry(string? userCountry)
    {
        LocaleCode = Normalize(userCountry);
    }

    private static string Normalize(string? userCountry) => (userCountry ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "US" => "US",
        "PH" => "US",
        "CA" => "CA",
        "GB" => "GB",
        "IE" => "GB",
        "AU" => "AU",
        "NZ" => "AU",
        "ZA" => "GB",
        "IN" => "GB",
        "SG" => "GB",
        "NG" => "GB",
        "KE" => "GB",
        "GH" => "GB",
        "PK" => "GB",
        "BD" => "GB",
        "LK" => "GB",
        "MT" => "GB",
        "CY" => "GB",
        "IL" => "GB",
        "AE" => "GB",
        "QA" => "GB",
        "KW" => "GB",
        "SA" => "GB",
        "OM" => "GB",
        "JO" => "GB",
        "LB" => "GB",
        "EG" => "GB",
        "MA" => "GB",
        "TN" => "GB",
        "DZ" => "GB",
        "LY" => "GB",
        "SD" => "GB",
        _ => "GB",
    };
}
