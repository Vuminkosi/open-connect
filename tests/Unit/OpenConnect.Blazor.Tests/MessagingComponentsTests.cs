using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Client.Messaging;
using OpenConnect.Components.Blazor.Messaging;
using OpenConnect.Components.Contracts.Messaging;
using Xunit;

namespace OpenConnect.Blazor.Tests;

public sealed class MessagingComponentsTests : OpenConnectBunitContext
{
    public MessagingComponentsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddOpenConnectMudServices();
        Services.AddOpenConnectMessagingClient();
        this.AddPopoverHost();
    }

    [Fact]
    public void MessagingInbox_RendersHonestSecurityProfiles_UnreadBadge_AndOfflineBanner()
    {
        var conversations = new[]
        {
            new MessagingConversationSummary(
                Ulid.NewUlid().ToString(),
                "Claims Desk",
                MessagingSecurityProfileKind.AgentE2EE,
                MessagingConversationLifecycleState.Active,
                UnreadCount: 3,
                IncludesOperationalAgent: true,
                LastMessagePreview: "Policy review ready",
                LastMessageAtUtc: DateTimeOffset.UtcNow),
            new MessagingConversationSummary(
                Ulid.NewUlid().ToString(),
                "Compliance Archive",
                MessagingSecurityProfileKind.Governed,
                MessagingConversationLifecycleState.Active,
                UnreadCount: 0,
                IncludesOperationalAgent: false,
                LastMessagePreview: "Audit snapshot",
                LastMessageAtUtc: DateTimeOffset.UtcNow)
        };

        var cut = Render<MessagingInbox>(p => p
            .Add(c => c.Conversations, conversations)
            .Add(c => c.ConnectionState, MessagingConnectionState.Offline));

        Assert.Contains("Offline", cut.Markup);
        Assert.Contains("AgentE2EE", cut.Markup);
        Assert.Contains("Governed (not E2EE)", cut.Markup);
        Assert.Contains("3 unread messages", cut.Markup);
    }

    [Fact]
    public void IncomingCallDialog_RequiresExplicitConsent_AndDisplaysTruthfulMediaSecurity()
    {
        string? acceptedId = null;
        var callId = Ulid.NewUlid().ToString();
        var invitation = new MessagingCallInvitation(
            callId,
            Ulid.NewUlid().ToString(),
            "Claims Operational Agent",
            IsAgentCaller: true,
            "Policy #901",
            MessagingCallSecurityKind.TransportOnly,
            DateTimeOffset.UtcNow.AddSeconds(45));

        var cut = Render<IncomingCallDialog>(p => p
            .Add(c => c.Invitation, invitation)
            .Add(c => c.OnAccept, id => acceptedId = id));

        // Never auto-answers on render.
        Assert.Null(acceptedId);
        Assert.Contains("Transport-encrypted audio (TLS/WSS, not E2EE)", cut.Markup);
        Assert.Contains("Microphone will only activate after you accept.", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Contains("Accept call")).Click();
        Assert.Equal(callId, acceptedId);
    }

    [Fact]
    public void PairingDialog_ShowsVerificationCode_Countdown_AndRequiresExplicitApproval()
    {
        string? approvedId = null;
        var enrollmentId = Ulid.NewUlid().ToString();
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var session = new MessagingPairingSession(
            enrollmentId,
            "Alpha Insurance",
            "Claims CRM",
            "{\"version\":1}",
            "482-910",
            MessagingPairingState.Claimed,
            now.AddSeconds(95),
            ClaimedDeviceName: "Pixel 9 Pro",
            ClaimedDevicePlatform: "Android");

        var cut = Render<PairingDialog>(p => p
            .Add(c => c.Session, session)
            .Add(c => c.UtcNow, now)
            .Add(c => c.OnConfirmApproval, id => approvedId = id));

        Assert.Contains("482-910", cut.Markup);
        Assert.Contains("Expires in 95s", cut.Markup);
        Assert.Contains("Pixel 9 Pro", cut.Markup);
        Assert.Null(approvedId);

        cut.FindAll("button").First(b => b.TextContent.Contains("Approve device")).Click();
        Assert.Equal(enrollmentId, approvedId);
    }

    [Fact]
    public void ApprovalRequestCard_DisablesActionsWhenExpired()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 5, 0, TimeSpan.Zero);
        var request = new MessagingApprovalRequest(
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid().ToString(),
            "Issue claim payout",
            "Approve payout of $1,250.00 for claim #4821.",
            "Claims Agent",
            ExpiresAtUtc: now.AddSeconds(-10),
            ResourceScope: "claim:4821");

        var cut = Render<ApprovalRequestCard>(p => p
            .Add(c => c.Request, request)
            .Add(c => c.UtcNow, now));

        Assert.Contains("Expired", cut.Markup);
        Assert.All(cut.FindAll("button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Fact]
    public async Task MessagingProvider_FencesCallbacksByProfileEpoch()
    {
        var manager = Services.GetRequiredService<IMessagingProfileManager>();
        var identityA = MessagingProfileIdentity.Create(
            new Uri("https://alpha.example/"),
            new TenantId(Ulid.NewUlid()),
            "app-a",
            "https://issuer.example",
            "sub-1",
            new DeviceId(Ulid.NewUlid()));
        var creds = new MessagingProfileCredentials(
            "tok", DateTimeOffset.UtcNow.AddMinutes(10),
            "ref", DateTimeOffset.UtcNow.AddDays(1),
            new RefreshGrantId(Ulid.NewUlid()), "fam", identityA.DeviceId);

        await manager.AddOrUpdateProfileAsync(
            new MessagingProfileRecord(identityA, "Alpha", "App A", MessagingProfileState.Active),
            creds,
            activate: true);

        var cut = Render<MessagingProvider>();
        var provider = cut.Instance;
        var epochA = provider.BoundProfileEpoch;

        var evt = new MessagingEventEnvelope(
            1, "evt-1", MessagingEventTypes.MessageCommitted,
            identityA.TenantId, new ConversationId(Ulid.NewUlid()),
            1, "cur-1", DateTimeOffset.UtcNow, "{}");

        Assert.True(provider.TryApplyEvent(epochA, identityA.ProfileId, evt));
        Assert.False(provider.TryApplyEvent(epochA, identityA.ProfileId, evt));

        await manager.DeactivateCurrentProfileAsync();
        var lateEvt = evt with { EventId = "evt-2", Sequence = 2 };
        Assert.False(provider.TryApplyEvent(epochA, identityA.ProfileId, lateEvt));
    }
}
