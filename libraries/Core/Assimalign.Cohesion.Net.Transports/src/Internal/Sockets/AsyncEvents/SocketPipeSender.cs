using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketPipeSender : SocketPipeAsyncArgs
{
    private List<ArraySegment<byte>> bufferList = default!;

    public SocketPipeSender(PipeScheduler pipeScheduler) : base(pipeScheduler)
    {
        
    }

    public ValueTask<SocketPipeResult> SendToAsync(Socket socket, in ReadOnlySequence<byte> buffers)
    {
        if (buffers.IsSingleSegment)
        {
          
            return SendToAsync(socket, buffers.First);
        }

        SetBufferList(buffers);

        if (socket.SendToAsync(this))
        {
            return new ValueTask<SocketPipeResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketPipeResult>(new SocketPipeResult(bytesTransferred))
            : new ValueTask<SocketPipeResult>(new SocketPipeResult(CreateException(error)));
    }
    public ValueTask<SocketPipeResult> SendAsync(Socket socket, in ReadOnlySequence<byte> buffers)
    {
        if (buffers.IsSingleSegment)
        {
            return SendAsync(socket, buffers.First);
        }

        SetBufferList(buffers);

        if (socket.SendAsync(this))
        {
            return new ValueTask<SocketPipeResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketPipeResult>(new SocketPipeResult(bytesTransferred))
            : new ValueTask<SocketPipeResult>(new SocketPipeResult(CreateException(error)));
    }

    public void Reset()
    {
        // We clear the buffer and buffer list before we put it back into the pool
        // it's a small performance hit but it removes the confusion when looking at dumps to see this still
        // holds onto the buffer when it's back in the pool
        if (BufferList != null)
        {
            BufferList = null;

            bufferList?.Clear();
        }
        else
        {
            SetBuffer(null, 0, 0);
        }
    }


    private ValueTask<SocketPipeResult> SendAsync(Socket socket, ReadOnlyMemory<byte> memory)
    {
        SetBuffer(MemoryMarshal.AsMemory(memory));

        if (socket.SendAsync(this))
        {
            return new ValueTask<SocketPipeResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketPipeResult>(new SocketPipeResult(bytesTransferred))
            : new ValueTask<SocketPipeResult>(new SocketPipeResult(CreateException(error)));
    }
    private ValueTask<SocketPipeResult> SendToAsync(Socket socket, ReadOnlyMemory<byte> memory)
    {
        SetBuffer(MemoryMarshal.AsMemory(memory));

        if (socket.SendToAsync(this))
        {
            return new ValueTask<SocketPipeResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketPipeResult>(new SocketPipeResult(bytesTransferred))
            : new ValueTask<SocketPipeResult>(new SocketPipeResult(CreateException(error)));
    }
    private void SetBufferList(in ReadOnlySequence<byte> buffer)
    {
        Debug.Assert(!buffer.IsEmpty);
        Debug.Assert(!buffer.IsSingleSegment);

        bufferList ??= new List<ArraySegment<byte>>();

        foreach (var b in buffer)
        {
            bufferList.Add(GetArray(b));
        }

        // The act of setting this list, sets the buffers in the internal buffer list
        BufferList = bufferList;
    }
    private ArraySegment<byte> GetArray(ReadOnlyMemory<byte> memory)
    {
        if (!MemoryMarshal.TryGetArray(memory, out var result))
        {
            throw new InvalidOperationException("Buffer backed by array was expected");
        }
        return result;
    }
}