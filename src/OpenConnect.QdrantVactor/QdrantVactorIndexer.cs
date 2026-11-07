using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
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

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        if (!configured.Enabled || !configured.SubscribeToAgentEvents) return;
        if (subscription is null)
            throw new InvalidOperationException("QdrantVactor is enabled for agent event subscriptions, but no IAgentEventSubscriptionV2 is registered.");
        await foreach (var item in subscription.SubscribeAsync(
                           new AgentEventSubscriptionOptions("openconnect-qdrant-vactor", ApprovedEvents), cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var delivery = item;
            var committedEvent = delivery.Event;
            if (string.IsNullOrWhiteSpace(committedEvent.TenantId) || !ApprovedEvents.Contains(committedEvent.EventType, StringComparer.Ordinal))
            {
                await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            var started = Stopwatch.GetTimestamp();
            var outcome = "success";
            using var activity = Instrumentation.SafeStartActivity("vectors", "index", [new("operation", "index"), new("component", "qdrantvactor")]);
            try
            {
                // Only the sanitized projection reader may supply content to the embedder.
                var content = await projections.ReadSanitizedAsync(committedEvent.ProjectionReference, committedEvent.TenantId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                {
                    await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var vector = await embeddings.GenerateAsync(content, cancellationToken).ConfigureAwait(false);
                var idSource = $"{committedEvent.EventId:N}:{committedEvent.Sequence}:deterministic-stub-v1";
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource))).ToLowerInvariant();
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
                await delivery.NakAsync("indexing_failed", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Instrumentation.SafeRecordVector(Stopwatch.GetElapsedTime(started).TotalMilliseconds, "index", outcome);
            }
        }
    }
}
