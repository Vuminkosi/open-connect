using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class ClientInteractionDiagnosticsTests
{
    [Fact]
    public void Tool_origin_is_optional_and_old_requests_remain_compatible()
    {
        var options = OpenConnectHttpJson.CreateOptions();
        var legacy = new ClientInteractionRequest("id", "await_state");
        var oldJson = JsonSerializer.Serialize(legacy, options);
        Assert.DoesNotContain("originTool", oldJson, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<ClientInteractionRequest>(oldJson, options)!.OriginTool);

        var tagged = legacy with { OriginTool = "liveui_click_and_observe_text" };
        var json = JsonSerializer.Serialize(tagged, options);
        Assert.Equal(tagged.OriginTool, JsonSerializer.Deserialize<ClientInteractionRequest>(json, options)!.OriginTool);
        var oldReader = JsonSerializer.Deserialize<LegacyRequest>(json, options);
        Assert.Equal("await_state", oldReader!.Operation);
    }

    private sealed record LegacyRequest(string CorrelationId, string Operation);
}
