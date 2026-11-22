using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Client.Cloud;
using Xunit;

namespace OpenConnect.Client.Tests;

public sealed class OpenConnectCloudClientTests
{
    private static readonly Regex W3CTraceparentRegex = new(
        "^00-(?!0{32}$)[0-9a-f]{32}-(?!0{16}$)[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UtcMillisRegex = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void OpenConnectCloudOptions_Defaults_ArePrivacyFirstAndFailClosed()
    {
        var options = new OpenConnectCloudOptions();

        Assert.Null(options.Endpoint);
        Assert.Equal(CloudAuthenticationMode.ApiKey, options.Authentication);
        Assert.Null(options.ApiKey);
        Assert.True(options.RequirePrivacyEgressApproval);
        Assert.False(options.AllowInsecureLoopbackHttp);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal(CloudRoutingMode.LocalOnly, options.Routing.Default);
        Assert.Equal(CloudRoutingMode.LocalOnly, options.Routing.LiveUI);
        Assert.Equal(CloudRoutingMode.LocalOnly, options.Routing.Security);
        Assert.Equal(CloudRoutingMode.LocalOnly, options.Routing.Inference);
        Assert.Equal(CloudRoutingMode.LocalOnly, options.Routing.Voice);
    }

    [Fact]
    public void OpenConnectCloudOptions_Validate_EnforcesHttpsLoopbackAndUriSanitation()
    {
        // Rejects missing endpoint
        Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions { ApiKey = "oc_test_key" }.Validate());

        // Rejects non-HTTPS by default (even loopback)
        Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8080"),
            ApiKey = "oc_test_key",
        }.Validate());

        // Rejects non-loopback HTTP even when AllowInsecureLoopbackHttp is true
        Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("http://cloud.example.test"),
            AllowInsecureLoopbackHttp = true,
            ApiKey = "oc_test_key",
        }.Validate());

        // Allows loopback HTTP when AllowInsecureLoopbackHttp is true
        var loopbackOptions = new OpenConnectCloudOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8080"),
            AllowInsecureLoopbackHttp = true,
            ApiKey = "oc_test_key",
        };
        loopbackOptions.Validate();

        // Rejects UserInfo, Query, and Fragment
        Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://user:pass@cloud.example.test"),
            ApiKey = "oc_test_key",
        }.Validate());

        Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test?foo=bar"),
            ApiKey = "oc_test_key",
        }.Validate());

        Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test#section"),
            ApiKey = "oc_test_key",
        }.Validate());
    }

    [Fact]
    public void OpenConnectCloudOptions_Validate_RejectsBadApiKeyWithoutLeakingSecret_AndValidatesTimeoutAndEnums()
    {
        const string secretWithSpace = "oc_live_super_secret_token_with space";
        var ex = Assert.Throws<ArgumentException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = secretWithSpace,
        }.Validate());

        Assert.DoesNotContain(secretWithSpace, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super_secret", ex.Message, StringComparison.Ordinal);

        // WorkloadCredential allows null ApiKey
        var workloadOptions = new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            Authentication = CloudAuthenticationMode.WorkloadCredential,
            ApiKey = null,
        };
        workloadOptions.Validate();

        // All 7 CloudRoutingMode values are valid
        var routingModes = new[]
        {
            CloudRoutingMode.CloudOnly,
            CloudRoutingMode.LocalOnly,
            CloudRoutingMode.Hybrid,
            CloudRoutingMode.Automatic,
            CloudRoutingMode.CloudFirst,
            CloudRoutingMode.BrowserThenCloud,
            CloudRoutingMode.LocalFirst,
        };
        foreach (var mode in routingModes)
        {
            workloadOptions.Routing.Default = mode;
            workloadOptions.Routing.LiveUI = mode;
            workloadOptions.Routing.Security = mode;
            workloadOptions.Routing.Inference = mode;
            workloadOptions.Routing.Voice = mode;
            workloadOptions.Validate();
        }

        // Invalid timeout
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = "oc_test_key",
            RequestTimeout = TimeSpan.Zero,
        }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = "oc_test_key",
            RequestTimeout = TimeSpan.FromMinutes(6),
        }.Validate());

        // Invalid enum value
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = "oc_test_key",
            Authentication = (CloudAuthenticationMode)99,
        }.Validate());
    }

    [Fact]
    public async Task Client_SendsBearerAcceptAndW3CTraceparent_GeneratingOneWhenActivityIsNull()
    {
        const string apiKey = "oc_live_test_key_987654";
        HttpRequestMessage? captured = null;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"ready","capabilities":["inference.chat","classification.classify"]}"""));
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.example.test/") };
        var client = new OpenConnectCloudClient(httpClient, Options.Create(new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = apiKey,
        }));

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var readiness = await client.GetHealthReadyAsync();
            Assert.Equal("ready", readiness.Status);
            Assert.Equal(2, readiness.Capabilities.Count);
        }
        finally
        {
            Activity.Current = previous;
        }

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("/cloud/v1/health/ready", captured.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal(apiKey, captured.Headers.Authorization?.Parameter);
        Assert.Contains(captured.Headers.Accept, h => h.MediaType == "application/json");
        Assert.True(captured.Headers.TryGetValues("traceparent", out var traceValues));
        var traceparent = Assert.Single(traceValues);
        Assert.Matches(W3CTraceparentRegex, traceparent);
    }

    [Fact]
    public async Task Client_PropagatesActiveW3CActivityTraceparent()
    {
        string? capturedTraceparent = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedTraceparent = request.Headers.GetValues("traceparent").Single();
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"ready","capabilities":[]}"""));
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.example.test/") };
        var client = new OpenConnectCloudClient(httpClient, Options.Create(new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            Authentication = CloudAuthenticationMode.WorkloadCredential,
        }));

        using var activity = new Activity("cloud.test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        try
        {
            await client.GetHealthReadyAsync();
            Assert.Equal(activity.Id, capturedTraceparent);
        }
        finally
        {
            activity.Stop();
        }
    }

    [Fact]
    public async Task Client_ParsesSection8ProblemDetails_PreservingCodeTraceIdRetryable_WithoutLeakingApiKey()
    {
        const string secretKey = "oc_live_never_leak_this_secret_value";
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    $$"""{"type":"about:blank","title":"Quota exceeded","status":429,"code":"quota_exceeded","traceId":"{{traceId}}","retryable":true,"detail":"Daily token budget reached"}""",
                    Encoding.UTF8,
                    "application/problem+json"),
            };
            response.Headers.TryAddWithoutValidation("x-trace-id", traceId);
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.example.test/") };
        var client = new OpenConnectCloudClient(httpClient, Options.Create(new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = secretKey,
        }));

        var ex = await Assert.ThrowsAsync<OpenConnectCloudException>(() => client.GetCapabilitiesAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(429, ex.StatusCodeValue);
        Assert.Equal("quota_exceeded", ex.Code);
        Assert.Equal("Quota exceeded", ex.Title);
        Assert.Equal(traceId, ex.TraceId);
        Assert.True(ex.Retryable);
        Assert.Equal("Daily token budget reached", ex.Detail);
        Assert.NotNull(ex.Problem);
        Assert.DoesNotContain(secretKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_HandlesNonJsonErrorResponse_SafelyWithoutLeakingRawBodyOrApiKey()
    {
        const string secretKey = "oc_live_secret_do_not_leak";
        const string rawUpstreamBody = "INTERNAL_STACK_TRACE_AND_DB_DSN=postgres://admin:secret@internal";
        const string headerTraceId = "abcdef0123456789abcdef0123456789";

        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(rawUpstreamBody, Encoding.UTF8, "text/plain"),
            };
            response.Headers.TryAddWithoutValidation("x-trace-id", headerTraceId);
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.example.test/") };
        var client = new OpenConnectCloudClient(httpClient, Options.Create(new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = secretKey,
        }));

        var ex = await Assert.ThrowsAsync<OpenConnectCloudException>(() => client.GetHealthReadyAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal("capability_unavailable", ex.Code);
        Assert.Equal(headerTraceId, ex.TraceId);
        Assert.False(ex.Retryable);
        Assert.DoesNotContain(secretKey, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("INTERNAL_STACK_TRACE", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_TypedCapabilityMethods_SendValidEnvelopesAndDeserializeResponses()
    {
        var requests = new Dictionary<string, string>(StringComparer.Ordinal);
        var audioBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04 };
        var audioBase64 = Convert.ToBase64String(audioBytes);

        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            requests[path] = body;

            return path switch
            {
                "/cloud/v1/capabilities" => JsonResponse(
                    HttpStatusCode.OK,
                    """{"capabilities":[{"capability":"inference.chat","status":"available","streamingSupported":false},{"capability":"voice.synthesize","status":"unavailable","streamingSupported":false}]}""",
                    "11111111111111111111111111111111"),
                "/cloud/v1/classification" => JsonResponse(
                    HttpStatusCode.OK,
                    """{"schemaVersion":"openconnect.cloud.classification.result/v1","requestId":"11111111-1111-1111-1111-111111111111","traceId":"22222222222222222222222222222222","result":{"labels":["safe"],"scores":{"safe":0.99},"abstained":false,"modelRevision":"rev-1","labelSetVersion":"v1"}}"""),
                "/cloud/v1/inference/chat" => JsonResponse(
                    HttpStatusCode.OK,
                    """{"schemaVersion":"openconnect.cloud.inference.result/v1","requestId":"11111111-1111-1111-1111-111111111111","modelId":"qwen3-4b","traceId":"33333333333333333333333333333333","output":"Hello from cloud","usage":{"inputTokens":12,"outputTokens":4}}"""),
                "/cloud/v1/liveui/plan" => JsonResponse(
                    HttpStatusCode.OK,
                    """{"schemaVersion":"openconnect.cloud.liveui.result/v1","requestId":"11111111-1111-1111-1111-111111111111","traceId":"44444444444444444444444444444444","provider":"deterministic","plan":{"schemaVersion":"1.0","actions":[{"type":"activate","target":"btn-save"}]}}"""),
                "/cloud/v1/voice/synthesis" => JsonResponse(
                    HttpStatusCode.OK,
                    $$$"""{"schemaVersion":"openconnect.cloud.voice-synthesis.result/v1","requestId":"11111111-1111-1111-1111-111111111111","traceId":"55555555555555555555555555555555","result":{"format":"wav","sampleRateHz":24000,"channels":1,"voiceId":"af_heart","locale":"en-US","audioBase64":"{{{audioBase64}}}"}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.example.test/") };
        var client = new OpenConnectCloudClient(httpClient, Options.Create(new OpenConnectCloudOptions
        {
            Endpoint = new Uri("https://cloud.example.test"),
            ApiKey = "oc_live_valid_key",
        }));

        var caps = await client.GetCapabilitiesAsync();
        Assert.Equal("11111111111111111111111111111111", caps.TraceId);
        Assert.Equal(2, caps.Capabilities.Count);
        Assert.Equal("available", caps.Capabilities[0].Status);

        var classify = await client.ClassifyAsync(new CloudClassificationRequest(new CloudClassificationInput(Text: "hello world")));
        Assert.Equal("safe", Assert.Single(classify.Result.Labels));
        using (var doc = JsonDocument.Parse(requests["/cloud/v1/classification"]))
        {
            Assert.Equal("openconnect.cloud.classification/v2", doc.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("classification.classify", doc.RootElement.GetProperty("capability").GetString());
            Assert.Matches(UtcMillisRegex, doc.RootElement.GetProperty("expiresAt").GetString()!);
            Assert.False(doc.RootElement.GetProperty("input").TryGetProperty("inputRef", out _));
        }

        var chat = await client.ChatAsync(new CloudChatRequest(
            "qwen3-4b",
            new CloudChatInput([new CloudChatMessage("user", "Hi")], MaxTokens: 128, Stream: false)));
        Assert.Equal("Hello from cloud", chat.Output);
        Assert.Equal(12, chat.Usage.InputTokens);

        var plan = await client.PlanLiveUIAsync(new CloudLiveUIPlanRequest(
            new CloudLiveUIPlanInput(
                "1.0",
                "Click save",
                new CloudLiveUIContext("/settings", [new CloudLiveUIAvailableAction("btn-save", "activate", "Save")]),
                new CloudLiveUIGenerationOptions(MaxActions: 4, MaxOutputTokens: 128))));
        Assert.Equal("deterministic", plan.Provider);
        Assert.Equal("btn-save", Assert.Single(plan.Plan.Actions).Target);

        var speech = await client.SynthesizeSpeechAsync(new CloudSpeechSynthesisRequest(
            new CloudSpeechSynthesisInput("Hello OpenConnect", VoiceId: "af_heart", Locale: "en-US", Format: "wav")));
        Assert.Equal(24_000, speech.Result.SampleRateHz);
        Assert.Equal(audioBytes, speech.Result.GetAudioBytes());
    }

    [Fact]
    public void AddOpenConnectCloud_IsIsolatedFromAddOpenConnectClient_AndValidatesOptions()
    {
        var services = new ServiceCollection();

        services.AddOpenConnectClient(o =>
        {
            o.ApiBaseAddress = new Uri("https://app.example.test/openconnect/v1");
            o.Authentication = ClientAuthenticationMode.Host;
        });

        services.AddOpenConnectCloud(o =>
        {
            o.Endpoint = new Uri("https://cloud.example.test");
            o.Authentication = CloudAuthenticationMode.ApiKey;
            o.ApiKey = "oc_live_isolated_key";
            o.Routing.Inference = CloudRoutingMode.CloudFirst;
            o.Routing.Voice = CloudRoutingMode.Hybrid;
        });

        using var provider = services.BuildServiceProvider();

        var appOptions = provider.GetRequiredService<OpenConnectClientOptions>();
        var appClient = provider.GetRequiredService<IOpenConnectClient>();
        var cloudOptions = provider.GetRequiredService<IOptions<OpenConnectCloudOptions>>().Value;
        var cloudClient = provider.GetRequiredService<IOpenConnectCloudClient>();

        Assert.NotNull(appClient);
        Assert.NotNull(cloudClient);
        Assert.Equal(new Uri("https://app.example.test/openconnect/v1"), appOptions.ApiBaseAddress);
        Assert.Equal(new Uri("https://cloud.example.test"), cloudOptions.Endpoint);
        Assert.Equal(CloudRoutingMode.CloudFirst, cloudOptions.Routing.Inference);
        Assert.Equal(CloudRoutingMode.Hybrid, cloudOptions.Routing.Voice);
    }

    [Fact]
    public void AddOpenConnectCloud_InvalidOptions_FailsOptionsValidationWithoutLeakingApiKey()
    {
        const string secretKey = "oc_live_secret_must_not_leak";
        var services = new ServiceCollection();

        services.AddOpenConnectCloud(o =>
        {
            o.Endpoint = new Uri("http://insecure.example.test");
            o.ApiKey = secretKey;
        });

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OpenConnectCloudOptions>>().Value);

        Assert.DoesNotContain(secretKey, ex.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json, string? traceId = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            response.Headers.TryAddWithoutValidation("x-trace-id", traceId);
        }

        return response;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
