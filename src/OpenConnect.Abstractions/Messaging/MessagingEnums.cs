using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions.Messaging;

[JsonConverter(typeof(KebabCaseStringEnumConverter<ConversationSecurityProfile>))]
public enum ConversationSecurityProfile
{
    PrivateE2EE,
    AgentE2EE,
    Governed
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<ConversationState>))]
public enum ConversationState
{
    Active,
    Closed,
    Archived
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<ParticipantKind>))]
public enum ParticipantKind
{
    Human,
    Agent
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<ParticipantRole>))]
public enum ParticipantRole
{
    Owner,
    Member,
    Guest,
    OperationalAgent
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<ParticipantStatus>))]
public enum ParticipantStatus
{
    Active,
    Left,
    Removed,
    Revoked
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<MessageReceiptKind>))]
public enum MessageReceiptKind
{
    Queued,
    Committed,
    Delivered,
    Read,
    Failed
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<DeviceStatus>))]
public enum DeviceStatus
{
    PendingVerification,
    Verified,
    Revoked
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<DevicePlatform>))]
public enum DevicePlatform
{
    Web,
    Desktop,
    Android,
    Ios,
    AgentEndpoint,
    Other
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<EnrollmentState>))]
public enum EnrollmentState
{
    Pending,
    Claimed,
    Approved,
    Consumed,
    Expired,
    Cancelled,
    Denied
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<RefreshGrantStatus>))]
public enum RefreshGrantStatus
{
    Active,
    Rotated,
    Revoked,
    ReuseDetected
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<CallState>))]
public enum CallState
{
    Invited,
    Ringing,
    Accepted,
    Connecting,
    Connected,
    Ended,
    Declined,
    Missed,
    Cancelled,
    Failed
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<CallMediaSecurityMode>))]
public enum CallMediaSecurityMode
{
    TransportOnly,
    EndpointE2EE
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<FollowUpState>))]
public enum FollowUpState
{
    Scheduled,
    Claimed,
    Authorized,
    Dispatched,
    Waiting,
    ResponseReceived,
    TimedOut,
    ContinuationQueued,
    Completed,
    Rescheduled,
    Escalated,
    Cancelled,
    Expired
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<ContinuationStatus>))]
public enum ContinuationStatus
{
    Queued,
    Claimed,
    Running,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<PushProviderKind>))]
public enum PushProviderKind
{
    Fcm,
    WebPush
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<PushRegistrationStatus>))]
public enum PushRegistrationStatus
{
    Active,
    InvalidToken,
    Revoked
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<AttachmentState>))]
public enum AttachmentState
{
    Reserved,
    Committed,
    Deleted,
    Expired
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<MessagingChannelKind>))]
public enum MessagingChannelKind
{
    InApp,
    WhatsAppPlanned,
    TelegramPlanned,
    DiscordPlanned
}

[JsonConverter(typeof(KebabCaseStringEnumConverter<MessagingPermission>))]
public enum MessagingPermission
{
    Discover,
    Receive,
    Send,
    Initiate,
    Invite,
    Call,
    FollowUp,
    Attachment,
    Approval
}
