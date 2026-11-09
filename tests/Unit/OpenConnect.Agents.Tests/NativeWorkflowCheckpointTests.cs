using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class NativeWorkflowCheckpointTests
{
    [Fact]
    public async Task Native_workflow_commits_checkpoint_that_fresh_manager_can_retrieve()
    {
        const string sessionId = "native-checkpoint-test-session";
        var store = new JsonCheckpointStoreForTest();
        var manager = CheckpointManager.CreateJson(store);
        var agent = CreateAgent(new DeterministicChatClient());
        var workflow = AgentWorkflowBuilder.BuildSequential([agent]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var checkpointingEnvironment = InProcessExecution.Default.WithCheckpointing(manager);
        await using (var run = await checkpointingEnvironment.RunStreamingAsync(
            workflow,
            new List<ChatMessage> { new(ChatRole.User, "checkpoint probe") },
            sessionId,
            timeout.Token))
        {
            await foreach (var _ in run.WatchStreamAsync(timeout.Token))
            {
            }
        }

        var checkpoint = await manager.GetLatestCheckpointAsync(sessionId, timeout.Token);
        Assert.NotNull(checkpoint);
        Assert.NotEmpty(store.SerializedCheckpoints);
        var committedIndex = await store.RetrieveIndexAsync(sessionId);
        Assert.Equal(
            store.SerializedCheckpoints.Select(item => item.Info.CheckpointId),
            committedIndex.Select(item => item.CheckpointId));

        // Rehydrate only the native JSON checkpoint objects into fresh test infrastructure.
        var rehydratedStore = JsonCheckpointStoreForTest.FromSerialized(store.SerializedCheckpoints);
        var freshManager = CheckpointManager.CreateJson(rehydratedStore);
        var retrieved = await freshManager.GetLatestCheckpointAsync(sessionId, timeout.Token);

        Assert.NotNull(retrieved);
        Assert.Equal(checkpoint!.CheckpointId, retrieved!.CheckpointId);
        Assert.Equal(checkpoint.SessionId, retrieved.SessionId);
        Assert.Equal(store.SerializedCheckpoints.Count, rehydratedStore.SerializedCheckpoints.Count);
    }

    private static AIAgent CreateAgent(IChatClient client) =>
        new MafRealAgentFactory(new TestRegistry(), client).CreateAgent(new AgentId("checkpoint-probe"));

    private sealed class TestRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("checkpoint-probe"), "Checkpoint probe");

        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class DeterministicChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("native-checkpoint-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, input)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class JsonCheckpointStoreForTest : ICheckpointStore<JsonElement>
    {
        private readonly List<StoredCheckpoint> _checkpoints = [];

        public IReadOnlyList<StoredCheckpoint> SerializedCheckpoints => _checkpoints;

        public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
            string sessionId,
            CheckpointInfo? withParent = null)
        {
            IEnumerable<CheckpointInfo> index = _checkpoints
                .Where(checkpoint => checkpoint.SessionId == sessionId)
                .Where(checkpoint => withParent is null || checkpoint.ParentCheckpointId == withParent.CheckpointId)
                .Select(checkpoint => checkpoint.Info)
                .ToArray();
            return ValueTask.FromResult(index);
        }

        public ValueTask<CheckpointInfo> CreateCheckpointAsync(
            string sessionId,
            JsonElement value,
            CheckpointInfo? parent)
        {
            var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
            _checkpoints.Add(new StoredCheckpoint(sessionId, value.Clone(), info, parent?.CheckpointId));
            return ValueTask.FromResult(info);
        }

        public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
        {
            var checkpoint = _checkpoints.LastOrDefault(item =>
                item.SessionId == sessionId && item.Info.CheckpointId == key.CheckpointId);
            return checkpoint is null
                ? ValueTask.FromException<JsonElement>(new KeyNotFoundException("Checkpoint not found."))
                : ValueTask.FromResult(checkpoint.Value.Clone());
        }

        public static JsonCheckpointStoreForTest FromSerialized(IEnumerable<StoredCheckpoint> checkpoints)
        {
            var store = new JsonCheckpointStoreForTest();
            store._checkpoints.AddRange(checkpoints.Select(checkpoint => checkpoint with
            {
                Value = checkpoint.Value.Clone()
            }));
            return store;
        }

        public sealed record StoredCheckpoint(string SessionId, JsonElement Value, CheckpointInfo Info,
            string? ParentCheckpointId);
    }
}
