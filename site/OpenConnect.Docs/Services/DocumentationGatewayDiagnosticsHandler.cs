namespace OpenConnect.Docs.Services;

/// <summary>Records bounded, content-free failure categories for the documentation gateway.</summary>
internal sealed class DocumentationGatewayDiagnosticsHandler(
    ILogger<DocumentationGatewayDiagnosticsHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Documentation assistant gateway returned HTTP status {StatusCode}", (int)response.StatusCode);
            return response;
        }
        catch (HttpRequestException exception)
        {
            // Exception messages can contain URLs or other sensitive data.
            logger.LogWarning("Documentation assistant gateway transport failed: {ErrorCategory}", exception.HttpRequestError);
            throw;
        }
    }
}
