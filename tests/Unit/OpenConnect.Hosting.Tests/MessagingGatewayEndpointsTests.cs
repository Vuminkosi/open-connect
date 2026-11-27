using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Messaging;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public sealed class MessagingGatewayEndpointsTests
{
    [Fact]
    public void MapOpenConnectMessaging_IsIdempotentAndAppliesAuthPolicyCorrectly()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        services.AddOpenConnect();
        services.AddOpenConnectTaskExecution();
        services.AddOpenConnectMessagingHosting();

        using var sp = services.BuildServiceProvider();
        var builder = new TestEndpointRouteBuilder(sp);

        builder.MapOpenConnect();
        builder.MapOpenConnectMessaging();
        builder.MapOpenConnect();

        var endpoints = builder.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToArray();

        // Protected routes require authorization
        var compat = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/openconnect/v1/messaging/compatibility");
        Assert.NotEmpty(compat.Metadata.GetOrderedMetadata<IAuthorizeData>());

        var discover = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/openconnect/v1/agents/discover");
        Assert.NotEmpty(discover.Metadata.GetOrderedMetadata<IAuthorizeData>());

        var revoke = Assert.Single(endpoints, e =>
            e.RoutePattern.RawText == "/openconnect/v1/devices/{id}" &&
            e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);
        Assert.NotEmpty(revoke.Metadata.GetOrderedMetadata<IAuthorizeData>());

        // Narrowly scoped enrollment redeem/complete/refresh endpoints allow anonymous (credential-scoped)
        var redeem = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/openconnect/v1/devices/enrollment/redeem");
        Assert.NotEmpty(redeem.Metadata.GetOrderedMetadata<IAllowAnonymous>());

        var refresh = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/openconnect/v1/devices/token/refresh");
        Assert.NotEmpty(refresh.Metadata.GetOrderedMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void MessagingHostingOptions_DefaultDisabledAndRequireEncryption()
    {
        var options = new MessagingHostingOptions();
        Assert.False(options.Enabled);
        Assert.True(options.RequireEncryption);
        Assert.False(options.AllowUnencryptedFallback);
    }

    [Fact]
    public void CursorCodec_EnforcesScopeTenantPrincipalDeviceAndIntegrity()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(i + 1);

        var codec = new MessagingCursorCodec(Options.Create(new MessagingHostingOptions { CursorSigningKey = key }));
        var tenantA = new TenantId(Ulid.NewUlid());
        var tenantB = new TenantId(Ulid.NewUlid());
        var principalA = new PrincipalId(Ulid.NewUlid());
        var principalB = new PrincipalId(Ulid.NewUlid());
        var deviceA = new DeviceId(Ulid.NewUlid());
        var deviceB = new DeviceId(Ulid.NewUlid());

        var cursor = codec.Encode(tenantA, principalA, deviceA, "events", 42);

        // Valid decode
        Assert.True(codec.TryDecode(cursor, tenantA, principalA, deviceA, "events", out var seq));
        Assert.Equal(42, seq);

        // Cross-tenant rejected
        Assert.False(codec.TryDecode(cursor, tenantB, principalA, deviceA, "events", out _));
        // Cross-principal rejected
        Assert.False(codec.TryDecode(cursor, tenantA, principalB, deviceA, "events", out _));
        // Cross-device rejected
        Assert.False(codec.TryDecode(cursor, tenantA, principalA, deviceB, "events", out _));
        // Cross-scope rejected
        Assert.False(codec.TryDecode(cursor, tenantA, principalA, deviceA, "conv:other", out _));
        // Tampered signature rejected
        Assert.False(codec.TryDecode(cursor + "x", tenantA, principalA, deviceA, "events", out _));
    }

    [Fact]
    public async Task VoiceAdmissionBroker_IssuesShortLivedScopedAdmissionBoundToSession()
    {
        var store = new InMemorySessionStore();
        var broker = new DefaultMessagingVoiceAdmissionBroker(store);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var device = new DeviceId(Ulid.NewUlid());
        var call = new CallId(Ulid.NewUlid());

        var admission = await broker.IssueAdmissionAsync(tenant, principal, device, call);

        Assert.NotEmpty(admission.AdmissionToken);
        Assert.NotEmpty(admission.AdmissionTokenHash);
        Assert.NotEqual(admission.AdmissionToken, admission.AdmissionTokenHash);
        var session = store.Get(admission.VoiceSessionId.Value);
        Assert.NotNull(session);
        Assert.Equal(tenant, session!.TenantId);
        Assert.Equal(principal, session.PrincipalId);
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
    }
}
