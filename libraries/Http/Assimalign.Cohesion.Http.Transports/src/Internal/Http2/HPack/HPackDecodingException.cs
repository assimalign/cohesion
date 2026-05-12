using System;
namespace Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

[Serializable]
internal sealed class HPackDecodingException : Exception
{
    public HPackDecodingException()
    {
    }

    public HPackDecodingException(string message)
        : base(message)
    {
    }

    public HPackDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
