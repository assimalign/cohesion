using System;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>A Blob wire operation failed; the connection is no longer reusable.</summary>
public sealed class BlobClientException : DatabaseException
{
    /// <summary>Creates an exception preserving a server or client-local wire error code.</summary>
    /// <param name="code">The server code, or ProtocolViolation/Internal for a local failure.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public BlobClientException(ProtocolErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>Gets the stable wire error code.</summary>
    public ProtocolErrorCode Code { get; }
}
