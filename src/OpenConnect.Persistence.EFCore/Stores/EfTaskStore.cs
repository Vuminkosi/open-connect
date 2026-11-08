using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfTaskStore<TContext>(TContext context) : ITaskStore where TContext : DbContext
{
    public async Task<TaskRecord> AcceptAsync(TaskAcceptance acceptance, CancellationToken cancellationToken = default)
    {
        var id = UlidGuidMapper.ToGuid(acceptance.TaskId.Value);
        var tenant = UlidGuidMapper.Scope(acceptance.TenantId.Value);
        var principal = UlidGuidMapper.Scope(acceptance.PrincipalId.Value);
        var existing = await context.Set<TaskEntity>().SingleOrDefaultAsync(x => x.ClientTaskId == acceptance.IdempotencyKey && x.TenantScope == tenant && x.PrincipalScope == principal, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{acceptance.TaskId}:{acceptance.IdempotencyKey}"))).ToLowerInvariant();
        if (existing is not null)
        {
            if (existing.TaskId != id) throw new InvalidOperationException($"{ErrorCodes.Conflict}: idempotency key already belongs to another task.");
            return new(acceptance.TaskId, acceptance.TenantId, acceptance.PrincipalId, Enum.Parse<AgentRunState>(existing.State));
        }
        // EF-02D master-approved interim mapping: no session is carried; payload-free canonical binding.
        context.Add(new TaskEntity { TaskId = id, TenantScope = tenant, PrincipalScope = principal, SessionId = Guid.Empty, ClientTaskId = acceptance.IdempotencyKey, WorkloadScope = "", AgentId = "unassigned", State = nameof(AgentRunState.Queued), Version = 1, CanonicalHash = hash, CreatedUtc = DateTime.UtcNow });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(acceptance.TaskId, acceptance.TenantId, acceptance.PrincipalId, AgentRunState.Queued);
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
}
