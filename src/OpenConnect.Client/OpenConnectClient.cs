using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
using OpenConnect.Abstractions;
namespace OpenConnect.Client;
public sealed class OpenConnectClient(HttpClient httpClient) : IOpenConnectClient
{
    private static readonly ActivitySource ActivitySource = new("OpenConnect.Client");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientTaskId) || request.ClientTaskId.Length > 128) throw new ArgumentException("A client task ID of 1 to 128 characters is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256) throw new ArgumentException("An idempotency key of 1 to 256 characters is required.", nameof(request));
        using var activity = ActivitySource.StartActivity("openconnect.task.create", ActivityKind.Client);
        using var message = new HttpRequestMessage(HttpMethod.Post, "tasks") { Content = JsonContent.Create(new { clientTaskId = request.ClientTaskId, message = request.Message, sessionId = request.SessionId }, options: JsonOptions) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TaskRecord>(response, cancellationToken).ConfigureAwait(false);
    }
    public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default) => SendAsync<TaskRecord>(HttpMethod.Post, $"tasks/{taskId.Value}/cancel", cancellationToken);
    public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default) => SendAsync<TaskRecord>(HttpMethod.Get, $"tasks/{taskId.Value}", cancellationToken);
    public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default) => SendAsync<SessionRecord>(HttpMethod.Get, $"sessions/{sessionId.Value}", cancellationToken);
    public async Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => await SendAsync<List<CapabilityDescriptor>>(HttpMethod.Get, "capabilities", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Retrieves one finite snapshot of events available after the specified sequence.
    /// This method does not poll or reconnect; call it again with the last received event's
    /// sequence as the cursor to retrieve subsequent events.
    /// </summary>
    /// <param name="sessionId">The session whose events are retrieved.</param>
    /// <param name="afterSequence">The exclusive sequence cursor; defaults to zero.</param>
    /// <param name="cancellationToken">A token used to cancel the request or enumeration.</param>
    /// <returns>The events available after the cursor at the time of the request.</returns>
    public async IAsyncEnumerable<EventRecord> SubscribeEventsAsync(SessionId sessionId, long afterSequence = 0, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        using var activity = ActivitySource.StartActivity("openconnect.events.subscribe", ActivityKind.Client);
        using var response = await _httpClient.GetAsync($"sessions/{sessionId.Value}/events?after={afterSequence.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var events = await ReadAsync<List<EventRecord>>(response, cancellationToken).ConfigureAwait(false);
        foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("openconnect.http", ActivityKind.Client);
        using var request = new HttpRequestMessage(method, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token) => await response.Content.ReadFromJsonAsync<T>(JsonOptions, token).ConfigureAwait(false) ?? throw new InvalidDataException("The OpenConnect service returned an empty response.");
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null;
        const string message = "The OpenConnect service could not complete the request.";
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("code", out var value) || doc.RootElement.TryGetProperty("type", out value)) &&
                value.ValueKind == JsonValueKind.String)
            {
                var candidate = value.GetString();
                if (candidate is { Length: > 0 and <= 64 } && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                    code = candidate;
            }
        }
        catch (JsonException) { }
        throw new OpenConnectClientException((int)response.StatusCode, code, message);
    }
}
