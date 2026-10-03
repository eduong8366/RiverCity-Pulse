using Sac311.Api.Data;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Endpoints;

/// <summary>Validation of the shared query parameters. Each method records a message in <c>errors</c> when the value is bad.</summary>
internal static class QueryRules
{
    public const int DefaultWindow = 90;
    public const int Districts = 8;

    /// <summary><c>window</c>: 30, 90 or 365 days (default 90).</summary>
    public static int Window(int? window, Dictionary<string, string[]> errors)
    {
        var value = window ?? DefaultWindow;
        if (!AggregateBuilder.Windows.Contains(value))
        {
            errors["window"] = [$"window must be one of {string.Join(", ", AggregateBuilder.Windows)} (days)."];
        }

        return value;
    }

    /// <summary><c>district</c>: council district 1–8, or absent for all of them.</summary>
    public static byte District(int? district, Dictionary<string, string[]> errors)
    {
        if (district is null)
        {
            return AggregateCell.AllDistricts;
        }

        if (district is < 1 or > Districts)
        {
            errors["district"] = [$"district must be a council district from 1 to {Districts}."];
            return AggregateCell.AllDistricts;
        }

        return (byte)district.Value;
    }

    /// <summary><c>category</c>: a category group (any case; returned as stored), or absent for all of them.</summary>
    public static async Task<string?> CategoryAsync(string? category, StatsReader reader, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var known = await reader.CategoriesAsync(cancellationToken).ConfigureAwait(false);
        var match = known.FirstOrDefault(k => string.Equals(k, category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            errors["category"] = [$"Unknown category '{category}'. Known categories: {string.Join(", ", known)}."];
        }

        return match;
    }
}
