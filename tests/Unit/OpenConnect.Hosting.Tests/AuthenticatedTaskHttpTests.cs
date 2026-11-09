using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class AuthenticatedTaskHttpTests
{
    private const string FixtureScheme = "Fixture";
    private const string TenantA = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string PrincipalA = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string TenantB = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string PrincipalB = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
    private const string MissingTenant = "missing-tenant";
    private const string RawClaimsPrefix = "raw-claims";
    private const string ZeroUlid = "00000000000000000000000000";
    private const string LowercaseTenant = "01arz3ndektsv4rrffq69g5fav";
    private const string OverflowUlid = "Z1ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly JsonSerializerOptions IdentifierJsonOptions = OpenConnectHttpJson.CreateOptions();

    [Fact]
    public async Task Real_http_routes_require_authentication()
    {
        await using var host = await HttpFixture.StartAsync();
        using var response = await host.Client.GetAsync("/openconnect/v1/tasks/not-a-task");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_without_tenant_claim_is_forbidden()
    {
        await using var host = await HttpFixture.StartAsync();
        using var client = host.AuthenticatedClient(MissingTenant, PrincipalA);
        using var response = await client.GetAsync("/openconnect/v1/tasks/01ARZ3NDEKTSV4RRFFQ69G5FAV");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("tenant", ZeroUlid)]
    [InlineData("principal", ZeroUlid)]
    [InlineData("tenant", LowercaseTenant)]
    [InlineData("principal", LowercaseTenant)]
    [InlineData("tenant", OverflowUlid)]
    [InlineData("principal", OverflowUlid)]
    public async Task Noncanonical_or_zero_identity_claims_are_forbidden_before_admission(string claim, string value)
    {
        await using var host = await HttpFixture.StartAsync();
        var tenant = claim == "tenant" ? value : TenantA;
        var principal = claim == "principal" ? value : PrincipalA;
        using var client = host.AuthenticatedClientWithRawClaims(tenant, principal);

        using var taskResponse = await Post(client, "/openconnect/v1/tasks", Json("""
            {"clientTaskId":"invalid-claim","message":"must not be admitted"}
            """), $"invalid-claim-{claim}");
        using var sessionResponse = await client.PostAsync("/openconnect/v1/sessions", Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, taskResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sessionResponse.StatusCode);
        Assert.Equal(0, host.TaskCount);
        Assert.Equal(0, host.SessionCount);
        Assert.Equal(0, host.Executor.Calls);
    }

    [Theory]
    [InlineData(ZeroUlid)]
    [InlineData(LowercaseTenant)]
    [InlineData(OverflowUlid)]
    public async Task Noncanonical_or_zero_session_request_ids_are_rejected_before_admission(string sessionId)
    {
        await using var host = await HttpFixture.StartAsync();
        using var client = host.AuthenticatedClient(TenantA, PrincipalA);
        var body = JsonSerializer.Serialize(new { clientTaskId = "invalid-session", sessionId });

        using var response = await Post(client, "/openconnect/v1/tasks", Json(body), $"invalid-session-{sessionId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.TaskCount);
        Assert.Equal(0, host.SessionCount);
        Assert.Equal(0, host.Executor.Calls);
    }

    [Theory]
    [InlineData(ZeroUlid)]
    [InlineData(LowercaseTenant)]
    [InlineData(OverflowUlid)]
    public async Task Noncanonical_or_zero_task_and_session_path_ids_return_bad_request(string id)
    {
        await using var host = await HttpFixture.StartAsync();
        using var client = host.AuthenticatedClient(TenantA, PrincipalA);

        using var taskResponse = await client.GetAsync($"/openconnect/v1/tasks/{id}");
        using var sessionResponse = await client.GetAsync($"/openconnect/v1/sessions/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, taskResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sessionResponse.StatusCode);
        Assert.Equal(0, host.TaskCount);
        Assert.Equal(0, host.SessionCount);
        Assert.Equal(0, host.Executor.Calls);
    }

    [Fact]
    public async Task Malformed_fixture_credentials_are_unauthorized()
    {
        await using var host = await HttpFixture.StartAsync();
        using var client = host.AuthenticatedClient("not-a-ulid", PrincipalA);
        using var response = await client.GetAsync("/openconnect/v1/tasks/not-a-task");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Claims_own_created_task_and_identity_in_body_cannot_spoof_them()
    {
        await using var host = await HttpFixture.StartAsync();
        using var client = host.AuthenticatedClient(TenantA, PrincipalA);
        using var create = await Post(client, "/openconnect/v1/tasks", Json("""
            {"clientTaskId":"owned-task","message":"hello","tenantId":"01ARZ3NDEKTSV4RRFFQ69G5FAX","principalId":"01ARZ3NDEKTSV4RRFFQ69G5FAY"}
            """), "owned-key");
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        Assert.NotNull(create.Headers.Location);
        var createdJson = await create.Content.ReadAsStringAsync();
        using (var raw = JsonDocument.Parse(createdJson))
            AssertIdentifierPropertiesAreCanonical(raw.RootElement, ("id", null), ("tenantId", TenantA), ("principalId", PrincipalA));
        var created = JsonSerializer.Deserialize<TaskRecord>(createdJson, IdentifierJsonOptions);
        Assert.NotNull(created);
        Assert.Equal($"/openconnect/v1/tasks/{created.Id.Value}", create.Headers.Location!.OriginalString);
        Assert.Equal(Ulid.Parse(TenantA), created.TenantId.Value);
        Assert.Equal(Ulid.Parse(PrincipalA), created.PrincipalId.Value);

        using var read = await client.GetAsync($"/openconnect/v1/tasks/{created.Id.Value}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var fetchedJson = await read.Content.ReadAsStringAsync();
        using (var raw = JsonDocument.Parse(fetchedJson))
            AssertIdentifierPropertiesAreCanonical(raw.RootElement, ("id", null), ("tenantId", TenantA), ("principalId", PrincipalA));
        var fetched = JsonSerializer.Deserialize<TaskRecord>(fetchedJson, IdentifierJsonOptions);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(Ulid.Parse(TenantA), fetched.TenantId.Value);
        Assert.Equal(Ulid.Parse(PrincipalA), fetched.PrincipalId.Value);
    }

    [Theory]
    [InlineData(TenantB, PrincipalA)]
    [InlineData(TenantA, PrincipalB)]
    public async Task Foreign_tenant_or_principal_cannot_read_or_cancel(string tenant, string principal)
    {
        await using var host = await HttpFixture.StartAsync();
        using var owner = host.AuthenticatedClient(TenantA, PrincipalA);
        using var create = await Post(owner, "/openconnect/v1/tasks", Json("""
            {"clientTaskId":"private-task","message":"hold"}
            """), "private-key");
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var record = await create.Content.ReadFromJsonAsync<TaskRecord>(IdentifierJsonOptions);
        Assert.NotNull(record);
        await host.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stranger = host.AuthenticatedClient(tenant, principal);
        using var read = await stranger.GetAsync($"/openconnect/v1/tasks/{record.Id.Value}");
        using var cancel = await stranger.PostAsync($"/openconnect/v1/tasks/{record.Id.Value}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(1, host.Executor.Calls);
    }

    [Fact]
    public async Task Foreign_session_is_rejected_before_executor_is_called()
    {
        await using var host = await HttpFixture.StartAsync();
        using var owner = host.AuthenticatedClient(TenantB, PrincipalB);
        using var sessionResponse = await owner.PostAsync("/openconnect/v1/sessions", Json("{}"));
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var sessionJson = await sessionResponse.Content.ReadAsStringAsync();
        using (var raw = JsonDocument.Parse(sessionJson))
            AssertIdentifierPropertiesAreCanonical(raw.RootElement, ("id", null), ("tenantId", TenantB), ("principalId", PrincipalB));
        var session = JsonSerializer.Deserialize<SessionRecord>(sessionJson, IdentifierJsonOptions);
        Assert.NotNull(session);

        using var caller = host.AuthenticatedClient(TenantA, PrincipalA);
        using var response = await Post(caller, "/openconnect/v1/tasks", Json(JsonSerializer.Serialize(new
        {
            clientTaskId = "foreign-session",
            message = "must not run",
            sessionId = session.Id.Value.ToString()
        })), "foreign-session-key");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Executor.Calls);
    }

    [Fact]
    public async Task Session_create_and_get_preserve_created_location_and_emit_string_identifiers()
    {
        await using var host = await HttpFixture.StartAsync();
        using var client = host.AuthenticatedClient(TenantA, PrincipalA);
        using var created = await client.PostAsync("/openconnect/v1/sessions", Json("{}"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var createdJson = await created.Content.ReadAsStringAsync();
        using (var raw = JsonDocument.Parse(createdJson))
            AssertIdentifierPropertiesAreCanonical(raw.RootElement, ("id", null), ("tenantId", TenantA), ("principalId", PrincipalA));
        var session = JsonSerializer.Deserialize<SessionRecord>(createdJson, IdentifierJsonOptions);
        Assert.NotNull(session);

        using var fetched = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var fetchedJson = await fetched.Content.ReadAsStringAsync();
        using (var raw = JsonDocument.Parse(fetchedJson))
            AssertIdentifierPropertiesAreCanonical(raw.RootElement, ("id", session.Id.Value.ToString()), ("tenantId", TenantA), ("principalId", PrincipalA));
        Assert.Equal(session, JsonSerializer.Deserialize<SessionRecord>(fetchedJson, IdentifierJsonOptions));
    }

    private static void AssertIdentifierPropertiesAreCanonical(JsonElement body, params (string Name, string? Expected)[] identifiers)
    {
        foreach (var (name, expected) in identifiers)
        {
            Assert.True(body.TryGetProperty(name, out var property), $"Expected {name} in the raw JSON response.");
            Assert.Equal(JsonValueKind.String, property.ValueKind);
            var text = property.GetString();
            Assert.NotNull(text);
            Assert.Equal(26, text.Length);
            Assert.Equal(text, Ulid.Parse(text).ToString());
            Assert.NotEqual(default, Ulid.Parse(text));
            if (expected is not null) Assert.Equal(expected, text);
        }
    }

    [Fact]
    public async Task Missing_executor_returns_service_unavailable()
    {
        await using var host = await HttpFixture.StartAsync(registerExecutor: false);
        using var client = host.AuthenticatedClient(TenantA, PrincipalA);
        using var response = await Post(client, "/openconnect/v1/tasks", Json("""
            {"clientTaskId":"no-executor","message":"hello"}
            """), "no-executor-key");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, HttpContent content, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private sealed class HttpFixture : IAsyncDisposable
    {
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
        private readonly WebApplication _app;
        public HttpClient Client { get; }
        public RecordingExecutor Executor { get; }
        public int TaskCount => _app.Services.GetRequiredService<InMemoryTaskStore>().All().Count;
        public int SessionCount => _app.Services.GetRequiredService<InMemorySessionStore>().Count;

        private HttpFixture(WebApplication app, HttpClient client, RecordingExecutor executor)
        {
            _app = app;
            Client = client;
            Executor = executor;
        }

        private static async Task StopBoundedAsync(WebApplication app)
        {
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            await app.StopAsync(timeout.Token);
        }

        public static async Task<HttpFixture> StartAsync(bool registerExecutor = true)
        {
            var executor = new RecordingExecutor();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication(FixtureScheme).AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>(FixtureScheme, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddOpenConnectTaskExecution(options => { options.Capacity = 8; options.WorkerConcurrency = 1; });
            builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, AllowAuthorizer>();
            if (registerExecutor) builder.Services.AddSingleton<IHostTaskExecutor>(executor);

            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapOpenConnect();
                await app.StartAsync();
                var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                    .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                    .Addresses.Single();
                return new HttpFixture(app, new HttpClient { BaseAddress = new Uri(address) }, executor);
            }
            catch
            {
                executor.Release();
                try { await StopBoundedAsync(app); }
                finally { await app.DisposeAsync(); }
                throw;
            }
        }

        public HttpClient AuthenticatedClient(string tenant, string principal)
        {
            var client = new HttpClient { BaseAddress = Client.BaseAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(FixtureScheme, $"{tenant}:{principal}");
            return client;
        }

        public HttpClient AuthenticatedClientWithRawClaims(string tenant, string principal)
        {
            var client = new HttpClient { BaseAddress = Client.BaseAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(FixtureScheme, $"{RawClaimsPrefix}:{tenant}:{principal}");
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Executor.Release();
            try { await StopBoundedAsync(_app); }
            finally { await _app.DisposeAsync(); }
        }
    }

    private sealed class FixtureAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public FixtureAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        private static bool IsUlid(string text)
        {
            try { _ = Ulid.Parse(text); return true; }
            catch (FormatException) { return false; }
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var value = Request.Headers.Authorization.ToString();
            if (!AuthenticationHeaderValue.TryParse(value, out var header) || header.Scheme != FixtureScheme || header.Parameter is null)
                return Task.FromResult(AuthenticateResult.NoResult());

            var parts = header.Parameter.Split(':', StringSplitOptions.None);
            if (parts.Length == 3 && parts[0] == RawClaimsPrefix)
            {
                // Test-only hook: authenticate deliberately invalid claim values so production route validation is exercised.
                var rawClaimsIdentity = new ClaimsIdentity(
                    [new Claim("tenant_id", parts[1]), new Claim(ClaimTypes.NameIdentifier, parts[2])], FixtureScheme);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(rawClaimsIdentity), FixtureScheme)));
            }

            if (parts.Length != 2 || !IsUlid(parts[1]))
                return Task.FromResult(AuthenticateResult.Fail("Invalid fixture credentials."));

            // This explicit test credential authenticates the principal but deliberately omits tenant_id.
            if (parts[0] == MissingTenant)
            {
                var principalOnly = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, parts[1])], FixtureScheme);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(principalOnly), FixtureScheme)));
            }

            if (!IsUlid(parts[0]))
                return Task.FromResult(AuthenticateResult.Fail("Invalid fixture credentials."));

            var identity = new ClaimsIdentity([new Claim("tenant_id", parts[0]), new Claim(ClaimTypes.NameIdentifier, parts[1])], FixtureScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), FixtureScheme)));
        }
    }

    private sealed class AllowAuthorizer : IHostTaskExecutionAuthorizer
    {
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingExecutor : IHostTaskExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public void Release() => _release.TrySetResult();

        public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
