using System.Text.RegularExpressions;

namespace RobloxUtility.Services;

/// <summary>Roblox universe/place id: positive decimal digits, fits in a signed 64-bit value (Roblox does not use negative or fractional ids).</summary>
public static class PlaceIdValidation
{
    private static readonly Regex DigitsOnly = new(@"^[0-9]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public const long MinGameId = 1L;
    public const long MaxGameId = long.MaxValue;

    /// <summary>Accepts only digits; optional spaces are stripped. No leading sign, no scientific notation.</summary>
    public static bool TryParse(string? raw, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = Regex.Replace(raw, @"\s", "");
        if (s.Length is 0 || !DigitsOnly.IsMatch(s))
        {
            return false;
        }

        if (!long.TryParse(s, out id) || id is < MinGameId or > MaxGameId)
        {
            return false;
        }

        return true;
    }
}
