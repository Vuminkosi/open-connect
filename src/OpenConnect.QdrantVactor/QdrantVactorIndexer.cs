using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using OpenConnect.Abstractions;
using OpenConnect.Observability;

namespace OpenConnect.QdrantVactor;

/// <summary>
/// Projects approved committed outputs from a trusted subscription publisher. The subscription is the
/// trusted boundary: it delivers committed events originating from the durable outbox with server-issued
/// tenant, session, and execution scope. Events must still carry a non-empty tenant and an approved type.
/// Deliveries are explicitly acknowledged after durable upsert or a terminal skip; transient failures are rejected.
/// </summary>
public sealed class QdrantVactorIndexer(
    IProjectionReader projections,
    IEmbeddingGenerator embeddings,
    IVectorStore store,
    IOptions<QdrantVactorOptions> options,
    IAgentEventSubscriptionV2? subscription = null)
{
    private static readonly string[] ApprovedEvents = ["task.completed", "agent.memory.candidate", "agent.execution.summarized"];
    internal ILogger<QdrantVactorIndexer>? Logger { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        if (!configured.Enabled || !configured.SubscribeToAgentEvents)
            return;
        if (subscription is null)
            throw new InvalidOperationException("QdrantVactor is enabled for agent event subscriptions, but no IAgentEventSubscriptionV2 is registered.");
        await foreach (var item in subscription.SubscribeAsync(
                           new AgentEventSubscriptionOptions("openconnect-qdrant-vactor", ApprovedEvents), cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var delivery = item;
            var committedEvent = delivery.Event;
            var started = Stopwatch.GetTimestamp();
            var outcome = "success";
            var stage = "projection";
            using var activity = Instrumentation.SafeStartActivity("vectors", "index", [new("operation", "index"), new("component", "qdrantvactor")]);
            try
            {
                if (string.IsNullOrWhiteSpace(committedEvent.TenantId) || !ApprovedEvents.Contains(committedEvent.EventType, StringComparer.Ordinal))
                {
                    outcome = "skipped";
                    LogEvent("skipped", string.IsNullOrWhiteSpace(committedEvent.TenantId) ? "missing_tenant" : "event_not_approved", committedEvent.EventId);
                    stage = "acknowledgement";
                    await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // Only the sanitized projection reader may supply content to the embedder.
                var content = await projections.ReadSanitizedAsync(committedEvent.ProjectionReference, committedEvent.TenantId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                {
                    outcome = "skipped";
                    LogEvent("skipped", "projection_empty", committedEvent.EventId);
                    stage = "acknowledgement";
                    await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                stage = "embedding";
                var vector = await embeddings.GenerateAsync(content, cancellationToken).ConfigureAwait(false);
                var idSource = $"{committedEvent.EventId:N}:{committedEvent.Sequence}:deterministic-stub-v1";
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource))).ToLowerInvariant();
                stage = "vector_store";
                await store.UpsertAsync(new VectorPoint(
                    id,
                    committedEvent.TenantId,
                    committedEvent.AgentId,
                    content,
                    vector,
                    committedEvent.Sequence,
                    EventId: committedEvent.EventId.ToString(),
                    SessionId: committedEvent.SessionId,
                    PrivacyClass: "Sanitized",
                    CreatedAt: committedEvent.CommittedAt), cancellationToken).ConfigureAwait(false);
                stage = "acknowledgement";
                await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "failure";
                LogEvent("failed", stage, committedEvent.EventId);
                await delivery.NakAsync("indexing_failed", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Instrumentation.SafeRecordVector(Stopwatch.GetElapsedTime(started).TotalMilliseconds, "index", outcome);
            }
        }
    }

    private void LogEvent(string outcome, string stage, Guid eventId)
    {
        try { Logger?.LogWarning("Qdrant indexing {Outcome} at {Stage} for event {EventId}.", outcome, stage, eventId); }
        catch { /* Logging must not prevent acknowledgement or redelivery. */ }
    }
}
