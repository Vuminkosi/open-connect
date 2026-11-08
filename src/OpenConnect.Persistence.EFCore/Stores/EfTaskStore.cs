using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfTaskStore<TContext>(TContext context) : ITaskStore, ITaskLifecycleStore where TContext : DbContext
{
    public async Task<TaskRecord> AcceptAsync(TaskAcceptance acceptance, CancellationToken cancellationToken = default)
    {
        var id = UlidGuidMapper.ToGuid(acceptance.TaskId.Value);
        var tenant = UlidGuidMapper.Scope(acceptance.TenantId.Value);
        var principal = UlidGuidMapper.Scope(acceptance.PrincipalId.Value);
        var existing = await context.Set<TaskEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.ClientTaskId == acceptance.IdempotencyKey && x.TenantScope == tenant && x.PrincipalScope == principal, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{acceptance.TaskId}:{acceptance.IdempotencyKey}"))).ToLowerInvariant();
        if (existing is not null)
        {
            if (existing.TaskId != id) throw new InvalidOperationException($"{ErrorCodes.Conflict}: idempotency key already belongs to another task.");
            return new(acceptance.TaskId, acceptance.TenantId, acceptance.PrincipalId, Enum.Parse<AgentRunState>(existing.State));
        }
        // EF-02D master-approved interim mapping: no session is carried; payload-free canonical binding.
        context.Add(new TaskEntity { TaskId = id, TenantScope = tenant, PrincipalScope = principal, SessionId = Guid.Empty, ClientTaskId = acceptance.IdempotencyKey, WorkloadScope = "", AgentId = "unassigned", State = nameof(AgentRunState.Accepted), Version = 1, CanonicalHash = hash, CreatedUtc = DateTime.UtcNow });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(acceptance.TaskId, acceptance.TenantId, acceptance.PrincipalId, AgentRunState.Accepted);
    }

    public async Task<TaskRecord?> GetAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        var e = await context.Set<TaskEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == UlidGuidMapper.ToGuid(taskId.Value), cancellationToken).ConfigureAwait(false);
        if (e is null) return null;
        return new(taskId, new(Ulid.Parse(e.TenantScope)), new(Ulid.Parse(e.PrincipalScope)), Enum.Parse<AgentRunState>(e.State));
    }

    public async Task<TaskRecord?> GetByIdempotencyKeyAsync(string clientTaskId, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken = default)
    {
        var t = UlidGuidMapper.Scope(tenant.Value); var p = UlidGuidMapper.Scope(principal.Value);
        var e = await context.Set<TaskEntity>().AsNoTracking().SingleOrDefaultAsync(x => x.ClientTaskId == clientTaskId && x.TenantScope == t && x.PrincipalScope == p, cancellationToken).ConfigureAwait(false);
        return e is null ? null : new(new(UlidGuidMapper.FromGuid(e.TaskId)), tenant, principal, Enum.Parse<AgentRunState>(e.State));
    }

    public async Task<TaskRecord?> TransitionAsync(TaskId taskId, TenantId tenantId, PrincipalId principalId, AgentRunState expectedState, AgentRunState newState, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedTransition(expectedState, newState))
            throw new ArgumentException($"Transition from {expectedState} to {newState} is not allowed.", nameof(newState));

        var taskGuid = UlidGuidMapper.ToGuid(taskId.Value);
        var tenant = UlidGuidMapper.Scope(tenantId.Value);
        var principal = UlidGuidMapper.Scope(principalId.Value);
        var affected = await context.Set<TaskEntity>()
            .Where(x => x.TaskId == taskGuid && x.TenantScope == tenant && x.PrincipalScope == principal && x.State == expectedState.ToString())
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, newState.ToString()), cancellationToken)
            .ConfigureAwait(false);

        if (affected == 0) return null;

        // Observe the current scoped state after the successful compare-and-set. A later
        // lifecycle update may already have advanced it, so this is not necessarily newState.
        var persisted = await context.Set<TaskEntity>().AsNoTracking().SingleOrDefaultAsync(
            x => x.TaskId == taskGuid && x.TenantScope == tenant && x.PrincipalScope == principal,
            cancellationToken).ConfigureAwait(false);
        // The row may have been deleted after our successful CAS; that does not undo success,
        // and null is reserved for a failed initial match.
        return persisted is null
            ? new(taskId, tenantId, principalId, newState)
            : new(taskId, tenantId, principalId, Enum.Parse<AgentRunState>(persisted.State));
    }

    private static bool IsAllowedTransition(AgentRunState current, AgentRunState next) =>
        (current, next) is (AgentRunState.Accepted, AgentRunState.Running or AgentRunState.Cancelled)
            or (AgentRunState.Running, AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled);
}
