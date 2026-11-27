using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Messaging;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class MessagingKeyPackageConsumeTests
{
    private static readonly string TenantA = Ulid.NewUlid().ToString();
    private static readonly string TenantB = Ulid.NewUlid().ToString();
    private static readonly string Caller = Ulid.NewUlid().ToString();
    private static readonly string Other = Ulid.NewUlid().ToString();

    [Fact]
    public async Task Consume_OtherPrincipalWithoutAuthorizer_FailsClosedAndDoesNotTouchStore()
    {
        using var h = Harness.Create(authorizer: null);
        var status = await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}");
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(0, h.Store.ConsumeCalls);
    }

    [Fact]
    public async Task Consume_UnauthorizedConsumer_Returns403AndStoreNotInvoked()
    {
        var authorizer = new FakeAuthorizer(allow: false);
        using var h = Harness.Create(authorizer);
        var status = await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}");
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(0, h.Store.ConsumeCalls);
        var call = Assert.Single(authorizer.Calls);
        Assert.Equal(MessagingPermission.Initiate, call.Permission);
        Assert.Equal(Ulid.Parse(Caller), call.Actor.PrincipalId!.Value.Value);
        Assert.Equal(Ulid.Parse(Other), call.Target!.PrincipalId!.Value.Value);
    }

    [Fact]
    public async Task Consume_DeniedAndMissingPackage_AreIndistinguishableFor403()
    {
        // A denied caller must get the same response whether or not packages exist for the target.
        using var withPackage = Harness.Create(new FakeAuthorizer(allow: false), hasPackage: true);
        using var withoutPackage = Harness.Create(new FakeAuthorizer(allow: false), hasPackage: false);
        Assert.Equal(StatusCodes.Status403Forbidden, await withPackage.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal(StatusCodes.Status403Forbidden, await withoutPackage.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
    }

    [Fact]
    public async Task Consume_AuthorizedConsumer_ReturnsPackage()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true);
        var status = await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}");
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(1, h.Store.ConsumeCalls);
    }

    [Fact]
    public async Task Consume_AuthorizedAgentTarget_AuthorizesAgentActor()
    {
        var authorizer = new FakeAuthorizer(allow: true);
        using var h = Harness.Create(authorizer, hasPackage: true);
        Assert.Equal(StatusCodes.Status200OK, await h.ConsumeAsync(TenantA, Caller, "agentId=agent-1"));
        var call = Assert.Single(authorizer.Calls);
        Assert.Equal(ParticipantKind.Agent, call.Target!.Kind);
        Assert.Equal("agent-1", call.Target.AgentId!.Value.Value);
    }

    [Fact]
    public async Task Consume_OwnPrincipal_DoesNotRequireAuthorizer()
    {
        using var h = Harness.Create(authorizer: null, hasPackage: true);
        Assert.Equal(StatusCodes.Status200OK, await h.ConsumeAsync(TenantA, Caller, $"principalId={Caller}"));
    }

    [Fact]
    public async Task Consume_DeviceOnlyTarget_FailsClosed()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true);
        Assert.Equal(StatusCodes.Status403Forbidden, await h.ConsumeAsync(TenantA, Caller, $"deviceId={Ulid.NewUlid()}"));
        Assert.Equal(0, h.Store.ConsumeCalls);
    }

    [Fact]
    public async Task Consume_ExceedingCallerQuota_Returns429WithRetryAfter()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true, o =>
        {
            o.KeyPackageConsumeLimitPerCaller = 2;
            o.KeyPackageConsumeLimitPerTarget = 100;
        });
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        var status = await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}");
        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        Assert.Equal(2, h.Store.ConsumeCalls);
        Assert.True(int.Parse(h.LastRetryAfter!, System.Globalization.CultureInfo.InvariantCulture) >= 1);

        // Window rollover restores access.
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
    }

    [Fact]
    public async Task Consume_DeniedAttemptsCountAgainstCallerQuota()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: false), o => o.KeyPackageConsumeLimitPerCaller = 1);
        Assert.Equal(403, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal(429, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
    }

    [Fact]
    public async Task Consume_ExceedingTargetQuotaAcrossCallers_Returns429()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true, o => o.KeyPackageConsumeLimitPerTarget = 1);
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal(429, await h.ConsumeAsync(TenantA, Ulid.NewUlid().ToString(), $"principalId={Other}"));
    }

    [Fact]
    public async Task Consume_NonPositiveLimit_DeniesAll()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true, o => o.KeyPackageConsumeLimitPerCaller = 0);
        Assert.Equal(429, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal(0, h.Store.ConsumeCalls);
    }

    [Fact]
    public async Task Consume_QuotaAndStoreAreTenantScoped()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true, o => o.KeyPackageConsumeLimitPerTarget = 1);
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal(200, await h.ConsumeAsync(TenantB, Caller, $"principalId={Other}"));
        Assert.Equal(new[] { TenantA, TenantB }, h.Store.Tenants.Select(t => t.Value.ToString()).ToArray());
    }

    [Fact]
    public void Limiter_RejectsNewKeysWhenFullOfActiveWindows()
    {
        var clock = new TestClock();
        var limiter = new KeyPackageConsumeLimiter(clock);
        for (var i = 0; i < 10_000; i++)
            Assert.True(limiter.TryAcquire($"k{i}", 1, TimeSpan.FromMinutes(1), out _));
        Assert.False(limiter.TryAcquire("overflow", 1, TimeSpan.FromMinutes(1), out _));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(limiter.TryAcquire("overflow", 1, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public async Task Consume_Responses_AreNoStore()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true, o => o.KeyPackageConsumeLimitPerCaller = 1);
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal("no-store", h.LastCacheControl);
        Assert.Equal(429, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal("no-store", h.LastCacheControl);
        using var denied = Harness.Create(new FakeAuthorizer(allow: false));
        Assert.Equal(403, await denied.ConsumeAsync(TenantA, Caller, $"principalId={Other}"));
        Assert.Equal("no-store", denied.LastCacheControl);
    }

    [Fact]
    public async Task Consume_AuthorizerThrows_FailsClosedWithoutDetail()
    {
        using var h = Harness.Create(new ThrowingAuthorizer(), hasPackage: true);
        var status = await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal(0, h.Store.ConsumeCalls);
        Assert.DoesNotContain("secret-backend-detail", h.LastBody);
    }

    [Fact]
    public async Task Consume_PrincipalAndAgent_AuthorizesBothAndDeniesIfSecondDenied()
    {
        var authorizer = new FakeAuthorizer(allow: true);
        using var ok = Harness.Create(authorizer, hasPackage: true);
        Assert.Equal(200, await ok.ConsumeAsync(TenantA, Caller, $"principalId={Other}&agentId=agent-1"));
        Assert.Equal(2, authorizer.Calls.Count);

        var partial = new FakeAuthorizer(allow: true, denyKind: ParticipantKind.Human);
        using var h = Harness.Create(partial, hasPackage: true);
        Assert.Equal(403, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}&agentId=agent-1"));
        Assert.Equal(0, h.Store.ConsumeCalls);
    }

    [Fact]
    public async Task Consume_PrincipalAndAgent_CountsBothTargetQuotas()
    {
        using var h = Harness.Create(new FakeAuthorizer(allow: true), hasPackage: true, o => o.KeyPackageConsumeLimitPerTarget = 1);
        Assert.Equal(200, await h.ConsumeAsync(TenantA, Caller, $"principalId={Other}&agentId=agent-1"));
        // Principal-only request must hit the principal's quota consumed by the combined request.
        Assert.Equal(429, await h.ConsumeAsync(TenantA, Ulid.NewUlid().ToString(), $"principalId={Other}"));
    }

    [Fact]
    public void Limiter_FullTableDoesNotRescanWithinSameWindow()
    {
        var clock = new TestClock();
        var limiter = new KeyPackageConsumeLimiter(clock);
        var window = TimeSpan.FromMinutes(1);
        for (var i = 0; i < 10_000; i++)
            Assert.True(limiter.TryAcquire($"k{i}", 1, window, out _));
        Assert.False(limiter.TryAcquire("a", 1, window, out _));
        Assert.False(limiter.TryAcquire("b", 1, window, out _));
        clock.Advance(window);
        Assert.True(limiter.TryAcquire("c", 1, window, out _));
    }

    private sealed class ThrowingAuthorizer : IMessagingAuthorizer
    {
        public ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(
            MessagingActor actor, MessagingPermission permission, ConversationId? conversationId = null,
            MessagingActor? target = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-backend-detail");
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeAuthorizer(bool allow, ParticipantKind? denyKind = null) : IMessagingAuthorizer
    {
        public List<(MessagingActor Actor, MessagingPermission Permission, MessagingActor? Target)> Calls { get; } = [];

        public ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(
            MessagingActor actor, MessagingPermission permission, ConversationId? conversationId = null,
            MessagingActor? target = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((actor, permission, target));
            var allowed = allow && (denyKind is null || target?.Kind != denyKind);
            return ValueTask.FromResult(new MessagingAuthorizationDecision(allowed, allowed ? null : "denied", allowed ? null : "denied"));
        }
    }

    private sealed class FakeStore(bool hasPackage) : IMessagingKeyPackageStore
    {
        public int ConsumeCalls { get; private set; }
        public List<TenantId> Tenants { get; } = [];

        public ValueTask<DeviceKeyPackage> PublishAsync(DeviceKeyPackage keyPackage, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(keyPackage);

        public ValueTask<DeviceKeyPackage?> ConsumeOneAsync(TenantId tenantId, PrincipalId? ownerPrincipalId, AgentId? ownerAgentId, DeviceId? deviceId, CancellationToken cancellationToken = default)
        {
            ConsumeCalls++;
            Tenants.Add(tenantId);
            DeviceKeyPackage? pkg = hasPackage
                ? new DeviceKeyPackage(tenantId, new KeyPackageId(Ulid.NewUlid()), ownerPrincipalId, ownerAgentId,
                    new DeviceId(Ulid.NewUlid()), "mls", "AAAA", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), true, DateTimeOffset.UtcNow)
                : null;
            return ValueTask.FromResult(pkg);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _sp;
        private readonly RequestDelegate _handler;

        public FakeStore Store { get; }
        public TestClock Clock { get; }
        public string? LastRetryAfter { get; private set; }
        public string? LastCacheControl { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        private Harness(ServiceProvider sp, RequestDelegate handler, FakeStore store, TestClock clock)
        {
            _sp = sp;
            _handler = handler;
            Store = store;
            Clock = clock;
        }

        public static Harness Create(IMessagingAuthorizer? authorizer, Action<MessagingHostingOptions>? configure = null) =>
            Create(authorizer, true, configure);

        public static Harness Create(IMessagingAuthorizer? authorizer, bool hasPackage, Action<MessagingHostingOptions>? configure = null)
        {
            var store = new FakeStore(hasPackage);
            var clock = new TestClock();
            var services = new ServiceCollection();
            services.AddRouting();
            services.AddAuthorization();
            services.AddLogging();
            services.AddOpenConnect();
            services.AddOpenConnectTaskExecution();
            services.AddSingleton<TimeProvider>(clock);
            services.AddOpenConnectMessagingHosting(o =>
            {
                o.Enabled = true;
                configure?.Invoke(o);
            });
            services.AddSingleton<IMessagingKeyPackageStore>(store);
            if (authorizer is not null)
                services.AddSingleton(authorizer);

            var sp = services.BuildServiceProvider();
            var builder = new TestEndpointRouteBuilder(sp);
            builder.MapOpenConnectMessaging();
            var endpoint = builder.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Single(e =>
                e.RoutePattern.RawText == "/openconnect/v1/messaging/keys/packages" &&
                e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
            return new Harness(sp, endpoint.RequestDelegate!, store, clock);
        }

        public async Task<int> ConsumeAsync(string tenant, string principal, string query)
        {
            var context = new DefaultHttpContext { RequestServices = _sp };
            context.Request.Method = "GET";
            context.Request.QueryString = new QueryString("?" + query);
            context.Response.Body = new MemoryStream();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", tenant), new Claim("sub", principal)], "test"));
            await _handler(context);
            LastRetryAfter = context.Response.Headers.TryGetValue("Retry-After", out StringValues v) ? v.ToString() : null;
            LastCacheControl = context.Response.Headers.CacheControl.ToString();
            context.Response.Body.Position = 0;
            LastBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
            return context.Response.StatusCode;
        }

        public void Dispose() => _sp.Dispose();
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
    }
}
