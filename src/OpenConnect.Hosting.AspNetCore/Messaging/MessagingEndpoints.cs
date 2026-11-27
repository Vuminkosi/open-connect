using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Abstractions.Serialization;

namespace OpenConnect.Hosting.AspNetCore.Messaging;

internal static class MessagingEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = OpenConnectHttpJson.CreateOptions();

    internal static void MapRoutes(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/openconnect/v1");

        // Compatibility & Agent Discovery
        group.MapGet("/messaging/compatibility", HandleCompatibilityAsync).RequireAuthorization();
        group.MapGet("/agents/discover", HandleDiscoverAgentsAsync).RequireAuthorization();

        // Conversations & Participants
        group.MapPost("/messaging/conversations", HandleCreateConversationAsync).RequireAuthorization();
        group.MapGet("/messaging/conversations", HandleListConversationsAsync).RequireAuthorization();
        group.MapGet("/messaging/conversations/{id}", HandleGetConversationAsync).RequireAuthorization();
        group.MapPost("/messaging/conversations/{id}/participants", HandleAddParticipantAsync).RequireAuthorization();
        group.MapDelete("/messaging/conversations/{id}/participants/{participantId}", HandleRemoveParticipantAsync).RequireAuthorization();

        // Messages & Receipts
        group.MapPost("/messaging/conversations/{id}/messages", HandleCommitMessageAsync).RequireAuthorization();
        group.MapGet("/messaging/conversations/{id}/messages", HandleListMessagesAsync).RequireAuthorization();
        group.MapPost("/messaging/messages/{id}/receipts", HandleRecordReceiptAsync).RequireAuthorization();

        // Key Packages
        group.MapPost("/messaging/keys/packages", HandlePublishKeyPackageAsync).RequireAuthorization();
        group.MapGet("/messaging/keys/packages", HandleConsumeKeyPackageAsync).RequireAuthorization();

        // Encrypted Attachments
        group.MapPost("/messaging/attachments/init", HandleInitAttachmentAsync).RequireAuthorization();
        group.MapPost("/messaging/attachments/complete", HandleCompleteAttachmentAsync).RequireAuthorization();
        group.MapGet("/messaging/attachments/{id}", HandleDownloadAttachmentAsync).RequireAuthorization();

        // Voice / Media Calls (wraps existing duplex voice admission; never auto-answer; enforces E2EE gate)
        group.MapPost("/messaging/calls", HandleCreateCallAsync).RequireAuthorization();
        group.MapPost("/messaging/calls/{id}/accept", HandleAcceptCallAsync).RequireAuthorization();
        group.MapPost("/messaging/calls/{id}/decline", HandleDeclineCallAsync).RequireAuthorization();
        group.MapPost("/messaging/calls/{id}/end", HandleEndCallAsync).RequireAuthorization();

        // Durable Follow-Ups
        group.MapPost("/messaging/followups", HandleScheduleFollowUpAsync).RequireAuthorization();
        group.MapGet("/messaging/followups", HandleListFollowUpsAsync).RequireAuthorization();
        group.MapDelete("/messaging/followups/{id}", HandleCancelFollowUpAsync).RequireAuthorization();

        // Bounded Opaque Scope-Bound Event Replay & Realtime WebSocket
        group.MapGet("/messaging/events", HandleReplayEventsAsync).RequireAuthorization();
        group.MapPost("/messaging/realtime/ticket", HandleIssueRealtimeTicketAsync).RequireAuthorization();
        group.Map("/messaging/realtime", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            await MessagingRealtimeWebSocketHandler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
        });

        // Device Enrollment, Refresh Rotation, Management & Push
        group.MapPost("/devices/enrollment", HandleInitiateEnrollmentAsync).RequireAuthorization();
        group.MapPost("/devices/enrollment/redeem", HandleRedeemEnrollmentAsync).AllowAnonymous();
        group.MapPost("/devices/enrollment/{id}/confirm", HandleConfirmEnrollmentAsync).RequireAuthorization();
        group.MapPost("/devices/enrollment/{id}/complete", HandleCompleteEnrollmentAsync).AllowAnonymous();
        group.MapPost("/devices/token/refresh", HandleRefreshTokenAsync).AllowAnonymous();
        group.MapGet("/devices", HandleListDevicesAsync).RequireAuthorization();
        group.MapDelete("/devices/{id}", HandleRevokeDeviceAsync).RequireAuthorization();
        group.MapPost("/devices/{id}/push", HandleRegisterPushAsync).RequireAuthorization();
        group.MapDelete("/devices/{id}/push", HandleRevokePushAsync).RequireAuthorization();
    }

    private static async Task<IResult> HandleCompatibilityAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var capabilities = new List<string>();
        var workerClient = context.RequestServices.GetService<IMessagingWorkerClient>();
        if (workerClient is not null)
        {
            try
            {
                var readiness = await workerClient.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
                if (readiness.IsReady)
                    capabilities.AddRange(readiness.VerifiedCapabilities);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Worker unreachable: report honest empty worker capability list rather than fabricating readiness.
            }
        }

        var supportedProfiles = options!.RequireEncryption && !options.AllowUnencryptedFallback
            ? new[] { ConversationSecurityProfile.PrivateE2EE, ConversationSecurityProfile.AgentE2EE }
            : new[] { ConversationSecurityProfile.PrivateE2EE, ConversationSecurityProfile.AgentE2EE, ConversationSecurityProfile.Governed };

        return JsonOk(new
        {
            contractVersion = "openconnect.messaging.v1",
            tenantId = tenantId.Value.ToString(),
            applicationId = options.ApplicationId,
            companyDisplayName = options.CompanyDisplayName,
            issuer = options.Issuer,
            requireEncryption = options.RequireEncryption,
            allowUnencryptedFallback = options.AllowUnencryptedFallback,
            supportedSecurityProfiles = supportedProfiles,
            verifiedCapabilities = capabilities
        });
    }

    private static async Task<IResult> HandleDiscoverAgentsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var authorizer = context.RequestServices.GetService<IAgentDiscoveryAuthorizer>();
        if (authorizer is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Agent discovery is not configured.");

        var limit = ParseBoundedInt(context.Request.Query["limit"].FirstOrDefault(), 50, 1, options!.MaxReplayPageSize);
        var offset = ParseBoundedInt(context.Request.Query["offset"].FirstOrDefault(), 0, 0, 10_000);

        return await ExecuteDomainAsync(context, async () =>
        {
            var all = await authorizer.GetAuthorizedAgentsAsync(tenantId, options.ApplicationId, principalId, cancellationToken).ConfigureAwait(false);
            var page = all.Skip(offset).Take(limit).ToArray();
            return JsonOk(new { items = page, total = all.Count, limit, offset });
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCreateConversationAsync(HttpContext context, CreateConversationHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryCheckBodyBounds(context, options!, out var bodyError))
            return bodyError!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (request is null)
            return BadRequest(context, ErrorCodes.InvalidRequest, "Request body is required.");

        var coordinator = context.RequestServices.GetService<IMessagingCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.MessagingDisabled, "Messaging coordinator is not configured.");

        if (options!.RequireEncryption && !options.AllowUnencryptedFallback && request.SecurityProfile == ConversationSecurityProfile.Governed)
            return Forbidden(context, ErrorCodes.MediaEncryptionRequired, "Unencrypted governed profile is disabled by host policy.");

        var creator = new MessagingActor(tenantId, options.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        var initialParticipants = new List<MessagingActor>();
        if (request.Participants is not null)
        {
            foreach (var p in request.Participants)
            {
                if (!TryParseParticipantActor(tenantId, options.ApplicationId, p, out var actor))
                    return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid participant descriptor.");
                initialParticipants.Add(actor!);
            }
        }

        return await ExecuteDomainAsync(context, async () =>
        {
            var created = await coordinator.CreateConversationAsync(creator, request.SecurityProfile, initialParticipants, cancellationToken).ConfigureAwait(false);
            return JsonResult(created, StatusCodes.Status201Created, $"/openconnect/v1/messaging/conversations/{created.ConversationId.Value}");
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleListConversationsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var replayStore = context.RequestServices.GetService<IMessagingEventReplayStore>();
        if (replayStore is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Conversation listing is not configured.");

        var limit = ParseBoundedInt(context.Request.Query["limit"].FirstOrDefault(), 50, 1, options!.MaxReplayPageSize);
        return await ExecuteDomainAsync(context, async () =>
        {
            var items = await replayStore.ListConversationsAsync(tenantId, principalId, limit, cancellationToken).ConfigureAwait(false);
            return JsonOk(new { items });
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleGetConversationAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var convUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid conversation ID.");

        var store = context.RequestServices.GetService<IMessagingConversationStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.MessagingDisabled, "Messaging conversation store is not configured.");

        var convId = new ConversationId(convUlid);
        var conversation = await store.GetConversationAsync(tenantId, convId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != tenantId ||
            !conversation.Participants.Any(p => p.Kind == ParticipantKind.Human && p.PrincipalId == principalId && p.Status == ParticipantStatus.Active))
        {
            // Non-disclosable 404 prevents cross-tenant / non-member enumeration.
            return NotFound(context, ErrorCodes.ConversationNotFound, "Conversation not found.");
        }

        var authorizer = context.RequestServices.GetService<IMessagingAuthorizer>();
        if (authorizer is not null)
        {
            var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
            var decision = await authorizer.AuthorizeAsync(actor, MessagingPermission.Receive, convId, null, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                return Forbidden(context, decision.ErrorCode ?? ErrorCodes.Unauthorized, decision.Reason ?? "Access denied.");
        }

        return JsonOk(conversation);
    }

    private static async Task<IResult> HandleAddParticipantAsync(string id, HttpContext context, AddParticipantHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var convUlid) || request is null)
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid conversation ID or participant payload.");

        var coordinator = context.RequestServices.GetService<IMessagingCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.MessagingDisabled, "Messaging coordinator is not configured.");

        if (!TryParseParticipantActor(tenantId, options!.ApplicationId, new ParticipantInput(request.Kind, request.PrincipalId, request.AgentId), out var target))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid participant target.");

        var actor = new MessagingActor(tenantId, options.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var updated = await coordinator.AddParticipantAsync(actor, new ConversationId(convUlid), target!, request.Role, cancellationToken).ConfigureAwait(false);
            return JsonOk(updated);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRemoveParticipantAsync(string id, string participantId, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var convUlid) || string.IsNullOrWhiteSpace(participantId))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid conversation or participant ID.");

        var coordinator = context.RequestServices.GetService<IMessagingCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.MessagingDisabled, "Messaging coordinator is not configured.");

        var kindQuery = context.Request.Query["kind"].FirstOrDefault();
        var isAgent = string.Equals(kindQuery, "agent", StringComparison.OrdinalIgnoreCase);
        PrincipalId? targetPrincipal = null;
        AgentId? targetAgent = null;
        if (isAgent)
        {
            targetAgent = new AgentId(participantId);
        }
        else
        {
            if (!TryParseUlid(participantId, out var pUlid))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid human participant ID.");
            targetPrincipal = new PrincipalId(pUlid);
        }

        var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var result = await coordinator.RemoveParticipantAsync(
                actor,
                new ConversationId(convUlid),
                isAgent ? ParticipantKind.Agent : ParticipantKind.Human,
                targetPrincipal,
                targetAgent,
                cancellationToken).ConfigureAwait(false);

            if (result.TerminateActiveCalls)
            {
                var callCoordinator = context.RequestServices.GetService<IMessagingCallCoordinator>();
                if (callCoordinator is not null)
                {
                    await callCoordinator.TerminateCallsForConversationAsync(tenantId, new ConversationId(convUlid), "last_human_removed", cancellationToken).ConfigureAwait(false);
                }
            }

            return JsonOk(result);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCommitMessageAsync(string id, HttpContext context, CommitMessageHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryCheckBodyBounds(context, options!, out var bodyError))
            return bodyError!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var convUlid) || request is null || request.Envelope is null || string.IsNullOrWhiteSpace(request.ClientOperationId))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Conversation ID, encrypted envelope, and clientOperationId are required.");
        if (request.ClientOperationId.Length > 128)
            return BadRequest(context, ErrorCodes.InvalidRequest, "clientOperationId exceeds maximum length.");

        var coordinator = context.RequestServices.GetService<IMessagingCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.MessagingDisabled, "Messaging coordinator is not configured.");

        var sender = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var committed = await coordinator.CommitMessageAsync(
                sender,
                new ConversationId(convUlid),
                request.Envelope,
                request.ClientOperationId,
                request.CorrelationId,
                cancellationToken).ConfigureAwait(false);

            var status = committed.WasDeduplicated ? StatusCodes.Status200OK : StatusCodes.Status201Created;
            return JsonResult(committed, status);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleListMessagesAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var convUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid conversation ID.");

        var store = context.RequestServices.GetService<IMessagingConversationStore>();
        var replayStore = context.RequestServices.GetService<IMessagingEventReplayStore>();
        if (store is null || replayStore is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Message history replay is not configured.");

        var convId = new ConversationId(convUlid);
        var conversation = await store.GetConversationAsync(tenantId, convId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != tenantId ||
            !conversation.Participants.Any(p => p.Kind == ParticipantKind.Human && p.PrincipalId == principalId && p.Status == ParticipantStatus.Active))
            return NotFound(context, ErrorCodes.ConversationNotFound, "Conversation not found.");

        long afterSequence = 0;
        var cursor = context.Request.Query["cursor"].FirstOrDefault();
        var codec = context.RequestServices.GetService<MessagingCursorCodec>() ?? new MessagingCursorCodec(Options.Create(options!));
        var scope = $"conv:{convId.Value}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!codec.TryDecode(cursor, tenantId, principalId, deviceId, scope, out afterSequence))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid or out-of-scope cursor.");
        }

        var limit = ParseBoundedInt(context.Request.Query["limit"].FirstOrDefault(), 50, 1, options!.MaxReplayPageSize);
        var messages = await replayStore.ListMessagesAsync(tenantId, convId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        var nextCursor = messages.Count > 0
            ? codec.Encode(tenantId, principalId, deviceId, scope, messages[^1].Sequence)
            : cursor;

        return JsonOk(new { items = messages, nextCursor, hasMore = messages.Count == limit });
    }

    private static async Task<IResult> HandleRecordReceiptAsync(string id, HttpContext context, RecordReceiptHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var messageUlid) || request is null || !TryParseUlid(request.ConversationId, out var convUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid message ID and conversationId are required.");

        var coordinator = context.RequestServices.GetService<IMessagingCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.MessagingDisabled, "Messaging coordinator is not configured.");

        var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var receipt = await coordinator.RecordReceiptAsync(
                actor,
                new ConversationId(convUlid),
                new MessageId(messageUlid),
                request.Kind,
                request.FailureReason,
                cancellationToken).ConfigureAwait(false);
            return JsonOk(receipt);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandlePublishKeyPackageAsync(HttpContext context, PublishKeyPackageHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var claimDeviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (request is null || string.IsNullOrWhiteSpace(request.PublicPackageBase64) || string.IsNullOrWhiteSpace(request.Protocol))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Protocol and publicPackageBase64 are required.");

        var store = context.RequestServices.GetService<IMessagingKeyPackageStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Key package store is not configured.");

        DeviceId deviceId;
        if (!string.IsNullOrWhiteSpace(request.DeviceId))
        {
            if (!TryParseUlid(request.DeviceId, out var parsedDev))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid deviceId.");
            if (claimDeviceId.HasValue && claimDeviceId.Value.Value != parsedDev)
                return Forbidden(context, ErrorCodes.Unauthorized, "Device mismatch.");
            deviceId = new DeviceId(parsedDev);
        }
        else if (claimDeviceId.HasValue)
        {
            deviceId = claimDeviceId.Value;
        }
        else
        {
            return BadRequest(context, ErrorCodes.InvalidRequest, "deviceId is required.");
        }

        var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var now = timeProvider.GetUtcNow();
        var expiresAt = request.ExpiresAt ?? now.AddDays(7);
        if (expiresAt <= now)
            return BadRequest(context, ErrorCodes.InvalidRequest, "Key package expiry must be in the future.");

        var pkg = new DeviceKeyPackage(
            tenantId,
            new KeyPackageId(Ulid.NewUlid()),
            principalId,
            null,
            deviceId,
            request.Protocol.Trim(),
            request.PublicPackageBase64,
            now,
            expiresAt);

        return await ExecuteDomainAsync(context, async () =>
        {
            var saved = await store.PublishAsync(pkg, cancellationToken).ConfigureAwait(false);
            return JsonResult(saved, StatusCodes.Status201Created);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleConsumeKeyPackageAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var callerPrincipal, out var callerDevice))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        // Consumption is state-changing; intermediaries and clients must never cache any response.
        context.Response.Headers.CacheControl = "no-store";

        var store = context.RequestServices.GetService<IMessagingKeyPackageStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Key package store is not configured.");

        PrincipalId? ownerPrincipal = null;
        AgentId? ownerAgent = null;
        DeviceId? targetDevice = null;

        var principalQuery = context.Request.Query["principalId"].FirstOrDefault();
        var agentQuery = context.Request.Query["agentId"].FirstOrDefault();
        var deviceQuery = context.Request.Query["deviceId"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(principalQuery))
        {
            if (!TryParseUlid(principalQuery, out var pUlid))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid principalId.");
            ownerPrincipal = new PrincipalId(pUlid);
        }
        if (!string.IsNullOrWhiteSpace(agentQuery))
            ownerAgent = new AgentId(agentQuery);
        if (!string.IsNullOrWhiteSpace(deviceQuery))
        {
            if (!TryParseUlid(deviceQuery, out var dUlid))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid deviceId.");
            targetDevice = new DeviceId(dUlid);
        }

        if (ownerPrincipal is null && ownerAgent is null && targetDevice is null)
            return BadRequest(context, ErrorCodes.InvalidRequest, "At least one of principalId, agentId, or deviceId is required.");

        const string deniedMessage = "Key package consumption is not permitted for the requested target.";

        // The owner of a package must be identifiable to authorize consumption; device-only lookups fail closed.
        if (ownerPrincipal is null && ownerAgent is null)
            return Forbidden(context, ErrorCodes.Unauthorized, deniedMessage);

        var limiter = context.RequestServices.GetService<KeyPackageConsumeLimiter>();
        if (limiter is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Key package consumption limiter is not configured.");

        // Every attempt, including denied ones, counts against the caller so authorization probing is bounded.
        if (!limiter.TryAcquire($"caller:{tenantId.Value}:{callerPrincipal.Value}", options!.KeyPackageConsumeLimitPerCaller, options.KeyPackageConsumeWindow, out var callerRetry))
            return TooManyRequests(context, callerRetry);

        var applicationId = options.ApplicationId;
        var targets = new List<MessagingActor>(2);
        if (ownerAgent is not null)
            targets.Add(new MessagingActor(tenantId, applicationId, ParticipantKind.Agent, null, ownerAgent));
        if (ownerPrincipal is not null && ownerPrincipal.Value != callerPrincipal)
            targets.Add(new MessagingActor(tenantId, applicationId, ParticipantKind.Human, ownerPrincipal));

        if (targets.Count > 0)
        {
            var authorizer = context.RequestServices.GetService<IMessagingAuthorizer>();
            if (authorizer is null)
                return Forbidden(context, ErrorCodes.Unauthorized, deniedMessage);

            var caller = new MessagingActor(tenantId, applicationId, ParticipantKind.Human, callerPrincipal, null, callerDevice);
            foreach (var target in targets)
            {
                MessagingAuthorizationDecision decision;
                try
                {
                    decision = await authorizer.AuthorizeAsync(caller, MessagingPermission.Initiate, null, target, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail closed without disclosing the host authorizer's fault.
                    context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("OpenConnect.Hosting.Messaging")
                        .LogError(ex, "Messaging authorizer failed while authorizing key package consumption.");
                    return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Key package consumption could not be authorized.");
                }
                if (!decision.Allowed)
                    return Forbidden(context, ErrorCodes.Unauthorized, deniedMessage);
            }
        }

        // Every named owner counts against its own quota.
        if (ownerAgent is not null &&
            !limiter.TryAcquire($"target:{tenantId.Value}:agent:{ownerAgent.Value.Value}", options.KeyPackageConsumeLimitPerTarget, options.KeyPackageConsumeWindow, out var agentRetry))
            return TooManyRequests(context, agentRetry);
        if (ownerPrincipal is not null &&
            !limiter.TryAcquire($"target:{tenantId.Value}:principal:{ownerPrincipal.Value.Value}", options.KeyPackageConsumeLimitPerTarget, options.KeyPackageConsumeWindow, out var principalRetry))
            return TooManyRequests(context, principalRetry);

        return await ExecuteDomainAsync(context, async () =>
        {
            var pkg = await store.ConsumeOneAsync(tenantId, ownerPrincipal, ownerAgent, targetDevice, cancellationToken).ConfigureAwait(false);
            return pkg is null
                ? NotFound(context, ErrorCodes.ConversationNotFound, "No available key package found.")
                : JsonOk(pkg);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleInitAttachmentAsync(HttpContext context, InitAttachmentHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (request is null || !TryParseUlid(request.ConversationId, out var convUlid) ||
            request.EncryptedSizeBytes <= 0 || string.IsNullOrWhiteSpace(request.EncryptedDigestSha256))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid conversationId, encryptedSizeBytes, and encryptedDigestSha256 are required.");

        if (request.EncryptedSizeBytes > options!.MaxAttachmentBytes)
            return Problem(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.InvalidRequest, "Encrypted attachment exceeds maximum allowed size.");

        var store = context.RequestServices.GetService<IMessagingAttachmentStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Attachment storage is not configured.");

        var authorizer = context.RequestServices.GetService<IMessagingAuthorizer>();
        var convId = new ConversationId(convUlid);
        if (authorizer is not null)
        {
            var actor = new MessagingActor(tenantId, options.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
            var decision = await authorizer.AuthorizeAsync(actor, MessagingPermission.Attachment, convId, null, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                return Forbidden(context, decision.ErrorCode ?? ErrorCodes.Unauthorized, decision.Reason ?? "Attachment upload denied.");
        }

        return await ExecuteDomainAsync(context, async () =>
        {
            var record = await store.InitUploadAsync(tenantId, convId, request.EncryptedSizeBytes, request.EncryptedDigestSha256, TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
            return JsonResult(record, StatusCodes.Status201Created);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCompleteAttachmentAsync(HttpContext context, CompleteAttachmentHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out _, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (request is null || !TryParseUlid(request.AttachmentId, out var attUlid) || string.IsNullOrWhiteSpace(request.EncryptedPayloadBase64))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid attachmentId and encryptedPayloadBase64 are required.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.EncryptedPayloadBase64); }
        catch (FormatException) { return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid base64 payload."); }

        if (bytes.Length == 0 || bytes.Length > options!.MaxAttachmentBytes)
            return Problem(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.InvalidRequest, "Encrypted attachment exceeds maximum allowed size.");

        var store = context.RequestServices.GetService<IMessagingAttachmentStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Attachment storage is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var committed = await store.CompleteUploadAsync(tenantId, new AttachmentId(attUlid), bytes, cancellationToken).ConfigureAwait(false);
            return JsonOk(committed);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDownloadAttachmentAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var attUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid attachment ID.");

        var store = context.RequestServices.GetService<IMessagingAttachmentStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Attachment storage is not configured.");

        var download = await store.DownloadAsync(tenantId, new AttachmentId(attUlid), cancellationToken).ConfigureAwait(false);
        if (download is null || download.Metadata.TenantId != tenantId)
            return NotFound(context, ErrorCodes.ConversationNotFound, "Attachment not found.");

        var authorizer = context.RequestServices.GetService<IMessagingAuthorizer>();
        if (authorizer is not null)
        {
            var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
            var decision = await authorizer.AuthorizeAsync(actor, MessagingPermission.Attachment, download.Metadata.ConversationId, null, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                return Forbidden(context, decision.ErrorCode ?? ErrorCodes.Unauthorized, decision.Reason ?? "Attachment download denied.");
        }

        context.Response.Headers["X-OpenConnect-Attachment-Digest"] = download.Metadata.EncryptedDigestSha256;
        return Results.File(download.EncryptedBytes.ToArray(), "application/octet-stream");
    }

    private static async Task<IResult> HandleCreateCallAsync(HttpContext context, CreateCallHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (request is null || !TryParseUlid(request.ConversationId, out var convUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid conversationId is required.");

        var callCoordinator = context.RequestServices.GetService<IMessagingCallCoordinator>();
        if (callCoordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Call coordinator is not configured.");

        // E2EE media security gate: never allow transport-only calls when RequireEncryption is on and fallback is disabled.
        var requireEncryption = options!.RequireEncryption && !options.AllowUnencryptedFallback;
        if (requireEncryption && request.MediaSecurityMode != CallMediaSecurityMode.EndpointE2EE)
            return Forbidden(context, ErrorCodes.MediaEncryptionRequired, "Endpoint E2EE media security is required by host policy.");

        var caller = new MessagingActor(tenantId, options.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var created = await callCoordinator.CreateCallAsync(
                caller,
                new ConversationId(convUlid),
                request.MediaSecurityMode,
                requireEncryption,
                request.RingTimeoutSeconds is > 0 and <= 120 ? TimeSpan.FromSeconds(request.RingTimeoutSeconds.Value) : null,
                cancellationToken).ConfigureAwait(false);
            return JsonResult(created, StatusCodes.Status201Created);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleAcceptCallAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var callUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid call ID.");

        var callCoordinator = context.RequestServices.GetService<IMessagingCallCoordinator>();
        var admissionBroker = context.RequestServices.GetService<IMessagingVoiceAdmissionBroker>();
        if (callCoordinator is null || admissionBroker is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Call admission is not configured.");

        var effectiveDevice = deviceId ?? new DeviceId(Ulid.NewUlid());
        var callId = new CallId(callUlid);

        return await ExecuteDomainAsync(context, async () =>
        {
            var admission = await admissionBroker.IssueAdmissionAsync(tenantId, principalId, effectiveDevice, callId, cancellationToken).ConfigureAwait(false);
            var acceptor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, effectiveDevice);
            var accepted = await callCoordinator.AcceptCallAsync(acceptor, callId, admission.VoiceSessionId, admission.AdmissionTokenHash, cancellationToken).ConfigureAwait(false);
            return JsonOk(new
            {
                call = accepted,
                voiceSessionId = admission.VoiceSessionId.Value.ToString(),
                admissionToken = admission.AdmissionToken,
                admissionExpiresAt = admission.ExpiresAtUtc
            });
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDeclineCallAsync(string id, HttpContext context, CallReasonHttpRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var callUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid call ID.");

        var callCoordinator = context.RequestServices.GetService<IMessagingCallCoordinator>();
        if (callCoordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Call coordinator is not configured.");

        var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var declined = await callCoordinator.DeclineCallAsync(actor, new CallId(callUlid), request?.Reason, cancellationToken).ConfigureAwait(false);
            return JsonOk(declined);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleEndCallAsync(string id, HttpContext context, CallReasonHttpRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var callUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid call ID.");

        var callCoordinator = context.RequestServices.GetService<IMessagingCallCoordinator>();
        if (callCoordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Call coordinator is not configured.");

        var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Human, principalId, null, deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var ended = await callCoordinator.EndCallAsync(actor, new CallId(callUlid), request?.Reason, cancellationToken).ConfigureAwait(false);
            return JsonOk(ended);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleScheduleFollowUpAsync(HttpContext context, ScheduleFollowUpHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (request is null || !TryParseUlid(request.ConversationId, out var convUlid) ||
            !TryParseUlid(request.RecipientPrincipalId, out var recipientUlid) ||
            string.IsNullOrWhiteSpace(request.AgentId) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(request.CausalEventId))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid conversationId, recipientPrincipalId, agentId, idempotencyKey, and causalEventId are required.");

        var coordinator = context.RequestServices.GetService<IFollowUpSchedulerCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Follow-up scheduler is not configured.");

        TaskId? taskId = null;
        if (!string.IsNullOrWhiteSpace(request.TaskId))
        {
            if (!TryParseUlid(request.TaskId, out var parsedTask))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid taskId.");
            taskId = new TaskId(parsedTask);
        }

        var actor = new MessagingActor(tenantId, options!.ApplicationId, ParticipantKind.Agent, principalId, new AgentId(request.AgentId), deviceId);
        return await ExecuteDomainAsync(context, async () =>
        {
            var created = await coordinator.ScheduleFollowUpAsync(
                actor,
                new ConversationId(convUlid),
                new PrincipalId(recipientUlid),
                request.DueAtUtc,
                request.MaxAttempts > 0 ? request.MaxAttempts : 3,
                request.IdempotencyKey,
                request.CausalEventId,
                taskId,
                request.QuietHoursStartUtc,
                request.QuietHoursEndUtc,
                cancellationToken).ConfigureAwait(false);
            return JsonResult(created, StatusCodes.Status201Created);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleListFollowUpsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out _, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var store = context.RequestServices.GetService<IFollowUpStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Follow-up store is not configured.");

        var convQuery = context.Request.Query["conversationId"].FirstOrDefault();
        if (!TryParseUlid(convQuery, out var convUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid conversationId query parameter is required.");

        var items = await store.GetActiveFollowUpsForConversationAsync(tenantId, new ConversationId(convUlid), principalId, cancellationToken).ConfigureAwait(false);
        return JsonOk(new { items });
    }

    private static async Task<IResult> HandleCancelFollowUpAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out _, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out _, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var followUpUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid follow-up ID.");

        var coordinator = context.RequestServices.GetService<IFollowUpSchedulerCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Follow-up scheduler is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var cancelled = await coordinator.CancelFollowUpAsync(tenantId, new FollowUpId(followUpUlid), cancellationToken).ConfigureAwait(false);
            return JsonOk(cancelled);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleReplayEventsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var replayStore = context.RequestServices.GetService<IMessagingEventReplayStore>();
        if (replayStore is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Event replay is not configured.");

        var codec = context.RequestServices.GetService<MessagingCursorCodec>() ?? new MessagingCursorCodec(Options.Create(options!));
        long afterSequence = 0;
        var cursor = context.Request.Query["cursor"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!codec.TryDecode(cursor, tenantId, principalId, deviceId, "events", out afterSequence))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid or out-of-scope replay cursor.");
        }

        var limit = ParseBoundedInt(context.Request.Query["limit"].FirstOrDefault(), 50, 1, options!.MaxReplayPageSize);
        var events = await replayStore.ReplayEventsAsync(tenantId, principalId, deviceId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        var nextCursor = events.Count > 0
            ? codec.Encode(tenantId, principalId, deviceId, "events", events[^1].Sequence)
            : cursor;

        return JsonOk(new MessagingEventPage(events, nextCursor, events.Count == limit));
    }

    private static async Task<IResult> HandleIssueRealtimeTicketAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out var deviceId))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var ticketService = context.RequestServices.GetService<IMessagingWebSocketTicketService>();
        if (ticketService is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "WebSocket ticket service is not configured.");

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!MessagingRealtimeWebSocketHandler.IsOriginAllowed(context, options!, origin))
            return Forbidden(context, ErrorCodes.Unauthorized, "Origin is not allowed.");

        var issued = await ticketService.IssueAsync(tenantId, options!.ApplicationId, principalId, deviceId, origin, cancellationToken).ConfigureAwait(false);
        return JsonResult(new
        {
            ticket = issued.Ticket,
            expiresAt = issued.ExpiresAtUtc
        }, StatusCodes.Status201Created);
    }

    private static async Task<IResult> HandleInitiateEnrollmentAsync(HttpContext context, InitiateEnrollmentHttpRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var coordinator = context.RequestServices.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device enrollment is not configured.");

        Uri baseGatewayUrl;
        if (!string.IsNullOrWhiteSpace(request?.BaseGatewayUrl))
        {
            if (!Uri.TryCreate(request.BaseGatewayUrl, UriKind.Absolute, out baseGatewayUrl!))
                return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid baseGatewayUrl.");
        }
        else
        {
            var host = context.Request.Host.HasValue ? context.Request.Host.Value : "localhost";
            baseGatewayUrl = new Uri($"{context.Request.Scheme}://{host}/");
        }

        var issuer = context.User.FindFirstValue("iss") ?? options!.Issuer;
        var subject = context.User.FindFirstValue("sub") ?? principalId.Value.ToString();
        TimeSpan? expiry = request?.ExpirySeconds is > 0 and <= 600 ? TimeSpan.FromSeconds(request.ExpirySeconds.Value) : null;

        return await ExecuteDomainAsync(context, async () =>
        {
            var created = await coordinator.InitiateEnrollmentAsync(
                tenantId,
                options!.ApplicationId,
                principalId,
                issuer,
                subject,
                baseGatewayUrl,
                expiry,
                cancellationToken).ConfigureAwait(false);
            return JsonResult(new
            {
                enrollmentId = created.Record.EnrollmentId.Value.ToString(),
                verificationCode = created.Record.VerificationCode,
                state = created.Record.State,
                expiresAt = created.Record.ExpiresAt,
                qrPayload = created.QrPayload
            }, StatusCodes.Status201Created);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRedeemEnrollmentAsync(HttpContext context, RedeemEnrollmentHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out _, out var disabledResult))
            return disabledResult!;
        if (request is null || string.IsNullOrWhiteSpace(request.EnrollmentToken) ||
            !TryParseUlid(request.DeviceId, out var deviceUlid) ||
            string.IsNullOrWhiteSpace(request.DevicePublicKeyBase64) ||
            string.IsNullOrWhiteSpace(request.DeviceDisplayName) ||
            string.IsNullOrWhiteSpace(request.ChallengeProofSignatureBase64))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Enrollment token, deviceId, public key, display name, and proof signature are required.");

        byte[] signature;
        try { signature = Convert.FromBase64String(request.ChallengeProofSignatureBase64); }
        catch (FormatException) { return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid proof signature encoding."); }

        var coordinator = context.RequestServices.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device enrollment is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var claimed = await coordinator.ClaimEnrollmentAsync(
                request.EnrollmentToken,
                new DeviceId(deviceUlid),
                request.DevicePublicKeyBase64,
                request.DeviceDisplayName,
                request.Platform,
                signature,
                cancellationToken).ConfigureAwait(false);
            return JsonOk(new
            {
                enrollmentId = claimed.EnrollmentId.Value.ToString(),
                verificationCode = claimed.VerificationCode,
                state = claimed.State,
                challengeNonceBase64 = claimed.ChallengeNonceBase64,
                expiresAt = claimed.ExpiresAt
            });
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleConfirmEnrollmentAsync(string id, HttpContext context, ConfirmEnrollmentHttpRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var enrollmentUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid enrollment ID.");

        var coordinator = context.RequestServices.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device enrollment is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var result = request?.Deny == true
                ? await coordinator.DenyOrCancelEnrollmentAsync(tenantId, options!.ApplicationId, principalId, new EnrollmentId(enrollmentUlid), true, cancellationToken).ConfigureAwait(false)
                : await coordinator.ApproveEnrollmentAsync(tenantId, options!.ApplicationId, principalId, new EnrollmentId(enrollmentUlid), cancellationToken).ConfigureAwait(false);
            return JsonOk(new
            {
                enrollmentId = result.EnrollmentId.Value.ToString(),
                state = result.State,
                approvedAt = result.ApprovedAt
            });
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleCompleteEnrollmentAsync(string id, HttpContext context, CompleteEnrollmentHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryParseUlid(id, out var enrollmentUlid) || request is null ||
            !TryParseUlid(request.TenantId, out var tenantUlid) ||
            !TryParseUlid(request.DeviceId, out var deviceUlid) ||
            string.IsNullOrWhiteSpace(request.CompletionProofSignatureBase64))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid enrollmentId, tenantId, deviceId, and completionProofSignatureBase64 are required.");

        byte[] signature;
        try { signature = Convert.FromBase64String(request.CompletionProofSignatureBase64); }
        catch (FormatException) { return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid proof signature encoding."); }

        var coordinator = context.RequestServices.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device enrollment is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var bundle = await coordinator.CompleteEnrollmentAsync(
                new TenantId(tenantUlid),
                new EnrollmentId(enrollmentUlid),
                new DeviceId(deviceUlid),
                signature,
                options!.Audience,
                cancellationToken).ConfigureAwait(false);
            return JsonOk(bundle);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRefreshTokenAsync(HttpContext context, RefreshTokenHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out _, out var disabledResult))
            return disabledResult!;
        if (request is null || string.IsNullOrWhiteSpace(request.RefreshToken) || !TryParseUlid(request.DeviceId, out var deviceUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid refreshToken and deviceId are required.");

        var coordinator = context.RequestServices.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device token refresh is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var bundle = await coordinator.RotateRefreshTokenAsync(request.RefreshToken, new DeviceId(deviceUlid), cancellationToken).ConfigureAwait(false);
            return JsonOk(bundle);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleListDevicesAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out _, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");

        var store = context.RequestServices.GetService<IDeviceEnrollmentStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device store is not configured.");

        var devices = await store.ListDevicesAsync(tenantId, principalId, cancellationToken).ConfigureAwait(false);
        return JsonOk(new { items = devices });
    }

    private static async Task<IResult> HandleRevokeDeviceAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var deviceUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid device ID.");

        var coordinator = context.RequestServices.GetService<IDeviceEnrollmentCoordinator>();
        if (coordinator is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Device enrollment is not configured.");

        var deviceId = new DeviceId(deviceUlid);
        return await ExecuteDomainAsync(context, async () =>
        {
            var revoked = await coordinator.RevokeDeviceAsync(tenantId, options!.ApplicationId, principalId, deviceId, cancellationToken).ConfigureAwait(false);

            // Immediately terminate any active realtime WebSockets bound to the revoked device.
            var hub = context.RequestServices.GetService<IMessagingRealtimeHub>();
            if (hub is not null)
            {
                await hub.CloseDeviceConnectionsAsync(tenantId, deviceId, "device_revoked", cancellationToken).ConfigureAwait(false);
            }

            return JsonOk(revoked);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRegisterPushAsync(string id, HttpContext context, RegisterPushHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out var options, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var deviceUlid) || request is null || string.IsNullOrWhiteSpace(request.ProtectedTokenHandle))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Valid device ID and protectedTokenHandle are required.");
        if (!request.ConsentGranted)
            return Forbidden(context, ErrorCodes.Unauthorized, "Explicit push notification consent is required.");

        var store = context.RequestServices.GetService<IMessagingPushRegistrationStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Push registration is not configured.");

        return await ExecuteDomainAsync(context, async () =>
        {
            var reg = await store.RegisterAsync(
                tenantId,
                options!.ApplicationId,
                principalId,
                new DeviceId(deviceUlid),
                request.Provider,
                request.ProtectedTokenHandle,
                request.ConsentGranted,
                cancellationToken).ConfigureAwait(false);
            return JsonResult(reg, StatusCodes.Status201Created);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRevokePushAsync(string id, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryGetEnabledOptions(context, out _, out var disabledResult))
            return disabledResult!;
        if (!TryGetIdentity(context, out var tenantId, out var principalId, out _))
            return Forbidden(context, ErrorCodes.Unauthorized, "Missing required tenant or subject claims.");
        if (!TryParseUlid(id, out var deviceUlid))
            return BadRequest(context, ErrorCodes.InvalidRequest, "Invalid device ID.");

        var store = context.RequestServices.GetService<IMessagingPushRegistrationStore>();
        if (store is null)
            return Unavailable(context, ErrorCodes.CapabilityUnavailable, "Push registration is not configured.");

        await store.RevokeAsync(tenantId, principalId, new DeviceId(deviceUlid), null, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static bool TryGetEnabledOptions(HttpContext context, out MessagingHostingOptions? options, out IResult? errorResult)
    {
        options = context.RequestServices.GetService<IOptions<MessagingHostingOptions>>()?.Value;
        if (options is null || !options.Enabled)
        {
            errorResult = Unavailable(context, ErrorCodes.MessagingDisabled, "OpenConnect IMessaging is disabled.");
            return false;
        }

        errorResult = null;
        return true;
    }

    private static bool TryCheckBodyBounds(HttpContext context, MessagingHostingOptions options, out IResult? errorResult)
    {
        if (context.Request.ContentLength is { } length && length > options.MaxRequestBodyBytes)
        {
            errorResult = Problem(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.InvalidRequest, "Request body exceeds maximum allowed size.");
            return false;
        }

        errorResult = null;
        return true;
    }

    private static bool TryGetIdentity(HttpContext context, out TenantId tenantId, out PrincipalId principalId, out DeviceId? deviceId)
    {
        tenantId = default;
        principalId = default;
        deviceId = null;

        var tenantClaim = context.User.FindFirstValue("tenant_id");
        var principalClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!TryParseUlid(tenantClaim, out var tenantUlid) || !TryParseUlid(principalClaim, out var principalUlid))
            return false;

        tenantId = new TenantId(tenantUlid);
        principalId = new PrincipalId(principalUlid);

        var deviceClaim = context.User.FindFirstValue("device_id");
        if (!string.IsNullOrWhiteSpace(deviceClaim))
        {
            if (!TryParseUlid(deviceClaim, out var deviceUlid))
                return false;
            deviceId = new DeviceId(deviceUlid);
        }

        return true;
    }

    private static bool TryParseParticipantActor(TenantId tenantId, string applicationId, ParticipantInput input, out MessagingActor? actor)
    {
        actor = null;
        if (input.Kind == ParticipantKind.Human)
        {
            if (!TryParseUlid(input.PrincipalId, out var pUlid))
                return false;
            actor = new MessagingActor(tenantId, applicationId, ParticipantKind.Human, new PrincipalId(pUlid));
            return true;
        }

        if (string.IsNullOrWhiteSpace(input.AgentId))
            return false;
        actor = new MessagingActor(tenantId, applicationId, ParticipantKind.Agent, null, new AgentId(input.AgentId.Trim()));
        return true;
    }

    private static int ParseBoundedInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var value))
            return Math.Clamp(fallback, min, max);
        return Math.Clamp(value, min, max);
    }

    private static bool TryParseUlid(string? value, out Ulid ulid)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ulid = default;
            return false;
        }
        try
        {
            ulid = Ulid.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            ulid = default;
            return false;
        }
    }

    private static async Task<IResult> ExecuteDomainAsync(HttpContext context, Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (MessagingDomainException ex)
        {
            var status = ex.StatusCode is >= 400 and <= 599 ? ex.StatusCode : StatusCodes.Status400BadRequest;
            return Problem(context, status, ex.ErrorCode, ex.Message);
        }
    }

    private static IResult JsonOk(object payload) => Results.Json(payload, JsonOptions, statusCode: StatusCodes.Status200OK);

    private static IResult JsonResult(object payload, int statusCode, string? location = null)
    {
        if (location is not null)
            return Results.Created(location, payload);
        return Results.Json(payload, JsonOptions, statusCode: statusCode);
    }

    private static IResult BadRequest(HttpContext context, string errorCode, string title) =>
        Problem(context, StatusCodes.Status400BadRequest, errorCode, title);

    private static IResult Forbidden(HttpContext context, string errorCode, string title) =>
        Problem(context, StatusCodes.Status403Forbidden, errorCode, title);

    private static IResult NotFound(HttpContext context, string errorCode, string title) =>
        Problem(context, StatusCodes.Status404NotFound, errorCode, title);

    private static IResult TooManyRequests(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = (int)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(context, StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, "Too many key package requests.");
    }

    private static IResult Unavailable(HttpContext context, string errorCode, string title) =>
        Problem(context, StatusCodes.Status503ServiceUnavailable, errorCode, title);

    private static IResult Problem(HttpContext context, int statusCode, string errorCode, string title) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            type: $"https://openconnect.dev/problems/{errorCode}",
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = errorCode,
                ["traceId"] = context.TraceIdentifier
            });

    internal sealed record ParticipantInput(ParticipantKind Kind, string? PrincipalId = null, string? AgentId = null);
    internal sealed record CreateConversationHttpRequest(ConversationSecurityProfile SecurityProfile, IReadOnlyList<ParticipantInput>? Participants = null);
    internal sealed record AddParticipantHttpRequest(ParticipantKind Kind, ParticipantRole Role, string? PrincipalId = null, string? AgentId = null);
    internal sealed record CommitMessageHttpRequest(EncryptedMessageEnvelope Envelope, string ClientOperationId, string? CorrelationId = null);
    internal sealed record RecordReceiptHttpRequest(string ConversationId, MessageReceiptKind Kind, string? FailureReason = null);
    internal sealed record PublishKeyPackageHttpRequest(string Protocol, string PublicPackageBase64, string? DeviceId = null, DateTimeOffset? ExpiresAt = null);
    internal sealed record InitAttachmentHttpRequest(string ConversationId, long EncryptedSizeBytes, string EncryptedDigestSha256);
    internal sealed record CompleteAttachmentHttpRequest(string AttachmentId, string EncryptedPayloadBase64);
    internal sealed record CreateCallHttpRequest(string ConversationId, CallMediaSecurityMode MediaSecurityMode = CallMediaSecurityMode.EndpointE2EE, int? RingTimeoutSeconds = null);
    internal sealed record CallReasonHttpRequest(string? Reason = null);
    internal sealed record ScheduleFollowUpHttpRequest(string ConversationId, string RecipientPrincipalId, string AgentId, DateTimeOffset DueAtUtc, int MaxAttempts, string IdempotencyKey, string CausalEventId, string? TaskId = null, TimeOnly? QuietHoursStartUtc = null, TimeOnly? QuietHoursEndUtc = null);
    internal sealed record InitiateEnrollmentHttpRequest(string? BaseGatewayUrl = null, int? ExpirySeconds = null);
    internal sealed record RedeemEnrollmentHttpRequest(string EnrollmentToken, string DeviceId, string DevicePublicKeyBase64, string DeviceDisplayName, DevicePlatform Platform, string ChallengeProofSignatureBase64);
    internal sealed record ConfirmEnrollmentHttpRequest(bool Deny = false);
    internal sealed record CompleteEnrollmentHttpRequest(string TenantId, string DeviceId, string CompletionProofSignatureBase64);
    internal sealed record RefreshTokenHttpRequest(string RefreshToken, string DeviceId);
    internal sealed record RegisterPushHttpRequest(PushProviderKind Provider, string ProtectedTokenHandle, bool ConsentGranted);
}
