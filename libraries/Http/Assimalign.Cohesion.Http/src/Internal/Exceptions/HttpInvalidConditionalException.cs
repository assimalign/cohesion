namespace Assimalign.Cohesion.Http.Internal;

internal sealed class HttpInvalidConditionalException : HttpException
{
    public HttpInvalidConditionalException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidConditional;
    }
}
