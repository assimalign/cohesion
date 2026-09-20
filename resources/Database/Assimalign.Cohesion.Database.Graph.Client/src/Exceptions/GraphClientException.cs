using System;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A graph wire failure retaining its stable protocol error code.</summary>
public sealed class GraphClientException : DatabaseException
{
    /// <summary>Creates a graph client failure.</summary>
    /// <param name="code">The server or client-local protocol error code.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public GraphClientException(ProtocolErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>Gets the stable protocol error code.</summary>
    public ProtocolErrorCode Code { get; }
}

