namespace Sac311.Domain.Cleaners;

/// <summary>Reasons stored in <c>ops.ingest_reject.reason</c>.</summary>
public static class RejectReason
{
    public const string InvalidObjectId = "InvalidObjectId";
    public const string MissingReferenceNumber = "MissingReferenceNumber";
    public const string ReferenceNumberTooLong = "ReferenceNumberTooLong";
    public const string MissingDateUpdated = "MissingDateUpdated";
    public const string InvalidDateUpdated = "InvalidDateUpdated";
}

public static class Record
{
    public const int MaxReferenceNumberLength = 50;

    private static readonly long MinValidEpochMilliseconds = new DateTimeOffset(EsriDate.MinValidUtc).ToUnixTimeMilliseconds();

    /// <summary>
    /// Returns why a row can't be loaded, or null if it can. Only rows missing what the pipeline needs to
    /// identify and order them are rejected: everything else is cleaned and flagged instead.
    /// </summary>
    public static string? Validate(SourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.ObjectId <= 0)
        {
            return RejectReason.InvalidObjectId;
        }

        var reference = Text.NullIfBlank(row.ReferenceNumber);
        if (reference is null)
        {
            return RejectReason.MissingReferenceNumber;
        }

        if (reference.Length > MaxReferenceNumberLength)
        {
            return RejectReason.ReferenceNumberTooLong;
        }

        if (row.DateUpdated is not { } updated)
        {
            return RejectReason.MissingDateUpdated;
        }

        // DateUpdated drives the incremental watermark, so a placeholder date can't be flagged and kept.
        return updated < MinValidEpochMilliseconds ? RejectReason.InvalidDateUpdated : null;
    }
}
