using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using System.Buffers;

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
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
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
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
        _stream = new PipeStream(this);
    }

    public PipeReader Input => _input;
    public PipeWriter Output => _output;
    public Stream GetStream() => _stream;
}