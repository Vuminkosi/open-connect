namespace OpenConnect.Abstractions.Messaging;

/// <summary>Canonical wire event types for OpenConnect IMessaging.</summary>
public static class MessagingEventTypes
{
    public const string MessageCommitted = "messaging.message.committed";
    public const string MessageDelivered = "messaging.message.delivered";
    public const string MessageRead = "messaging.message.read";
    public const string MembershipChanged = "messaging.membership.changed";
    public const string KeyCommit = "messaging.key.commit";
    public const string PresenceChanged = "messaging.presence.changed";
    public const string TypingChanged = "messaging.typing.changed";
    public const string CallInvited = "messaging.call.invited";
    public const string CallAccepted = "messaging.call.accepted";
    public const string CallDeclined = "messaging.call.declined";
    public const string CallEnded = "messaging.call.ended";
    public const string CallMissed = "messaging.call.missed";
    public const string FollowUpDue = "messaging.followup.due";
    public const string ApprovalRequested = "messaging.approval.requested";
    public const string ApprovalResolved = "messaging.approval.resolved";
    public const string DeviceRevoked = "messaging.device.revoked";
    public const string CapabilitiesChanged = "messaging.capabilities.changed";
}

/// <summary>Canonical capability identifiers advertised only when operationally verified.</summary>
public static class MessagingCapabilityIds
{
    public const string InAppSend = "messaging.inapp.send";
    public const string InAppReceive = "messaging.inapp.receive";
    public const string InAppCall = "messaging.inapp.call";
    public const string E2EE = "messaging.e2ee";
    public const string PushFcm = "messaging.push.fcm";
    public const string PushWeb = "messaging.push.web";
    public const string DeviceEnrollment = "messaging.device.enrollment";
}

/// <summary>Canonical command kinds sent from the .NET gateway to the private IMessaging worker.</summary>
public static class MessagingWorkerCommandKinds
{
    public const string Route = "messaging.route";
    public const string Presence = "messaging.presence";
    public const string Signal = "messaging.signal";
    public const string PushDispatch = "messaging.push.dispatch";
}
