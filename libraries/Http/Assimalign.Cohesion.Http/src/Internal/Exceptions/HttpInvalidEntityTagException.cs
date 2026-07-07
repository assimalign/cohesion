namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidEntityTagException : HttpException
{
    public HttpInvalidEntityTagException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidEntityTag;
    }
}
