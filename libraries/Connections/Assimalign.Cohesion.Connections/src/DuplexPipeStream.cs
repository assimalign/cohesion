using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Adapts an <see cref="IDuplexPipe"/> as a bidirectional <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// Use this where a stream-based API (such as <see cref="System.Net.Security.SslStream"/>) must
/// operate over a connection's duplex pipe. The adapter is allocation-light and lazy; prefer the
/// pipe surface directly for parsing and writing where possible.
/// </remarks>
public sealed class DuplexPipeStream : Stream
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplexPipeStream"/> class over a duplex pipe.
    /// </summary>
    /// <param name="pipe">The duplex pipe to adapt.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipe"/> is <see langword="null"/>.</exception>
    public DuplexPipeStream(IDuplexPipe pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        _input = pipe.Input;
        _output = pipe.Output;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplexPipeStream"/> class over a reader/writer pair.
    /// </summary>
    /// <param name="input">The reader supplying the stream's read side.</param>
    /// <param name="output">The writer backing the stream's write side.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.</exception>
    public DuplexPipeStream(PipeReader input, PipeWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => ReadInternalAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadInternalAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadInternalAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadInternalAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (!buffer.IsEmpty)
            {
                int copied = (int)Math.Min(buffer.Length, destination.Length);
                ReadOnlySequence<byte> consumed = buffer.Slice(0, copied);
                consumed.CopyTo(destination.Span);
                _input.AdvanceTo(consumed.End);
                return copied;
            }

            if (result.IsCompleted)
            {
                return 0;
            }

            if (result.IsCanceled)
            {
                throw new OperationCanceledException();
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}
