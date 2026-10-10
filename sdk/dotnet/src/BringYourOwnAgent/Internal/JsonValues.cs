using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BringYourOwnAgent.Internal;

/// <summary>
/// Reading JSON values with the semantics shared by every BYOA implementation:
/// null means absent, numbers are IEEE doubles, the last of duplicate keys wins,
/// and strings keep lone surrogates (as JSON.parse and Python's json do).
/// </summary>
internal static class JsonValues
{
    private static readonly Regex SkillIdPattern = new(
        @"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z",
        RegexOptions.CultureInvariant);

    /// <summary>Missing and null are the same everywhere.</summary>
    public static bool IsAbsent(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null;

    public static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    public static bool IsArray(JsonElement value) => value.ValueKind == JsonValueKind.Array;

    public static bool IsBoolean(JsonElement value) =>
        value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;

    /// <summary>The string value, or null when the value is not a JSON string.</summary>
    public static string? AsString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses lone surrogates such as "\ud83c"; other implementations keep them.
            return Unescape(value.GetRawText());
        }
    }

    /// <summary>A finite JSON number. Booleans are not numbers.</summary>
    public static bool TryGetNumber(JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        number = ParseNumber(value.GetRawText());
        return !double.IsInfinity(number) && !double.IsNaN(number);
    }

    /// <summary>A number from 0 to 2^53 - 1.</summary>
    public static bool IsNonNegative(JsonElement value) =>
        TryGetNumber(value, out var number) && number >= 0 && number <= Rules.MaxNumber;

    /// <summary>A whole number from 0 to 2^53 - 1; 20.0 counts as whole.</summary>
    public static bool IsCount(JsonElement value) =>
        IsNonNegative(value) && TryGetNumber(value, out var number) && Math.Floor(number) == number;

    public static bool IsSkillId(string? value) =>
        value is not null && value.Length <= Rules.SkillIdMaxLength && SkillIdPattern.IsMatch(value);

    public static bool IsSkillId(JsonElement value) => IsSkillId(AsString(value));

    /// <summary>Only Unicode White_Space, spelled out so that every implementation agrees exactly.</summary>
    public static bool IsBlank(string text)
    {
        foreach (var c in text)
        {
            var blank = (c >= 0x0009 && c <= 0x000D) || c == 0x0020 || c == 0x0085 || c == 0x00A0 || c == 0x1680
                || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F
                || c == 0x3000;
            if (!blank)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Number of Unicode code points; a lone surrogate counts as one.</summary>
    public static int CodePointLength(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>Build a JSON Pointer (RFC 6901) from path segments.</summary>
    public static string Pointer(params string[] parts)
    {
        var path = new StringBuilder();
        foreach (var part in parts)
        {
            path.Append('/').Append(part.Replace("~", "~0").Replace("/", "~1"));
        }

        return path.ToString();
    }

    /// <summary>
    /// A JSON number as a double, rounded to nearest. Values too large for a double become
    /// infinite, as in JavaScript and Python.
    /// </summary>
    public static double ParseNumber(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        // .NET Framework reports overflow instead of returning infinity. The text is a valid
        // JSON number, so the only failures are a magnitude too large or too small for a double.
        var negative = text.StartsWith("-", StringComparison.Ordinal);
        var exponentAt = text.IndexOfAny(new[] { 'e', 'E' });
        var mantissa = exponentAt < 0 ? text : text.Substring(0, exponentAt);
        var exponent = exponentAt < 0 ? 0L : ParseExponent(text.Substring(exponentAt + 1));
        var point = mantissa.IndexOf('.');
        var integerDigits = (point < 0 ? mantissa : mantissa.Substring(0, point)).TrimStart('-');
        var digits = integerDigits + (point < 0 ? "" : mantissa.Substring(point + 1));
        var firstNonZero = digits.IndexOfAny("123456789".ToCharArray());
        if (firstNonZero < 0)
        {
            return negative ? -0.0 : 0.0;
        }

        var magnitude = exponent + integerDigits.Length - firstNonZero;
        if (magnitude > 0)
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        return negative ? -0.0 : 0.0;
    }

    private static long ParseExponent(string text)
    {
        var negative = text.StartsWith("-", StringComparison.Ordinal);
        var digits = text.TrimStart('+', '-').TrimStart('0');
        if (digits.Length > 15)
        {
            return negative ? long.MinValue / 2 : long.MaxValue / 2;
        }

        var value = digits.Length == 0 ? 0L : long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    /// <summary>Unescape a quoted JSON string token, keeping lone surrogates.</summary>
    public static string Unescape(string quoted)
    {
        var text = new StringBuilder(quoted.Length);
        for (var i = 1; i < quoted.Length - 1; i++)
        {
            var c = quoted[i];
            if (c != '\\')
            {
                text.Append(c);
                continue;
            }

            i++;
            switch (quoted[i])
            {
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'u':
                    text.Append((char)int.Parse(quoted.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: text.Append(quoted[i]); break; // \" \\ \/
            }
        }

        return text.ToString();
    }

    /// <summary>The unescaped name of a property, keeping lone surrogates.</summary>
    public static string PropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException)
        {
            // The raw text of a property starts with its quoted name.
            var raw = property.ToString();
            var end = 1;
            while (raw[end] != '"')
            {
                end += raw[end] == '\\' ? 2 : 1;
            }

            return Unescape(raw.Substring(0, end + 1));
        }
    }
}

/// <summary>A JSON object read once: the last of duplicate keys wins, keys keep their first position.</summary>
internal sealed class JsonObjectView
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public JsonObjectView(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            var name = JsonValues.PropertyName(property);
            if (!_values.ContainsKey(name))
            {
                _order.Add(name);
            }

            _values[name] = property.Value;
        }
    }

    /// <summary>All keys, in the order they first appear.</summary>
    public IReadOnlyList<string> Keys => _order;

    /// <summary>The value of a key; missing and null both give an undefined element.</summary>
    public JsonElement Get(string key) =>
        _values.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null ? value : default;

    /// <summary>The value of a key exactly as stored (null stays null).</summary>
    public JsonElement Raw(string key) => _values[key];

    /// <summary>Keys whose value is not null, in Unicode code point order.</summary>
    public List<string> PresentKeys()
    {
        var keys = new List<string>();
        foreach (var key in _order)
        {
            if (_values[key].ValueKind != JsonValueKind.Null)
            {
                keys.Add(key);
            }
        }

        keys.Sort(CodePointComparer.Instance);
        return keys;
    }
}

/// <summary>Compares strings by Unicode code points (ordinal comparison uses UTF-16 units).</summary>
internal sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var a = Next(x, ref i);
            var b = Next(y, ref j);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return (i < x.Length ? 1 : 0) - (j < y.Length ? 1 : 0);
    }

    private static int Next(string text, ref int index)
    {
        var c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            var codePoint = char.ConvertToUtf32(c, text[index + 1]);
            index += 2;
            return codePoint;
        }

        index++;
        return c;
    }
}
