using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class NativeAgentSessionPersistenceTests
{
    public static IEnumerable<object?[]> InvalidBindingValues()
    {
        yield return ["SessionId", null];
        yield return ["TenantId", null];
        yield return ["PrincipalId", null];
        foreach (var field in new[] { "AgentId", "DefinitionRevision", "MafVersion", "SchemaVersion", "ProviderId" })
        {
            yield return [field, null];
            yield return [field, ""];
            yield return [field, " \t "];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidBindingValues))]
    public void Binding_rejects_default_identifiers_and_missing_metadata(string field, string? invalidValue)
    {
        var sessionId = field == "SessionId" ? default : new SessionId(Ulid.NewUlid());
        var tenantId = field == "TenantId" ? default : new TenantId(Ulid.NewUlid());
        var principalId = field == "PrincipalId" ? default : new PrincipalId(Ulid.NewUlid());

        void CreateBinding() => _ = new NativeAgentSessionBinding(
            sessionId,
            tenantId,
            principalId,
            field == "AgentId" ? invalidValue! : "actor",
            field == "DefinitionRevision" ? invalidValue! : "revision-1",
            field == "MafVersion" ? invalidValue! : "maf-test",
            field == "SchemaVersion" ? invalidValue! : "schema-1",
            field == "ProviderId" ? invalidValue! : "provider-test");
        var isIdentifier = field is nameof(NativeAgentSessionBinding.SessionId)
            or nameof(NativeAgentSessionBinding.TenantId)
            or nameof(NativeAgentSessionBinding.PrincipalId);
        var exception = !isIdentifier && invalidValue is null
            ? Assert.Throws<ArgumentNullException>(CreateBinding)
            : Assert.Throws<ArgumentException>(CreateBinding);

        Assert.Equal(field, exception.ParamName);
    }

    [Fact]
    public void Binding_preserves_exact_case_and_untrimmed_metadata()
    {
        var binding = new NativeAgentSessionBinding(
            new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            " Actor ", " Rev-A ", " MAF-X ", " Schema-X ", " Provider-X ");

        Assert.Equal(" Actor ", binding.AgentId);
        Assert.Equal(" Rev-A ", binding.DefinitionRevision);
        Assert.Equal(" MAF-X ", binding.MafVersion);
        Assert.Equal(" Schema-X ", binding.SchemaVersion);
        Assert.Equal(" Provider-X ", binding.ProviderId);
    }

    [Fact]
    public void Session_record_defensively_copies_ciphertext_on_input_and_output()
    {
        var binding = CreateBinding();
        byte[] ciphertext = [1, 2, 3];
        var record = new NativeAgentSessionRecord(binding, 1, ciphertext);

        ciphertext[0] = 9;
        var returnedCopy = record.GetCiphertextCopy();
        returnedCopy[1] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, record.GetCiphertextCopy());
    }

    [Fact]
    public void Session_record_rejects_invalid_version_and_null_inputs()
    {
        var binding = CreateBinding();

        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeAgentSessionRecord(binding, 0, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeAgentSessionRecord(binding, -1, [1]));
        Assert.Throws<ArgumentNullException>(() => new NativeAgentSessionRecord(null!, 1, [1]));
        Assert.Throws<ArgumentNullException>(() => new NativeAgentSessionRecord(binding, 1, null!));
    }

    [Fact]
    public async Task Save_load_and_continue_round_trip_native_session()
    {
        var client = new StubChatClient();
        var agent = CreateAgent(client);
        var binding = CreateBinding();
        var store = new Store();
        var protector = new Protector();
        var persistence = new NativeAgentSessionPersistence(store, protector);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("first turn", session);

        Assert.Equal(NativeAgentSessionSaveOutcome.Saved,
            await persistence.SaveAsync(agent, session, binding, 0));
        var loaded = await persistence.LoadAsync(agent, binding);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Value.Version);
        await agent.RunAsync("continued turn", loaded.Value.Session);
        Assert.Contains(client.Requests.Last(), message => message.Role == ChatRole.User && message.Text == "first turn");
        Assert.Equal("continued turn", client.Requests.Last().Last(message => message.Role == ChatRole.User).Text);
        Assert.True(protector.ProtectCalled);
        Assert.True(protector.UnprotectCalled);
    }

    [Fact]
    public async Task Rejects_mismatched_store_binding_before_unprotect()
    {
        var agent = CreateAgent(new StubChatClient());
        var binding = CreateBinding();
        var protector = new Protector();
        var differentBinding = new NativeAgentSessionBinding(
            binding.SessionId, binding.TenantId, binding.PrincipalId, binding.AgentId,
            binding.DefinitionRevision, binding.MafVersion, binding.SchemaVersion, "other-provider");
        var store = new Store { ReturnedRecord = new NativeAgentSessionRecord(differentBinding, 1, [1]) };
        var persistence = new NativeAgentSessionPersistence(store, protector);

        await Assert.ThrowsAsync<InvalidDataException>(() => persistence.LoadAsync(agent, binding));
        Assert.False(protector.UnprotectCalled);
    }

    [Fact]
    public async Task Save_preserves_compare_and_swap_conflict()
    {
        var agent = CreateAgent(new StubChatClient());
        var store = new Store { SaveOutcome = NativeAgentSessionSaveOutcome.Conflict };
        var persistence = new NativeAgentSessionPersistence(store, new Protector());

        var outcome = await persistence.SaveAsync(agent, await agent.CreateSessionAsync(), CreateBinding(), 0);

        Assert.Equal(NativeAgentSessionSaveOutcome.Conflict, outcome);
        Assert.Equal(1, store.SaveCalls);
    }

    [Fact]
    public async Task Protection_failure_does_not_write()
    {
        var agent = CreateAgent(new StubChatClient());
        var store = new Store();
        var protector = new Protector { FailProtect = true };
        var persistence = new NativeAgentSessionPersistence(store, protector);
        var session = await agent.CreateSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveAsync(agent, session, CreateBinding(), 0));

        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Cancellation_before_store_save_does_not_write()
    {
        var agent = CreateAgent(new StubChatClient());
        var store = new Store();
        using var cancellation = new CancellationTokenSource();
        var protector = new Protector { CancellationSource = cancellation };
        var persistence = new NativeAgentSessionPersistence(store, protector);
        var session = await agent.CreateSessionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            persistence.SaveAsync(agent, session, CreateBinding(), 0, cancellation.Token));

        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Version_overflow_is_rejected_before_serialization_or_write()
    {
        var agent = CreateAgent(new StubChatClient());
        var store = new Store();
        var persistence = new NativeAgentSessionPersistence(store, new Protector());
        var session = await agent.CreateSessionAsync();

        await Assert.ThrowsAsync<OverflowException>(() =>
            persistence.SaveAsync(agent, session, CreateBinding(), long.MaxValue));

        Assert.Equal(0, store.SaveCalls);
    }

    private static AIAgent CreateAgent(StubChatClient client) =>
        new MafRealAgentFactory(new Registry(), client).CreateAgent(new AgentId("actor"));

    private static NativeAgentSessionBinding CreateBinding() => new(
        new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        "actor", "revision-1", "maf-test", "schema-1", "provider-test");

    private sealed class Store : INativeAgentSessionStore
    {
        public NativeAgentSessionRecord? ReturnedRecord { get; set; }
        public NativeAgentSessionSaveOutcome SaveOutcome { get; set; } = NativeAgentSessionSaveOutcome.Saved;
        public int SaveCalls { get; private set; }

        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ReturnedRecord);
        }

        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SaveCalls++;
            if (SaveOutcome == NativeAgentSessionSaveOutcome.Saved)
                ReturnedRecord = record;
            return Task.FromResult(SaveOutcome);
        }
    }

    private sealed class Protector : INativeAgentSessionProtector
    {
        public bool ProtectCalled { get; private set; }
        public bool UnprotectCalled { get; private set; }
        public bool FailProtect { get; init; }
        public CancellationTokenSource? CancellationSource { get; init; }

        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ProtectCalled = true;
            if (FailProtect)
                throw new InvalidOperationException("test protection failure");
            CancellationSource?.Cancel();
            return Task.FromResult(plaintext.ToArray());
        }

        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UnprotectCalled = true;
            return Task.FromResult(ciphertext.ToArray());
        }
    }

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("actor"), "Actor");
        public AgentDefinition? Get(AgentId id) => Definition;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class StubChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("native-session-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            Requests.Add(request);
            var text = request.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
