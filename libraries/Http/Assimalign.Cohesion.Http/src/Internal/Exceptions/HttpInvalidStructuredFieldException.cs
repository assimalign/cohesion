namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidStructuredFieldException : HttpException
{
    public HttpInvalidStructuredFieldException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidStructuredField;
    }
}
