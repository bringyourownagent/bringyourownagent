using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace BringYourOwnAgent.Internal;

/// <summary>
/// Shows values inside messages exactly as the Node.js library does (JSON.stringify and
/// String(number)), so that every implementation writes the same messages.
/// </summary>
internal static class JsText
{
    private const string Hex = "0123456789abcdef";

    /// <summary>A JSON value as compact JSON, like JSON.stringify.</summary>
    public static string Quote(JsonElement value)
    {
        var text = new StringBuilder();
        try
        {
            AppendValue(text, value);
        }
        catch (InsufficientExecutionStackException)
        {
            return "[nested too deeply]";
        }

        return text.ToString();
    }

    /// <summary>A string as a JSON string literal, like JSON.stringify.</summary>
    public static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2);
        AppendString(text, value);
        return text.ToString();
    }

    /// <summary>Escape like JSON.stringify: quote, backslash, control characters and lone surrogates.</summary>
    public static void AppendString(StringBuilder text, string value)
    {
        text.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': text.Append("\\\""); continue;
                case '\\': text.Append("\\\\"); continue;
                case '\b': text.Append("\\b"); continue;
                case '\f': text.Append("\\f"); continue;
                case '\n': text.Append("\\n"); continue;
                case '\r': text.Append("\\r"); continue;
                case '\t': text.Append("\\t"); continue;
            }

            var lone = (char.IsHighSurrogate(c) && !(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])))
                || (char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(value[i - 1])));
            if (c < 0x20 || lone)
            {
                text.Append("\\u")
                    .Append(Hex[(c >> 12) & 0xF])
                    .Append(Hex[(c >> 8) & 0xF])
                    .Append(Hex[(c >> 4) & 0xF])
                    .Append(Hex[c & 0xF]);
            }
            else
            {
                text.Append(c);
            }
        }

        text.Append('"');
    }

    /// <summary>
    /// A finite number as JavaScript's String(number) writes it: "15", "9.5", "1e-7", "1e+21".
    /// Negative zero is "0".
    /// </summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : value < 0 ? "-Infinity" : "NaN";
        }

        if (value == 0)
        {
            return "0";
        }

        var sign = value < 0 ? "-" : "";
        ShortestDigits(Math.Abs(value), out var digits, out var n);
        var k = digits.Length;
        string text;
        if (k <= n && n <= 21)
        {
            text = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            text = digits.Substring(0, n) + "." + digits.Substring(n);
        }
        else if (-6 < n && n <= 0)
        {
            text = "0." + new string('0', -n) + digits;
        }
        else
        {
            var e = n - 1;
            var exponent = (e < 0 ? "-" : "+") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            text = k == 1 ? digits + "e" + exponent : digits.Substring(0, 1) + "." + digits.Substring(1) + "e" + exponent;
        }

        return sign + text;
    }

    /// <summary>
    /// The shortest decimal digits that read back as <paramref name="value"/> (positive, finite),
    /// with <paramref name="n"/> such that value = 0.digits * 10^n.
    /// </summary>
    private static void ShortestDigits(double value, out string digits, out int n)
    {
#if NETCOREAPP3_0_OR_GREATER
        // .NET Core 3.0 and later format the shortest round-trip form.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
#else
        // .NET Framework's "R" is not always the shortest; find it by precision.
        var text = value.ToString("E16", CultureInfo.InvariantCulture);
        for (var precision = 1; precision <= 17; precision++)
        {
            var candidate = value.ToString("E" + (precision - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (double.Parse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture) == value)
            {
                text = candidate;
                break;
            }
        }
#endif
        var exponentAt = text.IndexOfAny(new[] { 'E', 'e' });
        var mantissa = exponentAt < 0 ? text : text.Substring(0, exponentAt);
        var exponent = exponentAt < 0 ? 0 : int.Parse(text.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var integerPart = point < 0 ? mantissa : mantissa.Substring(0, point);
        var all = integerPart + (point < 0 ? "" : mantissa.Substring(point + 1));
        var pointAt = integerPart.Length;
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0')
        {
            leading++;
        }

        all = all.Substring(leading).TrimEnd('0');
        digits = all.Length == 0 ? "0" : all;
        n = pointAt - leading + exponent;
    }

    private static void AppendValue(StringBuilder text, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                var view = new JsonObjectView(value);
                text.Append('{');
                var first = true;
                foreach (var key in JavaScriptKeyOrder(view.Keys))
                {
                    if (!first)
                    {
                        text.Append(',');
                    }

                    first = false;
                    AppendString(text, key);
                    text.Append(':');
                    AppendValue(text, view.Raw(key));
                }

                text.Append('}');
                break;
            case JsonValueKind.Array:
                RuntimeHelpers.EnsureSufficientExecutionStack();
                text.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ > 0)
                    {
                        text.Append(',');
                    }

                    AppendValue(text, item);
                }

                text.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(text, JsonValues.AsString(value)!);
                break;
            case JsonValueKind.Number:
                var number = JsonValues.ParseNumber(value.GetRawText());
                text.Append(double.IsInfinity(number) || double.IsNaN(number) ? "null" : FormatNumber(number));
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            case JsonValueKind.Null:
                text.Append("null");
                break;
            default:
                text.Append("undefined");
                break;
        }
    }

    /// <summary>
    /// The order in which JavaScript lists the keys of a parsed object: array indexes
    /// ascending first, then the other keys in the order they first appear.
    /// </summary>
    private static IEnumerable<string> JavaScriptKeyOrder(IReadOnlyList<string> keys)
    {
        var indexes = new List<KeyValuePair<uint, string>>();
        var others = new List<string>();
        foreach (var key in keys)
        {
            if (IsArrayIndex(key, out var index))
            {
                indexes.Add(new KeyValuePair<uint, string>(index, key));
            }
            else
            {
                others.Add(key);
            }
        }

        indexes.Sort((a, b) => a.Key.CompareTo(b.Key));
        foreach (var pair in indexes)
        {
            yield return pair.Value;
        }

        foreach (var key in others)
        {
            yield return key;
        }
    }

    private static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10 || (key.Length > 1 && key[0] == '0'))
        {
            return false;
        }

        ulong value = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            value = (value * 10) + (ulong)(c - '0');
        }

        if (value >= uint.MaxValue)
        {
            return false;
        }

        index = (uint)value;
        return true;
    }
}
