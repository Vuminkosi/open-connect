using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Persistence.EFCore.Transactions;

internal sealed class EfUnitOfWork<TContext>(TContext context) : IOpenConnectPersistenceUnitOfWork where TContext : DbContext
{
    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (context.Database.CurrentTransaction is not null) return await work(cancellationToken).ConfigureAwait(false);
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await work(cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }
}
