namespace OpenConnect.Docs.Cloud;

public static class CloudQuotaUnits
{
    // Catalog billing descriptions are display text, not a list of interchangeable units.
    // Classification request admission has a defined unit; compute accounting is separate.
    public static bool Supports(string capabilityId, string billingDescription, string unit) =>
        capabilityId == "classification.classify"
            ? unit == "request"
            : CloudDeploymentRegistry.ValidToken(billingDescription, 80) && billingDescription == unit;
}
