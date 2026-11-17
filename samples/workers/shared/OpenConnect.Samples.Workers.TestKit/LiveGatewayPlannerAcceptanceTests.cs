using System.ClientModel;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Samples.Workers.TestKit;

public sealed class LiveGatewayPlannerAcceptanceTests
{
    private const string Route = "route.root";
    private const string Completed = "route.root.components-todolistcomponent-razor-tab-completed";
    private const string Active = "route.root.components-todolistcomponent-razor-tab-active";
    private const string Search = "route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description";
    private const string List = "route.root.components-todolistcomponent-razor-list-task-list";
    private const string Region = "route.root.components-todolistcomponent-razor-region-todo-list";

    [LiveGatewayPlannerTheory]
    [Trait("Category", "LiveInference")]
    [InlineData("Open my Todo list.", "navigate", Route, null, null, null)]
    [InlineData("Show completed tasks.", "activate", Completed, null, null, null)]
    [InlineData("Show active tasks.", "activate", Active, null, null, null)]
    [InlineData("Search for report.", "setValue", Search, "report", null, null)]
    [InlineData("Show completed tasks containing report.", "activate", Completed, null, "setValue", Search)]
    [InlineData("Highlight the results.", "highlight", List, null, null, null)]
    [InlineData("Explain the current view.", "inspect", Region, null, null, null)]
    public async Task Real_gateway_planner_produces_authorized_todo_plan(
        string intent, string firstType, string firstTarget, string? firstValue, string? secondType, string? secondTarget)
    {
        var endpoint = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")!;
        var apiKey = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")!;
        var chat = new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var gate = new EgressGate(new PrivacyFirewall(Options.Create(new PrivacyOptions()),
            new PolicyEngine(new RulesSecurityDetector(vault))));
        var context = new EgressContext(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);
        using var guarded = new FirewallGuardedChatClient(chat.AsIChatClient(), gate, new FixedEgressContextProvider(context));
        var provider = new FirewallGuardedLiveUIGatewayInferenceProvider(guarded);
        var request = new LiveUIInferenceRequest(intent, "acceptance-session", Route, "openconnect.liveui/v1",
            [new(Route, ["navigate"]), new(Completed, ["activate"]), new(Active, ["activate"]),
             new(Search, ["setValue", "focus"]), new(List, ["highlight", "scroll"]), new(Region, ["inspect"])]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await provider.GeneratePlanAsync(request, timeout.Token);

        Assert.Equal("completed", result.Outcome);
        Assert.NotNull(result.Plan);
        Assert.True(new LiveUIActionPlanValidator().Validate(request, result.Plan!, 8, out var reason),
            $"{reason}: {System.Text.Json.JsonSerializer.Serialize(result.Plan)}");
        Assert.Equal(secondType is null ? 1 : 2, result.Plan!.Actions.Count);
        Assert.Equal(new LiveUIPlannedAction(firstType, firstTarget, firstValue), result.Plan.Actions[0]);
        if (secondType is not null)
            Assert.Equal(new LiveUIPlannedAction(secondType, secondTarget!, "report"), result.Plan.Actions[1]);
    }
}

public sealed class LiveGatewayPlannerTheoryAttribute : TheoryAttribute
{
    public LiveGatewayPlannerTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL")))
            Skip = "Set the explicit real-provider acceptance configuration to run live gateway planning.";
    }
}
