using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a base exception for HTTP failures.
/// </summary>
public abstract class HttpException : NetworkException
{
    /// <summary>
    /// Initializes a new HTTP exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    protected HttpException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new HTTP exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="inner">The inner exception.</param>
    protected HttpException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Gets the error code associated with the failure.
    /// </summary>
    public HttpErrorCode Code { get; init; }

    /// <inheritdoc />
    public override NetworkOsiLayer Layer => NetworkOsiLayer.Application;
}
