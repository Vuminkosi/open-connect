using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;

namespace OpenConnect.Persistence.EFCore.Health;

internal sealed class EfSchemaHealthCheck<TContext>(TContext context, IOptions<OpenConnectEfOptions> options) where TContext : DbContext
{
    public async Task<(bool Healthy, string? Description)> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (options.Value.ValidateSchema == OpenConnectSchemaValidationMode.None) return (true, null);
            EfRuntimeModelValidator.Validate(context);
            if (options.Value.ValidateSchema == OpenConnectSchemaValidationMode.ValidateModel) return (true, null);
            if (!await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
                return (false, $"{ErrorCodes.EfSchemaMissing}: database connection unavailable during schema validation.");
            return (true, "EF model is present; schema connectivity verified. Database object creation is never performed by this check.");
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, $"{ErrorCodes.EfSchemaMismatch}: schema validation failed: {ex.Message}");
        }
    }
}
