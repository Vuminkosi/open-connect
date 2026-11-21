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

/// <summary>Firewall-decorated <see cref="IChatClient"/> that guards outbound model content fail-closed.</summary>
/// <remarks>Message text and structured function arguments and results are evaluated through the egress gate
/// before reaching the inner client; sanitized content is forwarded and denials throw without echoing content.
/// Responses are also evaluated with the output destination. Streaming responses are buffered until
/// the response has been inspected, including text split across updates of the same message.
/// Function arguments are materialized as JSON-compatible values. String results remain strings; other
/// structured results are forwarded as sanitized JSON values rather than their original CLR instances.
/// Unsupported content, including binary media, is rejected unless handled by a separate authorized modality path.
/// Service resolution and disposal delegate to the inner client via <see cref="DelegatingChatClient"/>.</remarks>
public sealed class FirewallGuardedChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions EgressJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const int MaxBufferedUpdates = 4096;
    private const int MaxBufferedCharacters = 1_000_000;
    private readonly IEgressGate _gate;
    private readonly IEgressContextProvider _contextProvider;

    /// <summary>Creates a firewall-guarded decorator over the supplied inner client.</summary>
    public FirewallGuardedChatClient(IChatClient inner, IEgressGate gate, IEgressContextProvider contextProvider)
        : base(inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await GuardMessageBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        await GuardContentBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        var guardedOptions = await GuardOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        var response = await InnerClient.GetResponseAsync(guarded, guardedOptions, cancellationToken).ConfigureAwait(false);
        return await GuardResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await GuardMessageBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        await GuardContentBoundariesAsync(guarded, null, cancellationToken).ConfigureAwait(false);
        var guardedOptions = await GuardOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        var updates = new List<ChatResponseUpdate>();
        var size = 0;
        await foreach (var update in InnerClient.GetStreamingResponseAsync(guarded, guardedOptions, cancellationToken).ConfigureAwait(false))
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
}
