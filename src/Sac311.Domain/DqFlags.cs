using System.Diagnostics.CodeAnalysis;

namespace Sac311.Domain;

/// <summary>
/// Data-quality flags stored as a bit mask in <c>service_request.dq_flags</c>.
/// Every single-bit member has a row in <c>ref.dq_flag</c> (db/seed/dq_flag.sql) with the same value.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Matches the dq_flags column and ref.dq_flag table.")]
public enum DqFlags
{
    None = 0,
    SentinelDate = 1,
    FutureDate = 2,
    InvalidCloseOrder = 4,
    ClosedMissingDate = 8,
    AddressJunk = 16,
    NeighborhoodMissingInCity = 32,
    GeoOutOfBounds = 64,
    UnmappedCategory = 128,
    UnmappedSource = 256,
    UnknownStatus = 512,
    UnknownDistrict = 1024,
    InvalidZip = 2048,

    /// <summary>Rows with any of these are left out of response-time metrics (<c>is_metric_eligible = 0</c>).</summary>
    MetricExclusions = SentinelDate | FutureDate | InvalidCloseOrder | ClosedMissingDate,
}
