using System.Net;

namespace OAuth2ClientCredentials;

public sealed class TokenRequestException(
    HttpStatusCode statusCode,
    string? error,
    string? description,
    bool isTransient,
    Exception? innerException = null)
    : Exception($"Token request failed: {(int)statusCode} {error} {description}".TrimEnd(), innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Error { get; } = error;

    public string? ErrorDescription { get; } = description;

    public bool IsTransient { get; } = isTransient;
}
