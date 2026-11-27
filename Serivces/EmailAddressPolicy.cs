using System.Globalization;
using System.Text.RegularExpressions;

namespace Zullo.Api.Services;

public static class EmailAddressPolicy
{
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = (input ?? "").Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 200 || normalized.Any(char.IsWhiteSpace)) return false;
        var parts = normalized.Split('@');
        if (parts.Length != 2 || parts[0].Length is 0 or > 64 || parts[0].StartsWith('.')
            || parts[0].EndsWith('.') || parts[0].Contains("..")) return false;
        if (!Regex.IsMatch(parts[0], @"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+$", RegexOptions.CultureInvariant)) return false;
        string domain;
        try { domain = new IdnMapping().GetAscii(parts[1]); }
        catch (ArgumentException) { return false; }
        var labels = domain.Split('.');
        if (labels.Length < 2 || labels[^1].Length < 2 || labels.Any(label =>
            label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')
            || !Regex.IsMatch(label, @"^[a-zA-Z0-9-]+$", RegexOptions.CultureInvariant))) return false;
        // Keep the established normalized stored address for legacy compatibility.
        return true;
    }

    public static string? SuggestAddress(string email)
    {
        var index = email.LastIndexOf('@');
        if (index < 1) return null;
        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail.ocm"] = "gmail.com", ["gmail.con"] = "gmail.com", ["gmial.com"] = "gmail.com", ["gmai.com"] = "gmail.com",
            ["outlook.ocm"] = "outlook.com", ["outlook.con"] = "outlook.com",
            ["hotmail.ocm"] = "hotmail.com", ["hotmail.con"] = "hotmail.com",
            ["yahoo.ocm"] = "yahoo.com", ["yahoo.con"] = "yahoo.com"
        };
        return domains.TryGetValue(email[(index + 1)..], out var domain) ? email[..(index + 1)] + domain : null;
    }
}
