namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidPathException : HttpException
{
    public HttpInvalidPathException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidPath;
    }
}
