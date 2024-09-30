using System;

namespace Assimalign.Cohesion;

public class CoreException : Exception
{
    public CoreException(string message) 
        : base(message) { }

    public CoreException(string message, Exception? innerException) 
        : base(message, innerException) { }
}
