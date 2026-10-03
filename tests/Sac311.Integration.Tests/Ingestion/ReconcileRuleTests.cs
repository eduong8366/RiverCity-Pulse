using Sac311.Ingestion;

namespace Sac311.Integration.Tests.Ingestion;

public class ReconcileRuleTests
{
    private static readonly TimeSpan HalfPastThree = new(3, 30, 0);

    [Theory]
    [InlineData(950, 1000, false)]
    [InlineData(949, 1000, true)]
    [InlineData(10, null, false)]
    public void Guard_fails_below_95_percent_of_the_baseline(int keys, int? baseline, bool fails) =>
        Assert.Equal(fails, ReconcileJob.GuardFails(keys, baseline, 0.95));

    [Fact]
    public void In_clause_quotes_and_escapes_reference_numbers() =>
        Assert.Equal("ReferenceNumber IN ('240101-1','O''Brien')", ReconcileJob.InClause(["240101-1", "O'Brien"]));

    [Fact]
    public void Next_reconcile_is_later_the_same_day_in_summer()
    {
        // 2026-07-01 01:00 PDT = 08:00 UTC; 03:30 PDT = 10:30 UTC.
        var now = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 7, 1, 10, 30, 0), ReconcileJob.NextRunUtc(now, HalfPastThree));
    }

    [Fact]
    public void Next_reconcile_is_the_next_day_once_the_time_has_passed_in_winter()
    {
        // 2026-12-01 09:00 PST = 17:00 UTC; next 03:30 PST = 2026-12-02 11:30 UTC.
        var now = new DateTime(2026, 12, 1, 17, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 12, 2, 11, 30, 0), ReconcileJob.NextRunUtc(now, HalfPastThree));
    }

    [Fact]
    public void Next_reconcile_across_the_spring_forward_uses_the_new_offset()
    {
        // 2026-03-08 is the US spring-forward day: 03:30 that morning is PDT (UTC−7) = 10:30 UTC.
        var now = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 3, 8, 10, 30, 0), ReconcileJob.NextRunUtc(now, HalfPastThree));
    }

    [Fact]
    public void A_local_time_that_does_not_exist_moves_an_hour_later()
    {
        // 02:30 doesn't exist on 2026-03-08; it runs at 03:30 PDT.
        var now = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 3, 8, 10, 30, 0), ReconcileJob.NextRunUtc(now, new TimeSpan(2, 30, 0)));
    }
}
