using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public class TimeoutRejectedException : ExecutionRejectedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutRejectedException" /> class.
    /// </summary>
    public TimeoutRejectedException()
        : base("The operation didn't complete within the allowed timeout.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public TimeoutRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TimeoutRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutRejectedException" /> class.
    /// </summary>
    /// <param name="timeout">The timeout value that caused this exception.</param>
    public TimeoutRejectedException(TimeSpan timeout)
        : base("The operation didn't complete within the allowed timeout.") => Timeout = timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutRejectedException"/> class.
    /// </summary>
    /// <param name="timeout">The timeout value that caused this exception.</param>
    /// <param name="message">The message.</param>
    public TimeoutRejectedException(string message, TimeSpan timeout)
        : base(message) => Timeout = timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutRejectedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="timeout">The timeout value that caused this exception.</param>
    /// <param name="innerException">The inner exception.</param>
    public TimeoutRejectedException(string message, TimeSpan timeout, Exception innerException)
        : base(message, innerException) => Timeout = timeout;

    /// <summary>
    /// Gets the timeout value that caused this exception.
    /// </summary>
    public TimeSpan Timeout { get; private set; } = System.Threading.Timeout.InfiniteTimeSpan;
}
