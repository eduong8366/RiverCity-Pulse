using System.Text.Json;

namespace Sac311.Domain.Tests;

/// <summary>
/// Loads fixture rows. <c>real/</c> files come from the live feed via <c>worker capture-fixture</c>;
/// <c>synthetic/</c> files cover cases the live data doesn't contain and say so in their description.
/// </summary>
internal static class Fixture
{
    public static IReadOnlyList<SourceRow> Real(string name) => Load("real", name);

    public static IReadOnlyList<SourceRow> Synthetic(string name) => Load("synthetic", name);

    public static IEnumerable<string> AllRealNames() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    private static List<SourceRow> Load(string folder, string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name + ".json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var rows = doc.RootElement.GetProperty("features").EnumerateArray().Select(SourceRow.FromFeature).ToList();
        Assert.NotEmpty(rows);
        return rows;
    }
}

/// <summary>Files outside the test output (seeds, boundary GeoJSON), found by walking up to Sac311.slnx.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Sac311.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Sac311.slnx not found above " + AppContext.BaseDirectory);
    }
}
