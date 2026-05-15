namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidRequestTargetException : HttpException
{
    public HttpInvalidRequestTargetException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidRequestTarget;
    }
}
