using OpenConnect.Observability;
using Xunit;

namespace OpenConnect.Observability.Tests;

public sealed class TelemetryPrivacyTests
{
    private static ITelemetryPrivacyFilter Filter(TelemetryPrivacyOptions? options = null)
    {
        options ??= new TelemetryPrivacyOptions();
        var detector = new DefaultTelemetrySecretDetector();
        var classifier = new TelemetryDataClassifier(detector, new NoOpTelemetryPiiDetector());
        var policy = new TelemetryPrivacyPolicy(options);
        return new TelemetryPrivacyFilter(classifier, policy, new TelemetryPrivacyOptionsAccessor(options));
    }

    [Theory]
    [InlineData("authorization", "Bearer abc")]
    [InlineData("cookie", "session=abc")]
    [InlineData("api_key", "sk-live")]
    [InlineData("password", "p@ss")]
    [InlineData("connection_string", "postgres://u:p@host/db")]
    [InlineData("private_key", "-----BEGIN PRIVATE KEY-----")]
    [InlineData("token", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("oauth_access_token", "abc")]
    [InlineData("authorization", "Basic abc")]
    [InlineData("apiKey", "synthetic-secret")]
    public void Credentials_are_dropped(string name, string value) =>
        Assert.Empty(Filter().Filter([new(name, value)]));

    [Theory]
    [InlineData("tenant_id", "tenant-1")]
    [InlineData("user.id", "user-1")]
    [InlineData("prompt", "question")]
    [InlineData("completion", "answer")]
    [InlineData("exception.message", "details")]
    [InlineData("tool.arguments", "args")]
    [InlineData("custom.dimension", "value")]
    public void Personal_and_unknown_data_are_dropped(string name, string value) =>
        Assert.Empty(Filter().Filter([new(name, value)]));

    [Theory]
    [InlineData("operation", "chat")]
    [InlineData("provider", "openai")]
    [InlineData("model_family", "gpt")]
    [InlineData("outcome", "success")]
    [InlineData("mode", "strict-local")]
    [InlineData("component", "gateway")]
    public void Allowlisted_operational_dimensions_survive(string name, string value)
    {
        var filtered = Filter().Filter([new(name, value)]);
        Assert.Single(filtered);
        Assert.Equal(value, filtered[0].Value);
    }

    [Fact]
    public void Unapproved_dimension_value_is_dropped() =>
        Assert.Empty(Filter().Filter([new("provider", "Bearer secret") ]));

    [Fact]
    public void Oversized_value_is_dropped()
    {
        var options = new TelemetryPrivacyOptions { MaxAttributeLength = 4 };
        Assert.Empty(Filter(options).Filter([new("operation", "chat ")]));
    }

    [Fact]
    public void Detector_exception_fails_closed()
    {
        var classifier = new ThrowingClassifier();
        var filter = new TelemetryPrivacyFilter(classifier, new TelemetryPrivacyPolicy(new()),
            new TelemetryPrivacyOptionsAccessor(new()));
        Assert.Empty(filter.Filter([new("operation", "chat")]));
    }

    [Fact]
    public void Export_validator_rejects_any_unsafe_attribute()
    {
        var filter = Filter();
        var validator = new TelemetryExportValidator(filter);
        Assert.True(validator.IsSafe([new("operation", "chat")]));
        Assert.False(validator.IsSafe([new("operation", "chat"), new("prompt", "secret text")]));
    }

    [Fact]
    public void Credential_rejection_cannot_be_opted_out()
    {
        var options = new TelemetryPrivacyOptions { Mode = TelemetryPrivacyMode.Custom, DropUnknown = false,
            AllowPrompt = true, AllowCompletion = true, AllowExceptionMessages = true };
        Assert.Empty(Filter(options).Filter([new("authorization", "secret") ]));
    }

    [Theory]
    [InlineData(TelemetryPrivacyMode.Strict)]
    [InlineData(TelemetryPrivacyMode.Balanced)]
    [InlineData(TelemetryPrivacyMode.Custom)]
    public void Export_validation_rejects_sensitive_and_unknown_activity_tags_in_every_mode(TelemetryPrivacyMode mode)
    {
        var options = new TelemetryPrivacyOptions { Mode = mode, DropUnknown = false,
            AllowPrompt = true, AllowCompletion = true, AllowExceptionMessages = true };
        var filter = Filter(options);
        var validator = new TelemetryExportValidator(filter);
        using var activity = new System.Diagnostics.Activity("export-test")
            .SetTag("authorization", "Bearer synthetic")
            .SetTag("prompt", "synthetic prompt")
            .SetTag("custom.dimension", "unknown");
        Assert.False(validator.ValidateActivity(activity));
        Assert.Empty(filter.Filter(activity.TagObjects.Select(tag => new KeyValuePair<string, object?>(tag.Key, tag.Value))));
    }

    [Fact]
    public void Public_safe_entrypoint_drops_credentials_and_unknown_tags()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var activity = Instrumentation.SafeStartActivity("gateway", "request", [
            new("operation", "chat"), new("authorization", "Bearer synthetic"), new("mystery", "unknown")]);
        Assert.NotNull(activity);
        Assert.Single(activity.TagObjects);
        Assert.Equal("chat", activity.GetTagItem("operation"));
    }

    [Fact]
    public void Vector_activity_source_accepts_only_vector_dimensions()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "OpenConnect.Vectors",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var activity = Instrumentation.SafeStartActivity("vectors", "search", [
            new("operation", "search"), new("component", "qdrantvactor"), new("outcome", "success"),
            new("collection", "private-customer-index")]);

        Assert.NotNull(activity);
        Assert.Equal("OpenConnect.Vectors", activity.Source.Name);
        Assert.Equal(3, activity.TagObjects.Count());
        Assert.Null(activity.GetTagItem("collection"));
    }

    [Fact]
    public void Vector_metric_rejects_unapproved_dimension_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Instrumentation.SafeRecordVector(12.5, "private-index-name", "success"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Instrumentation.SafeRecordVector(-1, "search", "success"));
    }

    [Theory]
    [InlineData(TelemetryPrivacyMode.Strict)]
    [InlineData(TelemetryPrivacyMode.Balanced)]
    [InlineData(TelemetryPrivacyMode.Custom)]
    public void Stopped_activity_is_sanitized_after_caller_mutates_tags(TelemetryPrivacyMode mode)
    {
        var options = new TelemetryPrivacyOptions
        {
            Mode = mode,
            DropUnknown = false,
            AllowPrompt = true
        };
        var filter = Filter(options);
        using var privacyListener = new SafeActivityListener(filter);
        using var activity = Instrumentation.SafeStartActivity("gateway", "request", [new("operation", "chat")]);

        Assert.NotNull(activity);
        activity.SetTag("authorization", "Bearer synthetic-credential");
        activity.SetTag("unknown.attribute", "unknown");
        activity.SetTag("prompt", "synthetic prompt");

        activity.Dispose();

        Assert.Null(activity.GetTagItem("authorization"));
        Assert.Null(activity.GetTagItem("unknown.attribute"));
        Assert.Null(activity.GetTagItem("prompt"));
        Assert.Equal("chat", activity.GetTagItem("operation"));
        Assert.True(SafeExporter.ValidateAndSanitizeActivity(activity, filter));
    }

    [Fact]
    public void Hmac_pseudonyms_are_domain_separated_and_stable()
    {
        var pseudonymizer = new HmacTelemetryPseudonymizer(new TestKeyProvider());
        var a = pseudonymizer.Pseudonymize("user", "tenant-a", "123");
        Assert.Equal(a, pseudonymizer.Pseudonymize("user", "tenant-a", "123"));
        Assert.NotEqual(a, pseudonymizer.Pseudonymize("email", "tenant-a", "123"));
        Assert.NotEqual(a, pseudonymizer.Pseudonymize("user", "tenant-b", "123"));
    }

    [Fact]
    public void Options_reject_out_of_range_attribute_limit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryPrivacyOptionsValidator().Validate(new() { MaxAttributeLength = 0 }));

    private sealed class ThrowingClassifier : ITelemetryDataClassifier
    {
        public TelemetryDataClassification Classify(string name, object? value) => throw new InvalidOperationException();
    }

    private sealed class TestKeyProvider : ITelemetryPseudonymizationKeyProvider
    {
        private readonly byte[] _key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        public string KeyId => "test-key-v1";
        public ReadOnlyMemory<byte> GetKey(string tenantId) => _key;
    }
}
