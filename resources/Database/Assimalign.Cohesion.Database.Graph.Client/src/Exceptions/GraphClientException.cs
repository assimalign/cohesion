using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Client.Internal;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A graph wire failure retaining its stable protocol error code.</summary>
public sealed class GraphClientException : Exception
{
    /// <summary>Gets the complete read-only diagnostic snapshot, ordered by source offset; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

    /// <summary>Creates a graph client failure.</summary>
    /// <param name="code">The server or client-local protocol error code.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public GraphClientException(ProtocolErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) {
        Code = code;
        Diagnostics = ClientDiagnostics.Snapshot(innerException switch
        {
            DatabaseClientException shared => shared.Diagnostics,
            GraphClientException nested => nested.Diagnostics,
            _ => Array.Empty<Diagnostic>(),
        });
    }

    /// <summary>Gets the stable protocol error code.</summary>
    public ProtocolErrorCode Code { get; }
}

