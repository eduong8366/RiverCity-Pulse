using Dapper;

namespace Sac311.Api.Tests.Support;

/// <summary>
/// One database for a test class, seeded with a small scenario whose figures are worked out by hand in the tests,
/// aggregated as of <see cref="AsOfUtc"/> (2026-10-02 in Sacramento), and the API on top of it, its clock 10 minutes later.
/// <list type="bullet">
/// <item>downtown (district 4), Streets: closed 1, 2, 3 and 4 days ago after 1, 2, 3 and 10 days; one closed 40 days ago after
/// 100 days; one closed without a close date 5 days ago; two open, created 2 and 4 days ago.</item>
/// <item>central-oak-park (district 5), Solid Waste: 30 closed in the last 30 days after 1..30 days, 30 closed in the 30 days
/// before after 2 days each.</item>
/// <item>outside the city (no neighborhood or district), Water: one closed 6 days ago after 7 days.</item>
/// <item>non-service: outside the city, two "Other / Information" calls closed 1 and 2 days ago after 0.01 days and one
/// "Review / Email Review" item open since 3 days ago; in downtown, one "Parking / General" call closed 2 days ago after 0.02 days.</item>
/// <item>a clear-out: outside the city, 100 Parking meter requests closed 3 days ago after 200 days.</item>
/// </list>
/// Plus a run that succeeded 20 minutes before the API's clock, and two data-quality results (one Pass, one Warn).
/// </summary>
public sealed class SeededApi : IAsyncLifetime, IDisposable
{
    public static readonly DateTime AsOfUtc = new(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc);

    private ApiFactory? _factory;

    public SqlServerFixture Db { get; } = new();

    public ApiFactory Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();

        var seed = new RequestSeeder(AsOfUtc)
            .Closed("downtown", 4, "Streets", 1, 1m)
            .Closed("downtown", 4, "Streets", 2, 2m)
            .Closed("downtown", 4, "Streets", 3, 3m)
            .Closed("downtown", 4, "Streets", 4, 10m)
            .Closed("downtown", 4, "Streets", 40, 100m)
            .ClosedWithoutDate("downtown", 4, "Streets", 5, 8)
            .Open("downtown", 4, "Streets", 2)
            .Open("downtown", 4, "Streets", 4)
            .Closed(null, null, "Water", 6, 7m)
            .Closed(null, null, "Other", 1, 0.01m, "Other", "Information")
            .Closed(null, null, "Other", 2, 0.01m, "Other", "Information")
            .Open(null, null, "Process/Unclassified", 3, "Review", "Email Review")
            .Closed("downtown", 4, "Parking", 2, 0.02m, "Parking", "General");
        for (var i = 0; i < 100; i++)
        {
            seed.Closed(null, null, "Parking", 3, 200m, "Parking", "Meter");
        }

        for (var i = 0; i < 30; i++)
        {
            seed.Closed("central-oak-park", 5, "Solid Waste", i, i + 1)
                .Closed("central-oak-park", 5, "Solid Waste", 30 + i, 2m);
        }

        await seed.SaveAsync(Db);
        await ApiFactory.RefreshAggregatesAsync(Db, AsOfUtc);

        await using (var conn = await Db.OpenAsync())
        {
            var runId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO ops.ingest_run (pipeline, status, started_utc, finished_utc, rows_fetched, rows_inserted, rows_updated)
                OUTPUT inserted.run_id
                VALUES ('Incremental', 'Succeeded', DATEADD(minute, -11, @AsOfUtc), DATEADD(minute, -10, @AsOfUtc), 3, 1, 2);
                """,
                new { AsOfUtc });
            await conn.ExecuteAsync(
                """
                INSERT INTO ops.ingest_checkpoint (pipeline, watermark_utc, run_id) VALUES ('Incremental', DATEADD(minute, -30, @AsOfUtc), @runId);
                INSERT INTO ops.dq_result (run_id, check_name, status, message) VALUES
                    (@runId, 'source_count', 'Pass', 'ok'),
                    (@runId, 'null_rate.address', 'Warn', 'Address null rate up 7 points');
                """,
                new { AsOfUtc, runId });
        }

        _factory = new ApiFactory(Db.ConnectionString, AsOfUtc.AddMinutes(10));
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Db.DisposeAsync();
    }

    public void Dispose() => _factory?.Dispose();
}
