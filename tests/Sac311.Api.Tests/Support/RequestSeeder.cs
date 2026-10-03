using System.Globalization;
using Dapper;
using Sac311.Domain;
using Sac311.Domain.Cleaners;

namespace Sac311.Api.Tests.Support;

/// <summary>
/// Writes cleaned requests straight into <c>dbo.service_request</c>, dated relative to an as-of time, so a test controls
/// exactly which requests each window sees. Close times fall at 12:00 Sacramento time on their day.
/// </summary>
internal sealed class RequestSeeder(DateTime asOfUtc)
{
    private readonly List<object> _rows = [];

    /// <summary>A request closed <paramref name="closedDaysAgo"/> local days before the as-of date, <paramref name="days"/> after it was created.</summary>
    public RequestSeeder Closed(string? slug, byte? district, string category, int closedDaysAgo, decimal days)
    {
        var closed = asOfUtc.Date.AddDays(-closedDaysAgo).AddHours(19);
        var created = closed.AddDays((double)-days);
        return Add(slug, district, category, "Closed", created, closed, Pacific.ToLocalDate(closed), days, DqFlags.None);
    }

    /// <summary>A Closed request with no close date (<see cref="DqFlags.ClosedMissingDate"/>): left out of the medians, counted as excluded.</summary>
    public RequestSeeder ClosedWithoutDate(string? slug, byte? district, string category, int lastUpdatedDaysAgo, int createdDaysAgo)
    {
        var left = Pacific.ToLocalDate(asOfUtc.Date.AddDays(-lastUpdatedDaysAgo).AddHours(19));
        return Add(slug, district, category, "Closed", asOfUtc.AddDays(-createdDaysAgo), null, left, null, DqFlags.ClosedMissingDate);
    }

    /// <summary>A request still open, created exactly <paramref name="createdDaysAgo"/> days before the as-of time.</summary>
    public RequestSeeder Open(string? slug, byte? district, string category, int createdDaysAgo) =>
        Add(slug, district, category, "Open", asOfUtc.AddDays(-createdDaysAgo), null, null, null, DqFlags.None);

    public async Task SaveAsync(SqlServerFixture db)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.service_request
                (reference_number, object_id, category_group, source_channel, district_number, is_city, neighborhood_slug, status_group,
                 created_utc, updated_utc, closed_utc, created_date_local, closed_date_local, backlog_close_date_local, days_to_close,
                 dq_flags, row_hash, first_seen_utc, last_seen_utc, last_changed_utc)
            VALUES
                (@ReferenceNumber, @ObjectId, @Category, N'Phone', @District, @IsCity, @Slug, @Status,
                 @CreatedUtc, @UpdatedUtc, @ClosedUtc, @CreatedLocal, @ClosedLocal, @BacklogCloseLocal, @Days,
                 @Flags, @Hash, @UpdatedUtc, @UpdatedUtc, @UpdatedUtc);
            """,
            _rows);
        _rows.Clear();
    }

    private RequestSeeder Add(
        string? slug, byte? district, string category, string status, DateTime createdUtc, DateTime? closedUtc, DateOnly? leftBacklog, decimal? days,
        DqFlags flags)
    {
        var n = _rows.Count + 1;
        _rows.Add(new
        {
            ReferenceNumber = string.Create(CultureInfo.InvariantCulture, $"TEST-{n:D6}"),
            ObjectId = (long)n,
            Category = category,
            District = district,
            IsCity = district is null ? (bool?)null : true,
            Slug = slug,
            Status = status,
            CreatedUtc = createdUtc,
            UpdatedUtc = closedUtc ?? createdUtc,
            ClosedUtc = closedUtc,
            CreatedLocal = Pacific.ToLocalDate(createdUtc).ToDateTime(TimeOnly.MinValue),
            ClosedLocal = Pacific.ToLocalDate(closedUtc)?.ToDateTime(TimeOnly.MinValue),
            BacklogCloseLocal = leftBacklog?.ToDateTime(TimeOnly.MinValue),
            Days = days,
            Flags = (int)flags,
            Hash = new byte[32],
        });
        return this;
    }
}
