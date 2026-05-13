using System;

namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Domain exception raised by Cohesion cache implementations.
/// </summary>
public class CacheException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="CacheException"/>.
    /// </summary>
    /// <param name="errorCode">The diagnostics code describing the failure.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="innerException">Optional inner exception.</param>
    public CacheException(CacheErrorCode errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the diagnostics code attached to the exception.
    /// </summary>
    public CacheErrorCode ErrorCode { get; }
}
