namespace Assimalign.Cohesion.Http.Connections.Internal;

internal class HttpInvalidPathException : HttpException
{
    public HttpInvalidPathException(string message) : base(message) { }
}
