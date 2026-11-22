using OpenConnect.Hosting.AspNetCore;

namespace OpenConnect.Samples.Blazor;

/// <summary>Builds the sample's opt-in, development-only capability sources.</summary>
internal static class TodoCapabilityMonitorConfiguration
{
    internal static IReadOnlyList<WorkerCapabilitySource> Create(IConfiguration configuration, IHostEnvironment environment,
        TodoGatewayOptions gateway)
    {
        if (!environment.IsDevelopment() || gateway.Mode != TodoGatewayMode.SameHostGateway)
            throw new InvalidOperationException("Capability monitoring in this sample requires the development-only same-host gateway.");
        const string prefix = "OpenConnect:CapabilityMonitor:";
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        var inferenceToken = configuration["OPENCONNECT_INFERENCE_TOKEN"];
        if (string.IsNullOrWhiteSpace(inferenceToken))
            inferenceToken = token;
        var allowHttp = configuration.GetValue<bool>(prefix + "AllowInsecureHttp");
        var sources = new List<WorkerCapabilitySource>();
        Add("DocumentEndpoint", ["documents.analyze"]);
        Add("KnowledgeEndpoint", ["knowledge.embed"]);
        Add("RerankEndpoint", ["knowledge.rerank"]);
        Add("IndexEndpoint", ["knowledge.index"]);
        Add("VisionEndpoint", ["vision.analyze"]);
        Add("VisionEmbedEndpoint", ["vision.embed"]);
        Add("AnalyticsEndpoint", ["analytics.analyze"]);
        Add("InferenceEndpoint", ["documents.extract"], inference: true);
        if (sources.Count == 0)
            throw new InvalidOperationException("No capability sources are configured.");
        return sources.AsReadOnly();

        void Add(string name, string[] ids, bool inference = false)
        {
            var endpoint = configuration[prefix + name];
            if (string.IsNullOrWhiteSpace(endpoint))
                return;
            var credential = inference ? inferenceToken : token;
            if (string.IsNullOrWhiteSpace(credential))
                throw new InvalidOperationException("A worker credential is required.");
            sources.Add(new WorkerCapabilitySource(endpoint, credential, ids, inference, allowHttp));
        }
    }
}
