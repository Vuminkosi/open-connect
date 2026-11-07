namespace OpenConnect.Client;
public sealed class OpenConnectClientException(int statusCode, string? errorCode, string message)
    : HttpRequestException(message, null, (System.Net.HttpStatusCode)statusCode)
{
    public int StatusCodeValue { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
}
