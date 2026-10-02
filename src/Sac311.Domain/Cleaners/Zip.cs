using System.Text.RegularExpressions;

namespace Sac311.Domain.Cleaners;

public static partial class Zip
{
    /// <summary>Keeps the 5-digit ZIP (ZIP+4 is cut to 5). Blank is null; anything else is null with <see cref="DqFlags.InvalidZip"/>.</summary>
    public static Cleaned<string?> Normalize(string? raw)
    {
        var text = Text.NullIfBlank(raw);
        if (text is null)
        {
            return new(null, DqFlags.None);
        }

        var m = ZipPattern().Match(text);
        return m.Success ? new(m.Groups[1].Value, DqFlags.None) : new(null, DqFlags.InvalidZip);
    }

    [GeneratedRegex(@"^(\d{5})(?:[- ]?\d{4})?$")]
    private static partial Regex ZipPattern();
}
