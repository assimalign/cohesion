using System;

namespace Assimalign.Cohesion.Net;

public abstract class NetworkException : CoreException
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
