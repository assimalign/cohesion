using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>A model-owned response whose content is consumed through a live download stream.</summary>
/// <remarks>
/// <para>
/// The shared client keeps the frame exchange running while it hands content to the caller through
/// bounded buffering. A model client derives from this type and implements two phases:
/// <see cref="OpenCoreAsync(ProtocolFrameReader, ProtocolFrameWriter, CancellationToken)"/> sends the
/// request and validates the response's opening metadata, and
/// <see cref="CopyToCoreAsync(ProtocolFrameReader, ProtocolFrameWriter, Stream, CancellationToken)"/>
/// copies the content and verifies its terminal completion.
/// </para>
/// <para>
/// Only <see cref="DatabaseConnection.ExecuteStreamingAsync(DatabaseStreamingExchange, CancellationToken)"/>
/// and <see cref="DatabaseClient.ExecuteStreamingAsync(DatabaseStreamingExchange, CancellationToken)"/> run
/// the phases, in that order, so their entry points are internal to Database.Client and the constructor is
/// protected. An implementation never retains or disposes the connection's reader or writer, and never
/// disposes the content destination it is handed.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseStreamingExchange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseStreamingExchange"/> class.
    /// </summary>
    /// <param name="family">The exact message family instance the operation requires.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="family"/> is null.</exception>
    /// <remarks>
    /// Protected, not private protected: the streaming exchanges live in the Graph and Blob clients.
    /// </remarks>
    protected DatabaseStreamingExchange(ProtocolMessageFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        Family = family;
    }

    /// <summary>Gets the exact family instance required by this operation.</summary>
    public ProtocolMessageFamily Family { get; }

    /// <summary>Runs the opening phase.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="cancellationToken">Cancellation token for the entire streaming exchange.</param>
    /// <returns>A task that completes when the caller can receive the content stream.</returns>
    internal ValueTask OpenAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        => OpenCoreAsync(reader, writer, cancellationToken);

    /// <summary>Runs the content phase.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="destination">The bounded writable handoff to the caller.</param>
    /// <param name="cancellationToken">Cancellation token for the entire streaming exchange.</param>
    /// <returns>A task that completes only after the entire response has been verified.</returns>
    internal ValueTask CopyToAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, Stream destination, CancellationToken cancellationToken)
        => CopyToCoreAsync(reader, writer, destination, cancellationToken);

    /// <summary>Sends the request and consumes and validates the response's initial metadata.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="cancellationToken">Cancellation token for the entire streaming exchange.</param>
    /// <returns>A task that completes when the caller can receive the content stream.</returns>
    /// <remarks>A failure here faults stream creation; later failures surface from stream reads.</remarks>
    /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="ProtocolException">The initial model response is invalid.</exception>
    /// <exception cref="OperationCanceledException">The streaming operation is canceled.</exception>
    protected abstract ValueTask OpenCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken);

    /// <summary>Copies response content and consumes and validates its terminal completion.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="destination">The bounded writable handoff to the caller; writes apply backpressure.</param>
    /// <param name="cancellationToken">Cancellation token for the entire streaming exchange.</param>
    /// <returns>A task that completes only after the entire response has been verified.</returns>
    /// <remarks>
    /// Returning successfully certifies that the connection is ready for another exchange. Any
    /// exception leaves the connection unusable, independently of the server's diagnostic error code.
    /// Content writes may have any size; the shared destination limits its own copied chunks.
    /// </remarks>
    /// <exception cref="DatabaseClientException">The server reports a transfer failure or the connection fails.</exception>
    /// <exception cref="ProtocolException">The model response or its terminal completion is invalid.</exception>
    /// <exception cref="OperationCanceledException">The streaming operation is canceled.</exception>
    protected abstract ValueTask CopyToCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, Stream destination,
        CancellationToken cancellationToken);
}
