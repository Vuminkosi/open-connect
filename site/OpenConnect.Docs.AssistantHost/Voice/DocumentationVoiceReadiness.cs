using System.Net.Http.Headers;
using System.Text.Json;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Docs.AssistantHost.Voice;

/// <summary>Checks the configured speech-to-text route or local-only transcription model without exposing credentials.</summary>
internal sealed class DocumentationVoiceReadiness(
    HttpClient client,
    IConfiguration configuration,
    IRealtimeSpeechToTextClient? realtimeSpeechToText = null)
{
    private const int MaxResponseBytes = 1024;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Workers:Transcription:RealtimeLocalOnly")
            && realtimeSpeechToText?.IsConfigured == true)
        {
            return true;
        }

        var endpoint = configuration["Workers:Transcription:Endpoint"];
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        if (!configuration.GetValue<bool>("Workers:Transcription:RealtimeLocalOnly") ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/internal/v1/realtime/local/model-ready"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[MaxResponseBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
            if (total > MaxResponseBytes)
                return false;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, total));
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("modelReady", out var ready) && ready.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException) { return false; }
    }
}
