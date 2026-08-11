using System.Net;

namespace Logibooks.Alta.CoreApi;

internal class CoreApiException(string message) : Exception(message);

internal sealed class CoreConfigurationException(string message) : CoreApiException(message);

internal sealed class CoreAuthenticationException(string message) : CoreApiException(message);

internal sealed class CoreAuthorizationException(string message) : CoreApiException(message);

internal sealed class CoreRequestException(HttpStatusCode statusCode)
    : CoreApiException($"Logibooks Core returned HTTP status {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
