using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class MessagingContractsTests
{
    [Theory]
    [InlineData(ConversationSecurityProfile.PrivateE2EE, "\"private-e2-ee\"")]
    [InlineData(ConversationSecurityProfile.AgentE2EE, "\"agent-e2-ee\"")]
    [InlineData(ConversationSecurityProfile.Governed, "\"governed\"")]
    public void ConversationSecurityProfile_SerializesAsKebabCase(ConversationSecurityProfile profile, string expectedJson)
    {
        var json = JsonSerializer.Serialize(profile);
        Assert.Equal(expectedJson, json);
        Assert.Equal(profile, JsonSerializer.Deserialize<ConversationSecurityProfile>(json));
    }

    [Fact]
    public void MessagingEnums_RejectNumericValuesOnDeserialization()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ConversationSecurityProfile>("0"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnrollmentState>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CallState>("2"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FollowUpState>("3"));
    }

    [Fact]
    public void MessagingIdentifiers_PreserveUlidValueEquality()
    {
        var u = Ulid.NewUlid();
        Assert.Equal(new ConversationId(u), new ConversationId(u));
        Assert.Equal(new MessageId(u), new MessageId(u));
        Assert.Equal(new DeviceId(u), new DeviceId(u));
        Assert.Equal(new CallId(u), new CallId(u));
        Assert.Equal(new EnrollmentId(u), new EnrollmentId(u));
        Assert.Equal(new FollowUpId(u), new FollowUpId(u));
        Assert.Equal(new ContinuationId(u), new ContinuationId(u));
        Assert.Equal(new AttachmentId(u), new AttachmentId(u));
        Assert.Equal(new KeyPackageId(u), new KeyPackageId(u));
        Assert.Equal(new RefreshGrantId(u), new RefreshGrantId(u));
        Assert.Equal(new PushRegistrationId(u), new PushRegistrationId(u));
        Assert.Equal(new ApprovalRequestId(u), new ApprovalRequestId(u));
    }

    [Fact]
    public void MessagingConstants_MatchCanonicalWireNames()
    {
        Assert.Equal("messaging.message.committed", MessagingEventTypes.MessageCommitted);
        Assert.Equal("messaging.call.invited", MessagingEventTypes.CallInvited);
        Assert.Equal("messaging.device.revoked", MessagingEventTypes.DeviceRevoked);
        Assert.Equal("messaging.inapp.send", MessagingCapabilityIds.InAppSend);
        Assert.Equal("messaging.e2ee", MessagingCapabilityIds.E2EE);
        Assert.Equal("messaging.route", MessagingWorkerCommandKinds.Route);
        Assert.Equal("human_participant_required", ErrorCodes.HumanParticipantRequired);
        Assert.Equal("media_encryption_required", ErrorCodes.MediaEncryptionRequired);
        Assert.Equal("token_reuse_detected", ErrorCodes.TokenReuseDetected);
    }

    [Fact]
    public void MessagingDomainException_CarriesErrorCodeAndStatusCode()
    {
        var ex = new MessagingDomainException(ErrorCodes.PrivateE2EEAgentForbidden, "Denied", 409);
        Assert.Equal(ErrorCodes.PrivateE2EEAgentForbidden, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("Denied", ex.Message);
    }
}
