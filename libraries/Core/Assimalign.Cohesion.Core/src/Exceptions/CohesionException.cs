using System;

namespace Assimalign.Cohesion;

/// <summary>
/// 
/// </summary>
public abstract class CohesionException : Exception
{
    protected CohesionException() { }

    protected CohesionException(string message) 
        : base(message) { }

    protected CohesionException(string message, Exception? innerException) 
        : base(message, innerException) { }
}