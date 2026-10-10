using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BringYourOwnAgent.Internal;

/// <summary>
/// A money string such as "0.06 USD", kept as whole micro-units (millionths), never as
/// floating point. The format allows at most 12 digits before the point and 6 after it,
/// so every amount and every sum of two amounts fits a 64-bit integer exactly.
/// </summary>
internal sealed class Money
{
    private const long MicrosPerUnit = 1000000;

    private static readonly Regex Pattern = new(
        @"^(0|[1-9][0-9]{0,11})(?:\.([0-9]{1,6}))? ([A-Z]{3})\z",
        RegexOptions.CultureInvariant);

    private Money(long micros, string currency)
    {
        Micros = micros;
        Currency = currency;
    }

    public long Micros { get; }

    public string Currency { get; }

    /// <summary>Parse "&lt;amount&gt; &lt;CUR&gt;"; null when the value is not valid money.</summary>
    public static Money? Parse(JsonElement value)
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

        var whole = long.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var fraction = match.Groups[2].Success ? match.Groups[2].Value : "";
        var micros = long.Parse(fraction.PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        return new Money((whole * MicrosPerUnit) + micros, match.Groups[3].Value);
    }

    public static Money Zero(string currency) => new(0, currency);

    public Money Add(Money other) => new(checked(Micros + other.Micros), Currency);

    /// <summary>Shortest form: "0.06 USD", "0 USD", "12 EUR".</summary>
    public override string ToString()
    {
        var whole = (Micros / MicrosPerUnit).ToString(CultureInfo.InvariantCulture);
        var fraction = (Micros % MicrosPerUnit).ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return (fraction.Length == 0 ? whole : whole + "." + fraction) + " " + Currency;
    }
}
