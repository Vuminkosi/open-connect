using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudWorkerExpansionCatalogTests
{
    private static readonly string[] Ids = ["documents.analyze", "knowledge.embed", "knowledge.rerank", "vision.analyze",
        "analytics.analyze", "knowledge.index", "documents.extract", "vision.embed"];

    [Fact]
    public void Application_projection_does_not_enable_reranking_when_only_embedding_is_selected()
    {
        var tenant = Guid.NewGuid();
        var application = Guid.NewGuid();
        var definitions = Ids.Select(id => new CloudCapabilityDefinition
        {
            Id = id,
            Name = id,
            Description = "fixture",
            ContractVersion = "openconnect.v1",
            BillingUnit = "unconfigured",
            PlatformAvailability = "available",
            Dependencies = "[]",
            Modalities = "[]",
            AllowedRegions = "[]"
        }).ToArray();
        var entitlements = Ids.ToDictionary(id => id, id => new CloudTenantEntitlement { TenantId = tenant, CapabilityId = id, PlanGranted = true });
        var tenantSelections = Ids.ToDictionary(id => id, id => new CloudTenantCapabilitySelection { TenantId = tenant, CapabilityId = id, Enabled = true });
        var applicationSelections = Ids.ToDictionary(id => id, id => new CloudApplicationCapabilitySelection
        { TenantId = tenant, ApplicationId = application, CapabilityId = id, Enabled = id != "knowledge.rerank" });
        var snapshot = new CloudCapabilitySnapshot(definitions, entitlements, tenantSelections, applicationSelections);
        var views = CloudCapabilityProjection.Application(snapshot, DateTimeOffset.UtcNow);
        Assert.Null(views.Single(item => item.Id == "knowledge.embed").ReasonCode);
        Assert.Equal("capability_disabled", views.Single(item => item.Id == "knowledge.rerank").ReasonCode);
        Assert.Null(views.Single(item => item.Id == "vision.embed").ReasonCode);
        entitlements["vision.embed"].PlanGranted = false;
        views = CloudCapabilityProjection.Application(snapshot, DateTimeOffset.UtcNow);
        Assert.Equal("capability_not_entitled", views.Single(item => item.Id == "vision.embed").ReasonCode);
        Assert.Null(views.Single(item => item.Id == "knowledge.embed").ReasonCode);
    }

    [Fact]
    public async Task Machine_admission_enforces_each_capability_scope_and_entitlement_independently()
    {
        var tenant = Guid.NewGuid();
        var application = Guid.NewGuid();
        var definitions = Ids.Select(id => new CloudCapabilityDefinition
        {
            Id = id,
            Name = id,
            Description = "fixture",
            ContractVersion = "openconnect.v1",
            BillingUnit = "unconfigured",
            PlatformAvailability = "available",
            Dependencies = "[]",
            Modalities = "[]",
            AllowedRegions = "[]"
        }).ToArray();
        var entitlements = Ids.ToDictionary(id => id, id => new CloudTenantEntitlement { TenantId = tenant, CapabilityId = id, PlanGranted = true });
        var selections = Ids.ToDictionary(id => id, id => new CloudTenantCapabilitySelection { TenantId = tenant, CapabilityId = id, Enabled = true });
        var applications = Ids.ToDictionary(id => id, id => new CloudApplicationCapabilitySelection
        { TenantId = tenant, ApplicationId = application, CapabilityId = id, Enabled = true });
        var snapshot = new CloudCapabilitySnapshot(definitions, entitlements, selections, applications);
        var material = CloudCredentialSecrets.Create("Development");
        var credential = new CloudCredential
        {
            Id = material.Id,
            TenantId = tenant,
            ApplicationId = application,
            Environment = "Development",
            KeyHash = material.Hash,
            Fingerprint = material.Fingerprint,
            Scopes = "[]",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var service = new CloudMachineAuthorizationService(new CloudCredentialService(new CredentialStore(credential), TimeProvider.System),
            new CapabilityReader(snapshot), TimeProvider.System);

        foreach (var selected in Ids)
        {
            credential.Scopes = JsonSerializer.Serialize(new[] { selected });
            Assert.True((await service.AuthorizeAsync(material.Secret, selected, CancellationToken.None)).Authorized);
            foreach (var other in Ids.Where(id => id != selected))
                Assert.Equal("forbidden", (await service.AuthorizeAsync(material.Secret, other, CancellationToken.None)).Code);
            entitlements[selected].PlanGranted = false;
            Assert.Equal("capability_not_entitled", (await service.AuthorizeAsync(material.Secret, selected, CancellationToken.None)).Code);
            entitlements[selected].PlanGranted = true;
            applications[selected].Enabled = false;
            Assert.Equal("capability_disabled", (await service.AuthorizeAsync(material.Secret, selected, CancellationToken.None)).Code);
            applications[selected].Enabled = true;
            var definition = definitions.Single(item => item.Id == selected);
            definition.PlatformAvailability = "not_configured";
            Assert.Equal("capability_unavailable", (await service.AuthorizeAsync(material.Secret, selected, CancellationToken.None)).Code);
            definition.PlatformAvailability = "available";
        }
    }

    private sealed class CapabilityReader(CloudCapabilitySnapshot snapshot) : ICloudMachineCapabilityReader
    {
        public Task<CloudCapabilitySnapshot?> GetCurrentAsync(CloudCredentialIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult<CloudCapabilitySnapshot?>(snapshot);
    }

    private sealed class CredentialStore(CloudCredential credential) : ICloudCredentialStore, ICloudCredentialSession
    {
        public Task<ICloudCredentialSession> OpenAsync(bool transactional, CancellationToken cancellationToken) => Task.FromResult<ICloudCredentialSession>(this);
        public Task<CloudCredential?> FindAsync(Guid credentialId, Guid? tenantId, Guid? applicationId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudCredential?>(credentialId == credential.Id ? credential : null);
        public Task<bool> TouchIfActiveAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<string?> FindRoleAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudCredentialApplication?> FindApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudCredential>> ListAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CloudCapabilitySnapshot?> GetCapabilitiesAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Add(CloudCredential value) => throw new NotSupportedException();
        public void AddAudit(CloudSubject subject, Guid tenantId, string role, string action, Guid credentialId, DateTimeOffset now) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Migration_seeds_all_capabilities_as_unavailable_without_overwriting_existing_rows()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new CloudControlPlaneDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript("20261120163836_AddCloudPrivacyEvidence", "20261121235958_AddWorkerExpansionCatalog");
        foreach (var id in Ids)
            Assert.Contains($"'{id}'", script, StringComparison.Ordinal);
        Assert.Contains("'not_configured'", script, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"Id\") DO NOTHING", script, StringComparison.Ordinal);
    }

    [CloudPostgresFact]
    public async Task Late_catalog_migration_preserves_existing_capability_configuration()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        await using var db = new CloudControlPlaneDbContext(options);
        await db.Database.MigrateAsync();
        const string capability = "knowledge.embed";
        var existing = await db.Capabilities.SingleAsync(item => item.Id == capability);
        var original = existing.PlatformAvailability;
        try
        {
            existing.PlatformAvailability = "available";
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261121235958_AddWorkerExpansionCatalog'");
            await db.Database.MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.Equal("available", (await db.Capabilities.SingleAsync(item => item.Id == capability)).PlatformAvailability);
            Assert.DoesNotContain("20261121235958_AddWorkerExpansionCatalog", await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            db.ChangeTracker.Clear();
            var restored = await db.Capabilities.SingleAsync(item => item.Id == capability);
            restored.PlatformAvailability = original;
            await db.SaveChangesAsync();
        }
    }

    [CloudPostgresFact]
    public async Task Seeded_capabilities_are_not_configured_and_have_no_dependencies()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        await using var db = new CloudControlPlaneDbContext(options);
        await db.Database.MigrateAsync();
        var rows = await db.Capabilities.AsNoTracking().Where(item => Ids.Contains(item.Id)).ToListAsync();
        Assert.Equal(Ids.Length, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("not_configured", row.PlatformAvailability);
            Assert.Equal("[]", row.Dependencies);
            Assert.False(row.ModalitiesVerified);
            Assert.False(row.AllowedRegionsVerified);
        });
    }
}
