using System;
using System.IO;
using System.IO.Pipelines;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// A generic wrapper for turning a <see cref="ITransportConnectionPipe"/> into a stream.
/// </summary>
public sealed class PipeStream : Stream
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly bool _throwOnCanceled;

    private volatile bool _isCancelCalled;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pipe"></param>
    /// <param name="throwOnCancelled"></param>
    public PipeStream(ITransportConnectionPipe pipe, bool throwOnCancelled = false) 
        : this(pipe.Input, pipe.Output, throwOnCancelled) 
    {
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <param name="throwOnCancelled"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public PipeStream(PipeReader input, PipeWriter output, bool throwOnCancelled = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
        _throwOnCanceled = throwOnCancelled;
    }

    /// <summary>
    /// Always returns true.
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    /// Always returns false
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Always return true.
    /// </summary>
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
  
    public override int Read(byte[] buffer, int offset, int count)
    {
        var valueTask = ReadAsyncInternal(new Memory<byte>(buffer, offset, count), default);
        
        return valueTask.IsCompleted ?
            valueTask.Result :
            valueTask.AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        return ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        return ReadAsyncInternal(destination, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override Task WriteAsync(byte[]? buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _output.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).GetAsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        return _output.WriteAsync(source, cancellationToken).GetAsValueTask();
    }

    public override void Flush()
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _output.FlushAsync(cancellationToken).GetAsTask();
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);
    }

    public override int EndRead(IAsyncResult asyncResult)
    {
        return TaskToApm.End<int>(asyncResult);
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        return TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        TaskToApm.End(asyncResult);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadAsyncInternal(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> readableBuffer = result.Buffer;
            try
            {
                if (_throwOnCanceled && result.IsCanceled && _isCancelCalled)
                {
                    // Reset the bool
                    _isCancelCalled = false;
                    throw new OperationCanceledException();
                }
                if (!readableBuffer.IsEmpty)
                {
                    // buffer.Count is int
                    var count = (int)Math.Min(readableBuffer.Length, destination.Length);
                    readableBuffer = readableBuffer.Slice(0, count);
                    readableBuffer.CopyTo(destination.Span);
                    return count;
                }

                if (result.IsCompleted)
                {
                    return 0;
                }
            }
            finally
            {
                _input.AdvanceTo(readableBuffer.End, readableBuffer.End);
            }
        }
    }
}