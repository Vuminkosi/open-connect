using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.Postgres.Migrations;

[DbContext(typeof(OpenConnectDbContext))]
partial class OpenConnectDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.11");
        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 63);

        var session = modelBuilder.Entity<Entities.SessionEntity>();
        session.ToTable("oc_sessions"); session.HasKey(x => x.Id); session.Property(x => x.Id).HasColumnName("id");
        session.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired(); session.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        session.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1L).IsConcurrencyToken(); session.Property(x => x.ContextCheckpoint).HasColumnName("context_checkpoint").HasColumnType("jsonb");
        session.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); session.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone"); session.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        session.HasAlternateKey(x => new { x.TenantId, x.OwnerId, x.Id }).HasName("uq_session_tenant_owner"); session.HasIndex(x => new { x.TenantId, x.OwnerId }).HasDatabaseName("idx_sessions_tenant_owner");

        var task = modelBuilder.Entity<Entities.TaskEntity>(); task.ToTable("oc_tasks"); task.HasKey(x => x.Id);
        task.Property(x => x.Id).HasColumnName("id"); task.Property(x => x.TenantId).HasColumnName("tenant_id"); task.Property(x => x.PrincipalId).HasColumnName("principal_id"); task.Property(x => x.SessionId).HasColumnName("session_id");
        task.Property(x => x.ClientTaskId).HasColumnName("client_task_id").IsRequired(); task.Property(x => x.AgentId).HasColumnName("agent_id").IsRequired(); task.Property(x => x.State).HasColumnName("state").IsRequired();
        task.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1L).IsConcurrencyToken(); task.Property(x => x.CanonicalHash).HasColumnName("canonical_hash").IsRequired(); task.Property(x => x.InputRef).HasColumnName("input_ref"); task.Property(x => x.ResultRef).HasColumnName("result_ref");
        task.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); task.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        task.HasAlternateKey(x => new { x.TenantId, x.PrincipalId, x.Id }).HasName("uq_task_tenant_principal_id"); task.HasIndex(x => new { x.TenantId, x.PrincipalId, x.ClientTaskId }).IsUnique().HasDatabaseName("uq_task_idempotency"); task.HasIndex(x => x.SessionId).HasDatabaseName("idx_tasks_session"); task.HasIndex(x => x.State).HasDatabaseName("idx_tasks_state");
        task.HasOne<Entities.SessionEntity>().WithMany().HasForeignKey(x => new { x.TenantId, x.PrincipalId, x.SessionId }).HasPrincipalKey(x => new { x.TenantId, x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var turn = modelBuilder.Entity<Entities.TurnEntity>(); turn.ToTable("oc_turns"); turn.HasKey(x => x.Id);
        turn.Property(x => x.Id).HasColumnName("id"); turn.Property(x => x.TenantId).HasColumnName("tenant_id"); turn.Property(x => x.PrincipalId).HasColumnName("principal_id"); turn.Property(x => x.SessionId).HasColumnName("session_id"); turn.Property(x => x.Sequence).HasColumnName("sequence"); turn.Property(x => x.State).HasColumnName("state"); turn.Property(x => x.Context).HasColumnName("context").HasColumnType("jsonb"); turn.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone"); turn.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        turn.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique().HasDatabaseName("uq_turn_session_sequence"); turn.HasOne<Entities.SessionEntity>().WithMany().HasForeignKey(x => new { x.TenantId, x.PrincipalId, x.SessionId }).HasPrincipalKey(x => new { x.TenantId, x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var ev = modelBuilder.Entity<Entities.EventEntity>(); ev.ToTable("oc_events"); ev.HasKey(x => x.Id); ev.Property(x => x.Id).HasColumnName("id"); ev.Property(x => x.SessionId).HasColumnName("session_id"); ev.Property(x => x.Sequence).HasColumnName("sequence"); ev.Property(x => x.EventType).HasColumnName("event_type"); ev.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb"); ev.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone"); ev.Property(x => x.CorrelationId).HasColumnName("correlation_id"); ev.HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique().HasDatabaseName("uq_event_sequence");

        var outbox = modelBuilder.Entity<Entities.OutboxEntity>(); outbox.ToTable("oc_outbox"); outbox.HasKey(x => x.Id); outbox.Property(x => x.Id).HasColumnName("id"); outbox.Property(x => x.Topic).HasColumnName("topic"); outbox.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb"); outbox.Property(x => x.State).HasColumnName("state").HasDefaultValue("pending"); outbox.Property(x => x.Attempts).HasColumnName("attempts"); outbox.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone"); outbox.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); outbox.Property(x => x.DeliveredAt).HasColumnName("delivered_at").HasColumnType("timestamp with time zone"); outbox.HasIndex(x => new { x.State, x.NextAttemptAt }).HasDatabaseName("idx_outbox_pending").HasFilter("state = 'pending'");

        var lease = modelBuilder.Entity<Entities.LeaseEntity>(); lease.ToTable("oc_leases"); lease.HasKey(x => x.Name); lease.Property(x => x.Name).HasColumnName("name"); lease.Property(x => x.Owner).HasColumnName("owner"); lease.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone"); lease.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();

        var approval = modelBuilder.Entity<Entities.ApprovalEntity>(); approval.ToTable("oc_approvals"); approval.HasKey(x => x.Id); approval.Property(x => x.Id).HasColumnName("id"); approval.Property(x => x.TenantId).HasColumnName("tenant_id"); approval.Property(x => x.PrincipalId).HasColumnName("principal_id"); approval.Property(x => x.TaskId).HasColumnName("task_id"); approval.Property(x => x.State).HasColumnName("state"); approval.Property(x => x.Decision).HasColumnName("decision"); approval.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); approval.Property(x => x.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamp with time zone"); approval.HasOne<Entities.TaskEntity>().WithMany().HasForeignKey(x => new { x.TenantId, x.PrincipalId, x.TaskId }).HasPrincipalKey(x => new { x.TenantId, x.PrincipalId, x.Id }).OnDelete(DeleteBehavior.Cascade);

        var token = modelBuilder.Entity<Entities.PrivacyTokenEntity>(); token.ToTable("oc_privacy_tokens"); token.HasKey(x => x.Token); token.Property(x => x.Token).HasColumnName("token"); token.Property(x => x.EncryptedValue).HasColumnName("encrypted_value").IsRequired(); token.Property(x => x.TenantId).HasColumnName("tenant_id"); token.Property(x => x.PrincipalId).HasColumnName("principal_id"); token.Property(x => x.SessionId).HasColumnName("session_id"); token.Property(x => x.TurnId).HasColumnName("turn_id"); token.Property(x => x.EntityType).HasColumnName("entity_type"); token.Property(x => x.KeyId).HasColumnName("key_id"); token.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone"); token.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone"); token.HasIndex(x => x.SessionId).HasDatabaseName("idx_tokens_session"); token.HasIndex(x => x.ExpiresAt).HasDatabaseName("idx_tokens_expiry");
    }
}
