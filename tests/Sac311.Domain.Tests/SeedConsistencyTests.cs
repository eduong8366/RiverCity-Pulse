using System.Text.Json;
using System.Text.RegularExpressions;
using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests;

/// <summary>The db/seed SQL must agree with the C# that produces the keys it's joined on.</summary>
public partial class SeedConsistencyTests
{
    private static string Seed(string name) => File.ReadAllText(Repo.Path("db", "seed", name + ".sql"));

    private static string Unquote(string sql) => sql.Replace("''", "'", StringComparison.Ordinal);

    [Fact]
    public void Dq_flag_seed_matches_the_enum()
    {
        var seeded = DqFlagRow().Matches(Seed("dq_flag"))
            .ToDictionary(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m => (m.Groups[2].Value, m.Groups[3].Value == "1"));

        var single = Enum.GetValues<DqFlags>().Where(f => f != DqFlags.None && int.IsPow2((int)f)).ToList();
        Assert.Equal(single.Select(f => (int)f).Order(), seeded.Keys.Order());
        Assert.All(single, f =>
        {
            Assert.Equal(f.ToString(), seeded[(int)f].Item1);
            Assert.Equal(DqFlags.MetricExclusions.HasFlag(f), seeded[(int)f].Item2);
        });
    }

    [Fact]
    public void Is_metric_eligible_uses_the_metric_exclusion_mask()
    {
        var migration = File.ReadAllText(Repo.Path("db", "migrations", "0012_clear_out_notes.sql"));
        Assert.Contains($"dq_flags & {(int)DqFlags.MetricExclusions} = 0", migration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("category_map")]
    [InlineData("source_map")]
    public void Map_seed_keys_are_the_map_key_of_their_source_value(string seed)
    {
        var rows = MapRow().Matches(Seed(seed)).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, m => Assert.Equal(m.Groups[1].Value, MapKey.For(Unquote(m.Groups[2].Value))));
    }

    [Fact]
    public void Non_service_type_keys_are_the_map_keys_of_their_source_values()
    {
        var rows = NonServiceRow().Matches(Seed("non_service_type")).ToList();
        var categories = MapRow().Matches(Seed("category_map")).Select(m => m.Groups[1].Value).ToHashSet();

        Assert.NotEmpty(rows);
        Assert.All(rows, m =>
        {
            Assert.Equal(m.Groups[1].Value, MapKey.For(Unquote(m.Groups[3].Value)));
            Assert.Equal(m.Groups[2].Value, MapKey.For(Unquote(m.Groups[4].Value)));
            Assert.Contains(m.Groups[1].Value, categories);
        });
        Assert.Equal(rows.Count, rows.Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Distinct().Count());
    }

    [Fact]
    public void Exactly_the_other_and_process_groups_are_non_service()
    {
        // ('key', N'Source', N'Group', NULL | N'reason' | @grouped_other)
        var rows = CategoryServiceRow().Matches(Seed("category_map")).ToList();

        Assert.Equal(27, rows.Count);
        Assert.All(rows, m =>
        {
            var nonService = m.Groups[2].Value != "NULL";
            Assert.Equal(m.Groups[1].Value is "Other" or "Process/Unclassified", nonService);
        });
    }

    [Fact]
    public void Every_source_profile_category_is_seeded()
    {
        // The 27 non-blank CategoryLevel1 values in docs/source-profile.md.
        var keys = MapRow().Matches(Seed("category_map")).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(27, keys.Count);
        Assert.Contains("homelesscampprimary", keys);
    }

    [Fact]
    public void Neighborhood_seed_matches_the_boundary_file()
    {
        using var geo = JsonDocument.Parse(File.ReadAllBytes(Repo.Path("data", "geo", "sacramento-neighborhoods.geojson")));
        var names = geo.RootElement.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("properties").GetProperty("NAME").GetString()!)
            .ToList();

        var seeded = NeighborhoodRow().Matches(Seed("neighborhood_alias"))
            .ToDictionary(m => m.Groups[1].Value, m => (Name: Unquote(m.Groups[2].Value), Slug: m.Groups[3].Value));

        Assert.Equal(129, names.Count);
        Assert.All(names, name =>
        {
            var key = MapKey.For(name)!;
            Assert.True(seeded.ContainsKey(key), $"No alias row for '{name}' ({key}).");
            Assert.Equal(name, seeded[key].Name);
            Assert.Equal(Neighborhood.Slug(name), seeded[key].Slug);
        });
        Assert.Equal(names.Count, seeded.Values.Select(v => v.Slug).Distinct().Count());
    }

    // (1, 'SentinelDate', N'...', 1)
    [GeneratedRegex(@"^\s*\((\d+),\s*'(\w+)',\s*N'(?:[^']|'')*',\s*([01])\)", RegexOptions.Multiline)]
    private static partial Regex DqFlagRow();

    // ('key', N'Source Value', N'Group')
    [GeneratedRegex(@"^\s*\('([a-z0-9]+)',\s*N'((?:[^']|'')*)',\s*N'", RegexOptions.Multiline)]
    private static partial Regex MapRow();

    // ('key', 'level2key', N'Level 1', N'Level 2', N'reason')
    [GeneratedRegex(@"^\s*\('([a-z0-9]+)',\s*'([a-z0-9]+)',\s*N'((?:[^']|'')*)',\s*N'((?:[^']|'')*)',\s*N'", RegexOptions.Multiline)]
    private static partial Regex NonServiceRow();

    // ('key', N'Source', N'Group', <reason>): group and whether the reason is NULL
    [GeneratedRegex(@"^\s*\('[a-z0-9]+',\s*N'(?:[^']|'')*',\s*N'((?:[^']|'')*)',\s*(NULL|N'|@)", RegexOptions.Multiline)]
    private static partial Regex CategoryServiceRow();

    // ('key', N'Name', 'slug')
    [GeneratedRegex(@"^\s*\('([a-z0-9]+)',\s*N'((?:[^']|'')*)',\s*'([a-z0-9-]+)'\)", RegexOptions.Multiline)]
    private static partial Regex NeighborhoodRow();
}
