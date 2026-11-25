namespace OpenConnect.Abstractions.Messaging;

/// <summary>Sanitized domain exception carrying a stable OpenConnect error code and HTTP status code.</summary>
public sealed class MessagingDomainException : Exception
{
    public MessagingDomainException(string errorCode, string message, int statusCode = 400, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }

    public int StatusCode { get; }
}
