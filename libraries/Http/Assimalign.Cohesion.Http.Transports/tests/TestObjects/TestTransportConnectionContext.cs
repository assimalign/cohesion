using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class TestTransportConnectionContext : ITransportConnectionContext
{
    private readonly PipeReader _outputReader;

    public TestTransportConnectionContext(byte[] input, EndPoint? localEndPoint = null, EndPoint? remoteEndPoint = null)
    {
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
        Items = new Dictionary<string, object?>(System.StringComparer.Ordinal);
    }

    public EndPoint LocalEndPoint { get; }

    public EndPoint RemoteEndPoint { get; }

    public ITransportConnectionPipe Pipe { get; }

    public IDictionary<string, object?> Items { get; }

    public async Task<byte[]> ReadOutputAsync()
    {
        ReadResult result = await _outputReader.ReadAsync();
        byte[] output = result.Buffer.ToArray();
        _outputReader.AdvanceTo(result.Buffer.End);
        return output;
    }
}
