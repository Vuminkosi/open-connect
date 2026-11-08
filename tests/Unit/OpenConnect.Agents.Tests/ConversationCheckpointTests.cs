using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ConversationCheckpointTests
{
    [Fact]
    public async Task Import_into_new_runner_continues_sequence_without_copying_history()
    {
        var first = CreateRunner();
        await first.TurnAsync("private transcript");
        var checkpoint = first.ExportCheckpoint("conversation-1");

        var resumed = CreateRunner();
        resumed.ImportCheckpoint(checkpoint, "conversation-1");
        Assert.Empty(resumed.History);
        await resumed.TurnAsync("next");

        Assert.Equal(2, resumed.ExportCheckpoint("conversation-1").TurnSequence);
        Assert.DoesNotContain(checkpoint.ToString(), "private transcript", StringComparison.Ordinal);
        Assert.DoesNotContain(resumed.History, message => message.Text == "private transcript");
    }

    [Theory]
    [InlineData("oc-checkpoint/v99")]
    [InlineData("")]
    public void Import_rejects_incompatible_version_with_migration_guidance(string version)
    {
        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 3, []) with { Version = version };
        var exception = Assert.Throws<NotSupportedException>(() =>
            ConversationCheckpointCodec.Import(checkpoint, "lead", "session"));

        Assert.Contains(version, exception.Message, StringComparison.Ordinal);
        Assert.Contains("oc-checkpoint/v1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Export a new checkpoint from the current runner; cross-version restore is not supported.",
            exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("oc-checkpoint/v99")]
    [InlineData("")]
    public void Import_rejects_unsupported_version_without_mutating_runner(string version)
    {
        var runner = CreateRunner();
        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 3, []) with
        {
            Version = version
        };

        Assert.Throws<NotSupportedException>(() => runner.ImportCheckpoint(checkpoint, "session"));

        Assert.Equal(0, runner.ExportCheckpoint("session").TurnSequence);
    }

    [Theory]
    [InlineData("another-lead", "session")]
    [InlineData("lead", "another-session")]
    public void Import_rejects_cross_lead_or_session_mixing(string lead, string session)
    {
        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 1, []);
        Assert.Throws<InvalidOperationException>(() => ConversationCheckpointCodec.Import(checkpoint, lead, session));
    }

    [Fact]
    public void Export_and_import_allow_same_key_for_distinct_child_agents()
    {
        ChildSessionCheckpoint[] children =
        [
            new("child-a", "shared-key", 2),
            new("child-b", "shared-key", 4)
        ];

        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 1, children);
        var imported = ConversationCheckpointCodec.Import(checkpoint, "lead", "session");

        Assert.Equal(children, imported.ChildSessions);
    }

    [Fact]
    public void Export_and_import_accept_checkpoint_at_child_limit()
    {
        var children = Enumerable.Range(0, ConversationCheckpointCodec.MaxCheckpointChildren)
            .Select(index => new ChildSessionCheckpoint($"child-{index}", $"session-{index}", 0)).ToArray();

        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 1, children);

        Assert.Equal(ConversationCheckpointCodec.MaxCheckpointChildren,
            ConversationCheckpointCodec.Import(checkpoint, "lead", "session").ChildSessions.Count);
    }

    [Fact]
    public void Export_and_import_reject_children_over_limit_with_count_details()
    {
        var children = Enumerable.Range(0, ConversationCheckpointCodec.MaxCheckpointChildren + 1)
            .Select(index => new ChildSessionCheckpoint($"child-{index}", $"session-{index}", 0)).ToArray();
        var exportException = Assert.Throws<ArgumentException>(() =>
            ConversationCheckpointCodec.Export("lead", "session", 1, children));
        Assert.Contains(ConversationCheckpointCodec.MaxCheckpointChildren.ToString(), exportException.Message);
        Assert.Contains(children.Length.ToString(), exportException.Message);

        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 1, []) with
        {
            ChildSessions = children
        };
        var importException = Assert.Throws<ArgumentException>(() =>
            ConversationCheckpointCodec.Import(checkpoint, "lead", "session"));
        Assert.Contains(ConversationCheckpointCodec.MaxCheckpointChildren.ToString(), importException.Message);
        Assert.Contains(children.Length.ToString(), importException.Message);
    }

    [Fact]
    public void Import_rejects_over_limit_without_mutating_runner()
    {
        var runner = CreateRunner();
        var children = Enumerable.Range(0, ConversationCheckpointCodec.MaxCheckpointChildren + 1)
            .Select(index => new ChildSessionCheckpoint($"child-{index}", $"session-{index}", 0)).ToArray();
        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 1, []) with
        {
            ChildSessions = children
        };

        Assert.Throws<ArgumentException>(() => runner.ImportCheckpoint(checkpoint, "session"));

        Assert.Equal(0, runner.ExportCheckpoint("session").TurnSequence);
    }

    [Theory]
    [InlineData("", "child-session", 1)]
    [InlineData("child", "   ", 1)]
    [InlineData("child", "child-session", -1)]
    public void Import_rejects_invalid_child_metadata_without_mutating_runner(
        string agentId, string childSessionKey, int historyCount)
    {
        var runner = CreateRunner();
        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 3, []) with
        {
            ChildSessions = [new ChildSessionCheckpoint(agentId, childSessionKey, historyCount)]
        };

        Assert.Throws<ArgumentException>(() => runner.ImportCheckpoint(checkpoint, "session"));

        Assert.Equal(0, runner.ExportCheckpoint("session").TurnSequence);
        Assert.Empty(runner.History);
        Assert.Empty(runner.GetTrace());
    }

    [Fact]
    public void Export_and_import_reject_duplicate_child_agent_and_key_pair()
    {
        ChildSessionCheckpoint[] children =
        [
            new("child-a", "same-key", 2),
            new("child-a", "same-key", 4)
        ];

        Assert.Throws<ArgumentException>(() =>
            ConversationCheckpointCodec.Export("lead", "session", 1, children));

        var invalidCheckpoint = ConversationCheckpointCodec.Export("lead", "session", 1, []) with
        {
            ChildSessions = children
        };
        Assert.Throws<ArgumentException>(() =>
            ConversationCheckpointCodec.Import(invalidCheckpoint, "lead", "session"));
    }

    [Fact]
    public async Task Import_rejects_nonempty_runner()
    {
        var runner = CreateRunner();
        await runner.TurnAsync("existing");
        var checkpoint = ConversationCheckpointCodec.Export("lead", "session", 2, []);
        Assert.Throws<InvalidOperationException>(() => runner.ImportCheckpoint(checkpoint, "session"));
    }

    private static MafConversationRunner CreateRunner() => new(
        new MafRealAgentFactory(new TestRegistry(), new TestChatClient()), new AgentId("lead"));

    private sealed class TestRegistry : IAgentDefinitionRegistry
    {
        private readonly AgentDefinition _definition = new(new AgentId("lead"), "Lead");
        public AgentDefinition? Get(AgentId id) => id == _definition.Id ? _definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [_definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class TestChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("checkpoint-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "response")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "response");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
