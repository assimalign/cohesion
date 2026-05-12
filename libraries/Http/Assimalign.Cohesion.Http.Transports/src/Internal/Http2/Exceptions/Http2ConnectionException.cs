namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal class Http2ConnectionException : HttpException
{
    public Http2ConnectionException(string message)
        : base(message)
    {
    }
}
