namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidMediaTypeException : HttpException
{
    public HttpInvalidMediaTypeException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidMediaType;
    }
}
