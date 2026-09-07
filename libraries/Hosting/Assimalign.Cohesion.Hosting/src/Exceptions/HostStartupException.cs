using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Represents a failure that prevents a host resource from completing startup.
/// </summary>
public sealed class HostStartupException : HostException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HostStartupException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the startup failure.</param>
    public HostStartupException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HostStartupException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the startup failure.</param>
    /// <param name="innerException">The exception that caused the startup failure.</param>
    public HostStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
