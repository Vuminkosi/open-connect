using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenConnect.Navigator.Events;

/// <summary>Projects committed events to safe consumers once per event ID within this process.</summary>
public sealed class CommittedEventProjector(IQdrantProjectionSubscriber? subscriber = null)
{
    private static readonly Regex SecretValuePattern = new(
        @"(?:password|secret|token|api[_-]?key|bearer|private|credential)|(?:[A-Za-z0-9_-]{40,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HashSet<string> _deliveredEventIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Validates and projects a committed event. Duplicate event IDs are ignored.</summary>
    public async Task<CommittedNavigatorEvent?> ProjectAsync(
        NavigatorEvent value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!NavigatorEventTypes.IsSafe(value) || !HasSafeMetadataValues(value.Metadata))
            throw new ArgumentException("Navigator event contains unsafe or invalid data.", nameof(value));

        lock (_gate)
        {
            if (!_deliveredEventIds.Add(value.EventId))
                return null;
        }

        var projected = new CommittedNavigatorEvent(
            value.EventId,
            value.SessionId,
            value.TaskId,
            value.Sequence,
            value.CorrelationId,
            value.Type,
            value.OccurredAt,
            new Dictionary<string, string>(value.Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal));

        if (subscriber is not null)
        {
            try
            {
                await subscriber.OnCommittedAsync(projected, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace.TraceWarning("Optional Navigator event subscriber failed for event {0}: {1}", value.EventId, exception.GetType().Name);
            }
        }

        return projected;
    }

    private static bool HasSafeMetadataValues(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null || metadata.Values.All(IsSafeMetadataValue);

    private static bool IsSafeMetadataValue(string? value)
    {
        if (value is null || value.Length > 2000 || value.Contains('<') || value.Contains('>') ||
            SecretValuePattern.IsMatch(value))
            return false;

        var trimmed = value.TrimStart();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && value.Length > 1000)
            return false;

        return true;
    }
}
