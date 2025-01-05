using System;
using System.IO;
using System.IO.Pipelines;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;


namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

/// <summary>
/// A generic wrapper for turning a <see cref="ITransportConnectionPipe"/> into a stream.
/// </summary>
public sealed class PipeStream : Stream
{
    private readonly bool throwOnCanceled;
    private volatile bool isCancelCalled;
    private readonly PipeReader input;
    private readonly PipeWriter output;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pipe"></param>
    public PipeStream(ITransportConnectionPipe pipe) : this(pipe.Input, pipe.Output) 
    {

    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public PipeStream(PipeReader input, PipeWriter output)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        this.input = input;
        this.output = output;
        this.throwOnCanceled = false;
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
    /// <summary>
    /// Not Supported.
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public override long Length => throw new NotSupportedException();
    /// <summary>
    /// Not Supported.
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="origin"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <exception cref="NotSupportedException"></exception>
    public override void SetLength(long value) => throw new NotSupportedException();
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        var valueTask = ReadAsyncInternal(new Memory<byte>(buffer, offset, count), default);
        
        return valueTask.IsCompleted ?
            valueTask.Result :
            valueTask.AsTask().GetAwaiter().GetResult();
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) => ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) => ReadAsyncInternal(destination, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override Task WriteAsync(byte[]? buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return output.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).GetAsTask();
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        return output.WriteAsync(source, cancellationToken).GetAsValueTask();
    }
    public override void Flush()
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken).GetAsTask();

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadAsyncInternal(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellationToken);
            var readableBuffer = result.Buffer;
            try
            {
                if (throwOnCanceled && result.IsCanceled && isCancelCalled)
                {
                    // Reset the bool
                    isCancelCalled = false;
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
                input.AdvanceTo(readableBuffer.End, readableBuffer.End);
            }
        }
    }
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);
    public override int EndRead(IAsyncResult asyncResult) => TaskToApm.End<int>(asyncResult);
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);
    public override void EndWrite(IAsyncResult asyncResult) => TaskToApm.End(asyncResult);
}