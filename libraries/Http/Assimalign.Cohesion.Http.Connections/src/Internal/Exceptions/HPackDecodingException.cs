using System;
namespace Assimalign.Cohesion.Http.Connections.Internal;

[Serializable]
internal class HPackDecodingException : Exception
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
