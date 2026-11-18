using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class LocalAcceptanceGuardTests
{
    private const string Host = "127.0.0.1:42123";
    private const string Origin = "http://127.0.0.1:42123";

    [Fact]
    public async Task OptIn_requires_development_and_valid_configuration()
    {
        var disabled = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        await using (var app = disabled.Build())
            LocalAcceptanceGuard.UseIfEnabled(app);

        var production = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        production.Configuration["OpenConnect:LocalAcceptance:Enabled"] = "true";
        await using (var app = production.Build())
            Assert.Throws<InvalidOperationException>(() => LocalAcceptanceGuard.UseIfEnabled(app));

        var development = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        development.Configuration["OpenConnect:LocalAcceptance:Enabled"] = "true";
        await using (var app = development.Build())
            Assert.Throws<InvalidOperationException>(() => LocalAcceptanceGuard.UseIfEnabled(app));
    }

    [Fact]
    public async Task Loopback_listener_connection_host_and_origin_are_required()
    {
        var secret = Secret();
        Assert.Equal(204, await Invoke(secret));
        Assert.Equal(403, await Invoke(secret, local: IPAddress.Any));
        Assert.Equal(403, await Invoke(secret, remote: IPAddress.Parse("192.0.2.1")));
        Assert.Equal(403, await Invoke(secret, host: "localhost:42123"));
        Assert.Equal(403, await Invoke(secret, origin: "http://localhost:42123"));
        Assert.Equal(403, await Invoke(secret, address: "http://0.0.0.0:42123"));
        Assert.Equal(403, await Invoke(secret, address: null));
    }

    [Fact]
    public async Task Duplicate_or_comma_joined_security_headers_are_rejected()
    {
        var secret = Secret();
        Assert.Equal(403, await Invoke(secret, hostValues: new StringValues([Host, Host])));
        Assert.Equal(403, await Invoke(secret, originValues: new StringValues([Origin, Origin])));
        Assert.Equal(403, await Invoke(secret, authorization: new StringValues([$"Bearer {secret}", $"Bearer {secret}"])));
        Assert.Equal(403, await Invoke(secret, origin: Origin + "," + Origin));
        Assert.Equal(403, await Invoke(secret, path: "/internal/openconnect/todos", guard: new StringValues([secret, secret])));
    }

    [Fact]
    public async Task Unsafe_requests_without_origin_need_valid_guard_credential()
    {
        var secret = Secret();
        Assert.Equal(403, await Invoke(secret, method: "POST", origin: null));
        Assert.Equal(403, await Invoke(secret, method: "POST", origin: null, authorization: $"Bearer {Secret()}"));
        Assert.Equal(204, await Invoke(secret, method: "POST", origin: null, authorization: $"Bearer {secret}"));
        Assert.Equal(204, await Invoke(secret, method: "GET", origin: null));
        Assert.Equal(403, await Invoke(secret, method: "CONNECT", origin: null));
    }

    [Fact]
    public async Task Todo_guard_header_is_separate_from_todo_authorization()
    {
        var guard = Secret();
        var todoToken = Secret();
        const string path = "/internal/openconnect/todos";
        Assert.Equal(403, await Invoke(guard, path: path, authorization: $"Bearer {guard}"));
        Assert.Equal(403, await Invoke(guard, path: path, guard: todoToken, authorization: $"Bearer {todoToken}"));
        Assert.Equal(204, await Invoke(guard, path: path, method: "POST", origin: null,
            guard: guard, authorization: $"Bearer {todoToken}"));
        Assert.Equal(403, await Invoke(guard, guard: guard));
        // The downstream Todo endpoint, not this middleware, validates its bearer and tenant.
    }

    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<int> Invoke(
        string secret, string path = "/", string method = "GET", string? origin = Origin,
        StringValues? authorization = null, StringValues? guard = null,
        IPAddress? local = null, IPAddress? remote = null, string host = Host,
        StringValues? hostValues = null, StringValues? originValues = null,
        string? address = "http://127.0.0.1:42123")
    {
        var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        var addresses = new ServerAddressesFeature();
        if (address is not null) addresses.Addresses.Add(address);
        features.Set<IServerAddressesFeature>(addresses);
        var server = new StubServer(features);
        RequestDelegate next = context => { context.Response.StatusCode = 204; return Task.CompletedTask; };
        var constructor = typeof(LocalAcceptanceGuard).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var instance = constructor.Invoke([next, Origin, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Host },
            SHA256.HashData(Encoding.ASCII.GetBytes(secret)), server]);
        var invoke = typeof(LocalAcceptanceGuard).GetMethod("InvokeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = local ?? IPAddress.Loopback;
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Headers.Host = hostValues ?? host;
        if (originValues.HasValue) context.Request.Headers.Origin = originValues.Value;
        else if (origin is not null) context.Request.Headers.Origin = origin;
        if (authorization.HasValue) context.Request.Headers.Authorization = authorization.Value;
        if (guard.HasValue) context.Request.Headers["X-OpenConnect-Local-Guard"] = guard.Value;
        await (Task)invoke.Invoke(instance, [context])!;
        return context.Response.StatusCode;
    }

    private sealed class StubServer(Microsoft.AspNetCore.Http.Features.IFeatureCollection features) : IServer
    {
        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = features;
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
