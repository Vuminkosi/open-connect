using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.Postgres.Entities;

namespace OpenConnect.Persistence.Postgres.Repositories;

/// <summary>Performs session lookups only within the authenticated tenant and principal scope.</summary>
public sealed class SessionStore(OpenConnectDbContext dbContext)
{
    /// <summary>Finds a session belonging to the specified tenant and principal.</summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="tenantId">The authenticated tenant identifier.</param>
    /// <param name="principalId">The authenticated principal identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The session when found in scope; otherwise, <see langword="null"/>.</returns>
    public Task<SessionEntity?> GetAsync(SessionId sessionId, TenantId tenantId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
        dbContext.Sessions.AsNoTracking().SingleOrDefaultAsync(
            session => session.Id == ToGuid(sessionId.Value) && session.TenantId == ToGuid(tenantId.Value) && session.OwnerId == ToGuid(principalId.Value),
            cancellationToken);

    private static Guid ToGuid(Ulid id) => UlidGuidConverter.ToGuid(id);
}

/// <summary>Performs task lookups only within the authenticated tenant and principal scope.</summary>
public sealed class TaskStore(OpenConnectDbContext dbContext)
{
    /// <summary>Finds a task belonging to the specified tenant and principal.</summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="tenantId">The authenticated tenant identifier.</param>
    /// <param name="principalId">The authenticated principal identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The task when found in scope; otherwise, <see langword="null"/>.</returns>
    public Task<TaskEntity?> GetAsync(TaskId taskId, TenantId tenantId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
        dbContext.Tasks.AsNoTracking().SingleOrDefaultAsync(
            task => task.Id == ToGuid(taskId.Value) && task.TenantId == ToGuid(tenantId.Value) && task.PrincipalId == ToGuid(principalId.Value),
            cancellationToken);

    /// <summary>Finds a task by its idempotency key within the specified tenant and principal.</summary>
    /// <param name="clientTaskId">The client-provided idempotency key.</param>
    /// <param name="tenantId">The authenticated tenant identifier.</param>
    /// <param name="principalId">The authenticated principal identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The matching task when found in scope; otherwise, <see langword="null"/>.</returns>
    public Task<TaskEntity?> GetByIdempotencyKeyAsync(string clientTaskId, TenantId tenantId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
        dbContext.Tasks.AsNoTracking().SingleOrDefaultAsync(
            task => task.ClientTaskId == clientTaskId && task.TenantId == ToGuid(tenantId.Value) && task.PrincipalId == ToGuid(principalId.Value),
            cancellationToken);

    private static Guid ToGuid(Ulid id) => UlidGuidConverter.ToGuid(id);
}

internal static class UlidGuidConverter
{
    public static Guid ToGuid(Ulid id)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        UInt128 value = 0;
        foreach (var character in id.ToString())
            value = (value << 5) | (uint)alphabet.IndexOf(character);
        value &= UInt128.MaxValue;
        var bytes = new byte[16];
        for (var index = 15; index >= 0; index--)
        {
            bytes[index] = (byte)value;
            value >>= 8;
        }
        return new Guid(bytes, bigEndian: true);
    }
}
