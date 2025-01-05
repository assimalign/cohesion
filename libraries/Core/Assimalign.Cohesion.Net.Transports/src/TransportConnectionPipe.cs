using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public sealed class TransportConnectionPipe : ITransportConnectionPipe
{
    private readonly Stream stream;

    /// <summary>
    /// Creates a <see cref="ITransportConnectionPipe"/> from a stream.
    /// </summary>
    /// <param name="stream"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportConnectionPipe(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        this.stream = stream;
        this.Input = PipeReader.Create(stream);
        this.Output = PipeWriter.Create(stream);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportConnectionPipe(PipeReader input, PipeWriter output)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        Input = input;
        Output = output;
        stream = new PipeStream(this);
    }

    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public Stream GetStream() => stream;



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