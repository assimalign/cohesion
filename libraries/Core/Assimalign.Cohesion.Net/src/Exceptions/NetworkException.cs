using System;

namespace Assimalign.Cohesion.Net;

public abstract class NetworkException : CohesionException
{
    public NetworkException(string message) 
        : base(message)
    {
    }

    public NetworkException(string message, Exception? innerException) 
        : base(message, innerException)
    {
    }

    public abstract NetworkOsiLayer Layer { get; }
}
