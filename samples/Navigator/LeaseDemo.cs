using OpenConnect.Persistence.EFCore.Navigator;

namespace OpenConnect.Samples.Navigator;

/// <summary>
/// Deterministic lease-fencing scenario against the hardened seam. All clock values are
/// derived arithmetically from a single captured instant: no timers, sleeps, or races in
/// the demo path. Execution keys are unique per invocation (fixed tenant GUID plus a fresh
/// run suffix) so the verb is rerun-safe against its own SQLite file. Assertions are
/// relational on fencing tokens (monotonic +1), never on absolute values.
/// </summary>
public static class LeaseDemo
{
    private const string TenantGuid = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Runs the acquire/conflict/expire/renew/release scenario. Throws
    /// <see cref="InvalidOperationException"/> with a <c>navigator_lease_demo_failed</c>
    /// message on the first unexpected outcome.
    /// </summary>
    public static async Task RunAsync(NavigatorDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var adapter = new NavigatorLeaseAdapter(new EfNavigatorLeaseSeam<NavigatorDbContext>(db));
        var runId = Guid.NewGuid().ToString("N");
        var executionA = $"{TenantGuid}:lease-demo-{runId}";
        var executionB = $"{TenantGuid}:expire-reclaim-{runId}";
        var start = DateTimeOffset.UtcNow;

        // 1. Acquire: first holder wins with the initial fencing token.
        var first = await adapter.AcquireAsync(executionA, "owner-a", TimeSpan.FromMinutes(10), start, cancellationToken).ConfigureAwait(false);
        Check(first is not null, "initial acquire returned null");
        Console.WriteLine($"acquire ok: execution={executionA} owner=owner-a token={first!.FencingToken} lease={first.LeaseId}");

        // 2. Conflict: a second owner while the lease is live fails closed.
        var conflict = await adapter.AcquireAsync(executionA, "owner-b", TimeSpan.FromMinutes(10), start, cancellationToken).ConfigureAwait(false);
        Check(conflict is null, "conflicting acquire unexpectedly succeeded");
        Console.WriteLine("conflict ok: live holder blocked second acquire (null)");

        // 3. Currency: the held lease is the current fence.
        Check(await adapter.IsCurrentAsync(first, start, cancellationToken).ConfigureAwait(false), "held lease not current");
        Console.WriteLine("is-current ok: held lease is current");

        // 4. Renew: matching token + unexpired advances the fence; LeaseId is retained.
        var renewed = await adapter.RenewAsync(first, TimeSpan.FromMinutes(10), start.AddMinutes(1), cancellationToken).ConfigureAwait(false);
        Check(renewed is not null, "renew of live lease returned null");
        Check(renewed!.FencingToken == first.FencingToken + 1, $"renew token {renewed.FencingToken} != prior+1");
        Check(string.Equals(renewed.LeaseId, first.LeaseId, StringComparison.Ordinal), "renew changed LeaseId");
        Console.WriteLine($"renew ok: token={renewed.FencingToken} lease retained");

        // 5. Stale fence: renewing with the superseded token fails closed.
        var stale = await adapter.RenewAsync(first, TimeSpan.FromMinutes(10), start.AddMinutes(1), cancellationToken).ConfigureAwait(false);
        Check(stale is null, "stale-token renew unexpectedly succeeded");
        Console.WriteLine("stale-fence ok: superseded token renew returned null");

        // 6. Release: the fence is retired; the released lease is no longer current.
        await adapter.ReleaseAsync(renewed, cancellationToken).ConfigureAwait(false);
        Check(!await adapter.IsCurrentAsync(renewed, start.AddMinutes(2), cancellationToken).ConfigureAwait(false), "released lease still current");
        Console.WriteLine("release ok: released lease is no longer current");

        // 7. Re-acquire after release: the retained row advances the fence (no token reuse).
        var afterRelease = await adapter.AcquireAsync(executionA, "owner-c", TimeSpan.FromMinutes(10), start.AddMinutes(2), cancellationToken).ConfigureAwait(false);
        Check(afterRelease is not null, "acquire after release returned null");
        Check(afterRelease!.FencingToken == renewed.FencingToken + 1, $"post-release token {afterRelease.FencingToken} != released+1");
        Check(string.Equals(afterRelease.LeaseId, renewed.LeaseId, StringComparison.Ordinal), "post-release acquire did not retain the row");
        Console.WriteLine($"re-acquire ok: token={afterRelease.FencingToken} row retained");

        // 8. Expire + reclaim: a takeover past expiry advances the fence under a new owner.
        var shortLived = await adapter.AcquireAsync(executionB, "owner-a", TimeSpan.FromMinutes(5), start, cancellationToken).ConfigureAwait(false);
        Check(shortLived is not null, "expire-reclaim initial acquire returned null");
        var reclaimed = await adapter.AcquireAsync(executionB, "owner-b", TimeSpan.FromMinutes(5), start.AddMinutes(6), cancellationToken).ConfigureAwait(false);
        Check(reclaimed is not null, "expired takeover returned null");
        Check(reclaimed!.FencingToken == shortLived!.FencingToken + 1, $"takeover token {reclaimed.FencingToken} != prior+1");
        Console.WriteLine($"expire-reclaim ok: token={reclaimed.FencingToken} owner=owner-b");

        // 9. Fail-closed input: malformed execution id never touches the store.
        var invalid = await adapter.AcquireAsync("not-an-execution-id", "owner-a", TimeSpan.FromMinutes(5), start, cancellationToken).ConfigureAwait(false);
        Check(invalid is null, "malformed execution id unexpectedly acquired");
        Console.WriteLine("fail-closed ok: malformed execution id returned null");

        Console.WriteLine("Lease fencing demo complete: all fencing assertions held.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"navigator_lease_demo_failed: {message}.");
        }
    }
}
