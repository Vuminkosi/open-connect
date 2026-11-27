using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Agents.Execution;

/// <summary>
/// MAF <see cref="AIFunction"/> adapter that invokes a host-brokered security tool and passes its JSON
/// output through the privacy egress gate before returning to the model (FW-090).
/// </summary>
public sealed class BrokeredSecurityAIFunction : AIFunction
{
    private readonly ToolId _toolId;
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly Func<JsonElement, CancellationToken, ValueTask<JsonElement>> _invokeBroker;
    private readonly IEgressGate? _egressGate;
    private readonly IEgressContextProvider? _contextProvider;

    /// <summary>Creates a new MAF <see cref="AIFunction"/> backed by a brokered security tool delegate.</summary>
    public BrokeredSecurityAIFunction(
        ToolId toolId,
        string name,
        string description,
        JsonElement jsonSchema,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>> invokeBroker,
        IEgressGate? egressGate = null,
        IEgressContextProvider? contextProvider = null)
    {
        if (string.IsNullOrWhiteSpace(toolId.Value))
            throw new ArgumentException("ToolId is required.", nameof(toolId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Tool description is required.", nameof(description));
        if (jsonSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool JSON schema must be an object.", nameof(jsonSchema));
        if ((egressGate is null) != (contextProvider is null))
            throw new ArgumentException("EgressGate and EgressContextProvider must either both be provided or both be null.");

        _toolId = toolId;
        _name = name;
        _description = description;
        _jsonSchema = jsonSchema.Clone();
        _invokeBroker = invokeBroker ?? throw new ArgumentNullException(nameof(invokeBroker));
        _egressGate = egressGate;
        _contextProvider = contextProvider;
    }

    /// <summary>The canonical OpenConnect tool identifier.</summary>
    public ToolId ToolId => _toolId;

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _jsonSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serializedArgs = SerializeArguments(arguments);
        var result = await _invokeBroker(serializedArgs, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Undefined)
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: security tool returned invalid JSON output.");

        if (_egressGate is null || _contextProvider is null)
            return result;

        EgressContext context;
        try
        {
            context = _contextProvider.GetContext() with { Destination = EgressDestination.Tool };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: unable to resolve egress context for security tool output.");
        }

        var rawJson = result.GetRawText();
        EgressDecision decision;
        try
        {
            decision = await _egressGate.EvaluateAsync(rawJson, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: security tool output blocked by privacy policy.");
        }

        if (decision is null || !decision.Allowed || decision.SanitizedContent is null ||
            decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review)
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: security tool output blocked by privacy policy.");
        }

        try
        {
            using var sanitizedDoc = JsonDocument.Parse(decision.SanitizedContent);
            return sanitizedDoc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PrivacyBlockedException($"{ErrorCodes.PrivacyBlocked}: sanitized security tool output is not valid JSON.");
        }
    }

    private static JsonElement SerializeArguments(AIFunctionArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in arguments)
            dict[kvp.Key] = kvp.Value;
        return JsonSerializer.SerializeToElement(dict);
    }
}
