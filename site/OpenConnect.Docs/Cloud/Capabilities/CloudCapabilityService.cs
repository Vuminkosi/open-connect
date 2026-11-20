using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static OpenConnect.Docs.Cloud.CloudCapabilityPolicy;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudCapabilityView(
    string Id,
    string Name,
    string Description,
    string ContractVersion,
    string BillingUnit,
    string PlatformAvailability,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Modalities,
    bool ModalitiesVerified,
    IReadOnlyList<string> AllowedRegions,
    bool AllowedRegionsVerified,
    bool? Entitled,
    bool? TenantEnabled,
    bool? ApplicationEnabled,
    int? Revision,
    string? ReasonCode);

public sealed record CloudCapabilitySelectionRequest(bool Enabled);
public sealed record CloudCapabilitySelectionResult(string Status, int? Revision = null);

public sealed class CloudCapabilityService(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider)
{
    private static readonly string[] Administrators = ["Owner", "Administrator"];

    public async Task<IReadOnlyList<CloudCapabilityView>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var capabilities = await db.Capabilities.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        return capabilities.Select(capability => ToView(
            capability, null, null, null, null,
            capability.PlatformAvailability == "available" ? null : "capability_unavailable")).ToArray();
    }

    public async Task<IReadOnlyList<CloudCapabilityView>?> GetTenantCapabilitiesAsync(
        CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(db, subject, tenantId, cancellationToken))
        {
            return null;
        }

        var capabilities = await db.Capabilities.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var entitlements = await db.TenantEntitlements.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        var selections = await db.TenantCapabilitySelections.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        return capabilities.Select(capability =>
        {
            var entitled = IsEntitled(entitlements.GetValueOrDefault(capability.Id), timeProvider.GetUtcNow());
            var enabled = selections.TryGetValue(capability.Id, out var selection) && selection.Enabled;
            var dependencyReady = DependenciesEnabled(capability, capabilities, entitlements, selections, timeProvider.GetUtcNow());
            return ToView(capability, entitled, enabled, false, selection?.Revision,
                GetReason(capability, entitled, enabled, false, dependencyReady, applicationSelectionRequired: false));
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudCapabilityView>?> GetApplicationCapabilitiesAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await GetApplicationCapabilitiesAsync(db, subject, tenantId, applicationId, cancellationToken);
    }

    internal async Task<IReadOnlyList<CloudCapabilityView>?> GetApplicationCapabilitiesAsync(
        CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        if (!await IsMemberAsync(db, subject, tenantId, cancellationToken))
        {
            return null;
        }
        if (!await db.Applications.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == applicationId, cancellationToken))
        {
            return null;
        }

        var capabilities = await db.Capabilities.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var entitlements = await db.TenantEntitlements.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        var tenantSelections = await db.TenantCapabilitySelections.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        var applicationSelections = await db.ApplicationCapabilitySelections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId)
            .ToDictionaryAsync(x => x.CapabilityId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return capabilities.Select(capability =>
        {
            var entitled = IsEntitled(entitlements.GetValueOrDefault(capability.Id), now);
            var tenantEnabled = tenantSelections.TryGetValue(capability.Id, out var tenantSelection) && tenantSelection.Enabled;
            var appEnabled = applicationSelections.TryGetValue(capability.Id, out var appSelection) && appSelection.Enabled;
            var dependencyReady = DependenciesEnabled(capability, capabilities, entitlements, tenantSelections, now, applicationSelections);
            return ToView(capability, entitled, tenantEnabled, appEnabled, appSelection?.Revision,
                GetReason(capability, entitled, tenantEnabled, appEnabled, dependencyReady, applicationSelectionRequired: true));
        }).ToArray();
    }

    public Task<CloudCapabilitySelectionResult> SetTenantSelectionAsync(
        CloudSubject subject, Guid tenantId, string capabilityId, bool enabled, int expectedRevision, CancellationToken cancellationToken) =>
        SetSelectionAsync(subject, tenantId, null, capabilityId, enabled, expectedRevision, cancellationToken);

    public Task<CloudCapabilitySelectionResult> SetApplicationSelectionAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, string capabilityId, bool enabled, int expectedRevision, CancellationToken cancellationToken) =>
        SetSelectionAsync(subject, tenantId, applicationId, capabilityId, enabled, expectedRevision, cancellationToken);

    private async Task<CloudCapabilitySelectionResult> SetSelectionAsync(
        CloudSubject subject, Guid tenantId, Guid? applicationId, string capabilityId, bool enabled, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var role = await db.Memberships.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId)
            .Select(x => x.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            return new("not_found");
        }
        if (!Administrators.Contains(role, StringComparer.Ordinal))
        {
            return new("forbidden");
        }

        var capability = await db.Capabilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == capabilityId, cancellationToken);
        if (capability is null)
        {
            return new("not_found");
        }
        if (applicationId is not null && !await db.Applications.AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.Id == applicationId.Value, cancellationToken))
        {
            return new("not_found");
        }

        CloudTenantEntitlement? entitlement = await db.TenantEntitlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CapabilityId == capabilityId, cancellationToken);
        CloudTenantCapabilitySelection? tenantSelection = await db.TenantCapabilitySelections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CapabilityId == capabilityId, cancellationToken);
        if (enabled)
        {
            if (!IsEntitled(entitlement, timeProvider.GetUtcNow()))
            {
                return new("capability_not_entitled");
            }
            if (!string.Equals(capability.PlatformAvailability, "available", StringComparison.Ordinal))
            {
                return new("capability_unavailable");
            }
            if (!await AreDependenciesEnabledAsync(db, capability, tenantId, applicationId, timeProvider.GetUtcNow(), cancellationToken))
            {
                return new("dependency_not_enabled");
            }
            if (applicationId is not null && tenantSelection?.Enabled != true)
            {
                return new("capability_disabled");
            }
        }

        var currentRevision = 0;
        if (applicationId is null)
        {
            var selection = await db.TenantCapabilitySelections.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.CapabilityId == capabilityId, cancellationToken);
            currentRevision = selection?.Revision ?? 0;
            if (currentRevision != expectedRevision)
            {
                return new("conflict", currentRevision);
            }
            if (selection is null)
            {
                selection = new CloudTenantCapabilitySelection { TenantId = tenantId, CapabilityId = capabilityId, Enabled = enabled, Revision = 1 };
                db.TenantCapabilitySelections.Add(selection);
            }
            else
            {
                selection.Enabled = enabled;
                selection.Revision++;
            }
            currentRevision = selection.Revision;
        }
        else
        {
            var selection = await db.ApplicationCapabilitySelections.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == capabilityId, cancellationToken);
            currentRevision = selection?.Revision ?? 0;
            if (currentRevision != expectedRevision)
            {
                return new("conflict", currentRevision);
            }
            if (selection is null)
            {
                selection = new CloudApplicationCapabilitySelection
                {
                    TenantId = tenantId, ApplicationId = applicationId.Value, CapabilityId = capabilityId, Enabled = enabled, Revision = 1,
                };
                db.ApplicationCapabilitySelections.Add(selection);
            }
            else
            {
                selection.Enabled = enabled;
                selection.Revision++;
            }
            currentRevision = selection.Revision;
        }

        db.AuditEvents.Add(new CloudAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = subject.Issuer, SubjectId = subject.SubjectId,
            ActorRole = role, Action = applicationId is null ? "tenant.capability.selection_changed" : "application.capability.selection_changed",
            ResourceId = applicationId?.ToString("D") ?? tenantId.ToString("D"), Outcome = "succeeded", OccurredAt = timeProvider.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new("succeeded", currentRevision);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new("conflict", currentRevision);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new("conflict", currentRevision);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return new("conflict", currentRevision);
        }
    }

    private static async Task<bool> AreDependenciesEnabledAsync(
        CloudControlPlaneDbContext db, CloudCapabilityDefinition capability, Guid tenantId, Guid? applicationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var capabilities = await db.Capabilities.AsNoTracking().ToDictionaryAsync(x => x.Id, StringComparer.Ordinal, cancellationToken);
        var entitlements = await db.TenantEntitlements.AsNoTracking().Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.CapabilityId, StringComparer.Ordinal, cancellationToken);
        var selections = await db.TenantCapabilitySelections.AsNoTracking().Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.CapabilityId, StringComparer.Ordinal, cancellationToken);
        var applicationSelections = applicationId is null ? null : await db.ApplicationCapabilitySelections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId.Value)
            .ToDictionaryAsync(x => x.CapabilityId, StringComparer.Ordinal, cancellationToken);
        return DependenciesEnabled(capability, capabilities, entitlements, selections, now, new HashSet<string>(StringComparer.Ordinal), applicationSelections);
    }

    private static CloudCapabilityView ToView(
        CloudCapabilityDefinition capability, bool? entitled, bool? tenantEnabled, bool? applicationEnabled, int? revision, string? reason) => new(
            capability.Id, capability.Name, capability.Description, capability.ContractVersion, capability.BillingUnit,
            capability.PlatformAvailability, JsonSerializer.Deserialize<string[]>(capability.Dependencies) ?? [],
            JsonSerializer.Deserialize<string[]>(capability.Modalities) ?? [], capability.ModalitiesVerified,
            JsonSerializer.Deserialize<string[]>(capability.AllowedRegions) ?? [], capability.AllowedRegionsVerified,
            entitled, tenantEnabled, applicationEnabled, revision, reason);

    private static Task<bool> IsMemberAsync(CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
        db.Memberships.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId, cancellationToken);
}
