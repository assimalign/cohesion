using System;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// The area-scoped root exception for object mapping failures.
/// </summary>
public class MapperException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MapperException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public MapperException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MapperException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public MapperException(string message, Exception inner) : base(message, inner)
    {
    }
}
