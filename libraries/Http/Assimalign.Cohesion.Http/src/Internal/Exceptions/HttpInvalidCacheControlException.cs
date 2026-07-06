namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidCacheControlException : HttpException
{
    public HttpInvalidCacheControlException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidCacheControl;
    }
}
