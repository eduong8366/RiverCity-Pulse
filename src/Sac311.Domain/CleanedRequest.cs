using Sac311.Domain.Cleaners;
using C = Sac311.Domain.Cleaners;

namespace Sac311.Domain;

/// <summary>
/// One source row after every cleaner has run: the shape of <c>stg.request</c>. The map keys (category, source,
/// neighborhood) are resolved to groups and canonical names by <c>usp_apply_batch</c>, which also adds the Unmapped flags.
/// </summary>
public sealed record CleanedRequest
{
    public required string ReferenceNumber { get; init; }
    public long ObjectId { get; init; }
    public string? SfTicketId { get; init; }
    public string? CategoryLevel1 { get; init; }
    public string? CategoryLevel2 { get; init; }
    public string? CategoryName { get; init; }
    public string? CategoryKey { get; init; }
    public string? SourceChannelRaw { get; init; }
    public string? SourceKey { get; init; }
    public byte? DistrictNumber { get; init; }
    public bool? IsCity { get; init; }
    public string? Neighborhood { get; init; }
    public string? NeighborhoodKey { get; init; }
    public string? NeighborhoodSlug { get; init; }
    public string? Address { get; init; }
    public string? CrossStreet { get; init; }
    public string? Zip { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public string? PublicStatus { get; init; }
    public StatusGroup StatusGroup { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }
    public DateOnly? CreatedDateLocal { get; init; }
    public DateOnly? ClosedDateLocal { get; init; }
    public DateOnly? BacklogCloseDateLocal { get; init; }
    public decimal? DaysToClose { get; init; }
    public DqFlags Flags { get; init; }
#pragma warning disable CA1819 // A SHA-256 handed straight to SqlBulkCopy; copying it buys nothing.
    public required byte[] RowHash { get; init; }
#pragma warning restore CA1819

    /// <summary>
    /// Cleans a row that passed <see cref="Record.Validate"/>. <paramref name="nowUtc"/> is the reference for
    /// <see cref="DqFlags.FutureDate"/>.
    /// </summary>
    public static CleanedRequest From(SourceRow row, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Record.Validate(row) is { } reason)
        {
            throw new ArgumentException($"Row {row.ObjectId} can't be cleaned: {reason}. Reject it instead.", nameof(row));
        }

        var status = Status.Group(row.PublicStatus);
        var district = District.Parse(row.CouncilDistrictNumber);
        var neighborhood = C.Neighborhood.Normalize(row.Neighborhood, district.IsCity);
        var address = C.Address.Normalize(row.Address);
        var zip = C.Zip.Normalize(row.Zip);
        var geo = Geo.Validate(row.X, row.Y);
        var created = EsriDate.ToUtc(row.DateCreated, nowUtc);
        var updated = EsriDate.ToUtc(row.DateUpdated, nowUtc);
        var closed = EsriDate.ToUtc(row.DateClosed, nowUtc);
        var resolution = Resolution.Compute(status.Value, created.Value, updated.Value, closed.Value);
        var category1 = Text.NullIfBlank(row.CategoryLevel1);
        var source = Text.NullIfBlank(row.SourceLevel1);

        return new CleanedRequest
        {
            ReferenceNumber = Text.NullIfBlank(row.ReferenceNumber)!,
            ObjectId = row.ObjectId,
            SfTicketId = Text.NullIfBlank(row.SfTicketId),
            CategoryLevel1 = category1,
            CategoryLevel2 = Text.NullIfBlank(row.CategoryLevel2),
            CategoryName = Text.NullIfBlank(row.CategoryName),
            CategoryKey = MapKey.For(category1),
            SourceChannelRaw = source,
            SourceKey = MapKey.For(source),
            DistrictNumber = district.Number,
            IsCity = district.IsCity,
            Neighborhood = neighborhood.Name,
            NeighborhoodKey = neighborhood.Key,
            NeighborhoodSlug = neighborhood.Slug,
            Address = address.Value,
            CrossStreet = Text.NullIfBlank(row.CrossStreet),
            Zip = zip.Value,
            Latitude = geo.Latitude,
            Longitude = geo.Longitude,
            PublicStatus = Text.NullIfBlank(row.PublicStatus),
            StatusGroup = status.Value,
            CreatedUtc = created.Value,
            UpdatedUtc = updated.Value!.Value,
            ClosedUtc = closed.Value,
            CreatedDateLocal = Pacific.ToLocalDate(created.Value),
            ClosedDateLocal = Pacific.ToLocalDate(closed.Value),
            BacklogCloseDateLocal = resolution.BacklogCloseDateLocal,
            DaysToClose = resolution.DaysToClose,
            Flags = status.Flags | district.Flags | neighborhood.Flags | address.Flags | zip.Flags | geo.Flags
                | created.Flags | updated.Flags | closed.Flags | resolution.Flags,
            RowHash = RowHasher.Hash(row),
        };
    }
}
