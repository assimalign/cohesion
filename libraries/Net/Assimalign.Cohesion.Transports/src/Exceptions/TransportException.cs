using System;

namespace Assimalign.Cohesion.Transports;

public class TransportException : Exception
{
    public TransportException(string message) 
        : base(message) { }

    public TransportException(string message, Exception inner) 
        : base(message, inner) { }
}
