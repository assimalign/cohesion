using System;

namespace Assimalign.Cohesion.Net.Transports;

public abstract class TransportException : Exception
{
    public TransportException(string message) 
        : base(message) { }

    public TransportException(string message, Exception inner) 
        : base(message, inner) { }
}
