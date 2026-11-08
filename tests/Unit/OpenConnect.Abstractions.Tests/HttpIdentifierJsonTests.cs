using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class HttpIdentifierJsonTests
{
    private const string Canonical = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Next = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private static readonly JsonSerializerOptions DefaultOptions = OpenConnectHttpJson.CreateOptions(JsonSerializerDefaults.General);
    private static readonly JsonSerializerOptions WebOptions = OpenConnectHttpJson.CreateOptions();

    [Fact]
    public void Ulid_and_all_HTTP_identifier_wrappers_are_canonical_json_strings()
    {
        foreach (var options in new[] { DefaultOptions, WebOptions })
        {
            Assert.Equal($"\"{Canonical}\"", JsonSerializer.Serialize(Ulid.Parse(Canonical), options));
            Assert.Equal($"\"{Canonical}\"", JsonSerializer.Serialize(new TenantId(Ulid.Parse(Canonical)), options));
            Assert.Equal($"\"{Canonical}\"", JsonSerializer.Serialize(new PrincipalId(Ulid.Parse(Canonical)), options));
            Assert.Equal($"\"{Canonical}\"", JsonSerializer.Serialize(new SessionId(Ulid.Parse(Canonical)), options));
            Assert.Equal($"\"{Canonical}\"", JsonSerializer.Serialize(new TaskId(Ulid.Parse(Canonical)), options));
            Assert.Equal($"\"{Canonical}\"", JsonSerializer.Serialize(new ExecutionId(Ulid.Parse(Canonical)), options));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ulid_and_wrappers_round_trip_with_general_and_web_options(bool web)
    {
        var options = web ? WebOptions : DefaultOptions;
        Assert.Equal(Ulid.Parse(Canonical), JsonSerializer.Deserialize<Ulid>($"\"{Canonical}\"", options));
        Assert.Equal(new TenantId(Ulid.Parse(Canonical)), JsonSerializer.Deserialize<TenantId>($"\"{Canonical}\"", options));
        Assert.Equal(new PrincipalId(Ulid.Parse(Canonical)), JsonSerializer.Deserialize<PrincipalId>($"\"{Canonical}\"", options));
        Assert.Equal(new SessionId(Ulid.Parse(Canonical)), JsonSerializer.Deserialize<SessionId>($"\"{Canonical}\"", options));
        Assert.Equal(new TaskId(Ulid.Parse(Canonical)), JsonSerializer.Deserialize<TaskId>($"\"{Canonical}\"", options));
        Assert.Equal(new ExecutionId(Ulid.Parse(Canonical)), JsonSerializer.Deserialize<ExecutionId>($"\"{Canonical}\"", options));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"00000000000000000000000000\"")]
    [InlineData("\"01ARZ3NDEKTSV4RRFFQ69G5FA!")]
    [InlineData("\"Z1ARZ3NDEKTSV4RRFFQ69G5FAV\"")]
    [InlineData("\"81ARZ3NDEKTSV4RRFFQ69G5FAV\"")]
    [InlineData("\"01arz3ndektsv4rrffq69g5fav\"")]
    public void Ulid_and_wrappers_reject_invalid_http_identity_encodings(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Ulid>(json, WebOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TenantId>(json, WebOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PrincipalId>(json, WebOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionId>(json, WebOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TaskId>(json, WebOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExecutionId>(json, WebOptions));
    }

    [Fact]
    public void Task_record_round_trips_with_nonzero_identifiers()
    {
        var record = new TaskRecord(new TaskId(Ulid.Parse(Canonical)), new TenantId(Ulid.Parse(Next)),
            new PrincipalId(Ulid.Parse(Canonical)), AgentRunState.Accepted);

        var json = JsonSerializer.Serialize(record, WebOptions);
        Assert.Contains($"\"id\":\"{Canonical}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"tenantId\":\"{Next}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"principalId\":\"{Canonical}\"", json, StringComparison.Ordinal);
        Assert.Equal(record, JsonSerializer.Deserialize<TaskRecord>(json, WebOptions));
    }

    [Fact]
    public void Other_string_identifier_types_are_not_claimed_by_the_factory()
    {
        var factory = new UlidIdentifierJsonConverterFactory();

        Assert.False(factory.CanConvert(typeof(AgentId)));
        Assert.False(factory.CanConvert(typeof(CapabilityId)));
    }
}
