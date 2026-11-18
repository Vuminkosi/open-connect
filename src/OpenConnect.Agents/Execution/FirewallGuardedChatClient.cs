using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

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
/// Function arguments are materialized as JSON-compatible values. String results remain strings; other
/// structured results are forwarded as sanitized JSON values rather than their original CLR instances.
/// Service resolution and disposal delegate to the inner client via <see cref="DelegatingChatClient"/>.</remarks>
public sealed class FirewallGuardedChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions EgressJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
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
        return await InnerClient.GetResponseAsync(guarded, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await foreach (var update in InnerClient.GetStreamingResponseAsync(guarded, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        InnerClient.GetService(serviceType, serviceKey);

    private async Task<IReadOnlyList<ChatMessage>> GuardAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
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

        var guarded = new List<ChatMessage>();
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (string.IsNullOrEmpty(message.Text) && !message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent))
            {
                guarded.Add(message);
                continue;
            }

            var safeText = string.IsNullOrEmpty(message.Text) ? null :
                await SanitizeAsync(message.Text, context, cancellationToken).ConfigureAwait(false);
            // Keep tool calls and other protocol content attached to the assistant message.
            // The gate evaluated the combined text, so replace all text fragments with the
            // single sanitized result without mutating the original message.
            var sanitized = message.Clone();
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
                else
                    contents.Add(content);
            }
            sanitized.Contents = contents;
            sanitized.RawRepresentation = null;
            guarded.Add(sanitized);
        }

        return guarded;
    }

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
        return await SanitizeAsync(json, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken)
    {
        EgressDecision decision;
        try
        { decision = await _gate.EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw FailClosed(); }
        if (decision is null || !decision.Allowed || decision.SanitizedContent is null)
            throw FailClosed();
        return decision.SanitizedContent;
    }

    private static PrivacyBlockedException FailClosed() =>
        new($"{ErrorCodes.PrivacyBlocked}: content blocked by privacy policy.");
}
