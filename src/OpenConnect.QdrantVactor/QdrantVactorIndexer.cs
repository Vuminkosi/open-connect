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
/// Advancing subscription only occurs after durable upsert.
/// </summary>
public sealed class QdrantVactorIndexer(
    IProjectionReader projections,
    IEmbeddingGenerator embeddings,
    IVectorStore store,
    IOptions<QdrantVactorOptions> options,
    IAgentEventSubscription? subscription = null)
{
    private static readonly string[] ApprovedEvents = ["task.completed", "agent.memory.candidate", "agent.execution.summarized"];

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        if (!configured.Enabled || !configured.SubscribeToAgentEvents) return;
        if (subscription is null)
            throw new InvalidOperationException("QdrantVactor is enabled for agent event subscriptions, but no IAgentEventSubscription is registered.");
        await foreach (var item in subscription.SubscribeAsync(
                           new AgentEventSubscriptionOptions("openconnect-qdrant-vactor", ApprovedEvents), cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(item.TenantId) || !ApprovedEvents.Contains(item.EventType, StringComparer.Ordinal)) continue;
            var started = Stopwatch.GetTimestamp();
            var outcome = "success";
            using var activity = Instrumentation.SafeStartActivity("vectors", "index", [new("operation", "index"), new("component", "qdrantvactor")]);
            try
            {
                var content = await projections.ReadSanitizedAsync(item.ProjectionReference, item.TenantId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) continue;
                var vector = await embeddings.GenerateAsync(content, cancellationToken).ConfigureAwait(false);
                var idSource = $"{item.EventId:N}:{item.Sequence}:deterministic-stub-v1";
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSource))).ToLowerInvariant();
                await store.UpsertAsync(new VectorPoint(id, item.TenantId, item.AgentId, content, vector, item.Sequence), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "failure";
                throw;
            }
            finally
            {
                Instrumentation.SafeRecordVector(Stopwatch.GetElapsedTime(started).TotalMilliseconds, "index", outcome);
            }
        }
    }
}
