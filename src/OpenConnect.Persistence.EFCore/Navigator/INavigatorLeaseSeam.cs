// Derived lease-seam contract for the EF Core Navigator adapter (TASK PERSIST-04-NAV-SEAM).
//
// Source semantics (read-only derivation, no edits to the sources):
//   - OpenConnect.Navigator.Operational.Durable.IDurableOperationalStore (lease surface:
//     TryAcquireLeaseAsync / RenewLeaseAsync / IsLeaseCurrentAsync / ReleaseLeaseAsync).
//   - src/OpenConnect.Navigator.Persistence.Postgres/PostgresDurableNavigatorStore.cs
//     (TryAcquireLeaseAsync lines ~214-230, RenewLeaseAsync ~232-250, IsLeaseCurrentAsync
//     ~252-253, ReleaseLeaseAsync ~255-267, TryExecutionTenant ~294-300).
//
// Derived state machine (acquire / conflict / expire / renew / release / fencing-token):
//   - executionId MUST have the form "<uuid-tenant>:<local>" (separator index > 0, prefix
//     parses as Guid). Anything else is invalid input and fails closed (null), mirroring
//     TryExecutionTenant + TryAcquireLeaseAsync's tenant guard.
//   - acquire: invalid input (duration <= 0, blank owner, malformed executionId) -> null.
//     No row -> insert with fencing token 1. Live row (expires > now) -> null (conflict).
//     Expired row -> takeover: owner/expiry replaced, fencing token = prior + 1 (checked).
//     The fencing token is monotonic per execution and is the cross-process authority.
//   - renew: the stored row must exist with FencingVersion == lease token AND unexpired.
//     Success advances the token (checked +1) and returns a lease with the SAME LeaseId,
//     the NEW token, and expiry = now + duration. Anything else -> null. Owner is NOT
//     checked (matches the Postgres implementation: the fencing token is the authority).
//   - is-current: stored FencingVersion == token AND expires > now. LeaseId is provenance
//     only and is not part of the fence (matches Postgres, where LeaseId == ExecutionId).
//   - release: the row matching (tenant, executionId, token) is retained and its expiry is
//     set to the minimum instant so the next acquire advances the fencing token (matches
//     Postgres ReleaseLeaseAsync). Unknown/stale tokens are a no-op (idempotent).
//   - fail-closed: every semantic failure returns null/false; nothing throws except
//     cancellation and null-argument guards.
//
// Why this contract lives here: the approved owned path
// src/OpenConnect.Navigator.Abstractions/** requires the OpenConnect.Navigator.Abstractions
// project, which does NOT exist in this checkout (verified 2026-11-09: no such project under
// src/; Navigator surface lives in OpenConnect.Navigator + OpenConnect.Navigator.Operational).
// Per contract that interface portion STOPs. This seam therefore mirrors the
// IDurableOperationalStore lease surface with BCL-only types so the EF Core adapter needs NO
// new project reference (OpenConnect.Persistence.EFCore must keep referencing only
// OpenConnect.Abstractions). Promoting this contract to a shared Abstractions project is an
// explicit orchestrator follow-up; the shape is kept assignment-compatible on purpose.
//
// NOT VERIFIED (must be proven by the verification lane, never assumed):
//   - Cross-process atomicity parity with the Postgres single-statement upsert
//     (INSERT ... ON CONFLICT DO UPDATE WHERE expires_at <= now). The EF Core translation
//     below uses read-modify-write + unique index + concurrency token; losers fail closed
//     but single-statement atomicity is NOT proven here.
//   - LeaseId continuity across expired-takeover (row/Id retained) vs. the in-memory store
//     (fresh LeaseId per acquire). Fencing token remains authoritative in both.

namespace OpenConnect.Persistence.EFCore.Navigator;

/// <summary>
/// Durable Navigator lease seam over the EF Core lease model. BCL-only mirror of the
/// <c>OpenConnect.Navigator.Operational.Durable.IDurableOperationalStore</c> lease surface.
/// </summary>
public interface INavigatorLeaseSeam
{
    ValueTask<NavigatorLease?> TryAcquireAsync(
        string executionId, string owner, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken = default);

    ValueTask<NavigatorLease?> RenewAsync(
        NavigatorLease lease, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken = default);

    ValueTask<bool> IsCurrentAsync(
        NavigatorLease lease, DateTimeOffset now, CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(NavigatorLease lease, CancellationToken cancellationToken = default);
}

/// <summary>
/// Navigator lease handle. Shape-compatible with
/// <c>OpenConnect.Navigator.Operational.OperationalLease</c> without referencing that package.
/// <see cref="FencingToken"/> is the monotonic cross-process authority; <see cref="LeaseId"/>
/// is generation provenance only and is not part of the fence.
/// </summary>
public sealed record NavigatorLease(string LeaseId, string ExecutionId, long FencingToken, DateTimeOffset ExpiresAt);
