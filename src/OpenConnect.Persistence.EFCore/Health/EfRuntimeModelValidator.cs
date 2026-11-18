using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Health;

internal static class EfRuntimeModelValidator
{
    private static readonly (Type Type, string Table)[] Expected =
    [
        (typeof(SessionEntity), "Sessions"), (typeof(TaskEntity), "Tasks"), (typeof(TurnEntity), "Turns"),
        (typeof(EventEntity), "Events"), (typeof(OutboxEntity), "Outbox"), (typeof(LeaseEntity), "Leases"),
        (typeof(ApprovalEntity), "Approvals"), (typeof(PrivacyTokenEntity), "PrivacyTokens"),
        (typeof(OperationalRunEntity), "OperationalRuns"), (typeof(NativeAgentSessionEntity), "NativeAgentSessions"),
        (typeof(PublicSessionMappingEntity), "PublicSessionMappings"),
        (typeof(ApprovalNonceEntity), "ApprovalNonces")
    ];

    public static void Validate(DbContext context)
    {
        foreach (var (type, table) in Expected)
        {
            var entity = context.Model.FindEntityType(type);
            var tableName = entity?.GetTableName();
            var matches = entity is not null && (
                string.Equals(tableName, table, StringComparison.OrdinalIgnoreCase) ||
                (tableName?.EndsWith(table.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) == true));
            if (!matches)
                throw new InvalidOperationException($"{ErrorCodes.EfModelMissing}: EF model is missing {type.Name} mapped to {table}. Apply OpenConnectModelBuilderExtensions.ApplyOpenConnectModel() in OnModelCreating.");
        }
    }
}
