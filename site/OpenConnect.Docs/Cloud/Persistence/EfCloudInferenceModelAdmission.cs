using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

internal static class EfCloudInferenceModelAdmission
{
    private sealed class ModelPolicyRow { public required string Models { get; set; } }

    // Keep the policy locked until the claim commits; a committed removal wins over later claims.
    internal static async Task<bool> LockAllowedAsync(CloudControlPlaneDbContext db, Guid tenantId, string? modelId, CancellationToken cancellationToken)
    {
        if (!CloudInferenceModelPolicyService.ValidModelId(modelId)) return false;
        var rows = await db.Database.SqlQuery<ModelPolicyRow>($"SELECT \"AllowedModels\"::text AS \"Models\" FROM cloud.inference_model_policies WHERE \"TenantId\" = {tenantId} FOR SHARE").ToListAsync(cancellationToken);
        if (rows.Count != 1) return false;
        try
        {
            var models = JsonSerializer.Deserialize<string[]>(rows[0].Models);
            return models is not null && models.Length <= 64 && models.All(CloudInferenceModelPolicyService.ValidModelId) &&
                models.Distinct(StringComparer.Ordinal).Count() == models.Length && models.Contains(modelId!, StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}
