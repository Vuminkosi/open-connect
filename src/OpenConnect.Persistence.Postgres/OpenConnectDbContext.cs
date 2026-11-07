using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.Postgres.Entities;

namespace OpenConnect.Persistence.Postgres;

/// <summary>EF Core context for OpenConnect's PostgreSQL persistence model.</summary>
/// <param name="options">Configuration for this context instance.</param>
public sealed class OpenConnectDbContext(DbContextOptions<OpenConnectDbContext> options) : DbContext(options)
{
    /// <summary>Gets the persisted sessions.</summary>
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    /// <summary>Gets the persisted tasks.</summary>
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    /// <summary>Gets the persisted turns.</summary>
    public DbSet<TurnEntity> Turns => Set<TurnEntity>();
    /// <summary>Gets the persisted events.</summary>
    public DbSet<EventEntity> Events => Set<EventEntity>();
    /// <summary>Gets the messages awaiting reliable delivery.</summary>
    public DbSet<OutboxEntity> Outbox => Set<OutboxEntity>();
    /// <summary>Gets the distributed leases.</summary>
    public DbSet<LeaseEntity> Leases => Set<LeaseEntity>();
    /// <summary>Gets the persisted approval requests.</summary>
    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();
    /// <summary>Gets the encrypted privacy tokens.</summary>
    public DbSet<PrivacyTokenEntity> PrivacyTokens => Set<PrivacyTokenEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var sessions = modelBuilder.Entity<SessionEntity>();
        sessions.ToTable("oc_sessions");
        sessions.HasKey(x => x.Id);
        sessions.Property(x => x.Id).HasColumnName("id");
        sessions.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
        sessions.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        sessions.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().HasDefaultValue(1L);
        sessions.Property(x => x.ContextCheckpoint).HasColumnName("context_checkpoint").HasColumnType("jsonb");
        sessions.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        sessions.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        sessions.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        sessions.HasAlternateKey(x => new { x.TenantId, x.OwnerId, x.Id }).HasName("uq_session_tenant_owner");
        sessions.HasIndex(x => new { x.TenantId, x.OwnerId }).HasDatabaseName("idx_sessions_tenant_owner");

        var tasks = modelBuilder.Entity<TaskEntity>();
        tasks.ToTable("oc_tasks"); tasks.HasKey(x => x.Id);
        tasks.Property(x => x.Id).HasColumnName("id");
        tasks.Property(x => x.TenantId).HasColumnName("tenant_id"); tasks.Property(x => x.PrincipalId).HasColumnName("principal_id");
        tasks.Property(x => x.SessionId).HasColumnName("session_id"); tasks.Property(x => x.ClientTaskId).HasColumnName("client_task_id").IsRequired();
        tasks.Property(x => x.AgentId).HasColumnName("agent_id").IsRequired(); tasks.Property(x => x.State).HasColumnName("state").IsRequired();
        tasks.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().HasDefaultValue(1L);
        tasks.Property(x => x.CanonicalHash).HasColumnName("canonical_hash").IsRequired();
        tasks.Property(x => x.InputRef).HasColumnName("input_ref"); tasks.Property(x => x.ResultRef).HasColumnName("result_ref");
        tasks.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        tasks.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        tasks.HasAlternateKey(x => new { x.TenantId, x.PrincipalId, x.Id }).HasName("uq_task_tenant_principal_id");
        tasks.HasIndex(x => new { x.TenantId, x.PrincipalId, x.ClientTaskId }).IsUnique().HasDatabaseName("uq_task_idempotency");
        tasks.HasIndex(x => x.SessionId).HasDatabaseName("idx_tasks_session"); tasks.HasIndex(x => x.State).HasDatabaseName("idx_tasks_state");
        tasks.HasOne<SessionEntity>().WithMany().HasForeignKey(x => new { x.TenantId, x.PrincipalId, x.SessionId }).HasPrincipalKey(x => new { x.TenantId, x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var turns = modelBuilder.Entity<TurnEntity>(); turns.ToTable("oc_turns"); turns.HasKey(x => x.Id);
        turns.Property(x => x.Id).HasColumnName("id"); turns.Property(x => x.TenantId).HasColumnName("tenant_id"); turns.Property(x => x.PrincipalId).HasColumnName("principal_id");
        turns.Property(x => x.SessionId).HasColumnName("session_id"); turns.Property(x => x.Sequence).HasColumnName("sequence"); turns.Property(x => x.State).HasColumnName("state");
        turns.Property(x => x.Context).HasColumnName("context").HasColumnType("jsonb"); turns.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
        turns.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        turns.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique().HasDatabaseName("uq_turn_session_sequence");
        turns.HasOne<SessionEntity>().WithMany().HasForeignKey(x => new { x.TenantId, x.PrincipalId, x.SessionId }).HasPrincipalKey(x => new { x.TenantId, x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var events = modelBuilder.Entity<EventEntity>(); events.ToTable("oc_events"); events.HasKey(x => x.Id);
        events.Property(x => x.Id).HasColumnName("id"); events.Property(x => x.SessionId).HasColumnName("session_id"); events.Property(x => x.Sequence).HasColumnName("sequence");
        events.Property(x => x.EventType).HasColumnName("event_type"); events.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        events.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone"); events.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        events.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique().HasDatabaseName("uq_event_sequence");

        var outbox = modelBuilder.Entity<OutboxEntity>(); outbox.ToTable("oc_outbox"); outbox.HasKey(x => x.Id);
        outbox.Property(x => x.Id).HasColumnName("id"); outbox.Property(x => x.Topic).HasColumnName("topic"); outbox.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(x => x.State).HasColumnName("state").HasDefaultValue("pending"); outbox.Property(x => x.Attempts).HasColumnName("attempts");
        outbox.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone"); outbox.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        outbox.Property(x => x.DeliveredAt).HasColumnName("delivered_at").HasColumnType("timestamp with time zone");
        outbox.HasIndex(x => new { x.State, x.NextAttemptAt }).HasDatabaseName("idx_outbox_pending").HasFilter("state = 'pending'");

        var leases = modelBuilder.Entity<LeaseEntity>(); leases.ToTable("oc_leases"); leases.HasKey(x => x.Name);
        leases.Property(x => x.Name).HasColumnName("name"); leases.Property(x => x.Owner).HasColumnName("owner"); leases.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        leases.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();

        var approvals = modelBuilder.Entity<ApprovalEntity>(); approvals.ToTable("oc_approvals"); approvals.HasKey(x => x.Id);
        approvals.Property(x => x.Id).HasColumnName("id"); approvals.Property(x => x.TenantId).HasColumnName("tenant_id"); approvals.Property(x => x.PrincipalId).HasColumnName("principal_id");
        approvals.Property(x => x.TaskId).HasColumnName("task_id"); approvals.Property(x => x.State).HasColumnName("state"); approvals.Property(x => x.Decision).HasColumnName("decision");
        approvals.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); approvals.Property(x => x.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamp with time zone");
        approvals.HasOne<TaskEntity>().WithMany().HasForeignKey(x => new { x.TenantId, x.PrincipalId, x.TaskId }).HasPrincipalKey(x => new { x.TenantId, x.PrincipalId, x.Id }).OnDelete(DeleteBehavior.Cascade);

        var tokens = modelBuilder.Entity<PrivacyTokenEntity>(); tokens.ToTable("oc_privacy_tokens"); tokens.HasKey(x => x.Token);
        tokens.Property(x => x.Token).HasColumnName("token"); tokens.Property(x => x.EncryptedValue).HasColumnName("encrypted_value").IsRequired();
        tokens.Property(x => x.TenantId).HasColumnName("tenant_id"); tokens.Property(x => x.PrincipalId).HasColumnName("principal_id"); tokens.Property(x => x.SessionId).HasColumnName("session_id");
        tokens.Property(x => x.TurnId).HasColumnName("turn_id"); tokens.Property(x => x.EntityType).HasColumnName("entity_type"); tokens.Property(x => x.KeyId).HasColumnName("key_id");
        tokens.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); tokens.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        tokens.HasIndex(x => x.SessionId).HasDatabaseName("idx_tokens_session"); tokens.HasIndex(x => x.ExpiresAt).HasDatabaseName("idx_tokens_expiry");
    }
}
