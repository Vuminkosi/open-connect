using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Calls the private LiveUI worker from the server or Gateway process.</summary>
/// <remarks>Runtime labels require a request prepared by <see cref="LiveUIWorkerPrivacyScreen"/> in the worker host.
/// Other calls must also be screened by the caller for the authenticated worker egress context.</remarks>
public sealed class LocalWorkerLiveUIInferenceProvider(
    HttpClient httpClient,
    IOptions<LiveUIWorkerClientOptions> options) : ILiveUIInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LiveUIWorkerClientOptions _options = options.Value;

    public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.LocalWorker;

    public async Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "health/ready", null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("The LiveUI worker rejected server authorization.");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return new(Kind, LiveUIInferenceAvailabilityStatus.Blocked, Reason: "rate_limited");
            return new(Kind, response.IsSuccessStatusCode ? LiveUIInferenceAvailabilityStatus.Available : LiveUIInferenceAvailabilityStatus.Unavailable,
                SupportsStructuredOutput: true, SupportsCancellation: true,
                Reason: response.IsSuccessStatusCode ? null : "worker_not_ready");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "worker_unavailable");
        }
    }

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request with
        {
            Capabilities = request.Capabilities.Select(capability => capability with { Actions = capability.Actions.ToArray() }).ToArray()
        };
        if (request.Capabilities.Any(capability => capability.TargetId.StartsWith("oc.runtime.", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(capability.Label)) &&
            !string.Equals(request.WorkerScreenedDigest, LiveUIWorkerPrivacyScreen.ComputeDigest(request), StringComparison.Ordinal))
            throw new PrivacyBlockedException("Runtime target labels require authenticated worker privacy screening.");
        var body = new
        {
            requestId = request.RequestId,
            schemaVersion = request.SchemaVersion,
            intent = request.Intent,
            context = new
            {
                route = request.RouteId,
                availableActions = request.Capabilities.SelectMany(capability => capability.Actions.Select(action =>
                {
                    var item = new Dictionary<string, string> { ["target"] = capability.TargetId, ["action"] = action };
                    if (capability.TargetId.StartsWith("oc.runtime.", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(capability.Label))
                        item["label"] = capability.Label;
                    return item;
                })).ToArray()
            },
            generation = new { maxActions = 8, maxOutputTokens = 256 }
        };
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "v1/liveui/inference/plan", body, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("The LiveUI worker rejected server authorization.");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return new(Kind, null, "rate_limited");
            if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway)
                return new(Kind, null, "worker_unavailable");
            if (!response.IsSuccessStatusCode)
                return new(Kind, null, "worker_inference_failed");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("requestId", out var id) || id.GetString() != body.requestId ||
                !root.TryGetProperty("schemaVersion", out var version) || version.GetString() != request.SchemaVersion ||
                !root.TryGetProperty("plan", out var planElement))
                return new(Kind, null, "worker_response_mismatch");
            if (!planElement.TryGetProperty("actions", out var actionsElement) || actionsElement.ValueKind != JsonValueKind.Array)
                return new(Kind, null, "worker_plan_invalid");
            var actions = new List<LiveUIPlannedAction>();
            foreach (var action in actionsElement.EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var actionType) || actionType.ValueKind != JsonValueKind.String ||
                    !action.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String)
                    return new(Kind, null, "worker_plan_invalid");
                string? value = null;
                if (action.TryGetProperty("value", out var actionValue))
                {
                    if (actionValue.ValueKind != JsonValueKind.String && actionValue.ValueKind != JsonValueKind.Null)
                        return new(Kind, null, "worker_plan_invalid");
                    value = actionValue.ValueKind == JsonValueKind.String ? actionValue.GetString() : null;
                }
                actions.Add(new LiveUIPlannedAction(actionType.GetString()!, target.GetString()!, value));
            }
            var plan = new LiveUIActionPlan(request.SchemaVersion, actions);
            return new(Kind, plan, plan is null ? "worker_plan_invalid" : "completed");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return new(Kind, null, "worker_unavailable"); }
        catch (JsonException) { return new(Kind, null, "worker_response_invalid"); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class LiveUIWorkerClientOptions
{
    public Uri? Endpoint { get; set; }
    public string? BearerToken { get; set; }
}

public static class LiveUIWorkerServiceCollectionExtensions
{
    /// <summary>Registers the Gateway's private CPU worker client. The endpoint and credential remain server-side.</summary>
    public static IServiceCollection AddLiveUILocalWorkerInferenceProvider(
        this IServiceCollection services, Action<LiveUIWorkerClientOptions> configure) =>
        AddLiveUILocalWorkerInferenceProvider(services, configure, registerRouterProvider: true);

    public static IServiceCollection AddLiveUILocalWorkerInferenceProvider(
        this IServiceCollection services, Action<LiveUIWorkerClientOptions> configure, bool registerRouterProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<LiveUIWorkerClientOptions>().Configure(configure)
            .Validate(options => options.Endpoint is { IsAbsoluteUri: true } endpoint && endpoint.Scheme == Uri.UriSchemeHttp &&
                string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment),
                "The private worker endpoint must be an absolute HTTP endpoint without embedded credentials or query data.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.BearerToken),
                "A private worker bearer credential is required.")
            .ValidateOnStart();
        services.AddHttpClient<LocalWorkerLiveUIInferenceProvider>((provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<LiveUIWorkerClientOptions>>().Value;
            client.BaseAddress = settings.Endpoint is null ? null : new Uri(settings.Endpoint.AbsoluteUri.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        if (registerRouterProvider)
            services.AddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<LocalWorkerLiveUIInferenceProvider>());
        return services;
    }
}
