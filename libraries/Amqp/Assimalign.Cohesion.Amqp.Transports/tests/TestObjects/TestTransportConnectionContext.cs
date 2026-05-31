using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports.Tests.TestObjects;

internal sealed class TestTransportConnectionContext : TransportConnectionContext
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
    }

    public override EndPoint LocalEndPoint { get; }

    public override EndPoint RemoteEndPoint { get; }

    public override ITransportConnectionPipe Pipe { get; }

    public async Task<byte[]> ReadOutputAsync()
    {
        ReadResult result = await _outputReader.ReadAsync();
        byte[] output = result.Buffer.ToArray();
        _outputReader.AdvanceTo(result.Buffer.End);
        return output;
    }
}
