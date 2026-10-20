using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>A Blob wire operation failed; the connection is no longer reusable.</summary>
public sealed class BlobClientException : Exception
{
    /// <summary>Gets the complete read-only diagnostic snapshot, ordered by source offset; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

    /// <summary>Creates an exception preserving a server or client-local wire error code.</summary>
    /// <param name="code">The server code; ConnectionFailure for a failed dial; ProtocolViolation or Internal for another local failure.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public BlobClientException(ProtocolErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>Gets the stable wire error code.</summary>
    public ProtocolErrorCode Code { get; }
}
