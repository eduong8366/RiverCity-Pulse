using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sac311.Domain;

/// <summary>
/// SHA-256 over a row's raw source values. OBJECTID and GlobalID are left out because a republish can
/// reassign them without the request changing. Hashing raw (not cleaned) values means a change to a cleaning
/// rule never creates spurious history rows.
/// </summary>
public static class RowHasher
{
    // Changing the field list, order or encoding changes every hash: bump the golden test deliberately.
    public static byte[] Hash(SourceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var sb = new StringBuilder(512);
        Append(sb, "ReferenceNumber", row.ReferenceNumber);
        Append(sb, "CategoryLevel1", row.CategoryLevel1);
        Append(sb, "CategoryLevel2", row.CategoryLevel2);
        Append(sb, "CategoryName", row.CategoryName);
        Append(sb, "CouncilDistrictNumber", row.CouncilDistrictNumber);
        Append(sb, "SourceLevel1", row.SourceLevel1);
        Append(sb, "Neighborhood", row.Neighborhood);
        Append(sb, "DateCreated", row.DateCreated);
        Append(sb, "DateUpdated", row.DateUpdated);
        Append(sb, "DateClosed", row.DateClosed);
        Append(sb, "CrossStreet", row.CrossStreet);
        Append(sb, "ZIP", row.Zip);
        Append(sb, "SFTicketID", row.SfTicketId);
        Append(sb, "Address", row.Address);
        Append(sb, "Data_Source", row.DataSource);
        Append(sb, "PublicStatus", row.PublicStatus);
        Append(sb, "x", row.X);
        Append(sb, "y", row.Y);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public static string ToHex(byte[] hash) => Convert.ToHexStringLower(hash);

    // name US (NUL | SOH value) RS: the tag byte keeps null distinct from "" and from the text "null".
    private static void Append(StringBuilder sb, string name, string? value)
    {
        sb.Append(name).Append('\u001F');
        if (value is null)
        {
            sb.Append('\u0000');
        }
        else
        {
            sb.Append('\u0001').Append(value);
        }

        sb.Append('\u001E');
    }

    private static void Append(StringBuilder sb, string name, long? value) =>
        Append(sb, name, value?.ToString(CultureInfo.InvariantCulture));

    // Shortest round-trip form, so the same double always gives the same text.
    private static void Append(StringBuilder sb, string name, double? value) =>
        Append(sb, name, value?.ToString("R", CultureInfo.InvariantCulture));
}
