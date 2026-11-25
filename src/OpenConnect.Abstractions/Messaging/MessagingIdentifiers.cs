namespace OpenConnect.Abstractions.Messaging;

/// <summary>Identifies a messaging conversation within a tenant and application.</summary>
public readonly record struct ConversationId(Ulid Value);

/// <summary>Identifies a committed message within a conversation.</summary>
public readonly record struct MessageId(Ulid Value);

/// <summary>Identifies an enrolled client or agent cryptographic device.</summary>
public readonly record struct DeviceId(Ulid Value);

/// <summary>Identifies a voice/media call invitation and session lifecycle.</summary>
public readonly record struct CallId(Ulid Value);

/// <summary>Identifies a single-use device enrollment pairing.</summary>
public readonly record struct EnrollmentId(Ulid Value);

/// <summary>Identifies a durable scheduled operational follow-up.</summary>
public readonly record struct FollowUpId(Ulid Value);

/// <summary>Identifies a durable agent continuation triggered by a reply or timeout.</summary>
public readonly record struct ContinuationId(Ulid Value);

/// <summary>Identifies an encrypted attachment object reservation.</summary>
public readonly record struct AttachmentId(Ulid Value);

/// <summary>Identifies a published single-use device public key package.</summary>
public readonly record struct KeyPackageId(Ulid Value);

/// <summary>Identifies a rotating device refresh credential grant.</summary>
public readonly record struct RefreshGrantId(Ulid Value);

/// <summary>Identifies a device push notification registration.</summary>
public readonly record struct PushRegistrationId(Ulid Value);

/// <summary>Identifies a typed approval request bound to a conversation action.</summary>
public readonly record struct ApprovalRequestId(Ulid Value);
