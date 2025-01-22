using System;

namespace Assimalign.Cohesion;

public abstract class CohesionException : Exception
{
    public CohesionException(string message) 
        : base(message) { }
    public CohesionException(string message, Exception innerException) 
        : base(message, innerException) { }
}
