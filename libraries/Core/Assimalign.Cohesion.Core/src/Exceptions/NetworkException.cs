using System;

namespace Assimalign.Cohesion;

public enum NetworkOsiLayer
{
    Physical = 1,
    DataLink = 2,
    Network = 3,
    Transport = 4,
    Session = 5,
    Presentation = 6,
    Application = 7,
}

public abstract class NetworkException  : CohesionException
{
    public NetworkException(string message)
        : base(message) { }

    public NetworkException(string message, Exception innerException)
        : base(message, innerException) { }


    /// <summary>
    /// 
    /// </summary>
    public abstract NetworkOsiLayer Layer { get; }
}
