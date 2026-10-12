namespace Byoa.Wallet.Sources;

/// <summary>Cleaning shared by settings, providers, consent cards and the agent display.</summary>
public static class SourceLabels
{
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Select(c =>
        {
            var category = char.GetUnicodeCategory(c);
            return category is System.Globalization.UnicodeCategory.Control or
                System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or
                System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned
                ? char.IsWhiteSpace(c) ? ' ' : '\0'
                : c;
        }).Where(c => c != '\0').ToArray());
        cleaned = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
