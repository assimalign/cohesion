using System;

namespace Assimalign.Cohesion.Net.Transports;

public class TransportException : NetworkException
{
    public TransportException(string message) 
        : base(message) { }

    public TransportException(string message, Exception inner) 
        : base(message, inner) { }

    public override NetworkOsiLayer Layer => NetworkOsiLayer.Transport;

    public override CoreErrorCategory Category => CoreErrorCategory.Networking;
}
