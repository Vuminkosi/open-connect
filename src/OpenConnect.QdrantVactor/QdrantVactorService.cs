using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OpenConnect.QdrantVactor;

/// <summary>Runs the sequential event projection worker with bounded concurrency of one.</summary>
public sealed class QdrantVactorService(QdrantVactorIndexer indexer, IOptions<QdrantVactorOptions> options) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        options.Value.Enabled && options.Value.SubscribeToAgentEvents
            ? indexer.RunAsync(stoppingToken)
            : Task.CompletedTask;
}
