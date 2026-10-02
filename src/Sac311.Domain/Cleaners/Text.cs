using System.Text.RegularExpressions;

namespace Sac311.Domain.Cleaners;

public static partial class Text
{
    /// <summary>Trims, collapses runs of whitespace to one space, and returns null for null, '' or whitespace.</summary>
    public static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Whitespace().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
