namespace Assimalign.Cohesion.Web.Http.Internal;

internal class HttpInvalidPathException : HttpException
{
    public HttpInvalidPathException(string message) : base(message) { }
}
