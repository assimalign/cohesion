using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestTransportConnectionContext : TransportConnectionContext, IDisposable
{
    private readonly PipeReader _outputReader;
    private readonly CancellationTokenSource _connectionCancelledSource;

    public TestTransportConnectionContext(byte[] input, EndPoint? localEndPoint = null, EndPoint? remoteEndPoint = null, bool isBidirectional = true)
    {
        IsBidirectional = isBidirectional;

        // Pipes default to pauseWriterThreshold = 64 KB, which blocks the
        // synchronous prime write below for any input larger than that.
        // Tests for flow-control / large-frame scenarios need bigger
        // buffers; disable the threshold so all test payloads land
        // immediately.
        PipeOptions pipeOptions = new(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false);
        Pipe inputPipe = new(pipeOptions);
        Pipe outputPipe = new(pipeOptions);

        inputPipe.Writer.WriteAsync(input).GetAwaiter().GetResult();
        inputPipe.Writer.Complete();

        Pipe = new TransportConnectionPipe(inputPipe.Reader, outputPipe.Writer);
        _outputReader = outputPipe.Reader;
        LocalEndPoint = localEndPoint ?? new IPEndPoint(IPAddress.Loopback, 8080);
        RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 5000);
        _connectionCancelledSource = new CancellationTokenSource();
    }

    public override EndPoint LocalEndPoint { get; }

    public override EndPoint RemoteEndPoint { get; }

    public override ITransportConnectionPipe Pipe { get; }

    /// <summary>
    /// Whether this stream is bidirectional (a request stream) or
    /// unidirectional (an HTTP/3 control / QPACK / push stream). Defaults to
    /// <see langword="true"/>; set to <see langword="false"/> to simulate a
    /// peer-initiated unidirectional stream.
    /// </summary>
    public override bool IsBidirectional { get; }

    /// <summary>
    /// Cancellation token signalled when the test driver simulates a
    /// connection close. Test code can wire this through to assertions
    /// that exercise the connection-lifetime hook on
    /// <see cref="ITransportConnectionContext.PipelineCancelled"/>.
    /// </summary>
    public override CancellationToken PipelineCancelled => _connectionCancelledSource.Token;

    /// <summary>
    /// Trips <see cref="PipelineCancelled"/> so receive-loop tests can
    /// drive the same lifetime signal a real transport would raise.
    /// </summary>
    public void CancelConnection() => _connectionCancelledSource.Cancel();

    public async Task<byte[]> ReadOutputAsync()
    {
        ReadResult result = await _outputReader.ReadAsync();
        byte[] output = result.Buffer.ToArray();
        _outputReader.AdvanceTo(result.Buffer.End);
        return output;
    }

    public void Dispose() => _connectionCancelledSource.Dispose();
}
