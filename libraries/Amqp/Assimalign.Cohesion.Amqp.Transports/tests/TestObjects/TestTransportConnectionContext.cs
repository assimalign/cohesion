using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports.Tests.TestObjects;

internal sealed class TestTransportConnectionContext : ITransportConnectionContext
{
    private readonly PipeReader _outputReader;

    public TestTransportConnectionContext(byte[] input, EndPoint? localEndPoint = null, EndPoint? remoteEndPoint = null)
    {
        Pipe inputPipe = new();
        Pipe outputPipe = new();

        inputPipe.Writer.WriteAsync(input).GetAwaiter().GetResult();
        inputPipe.Writer.Complete();

        Pipe = new TransportConnectionPipe(inputPipe.Reader, outputPipe.Writer);
        _outputReader = outputPipe.Reader;
        LocalEndPoint = localEndPoint ?? new IPEndPoint(IPAddress.Loopback, 5672);
        RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 45678);
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
