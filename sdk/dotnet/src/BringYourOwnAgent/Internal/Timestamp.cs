using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BringYourOwnAgent.Internal;

/// <summary>
/// RFC 3339 timestamps, parsed by hand (not DateTimeOffset.Parse, which accepts many other
/// forms) so that every implementation accepts exactly the same strings and gets the same instant.
/// </summary>
internal static class Timestamp
{
    private static readonly Regex Pattern = new(
        @"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]+))?(?:([Zz])|([+-])([0-9]{2}):([0-9]{2}))\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Milliseconds since the Unix epoch, or null if the value is not an RFC 3339 date-time
    /// with an offset. Digits beyond milliseconds are cut off, not rounded. Leap seconds are
    /// not accepted.
    /// </summary>
    public static long? Parse(JsonElement value)
    {
        var text = JsonValues.AsString(value);
        if (text is null)
        {
            return null;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        int Number(int group) => int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

        int year = Number(1), month = Number(2), day = Number(3);
        int hour = Number(4), minute = Number(5), second = Number(6);
        if (month < 1 || month > 12 || day < 1 || day > DaysInMonth(year, month))
        {
            return null;
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var fraction = match.Groups[7].Success ? match.Groups[7].Value : "";
        var millis = int.Parse(fraction.PadRight(3, '0').Substring(0, 3), NumberStyles.None, CultureInfo.InvariantCulture);
        var offset = 0;
        if (!match.Groups[8].Success)
        {
            int offsetHour = Number(10), offsetMinute = Number(11);
            if (offsetHour > 23 || offsetMinute > 59)
            {
                return null;
            }

            offset = ((offsetHour * 60) + offsetMinute) * (match.Groups[9].Value == "+" ? 1 : -1);
        }

        var days = DaysFromCivil(year, month, day);
        var totalMinutes = (((days * 24) + hour) * 60) + minute - offset;
        return (((totalMinutes * 60) + second) * 1000) + millis;
    }

    private static bool IsLeap(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    private static int DaysInMonth(int year, int month) => month switch
    {
        2 => IsLeap(year) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>Days since 1970-01-01 in the proleptic Gregorian calendar.</summary>
    private static long DaysFromCivil(int year, int month, int day)
    {
        long y = year - (month <= 2 ? 1 : 0);
        var era = (y >= 0 ? y : y - 399) / 400; // floor division
        var yearOfEra = y - (era * 400);
        var dayOfYear = (((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5) + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146097) + dayOfEra - 719468;
    }
}
