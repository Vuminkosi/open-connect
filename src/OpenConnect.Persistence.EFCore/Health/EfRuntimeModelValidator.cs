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
        (typeof(PublicSessionMappingEntity), "PublicSessionMappings")
    ];

    public static void Validate(DbContext context)
    {
        foreach (var (type, table) in Expected)
        {
            var entity = context.Model.FindEntityType(type);
            if (entity is null || !string.Equals(entity.GetTableName(), table, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{ErrorCodes.EfModelMissing}: EF model is missing {type.Name} mapped to {table}. Apply OpenConnectModelBuilderExtensions.ApplyOpenConnectModel() in OnModelCreating.");
        }
    }
}
