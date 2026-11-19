using System.ClientModel;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Core;
using OpenConnect.LiveUI.Agents;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using OpenConnect.Samples.Workers.Host;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class LiveUncertainActionAcceptanceTests
{
    [LiveGatewayPlannerFact]
    [Trait("Category", "LiveInference")]
    public async Task Real_agent_can_use_sdk_visible_change_check_for_a_form_submission()
    {
        var chat = new ChatClient(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")!,
            new ApiKeyCredential(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")!),
            new OpenAIClientOptions { Endpoint = new Uri(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")!) });
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var session = new SessionId(Ulid.NewUlid());
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            session, EgressDestination.ExternalModel);
        var registry = new AgentDefinitionRegistry();
        var agentId = new AgentId("visible-change-acceptance");
        registry.Register(new AgentDefinition(agentId, "OpenConnect assistant"));
        var broker = new VisibleChangeBroker();
        var registered = LiveUIClientAgentToolFactory.CreateTools(broker, session, TimeSpan.FromSeconds(5));
        var observed = new ObservedFunction((AIFunction)registered.Single(tool => tool.Name == "liveui_click_and_observe_text"));
        var tools = registered.Select(tool => tool.Name == observed.Name ? observed : tool).ToArray();
        var factory = new MafRealAgentFactory(registry, chat.AsIChatClient(), gate, new FixedEgressContextProvider(context));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var response = await factory.RunAsync(agentId, "Submit the form and check that the message 'Request received' appears.",
            tools, timeout.Token, "Use available tools to operate the current UI. Report observations and uncertainty accurately.");

        Assert.True(broker.ActionSteps.Count >= 3);
        Assert.Equal(1, observed.Invocations);
        Assert.Equal(new[] { "textNotContains", "Click", "textContains" }, broker.ActionSteps.Take(3));
        Assert.Single(broker.ActionSteps, step => step == "Click");
        Assert.All(broker.ActionSteps.Skip(3), step => Assert.Contains(step, new[] { "textContains", "inspect" }));
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Matches("(?i)(visible|appear|show|uncertain|unverified|cannot|can't|not confirm)", response.Text);
    }

    [LiveGatewayPlannerFact]
    [Trait("Category", "LiveInference")]
    public async Task Real_agent_does_not_repeat_an_action_when_its_receipt_is_lost()
    {
        var chat = new ChatClient(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")!,
            new ApiKeyCredential(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")!),
            new OpenAIClientOptions { Endpoint = new Uri(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")!) });
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var session = new SessionId(Ulid.NewUlid());
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            session, EgressDestination.ExternalModel);
        var registry = new AgentDefinitionRegistry();
        var agentId = new AgentId("uncertain-action-acceptance");
        registry.Register(new AgentDefinition(agentId, "OpenConnect assistant"));
        var broker = new LostReceiptBroker();
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker, session, TimeSpan.FromSeconds(5));
        var factory = new MafRealAgentFactory(registry, chat.AsIChatClient(), gate, new FixedEgressContextProvider(context));
        var instructions = (string)typeof(MetaTodoTaskExecutor).GetField("AgentInstructions", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var response = await factory.RunAsync(agentId, "Show completed tasks.", tools, timeout.Token, instructions);

        Assert.Single(broker.ActionOperations, operation => operation == "execute");
        Assert.DoesNotContain(broker.ActionOperations, operation => operation == "navigate");
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Matches("(?i)(uncertain|unable|cannot|can't|could not|couldn't|not (?:able|confirm|verify|sure)|unverified)", response.Text);
        Assert.DoesNotMatch("(?i)\\b(?:the )?completed tab (?:is|was|has been) (?:successfully )?(?:selected|opened|activated)\\b|\\bi (?:have )?(?:selected|opened|activated) the completed tab\\b", response.Text);
    }

    private sealed class LostReceiptBroker : IClientInteractionBroker
    {
        public List<string> ActionOperations { get; } = [];

        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedResultKind == "action")
            {
                ActionOperations.Add(request.Operation);
                return Task.FromResult<ClientInteractionResponse?>(null);
            }
            var payload = request.Operation == "infer_plan"
                ? """{"provider":"LocalWorker","outcome":"completed","plan":{"actions":[{"type":"activate","targetId":"todo.completed"}]}}"""
                : """{"routeId":"todo.home","targets":[{"targetId":"todo.completed","label":"Completed","kind":"Tab","capabilities":["Activate"]}],"navigableRoutes":[]}""";
            return Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, expectedResultKind, payload));
        }
    }

    private sealed class VisibleChangeBroker : IClientInteractionBroker
    {
        public List<string> ActionSteps { get; } = [];

        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payload;
            if (expectedResultKind == "action")
            {
                ActionSteps.Add(request.Operation == "execute" ? request.Action ?? "" : request.StateName ?? request.Operation);
                payload = request.Operation == "execute"
                    ? "{\"outcome\":\"Uncertain\",\"code\":\"click_dispatched_without_postcondition\",\"postconditionVerified\":false}"
                    : request.Operation == "inspect"
                    ? "{\"outcome\":\"Completed\",\"code\":\"inspection_verified\",\"postconditionVerified\":true,\"verifiedState\":\"control_visible\"}"
                    : "{\"outcome\":\"Completed\",\"code\":\"state_verified\",\"postconditionVerified\":true}";
            }
            else
            {
                payload = request.Operation == "infer_plan"
                    ? "{\"outcome\":\"unavailable\",\"plan\":null}"
                    : "{\"routeId\":\"home\",\"targets\":[{\"targetId\":\"submit\",\"label\":\"Submit\",\"kind\":\"Button\",\"capabilities\":[\"Activate\"]},{\"targetId\":\"status\",\"label\":\"Status\",\"kind\":\"Region\",\"capabilities\":[\"WaitForState\"]}],\"navigableRoutes\":[]}";
            }
            return Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, expectedResultKind, payload));
        }
    }

    private sealed class ObservedFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        public int Invocations { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Invocations++;
            return base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
