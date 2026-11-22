using System.Net;

namespace OpenConnect.Client.Cloud;

public sealed record CloudProblemDetails(
    string? Type,
    string? Title,
    int? Status,
    string? Code,
    string? TraceId,
    bool Retryable = false,
    string? Detail = null);

public sealed class OpenConnectCloudException : HttpRequestException
{
    public OpenConnectCloudException(
        HttpStatusCode statusCode,
        string code,
        string title,
        string? traceId = null,
        bool retryable = false,
        string? detail = null,
        CloudProblemDetails? problem = null,
        Exception? innerException = null)
        : base(BuildSafeMessage(statusCode, code, title, traceId), innerException, statusCode)
    {
        StatusCodeValue = (int)statusCode;
        Code = string.IsNullOrWhiteSpace(code) ? "capability_unavailable" : code;
        Title = string.IsNullOrWhiteSpace(title) ? "OpenConnect Cloud request failed" : title;
        TraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId;
        Retryable = retryable;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        Problem = problem;
    }

    public int StatusCodeValue { get; }
    public string Code { get; }
    public string Title { get; }
    public string? TraceId { get; }
    public bool Retryable { get; }
    public string? Detail { get; }
    public CloudProblemDetails? Problem { get; }

    private static string BuildSafeMessage(HttpStatusCode statusCode, string? code, string? title, string? traceId)
    {
        var safeCode = string.IsNullOrWhiteSpace(code) ? "capability_unavailable" : code;
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "OpenConnect Cloud request failed" : title;
        return string.IsNullOrWhiteSpace(traceId)
            ? $"OpenConnect Cloud request failed ({(int)statusCode} {safeCode}): {safeTitle}."
            : $"OpenConnect Cloud request failed ({(int)statusCode} {safeCode}, traceId={traceId}): {safeTitle}.";
    }
}
