# Capability availability boundary

Capability availability is an operator declaration, separate from tenant entitlements, application selections and live gateway readiness. The control-plane availability service accepts `available` and `not_configured`; declaring a service available requires an enabled inventory entry at the time of the write. The inventory record is locked until the declaration commits. Inventory and worker health can change afterward, so neither a declaration nor an image digest establishes current health, location or artifact attestation. Request admission and worker readiness must continue to fail closed independently.

Availability revisions advance for changes made by both current and older database writers. Writes require the last observed revision, preserve audit outcomes and do not change tenant selections. The revision migration must be applied before enabling the service; rolling back application code must preserve the revision table and trigger. An operator API and workspace are still required before this service can be used through the portal.

The Cloud gateway is a capability service with versioned protocols and authorization boundaries. SDKs consume those contracts as clients; SDK-specific behavior belongs in the SDK unless a gateway-specific implementation is necessary for the public contract.
