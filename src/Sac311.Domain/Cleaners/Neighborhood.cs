using System.Text.RegularExpressions;

namespace Sac311.Domain.Cleaners;

/// <param name="Name">The cleaned source spelling; usp_apply_batch swaps in the boundary file's NAME via ref.neighborhood_alias.</param>
/// <param name="Key">MapKey of the name, the ref.neighborhood_alias lookup key.</param>
/// <param name="Slug">URL-safe id used by the API and the map ("College/Glen" is college-glen).</param>
public sealed record NeighborhoodResult(string? Name, string? Key, string? Slug, DqFlags Flags);

public static partial class Neighborhood
{
    /// <summary>
    /// Collapses whitespace and normalizes slashes ("College / Glen", "College\Glen" and "College/Glen" share a key and
    /// slug). A city row with no neighborhood gets <see cref="DqFlags.NeighborhoodMissingInCity"/>.
    /// </summary>
    public static NeighborhoodResult Normalize(string? raw, bool? isCity)
    {
        var text = Text.NullIfBlank(raw?.Replace('\\', '/'));
        if (text is null)
        {
            return new(null, null, null, isCity == true ? DqFlags.NeighborhoodMissingInCity : DqFlags.None);
        }

        return new(text, MapKey.For(text), Slug(text), DqFlags.None);
    }

    /// <summary>Lowercase; apostrophes dropped; every other run of non-alphanumerics becomes one dash.</summary>
    public static string? Slug(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var lower = name.ToLowerInvariant().Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal);
        var slug = NonAlphanumeric().Replace(lower, "-").Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
