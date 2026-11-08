using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfNativeAgentSessionStore<TContext>(TContext context) : INativeAgentSessionStore
    where TContext : DbContext
{
    public async Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var key = CreateBindingKey(binding);
        var row = await context.Set<NativeAgentSessionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BindingKey == key, ct).ConfigureAwait(false);
        if (row is null) return null;
        if (!Matches(row, binding)) throw new InvalidOperationException("native_session_binding_key_collision: Stored binding digest does not match the requested binding.");
        return new NativeAgentSessionRecord(binding, row.Version, row.Ciphertext);
    }

    public async Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (expectedVersion == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedVersion), "The session version cannot be incremented.");
        if (record.Version != expectedVersion + 1) throw new ArgumentException("Record version must be exactly expectedVersion + 1.", nameof(record));

        var binding = record.Binding;
        var key = CreateBindingKey(binding);
        var set = context.Set<NativeAgentSessionEntity>();
        var current = await set.AsNoTracking().SingleOrDefaultAsync(x => x.BindingKey == key, ct).ConfigureAwait(false);
        if (current is not null)
        {
            if (!Matches(current, binding)) throw new InvalidOperationException("native_session_binding_key_collision: Stored binding digest does not match the requested binding.");
            if (current.Version != expectedVersion) return NativeAgentSessionSaveOutcome.Conflict;

            var ciphertext = record.GetCiphertextCopy();
            var affected = await set.Where(x => x.BindingKey == key && x.Version == expectedVersion)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Ciphertext, ciphertext)
                    .SetProperty(x => x.Version, record.Version), ct).ConfigureAwait(false);
            return affected == 1 ? NativeAgentSessionSaveOutcome.Saved : NativeAgentSessionSaveOutcome.Conflict;
        }

        if (expectedVersion != 0) return NativeAgentSessionSaveOutcome.Conflict;
        var entity = ToEntity(binding, key, record.Version, record.GetCiphertextCopy());
        set.Add(entity);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return NativeAgentSessionSaveOutcome.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            context.Entry(entity).State = EntityState.Detached;
            return NativeAgentSessionSaveOutcome.Conflict;
        }
    }

    private static NativeAgentSessionEntity ToEntity(NativeAgentSessionBinding binding, string key, long version, byte[] ciphertext) => new()
    {
        BindingKey = key,
        SessionId = binding.SessionId.Value.ToString(), TenantId = binding.TenantId.Value.ToString(),
        PrincipalId = binding.PrincipalId.Value.ToString(), AgentId = binding.AgentId,
        DefinitionRevision = binding.DefinitionRevision, MafVersion = binding.MafVersion,
        SchemaVersion = binding.SchemaVersion, ProviderId = binding.ProviderId,
        Version = version, Ciphertext = ciphertext
    };

    private static bool Matches(NativeAgentSessionEntity row, NativeAgentSessionBinding binding) =>
        string.Equals(row.SessionId, binding.SessionId.Value.ToString(), StringComparison.Ordinal) &&
        string.Equals(row.TenantId, binding.TenantId.Value.ToString(), StringComparison.Ordinal) &&
        string.Equals(row.PrincipalId, binding.PrincipalId.Value.ToString(), StringComparison.Ordinal) &&
        string.Equals(row.AgentId, binding.AgentId, StringComparison.Ordinal) &&
        string.Equals(row.DefinitionRevision, binding.DefinitionRevision, StringComparison.Ordinal) &&
        string.Equals(row.MafVersion, binding.MafVersion, StringComparison.Ordinal) &&
        string.Equals(row.SchemaVersion, binding.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(row.ProviderId, binding.ProviderId, StringComparison.Ordinal);

    // Length-prefix every UTF-8 field so delimiters, nulls, or concatenation ambiguity cannot alias bindings.
    private static string CreateBindingKey(NativeAgentSessionBinding binding)
    {
        string[] fields = [binding.SessionId.Value.ToString(), binding.TenantId.Value.ToString(), binding.PrincipalId.Value.ToString(),
            binding.AgentId, binding.DefinitionRevision, binding.MafVersion, binding.SchemaVersion, binding.ProviderId];
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    // Keep this narrow: only a provider-reported unique/primary-key violation is a create race.
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var code = type.GetProperty("SqliteExtendedErrorCode")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.Sqlite.SqliteException" && (code == "1555" || code == "2067")) return true;
            code = type.GetProperty("SqlState")?.GetValue(current)?.ToString();
            if (type.FullName == "Npgsql.PostgresException" && code == "23505") return true;
            code = type.GetProperty("Number")?.GetValue(current)?.ToString();
            if (type.FullName == "Microsoft.Data.SqlClient.SqlException" && (code == "2601" || code == "2627")) return true;
        }
        return false;
    }
}
