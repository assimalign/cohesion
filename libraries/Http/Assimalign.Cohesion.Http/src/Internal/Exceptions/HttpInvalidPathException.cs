
namespace Assimalign.Cohesion.Http.Internal;

internal class HttpInvalidPathException : HttpException
{
    public HttpInvalidPathException(string message) : base(message) { }

    public override NetworkOsiLayer Layer => throw new System.NotImplementedException();
}
