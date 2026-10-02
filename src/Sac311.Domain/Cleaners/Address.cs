namespace Sac311.Domain.Cleaners;

public static class Address
{
    private static readonly HashSet<string> JunkTokens = new(StringComparer.Ordinal)
    {
        "na", "tbd", "ok", "zoom", "none", "unknown",
    };

    /// <summary>
    /// Blank becomes null with no flag (a missing address isn't an error). Placeholder text (N/A, TBD, OK, ZOOM,
    /// NONE, UNKNOWN), bare numbers and anything under 3 characters become null with <see cref="DqFlags.AddressJunk"/>.
    /// </summary>
    public static Cleaned<string?> Normalize(string? raw)
    {
        var text = Text.NullIfBlank(raw);
        if (text is null)
        {
            return new(null, DqFlags.None);
        }

        var key = MapKey.For(text);
        var junk = text.Length < 3
            || key is null
            || JunkTokens.Contains(key)
            || key.All(char.IsAsciiDigit);
        return junk ? new(null, DqFlags.AddressJunk) : new(text, DqFlags.None);
    }
}
