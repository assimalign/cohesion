using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>A document wire failure retaining its stable protocol error code.</summary>
public sealed class DocumentClientException : DatabaseClientException
{
    /// <summary>Creates a document client failure.</summary>
    /// <param name="code">The server or client-local protocol error code.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    /// <param name="diagnostics">Structured server diagnostics; null means none.</param>
    public DocumentClientException(ProtocolErrorCode code, string message, Exception? innerException = null,
        IReadOnlyList<Diagnostic>? diagnostics = null)
        : base(code, message, innerException)
    {
        Diagnostics = diagnostics is null ? [] : new List<Diagnostic>(diagnostics).AsReadOnly();
    }

    /// <summary>Gets server diagnostics, including their original codes and source locations.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
