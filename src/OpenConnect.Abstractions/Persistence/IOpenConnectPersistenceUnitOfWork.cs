namespace OpenConnect.Abstractions.Persistence;

/// <summary>Executes persistence work with atomic transaction ownership where supported.</summary>
public interface IOpenConnectPersistenceUnitOfWork
{
    /// <summary>Executes work within the persistence unit of work.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="work">The operation to execute.</param>
    /// <param name="cancellationToken">Cancellation signal.</param>
    /// <returns>The operation result.</returns>
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken = default);
}
