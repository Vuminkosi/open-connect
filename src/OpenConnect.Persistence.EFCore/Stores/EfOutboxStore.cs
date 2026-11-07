using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfOutboxStore<TContext>(TContext context) : OpenConnect.Abstractions.IOutboxStore where TContext : DbContext
{
    public async Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken = default)
    {
        context.Add(new OutboxEntity { Id = entry.Id, TenantScope = "", EventId = entry.Id.ToString("D"), Subject = entry.Type, SanitizedPayload = entry.Payload, Status = "pending", CreatedUtc = DateTime.UtcNow, NextAttemptUtc = DateTime.UtcNow });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = DateTime.UtcNow;
        var rows = await context.Set<OutboxEntity>().AsNoTracking().Where(x => x.Status == "pending" && x.NextAttemptUtc <= now).OrderBy(x => x.NextAttemptUtc).ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(x => new OutboxEntry(x.Id, x.Subject, x.SanitizedPayload)).ToArray();
    }

    public async Task MarkDeliveredAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var row = await context.Set<OutboxEntity>().SingleOrDefaultAsync(x => x.Id == entryId, cancellationToken).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "delivered";
        row.PublishedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
