using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore.Fencing;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class ServiceFencingTests
{
    private static readonly TenantId TenantA = new(Ulid.NewUlid());
    private static readonly TenantId TenantB = new(Ulid.NewUlid());
    private static readonly PrincipalId Principal1 = new(Ulid.NewUlid());

    [Fact]
    public async Task AllowedTargetHitsDownstreamOnceAndEmitsContentFreeEvidence()
    {
        var hits = 0;
        var sink = new InMemoryServiceFencingEvidenceSink();
        var canonicalSink = new RecordingSecurityEvidenceSink();
        var accessor = new AsyncLocalServiceFencingContextAccessor();
        using var client = CreateClient(
            serviceId: "orders-api",
            configure: p =>
            {
                p.AllowedHost = "orders.example.com";
                p.AllowedTenants.Add(TenantA);
                p.AllowedPurposes.Add("order-lookup");
                p.AllowedMethods.Add("GET");
            },
            resolver: new FixedResolver(IPAddress.Parse("93.184.216.34")),
            innerHandler: new CountingHandler(_ =>
            {
                hits++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            accessor: accessor,
            sink: sink,
            canonicalSink: canonicalSink);

        using var scope = accessor.BeginScope(new ServiceFencingExecutionContext(
            TenantA, Principal1, "orders-api", "order-lookup", ApplicationId: "demo-app", RequestId: "req-1"));
        using var response = await client.GetAsync("https://orders.example.com/api/orders?secret=raw-value");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, hits);
        var ev = Assert.Single(sink.Snapshot());
        Assert.Equal("allow", ev.Decision);
        Assert.Equal("executed", ev.EnforcementOutcome);
        Assert.Equal(ServiceFencingReasonCodes.Allowed, ev.ReasonCode);
        var canonical = Assert.Single(canonicalSink.Events);
        Assert.Equal(SecurityEvidenceDecision.Allow, canonical.Decision);
        Assert.Equal(SecurityEnforcementOutcome.Executed, canonical.EnforcementOutcome);
    }

    [Fact]
    public async Task MissingContextAndCrossTenantMismatchAreDeniedBeforeSocket()
    {
        var hits = 0;
        var sink = new InMemoryServiceFencingEvidenceSink();
        var accessor = new AsyncLocalServiceFencingContextAccessor();
        using var client = CreateClient(
            serviceId: "orders-api",
            configure: p =>
            {
                p.AllowedHost = "orders.example.com";
                p.AllowedTenants.Add(TenantA);
            },
            resolver: new FixedResolver(IPAddress.Parse("93.184.216.34")),
            innerHandler: new CountingHandler(_ => { hits++; return new HttpResponseMessage(HttpStatusCode.OK); }),
            accessor: accessor,
            sink: sink);

        var missingEx = await Assert.ThrowsAsync<ServiceFencingDeniedException>(() =>
            client.GetAsync("https://orders.example.com/api/orders"));
        Assert.Equal(ServiceFencingReasonCodes.MissingExecutionContext, missingEx.ReasonCode);
        Assert.Equal(0, hits);

        using var wrongTenantScope = accessor.BeginScope(new ServiceFencingExecutionContext(
            TenantB, Principal1, "orders-api", "order-lookup"));
        var tenantEx = await Assert.ThrowsAsync<ServiceFencingDeniedException>(() =>
            client.GetAsync("https://orders.example.com/api/orders"));
        Assert.Equal(ServiceFencingReasonCodes.TenantMismatch, tenantEx.ReasonCode);
        Assert.Equal(0, hits);
    }

    [Theory]
    [InlineData("http://orders.example.com/api", ServiceFencingReasonCodes.SchemeNotAllowed)]
    [InlineData("https://user:pass@orders.example.com/api", ServiceFencingReasonCodes.UserInfoRejected)]
    [InlineData("https://evil.example.com/api", ServiceFencingReasonCodes.HostNotAllowed)]
    [InlineData("https://orders.example.com:8443/api", ServiceFencingReasonCodes.PortNotAllowed)]
    public async Task UnsafeUriComponentsAreDeniedBeforeSocket(string url, string expectedReason)
    {
        var hits = 0;
        var accessor = new AsyncLocalServiceFencingContextAccessor();
        using var client = CreateClient(
            serviceId: "orders-api",
            configure: p =>
            {
                p.AllowedHost = "orders.example.com";
                p.AllowedTenants.Add(TenantA);
            },
            resolver: new FixedResolver(IPAddress.Parse("93.184.216.34")),
            innerHandler: new CountingHandler(_ => { hits++; return new HttpResponseMessage(HttpStatusCode.OK); }),
            accessor: accessor);

        using var scope = accessor.BeginScope(new ServiceFencingExecutionContext(TenantA, Principal1, "orders-api", "lookup"));
        var ex = await Assert.ThrowsAsync<ServiceFencingDeniedException>(() => client.GetAsync(url));
        Assert.Equal(expectedReason, ex.ReasonCode);
        Assert.Equal(0, hits);
    }

    [Theory]
    [InlineData("127.0.0.1", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    [InlineData("10.0.0.5", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    [InlineData("172.16.0.10", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    [InlineData("192.168.1.1", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    [InlineData("::1", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    [InlineData("::ffff:127.0.0.1", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    [InlineData("::ffff:169.254.169.254", false, ServiceFencingReasonCodes.MetadataIpBlocked)]
    [InlineData("::ffff:169.254.169.254", true, ServiceFencingReasonCodes.MetadataIpBlocked)]
    [InlineData("169.254.169.254", true, ServiceFencingReasonCodes.MetadataIpBlocked)]
    [InlineData("fd00:ec2::254", true, ServiceFencingReasonCodes.MetadataIpBlocked)]
    [InlineData("64:ff9b::169.254.169.254", true, ServiceFencingReasonCodes.MetadataIpBlocked)]
    [InlineData("64:ff9b::127.0.0.1", false, ServiceFencingReasonCodes.PrivateIpBlocked)]
    public async Task SsrfLoopbackPrivateMetadataAndIpv6MappedIpsAreBlocked(
        string resolvedIp, bool allowPrivateNetwork, string expectedReason)
    {
        var hits = 0;
        var accessor = new AsyncLocalServiceFencingContextAccessor();
        using var client = CreateClient(
            serviceId: "orders-api",
            configure: p =>
            {
                p.AllowedHost = "orders.example.com";
                p.AllowedTenants.Add(TenantA);
                p.AllowPrivateNetwork = allowPrivateNetwork;
            },
            resolver: new FixedResolver(IPAddress.Parse(resolvedIp)),
            innerHandler: new CountingHandler(_ => { hits++; return new HttpResponseMessage(HttpStatusCode.OK); }),
            accessor: accessor);

        using var scope = accessor.BeginScope(new ServiceFencingExecutionContext(TenantA, Principal1, "orders-api", "lookup"));
        var ex = await Assert.ThrowsAsync<ServiceFencingDeniedException>(() =>
            client.GetAsync("https://orders.example.com/api"));
        Assert.Equal(expectedReason, ex.ReasonCode);
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task ApprovedPrivateServiceEndpointAllowsRfc1918ButStillBlocksMetadata()
    {
        var hits = 0;
        var accessor = new AsyncLocalServiceFencingContextAccessor();
        using var client = CreateClient(
            serviceId: "internal-svc",
            configure: p =>
            {
                p.AllowedHost = "internal.svc.local";
                p.AllowedTenants.Add(TenantA);
                p.AllowPrivateNetwork = true;
            },
            resolver: new FixedResolver(IPAddress.Parse("10.20.30.40")),
            innerHandler: new CountingHandler(_ => { hits++; return new HttpResponseMessage(HttpStatusCode.OK); }),
            accessor: accessor);

        using var scope = accessor.BeginScope(new ServiceFencingExecutionContext(TenantA, Principal1, "internal-svc", "internal"));
        using var response = await client.GetAsync("https://internal.svc.local/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task RedirectEscapeIsBlockedByDefaultAndWhenReauthorizedDisallowedHostIsDenied()
    {
        var hits = 0;
        var accessor = new AsyncLocalServiceFencingContextAccessor();
        using var defaultClient = CreateClient(
            serviceId: "orders-api",
            configure: p =>
            {
                p.AllowedHost = "orders.example.com";
                p.AllowedTenants.Add(TenantA);
                p.MaxRedirectHops = 0;
            },
            resolver: new FixedResolver(IPAddress.Parse("93.184.216.34")),
            innerHandler: new CountingHandler(_ =>
            {
                hits++;
                var resp = new HttpResponseMessage(HttpStatusCode.Found);
                resp.Headers.Location = new Uri("https://evil.example.com/steal");
                return resp;
            }),
            accessor: accessor);

        using var scope = accessor.BeginScope(new ServiceFencingExecutionContext(TenantA, Principal1, "orders-api", "lookup"));
        var ex = await Assert.ThrowsAsync<ServiceFencingDeniedException>(() =>
            defaultClient.GetAsync("https://orders.example.com/start"));
        Assert.Equal(ServiceFencingReasonCodes.RedirectBlocked, ex.ReasonCode);
        Assert.Equal(1, hits);

        // Even with MaxRedirectHops > 0, redirecting to an unapproved host is denied before calling the second host.
        var hopHits = 0;
        using var redirectClient = CreateClient(
            serviceId: "orders-api",
            configure: p =>
            {
                p.AllowedHost = "orders.example.com";
                p.AllowedTenants.Add(TenantA);
                p.MaxRedirectHops = 2;
            },
            resolver: new FixedResolver(IPAddress.Parse("93.184.216.34")),
            innerHandler: new CountingHandler(_ =>
            {
                hopHits++;
                var resp = new HttpResponseMessage(HttpStatusCode.Found);
                resp.Headers.Location = new Uri("https://evil.example.com/steal");
                return resp;
            }),
            accessor: accessor);

        var escapeEx = await Assert.ThrowsAsync<ServiceFencingDeniedException>(() =>
            redirectClient.GetAsync("https://orders.example.com/start"));
        Assert.Equal(ServiceFencingReasonCodes.HostNotAllowed, escapeEx.ReasonCode);
        Assert.Equal(1, hopHits);
    }

    [Fact]
    public void DiRegistrationWiresNamedFencedHttpClientAndDisablesAutoRedirect()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectFencedHttpClient("billing-api", policy =>
        {
            policy.AllowedHost = "billing.example.com";
            policy.AllowedTenants.Add(TenantA);
        });
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("billing-api");
        Assert.NotNull(client);

        var options = provider.GetRequiredService<IOptionsMonitor<ServiceFencingOptions>>();
        using var socketsHandler = FencedSocketsHttpHandlerFactory.Create("billing-api", options);
        Assert.False(socketsHandler.AllowAutoRedirect);
        Assert.False(socketsHandler.UseCookies);
        Assert.False(socketsHandler.UseProxy);
        Assert.NotNull(socketsHandler.ConnectCallback);
    }

    private static HttpClient CreateClient(
        string serviceId,
        Action<FencedServicePolicy> configure,
        IFencedHostAddressResolver resolver,
        HttpMessageHandler innerHandler,
        IServiceFencingContextAccessor accessor,
        IServiceFencingEvidenceSink? sink = null,
        ISecurityEvidenceSink? canonicalSink = null)
    {
        var opts = new ServiceFencingOptions();
        var policy = new FencedServicePolicy { ServiceId = serviceId };
        configure(policy);
        opts.RegisterService(policy);

        var handler = new FencedHttpMessageHandler(
            serviceId,
            new StaticOptionsMonitor(opts),
            accessor,
            resolver,
            sink,
            TimeProvider.System,
            canonicalSink)
        {
            InnerHandler = innerHandler
        };
        return new HttpClient(handler);
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : IFencedHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StaticOptionsMonitor(ServiceFencingOptions current) : IOptionsMonitor<ServiceFencingOptions>
    {
        public ServiceFencingOptions CurrentValue => current;
        public ServiceFencingOptions Get(string? name) => current;
        public IDisposable? OnChange(Action<ServiceFencingOptions, string?> listener) => null;
    }

    private sealed class RecordingSecurityEvidenceSink : ISecurityEvidenceSink
    {
        public List<SecurityEvidenceEvent> Events { get; } = new();
        public ValueTask<SecurityEvidenceRecordResult> RecordAsync(SecurityEvidenceEvent evidence, CancellationToken cancellationToken = default)
        {
            Events.Add(evidence);
            return ValueTask.FromResult(new SecurityEvidenceRecordResult(true, false));
        }
    }
}
