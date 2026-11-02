using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The exception a gateway raises when it refuses a publication or cannot start its deployment.
/// </summary>
/// <remarks>
/// <see cref="ErrorCode"/> lets a transport answer without parsing the message: a publication to a
/// gateway that is not running, to a resource the gateway does not run, or to a resource it tore
/// down each has its own code.
/// </remarks>
public class ApplicationGatewayException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="ApplicationGatewayException"/>.
    /// </summary>
    /// <param name="errorCode">The code describing the failure.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public ApplicationGatewayException(ApplicationGatewayErrorCode errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the code describing the failure.
    /// </summary>
    public ApplicationGatewayErrorCode ErrorCode { get; }
}
