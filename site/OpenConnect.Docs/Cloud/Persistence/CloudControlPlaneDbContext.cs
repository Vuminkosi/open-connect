using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudControlPlaneDbContext(DbContextOptions<CloudControlPlaneDbContext> options) : DbContext(options)
{
    public DbSet<CloudTenant> Tenants => Set<CloudTenant>();
    public DbSet<CloudMembership> Memberships => Set<CloudMembership>();
    public DbSet<CloudMembershipRevision> MembershipRevisions => Set<CloudMembershipRevision>();
    public DbSet<CloudApplication> Applications => Set<CloudApplication>();
    public DbSet<CloudAuditEvent> AuditEvents => Set<CloudAuditEvent>();
    public DbSet<CloudCapabilityDefinition> Capabilities => Set<CloudCapabilityDefinition>();
    public DbSet<CloudTenantEntitlement> TenantEntitlements => Set<CloudTenantEntitlement>();
    public DbSet<CloudTenantCapabilitySelection> TenantCapabilitySelections => Set<CloudTenantCapabilitySelection>();
    public DbSet<CloudApplicationCapabilitySelection> ApplicationCapabilitySelections => Set<CloudApplicationCapabilitySelection>();
    public DbSet<CloudCredential> Credentials => Set<CloudCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cloud");

        modelBuilder.Entity<CloudTenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<CloudMembership>(entity =>
        {
            entity.ToTable("memberships", table => table.HasCheckConstraint(
                "ck_cloud_memberships_role", "\"Role\" IN ('Owner', 'Administrator', 'SecurityAnalyst', 'Developer', 'BillingReader')"));
            entity.HasKey(x => new { x.TenantId, x.SubjectIssuer, x.SubjectId });
            entity.Property(x => x.SubjectIssuer).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.SubjectId).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Revision).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.SubjectIssuer, x.SubjectId });
            entity.HasOne<CloudTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudMembershipRevision>(entity =>
        {
            entity.ToTable("membership_revisions");
            entity.HasKey(x => new { x.TenantId, x.SubjectHash });
            entity.Property(x => x.SubjectHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LastRevision).IsConcurrencyToken();
            entity.HasOne<CloudTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudApplication>(entity =>
        {
            entity.ToTable("applications", table => table.HasCheckConstraint(
                "ck_cloud_applications_environment", "\"Environment\" IN ('Development', 'Staging', 'Production')"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.HasOne<CloudTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.Name });
        });

        modelBuilder.Entity<CloudAuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SubjectIssuer).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.SubjectId).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ActorRole).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(128);
            entity.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(x => x.OccurredAt).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.OccurredAt });
        });

        modelBuilder.Entity<CloudCapabilityDefinition>(entity =>
        {
            entity.ToTable("capabilities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(80);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ContractVersion).HasMaxLength(32).IsRequired();
            entity.Property(x => x.BillingUnit).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PlatformAvailability).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Dependencies).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Modalities).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ModalitiesVerified).IsRequired();
            entity.Property(x => x.AllowedRegions).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.AllowedRegionsVerified).IsRequired();
        });

        modelBuilder.Entity<CloudTenantEntitlement>(entity =>
        {
            entity.ToTable("tenant_entitlements");
            entity.HasKey(x => new { x.TenantId, x.CapabilityId });
            entity.Property(x => x.CapabilityId).HasMaxLength(80);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<CloudTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CloudCapabilityDefinition>().WithMany().HasForeignKey(x => x.CapabilityId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudTenantCapabilitySelection>(entity =>
        {
            entity.ToTable("tenant_capability_selections");
            entity.HasKey(x => new { x.TenantId, x.CapabilityId });
            entity.Property(x => x.CapabilityId).HasMaxLength(80);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<CloudTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CloudCapabilityDefinition>().WithMany().HasForeignKey(x => x.CapabilityId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudApplicationCapabilitySelection>(entity =>
        {
            entity.ToTable("application_capability_selections");
            entity.HasKey(x => new { x.TenantId, x.ApplicationId, x.CapabilityId });
            entity.Property(x => x.CapabilityId).HasMaxLength(80);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<CloudApplication>().WithMany()
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .HasForeignKey(x => new { x.TenantId, x.ApplicationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CloudCapabilityDefinition>().WithMany().HasForeignKey(x => x.CapabilityId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CloudCredential>(entity =>
        {
            entity.ToTable("credentials");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Environment).HasMaxLength(16).IsRequired();
            entity.Property(x => x.KeyHash).HasColumnType("bytea").IsRequired();
            entity.Property(x => x.Fingerprint).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Scopes).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.IssuedAt).IsRequired();
            entity.HasOne<CloudApplication>().WithMany()
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .HasForeignKey(x => new { x.TenantId, x.ApplicationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId });
        });
    }
}

public sealed class CloudTenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CloudMembership
{
    public Guid TenantId { get; set; }
    public required string SubjectIssuer { get; set; }
    public required string SubjectId { get; set; }
    public required string Role { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CloudMembershipRevision
{
    public Guid TenantId { get; set; }
    public required string SubjectHash { get; set; }
    public int LastRevision { get; set; }
}

public sealed class CloudApplication
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string Environment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CloudAuditEvent
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public required string SubjectIssuer { get; set; }
    public required string SubjectId { get; set; }
    public required string ActorRole { get; set; }
    public required string Action { get; set; }
    public string? ResourceId { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class CloudCapabilityDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string ContractVersion { get; set; }
    public required string BillingUnit { get; set; }
    public required string PlatformAvailability { get; set; }
    public required string Dependencies { get; set; }
    public required string Modalities { get; set; }
    public bool ModalitiesVerified { get; set; }
    public required string AllowedRegions { get; set; }
    public bool AllowedRegionsVerified { get; set; }
}

public sealed class CloudTenantEntitlement
{
    public Guid TenantId { get; set; }
    public required string CapabilityId { get; set; }
    public bool PlanGranted { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int Revision { get; set; }
}

public sealed class CloudTenantCapabilitySelection
{
    public Guid TenantId { get; set; }
    public required string CapabilityId { get; set; }
    public bool Enabled { get; set; }
    public int Revision { get; set; }
}

public sealed class CloudApplicationCapabilitySelection
{
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public required string CapabilityId { get; set; }
    public bool Enabled { get; set; }
    public int Revision { get; set; }
}

public sealed class CloudCredential
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public required string Environment { get; set; }
    public required byte[] KeyHash { get; set; }
    public required string Fingerprint { get; set; }
    public required string Scopes { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
