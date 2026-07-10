namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidContentRangeException : HttpException
{
    public HttpInvalidContentRangeException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidContentRange;
    }
}
