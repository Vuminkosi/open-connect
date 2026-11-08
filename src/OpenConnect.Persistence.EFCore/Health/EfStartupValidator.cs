using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Health;

internal sealed class EfStartupValidator<TContext>(IServiceProvider services) : IHostedService
    where TContext : DbContext
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OpenConnectEfOptions>>().Value;
        var provider = db.Database.ProviderName ?? string.Empty;

        if (options.Consistency == OpenConnectPersistenceConsistency.DurableSingleDatabase &&
            provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            throw new OptionsValidationException(
                nameof(OpenConnectEfOptions),
                typeof(OpenConnectEfOptions),
                ["ef_inmemory_not_durable: DurableSingleDatabase requires a durable relational provider."]);
        }

        if (options.ValidateSchema is OpenConnectSchemaValidationMode.ValidateModel or OpenConnectSchemaValidationMode.ValidateSchemaAndVersion)
        {
            Type[] expectedTypes =
            [
                typeof(SessionEntity), typeof(TaskEntity), typeof(TurnEntity), typeof(EventEntity),
                typeof(OutboxEntity), typeof(LeaseEntity), typeof(ApprovalEntity),
                typeof(PrivacyTokenEntity), typeof(OperationalRunEntity), typeof(NativeAgentSessionEntity),
                typeof(PublicSessionMappingEntity),
                typeof(ApprovalNonceEntity)
            ];
            var missingTypes = expectedTypes.Where(type => db.Model.FindEntityType(type) is null).ToArray();
            if (missingTypes.Length > 0)
            {
                var names = string.Join(", ", missingTypes.Select(type => type.Name));
                throw new InvalidOperationException($"ef_model_missing: The EF model is missing expected OpenConnect entities ({names}). ApplyOpenConnectModel must be called from OnModelCreating.");
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
