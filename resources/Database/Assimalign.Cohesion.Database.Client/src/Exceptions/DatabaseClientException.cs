using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Client.Internal;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Thrown when a client operation fails: a failed dial, a server error frame, a broken
/// connection mid-exchange, or invalid connection settings.
/// </summary>
/// <remarks>
/// Server-reported failures carry the wire's stable <see cref="ProtocolErrorCode"/>
/// in <see cref="Code"/>. A failed dial uses <see cref="ProtocolErrorCode.ConnectionFailure"/>
/// and keeps the transport's exception as <see cref="Exception.InnerException"/>; other
/// client-local failures (settings, a transport broken after the dial, in the handshake or in
/// an exchange) use <see cref="ProtocolErrorCode.Internal"/>, and a broken transport keeps its
/// exception (an <see cref="System.IO.IOException"/>, a
/// <see cref="System.Net.Sockets.SocketException"/>, a connection reset or abort) as the inner
/// exception too. No server sends <see cref="ProtocolErrorCode.ConnectionFailure"/>: an error
/// frame that carries it is a <see cref="ProtocolErrorCode.ProtocolViolation"/>.
/// </remarks>
public sealed class DatabaseClientException : Exception
{
    /// <summary>Gets the complete read-only diagnostic snapshot, ordered by source offset; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

    /// <summary>
    /// Initializes a new <see cref="DatabaseClientException"/>.
    /// </summary>
    /// <param name="code">The wire error code the failure maps to.</param>
    /// <param name="message">The error message.</param>
    public DatabaseClientException(ProtocolErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes a new <see cref="DatabaseClientException"/> with an inner exception.
    /// </summary>
    /// <param name="code">The wire error code the failure maps to.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseClientException(ProtocolErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Creates a statement failure with its negotiated structured diagnostics.</summary>
    /// <param name="code">The wire error category.</param>
    /// <param name="message">The unchanged Error frame text.</param>
    /// <param name="diagnostics">The complete wire diagnostics.</param>
    /// <param name="innerException">The underlying cause, when available.</param>
    public DatabaseClientException(ProtocolErrorCode code, string message, IReadOnlyList<ProtocolDiagnostic> diagnostics, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
        Diagnostics = ClientDiagnostics.Snapshot(diagnostics.Select(entry => new Diagnostic
        {
            Code = entry.Code, Message = entry.Message,
            Severity = entry.Severity == ProtocolDiagnosticSeverity.Warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            Start = entry.Start, End = entry.End, Line = entry.Line,
            Location = entry.Start.HasValue ? DiagnosticLocation.Absolute : null,
        }));
    }

    /// <summary>
    /// Gets the wire error code of the failure.
    /// </summary>
    public ProtocolErrorCode Code { get; }
}
