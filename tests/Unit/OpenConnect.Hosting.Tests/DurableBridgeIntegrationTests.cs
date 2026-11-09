using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

/// <summary>
/// Durable bridge integration tests (BRIDGE-DURABLE-B): session UUID mapping over
/// <see cref="ISessionUuidMappingStore"/> / <see cref="IDurableSessionUuidResolver"/> and approval
/// nonces over <see cref="IApprovalNonceStore"/> (native request id stored verbatim, never coerced).
/// Verifies durable-first resolve with sync fallback, dual ownership, fail-closed conflicts, cross-instance
/// replay denial, denied-burns-nothing, and unchanged wire envelopes.
/// </summary>
public sealed class DurableBridgeIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string OtherPrincipal = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string ExpectedArgs = "args-hash-1";
    private const string ExpectedToolRev = "tool-rev-7";

    // ---- Session UUID mapping: durable resolve ----

    [Fact]
    public async Task Durable_unmapped_uuid_is_concealed_as_404()
    {
        var store = new InMemorySessionStore();
        store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var durable = new FakeSessionUuidStore();

        var response = await InvokeEventsAsync(store, Guid.NewGuid().ToString("D"), string.Empty, Services(durable: durable));

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Durable_unknown_uuid_falls_back_to_sync_resolver()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        await store.AppendEventAsync(session.Id.Value, "task.accepted",
            CanonicalData(Guid.Parse("123e4567-e89b-42d3-a456-426614174010"), "2026-01-01T00:00:00Z", """{"taskId":"task-1"}"""));
        var sync = new InMemorySessionUuidResolver();
        sync.Register(sessionUuid, session.Id.Value);

        var response = await InvokeEventsAsync(store, sessionUuid.ToString("D"), string.Empty,
            Services(durable: new FakeSessionUuidStore(), sync: sync));

        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(sessionUuid.ToString("D"), json.RootElement[0].GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Durable_principal_mismatch_is_concealed_as_404_post_resolve()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        // Permissive resolver ignores ownership (faulty-store simulation): the endpoint's own
        // post-resolve dual ownership check must still conceal the foreign session as 404.
        var permissive = new FakeSessionUuidStore(ignoreOwnership: true);
        Assert.Equal(SessionUuidMappingSaveOutcome.Saved,
            await permissive.TryAddAsync(sessionUuid, session.Id, new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal))));

        var response = await InvokeEventsAsync(store, sessionUuid.ToString("D"), string.Empty,
            Services(durable: permissive), principal: OtherPrincipal);

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Durable_resolve_returns_canonical_envelope_with_unchanged_wire()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        var eventId = Guid.Parse("123e4567-e89b-42d3-a456-426614174010");
        await store.AppendEventAsync(session.Id.Value, "task.accepted",
            CanonicalData(eventId, "2026-01-01T00:00:00Z", """{"taskId":"task-1"}"""));
        var durable = new FakeSessionUuidStore();
        Assert.Equal(SessionUuidMappingSaveOutcome.Saved,
            await durable.TryAddAsync(sessionUuid, session.Id, new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal))));

        var response = await InvokeEventsAsync(store, sessionUuid.ToString("D"), "?afterSequence=0", Services(durable: durable));

        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        var first = json.RootElement[0];
        Assert.Equal("openconnect.v1", first.GetProperty("schemaVersion").GetString());
        Assert.Equal(eventId.ToString("D"), first.GetProperty("eventId").GetString());
        Assert.Equal("task.accepted", first.GetProperty("eventType").GetString());
        Assert.Equal(sessionUuid.ToString("D"), first.GetProperty("sessionId").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", first.GetProperty("occurredAt").GetString());
        Assert.False(first.TryGetProperty("type", out _));
        Assert.False(first.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Durable_path_preserves_ulid_legacy_and_cursor()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        for (var i = 1; i <= 2; i++)
            await store.AppendEventAsync(session.Id.Value, "task.accepted",
                CanonicalData(Guid.Parse($"123e4567-e89b-42d3-a456-4266141740{i:D2}"), "2026-01-01T00:00:00Z", """{"n":1}"""));

        var legacy = await InvokeEventsAsync(store, session.Id.Value.ToString(), string.Empty, Services(durable: new FakeSessionUuidStore()));

        Assert.Equal(200, legacy.Status);
        using var json = JsonDocument.Parse(legacy.Body);
        Assert.Equal(JsonValueKind.Object, json.RootElement[0].GetProperty("sessionId").ValueKind);
        Assert.False(json.RootElement[0].TryGetProperty("eventId", out _));
    }

    // ---- Session create: genuine UUID + TryAdd side-effect ----

    [Fact]
    public async Task Session_create_registers_genuine_uuid_mapping()
    {
        var store = new InMemorySessionStore();
        var durable = new FakeSessionUuidStore();

        var response = await InvokeCreateSessionAsync(store, Services(durable: durable));

        Assert.Equal(201, response.Status);
        var publicId = Assert.Single(durable.PublicIds);
        Assert.NotEqual(Guid.Empty, publicId);
        var resolved = await durable.ResolveAsync(publicId, new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        Assert.NotNull(resolved);
        Assert.NotNull(store.Get(resolved!.Value.Value));
        Assert.Null(await durable.ResolveAsync(publicId, new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(OtherPrincipal))));
    }

    [Fact]
    public async Task Session_create_mapping_conflict_fails_closed()
    {
        var store = new InMemorySessionStore();

        var response = await InvokeCreateSessionAsync(store, Services(new AlwaysConflictMappingStore()));

        Assert.Equal(409, response.Status);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Session_create_mapping_throw_removes_orphan_and_rethrows()
    {
        var store = new InMemorySessionStore();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeCreateSessionAsync(store, Services(new ThrowingMappingStore())));

        Assert.Equal(0, store.Count);
    }

    // ---- Approval bridge: durable nonce store ----

    [Fact]
    public async Task Durable_replay_across_instances_is_denied()
    {
        await using var first = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        await using var second = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        var scope = NewScope();
        var nonces = new FakeNonceStore();
        var req1 = Assert.IsType<ExternalRequest>(first.PendingExternalRequest);
        var bridge1 = DurableBridge(first, scope, nonces);
        await bridge1.ApproveAsync(req1, CreateDecision(scope, req1, "cross-n1"), "approved").WaitAsync(Timeout);

        var req2 = Assert.IsType<ExternalRequest>(second.PendingExternalRequest);
        var bridge2 = DurableBridge(second, scope, nonces);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge2.ApproveAsync(req2, CreateDecision(scope, req2, "cross-n1"), "approved").WaitAsync(Timeout));
        Assert.Same(req2, second.PendingExternalRequest);
    }

    [Fact]
    public async Task Durable_preconsumed_binding_is_denied()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        var scope = NewScope();
        var nonces = new FakeNonceStore();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.Equal(ApprovalNonceConsumeOutcome.Consumed,
            await nonces.TryConsumeAsync(scope.TenantId, scope.PrincipalId, "pre-n1", request.RequestId));
        var bridge = DurableBridge(interaction, scope, nonces);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "pre-n1"), "approved").WaitAsync(Timeout));
        Assert.Same(request, interaction.PendingExternalRequest);
    }

    [Fact]
    public async Task Durable_denied_approval_burns_nothing()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        var scope = NewScope();
        var nonces = new FakeNonceStore();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = DurableBridge(interaction, scope, nonces);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "burn-n1", approved: false), "approved").WaitAsync(Timeout));

        Assert.Same(request, interaction.PendingExternalRequest);
        Assert.Null(await nonces.GetAsync(scope.TenantId, scope.PrincipalId, "burn-n1"));
    }

    [Fact]
    public async Task Durable_expired_approval_burns_nothing()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        var scope = NewScope();
        var nonces = new FakeNonceStore();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = DurableBridge(interaction, scope, nonces);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request,
                CreateDecision(scope, request, "burn-n2", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                "approved").WaitAsync(Timeout));

        Assert.Null(await nonces.GetAsync(scope.TenantId, scope.PrincipalId, "burn-n2"));
    }

    [Fact]
    public async Task Durable_revoked_approval_is_unauthorized_and_burns_nothing()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        var scope = NewScope();
        var nonces = new FakeNonceStore();
        var request = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        var bridge = new NativeWorkflowApprovalBridge(interaction, scope, ExpectedArgs, ExpectedToolRev,
            _ => ValueTask.FromResult(true), null, null, ConsumeVia(nonces, scope));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bridge.ApproveAsync(request, CreateDecision(scope, request, "burn-n3"), "approved").WaitAsync(Timeout));

        Assert.Null(await nonces.GetAsync(scope.TenantId, scope.PrincipalId, "burn-n3"));
    }

    [Fact]
    public async Task Durable_stale_request_is_invalid_operation()
    {
        await using var interaction = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        await using var other = await NativeWorkflowInteraction.StartAsync(CreateWorkflow(), "start").WaitAsync(Timeout);
        var scope = NewScope();
        var nonces = new FakeNonceStore();
        var foreign = Assert.IsType<ExternalRequest>(other.PendingExternalRequest);
        var bridge = DurableBridge(interaction, scope, nonces);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.ApproveAsync(foreign, CreateDecision(scope, foreign, "stale-n1"), "approved").WaitAsync(Timeout));
    }

    // ---- Helpers ----

    private static IServiceProvider Services(FakeSessionUuidStore? durable = null, ISessionUuidResolver? sync = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (durable is not null)
        {
            services.AddSingleton<IDurableSessionUuidResolver>(durable);
            services.AddSingleton<ISessionUuidMappingStore>(durable);
        }

        if (sync is not null) services.AddSingleton(sync);
        return services.BuildServiceProvider();
    }

    private static IServiceProvider Services(ISessionUuidMappingStore mapping)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mapping);
        return services.BuildServiceProvider();
    }

    private static async Task<(int Status, string Body)> InvokeEventsAsync(
        InMemorySessionStore store, string id, string query, IServiceProvider services, string principal = Principal)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, principal)
            ], "test")),
            Response = { Body = new MemoryStream() }
        };
        context.Request.QueryString = new QueryString(query);
        var result = await EndpointRouteBuilderExtensions.HandleGetSessionEventsFromRequestAsync(id, context, store);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    private static async Task<(int Status, string Body)> InvokeCreateSessionAsync(InMemorySessionStore store, IServiceProvider services)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, Principal)
            ], "test")),
            Response = { Body = new MemoryStream() }
        };
        var result = await EndpointRouteBuilderExtensions.HandleCreateSessionAsync(context, null, store);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    private static string CanonicalData(Guid eventId, string occurredAt, string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        var builder = new StringBuilder("{");
        builder.Append($"\"eventId\":\"{eventId:D}\",\"occurredAt\":\"{occurredAt}\",");
        foreach (var property in payload.RootElement.EnumerateObject())
            builder.Append($"\"{property.Name}\":{property.Value.GetRawText()},");
        builder.Length--;
        builder.Append('}');
        return builder.ToString();
    }

    private static NativeWorkflowApprovalScope NewScope() => new(
        new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()),
        new TaskId(Ulid.NewUlid()));

    private static NativeWorkflowApprovalBridge DurableBridge(
        NativeWorkflowInteraction interaction, NativeWorkflowApprovalScope scope, FakeNonceStore nonces) =>
        new(interaction, scope, ExpectedArgs, ExpectedToolRev, null, null, null, ConsumeVia(nonces, scope));

    private static Func<string, string, CancellationToken, ValueTask<bool>> ConsumeVia(FakeNonceStore store, NativeWorkflowApprovalScope scope) =>
        async (nonce, requestId, ct) =>
            await store.TryConsumeAsync(scope.TenantId, scope.PrincipalId, nonce, requestId, ct).ConfigureAwait(false)
            == ApprovalNonceConsumeOutcome.Consumed;

    private static NativeWorkflowApprovalDecision CreateDecision(
        NativeWorkflowApprovalScope scope,
        ExternalRequest request,
        string nonce,
        bool approved = true,
        DateTimeOffset? expiresAt = null) =>
        new(approved, scope.TenantId, scope.PrincipalId, scope.SessionId, scope.TaskId,
            request.RequestId, ExpectedArgs, ExpectedToolRev, nonce,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5));

    private static Workflow CreateWorkflow()
    {
        var source = new FunctionExecutor<string, ApprovalRequest>(
            "request-source",
            (input, _, _) => ValueTask.FromResult(new ApprovalRequest(input)));
        return new WorkflowBuilder(source)
            .AddExternalCall<ApprovalRequest, string>(source, "approval")
            .WithOutputFrom(source)
            .Build();
    }

    private sealed record ApprovalRequest(string Text);

    private sealed class FakeSessionUuidStore(bool ignoreOwnership = false) : ISessionUuidMappingStore, IDurableSessionUuidResolver
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, (SessionId Session, TenantId Tenant, PrincipalId Principal)> _byPublic = new();
        private readonly Dictionary<Ulid, Guid> _bySession = new();

        public IReadOnlyList<Guid> PublicIds
        {
            get { lock (_gate) { return _byPublic.Keys.ToArray(); } }
        }

        public Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_byPublic.TryGetValue(publicId, out var entry)) return Task.FromResult<SessionId?>(null);
                if (!ignoreOwnership && (entry.Tenant != tenantId || entry.Principal != principalId))
                    return Task.FromResult<SessionId?>(null);
                return Task.FromResult<SessionId?>(entry.Session);
            }
        }

        public Task<SessionUuidMappingSaveOutcome> TryAddAsync(Guid publicId, SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default)
        {
            if (publicId == Guid.Empty || sessionId.Value == default) return Task.FromResult(SessionUuidMappingSaveOutcome.Conflict);
            lock (_gate)
            {
                if (_byPublic.TryGetValue(publicId, out var existing))
                    return Task.FromResult(existing.Session.Equals(sessionId) && existing.Tenant.Equals(tenantId) && existing.Principal.Equals(principalId)
                        ? SessionUuidMappingSaveOutcome.AlreadyMapped
                        : SessionUuidMappingSaveOutcome.Conflict);
                if (_bySession.ContainsKey(sessionId.Value)) return Task.FromResult(SessionUuidMappingSaveOutcome.Conflict);
                _byPublic[publicId] = (sessionId, tenantId, principalId);
                _bySession[sessionId.Value] = publicId;
                return Task.FromResult(SessionUuidMappingSaveOutcome.Saved);
            }
        }
    }

    private sealed class AlwaysConflictMappingStore : ISessionUuidMappingStore
    {
        public Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
            Task.FromResult<SessionId?>(null);

        public Task<SessionUuidMappingSaveOutcome> TryAddAsync(Guid publicId, SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
            Task.FromResult(SessionUuidMappingSaveOutcome.Conflict);
    }

    private sealed class ThrowingMappingStore : ISessionUuidMappingStore
    {
        public Task<SessionId?> ResolveAsync(Guid publicId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
            Task.FromResult<SessionId?>(null);

        public Task<SessionUuidMappingSaveOutcome> TryAddAsync(Guid publicId, SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken ct = default) =>
            throw new InvalidOperationException("mapping store failure");
    }

    private sealed class FakeNonceStore : IApprovalNonceStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<(TenantId Tenant, string Nonce), (string RequestId, PrincipalId Principal, DateTime CreatedAtUtc)> _bindings = new();

        public Task<ApprovalNonceBinding?> GetAsync(TenantId tenantId, PrincipalId principalId, string nonce, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_bindings.TryGetValue((tenantId, nonce), out var binding) || !binding.Principal.Equals(principalId))
                    return Task.FromResult<ApprovalNonceBinding?>(null);
                return Task.FromResult<ApprovalNonceBinding?>(new ApprovalNonceBinding(binding.RequestId, binding.CreatedAtUtc));
            }
        }

        public Task<ApprovalNonceConsumeOutcome> TryConsumeAsync(TenantId tenantId, PrincipalId principalId, string nonce, string requestId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 64 || string.IsNullOrWhiteSpace(requestId))
                return Task.FromResult(ApprovalNonceConsumeOutcome.Conflict);
            lock (_gate)
            {
                if (_bindings.TryGetValue((tenantId, nonce), out var existing))
                    return Task.FromResult(existing.RequestId == requestId && existing.Principal.Equals(principalId)
                        ? ApprovalNonceConsumeOutcome.AlreadyConsumed
                        : ApprovalNonceConsumeOutcome.Conflict);
                _bindings[(tenantId, nonce)] = (requestId, principalId, DateTime.UtcNow);
                return Task.FromResult(ApprovalNonceConsumeOutcome.Consumed);
            }
        }
    }
}
