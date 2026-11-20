namespace OpenConnect.Docs.Cloud;

public static class CloudQuotaUnits
{
    // Catalog billing descriptions are display text, not a list of interchangeable units.
    // Inference token budgets count input and output together; separate usage evidence is still required.
    public static bool Supports(string capabilityId, string billingDescription, string unit) =>
        capabilityId switch
        {
            "classification.classify" => unit == "request",
            "inference.chat" => unit == "token",
            "inference.liveui" => unit == "plan",
            _ => CloudDeploymentRegistry.ValidToken(billingDescription, 80) && billingDescription == unit,
        };
}
