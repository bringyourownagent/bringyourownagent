using System.Globalization;
using System.Text.RegularExpressions;

namespace Byoa.Wallet;

/// <summary>
/// Money in the wallet: <see cref="decimal"/> amounts in US dollars, written as the spec's money strings
/// ("0.05 USD", at most 6 digits after the point). Never binary floating point.
/// </summary>
public static partial class Money
{
    public const string Currency = "USD";

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,11})(\.[0-9]{1,6})? ([A-Z]{3})$")]
    private static partial Regex Pattern();

    /// <summary>Parses "0.05 USD". Only dollars are used in 0.1.</summary>
    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0;
        if (text is null) return false;
        var m = Pattern().Match(text);
        if (!m.Success || m.Groups[3].Value != Currency) return false;
        return decimal.TryParse(m.Groups[1].Value + m.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    public static decimal Parse(string? text, decimal fallback) => TryParse(text, out var v) ? v : fallback;

    /// <summary>A spec money string, rounded up to whole micro-dollars: 0.000153 → "0.000153 USD", 0 → "0 USD".</summary>
    public static string ToSpec(decimal amount)
    {
        var micros = Math.Ceiling(Math.Max(0, amount) * 1_000_000m);
        var value = micros / 1_000_000m;
        return value.ToString("0.######", CultureInfo.InvariantCulture) + " " + Currency;
    }

    /// <summary>A receipt for the page: at least three decimals, "0.000 USD" or "0.000153 USD".</summary>
    public static string ToReceipt(decimal amount)
    {
        var micros = Math.Ceiling(Math.Max(0, amount) * 1_000_000m);
        return (micros / 1_000_000m).ToString("0.000###", CultureInfo.InvariantCulture) + " " + Currency;
    }

    /// <summary>For people: "$0.05", "$0.20", "$0.00015".</summary>
    public static string Show(decimal amount)
    {
        amount = Math.Max(0, amount);
        if (amount == 0) return "$0";
        if (amount >= 0.01m) return "$" + amount.ToString("0.00##", CultureInfo.InvariantCulture);
        return "$" + amount.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>The steps offered by the budget steppers.</summary>
    public static readonly decimal[] Steps = [0.01m, 0.02m, 0.05m, 0.10m, 0.20m, 0.50m, 1.00m, 2.00m, 5.00m];

    public static decimal StepUp(decimal value) => Steps.FirstOrDefault(s => s > value, Steps[^1]);

    public static decimal StepDown(decimal value) => Steps.LastOrDefault(s => s < value, Steps[0]);
}
