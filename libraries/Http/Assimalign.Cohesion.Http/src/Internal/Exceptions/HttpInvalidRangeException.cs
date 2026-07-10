namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidRangeException : HttpException
{
    public HttpInvalidRangeException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidRange;
    }
}
