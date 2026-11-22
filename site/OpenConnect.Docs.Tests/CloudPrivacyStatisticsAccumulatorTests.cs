using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudPrivacyStatisticsAccumulatorTests
{
    [Fact]
    public void Aggregates_categories_and_preserves_missing_evidence()
    {
        var accumulator = new CloudPrivacyStatisticsAccumulator();
        accumulator.Add("privacy_blocked", "{\"EMAIL\":2,\"PHONE\":1}");
        accumulator.Add("privacy_blocked", "{\"EMAIL\":3}");
        accumulator.Add("allowed", "{}");
        accumulator.Add("allowed", null);
        accumulator.Add("detector_unavailable", null);

        var result = accumulator.Snapshot();
        Assert.Equal((2L, 2L, 1L, 2L), (result.Allowed, result.Blocked, result.DetectorUnavailable, result.CategoryEvidenceUnavailable));
        Assert.Equal([new CloudPrivacyCategoryTotal("EMAIL", 5), new CloudPrivacyCategoryTotal("PHONE", 1)], result.Categories);
    }

    [Fact]
    public void Rejects_invalid_category_evidence_instead_of_reporting_it()
    {
        var accumulator = new CloudPrivacyStatisticsAccumulator();
        Assert.Throws<ArgumentException>(() => accumulator.Add("privacy_blocked", "{\"raw text\":1}"));
    }

    [Fact]
    public void Host_totals_weight_grouped_decisions_and_keep_actions_separate()
    {
        var accumulator = new CloudHostPrivacyStatisticsAccumulator();
        accumulator.Add("Output", true, "Mask", "allowed", "{\"EMAIL\":2}", 3);
        accumulator.Add("Output", false, "Block", "inspection_failed", "{\"EMAIL\":1,\"PHONE\":2}", 2);
        accumulator.Add("ExternalModel", true, "Allow", "allowed", "{\"SECRET\":1}", 1);

        var result = accumulator.Snapshot();
        Assert.Equal((1L, 5L, 4L, 2L), (result.OtherDestinations, result.OutputDecisions, result.Allowed, result.Blocked));
        Assert.Equal([new CloudPrivacyActionTotal("Allow", 1), new CloudPrivacyActionTotal("Block", 2),
            new CloudPrivacyActionTotal("Mask", 3)], result.Actions);
        Assert.Equal([new CloudPrivacyCategoryTotal("EMAIL", 8), new CloudPrivacyCategoryTotal("PHONE", 4),
            new CloudPrivacyCategoryTotal("SECRET", 1)], result.Categories);
        Assert.Equal([new CloudPrivacyCategoryTotal("EMAIL", 8), new CloudPrivacyCategoryTotal("PHONE", 4)], result.OutputCategories);
        Assert.Equal([new CloudPrivacyCategoryTotal("SECRET", 1)], result.OtherDestinationCategories);
        Assert.Equal([new CloudPrivacyReasonTotal("allowed", 4), new CloudPrivacyReasonTotal("inspection_failed", 2)], result.Reasons);
        Assert.Throws<ArgumentException>(() => accumulator.Add("Output", true, "Mask", "allowed", "{\"raw text\":1}", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.Add("Output", true, "Mask", "allowed", "{}", 0));
    }
}
