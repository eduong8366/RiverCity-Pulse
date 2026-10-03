using Sac311.Api.Data;
using Sac311.Api.Endpoints;

namespace Sac311.Api.Tests;

public class BacklogWeeksTests
{
    [Theory]
    [InlineData("2026-09-28", "2026-09-28")] // Monday
    [InlineData("2026-10-02", "2026-09-28")] // Friday
    [InlineData("2026-10-04", "2026-09-28")] // Sunday
    public void Week_starts_on_monday(string day, string monday)
    {
        Assert.Equal(DateOnly.Parse(monday, System.Globalization.CultureInfo.InvariantCulture),
            BacklogWeeks.WeekStart(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Weeks_sum_opened_and_closed_and_keep_the_last_days_open_count()
    {
        var sunday = new DateOnly(2026, 9, 27);
        BacklogPoint[] days =
        [
            new(sunday, 5, 1, 104),
            new(sunday.AddDays(1), 3, 2, 105),
            new(sunday.AddDays(2), 4, 6, 103),
            new(sunday.AddDays(3), 1, 0, 104),
        ];

        var weeks = BacklogWeeks.Group(days);

        Assert.Equal(
            [new BacklogPoint(new DateOnly(2026, 9, 21), 5, 1, 104), new BacklogPoint(new DateOnly(2026, 9, 28), 8, 8, 104)],
            weeks);
    }
}
