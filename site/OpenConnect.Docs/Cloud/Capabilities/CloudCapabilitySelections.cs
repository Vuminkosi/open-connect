namespace OpenConnect.Docs.Cloud;

public interface ICloudCapabilitySelectionStore
{
    Task<CloudCapabilitySelectionResult> ChangeAsync(
        CloudSubject actor, Guid tenantId, Guid? applicationId, string capabilityId, bool enabled,
        Func<string, bool> authorize, Func<CloudCapabilitySnapshot, CloudCapabilitySelectionResult> evaluate,
        CancellationToken cancellationToken);
}
