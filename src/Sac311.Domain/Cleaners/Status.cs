namespace Sac311.Domain.Cleaners;

public enum StatusGroup
{
    Unknown,
    Open,
    Closed,
    Cancelled,
}

public static class Status
{
    /// <summary>NEW and IN PROGRESS are Open; CLOSED is Closed; CANCELLED is Cancelled; anything else is Unknown with a flag.</summary>
    public static Cleaned<StatusGroup> Group(string? publicStatus) => MapKey.For(publicStatus) switch
    {
        "new" or "inprogress" => new(StatusGroup.Open, DqFlags.None),
        "closed" => new(StatusGroup.Closed, DqFlags.None),
        "cancelled" or "canceled" => new(StatusGroup.Cancelled, DqFlags.None),
        _ => new(StatusGroup.Unknown, DqFlags.UnknownStatus),
    };
}
