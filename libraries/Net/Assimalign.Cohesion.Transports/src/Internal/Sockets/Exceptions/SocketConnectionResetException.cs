using System;

namespace Assimalign.Cohesion.Transports.Internal;

internal class SocketConnectionResetException : TransportException
{
    public SocketConnectionResetException(string message) 
        : base(message) { }

    public SocketConnectionResetException(string message, Exception inner) 
        : base(message, inner) { }
}
