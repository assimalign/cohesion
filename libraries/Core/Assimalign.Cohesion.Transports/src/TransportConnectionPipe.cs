using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;

/// <summary>
/// A generic connection pipe which data is read and written to.
/// </summary>
public sealed class TransportConnectionPipe : ITransportConnectionPipe
{
    private readonly Stream _stream;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;

    /// <summary>
    /// Creates a <see cref="ITransportConnectionPipe"/> from a stream.
    /// </summary>
    /// <param name="stream"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportConnectionPipe(Stream stream)
    {
        _stream = ThrowHelper.ThrowIfNull(stream);
        _input = PipeReader.Create(stream);
        _output = PipeWriter.Create(stream);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportConnectionPipe(PipeReader input, PipeWriter output)
    {
        _input = ThrowHelper.ThrowIfNull(input);
        _output = ThrowHelper.ThrowIfNull(output);
        _stream = new PipeStream(this);
    }

    public PipeReader Input => _input;

    public PipeWriter Output => _output;

    public Stream GetStream() => _stream;

    public async ValueTask<ReadResult> ReadAsync()
    {
        var result = await Input.ReadAsync();

        Input.AdvanceTo(
            result.Buffer.Start,
            result.Buffer.End);

        //Input.AdvanceTo(result.Buffer.End);

        return result;
    }

    public async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> buffer)
    {
  
        var result = await Output.WriteAsync(buffer);

        //Output.Advance(buffer.Length);

        return result;
    }

}