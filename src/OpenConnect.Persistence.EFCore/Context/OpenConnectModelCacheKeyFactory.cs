using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace OpenConnect.Persistence.EFCore.Context;

public sealed class OpenConnectModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), ProviderFamily(context), designTime);

    private static string ProviderFamily(DbContext context)
    {
        var provider = context.Database.ProviderName ?? "unknown";
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return "sqlite";
        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return "postgres";
        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return "sqlserver";
        return provider;
    }
}
