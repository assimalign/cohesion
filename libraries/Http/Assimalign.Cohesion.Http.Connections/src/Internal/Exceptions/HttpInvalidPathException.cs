namespace Assimalign.Cohesion.Http.Internal;

internal class HttpInvalidPathException : HttpException
{
    public HttpInvalidPathException(string message) : base(message) { }
}
