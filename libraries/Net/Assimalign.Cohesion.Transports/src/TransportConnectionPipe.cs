using System;
using System.IO;
using System.IO.Pipelines;

namespace Assimalign.Cohesion.Transports;

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
    /// <param name="stream">The stream used by the connection pipe.</param>
    /// <param name="inputOptions">The optional reader configuration.</param>
    /// <param name="outputOptions">The optional writer configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
    public TransportConnectionPipe(
        Stream stream,
        StreamPipeReaderOptions? inputOptions = null,
        StreamPipeWriterOptions? outputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _input = inputOptions is null
            ? PipeReader.Create(stream)
            : PipeReader.Create(stream, inputOptions);
        _output = outputOptions is null
            ? PipeWriter.Create(stream)
            : PipeWriter.Create(stream, outputOptions);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportConnectionPipe(PipeReader input, PipeWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
        _stream = new PipeStream(this);
    }

    public PipeReader Input => _input;
    public PipeWriter Output => _output;
    public Stream Stream => _stream;
}
