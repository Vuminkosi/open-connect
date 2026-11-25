using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Rules;

namespace OpenConnect.Agents.Execution;

/// <summary>Provides the privacy egress context used when guarding model-bound chat content.</summary>
public interface IEgressContextProvider
{
    /// <summary>Gets the egress context for the current operation.</summary>
    EgressContext GetContext();
}

/// <summary>Provides a fixed egress context for firewall-guarded chat clients.</summary>
public sealed class FixedEgressContextProvider : IEgressContextProvider
{
    private readonly EgressContext _context;

    /// <summary>Creates a provider that always returns the supplied context.</summary>
    public FixedEgressContextProvider(EgressContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public EgressContext GetContext() => _context;
}

/// <summary>Configures host-side prompt-injection inspection and enforcement in <see cref="FirewallGuardedChatClient"/> (FW-050).</summary>
public sealed record PromptInjectionGuardOptions(
    bool Enabled = false,
    bool Required = true,
    double Threshold = 0.80,
    SecurityPolicyAction Action = SecurityPolicyAction.Block,
    bool MarkUntrustedContent = true,
    string DetectorId = "deberta-injection",
    string PolicyRevision = "1",
    string ApplicationId = "openconnect-agent",
    int MaxPromptBytes = 64 * 1024);

/// <summary>Firewall-decorated <see cref="IChatClient"/> that guards outbound model content fail-closed.</summary>
/// <remarks>Message text and structured function arguments and results are evaluated through the egress gate
/// before reaching the inner client; sanitized content is forwarded and denials throw without echoing content.
/// Responses are also evaluated with the output destination. Streaming responses are buffered until
/// the response has been inspected, including text split across updates of the same message.
/// Function arguments are materialized as JSON-compatible values. String results remain strings; other
/// structured results are forwarded as sanitized JSON values rather than their original CLR instances.
/// Unsupported content, including binary media, is rejected unless handled by a separate authorized modality path.
/// Optional prompt-injection inspection evaluates untrusted user and tool/retrieved content before model invocation,
/// enforcing host-determined policy actions and emitting content-free security evidence.
/// Service resolution and disposal delegate to the inner client via <see cref="DelegatingChatClient"/>.</remarks>
public sealed class FirewallGuardedChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions EgressJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const int MaxBufferedUpdates = 4096;
    private const int MaxBufferedCharacters = 1_000_000;
    private const string PromptInjectionCapability = "prompt_injection";
    private const string ChatClientBoundary = "agent-chat-client";
    private const string UntrustedBeginMarker = "[UNTRUSTED_CONTENT_BEGIN]\n";
    private const string UntrustedEndMarker = "\n[UNTRUSTED_CONTENT_END]";
    private readonly IEgressGate _gate;
    private readonly IEgressContextProvider _contextProvider;
    private readonly ISecurityWorkerClient? _securityWorkerClient;
    private readonly PromptInjectionGuardOptions? _promptInjectionOptions;
    private readonly ISecurityEvidenceSink? _evidenceSink;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a firewall-guarded decorator over the supplied inner client.</summary>
    public FirewallGuardedChatClient(IChatClient inner, IEgressGate gate, IEgressContextProvider contextProvider)
        : this(inner, gate, contextProvider, null, null, null, null)
    {
    }

    /// <summary>Creates a firewall-guarded decorator with optional prompt-injection inspection and content-free evidence recording.</summary>
    public FirewallGuardedChatClient(
        IChatClient inner,
        IEgressGate gate,
        IEgressContextProvider contextProvider,
        ISecurityWorkerClient? securityWorkerClient,
        PromptInjectionGuardOptions? promptInjectionOptions = null,
        ISecurityEvidenceSink? evidenceSink = null,
        TimeProvider? timeProvider = null)
        : base(inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _securityWorkerClient = securityWorkerClient;
        if (promptInjectionOptions is not null)
            ValidatePromptInjectionOptions(promptInjectionOptions);
        _promptInjectionOptions = promptInjectionOptions;
        _evidenceSink = evidenceSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await GuardMessageBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        await GuardContentBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        var guardedOptions = await GuardOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        var (promptGuarded, pendingEvidence) = await GuardPromptInjectionAsync(guarded, cancellationToken).ConfigureAwait(false);
        var response = await InnerClient.GetResponseAsync(promptGuarded, guardedOptions, cancellationToken).ConfigureAwait(false);
        await RecordPendingEvidenceAsync(pendingEvidence, cancellationToken).ConfigureAwait(false);
        return await GuardResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await GuardMessageBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        await GuardContentBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        var guardedOptions = await GuardOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        var (promptGuarded, pendingEvidence) = await GuardPromptInjectionAsync(guarded, cancellationToken).ConfigureAwait(false);
        var updates = new List<ChatResponseUpdate>();
        var size = 0;
        await foreach (var update in InnerClient.GetStreamingResponseAsync(promptGuarded, guardedOptions, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (updates.Count >= MaxBufferedUpdates || update is null)
                throw FailClosed();
            try
            {
                size = checked(size + JsonSerializer.Serialize(update.Contents, EgressJsonOptions).Length);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { throw FailClosed(); }
            if (size > MaxBufferedCharacters)
                throw FailClosed();
            updates.Add(update);
        }
        var response = await GuardResponseAsync(updates.ToChatResponse(), cancellationToken).ConfigureAwait(false);
        await RecordPendingEvidenceAsync(pendingEvidence, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        InnerClient.GetService(serviceType, serviceKey);

    private async Task<ChatResponse> GuardResponseAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        if (response is null)
            throw FailClosed();
        EgressContext context;
        try { context = _contextProvider.GetContext() with { Destination = EgressDestination.Output }; }
        catch { throw FailClosed(); }
        await DemandSafeIdentifierAsync(response.ResponseId, context, cancellationToken).ConfigureAwait(false);
        await DemandSafeIdentifierAsync(response.ConversationId, context, cancellationToken).ConfigureAwait(false);
        await DemandSafeIdentifierAsync(response.ModelId, context, cancellationToken).ConfigureAwait(false);
        await DemandSafeIdentifierAsync(response.FinishReason?.Value, context, cancellationToken).ConfigureAwait(false);
        if (response.ContinuationToken is not null) throw FailClosed();
        await GuardMessageBoundariesAsync(response.Messages, EgressDestination.Output, cancellationToken).ConfigureAwait(false);
        response.Messages = (await GuardAsync(response.Messages, cancellationToken, EgressDestination.Output).ConfigureAwait(false)).ToList();
        await GuardContentBoundariesAsync(response.Messages, EgressDestination.Output, cancellationToken).ConfigureAwait(false);
        response.RawRepresentation = null;
        response.AdditionalProperties = null;
        if (response.Usage is not null)
            response.Usage = SafeUsage(response.Usage);
        return response;
    }

    private async Task GuardMessageBoundariesAsync(IEnumerable<ChatMessage> messages, EgressDestination? destination, CancellationToken cancellationToken)
    {
        var fragments = messages.Select(message => message.Text ?? string.Empty).Where(text => text.Length > 0).ToArray();
        if (fragments.Length < 2)
            return;

        if (fragments.Length > MaxBufferedUpdates || fragments.Sum(text => (long)text.Length) > MaxBufferedCharacters)
            throw FailClosed();

        var combined = string.Concat(fragments);
        EgressDecision decision;
        EgressContext context;
        try
        {
            context = _contextProvider.GetContext();
            if (destination is not null) context = context with { Destination = destination.Value };
            decision = await _gate.EvaluateAsync(combined, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw FailClosed(); }
        if (!IsSafeDecision(combined, decision))
            throw FailClosed();
        var entities = decision!.DetectedEntities;
        var scalarLength = RulesSecurityDetector.CountUnicodeCodePoints(combined, 0, combined.Length);
        if (entities.Any(span => span is null || span.Start < 0 || span.Length <= 0 ||
            (long)span.Start + span.Length > scalarLength)) throw FailClosed();

        var offset = 0;
        var boundary = 0;
        var crossing = new HashSet<EntitySpan>();
        foreach (var fragment in fragments.Take(fragments.Length - 1))
        {
            offset += fragment.Length;
            // Do not split a Unicode scalar across message boundaries.
            if (char.IsHighSurrogate(combined[offset - 1]) && char.IsLowSurrogate(combined[offset]))
                throw FailClosed();
            boundary += RulesSecurityDetector.CountUnicodeCodePoints(fragment, 0, fragment.Length);
            foreach (var span in entities.Where(span => span.Start < boundary && (long)span.Start + span.Length > boundary))
                crossing.Add(span);
        }
        if (crossing.Count != 0)
        {
            await PrivacyJsonInspection.RecordBoundaryBlockAsync(_gate, context, crossing.ToArray(), cancellationToken).ConfigureAwait(false);
            throw FailClosed();
        }
    }

    private async Task GuardContentBoundariesAsync(IEnumerable<ChatMessage> messages, EgressDestination? destination,
        CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        var size = 0L;
        try
        {
            foreach (var message in messages)
            {
                foreach (var content in message.Contents)
                {
                    object? value = content switch
                    {
                        TextContent text when !string.IsNullOrEmpty(text.Text) => text.Text,
                        FunctionCallContent call => call.Arguments,
                        FunctionResultContent result => result.Result,
                        _ => null,
                    };
                    if (value is null) continue;
                    if (values.Count >= MaxBufferedUpdates) throw FailClosed();
                    var element = JsonSerializer.SerializeToElement(value, EgressJsonOptions);
                    size += element.GetRawText().Length;
                    if (size > MaxBufferedCharacters) throw FailClosed();
                    values.Add(element);
                }
            }
            if (values.Count < 2) return;
            var context = _contextProvider.GetContext();
            if (destination is not null) context = context with { Destination = destination.Value };
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, JsonSerializer.SerializeToElement(values), context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw FailClosed(); }
    }

    private async Task<IReadOnlyList<ChatMessage>> GuardAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken, EgressDestination? destination = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        EgressContext context;
        try
        {
            context = _contextProvider.GetContext();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw FailClosed();
        }

        if (context is null)
            throw FailClosed();
        if (destination is not null)
            context = context with { Destination = destination.Value };

        var guarded = new List<ChatMessage>();
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            await DemandSafeIdentifierAsync(message.MessageId, context, cancellationToken).ConfigureAwait(false);
            await DemandSafeIdentifierAsync(message.AuthorName, context, cancellationToken).ConfigureAwait(false);
            var safeText = string.IsNullOrEmpty(message.Text) ? null :
                await SanitizeAsync(message.Text, context, cancellationToken).ConfigureAwait(false);
            // Keep tool calls and other protocol content attached to the assistant message.
            // The gate evaluated the combined text, so replace all text fragments with the
            // single sanitized result without mutating the original message.
            var sanitized = message.Clone();
            sanitized.AdditionalProperties = null;
            var contents = new List<AIContent>();
            var addedText = false;
            foreach (var content in message.Contents)
            {
                if (content is TextContent)
                {
                    if (!addedText && safeText is not null)
                        contents.Add(new TextContent(safeText));
                    addedText = true;
                }
                else if (content is FunctionCallContent call && call.Arguments is not null)
                {
                    await DemandSafeIdentifierAsync(call.CallId, context, cancellationToken).ConfigureAwait(false);
                    await DemandSafeIdentifierAsync(call.Name, context, cancellationToken).ConfigureAwait(false);
                    var safeArguments = await SanitizeJsonAsync(call.Arguments, context, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using var document = JsonDocument.Parse(safeArguments);
                        var arguments = ReadJsonValue(document.RootElement) as Dictionary<string, object?> ?? throw FailClosed();
                        contents.Add(new FunctionCallContent(call.CallId, call.Name, arguments) { InformationalOnly = call.InformationalOnly });
                    }
                    catch (Exception exception) when (exception is JsonException or ArgumentException) { throw FailClosed(); }
                }
                else if (content is FunctionResultContent result && result.Result is not null)
                {
                    await DemandSafeIdentifierAsync(result.CallId, context, cancellationToken).ConfigureAwait(false);
                    var safeResult = await SanitizeJsonAsync(result.Result, context, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using var document = JsonDocument.Parse(safeResult);
                        object? safeValue = result.Result is string
                            ? document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : throw FailClosed()
                            : document.RootElement.Clone();
                        contents.Add(new FunctionResultContent(result.CallId, safeValue));
                    }
                    catch (JsonException) { throw FailClosed(); }
                }
                else if (destination == EgressDestination.Output && content is FunctionCallContent emptyCall && emptyCall.Arguments is null)
                {
                    await DemandSafeIdentifierAsync(emptyCall.CallId, context, cancellationToken).ConfigureAwait(false);
                    await DemandSafeIdentifierAsync(emptyCall.Name, context, cancellationToken).ConfigureAwait(false);
                    contents.Add(new FunctionCallContent(emptyCall.CallId, emptyCall.Name, null) { InformationalOnly = emptyCall.InformationalOnly });
                }
                else if (destination == EgressDestination.Output && content is FunctionResultContent emptyResult && emptyResult.Result is null)
                {
                    await DemandSafeIdentifierAsync(emptyResult.CallId, context, cancellationToken).ConfigureAwait(false);
                    contents.Add(new FunctionResultContent(emptyResult.CallId, null));
                }
                else if (destination == EgressDestination.Output && content is UsageContent usage)
                    contents.Add(new UsageContent(SafeUsage(usage.Details)));
                else
                    throw FailClosed();
            }
            sanitized.Contents = contents;
            sanitized.RawRepresentation = null;
            guarded.Add(sanitized);
        }

        return guarded;
    }

    private static UsageDetails SafeUsage(UsageDetails usage) => new()
    {
        InputTokenCount = usage.InputTokenCount,
        OutputTokenCount = usage.OutputTokenCount,
        TotalTokenCount = usage.TotalTokenCount,
        CachedInputTokenCount = usage.CachedInputTokenCount,
        ReasoningTokenCount = usage.ReasoningTokenCount
    };

    private static object? ReadJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name,
            property => ReadJsonValue(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ReadJsonValue).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt32(out var value) => value,
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.Number when element.TryGetDecimal(out var value) => value,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new JsonException("Unsupported JSON value.")
    };

    private async Task<string> SanitizeJsonAsync(object value, EgressContext context, CancellationToken cancellationToken)
    {
        string json;
        try
        { json = JsonSerializer.Serialize(value, EgressJsonOptions); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw FailClosed(); }
        var sanitized = await SanitizeAsync(json, context, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(sanitized);
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, document.RootElement, context, cancellationToken).ConfigureAwait(false);
            return sanitized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw FailClosed(); }
    }

    private async Task<string> SanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken)
    {
        EgressDecision decision;
        try
        { decision = await _gate.EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw FailClosed(); }
        if (!IsSafeDecision(content, decision))
            throw FailClosed();
        if (decision!.SanitizedContent!.Contains('%'))
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate,
                JsonSerializer.SerializeToElement(decision.SanitizedContent), context, cancellationToken).ConfigureAwait(false);
        return decision!.SanitizedContent!;
    }

    private async Task DemandSafeIdentifierAsync(string? value, EgressContext context, CancellationToken cancellationToken)
    {
        if (value is null) return;
        EgressDecision decision;
        try { decision = await _gate.EvaluateAsync(value, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw FailClosed(); }
        if (!IsSafeDecision(value, decision) ||
            decision.DetectedEntities.Count != 0 || !string.Equals(decision.SanitizedContent, value, StringComparison.Ordinal))
            throw FailClosed();
        if (value.Contains('%'))
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate,
                JsonSerializer.SerializeToElement(value), context, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSafeDecision(string source, EgressDecision? decision)
    {
        if (decision is null || !decision.Allowed || !Enum.IsDefined(decision.Action) ||
            decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
            decision.Reason == "privacy_enforcement_disabled" || decision.SanitizedContent is null ||
            decision.DetectedEntities is null || decision.DetectedEntities.Count > 20000)
            return false;
        var entities = decision.DetectedEntities;
        if (entities.Count == 0) return true;
        if ((long)entities.Count * decision.SanitizedContent.Length > 100_000_000 ||
            string.Equals(source, decision.SanitizedContent, StringComparison.Ordinal)) return false;
        var offsets = new List<int>();
        var offset = 0;
        foreach (var rune in source.EnumerateRunes()) { offsets.Add(offset); offset += rune.Utf16SequenceLength; }
        offsets.Add(offset);
        foreach (var entity in entities)
        {
            if (entity is null || entity.Start < 0 || entity.Length <= 0 ||
                (long)entity.Start + entity.Length >= offsets.Count) return false;
            var start = offsets[entity.Start];
            if (decision.SanitizedContent.AsSpan().Contains(source.AsSpan(start, offsets[entity.Start + entity.Length] - start),
                    StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private async Task<ChatOptions?> GuardOptionsAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        try { return await GuardOptionsCoreAsync(options, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw FailClosed(); }
    }

    private async Task<ChatOptions?> GuardOptionsCoreAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        if (options is null) return null;
        EgressContext context;
        try { context = _contextProvider.GetContext(); }
        catch { throw FailClosed(); }
        if (context is null || options.RawRepresentationFactory is not null || options.AdditionalProperties is { Count: > 0 })
            throw FailClosed();
        var safe = options.Clone();
        if (safe.Instructions is not null)
            safe.Instructions = await SanitizeAsync(safe.Instructions, context, cancellationToken).ConfigureAwait(false);
        await DemandSafeIdentifierAsync(safe.ConversationId, context, cancellationToken).ConfigureAwait(false);
        await DemandSafeIdentifierAsync(safe.ModelId, context, cancellationToken).ConfigureAwait(false);
        if (safe.ContinuationToken is not null) throw FailClosed();
        if (safe.StopSequences is not null)
            foreach (var sequence in safe.StopSequences)
                await DemandSafeIdentifierAsync(sequence, context, cancellationToken).ConfigureAwait(false);
        if (safe.ToolMode is RequiredChatToolMode required)
            await DemandSafeIdentifierAsync(required.RequiredFunctionName, context, cancellationToken).ConfigureAwait(false);
        else if (safe.ToolMode is not null && safe.ToolMode is not AutoChatToolMode && safe.ToolMode is not NoneChatToolMode)
            throw FailClosed();
        if (safe.ResponseFormat is ChatResponseFormatJson format)
        {
            await DemandSafeIdentifierAsync(format.SchemaName, context, cancellationToken).ConfigureAwait(false);
            await DemandSafeIdentifierAsync(format.SchemaDescription, context, cancellationToken).ConfigureAwait(false);
            if (format.Schema is { } schema)
                await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, schema, context, cancellationToken).ConfigureAwait(false);
        }
        else if (safe.ResponseFormat is not null && safe.ResponseFormat is not ChatResponseFormatText)
            throw FailClosed();
        if (safe.Tools is not null)
        {
            var snapshots = new List<AITool>();
            foreach (var tool in safe.Tools)
            {
                if (tool is not AIFunctionDeclaration function || tool.AdditionalProperties is { Count: > 0 })
                    throw FailClosed();
                var name = tool.Name;
                var description = tool.Description;
                var schema = function.JsonSchema.Clone();
                var resultSchema = function.ReturnJsonSchema?.Clone();
                await DemandSafeIdentifierAsync(name, context, cancellationToken).ConfigureAwait(false);
                await DemandSafeIdentifierAsync(description, context, cancellationToken).ConfigureAwait(false);
                if (schema.ValueKind != JsonValueKind.Undefined)
                    await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, schema, context, cancellationToken).ConfigureAwait(false);
                if (resultSchema is { } returnSchema && returnSchema.ValueKind != JsonValueKind.Undefined)
                    await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, returnSchema, context, cancellationToken).ConfigureAwait(false);
                snapshots.Add(function is AIFunction callable
                    ? new SafeFunction(callable, name, description, schema, resultSchema)
                    : new SafeDeclaration(name, description, schema, resultSchema));
            }
            safe.Tools = snapshots;
        }
        return safe;
    }

    private sealed class SafeDeclaration(string name, string? description, JsonElement schema, JsonElement? returnSchema) : AIFunctionDeclaration
    {
        public override string Name => name;
        public override string Description => description ?? string.Empty;
        public override JsonElement JsonSchema => schema;
        public override JsonElement? ReturnJsonSchema => returnSchema;
    }

    private sealed class SafeFunction(AIFunction inner, string name, string? description, JsonElement schema, JsonElement? returnSchema) : AIFunction
    {
        public override string Name => name;
        public override string Description => description ?? string.Empty;
        public override JsonElement JsonSchema => schema;
        public override JsonElement? ReturnJsonSchema => returnSchema;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            inner.InvokeAsync(arguments, cancellationToken);
    }

    private static PrivacyBlockedException FailClosed() =>
        new($"{ErrorCodes.PrivacyBlocked}: content blocked by privacy policy.");

    private static void ValidatePromptInjectionOptions(PromptInjectionGuardOptions options)
    {
        if (!double.IsFinite(options.Threshold) || options.Threshold < 0.0 || options.Threshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(options), "PromptInjectionGuardOptions.Threshold must be finite and between 0.0 and 1.0.");
        if (options.MaxPromptBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "PromptInjectionGuardOptions.MaxPromptBytes must be positive.");
        if (string.IsNullOrWhiteSpace(options.DetectorId))
            throw new ArgumentException("PromptInjectionGuardOptions.DetectorId is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.PolicyRevision))
            throw new ArgumentException("PromptInjectionGuardOptions.PolicyRevision is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApplicationId))
            throw new ArgumentException("PromptInjectionGuardOptions.ApplicationId is required.", nameof(options));
        if (!Enum.IsDefined(options.Action))
            throw new ArgumentOutOfRangeException(nameof(options), "PromptInjectionGuardOptions.Action must be a defined SecurityPolicyAction.");
    }

    private sealed record PendingPromptEvidence(
        string RequestId,
        string TenantId,
        string DetectorVersion,
        SecurityInspectionStatus InspectionStatus,
        SecurityEvidenceDecision Decision,
        string ReasonCode,
        IReadOnlyDictionary<string, int>? CategoryCounts,
        long DurationMs);

    private async Task<(IReadOnlyList<ChatMessage> Messages, List<PendingPromptEvidence>? PendingEvidence)> GuardPromptInjectionAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (_promptInjectionOptions is null || !_promptInjectionOptions.Enabled)
            return (messages, null);

        EgressContext context;
        try
        {
            context = _contextProvider.GetContext();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw FailClosed();
        }

        if (context is null)
            throw FailClosed();

        var tenantId = SanitizeEvidenceIdentifier(context.Tenant.Value.ToString(), "unknown-tenant", 128);
        var pending = new List<PendingPromptEvidence>();
        var resultMessages = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            var isSystem = message.Role == ChatRole.System;
            var isToolMessage = message.Role == ChatRole.Tool;
            var markMessageTextUntrusted = isToolMessage && _promptInjectionOptions.MarkUntrustedContent;
            var messageText = !isSystem ? message.Text : null;

            if (!string.IsNullOrEmpty(messageText))
            {
                var segmentOutcome = await InspectPromptSegmentAsync(messageText, tenantId, pending, cancellationToken).ConfigureAwait(false);
                if (segmentOutcome && _promptInjectionOptions.MarkUntrustedContent)
                    markMessageTextUntrusted = true;
            }

            var updatedContents = new List<AIContent>(message.Contents.Count);
            var replacedText = false;
            var messageModified = false;

            foreach (var content in message.Contents)
            {
                if (content is TextContent textContent)
                {
                    if (!isSystem && markMessageTextUntrusted && !string.IsNullOrEmpty(textContent.Text))
                    {
                        if (!replacedText)
                        {
                            updatedContents.Add(new TextContent(WrapUntrusted(messageText!)));
                            replacedText = true;
                            messageModified = true;
                        }
                    }
                    else
                    {
                        updatedContents.Add(textContent);
                    }
                }
                else if (content is FunctionResultContent funcResult && funcResult.Result is not null)
                {
                    var serialized = funcResult.Result is string s
                        ? s
                        : JsonSerializer.Serialize(funcResult.Result, EgressJsonOptions);
                    var detected = await InspectPromptSegmentAsync(serialized, tenantId, pending, cancellationToken).ConfigureAwait(false);
                    if (_promptInjectionOptions.MarkUntrustedContent && (detected || funcResult.Result is string))
                    {
                        var wrapped = WrapUntrusted(serialized);
                        updatedContents.Add(new FunctionResultContent(funcResult.CallId, wrapped));
                        messageModified = true;
                    }
                    else
                    {
                        updatedContents.Add(funcResult);
                    }
                }
                else
                {
                    updatedContents.Add(content);
                }
            }

            if (messageModified)
            {
                var clone = message.Clone();
                clone.Contents = updatedContents;
                clone.RawRepresentation = null;
                clone.AdditionalProperties = null;
                resultMessages.Add(clone);
            }
            else
            {
                resultMessages.Add(message);
            }
        }

        return (resultMessages, pending);
    }

    private async Task<bool> InspectPromptSegmentAsync(
        string text,
        string tenantId,
        List<PendingPromptEvidence> pending,
        CancellationToken cancellationToken)
    {
        var opts = _promptInjectionOptions!;
        var requestId = Guid.NewGuid().ToString("D");
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > opts.MaxPromptBytes)
        {
            await TryRecordEvidenceAsync(
                requestId,
                tenantId,
                "host-limit",
                SecurityInspectionStatus.LimitExceeded,
                SecurityEvidenceDecision.Block,
                SecurityEnforcementOutcome.Prevented,
                "prompt_limit_exceeded",
                null,
                0,
                cancellationToken).ConfigureAwait(false);
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: prompt exceeds configured maximum byte limit.");
        }

        if (_securityWorkerClient is null)
        {
            await HandlePromptDetectorUnavailableAsync(requestId, tenantId, SecurityInspectionStatus.Unavailable, cancellationToken).ConfigureAwait(false);
            return false;
        }

        SecurityInspectionResponse? response;
        try
        {
            IReadOnlySet<string>? requiredDetectors = opts.Required
                ? new HashSet<string>(StringComparer.Ordinal) { opts.DetectorId }
                : null;
            var request = new SecurityPromptInspectionRequest(
                requestId,
                opts.PolicyRevision,
                [opts.DetectorId],
                text,
                requiredDetectors);
            response = await _securityWorkerClient.InspectPromptAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await HandlePromptDetectorUnavailableAsync(requestId, tenantId, SecurityInspectionStatus.Unavailable, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var detectorStatus = response?.Detectors?.FirstOrDefault(
            d => string.Equals(d.Id, opts.DetectorId, StringComparison.Ordinal));
        if (response is null ||
            response.Status != SecurityInspectionStatus.Completed ||
            detectorStatus is null ||
            detectorStatus.Status != SecurityInspectionStatus.Completed)
        {
            var status = response?.Status ?? SecurityInspectionStatus.Unavailable;
            await HandlePromptDetectorUnavailableAsync(requestId, tenantId, status, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var detectorVersion = SanitizeEvidenceIdentifier(detectorStatus.Version, "unknown", 128);
        var durationMs = Math.Max(0, response.DurationMs);
        var injectionHits = 0;

        if (response.Findings is not null)
        {
            foreach (var finding in response.Findings)
            {
                if (finding is null || finding.Kind != SecurityFindingKind.Injection)
                    continue;
                if (!double.IsFinite(finding.Confidence) || finding.Confidence < 0.0 || finding.Confidence > 1.0)
                {
                    await TryRecordEvidenceAsync(
                        requestId,
                        tenantId,
                        detectorVersion,
                        SecurityInspectionStatus.Failed,
                        SecurityEvidenceDecision.Block,
                        SecurityEnforcementOutcome.Prevented,
                        "invalid_detector_score",
                        null,
                        durationMs,
                        cancellationToken).ConfigureAwait(false);
                    throw FailClosed();
                }

                if (finding.Confidence >= opts.Threshold)
                    injectionHits++;
            }
        }

        if (injectionHits == 0)
        {
            pending.Add(new PendingPromptEvidence(
                requestId,
                tenantId,
                detectorVersion,
                SecurityInspectionStatus.Completed,
                SecurityEvidenceDecision.Allow,
                "prompt_inspection_clean",
                null,
                durationMs));
            return false;
        }

        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [PromptInjectionCapability] = injectionHits
        };

        switch (opts.Action)
        {
            case SecurityPolicyAction.Block:
                await TryRecordEvidenceAsync(
                    requestId,
                    tenantId,
                    detectorVersion,
                    SecurityInspectionStatus.Completed,
                    SecurityEvidenceDecision.Block,
                    SecurityEnforcementOutcome.Prevented,
                    ErrorCodes.PromptInjectionDetected,
                    categoryCounts,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);
                throw new PrivacyBlockedException($"{ErrorCodes.PromptInjectionDetected}: prompt injection blocked by host security policy.");

            case SecurityPolicyAction.Review:
                await TryRecordEvidenceAsync(
                    requestId,
                    tenantId,
                    detectorVersion,
                    SecurityInspectionStatus.Completed,
                    SecurityEvidenceDecision.Review,
                    SecurityEnforcementOutcome.Prevented,
                    ErrorCodes.PromptInjectionDetected,
                    categoryCounts,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);
                throw new PrivacyBlockedException($"{ErrorCodes.PromptInjectionDetected}: prompt injection requires host review.");

            default:
                var decision = opts.Action switch
                {
                    SecurityPolicyAction.Mask => SecurityEvidenceDecision.Mask,
                    SecurityPolicyAction.Redact => SecurityEvidenceDecision.Redact,
                    _ => SecurityEvidenceDecision.Allow
                };
                pending.Add(new PendingPromptEvidence(
                    requestId,
                    tenantId,
                    detectorVersion,
                    SecurityInspectionStatus.Completed,
                    decision,
                    "prompt_injection_marked_untrusted",
                    categoryCounts,
                    durationMs));
                return true;
        }
    }

    private async Task HandlePromptDetectorUnavailableAsync(
        string requestId,
        string tenantId,
        SecurityInspectionStatus status,
        CancellationToken cancellationToken)
    {
        var opts = _promptInjectionOptions!;
        if (opts.Required)
        {
            await TryRecordEvidenceAsync(
                requestId,
                tenantId,
                "unavailable",
                status,
                SecurityEvidenceDecision.Unavailable,
                SecurityEnforcementOutcome.Prevented,
                ErrorCodes.CapabilityUnavailable,
                null,
                0,
                cancellationToken).ConfigureAwait(false);
            throw new PrivacyBlockedException($"{ErrorCodes.CapabilityUnavailable}: required prompt-injection detector '{opts.DetectorId}' is unavailable.");
        }

        await TryRecordEvidenceAsync(
            requestId,
            tenantId,
            "unavailable",
            status,
            SecurityEvidenceDecision.Unavailable,
            SecurityEnforcementOutcome.NotExecuted,
            ErrorCodes.CapabilityUnavailable,
            null,
            0,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordPendingEvidenceAsync(List<PendingPromptEvidence>? pending, CancellationToken cancellationToken)
    {
        if (pending is null || pending.Count == 0 || _evidenceSink is null)
            return;

        foreach (var item in pending)
        {
            await TryRecordEvidenceAsync(
                item.RequestId,
                item.TenantId,
                item.DetectorVersion,
                item.InspectionStatus,
                item.Decision,
                SecurityEnforcementOutcome.Executed,
                item.ReasonCode,
                item.CategoryCounts,
                item.DurationMs,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryRecordEvidenceAsync(
        string requestId,
        string tenantId,
        string detectorVersion,
        SecurityInspectionStatus inspectionStatus,
        SecurityEvidenceDecision decision,
        SecurityEnforcementOutcome enforcementOutcome,
        string reasonCode,
        IReadOnlyDictionary<string, int>? categoryCounts,
        long durationMs,
        CancellationToken cancellationToken)
    {
        if (_evidenceSink is null || _promptInjectionOptions is null)
            return;

        try
        {
            var ev = SecurityEvidenceEvent.Create(
                eventId: Guid.NewGuid(),
                requestId: requestId,
                tenantId: tenantId,
                applicationId: SanitizeEvidenceIdentifier(_promptInjectionOptions.ApplicationId, "openconnect-agent", 128),
                occurredAt: _timeProvider.GetUtcNow(),
                capability: PromptInjectionCapability,
                boundary: ChatClientBoundary,
                detectorId: SanitizeEvidenceIdentifier(_promptInjectionOptions.DetectorId, "deberta-injection", 64),
                detectorVersion: SanitizeEvidenceIdentifier(detectorVersion, "unknown", 128),
                policyRevision: SanitizeEvidenceIdentifier(_promptInjectionOptions.PolicyRevision, "1", 64),
                inspectionStatus: inspectionStatus,
                decision: decision,
                enforcementOutcome: enforcementOutcome,
                reasonCode: SanitizeEvidenceIdentifier(reasonCode, "unknown", 64),
                categoryCounts: categoryCounts,
                durationMs: durationMs);
            await _evidenceSink.RecordAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Evidence sink failure must never bypass security enforcement or crash allowed requests.
        }
    }

    private static string WrapUntrusted(string text)
    {
        if (text.StartsWith(UntrustedBeginMarker, StringComparison.Ordinal) &&
            text.EndsWith(UntrustedEndMarker, StringComparison.Ordinal))
        {
            return text;
        }

        return string.Concat(UntrustedBeginMarker, text, UntrustedEndMarker);
    }

    private static string SanitizeEvidenceIdentifier(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            if (sb.Length >= maxLength)
                break;
            var allowed = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':';
            sb.Append(allowed ? ch : '_');
        }
        return sb.Length > 0 ? sb.ToString() : fallback;
    }
}
