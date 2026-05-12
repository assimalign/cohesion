namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidMethodException : HttpException
{
    public HttpInvalidMethodException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidMethod;
    }
}
