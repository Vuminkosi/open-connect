using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Documents.Tests;

public sealed class ExtractionWireIntegrationTests
{
    [ExtractionWireFact]
    public async Task Authorized_dotnet_client_calls_python_extraction_route()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["total"],"properties":{"total":{"type":"number"}}}""");
        using var client = new ExtractionWorkerClient(new Uri(Environment.GetEnvironmentVariable("OC_TEST_EXTRACTION_ENDPOINT")!),
            "fixture-token", Convert.FromHexString(new string('a', 64)), new Firewall(),
            (_, _, _, _) => ValueTask.FromResult(true), allowInsecureHttp: true);
        using var response = await client.ExtractAsync(context, "Invoice total: 42", schema.RootElement);
        var field = response.RootElement.GetProperty("result").GetProperty("fields").GetProperty("total");
        Assert.Equal(42, field.GetProperty("value").GetInt32());
        Assert.Equal(15, field.GetProperty("start").GetInt32());
        Assert.Equal(17, field.GetProperty("end").GetInt32());
    }

    private sealed class Firewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "fixture"));
    }

    private sealed class ExtractionWireFactAttribute : FactAttribute
    {
        public ExtractionWireFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OC_TEST_EXTRACTION_ENDPOINT")))
                Skip = "Set OC_TEST_EXTRACTION_ENDPOINT to the disposable local fixture.";
        }
    }
}
