using System.Collections.Immutable;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Builds immutable child-context projections without performing I/O.</summary>
public sealed class ContextProjectionBuilder
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a builder using the supplied clock, or the system UTC clock.</summary>
    /// <param name="timeProvider">Clock used to calculate projection expiry.</param>
    public ContextProjectionBuilder(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Builds a version-one projection after enforcing classification, scope, secret, and budget boundaries.</summary>
    /// <param name="parentContext">Authority and lineage of the parent execution.</param>
    /// <param name="mode">Context sharing mode.</param>
    /// <param name="dataClassAllowlist">Data classes authorized for projection.</param>
    /// <param name="requestedItems">Candidate immutable context items.</param>
    /// <param name="childAgentId">Authorized recipient agent.</param>
    /// <param name="childAllowedToolIds">Tools granted to the child.</param>
    /// <param name="childScopes">Requested child scopes, which may not exceed the parent's scope.</param>
    /// <param name="tokenBudget">Maximum total estimated token cost.</param>
    /// <param name="ttl">Positive projection lifetime.</param>
    /// <returns>The projection and count of quarantined items.</returns>
    public (AgentContextProjection Projection, int QuarantinedCount) Build(AgentExecutionContext parentContext,
        AgentContextSharingMode mode, IEnumerable<string> dataClassAllowlist, IEnumerable<ContextItem> requestedItems,
        AgentId childAgentId, IEnumerable<ToolId> childAllowedToolIds, IEnumerable<string> childScopes,
        int tokenBudget, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(dataClassAllowlist);
        ArgumentNullException.ThrowIfNull(requestedItems);
        ArgumentNullException.ThrowIfNull(childAllowedToolIds);
        ArgumentNullException.ThrowIfNull(childScopes);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == AgentContextSharingMode.None && requestedItems.Any())
            throw new UnauthorizedAccessException("Context sharing is disabled (None); no items may be projected.");
        if (tokenBudget < 0) throw new ArgumentOutOfRangeException(nameof(tokenBudget));
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));

        var allowedClasses = dataClassAllowlist.ToImmutableHashSet(StringComparer.Ordinal);
        var requested = requestedItems.ToArray();
        if (requested.Any(item => !allowedClasses.Contains(item.DataClass)))
            throw new UnauthorizedAccessException("A requested data class is not authorized for projection.");
        var permittedScopes = childScopes.Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (permittedScopes.Any(scope => !StringComparer.Ordinal.Equals(scope, parentContext.PrincipalOrWorkloadScope)))
            throw new UnauthorizedAccessException("A child context cannot widen the parent's authority scope.");

        var included = requested.Where(item => !item.ContainsSecret && !item.ToolControl).ToImmutableArray();
        if (included.Any(item => item.TokenEstimate < 0 || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.SummaryOrRef)))
            throw new ArgumentException("Context items must have an identifier, safe content, and a non-negative token estimate.", nameof(requestedItems));
        if (included.Sum(item => (long)item.TokenEstimate) > tokenBudget)
            throw new InvalidOperationException("Requested context exceeds the token budget.");

        var lineage = parentContext.ParentExecutionId is { } parentId
            ? ImmutableArray.Create(parentId, parentContext.AgentExecutionId)
            : ImmutableArray.Create(parentContext.AgentExecutionId);
        // DataClass remains a single string, with delimiters escaped to keep the set unambiguous.
        var dataClassKey = EncodeSet(allowedClasses);
        var projection = new AgentContextProjection(Ulid.NewUlid(), parentContext.SessionId,
            parentContext.AgentExecutionId, 1, mode, dataClassKey,
            included, ImmutableArray.Create(childAgentId), _timeProvider.GetUtcNow().Add(ttl), parentContext.TenantScope,
            parentContext.PolicyRevision, lineage, childAllowedToolIds.Distinct().ToImmutableArray(), permittedScopes, tokenBudget);
        return (projection, requested.Length - included.Length);
    }

    /// <summary>Creates a new append-only revision after re-authorizing the requestor and every new item.</summary>
    /// <param name="existing">Existing immutable projection.</param>
    /// <param name="requestor">Agent requesting additional context.</param>
    /// <param name="extraItems">New candidate items.</param>
    /// <returns>A new projection with incremented version and quarantined item count.</returns>
    public (AgentContextProjection Projection, int QuarantinedCount) RequestAdditional(
        AgentContextProjection existing, AgentId requestor, IEnumerable<ContextItem> extraItems)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(extraItems);
        if (existing.Mode == AgentContextSharingMode.None)
            throw new UnauthorizedAccessException("Context sharing is disabled (None); a projection cannot be extended.");
        if (!existing.AllowedAgentIds.Contains(requestor))
            throw new UnauthorizedAccessException("The requesting agent is not authorized for this projection.");
        if (_timeProvider.GetUtcNow() >= existing.ExpiresAt)
            throw new UnauthorizedAccessException("The context projection has expired.");
        var candidates = extraItems.ToArray();
        var allowedClasses = DecodeSet(existing.DataClass);
        if (candidates.Any(item => !allowedClasses.Contains(item.DataClass)))
            throw new UnauthorizedAccessException("Additional context exceeds the authorized data classification.");
        var safe = candidates.Where(item => !item.ContainsSecret && !item.ToolControl).ToImmutableArray();
        if (safe.Any(item => item.TokenEstimate < 0 || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.SummaryOrRef)))
            throw new ArgumentException("Context items must have an identifier, safe content, and a non-negative token estimate.", nameof(extraItems));
        var combined = existing.Items.AddRange(safe);
        if (combined.Sum(item => (long)item.TokenEstimate) > existing.TokenBudget)
            throw new InvalidOperationException("Requested context exceeds the token budget.");
        return (existing with { Version = checked(existing.Version + 1), Items = combined }, candidates.Length - safe.Length);
    }

    /// <summary>Encodes a class set as an ordinally sorted, escaped comma-separated string.</summary>
    private static string EncodeSet(IEnumerable<string> classes) =>
        string.Join(",", classes.Order(StringComparer.Ordinal).Select(Escape));

    /// <summary>Escapes backslashes and commas so a class name cannot collide with set separators.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal);

    /// <summary>Decodes escaped class names, splitting only on unescaped commas.</summary>
    private static ImmutableHashSet<string> DecodeSet(string value)
    {
        var classes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length && value[index + 1] is '\\' or ',')
            {
                current.Append(value[++index]);
            }
            else if (character == ',')
            {
                classes.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        classes.Add(current.ToString());
        return classes.ToImmutable();
    }
}
