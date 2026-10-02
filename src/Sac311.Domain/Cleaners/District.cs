using System.Globalization;
using System.Text.RegularExpressions;

namespace Sac311.Domain.Cleaners;

/// <param name="Number">Council district 1-8, or null outside the city or when unparseable.</param>
/// <param name="IsCity">False for "Non City", null when the value can't be read.</param>
public sealed record DistrictResult(byte? Number, bool? IsCity, DqFlags Flags);

public static partial class District
{
    /// <summary>"District 4" becomes (4, city); "Non City" becomes (null, not city); anything else is unknown with a flag.</summary>
    public static DistrictResult Parse(string? raw)
    {
        var key = MapKey.For(raw);
        if (key == "noncity")
        {
            return new(null, false, DqFlags.None);
        }

        var m = key is null ? Match.Empty : DistrictKey().Match(key);
        return m.Success
            ? new(byte.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture), true, DqFlags.None)
            : new(null, null, DqFlags.UnknownDistrict);
    }

    [GeneratedRegex("^(?:district)?0*([1-8])$")]
    private static partial Regex DistrictKey();
}
