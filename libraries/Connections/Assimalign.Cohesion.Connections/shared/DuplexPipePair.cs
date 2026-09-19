using System;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Creates the mirrored pair of pipes that connect a transport driver's wire pump to its
/// consumer-facing connection, with the consumer and pump ends named explicitly so drivers never
/// hand-wire the topology.
/// </summary>
/// <remarks>
/// Two pipes are created: the <em>input</em> pipe carries bytes from the wire to the consumer
/// (configured by <c>inputOptions</c>: receive buffering and back-pressure), and the <em>output</em>
/// pipe carries bytes from the consumer to the wire (configured by <c>outputOptions</c>). The
/// consumer end (<see cref="Input"/>/<see cref="Output"/>) becomes the connection's duplex pipe;
/// the pump end (<see cref="TransportOutput"/>/<see cref="TransportInput"/>) is driven by the
/// driver's receive and send loops. The same wiring applies to server- and client-initiated
/// connections; there is no side-dependent variation.
/// </remarks>
// Deviates from the repo namespace-matches-assembly rule per design decision: this file is
// shared source (CohesionSharedSource), compiled into each transport driver that wires a pump
// to its consumer. It is a stateless factory over BCL pipes and no instance of it crosses an
// assembly boundary, so linking a private copy per driver is safe.
internal sealed class DuplexPipePair
{
    private DuplexPipePair(PipeReader input, PipeWriter output, PipeWriter transportOutput, PipeReader transportInput)
    {
        Input = input;
        Output = output;
        TransportOutput = transportOutput;
        TransportInput = transportInput;
    }

    /// <summary>
    /// Gets the consumer-side reader: the bytes received from the wire.
    /// </summary>
    public PipeReader Input { get; }

    /// <summary>
    /// Gets the consumer-side writer: the bytes to send to the wire.
    /// </summary>
    public PipeWriter Output { get; }

    /// <summary>
    /// Gets the pump-side writer the driver's receive loop writes wire bytes into.
    /// </summary>
    public PipeWriter TransportOutput { get; }

    /// <summary>
    /// Gets the pump-side reader the driver's send loop drains to the wire.
    /// </summary>
    public PipeReader TransportInput { get; }

    /// <summary>
    /// Creates the mirrored pipe pair.
    /// </summary>
    /// <param name="inputOptions">Options for the receive (wire-to-consumer) pipe.</param>
    /// <param name="outputOptions">Options for the send (consumer-to-wire) pipe.</param>
    /// <returns>The created <see cref="DuplexPipePair"/>.</returns>
    /// <exception cref="ArgumentNullException">Either pipe options argument is null.</exception>
    public static DuplexPipePair Create(PipeOptions inputOptions, PipeOptions outputOptions)
    {
        ArgumentNullException.ThrowIfNull(inputOptions);
        ArgumentNullException.ThrowIfNull(outputOptions);

        Pipe input = new(inputOptions);
        Pipe output = new(outputOptions);

        return new DuplexPipePair(input.Reader, output.Writer, input.Writer, output.Reader);
    }
}
