using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>A model-owned response whose content is consumed through a live download stream.</summary>
/// <remarks>
/// The shared client retains the frame exchange while handing content to the caller through bounded
/// buffering. Implementations validate model frames and terminal completion; they never retain or
/// dispose the connection's reader or writer, or dispose the supplied content destination.
/// </remarks>
public interface IDatabaseStreamingExchange
{
    /// <summary>Gets the exact family instance required by this operation.</summary>
    ProtocolMessageFamily Family { get; }

    /// <summary>Sends the request and consumes and validates the response's initial metadata.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="cancellationToken">Cancellation token for the entire streaming exchange.</param>
    /// <returns>A task that completes when the caller can receive the content stream.</returns>
    /// <remarks>A failure here faults stream creation; later failures surface from stream reads.</remarks>
    /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="ProtocolException">The initial model response is invalid.</exception>
    /// <exception cref="OperationCanceledException">The streaming operation is canceled.</exception>
    ValueTask OpenAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default);

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
    ValueTask CopyToAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, Stream destination,
        CancellationToken cancellationToken = default);
}
