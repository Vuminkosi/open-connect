using System.Net;
using System.Text.Json;
using Xunit;
using OpenConnect.Navigator;
using OpenConnect.Navigator.OperationalBrowser;

namespace OpenConnect.Navigator.OperationalBrowser.Tests;

public sealed class BrowserBoundaryTests
{
    private static readonly Uri Origin = new("https://example.com/");
    private sealed class Resolver(IPAddress address) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { address });
    }

    private sealed class SequenceResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        private int _index;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { addresses[Math.Min(_index++, addresses.Length - 1)] });
    }

    private sealed class RecordingTerminator(List<string> events, string profile) : IBrowserSessionTerminator
    {
        public Task TerminateAsync(IsolatedBrowserSession session, CancellationToken cancellationToken)
        {
            Assert.True(Directory.Exists(profile));
            events.Add("terminate");
            return Task.CompletedTask;
        }
    }

    private static BrowserJobSpec Job(params Uri[] origins) => new("exec", "tenant", "principal", origins,
        "rev1", DateTimeOffset.UtcNow.AddMinutes(1), "idem", new BrowserResourceBudget(10_000, 5, 1024));

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.4")]
    public async Task Denies_metadata_and_private_addresses(string ip)
    {
        var guard = new EgressGuard(new Resolver(IPAddress.Parse(ip)));
        Assert.False(await guard.IsAllowedAsync(Origin, new[] { Origin }));
    }

    [Fact]
    public async Task Denies_redirect_escape_origin()
    {
        var guard = new EgressGuard(new Resolver(IPAddress.Parse("93.184.216.34")));
        Assert.False(await guard.IsAllowedAsync(new Uri("https://evil.example/"), new[] { Origin }));
    }

    [Fact]
    public async Task Validates_every_redirect_hop_and_bounds_chain()
    {
        var guard = new EgressGuard(new Resolver(IPAddress.Parse("93.184.216.34")));
        Assert.Null(await guard.ValidateRedirectChainAsync(Origin,
            new[] { new Uri("http://example.com/"), Origin }, new[] { Origin }));
        Assert.Null(await guard.ValidateRedirectChainAsync(Origin,
            Enumerable.Repeat(Origin, EgressGuard.MaxRedirectHops + 1).ToArray(), new[] { Origin }));
        var chain = await guard.ValidateRedirectChainAsync(Origin, new[] { new Uri("https://example.com/next") }, new[] { Origin });
        Assert.Equal(2, chain!.Count);
        Assert.Single(chain[0].Addresses);
    }

    [Fact]
    public async Task Denies_dns_rebinding_on_redirect_hop()
    {
        var guard = new EgressGuard(new SequenceResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("169.254.169.254")));
        Assert.Null(await guard.ValidateRedirectChainAsync(Origin, new[] { new Uri("https://example.com/redirected") }, new[] { Origin }));
    }

    [Fact]
    public async Task Creates_unique_profiles_and_cleanup_deletes_directory()
    {
        var factory = new IsolatedBrowserSessionFactory();
        var first = factory.Create(Job(Origin));
        var second = factory.Create(Job(Origin));
        Assert.NotEqual(first.ProfileDir, second.ProfileDir);
        Assert.NotEqual(first.SessionId, second.SessionId);
        var receipt = await factory.CleanupAsync(Job(Origin), first);
        Assert.True(receipt.ProfileDeleted);
        Assert.False(Directory.Exists(first.ProfileDir));
        await factory.CleanupAsync(Job(Origin), second);
    }

    [Fact]
    public async Task Close_terminates_before_cleanup_and_alerts_on_failed_cleanup()
    {
        var factory = new IsolatedBrowserSessionFactory();
        var job = Job(Origin);
        var session = factory.Create(job);
        var events = new List<string>();
        var receipt = await factory.CloseAsync(job, session, new RecordingTerminator(events, session.ProfileDir));
        events.Add(receipt.ProfileDeleted ? "cleanup" : "cleanup-failed");
        Assert.Equal(new[] { "terminate", "cleanup" }, events);
        Assert.True(receipt.ProfileDeleted);
        Assert.False(receipt.CleanupRequiresOperatorAlert);

        var invalid = session with { ProfileDir = Path.GetTempPath() };
        var failed = await factory.CleanupAsync(job, invalid);
        Assert.False(failed.ProfileDeleted);
        Assert.True(failed.CleanupRequiresOperatorAlert);
    }

    [Fact]
    public void Tool_escape_is_denied()
    {
        using var args = JsonDocument.Parse("{}");
        Assert.False(BrowserJobBoundary.IsAllowedTool((NavigatorActionType)999, args.RootElement, Origin,
            false, out _, out _, out _));
    }

    [Fact]
    public async Task Enforces_budget_and_rejects_non_https_allowlist()
    {
        var boundary = new BrowserJobBoundary(new EgressGuard(new Resolver(IPAddress.Parse("93.184.216.34"))));
        Assert.Equal("invalid_job", (await boundary.ValidateAsync(Job(Origin), Origin, 5, 0)).DenialReason);
        Assert.Equal("invalid_origin_policy", (await boundary.ValidateAsync(Job(new Uri("http://example.com/")), Origin, 0, 0)).DenialReason);
    }
}
