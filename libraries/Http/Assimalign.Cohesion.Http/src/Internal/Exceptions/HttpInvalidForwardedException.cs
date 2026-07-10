namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidForwardedException : HttpException
{
    public HttpInvalidForwardedException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidForwarded;
    }
}
