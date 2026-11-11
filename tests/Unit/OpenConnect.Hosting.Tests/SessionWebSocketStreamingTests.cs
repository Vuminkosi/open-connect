using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore.Streaming;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class SessionWebSocketStreamingTests
{
    private const string FixtureScheme = "SessionWsFixture";
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string OtherPrincipal = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Fact]
    public async Task WebSocket_streams_historical_and_live_events_with_cursor_resume()
    {
        await using var server = await HostFixture.StartAsync();
        var sessionStore = server.App.Services.GetRequiredService<InMemorySessionStore>();
        var tenantId = new TenantId(Ulid.Parse(Tenant));
        var principalId = new PrincipalId(Ulid.Parse(Principal));
        var session = sessionStore.Create(tenantId, principalId);

        // Pre-seed event 1 and 2
        await sessionStore.AppendEventAsync(session.Id.Value, "task.accepted", """{"taskId":"task-1"}""");
        await sessionStore.AppendEventAsync(session.Id.Value, "tool.started", """{"toolId":"search"}""");

        // Connect with afterSequence = 1 (should only receive event 2 and subsequent live events)
        using var client = await server.ConnectWebSocketAsync(session.Id.Value.ToString(), afterSequence: 1, Tenant, Principal);

        // Read event 2
        var frame1 = await ReceiveJsonAsync(client);
        Assert.NotNull(frame1);
        Assert.Equal(2, frame1.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("tool.started", frame1.RootElement.GetProperty("type").GetString());

        // Emit live event 3
        await sessionStore.AppendEventAsync(session.Id.Value, "tool.completed", """{"result":"ok"}""");

        // Read event 3
        var frame2 = await ReceiveJsonAsync(client);
        Assert.NotNull(frame2);
        Assert.Equal(3, frame2.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("tool.completed", frame2.RootElement.GetProperty("type").GetString());

        // Graceful close
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.Equal(WebSocketState.Closed, client.State);
    }

    [Fact]
    public async Task WebSocket_supports_canonical_uuid_and_emits_canonical_agent_events()
    {
        await using var server = await HostFixture.StartAsync();
        var sessionStore = server.App.Services.GetRequiredService<InMemorySessionStore>();
        var tenantId = new TenantId(Ulid.Parse(Tenant));
        var principalId = new PrincipalId(Ulid.Parse(Principal));
        var session = sessionStore.Create(tenantId, principalId);

        var sessionUuid = Guid.Parse("987e6543-e21b-42d3-a456-426614174001");
        var resolver = server.App.Services.GetRequiredService<InMemorySessionUuidResolver>();
        resolver.Register(sessionUuid, session.Id.Value);

        var eventUuid = Guid.Parse("987e6543-e21b-42d3-a456-426614174010");
        var eventData = $$"""{"eventId":"{{eventUuid:D}}","occurredAt":"2026-03-01T12:00:00Z","step":1}""";
        await sessionStore.AppendEventAsync(session.Id.Value, "agent.step", eventData);

        using var client = await server.ConnectWebSocketAsync(sessionUuid.ToString("D"), afterSequence: 0, Tenant, Principal);

        var frame = await ReceiveJsonAsync(client);
        Assert.NotNull(frame);
        Assert.Equal("openconnect.v1", frame.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(eventUuid.ToString("D"), frame.RootElement.GetProperty("eventId").GetString());
        Assert.Equal(sessionUuid.ToString("D"), frame.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("agent.step", frame.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(1, frame.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("2026-03-01T12:00:00Z", frame.RootElement.GetProperty("occurredAt").GetString());

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task WebSocket_rejects_unauthorized_origin_with_403()
    {
        await using var server = await HostFixture.StartAsync(options =>
        {
            options.AllowedOrigins = new List<string> { "https://trusted.example.test" };
        });
        var sessionStore = server.App.Services.GetRequiredService<InMemorySessionStore>();
        var session = sessionStore.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        // Untrusted origin
        var wsUri = server.GetWebSocketUri(session.Id.Value.ToString(), 0);
        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("openconnect.v1");
        client.Options.SetRequestHeader("Origin", "https://malicious.attacker.test");
        client.Options.SetRequestHeader("Authorization", $"{FixtureScheme} {Tenant}:{Principal}");

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(wsUri, CancellationToken.None));
        // HTTP 403 will cause handshake failure
    }

    [Fact]
    public async Task WebSocket_conceals_foreign_tenant_session_with_404()
    {
        await using var server = await HostFixture.StartAsync();
        var sessionStore = server.App.Services.GetRequiredService<InMemorySessionStore>();
        var session = sessionStore.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        // Connect with other principal/tenant
        var wsUri = server.GetWebSocketUri(session.Id.Value.ToString(), 0);
        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol("openconnect.v1");
        client.Options.SetRequestHeader("Authorization", $"{FixtureScheme} {Tenant}:{OtherPrincipal}");

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(wsUri, CancellationToken.None));
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket client)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[2048];
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var utf8 = ms.ToArray();
        return JsonDocument.Parse(utf8);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        public WebApplication App { get; }
        public Uri BaseUri { get; }

        private HostFixture(WebApplication app, Uri baseUri)
        {
            App = app;
            BaseUri = baseUri;
        }

        public static async Task<HostFixture> StartAsync(Action<SessionWebSocketOptions>? configureOptions = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(opt => opt.Listen(System.Net.IPAddress.Loopback, 0));
            builder.Services.AddAuthentication(FixtureScheme).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(FixtureScheme, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<InMemorySessionStore>();
            builder.Services.AddSingleton<InMemoryTaskStore>();
            builder.Services.AddSingleton<InMemorySessionUuidResolver>();
            builder.Services.AddSingleton<ISessionUuidResolver>(sp => sp.GetRequiredService<InMemorySessionUuidResolver>());

            if (configureOptions is not null)
            {
                builder.Services.Configure(configureOptions);
            }

            var app = builder.Build();
            app.UseWebSockets();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapOpenConnect();

            await app.StartAsync();
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.Single();

            return new HostFixture(app, new Uri(address));
        }

        public Uri GetWebSocketUri(string sessionId, long afterSequence)
        {
            var wsBase = BaseUri.ToString().Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/');
            return new Uri($"{wsBase}/openconnect/v1/sessions/{sessionId}/events?afterSequence={afterSequence}");
        }

        public async Task<ClientWebSocket> ConnectWebSocketAsync(string sessionId, long afterSequence, string tenant, string principal)
        {
            var wsUri = GetWebSocketUri(sessionId, afterSequence);
            var client = new ClientWebSocket();
            client.Options.AddSubProtocol("openconnect.v1");
            client.Options.SetRequestHeader("Authorization", $"{FixtureScheme} {tenant}:{principal}");
            await client.ConnectAsync(wsUri, CancellationToken.None);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await App.StopAsync(cts.Token); }
            finally { await App.DisposeAsync(); }
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var auth = Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith(FixtureScheme + " ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());

            var token = auth[(FixtureScheme.Length + 1)..].Trim();
            var parts = token.Split(':');
            if (parts.Length != 2) return Task.FromResult(AuthenticateResult.Fail("Malformed token."));

            var claims = new[]
            {
                new Claim("tenant_id", parts[0]),
                new Claim(ClaimTypes.NameIdentifier, parts[1]),
                new Claim("sub", parts[1])
            };
            var identity = new ClaimsIdentity(claims, FixtureScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), FixtureScheme)));
        }
    }
}
