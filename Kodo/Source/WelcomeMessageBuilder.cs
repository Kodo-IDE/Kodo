// Licensed under GPL-v3.0
using System;
using System.Collections.Generic;

namespace Kodo;

internal static class WelcomeMessageBuilder
{

    private record HolidayEntry(string Name, string? Greeting);

    private static HolidayEntry? GetHolidayEntry(DateTime date, string country)
    {
        var m = date.Month;
        var d = date.Day;
        var dow = date.DayOfWeek;
        var y = date.Year;

        if (m == 1 && d == 1) return new("New Year's Day", "Happy New Year!");
        if (m == 12 && d == 31) return new("New Year's Eve", "Happy New Year's Eve!");
        if (m == 12 && d == 25) return new("Christmas Day", "Merry Christmas!");
        if (m == 12 && d == 24) return new("Christmas Eve", "Happy Christmas Eve!");
        if (m == 12 && d == 26 && country is "CA" or "GB" or "AU" or "NZ" or "ZA")
            return new("Boxing Day", "Happy Boxing Day!");
        if (m == 12 && d == 26) return new("Kwanzaa", "Happy Kwanzaa!");
        if (m == 10 && d == 31) return new("Halloween", "Happy Halloween!");
        if (m == 2 && d == 14) return new("Valentine's Day", "Happy Valentine's Day!");
        if (m == 4 && d == 1) return new("April Fools' Day", "Happy April Fools'! (Or is it?)");
        if (m == 3 && d == 8) return new("International Women's Day", "Happy International Women's Day!");
        if (m == 4 && d == 22) return new("Earth Day", "Happy Earth Day!");
        if (m == 5 && d == 5) return new("Cinco de Mayo", "¡Feliz Cinco de Mayo!");
        if (m == 6 && d == 5) return new("World Environment Day", "Happy World Environment Day!");
        if (m == 9 && d == 21) return new("International Day of Peace", "Happy International Day of Peace!");
        if (m == 12 && d == 10) return new("International Human Rights Day", "Happy Human Rights Day!");

        if (m == 5 && dow == DayOfWeek.Sunday && d >= 8 && d <= 14)
            return new("Mother's Day", "Happy Mother's Day!");

        if (m == 6 && dow == DayOfWeek.Sunday && d >= 15 && d <= 21)
            return new("Father's Day", "Happy Father's Day!");

        var easter = ComputeEaster(y);
        if (m == easter.Month && d == easter.Day)
            return new("Easter Sunday", "Happy Easter!");
        if (date == easter.AddDays(-2))
            return new("Good Friday", "Wishing you a peaceful Good Friday.");
        if (date == easter.AddDays(1) && country is "CA" or "GB" or "AU" or "NZ")
            return new("Easter Monday", "Happy Easter Monday!");

        if (LunarNewYear(y) is { } lny && m == lny.Month && d == lny.Day)
            return new("Lunar New Year", "Happy Lunar New Year!");

        if (HoliDate(y) is { } holi && m == holi.Month && d == holi.Day)
            return new("Holi", "Happy Holi!");

        if (VesakDate(y) is { } vesak && m == vesak.Month && d == vesak.Day)
            return new("Vesak", "Happy Vesak!");

        if (EidAlFitr(y) is { } eidFitr && m == eidFitr.Month && d == eidFitr.Day)
            return new("Eid al-Fitr", "Eid Mubarak!");

        if (EidAlAdha(y) is { } eidAdha && m == eidAdha.Month && d == eidAdha.Day)
            return new("Eid al-Adha", "Eid Mubarak!");

        if (RoshHashanah(y) is { } rosh && m == rosh.Month && d == rosh.Day)
            return new("Rosh Hashanah", "Shana Tova! Happy New Year!");

        if (YomKippur(y) is { } yk && m == yk.Month && d == yk.Day)
            return new("Yom Kippur", "G'mar Chatima Tova. Easy fast.");

        // Navratri / Sharad Navratri (day after new moon of Ashwin)
        if (NavratriDate(y) is { } nav && m == nav.Month && d == nav.Day)
            return new("Navratri", "Happy Navratri!");

        if (DiwaliDate(y) is { } diwali && m == diwali.Month && d == diwali.Day)
            return new("Diwali", "Happy Diwali!");

        if (HanukkahDate(y) is { } hanukkah && m == hanukkah.Month && d == hanukkah.Day)
            return new("Hanukkah", "Happy Hanukkah!");

        if (country == "CA")
        {
            if (m == 7 && d == 1) return new("Canada Day", "Happy Canada Day!");
            if (m == 11 && d == 11) return new("Remembrance Day", "Lest we forget.");
            if (m == 5 && dow == DayOfWeek.Monday && d >= 18 && d <= 24)
                return new("Victoria Day", "Happy Victoria Day! Enjoy the long weekend.");
            if (m == 9 && dow == DayOfWeek.Monday && d <= 7)
                return new("Labour Day", "Happy Labour Day! Enjoy the long weekend.");
            if (m == 10 && dow == DayOfWeek.Monday && d >= 8 && d <= 14)
                return new("Thanksgiving", "Happy Thanksgiving!");
            if (m == 2 && dow == DayOfWeek.Monday && d >= 15 && d <= 21)
                return new("Family Day", "Happy Family Day! Enjoy the long weekend.");
        }

        if (country == "US")
        {
            if (m == 7 && d == 4) return new("Independence Day", "Happy Fourth of July!");
            if (m == 11 && d == 11) return new("Veterans Day", "Thank you to all who have served.");
            if (m == 11 && dow == DayOfWeek.Thursday && d >= 22 && d <= 28)
                return new("Thanksgiving", "Happy Thanksgiving! (and happy coding after dinner)");
            if (m == 5 && dow == DayOfWeek.Monday && d >= 25)
                return new("Memorial Day", "Remembering those who gave their lives in service.");
            if (m == 9 && dow == DayOfWeek.Monday && d <= 7)
                return new("Labor Day", "Happy Labor Day! Enjoy the long weekend.");
            if (m == 1 && dow == DayOfWeek.Monday && d >= 15 && d <= 21)
                return new("MLK Day", "Happy Martin Luther King Jr. Day!");
            if (m == 2 && dow == DayOfWeek.Monday && d >= 15 && d <= 21)
                return new("Presidents' Day", "Happy Presidents' Day! Enjoy the long weekend!");
        }

        if (country == "GB")
        {
            if (m == 8 && dow == DayOfWeek.Monday && d >= 25)
                return new("August Bank Holiday", "Happy Bank Holiday! Enjoy the long weekend!");
            if (m == 5 && dow == DayOfWeek.Monday && d >= 1 && d <= 7)
                return new("Early May Bank Holiday", "Happy May Bank Holiday! Enjoy the long weekend!");
            if (m == 5 && dow == DayOfWeek.Monday && d >= 25)
                return new("Spring Bank Holiday", "Happy Spring Bank Holiday! Enjoy the long weekend!");
            if (m == 11 && d == 5)
                return new("Bonfire Night", "Remember, remember the 5th of November!");
        }

        if (country == "AU")
        {
            if (m == 1 && d == 26) return new("Australia Day", "Happy Australia Day!");
            if (m == 4 && d == 25) return new("ANZAC Day", "Lest we forget.");
            if (m == 6 && dow == DayOfWeek.Monday && d >= 8 && d <= 14)
                return new("King's Birthday (AU)", "Happy King's Birthday long weekend!");
        }

        if (country == "NZ")
        {
            if (m == 2 && d == 6) return new("Waitangi Day", "Happy Waitangi Day!");
            if (m == 4 && d == 25) return new("ANZAC Day", "Lest we forget.");
        }

        if (country == "DE")
        {
            if (m == 10 && d == 3) return new("German Unity Day", "Happy German Unity Day!");
            if (m == 5 && d == 1) return new("Labour Day", "Happy Labour Day!");
        }

        if (country == "FR")
        {
            if (m == 7 && d == 14) return new("Bastille Day", "Bonne fête nationale!");
            if (m == 5 && d == 1) return new("Fête du Travail", "Bonne Fête du Travail!");
        }

        if (country == "JP")
        {
            if (m == 1 && d == 1) return new("Shōgatsu", "あけましておめでとうございます！Happy New Year!");
            if (m == 11 && d == 3) return new("Culture Day", "Happy Culture Day!");
        }

        return null;
    }
    private static DateTime ComputeEaster(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100;
        int d2 = b / 4, e = b % 4, f = (b + 8) / 25;
        int g = (b - f + 1) / 3, h = (19 * a + b - d2 - g + 15) % 30;
        int i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m2 = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m2 + 114) / 31;
        int day = ((h + l - 7 * m2 + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }


    private static double MoonPhaseJdn(double k)
    {
        double T = k / 1236.85;
        double jde = 2451550.09766
                   + 29.530588861 * k
                   + 0.00015437 * T * T
                   - 0.000000150 * T * T * T
                   + 0.00000000073 * T * T * T * T;
        double M = Rad(2.5534 + 29.10535670 * k - 0.0000014 * T * T);
        double Mp = Rad(201.5643 + 385.81693528 * k + 0.0107582 * T * T);
        double F = Rad(160.7108 + 390.67050284 * k - 0.0016118 * T * T);
        double Om = Rad(124.7746 - 1.56375588 * k + 0.0020672 * T * T);
        double E = 1 - 0.002516 * T - 0.0000074 * T * T;
        return jde
            + (-0.40720 * Math.Sin(Mp))
            + (0.17241 * E * Math.Sin(M))
            + (0.01608 * Math.Sin(2 * Mp))
            + (0.01039 * Math.Sin(2 * F))
            + (0.00739 * E * Math.Sin(Mp - M))
            + (-0.00514 * E * Math.Sin(Mp + M))
            + (0.00208 * E * E * Math.Sin(2 * M))
            + (-0.00111 * Math.Sin(Mp - 2 * F))
            + (-0.00057 * Math.Sin(Mp + 2 * F))
            + (0.00056 * E * Math.Sin(2 * Mp + M))
            + (-0.00042 * Math.Sin(3 * Mp))
            + (0.00042 * E * Math.Sin(M + 2 * F))
            + (0.00038 * E * Math.Sin(M - 2 * F))
            + (-0.00024 * E * Math.Sin(2 * Mp - M))
            + (-0.00017 * Math.Sin(Om))
            + (-0.00007 * Math.Sin(Mp + 2 * M))
            + (0.00004 * Math.Sin(2 * Mp - 2 * F))
            + (0.00004 * Math.Sin(3 * M))
            + (0.00003 * Math.Sin(Mp + M - 2 * F))
            + (0.00003 * Math.Sin(2 * Mp + 2 * F))
            + (-0.00003 * Math.Sin(Mp + M + 2 * F))
            + (0.00003 * Math.Sin(Mp - M + 2 * F))
            + (-0.00002 * Math.Sin(Mp - M - 2 * F))
            + (-0.00002 * Math.Sin(3 * Mp + M))
            + (0.00002 * Math.Sin(4 * Mp));
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    private static DateTime JdnToDateTime(double jdn)
    {
        int j = (int)(jdn + 0.5);
        int a = j + 32044;
        int b = (4 * a + 3) / 146097;
        int c = a - 146097 * b / 4;
        int d = (4 * c + 3) / 1461;
        int e = c - 1461 * d / 4;
        int mo = (5 * e + 2) / 153;
        int day = e - (153 * mo + 2) / 5 + 1;
        int month = mo + 3 - 12 * (mo / 10);
        int year = 100 * b + d - 4800 + mo / 10;
        return new DateTime(year, month, day);
    }

    private static DateTime? MoonInMonth(int year, int month, bool fullMoon = false)
    {
        double kApprox = (year - 2000) * 12.3685 + month - 1;
        for (int offset = -2; offset <= 3; offset++)
        {
            double k = Math.Floor(kApprox) + offset + (fullMoon ? 0.5 : 0.0);
            double jdn = MoonPhaseJdn(k);
            var dt = JdnToDateTime(jdn);
            if (dt.Year == year && dt.Month == month)
                return dt;
        }
        return null;
    }

    private static DateTime? LunarNewYear(int year)
    {
        double kApprox = (year - 2000) * 12.3685;
        for (int offset = -2; offset <= 3; offset++)
        {
            double k = Math.Floor(kApprox) + offset;
            double jdn = MoonPhaseJdn(k) + 8.0 / 24.0; // shift to UTC+8
            var dt = JdnToDateTime(jdn);
            if (dt.Year == year && ((dt.Month == 1 && dt.Day >= 20) || (dt.Month == 2 && dt.Day <= 20)))
                return dt;
        }
        return null;
    }

    private static DateTime? HoliDate(int year)
    {
        var march = MoonInMonth(year, 3, fullMoon: true);
        if (march != null) return march;
        var feb = MoonInMonth(year, 2, fullMoon: true);
        return feb?.Day >= 20 ? feb : null;
    }

    private static DateTime? VesakDate(int year) =>
        MoonInMonth(year, 5, fullMoon: true);

    private static DateTime IslamicToGregorian(int iy, int im, int id)
    {
        int jdn = id
                + (int)Math.Ceiling(29.5 * (im - 1))
                + (iy - 1) * 354
                + (3 + 11 * iy) / 30
                + 1948438;
        return JdnToDateTime(jdn);
    }

    private static int ApproxHijriYear(int gregorianYear) =>
        (int)((gregorianYear - 622) * 1.030685);

    private static DateTime? EidAlFitr(int year)
    {
        int hy = ApproxHijriYear(year);
        for (int h = hy - 1; h <= hy + 1; h++)
        {
            var dt = IslamicToGregorian(h, 10, 1);
            if (dt.Year == year) return dt;
        }
        return null;
    }

    private static DateTime? EidAlAdha(int year)
    {
        int hy = ApproxHijriYear(year);
        for (int h = hy - 1; h <= hy + 1; h++)
        {
            var dt = IslamicToGregorian(h, 12, 10);
            if (dt.Year == year) return dt;
        }
        return null;
    }


    private static bool IsHebrewLeapYear(int hy) => (7 * hy + 1) % 19 < 7;

    private static int HebrewElapsedDays(int hy)
    {
        int monthsElapsed = 235 * ((hy - 1) / 19)
                          + 12 * ((hy - 1) % 19)
                          + (7 * ((hy - 1) % 19) + 1) / 19;
        int parts = 204 + 793 * (monthsElapsed % 1080);
        int hours = 5 + 12 * monthsElapsed + 793 * (monthsElapsed / 1080) + parts / 1080;
        int day = 1 + 29 * monthsElapsed + hours / 24;
        int pMod = 1080 * (hours % 24) + parts % 1080;

        int alt = day;
        if (pMod >= 19440
            || (day % 7 == 2 && pMod >= 9924 && !IsHebrewLeapYear(hy))
            || (day % 7 == 1 && pMod >= 16789 && IsHebrewLeapYear(hy - 1)))
            alt++;

        if (alt % 7 == 0 || alt % 7 == 3 || alt % 7 == 5) alt++;
        return alt;
    }

    private static int HebrewYearDays(int hy) =>
        HebrewElapsedDays(hy + 1) - HebrewElapsedDays(hy);

    private static int HebrewMonthLength(int hy, int hm)
    {
        int yd = HebrewYearDays(hy);
        if (hm == 2) return yd % 10 == 5 ? 30 : 29;
        if (hm == 3) return yd % 10 == 3 ? 29 : 30;
        // Adar (6) is 30 days in leap years, 29 in regular
        if (hm == 6) return IsHebrewLeapYear(hy) ? 30 : 29;
        return hm is 1 or 5 or 7 or 10 or 12 ? 30 : 29;
    }

    private static DateTime HebrewToGregorian(int hy, int hm, int hd)
    {
        const int HebrewEpoch = 347997; // JDN of 1 Tishrei AM 1
        int elapsed = HebrewElapsedDays(hy);
        int doy = hd;
        for (int mo = 1; mo < hm; mo++)
            doy += HebrewMonthLength(hy, mo);
        return JdnToDateTime(HebrewEpoch + elapsed + doy - 1);
    }

    private static int ApproxHebrewYear(int gregorianYear) => gregorianYear + 3760;

    private static DateTime? RoshHashanah(int year)
    {
        int hy0 = ApproxHebrewYear(year);
        for (int hy = hy0 - 1; hy <= hy0 + 1; hy++)
        {
            var dt = HebrewToGregorian(hy, 1, 1);
            if (dt.Year == year) return dt;
        }
        return null;
    }

    private static DateTime? YomKippur(int year)
    {
        int hy0 = ApproxHebrewYear(year);
        for (int hy = hy0 - 1; hy <= hy0 + 1; hy++)
        {
            var dt = HebrewToGregorian(hy, 1, 10);
            if (dt.Year == year) return dt;
        }
        return null;
    }

    private static DateTime? HanukkahDate(int year)
    {
        // 25 Kislev of Hebrew year ~(Gregorian + 3761) falls in Nov/Dec.
        int hy0 = ApproxHebrewYear(year) + 1;
        for (int hy = hy0 - 1; hy <= hy0 + 1; hy++)
        {
            var dt = HebrewToGregorian(hy, 3, 25);
            if (dt.Year == year) return dt;
        }
        return null;
    }

    private static DateTime? DiwaliDate(int year)
    {
        // Kartika new moon is always in the second half of October or early
        var oct = MoonInMonth(year, 10, fullMoon: false);
        if (oct != null && oct.Value.Day >= 14) return oct;
        var nov = MoonInMonth(year, 11, fullMoon: false);
        if (nov != null && nov.Value.Day <= 15) return nov;
        return oct; // Fallback
    }

    private static DateTime? NavratriDate(int year)
    {
        // Ashwin new moon falls in Sep (day >= 15) or early Oct (day <= 10).
        var sep = MoonInMonth(year, 9, fullMoon: false);
        if (sep != null && sep.Value.Day >= 15) return sep.Value.AddDays(1);
        var oct = MoonInMonth(year, 10, fullMoon: false);
        if (oct != null && oct.Value.Day <= 10) return oct.Value.AddDays(1);
        return null;
    }

    private static bool IsLongWeekendEve(DateTime date, string country)
    {
        if (date.DayOfWeek != DayOfWeek.Friday) return false;
        return GetHolidayEntry(date.AddDays(3), country) is not null;
    }
    private static bool IsPostLongWeekend(DateTime date, string country)
    {
        if (date.DayOfWeek != DayOfWeek.Tuesday) return false;
        return GetHolidayEntry(date.AddDays(-1), country) is not null;
    }

    public static string[] BuildMessages(
        string userName,
        string userCountry,
        int userHemisphereIndex,
        string userTimezoneOffset,
        bool isKodoBirthday,
        int kodoBirthdayAge)
    {
        // Resolve effective local time, honouring the user's timezone (handles "+05:30", "-03:30", "5.5", etc.)
        DateTime now;
        if (!string.IsNullOrWhiteSpace(userTimezoneOffset))
        {
            var raw = userTimezoneOffset.Trim();
            var sign = 1;
            if (raw.StartsWith("-", StringComparison.Ordinal)) { sign = -1; raw = raw[1..]; }
            else if (raw.StartsWith("+", StringComparison.Ordinal)) raw = raw[1..];

            if (raw.Contains(':'))
            {
                var parts = raw.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && double.TryParse(parts[0], out var h) && double.TryParse(parts[1], out var m))
                {
                    var offset = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m);
                    if (parts.Length > 2 && double.TryParse(parts[2], out var s)) offset += TimeSpan.FromSeconds(s);
                    if (sign == -1) offset = -offset;
                    now = DateTime.UtcNow + offset;
                }
                else
                {
                    now = DateTime.Now;
                }
            }
            else if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offsetHours))
            {
                var offset = TimeSpan.FromHours(offsetHours * sign);
                now = DateTime.UtcNow + offset;
            }
            else
            {
                now = DateTime.Now;
            }
        }
        else
        {
            now = DateTime.Now;
        }

        var tod = TimeOfDay(now.Hour);
        var country = userCountry;
        var dow = now.DayOfWeek;
        var dayName = now.ToString("dddd");   // e.g. "Monday"

        var messages = new List<string>();

        void Add(string text, int times = 1)
        {
            for (var i = 0; i < times; i++) messages.Add(text);
        }

        // Prepend the user's name to a subset of greetings so it's not
        var name = userName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            messages.Add($"Good {tod}, {name}!");
            messages.Add($"Hey {name}! Ready to build?");
            messages.Add($"Welcome back, {name}!");
            messages.Add($"Let's go, {name}!");

            messages.Add($"Great to see you again, {name}!");
            messages.Add($"Ready for another session, {name}?");
            messages.Add($"Time to be productive, {name}!");
            messages.Add($"Time to build something great, {name}!");
            messages.Add($"Locked in and ready, {name}?");
            messages.Add($"Good to have you back, {name}.");
            messages.Add($"Let's ship something great today, {name}!");
            messages.Add($"Your workspace is ready, {name}.");
        }

        var holiday = GetHolidayEntry(now, country);
        if (holiday?.Greeting is not null)
            Add(holiday.Greeting, 8);

        // Kodo birthday (April 18): weighted x5 so it dominates the pool
        if (isKodoBirthday)
        {
            var age = kodoBirthdayAge;
            var bdayMsg = age == 1 ? "Kodo turns 1 today! 🎂" : $"Kodo turns {age} today! 🎂";
            var ordinal = age switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{age}th" };
            Add(bdayMsg, 5);
            messages.Add("Happy birthday, Kodo! Thanks for coding with us 🎉");
            messages.Add($"It's Kodo's {ordinal} birthday! Let's celebrate with some great code 🎂");
            messages.Add("One year of fast, focused editing. Here's to many more! 🎉");
        }

        if (now.Minute == 11 && (now.Hour == 11 || now.Hour == 23))
            Add("11:11! Make a wish!", 8);

        // Friday the 13th: easter egg weighted x8, same pattern as the
        if (dow == DayOfWeek.Friday && now.Day == 13)
        {
            Add("Friday the 13th... may your builds stay bug-free! 🖤", 8);
            messages.Add("Unlucky for some, lucky for your commit history?");
        }

        // Leap Day: Feb 29 only exists every 4 years, so it gets its own
        if (now.Month == 2 && now.Day == 29)
            Add("Leap Day! Enjoy the extra day - it only comes around every 4 years.", 8);

        if (now.DayOfYear == 256)
            Add("Happy Programmer's Day! 🖥️ Day 256 of the year - fitting, isn't it?", 8);
        if (now.Month == 3 && now.Day == 14)
            Add("Happy Pi Day! 🥧 3.14159265...", 8);

        if (now.Month == 12 && now.Day == 31 && now.Hour == 23)
        {
            Add("Almost midnight - one more commit before the new year?", 8);
            messages.Add("The countdown's on. Ship it before the ball drops!");
        }

        if (IsLongWeekendEve(now, country))
        {
            Add("Long weekend starts tomorrow - one more push!", 2);
            Add("Almost there! Long weekend is just around the corner.", 2);
            Add($"Happy {dayName}! The long weekend is almost here.", 2);
        }

        if (IsPostLongWeekend(now, country))
        {
            Add("Back from the long weekend - fresh start!", 2);
            Add("Post-long-weekend. Let's ease back in.", 2);
            Add("Hope the long weekend recharged you. Ready to build?", 2);
        }

        messages.Add(dow switch
        {
            DayOfWeek.Monday => "Monday? Let's make it count.",
            DayOfWeek.Tuesday => "Tuesday momentum - keep it going!",
            DayOfWeek.Wednesday => "Midweek check-in - still crushing it?",
            DayOfWeek.Thursday => "Almost Friday - don't stop now!",
            DayOfWeek.Friday => "Happy Friday! Let's finish strong.",
            DayOfWeek.Saturday => "Coding on a Saturday - respect.",
            DayOfWeek.Sunday => "Sunday coding session - the quiet grind.",
            _ => $"Happy {dayName}!"
        });

        if (dow == DayOfWeek.Friday)
        {
            messages.Add("It's Friday - let's ship something before the weekend!");
            messages.Add("Friday energy. Let's make the most of it.");
        }
        if (dow is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            messages.Add("Weekend warrior mode: activated.");
            messages.Add("No meetings on weekends. Just code.");
        }
        if (dow == DayOfWeek.Monday)
        {
            messages.Add("New week, new bugs to squash.");
            messages.Add("Monday's for the brave. Welcome back.");
        }

        if (tod != "night")
        {
            messages.Add($"Good {tod}!");
            messages.Add($"Good {tod}, ready to build?");
            messages.Add($"Good {tod}, let's get to it!");
            messages.Add($"It's a great {tod} to code!");
        }

        if (tod == "morning")
        {
            messages.Add("Hey there, early bird!");
            messages.Add("Rise and shine, let's code!");
            messages.Add("Coffee in hand, let's ship something!");
            messages.Add("A fresh day, a fresh start.");
            messages.Add("Morning focus is unmatched.");
        }
        if (tod == "afternoon")
        {
            messages.Add("Afternoon grind, let's go!");
            messages.Add("Hope the day's treating you well!");
            messages.Add("Halfway through the day, keep it up!");
            messages.Add("Afternoon slump? Not here.");
        }
        if (tod == "evening")
        {
            messages.Add("Fancy coding over a cup of tea?");
            messages.Add("Winding down or just getting started?");
            messages.Add("Evening sessions hit different.");
            messages.Add("The day's winding down, but the code isn't.");
        }
        if (tod == "night")
        {
            messages.Add("Hey there, night owl!");
            messages.Add("Burning the midnight oil?");
            messages.Add("The best code gets written at night.");
            messages.Add("Still at it? Respect.");
            messages.Add("Late night, great code.");
            messages.Add("The quieter the world, the clearer the code.");
            messages.Add("Dark outside, bright ideas inside.");
            messages.Add("Another late one? Worth it.");
        }

        var isSouthern = userHemisphereIndex == 2
            || (userHemisphereIndex == 0 && country is "AU" or "NZ" or "ZA" or "AR" or "BR" or "CL");
        var month = now.Month;
        var season = isSouthern
            ? month switch
            {
                12 or 1 or 2 => "summer",
                3 or 4 or 5 => "autumn",
                6 or 7 or 8 => "winter",
                _ => "spring"
            }
            : month switch
            {
                12 or 1 or 2 => "winter",
                3 or 4 or 5 => "spring",
                6 or 7 or 8 => "summer",
                _ => "autumn"
            };

        messages.Add(season switch
        {
            "winter" => "Warm up your fingers - it's time to code.",
            "spring" => "Spring energy - let's build something fresh.",
            "summer" => "Hot outside, hotter code.",
            "autumn" => "Cozy season, perfect for shipping features.",
            _ => "Great day to write some code."
        });

        if (season == "winter")
        {
            messages.Add("Snow outside, cozy code inside.");
            messages.Add("Winter's here - perfect excuse to stay in and ship.");
        }
        if (season == "spring")
        {
            messages.Add("Spring cleaning? Let's refactor something too.");
            messages.Add("New blossoms, new builds.");
        }
        if (season == "summer")
        {
            messages.Add("Summer vibes, sharp code.");
            messages.Add("Long days, long streaks - let's keep building.");
        }
        if (season == "autumn")
        {
            messages.Add("Leaves are falling, but your code's holding up.");
            messages.Add("Sweater weather, solid code.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            messages.Add("Welcome back!");
            messages.Add("Great to see you!");
            messages.Add("Ready to code?");
            messages.Add("What are we building today?");
            messages.Add("Back at it again!");
            messages.Add("Let's get to work!");
            messages.Add($"Happy {dayName}!");
        }

        return messages.ToArray();
    }

    private static string TimeOfDay(int hour)
    {
        if (hour < 6) return "night";
        if (hour < 12) return "morning";
        if (hour < 17) return "afternoon";
        if (hour < 22) return "evening";
        return "night";
    }
}