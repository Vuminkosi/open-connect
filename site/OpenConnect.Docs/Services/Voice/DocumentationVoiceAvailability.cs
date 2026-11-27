using System.Text.Json;

namespace OpenConnect.Docs.Services.Voice;

/// <summary>Reads the application host's local voice status with the visitor's server-side credential.</summary>
public sealed class DocumentationVoiceAvailability(HttpClient client) : IDisposable
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(22));
        try
        {
            using var response = await client.GetAsync(new Uri(client.BaseAddress!, "/internal/docs/voice/model-ready"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[1025];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
            if (total > 1024)
                return false;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, total));
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException) { return false; }
    }

    public void Dispose() => client.Dispose();
}
