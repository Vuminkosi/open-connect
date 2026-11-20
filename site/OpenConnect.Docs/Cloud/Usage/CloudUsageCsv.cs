using System.Globalization;
using System.Text;

namespace OpenConnect.Docs.Cloud;

public static class CloudUsageCsv
{
    public static byte[] Encode(IReadOnlyList<CloudUsageEntry> entries)
    {
        var csv = new StringBuilder("recorded_at_utc,application_id,request_id,capability_id,unit,quantity,provenance,reservation_id\r\n");
        foreach (var entry in entries)
        {
            csv.Append(Field(entry.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Field(entry.ApplicationId.ToString("D"))).Append(',').Append(Field(entry.RequestId.ToString("D"))).Append(',')
                .Append(Field(entry.CapabilityId)).Append(',').Append(Field(entry.Unit)).Append(',')
                .Append(entry.Quantity.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Field(entry.Provenance)).Append(',')
                .Append(Field(entry.ReservationId.ToString("D"))).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string Field(string? value)
    {
        value ??= "";
        // Protect spreadsheet consumers from formula interpretation, including after leading whitespace.
        var trimmed = value.TrimStart();
        if ((trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) || (value.Length > 0 && char.IsControl(value[0]))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
