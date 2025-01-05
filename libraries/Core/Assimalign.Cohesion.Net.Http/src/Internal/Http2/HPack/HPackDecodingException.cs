using System;
using System.Runtime.Serialization;

namespace Assimalign.Cohesion.Net.Http.Internal;

// TODO: Should this be public?
[Serializable]
internal sealed class HPackDecodingException : Exception
{
    public HPackDecodingException()
    {
    }

    public HPackDecodingException(string message) : base(message)
    {
    }

    public HPackDecodingException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public HPackDecodingException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}