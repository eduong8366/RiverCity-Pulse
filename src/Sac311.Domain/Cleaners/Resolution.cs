namespace Sac311.Domain.Cleaners;

/// <param name="DaysToClose">Fractional days from created to closed; only for Closed rows with valid dates.</param>
/// <param name="BacklogCloseDateLocal">The local date the request left the backlog; null while it is still open.</param>
public sealed record ResolutionResult(decimal? DaysToClose, DateOnly? BacklogCloseDateLocal, DqFlags Flags);

public static class Resolution
{
    /// <summary>
    /// Response-time and backlog fields for one request:
    /// <list type="bullet">
    /// <item><see cref="DqFlags.InvalidCloseOrder"/> when closed is earlier than created; no days_to_close.</item>
    /// <item><see cref="DqFlags.ClosedMissingDate"/> when a Closed row has no close date; it leaves the backlog on its
    /// last-updated date so it doesn't count as open forever.</item>
    /// <item>Cancelled rows leave the backlog on their close (or last-updated) date.</item>
    /// <item>The backlog close date is never before the created date.</item>
    /// </list>
    /// </summary>
    public static ResolutionResult Compute(StatusGroup status, DateTime? createdUtc, DateTime? updatedUtc, DateTime? closedUtc)
    {
        var flags = DqFlags.None;
        var invalidOrder = createdUtc is { } c0 && closedUtc is { } x0 && x0 < c0;
        if (invalidOrder)
        {
            flags |= DqFlags.InvalidCloseOrder;
        }

        if (status == StatusGroup.Closed && closedUtc is null)
        {
            flags |= DqFlags.ClosedMissingDate;
        }

        decimal? days = null;
        if (status == StatusGroup.Closed && !invalidOrder && createdUtc is { } created && closedUtc is { } closed)
        {
            days = Math.Round((decimal)(closed - created).TotalDays, 2, MidpointRounding.AwayFromZero);
        }

        var leftBacklogUtc = status switch
        {
            StatusGroup.Closed or StatusGroup.Cancelled => closedUtc ?? updatedUtc,
            StatusGroup.Unknown => closedUtc,
            _ => null,
        };

        var backlogClose = Pacific.ToLocalDate(leftBacklogUtc);
        var createdLocal = Pacific.ToLocalDate(createdUtc);
        if (backlogClose is { } b && createdLocal is { } cl && b < cl)
        {
            backlogClose = cl;
        }

        return new ResolutionResult(days, backlogClose, flags);
    }
}
