using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudReadinessService(IDbContextFactory<CloudControlPlaneDbContext> contextFactory)
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken) &&
            !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
    }
}

public static class CloudDatabaseFailure
{
    public static bool IsDatabaseFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException or TimeoutException)
                return true;
        }
        return false;
    }
}
