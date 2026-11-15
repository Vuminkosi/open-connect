using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Extensions.AI;
using OpenConnect.Agents.Execution;
using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Uses the host's already firewall-guarded chat client as an explicit LiveUI planning fallback.</summary>
public sealed class FirewallGuardedLiveUIGatewayInferenceProvider : ILiveUIGatewayInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly IChatClient _chatClient;

    public FirewallGuardedLiveUIGatewayInferenceProvider(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        if (chatClient is not FirewallGuardedChatClient)
            throw new InvalidOperationException("LiveUI gateway planning requires the existing FirewallGuardedChatClient.");
        _chatClient = chatClient;
    }

    public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;

    public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available,
            SupportsStructuredOutput: true, SupportsCancellation: true));
    }

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var context = JsonSerializer.Serialize(request, JsonOptions);
        var prompt = "Produce a LiveUI action plan as JSON with properties schemaVersion and actions. " +
            "Only use target IDs and action names from the supplied capabilities. Return an empty actions array if the request is not a supported UI action. " +
            "Do not include explanations, code, URLs, or additional properties.\n" + context;
        try
        {
            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                cancellationToken).ConfigureAwait(false);
            var plan = JsonSerializer.Deserialize<LiveUIActionPlan>(response.Text, JsonOptions);
            return new(Kind, plan, plan is null ? "invalid_gateway_plan" : "completed");
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivacyBlockedException) { throw; }
        catch (Exception)
        {
            return new(Kind, null, "gateway_inference_failed");
        }
    }
}
