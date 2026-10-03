namespace Sac311.Api.Health;

internal sealed class FreshnessOptions
{
    public const string SectionName = "Freshness";

    /// <summary>Data is stale when no ingestion run has succeeded for this long (three missed 15-minute incrementals).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(45);
}

/// <summary>Whether the data is fresh: shared by the readiness check and <c>/api/meta/freshness</c>.</summary>
internal static class Freshness
{
    public const string Fresh = "fresh";
    public const string Stale = "stale";

    public static string Evaluate(DateTime? lastSuccessUtc, DateTime nowUtc, TimeSpan maxAge) =>
        lastSuccessUtc is { } last && nowUtc - last <= maxAge ? Fresh : Stale;
}
