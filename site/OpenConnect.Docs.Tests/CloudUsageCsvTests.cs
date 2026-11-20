using System.Text;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudUsageCsvTests
{
    [Fact]
    public void QuotesFieldsAndNeutralizesSpreadsheetFormulas()
    {
        var entry = new CloudUsageEntry(Guid.Empty, Guid.Empty, Guid.Empty, "  =SUM(1,2)", "\t+cmd", 1,
            "x\"y", DateTimeOffset.UnixEpoch);
        var csv = Encoding.UTF8.GetString(CloudUsageCsv.Encode([entry]));
        Assert.Contains("\"'  =SUM(1,2)\"", csv);
        Assert.Contains("\"'\t+cmd\"", csv);
        Assert.Contains("\"x\"\"y\"", csv);
        Assert.DoesNotContain("\"  =SUM", csv);
    }
}
