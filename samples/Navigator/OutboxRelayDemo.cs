using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;

namespace OpenConnect.Samples.Navigator;

/// <summary>
/// Deterministic outbox-relay scenario: seeds a process-local Navigator event log, relays it
/// into the EFCore outbox, and verifies the relayed entries are pending. No timers or
/// background dispatch in the demo path; the relay is a single explicit pass plus an
/// idempotent re-pass. Rerun-safe: the session id is fresh per invocation and assertions
/// filter pending entries by that session id.
/// </summary>
public static class OutboxRelayDemo
{
    private const string Tenant = "navigator-relay-demo";
    private const string Principal = "relay-demo-principal";

    /// <summary>
    /// Runs the relay scenario. Throws <see cref="InvalidOperationException"/> with an
    /// <c>navigator_relay_demo_failed</c> message on the first unexpected outcome.
    /// </summary>
    public static async Task RunAsync(NavigatorDbContext db, IOutboxStore outbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(outbox);

        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var navigatorStore = new InMemoryDurableNavigatorStore(new InMemoryDurableOperationalStore());
        var sessionId = await navigatorStore.CreateSessionAsync(Tenant, Principal, "relay-demo-client", "relay-demo-route", cancellationToken).ConfigureAwait(false);
        var start = DateTimeOffset.UtcNow;

        var seed = new[]
        {
            new NavigatorEvent("evt-1", "navigator.session.started", sessionId, null, 1, "corr-1", start,
                new Dictionary<string, string> { ["route"] = "relay-demo-route" }),
            new NavigatorEvent("evt-2", "tour.step.completed", sessionId, "task-1", 2, "corr-1", start.AddSeconds(1),
                new Dictionary<string, string> { ["step"] = "2" }),
            new NavigatorEvent("evt-3", "command.issued", sessionId, "task-1", 3, null, start.AddSeconds(2),
                new Dictionary<string, string> { ["command"] = "demo" }),
        };

        long expected = 1;
        foreach (var evt in seed)
        {
            var outcome = await navigatorStore.AppendEventAsync(sessionId, Tenant, Principal, expected, evt, cancellationToken).ConfigureAwait(false);
            if (outcome != NavigatorStoreOutcome.Succeeded)
            {
                throw new InvalidOperationException($"navigator_relay_demo_failed: seeding event {evt.EventId} returned {outcome}.");
            }

            expected++;
        }

        Console.WriteLine($"seeded ok: session={sessionId} events=3");

        var relay = new NavigatorOutboxRelay(navigatorStore, outbox);
        var first = await relay.RelayAsync(sessionId, Tenant, Principal, 0, cancellationToken).ConfigureAwait(false);
        if (first.Relayed != 3 || first.Skipped != 0 || first.LastSequence != 3)
        {
            throw new InvalidOperationException(
                $"navigator_relay_demo_failed: expected (3, 0, 3) but got ({first.Relayed}, {first.Skipped}, {first.LastSequence}).");
        }

        Console.WriteLine($"relay ok: relayed={first.Relayed} skipped={first.Skipped} lastSequence={first.LastSequence}");

        var pending = await outbox.GetPendingAsync(100, cancellationToken).ConfigureAwait(false);
        var ours = pending.Where(e => e.Payload.Contains(sessionId, StringComparison.Ordinal)).ToList();
        if (ours.Count != 3)
        {
            throw new InvalidOperationException($"navigator_relay_demo_failed: expected 3 pending entries for this session, found {ours.Count}.");
        }

        var types = ours.Select(e => e.Type).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"pending ok: {ours.Count} entries ({string.Join(", ", types)})");

        var second = await relay.RelayAsync(sessionId, Tenant, Principal, first.LastSequence, cancellationToken).ConfigureAwait(false);
        if (second.Relayed != 0 || second.LastSequence != first.LastSequence)
        {
            throw new InvalidOperationException(
                $"navigator_relay_demo_failed: re-pass not idempotent (relayed {second.Relayed}, last {second.LastSequence}).");
        }

        Console.WriteLine("idempotent ok: re-pass with cursor relayed 0 events");
        Console.WriteLine("Outbox relay demo complete: all relay assertions held.");
    }
}
