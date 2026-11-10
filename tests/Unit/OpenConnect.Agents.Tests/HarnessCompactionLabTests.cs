// <copyright file="HarnessCompactionLabTests.cs">
// Deterministic native Harness compaction lab (MAF 1.27.0).
// Proves native context-window compaction against a constrained-window fixture:
// reflection over Microsoft.Agents.AI.Harness (HarnessAgentOptions compaction
// surface) plus Microsoft.Agents.AI (CompactionStrategy family), then native
// compaction via TruncationCompactionStrategy and the default
// ContextWindowCompactionStrategy through the documented CompactionProvider
// entry point. No custom summarizer, no model calls, no network.
// </copyright>

using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
#pragma warning disable MAAI001 // Compaction surface is evaluation-only in MAF 1.27.0; this lab pins and records the exact surface.
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves native Harness compaction on MAF 1.27.0 with a deterministic stub
/// (no model calls). Compaction is the native truncation/tool-eviction path;
/// summarization strategies that require an LLM are intentionally not used.
/// </summary>
public sealed class HarnessCompactionLabTests
{
    // Constrained-window fixture: tiny on purpose so the native token estimate
    // (ByteCount / 4, no tokenizer download) deterministically exceeds budget.
    private const int FixtureMaxContextWindowTokens = 64;
    private const int FixtureMaxOutputTokens = 16;

    private readonly ITestOutputHelper _output;

    public HarnessCompactionLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Harness_compaction_surface_matches_expected_native_members()
    {
        var harnessAssembly = typeof(HarnessAgent).Assembly;
        var compactionAssembly = typeof(CompactionStrategy).Assembly;
        _output.WriteLine($"Harness assembly: {harnessAssembly.GetName().Name} {harnessAssembly.GetName().Version}");
        _output.WriteLine($"Compaction assembly: {compactionAssembly.GetName().Name} {compactionAssembly.GetName().Version}");

        var optionsType = harnessAssembly.GetType("Microsoft.Agents.AI.HarnessAgentOptions");
        Assert.NotNull(optionsType);
        var properties = optionsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);
        _output.WriteLine("HarnessAgentOptions members: " + string.Join(", ", properties.Keys.OrderBy(name => name, StringComparer.Ordinal)));

        Assert.True(properties.TryGetValue("CompactionStrategy", out var compactionStrategyProperty), "Missing HarnessAgentOptions.CompactionStrategy.");
        Assert.True(properties.TryGetValue("DisableCompaction", out var disableCompactionProperty), "Missing HarnessAgentOptions.DisableCompaction.");
        Assert.True(properties.TryGetValue("MaxContextWindowTokens", out var maxWindowProperty), "Missing HarnessAgentOptions.MaxContextWindowTokens.");
        Assert.True(properties.TryGetValue("MaxOutputTokens", out var maxOutputProperty), "Missing HarnessAgentOptions.MaxOutputTokens.");

        Assert.Contains("CompactionStrategy", compactionStrategyProperty!.PropertyType.Name, StringComparison.Ordinal);
        Assert.Equal(typeof(bool), disableCompactionProperty!.PropertyType);
        Assert.Equal(typeof(int?), maxWindowProperty!.PropertyType);
        Assert.Equal(typeof(int?), maxOutputProperty!.PropertyType);
        _output.WriteLine($"CompactionStrategy: {compactionStrategyProperty.PropertyType.FullName}; DisableCompaction: {disableCompactionProperty.PropertyType.FullName}; MaxContextWindowTokens/MaxOutputTokens: {maxWindowProperty.PropertyType.FullName}.");

        // Fail closed when the native compaction entry points are absent.
        Assert.NotNull(compactionAssembly.GetType("Microsoft.Agents.AI.Compaction.CompactionStrategy"));
        Assert.NotNull(compactionAssembly.GetType("Microsoft.Agents.AI.Compaction.ContextWindowCompactionStrategy"));
        Assert.NotNull(compactionAssembly.GetType("Microsoft.Agents.AI.Compaction.TruncationCompactionStrategy"));
        Assert.NotNull(compactionAssembly.GetType("Microsoft.Agents.AI.Compaction.CompactionProvider"));
        Assert.NotNull(compactionAssembly.GetType("Microsoft.Agents.AI.Compaction.CompactionMessageIndex"));
        Assert.NotNull(compactionAssembly.GetType("Microsoft.Agents.AI.Compaction.CompactionTriggers"));
        var compactMethod = typeof(CompactionStrategy).GetMethod(
            "CompactAsync",
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(CompactionMessageIndex), typeof(Microsoft.Extensions.Logging.ILogger), typeof(CancellationToken)]);
        Assert.NotNull(compactMethod);
        Assert.Equal(typeof(ValueTask<bool>), compactMethod!.ReturnType);
        _output.WriteLine($"Native entry point: {compactMethod} -> ValueTask<bool> (true = native compaction occurred).");
    }

    [Fact]
    public async Task Native_truncation_compaction_fires_on_constrained_fixture()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        List<ChatMessage> messages = BuildOverflowMessages(turns: 6);
        var index = BuildIndex(messages, firstTurn: 1);
        var before = Snapshot(index);
        _output.WriteLine($"Before: {before}");

        // Deterministic native strategy: no model calls, message-count trigger.
        CompactionStrategy strategy = new TruncationCompactionStrategy(
            CompactionTriggers.MessagesExceed(4),
            minimumPreservedGroups: 1,
            target: null);

        bool compacted = await strategy.CompactAsync(index, logger: null, ct);
        var after = Snapshot(index);
        _output.WriteLine($"After: {after}; native CompactAsync returned {compacted}.");

        Assert.True(compacted, "Native TruncationCompactionStrategy did not fire on the overflow fixture.");
        Assert.True(
            after.IncludedMessages < before.IncludedMessages,
            $"Expected native compaction to reduce included messages (before={before.IncludedMessages}, after={after.IncludedMessages}).");
    }

    [Fact]
    public async Task Constrained_window_default_strategy_compacts_via_documented_entry_point()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // The exact default the Harness builds when both window values are set
        // and no custom CompactionStrategy is provided (tool-eviction then
        // truncation; no LLM summarization involved).
        CompactionStrategy strategy = new ContextWindowCompactionStrategy(
            FixtureMaxContextWindowTokens,
            FixtureMaxOutputTokens);
        _output.WriteLine(
            $"Window fixture: MaxContextWindowTokens={FixtureMaxContextWindowTokens}, MaxOutputTokens={FixtureMaxOutputTokens}, " +
            $"strategy={strategy.GetType().FullName}.");

        // Options round-trip: the constrained fixture is accepted by the real
        // Harness options contract and compaction stays enabled by default.
        var options = new HarnessAgentOptions
        {
            MaxContextWindowTokens = FixtureMaxContextWindowTokens,
            MaxOutputTokens = FixtureMaxOutputTokens,
        };
        Assert.Equal(FixtureMaxContextWindowTokens, options.MaxContextWindowTokens);
        Assert.Equal(FixtureMaxOutputTokens, options.MaxOutputTokens);
        Assert.False(options.DisableCompaction);
        Assert.Null(options.CompactionStrategy);
        _output.WriteLine("HarnessAgentOptions round-trip: window values retained, DisableCompaction=false, CompactionStrategy=null (native default applies).");

        List<ChatMessage> messages = BuildOverflowMessages(turns: 6);
        var probe = BuildIndex(messages, firstTurn: 1);
        _output.WriteLine($"Source state before: {Snapshot(probe)}.");

        var compacted = (await CompactionProvider.CompactAsync(strategy, messages, logger: null, ct)).ToList();
        _output.WriteLine($"Documented entry point CompactionProvider.CompactAsync: source={messages.Count} messages -> compacted={compacted.Count} messages.");

        Assert.True(
            compacted.Count < messages.Count,
            $"Expected the native window strategy to reduce messages (source={messages.Count}, compacted={compacted.Count}).");
        var afterProbe = BuildIndex(compacted, firstTurn: 1);
        _output.WriteLine($"Source state after: {Snapshot(afterProbe)}.");
    }

    [Fact]
    public async Task Harness_agent_runs_turns_with_compaction_enabled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;

        var options = new HarnessAgentOptions
        {
            Id = "lab-harness-compaction",
            Name = "LabHarnessCompaction",
            Description = "Deterministic compaction-enabled Harness lab.",
            MaxContextWindowTokens = FixtureMaxContextWindowTokens,
            MaxOutputTokens = FixtureMaxOutputTokens,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = true,
        };
        _output.WriteLine(
            $"HarnessAgentOptions: compaction enabled via MaxContextWindowTokens={FixtureMaxContextWindowTokens}, MaxOutputTokens={FixtureMaxOutputTokens}; built-ins disabled (no network).");

        var client = new LabCompactionChatClient();
        HarnessAgent agent = client.AsHarnessAgent(options);

        AgentSession session = await agent.CreateSessionAsync(ct);
        AgentResponse first = await agent.RunAsync("lab-compact-turn-1: seed the constrained window", session, cancellationToken: ct);
        AgentResponse second = await agent.RunAsync("lab-compact-turn-2: continue under the constrained window", session, cancellationToken: ct);

        Assert.StartsWith("ack:lab-compact-turn-1", first.Text, StringComparison.Ordinal);
        Assert.StartsWith("ack:lab-compact-turn-2", second.Text, StringComparison.Ordinal);
        _output.WriteLine($"Compaction-enabled Harness loop: 2 turns acked; inner model calls={client.Requests.Count}, last call saw {client.Requests[^1].Count} messages.");
    }

    private static List<ChatMessage> BuildOverflowMessages(int turns)
    {
        var messages = new List<ChatMessage>();
        for (int turn = 1; turn <= turns; turn++)
        {
            // ~120 chars each so the ByteCount/4 estimate deterministically
            // overflows the 64-token constrained fixture window.
            messages.Add(new ChatMessage(ChatRole.User, $"lab-compact-turn-{turn}: " + new string('u', 100)));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"ack:lab-compact-turn-{turn} " + new string('a', 100)));
        }

        return messages;
    }

    private CompactionMessageIndex BuildIndex(IReadOnlyList<ChatMessage> messages, int firstTurn)
    {
        // CompactionMessageIndex.Create is internal in MAF 1.27.0; the public
        // construction path is the public ctor plus the public AddGroup API,
        // which performs the same native grouping/token estimation.
        var index = new CompactionMessageIndex([], null);
        int turn = firstTurn;
        foreach (var message in messages)
        {
            var kind = message.Role == ChatRole.User ? CompactionGroupKind.User : CompactionGroupKind.AssistantText;
            index.AddGroup(kind, [message], turn);
            if (kind == CompactionGroupKind.AssistantText)
            {
                turn++;
            }
        }

        return index;
    }

    private static (int Groups, int Messages, long Tokens, int IncludedMessages, long IncludedTokens) Snapshot(CompactionMessageIndex index)
        => (index.TotalGroupCount, index.TotalMessageCount, index.TotalTokenCount, index.IncludedMessageCount, index.IncludedTokenCount);

    private sealed class LabCompactionChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("harness-compaction-lab");

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            var prompt = snapshot
                .LastOrDefault(message => message.Role == ChatRole.User &&
                    (message.Text ?? string.Empty).StartsWith("lab-compact-turn-", StringComparison.Ordinal))?.Text ?? "lab-compact-ack";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ack:" + prompt)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
