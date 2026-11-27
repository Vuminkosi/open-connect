using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Privacy;

namespace OpenConnect.Agents.Messaging;

/// <summary>Descriptor describing a brokered messaging tool to expose to an operational MAF agent.</summary>
public sealed record MessagingAgentToolBinding(
    ToolId ToolId,
    string Name,
    string Description,
    string JsonSchema);

/// <summary>Factory that builds MAF <see cref="BrokeredMessagingAIFunction"/> instances for operational agents.</summary>
public static class MessagingAgentToolkit
{
    public static IReadOnlyList<BrokeredMessagingAIFunction> CreateFunctions(
        IEnumerable<MessagingAgentToolBinding> bindings,
        Func<ToolId, JsonElement, CancellationToken, ValueTask<JsonElement>> invokeBroker,
        IEgressGate? egressGate = null,
        IEgressContextProvider? contextProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(invokeBroker);

        var result = new List<BrokeredMessagingAIFunction>();
        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            using var doc = JsonDocument.Parse(binding.JsonSchema);
            var toolId = binding.ToolId;
            result.Add(new BrokeredMessagingAIFunction(
                toolId,
                binding.Name,
                binding.Description,
                doc.RootElement.Clone(),
                (args, ct) => invokeBroker(toolId, args, ct),
                egressGate,
                contextProvider));
        }

        return result;
    }
}
