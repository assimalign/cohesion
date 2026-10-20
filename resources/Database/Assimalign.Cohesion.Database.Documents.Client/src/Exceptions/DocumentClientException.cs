using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>A document wire failure retaining its protocol code, composed server text and structured diagnostics.</summary>
public sealed class DocumentClientException : DatabaseException
{
    /// <summary>Creates a document client failure.</summary>
    /// <param name="code">The server or client-local protocol error code.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    /// <param name="diagnostics">Structured server diagnostics; null means none.</param>
    public DocumentClientException(ProtocolErrorCode code, string message, Exception? innerException = null,
        IReadOnlyList<Diagnostic>? diagnostics = null)
        : base(message, diagnostics ?? [], innerException)
    {
        Code = code;
    }

    /// <summary>Gets the stable protocol error code.</summary>
    public ProtocolErrorCode Code { get; }
}
